using FhirAugury.Source.Fhir.Api;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Fhir.Readers;

public sealed partial class FhirSpecReader
{
    // Summary column list shared by ListStructures (0..14) and LoadStructure (+ Key at 15).
    private const string StructureSummaryColumns =
        "Id, Name, Title, ArtifactClass, Kind, FhirType, BaseDefinition, IsAbstract, " +
        "Status, StandardStatus, WorkGroup, FhirMaturity, UnversionedUrl, VersionedUrl, Description";

    /// <summary>Lists structures in a package, optionally filtered by artifact class and metadata.</summary>
    public List<StructureSummary> ListStructures(
        int packageKey,
        IReadOnlyList<string>? artifactClasses = null,
        string? workGroup = null,
        int? maturity = null,
        string? status = null,
        string? kind = null)
    {
        List<StructureSummary> results = [];
        if (!_db.Exists)
        {
            return results;
        }

        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();

        List<string> where = ["PackageKey = $pk"];
        cmd.Parameters.AddWithValue("$pk", packageKey);

        if (artifactClasses is { Count: > 0 })
        {
            List<string> placeholders = [];
            for (int i = 0; i < artifactClasses.Count; i++)
            {
                string p = $"$ac{i}";
                placeholders.Add(p);
                cmd.Parameters.AddWithValue(p, artifactClasses[i]);
            }
            where.Add($"ArtifactClass IN ({string.Join(", ", placeholders)})");
        }

        if (!string.IsNullOrWhiteSpace(workGroup))
        {
            where.Add("WorkGroup = $wg");
            cmd.Parameters.AddWithValue("$wg", workGroup);
        }
        if (maturity is int m)
        {
            where.Add("FhirMaturity = $mat");
            cmd.Parameters.AddWithValue("$mat", m);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            where.Add("Status = $st");
            cmd.Parameters.AddWithValue("$st", status);
        }
        if (!string.IsNullOrWhiteSpace(kind))
        {
            where.Add("Kind = $kind");
            cmd.Parameters.AddWithValue("$kind", kind);
        }

        cmd.CommandText =
            $"SELECT {StructureSummaryColumns} FROM Structures " +
            $"WHERE {string.Join(" AND ", where)} ORDER BY Name";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadStructureSummary(reader));
        }
        return results;
    }

    /// <summary>Loads a structure's summary metadata by name (or id).</summary>
    public StructureSummary? GetStructure(int packageKey, string name)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        return LoadStructure(conn, packageKey, name)?.Summary;
    }

    /// <summary>Loads a structure's summary plus its nested element tree.</summary>
    public StructureDetail? GetStructureDetail(int packageKey, string name)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        (StructureSummary Summary, long Key)? structure = LoadStructure(conn, packageKey, name);
        if (structure is null)
        {
            return null;
        }

        List<ElementBuild> all = LoadElementBuilds(conn, structure.Value.Key);
        return new StructureDetail(structure.Value.Summary, BuildRoots(all));
    }

    /// <summary>Returns a structure's elements, either as a flat field-ordered list or a nested tree.</summary>
    public IReadOnlyList<ElementNode>? GetElements(int packageKey, string name, bool nested)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        (StructureSummary Summary, long Key)? structure = LoadStructure(conn, packageKey, name);
        if (structure is null)
        {
            return null;
        }

        List<ElementBuild> all = LoadElementBuilds(conn, structure.Value.Key);
        return nested
            ? BuildRoots(all)
            : all.Select(e => e.ToNode(includeChildren: false)).ToList();
    }

    /// <summary>Returns a single element (with its sub-tree) by dotted path.</summary>
    public ElementNode? GetElement(int packageKey, string name, string path)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        (StructureSummary Summary, long Key)? structure = LoadStructure(conn, packageKey, name);
        if (structure is null)
        {
            return null;
        }

        List<ElementBuild> all = LoadElementBuilds(conn, structure.Value.Key);
        BuildRoots(all); // wires Children onto each ElementBuild
        ElementBuild? match = all.FirstOrDefault(e =>
            string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        return match?.ToNode(includeChildren: true);
    }

    private static (StructureSummary Summary, long Key)? LoadStructure(
        SqliteConnection conn, int packageKey, string name)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {StructureSummaryColumns}, Key FROM Structures " +
            "WHERE PackageKey = $pk AND (Name = $n OR Id = $n) " +
            "ORDER BY CASE ArtifactClass " +
            "  WHEN 'Resource' THEN 0 WHEN 'ComplexType' THEN 1 WHEN 'PrimitiveType' THEN 2 " +
            "  WHEN 'Interface' THEN 3 ELSE 4 END, Key " +
            "LIMIT 1";
        cmd.Parameters.AddWithValue("$pk", packageKey);
        cmd.Parameters.AddWithValue("$n", name);

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return (ReadStructureSummary(reader), reader.GetInt64(15));
    }

    private static StructureSummary ReadStructureSummary(SqliteDataReader r) => new(
        Id: r.GetString(0),
        Name: r.GetString(1),
        Title: GetNullableString(r, 2),
        ArtifactClass: r.GetString(3),
        Kind: GetNullableString(r, 4),
        FhirType: GetNullableString(r, 5),
        BaseDefinition: GetNullableString(r, 6),
        IsAbstract: GetNullableBool(r, 7),
        Status: GetNullableString(r, 8),
        StandardStatus: GetNullableString(r, 9),
        WorkGroup: GetNullableString(r, 10),
        FhirMaturity: GetNullableInt(r, 11),
        UnversionedUrl: r.GetString(12),
        VersionedUrl: r.GetString(13),
        Description: GetNullableString(r, 14));

    private List<ElementBuild> LoadElementBuilds(SqliteConnection conn, long structureKey)
    {
        Dictionary<long, List<ElementTypeInfo>> typesByElement = LoadElementTypes(conn, structureKey);
        Dictionary<long, List<AdditionalBindingInfo>> additionalByElement =
            LoadAdditionalBindings(conn, structureKey);

        List<ElementBuild> builds = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.Key, e.ParentElementKey, e.Id, e.Path, e.Name, e.SliceName,
                   e.MinCardinality, e.MaxCardinalityString, e.Short, e.Definition,
                   e.FullCollatedTypeLiteral, e.ValueSetBindingStrength, e.BindingValueSet,
                   e.AdditionalBindingCount, e.IsModifier, e.IsModifierReason, e.IsInherited,
                   e.StandardStatus, e.FixedValue, e.PatternValue, e.MeaningWhenMissing,
                   vs.Name AS BindingValueSetName
            FROM Elements e
            LEFT JOIN ValueSets vs ON vs.Key = e.BindingValueSetKey
            WHERE e.StructureKey = $sk
            ORDER BY e.ResourceFieldOrder
            """;
        cmd.Parameters.AddWithValue("$sk", structureKey);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            long key = r.GetInt64(0);
            string? bindingStrength = GetNullableString(r, 11);
            string? bindingUrl = GetNullableString(r, 12);
            string? bindingName = GetNullableString(r, 21);
            additionalByElement.TryGetValue(key, out List<AdditionalBindingInfo>? additional);

            BindingInfo? binding =
                bindingStrength is not null || bindingUrl is not null || additional is { Count: > 0 }
                    ? new BindingInfo(bindingStrength, bindingUrl, bindingName, additional ?? [])
                    : null;

            builds.Add(new ElementBuild
            {
                Key = key,
                ParentKey = GetNullableLong(r, 1),
                Id = r.GetString(2),
                Path = r.GetString(3),
                Name = r.GetString(4),
                SliceName = GetNullableString(r, 5),
                Min = r.GetInt32(6),
                Max = r.GetString(7),
                Short = GetNullableString(r, 8),
                Definition = GetNullableString(r, 9),
                TypeLiteral = r.GetString(10),
                IsModifier = r.GetInt64(14) != 0,
                IsModifierReason = GetNullableString(r, 15),
                IsInherited = r.GetInt64(16) != 0,
                StandardStatus = GetNullableString(r, 17),
                FixedValue = GetNullableString(r, 18),
                PatternValue = GetNullableString(r, 19),
                MeaningWhenMissing = GetNullableString(r, 20),
                Types = typesByElement.TryGetValue(key, out List<ElementTypeInfo>? t) ? t : [],
                Binding = binding,
            });
        }
        return builds;
    }

    private static Dictionary<long, List<ElementTypeInfo>> LoadElementTypes(
        SqliteConnection conn, long structureKey)
    {
        // ElementTypes is one row per (TypeName, TypeProfile, TargetProfile); group
        // by element then by type name, collecting profiles / target profiles.
        Dictionary<long, List<(string Name, List<string> Profiles, List<string> Targets)>> grouped = [];

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ElementKey, TypeName, TypeProfile, TargetProfile
            FROM ElementTypes WHERE StructureKey = $sk
            ORDER BY ElementKey, Key
            """;
        cmd.Parameters.AddWithValue("$sk", structureKey);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            long elementKey = r.GetInt64(0);
            string? typeName = GetNullableString(r, 1);
            if (typeName is null)
            {
                continue;
            }
            string? profile = GetNullableString(r, 2);
            string? target = GetNullableString(r, 3);

            if (!grouped.TryGetValue(elementKey, out var list))
            {
                list = [];
                grouped[elementKey] = list;
            }

            var entry = list.FirstOrDefault(e => e.Name == typeName);
            if (entry.Name is null)
            {
                entry = (typeName, [], []);
                list.Add(entry);
            }
            if (profile is not null && !entry.Profiles.Contains(profile))
            {
                entry.Profiles.Add(profile);
            }
            if (target is not null && !entry.Targets.Contains(target))
            {
                entry.Targets.Add(target);
            }
        }

        return grouped.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value
                .Select(e => new ElementTypeInfo(e.Name, e.Profiles, e.Targets))
                .ToList());
    }

    private static Dictionary<long, List<AdditionalBindingInfo>> LoadAdditionalBindings(
        SqliteConnection conn, long structureKey)
    {
        Dictionary<long, List<AdditionalBindingInfo>> result = [];

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ElementKey, Purpose, BindingValueSet, ShortDocumentation, Documentation
            FROM ElementAdditionalBindings WHERE StructureKey = $sk
            ORDER BY ElementKey, Key
            """;
        cmd.Parameters.AddWithValue("$sk", structureKey);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            long elementKey = r.GetInt64(0);
            AdditionalBindingInfo info = new(
                Purpose: GetNullableString(r, 1),
                ValueSetUrl: GetNullableString(r, 2),
                Documentation: GetNullableString(r, 3) ?? GetNullableString(r, 4));

            if (!result.TryGetValue(elementKey, out List<AdditionalBindingInfo>? list))
            {
                list = [];
                result[elementKey] = list;
            }
            list.Add(info);
        }
        return result;
    }

    // Wires Children onto each ElementBuild and returns the root nodes.
    private static List<ElementNode> BuildRoots(List<ElementBuild> all)
    {
        Dictionary<long, ElementBuild> byKey = all.ToDictionary(e => e.Key);
        List<ElementBuild> roots = [];
        foreach (ElementBuild e in all)
        {
            if (e.ParentKey is long pk && byKey.TryGetValue(pk, out ElementBuild? parent))
            {
                parent.Children.Add(e);
            }
            else
            {
                roots.Add(e);
            }
        }
        return roots.Select(e => e.ToNode(includeChildren: true)).ToList();
    }

    private static long? GetNullableLong(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private sealed class ElementBuild
    {
        public long Key { get; init; }
        public long? ParentKey { get; init; }
        public string Id { get; init; } = "";
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public string? SliceName { get; init; }
        public int Min { get; init; }
        public string Max { get; init; } = "";
        public string? Short { get; init; }
        public string? Definition { get; init; }
        public string TypeLiteral { get; init; } = "";
        public bool IsModifier { get; init; }
        public string? IsModifierReason { get; init; }
        public bool IsInherited { get; init; }
        public string? StandardStatus { get; init; }
        public string? FixedValue { get; init; }
        public string? PatternValue { get; init; }
        public string? MeaningWhenMissing { get; init; }
        public List<ElementTypeInfo> Types { get; init; } = [];
        public BindingInfo? Binding { get; init; }
        public List<ElementBuild> Children { get; } = [];

        public ElementNode ToNode(bool includeChildren) => new(
            Id, Path, Name, SliceName, Min, Max, Short, Definition, TypeLiteral,
            Types, Binding, IsModifier, IsModifierReason, IsInherited, StandardStatus,
            FixedValue, PatternValue, MeaningWhenMissing,
            includeChildren ? Children.Select(c => c.ToNode(includeChildren: true)).ToList() : []);
    }
}
