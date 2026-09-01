using FhirAugury.Source.Fhir.Api;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Fhir.Readers;

public sealed partial class FhirSpecReader
{
    private const string CodeSystemSummaryColumns =
        "Id, Name, Title, UnversionedUrl, VersionedUrl, Status, StandardStatus, WorkGroup, " +
        "FhirMaturity, Content, HierarchyMeaning, Count, Description";

    /// <summary>Lists the code systems in a package.</summary>
    public List<CodeSystemSummary> ListCodeSystems(int packageKey)
    {
        List<CodeSystemSummary> results = [];
        if (!_db.Exists)
        {
            return results;
        }

        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {CodeSystemSummaryColumns} FROM CodeSystems WHERE PackageKey = $pk ORDER BY Name";
        cmd.Parameters.AddWithValue("$pk", packageKey);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadCodeSystemSummary(reader));
        }
        return results;
    }

    /// <summary>Loads a code system (by id / url / name) with concept count, hierarchy flag, and property defs.</summary>
    public CodeSystemDetail? GetCodeSystem(int packageKey, string idOrUrl)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        (CodeSystemSummary Summary, long Key)? cs = LoadCodeSystem(conn, packageKey, idOrUrl);
        if (cs is null)
        {
            return null;
        }

        int conceptCount = ScalarInt(conn,
            "SELECT COUNT(*) FROM CodeSystemConcepts WHERE CodeSystemKey = $k", cs.Value.Key);
        bool hasHierarchy = ScalarInt(conn,
            "SELECT COUNT(*) FROM CodeSystemConcepts WHERE CodeSystemKey = $k AND ParentConceptKey IS NOT NULL",
            cs.Value.Key) > 0;

        List<CodeSystemPropertyDef> propertyDefs = [];
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Code, Type, Uri, Description FROM CodeSystemPropertyDefinitions
                WHERE CodeSystemKey = $k ORDER BY Code
                """;
            cmd.Parameters.AddWithValue("$k", cs.Value.Key);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                propertyDefs.Add(new CodeSystemPropertyDef(
                    r.GetString(0), r.GetString(1), GetNullableString(r, 2), GetNullableString(r, 3)));
            }
        }

        return new CodeSystemDetail(cs.Value.Summary, conceptCount, hasHierarchy, propertyDefs);
    }

    /// <summary>Returns a code system's concepts as a flat list or a hierarchy.</summary>
    public IReadOnlyList<ConceptNode>? GetConcepts(int packageKey, string idOrUrl, bool hierarchical)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        (CodeSystemSummary Summary, long Key)? cs = LoadCodeSystem(conn, packageKey, idOrUrl);
        if (cs is null)
        {
            return null;
        }

        List<ConceptBuild> all = LoadConceptBuilds(conn, cs.Value.Key);
        return hierarchical
            ? BuildConceptRoots(all)
            : all.Select(c => c.ToNode(includeChildren: false)).ToList();
    }

    /// <summary>Returns a single concept (with its sub-tree) by code.</summary>
    public ConceptNode? GetConcept(int packageKey, string idOrUrl, string code)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        (CodeSystemSummary Summary, long Key)? cs = LoadCodeSystem(conn, packageKey, idOrUrl);
        if (cs is null)
        {
            return null;
        }

        List<ConceptBuild> all = LoadConceptBuilds(conn, cs.Value.Key);
        BuildConceptRoots(all); // wires children
        ConceptBuild? match = all.FirstOrDefault(c =>
            string.Equals(c.Code, code, StringComparison.Ordinal));
        return match?.ToNode(includeChildren: true);
    }

    private static (CodeSystemSummary Summary, long Key)? LoadCodeSystem(
        SqliteConnection conn, int packageKey, string idOrUrl)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {CodeSystemSummaryColumns}, Key FROM CodeSystems " +
            "WHERE PackageKey = $pk AND (Id = $x OR VersionedUrl = $x OR UnversionedUrl = $x OR Name = $x) " +
            "ORDER BY Key LIMIT 1";
        cmd.Parameters.AddWithValue("$pk", packageKey);
        cmd.Parameters.AddWithValue("$x", idOrUrl);

        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? (ReadCodeSystemSummary(r), r.GetInt64(13)) : null;
    }

    private static CodeSystemSummary ReadCodeSystemSummary(SqliteDataReader r) => new(
        Id: r.GetString(0),
        Name: r.GetString(1),
        Title: GetNullableString(r, 2),
        UnversionedUrl: r.GetString(3),
        VersionedUrl: r.GetString(4),
        Status: GetNullableString(r, 5),
        StandardStatus: GetNullableString(r, 6),
        WorkGroup: GetNullableString(r, 7),
        FhirMaturity: GetNullableInt(r, 8),
        Content: GetNullableString(r, 9),
        HierarchyMeaning: GetNullableString(r, 10),
        Count: GetNullableInt(r, 11),
        Description: GetNullableString(r, 12));

    private static List<ConceptBuild> LoadConceptBuilds(SqliteConnection conn, long codeSystemKey)
    {
        List<ConceptBuild> builds = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Key, ParentConceptKey, Code, Display, Definition, Designations, Properties
            FROM CodeSystemConcepts WHERE CodeSystemKey = $k ORDER BY FlatOrder
            """;
        cmd.Parameters.AddWithValue("$k", codeSystemKey);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            builds.Add(new ConceptBuild
            {
                Key = r.GetInt64(0),
                ParentKey = GetNullableLong(r, 1),
                Code = r.GetString(2),
                Display = GetNullableString(r, 3),
                Definition = GetNullableString(r, 4),
                Designations = FhirSpecJson.ParseDesignations(GetNullableString(r, 5)),
                Properties = FhirSpecJson.ParseConceptProperties(GetNullableString(r, 6)),
            });
        }
        return builds;
    }

    private static List<ConceptNode> BuildConceptRoots(List<ConceptBuild> all)
    {
        Dictionary<long, ConceptBuild> byKey = all.ToDictionary(c => c.Key);
        List<ConceptBuild> roots = [];
        foreach (ConceptBuild c in all)
        {
            if (c.ParentKey is long pk && byKey.TryGetValue(pk, out ConceptBuild? parent))
            {
                parent.Children.Add(c);
            }
            else
            {
                roots.Add(c);
            }
        }
        return roots.Select(c => c.ToNode(includeChildren: true)).ToList();
    }

    private static int ScalarInt(SqliteConnection conn, string sql, long key)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$k", key);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private sealed class ConceptBuild
    {
        public long Key { get; init; }
        public long? ParentKey { get; init; }
        public string Code { get; init; } = "";
        public string? Display { get; init; }
        public string? Definition { get; init; }
        public List<ConceptDesignation> Designations { get; init; } = [];
        public List<ConceptProperty> Properties { get; init; } = [];
        public List<ConceptBuild> Children { get; } = [];

        public ConceptNode ToNode(bool includeChildren) => new(
            Code, Display, Definition, Designations, Properties,
            includeChildren ? Children.Select(c => c.ToNode(includeChildren: true)).ToList() : []);
    }
}
