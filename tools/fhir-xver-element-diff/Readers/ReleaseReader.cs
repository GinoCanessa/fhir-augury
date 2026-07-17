using FhirAugury.Tools.FhirXverElementDiff.Model;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Tools.FhirXverElementDiff.Readers;

/// <summary>
/// Loads a whole FHIR release (structures + snapshot elements + element types) from
/// one of the two read-only spec DBs — <c>fhir-spec.db</c> (R4/R4B/R5) or
/// <c>fhir-r6.db</c> (R6) — into an in-memory <see cref="ReleaseModel"/>. Both DBs
/// share an identical schema, so a single reader handles all four releases; the only
/// per-release difference is the DB path and the surrogate <c>Packages.Key</c>, which
/// is resolved dynamically (never hard-coded).
/// </summary>
internal sealed class ReleaseReader
{
    // In-scope structure kinds per decision #6 (base types + specializations; the core
    // DBs contain no constraint profiles or logical models, so this yields exactly the
    // primitive/complex/resource set, including abstract bases like Element/Resource).
    private const string ScopeFilter =
        "Kind IN ('primitive-type','complex-type','resource') AND (Derivation IS NULL OR Derivation <> 'constraint')";

    private readonly ILogger _logger;

    public ReleaseReader(ILogger logger)
    {
        _logger = logger;
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        SqliteConnection conn = new(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Resolves the <c>Packages</c> row for a release, matching <c>ShortName</c> /
    /// <c>FhirVersionShort</c> / <c>PackageId</c>, and returns its build tuple.
    /// </summary>
    public ResolvedRelease ResolveRelease(ReleaseId id, string dbPath)
    {
        using SqliteConnection conn = OpenReadOnly(dbPath);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Key, PackageId, PackageVersion, ProcessDate FROM Packages
            WHERE ShortName = $s OR FhirVersionShort = $fv OR PackageId = $pid
            ORDER BY Key
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$s", Release.ShortName(id));
        cmd.Parameters.AddWithValue("$fv", FhirVersionShort(id));
        cmd.Parameters.AddWithValue("$pid", PackageId(id));

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                $"Release {Release.DisplayLabel(id)} not found in {dbPath} (no matching Packages row).");
        }

        int packageKey = reader.GetInt32(0);
        string packageId = reader.IsDBNull(1) ? PackageId(id) : reader.GetString(1);
        string version = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        string? processDate = reader.IsDBNull(3) ? null : reader.GetString(3);
        return new ResolvedRelease(id, dbPath, packageKey, packageId, version, processDate);
    }

    /// <summary>Loads all in-scope structures, elements, and element types for a release.</summary>
    public ReleaseModel LoadRelease(ResolvedRelease release)
    {
        using SqliteConnection conn = OpenReadOnly(release.DbPath);

        Dictionary<int, StructureBuilder> structuresByKey = LoadStructures(conn, release.PackageKey);
        Dictionary<int, ElementBuilder> elementsByKey = LoadElements(conn, release.PackageKey, structuresByKey);
        LoadElementTypes(conn, release.PackageKey, elementsByKey);

        List<StructureModel> structures = new(structuresByKey.Count);
        foreach (StructureBuilder sb in structuresByKey.Values)
        {
            List<ElementModel> elements = new(sb.Elements.Count);
            foreach (ElementBuilder eb in sb.Elements)
            {
                elements.Add(eb.Build());
            }
            structures.Add(sb.Build(elements));
        }

        return new ReleaseModel(release, structures);
    }

    private Dictionary<int, StructureBuilder> LoadStructures(SqliteConnection conn, int packageKey)
    {
        Dictionary<int, StructureBuilder> byKey = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT Key, Name, Kind, Derivation, IsAbstract, BaseDefinition, FhirType, WorkGroup, SnapshotCount
            FROM Structures
            WHERE PackageKey = $pk AND {ScopeFilter}
            """;
        cmd.Parameters.AddWithValue("$pk", packageKey);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int key = reader.GetInt32(0);
            byKey[key] = new StructureBuilder(
                Name: reader.GetString(1),
                Kind: reader.IsDBNull(2) ? "complex-type" : reader.GetString(2),
                Derivation: reader.IsDBNull(3) ? null : reader.GetString(3),
                IsAbstract: !reader.IsDBNull(4) && reader.GetInt32(4) != 0,
                BaseDefinition: reader.IsDBNull(5) ? null : reader.GetString(5),
                FhirType: reader.IsDBNull(6) ? null : reader.GetString(6),
                WorkGroup: reader.IsDBNull(7) ? null : reader.GetString(7),
                SnapshotCount: reader.IsDBNull(8) ? 0 : reader.GetInt32(8));
        }
        return byKey;
    }

    private Dictionary<int, ElementBuilder> LoadElements(
        SqliteConnection conn, int packageKey, Dictionary<int, StructureBuilder> structuresByKey)
    {
        Dictionary<int, ElementBuilder> byKey = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Key, StructureKey, Path, Name, SliceName, MinCardinality, MaxCardinalityString,
                   IsInherited, BasePath, FullCollatedTypeLiteral
            FROM Elements
            WHERE PackageKey = $pk
            ORDER BY StructureKey, ResourceFieldOrder, ComponentFieldOrder, Key
            """;
        cmd.Parameters.AddWithValue("$pk", packageKey);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int structureKey = reader.GetInt32(1);
            if (!structuresByKey.TryGetValue(structureKey, out StructureBuilder? owner))
            {
                continue; // element of an out-of-scope structure
            }

            int elementKey = reader.GetInt32(0);
            string path = reader.GetString(2);
            string rootRelative = ElementModel.ComputeRootRelativePath(path);
            ElementBuilder eb = new(
                Path: path,
                RootRelativePath: rootRelative,
                NormalizedKey: ElementModel.ComputeNormalizedKey(rootRelative),
                Name: reader.GetString(3),
                SliceName: reader.IsDBNull(4) ? null : reader.GetString(4),
                Min: reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                MaxString: reader.IsDBNull(6) ? "1" : reader.GetString(6),
                IsInherited: !reader.IsDBNull(7) && reader.GetInt32(7) != 0,
                BasePath: reader.IsDBNull(8) ? null : reader.GetString(8),
                TypeLiteral: reader.IsDBNull(9) ? string.Empty : reader.GetString(9));

            if (!owner.SeenPathSlice.Add((path, eb.SliceName)))
            {
                _logger.LogWarning(
                    "Duplicate (Path, SliceName) in {Structure}: {Path} / {Slice}; keeping first.",
                    owner.Name, path, eb.SliceName ?? "<none>");
                continue;
            }
            owner.Elements.Add(eb);
            byKey[elementKey] = eb;
        }
        return byKey;
    }

    private static void LoadElementTypes(
        SqliteConnection conn, int packageKey, Dictionary<int, ElementBuilder> elementsByKey)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ElementKey, TypeName, TypeProfile, TargetProfile
            FROM ElementTypes
            WHERE PackageKey = $pk
            ORDER BY ElementKey, CollatedTypeKey, Key
            """;
        cmd.Parameters.AddWithValue("$pk", packageKey);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int elementKey = reader.GetInt32(0);
            if (!elementsByKey.TryGetValue(elementKey, out ElementBuilder? owner))
            {
                continue;
            }

            string typeName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            string? typeProfile = reader.IsDBNull(2) ? null : reader.GetString(2);
            string? targetProfile = reader.IsDBNull(3) ? null : reader.GetString(3);

            ElementType type = new(typeName, string.IsNullOrEmpty(typeProfile) ? null : typeProfile);
            if (owner.TypeSet.Add(type))
            {
                owner.Types.Add(type);
            }
            if (!string.IsNullOrEmpty(targetProfile) && owner.TargetSet.Add(targetProfile))
            {
                owner.TargetProfiles.Add(targetProfile);
            }
        }
    }

    private static string FhirVersionShort(ReleaseId id) => id switch
    {
        ReleaseId.R4 => "4.0",
        ReleaseId.R4B => "4.3",
        ReleaseId.R5 => "5.0",
        ReleaseId.R6 => "6.0",
        _ => string.Empty,
    };

    private static string PackageId(ReleaseId id) => id switch
    {
        ReleaseId.R4 => "hl7.fhir.r4.core",
        ReleaseId.R4B => "hl7.fhir.r4b.core",
        ReleaseId.R5 => "hl7.fhir.r5.core",
        ReleaseId.R6 => "hl7.fhir.r6.core",
        _ => string.Empty,
    };

    private sealed class StructureBuilder(
        string Name, string Kind, string? Derivation, bool IsAbstract,
        string? BaseDefinition, string? FhirType, string? WorkGroup, int SnapshotCount)
    {
        public string Name { get; } = Name;
        public List<ElementBuilder> Elements { get; } = [];
        public HashSet<(string, string?)> SeenPathSlice { get; } = [];

        public StructureModel Build(IReadOnlyList<ElementModel> elements) => new(
            Name, Kind, Derivation, IsAbstract, BaseDefinition, FhirType, WorkGroup, SnapshotCount, elements);
    }

    private sealed class ElementBuilder(
        string Path, string RootRelativePath, string NormalizedKey, string Name, string? SliceName,
        int Min, string MaxString, bool IsInherited, string? BasePath, string TypeLiteral)
    {
        public string? SliceName { get; } = SliceName;
        public List<ElementType> Types { get; } = [];
        public HashSet<ElementType> TypeSet { get; } = [];
        public List<string> TargetProfiles { get; } = [];
        public HashSet<string> TargetSet { get; } = new(StringComparer.Ordinal);

        public ElementModel Build() => new(
            Path, RootRelativePath, NormalizedKey, Name, SliceName, Min, MaxString,
            IsInherited, BasePath, TypeLiteral, Types, TargetProfiles);
    }
}
