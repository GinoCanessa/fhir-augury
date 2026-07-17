using FhirAugury.Source.Fhir.Api;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Fhir.Readers;

public sealed partial class FhirSpecReader
{
    private const string ValueSetSummaryColumns =
        "Id, Name, Title, UnversionedUrl, VersionedUrl, Status, StandardStatus, WorkGroup, " +
        "FhirMaturity, ConceptCount, Description";

    /// <summary>Lists the value sets in a package.</summary>
    public List<ValueSetSummary> ListValueSets(int packageKey)
    {
        List<ValueSetSummary> results = [];
        if (!_db.Exists)
        {
            return results;
        }

        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {ValueSetSummaryColumns} FROM ValueSets WHERE PackageKey = $pk ORDER BY Name";
        cmd.Parameters.AddWithValue("$pk", packageKey);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadValueSetSummary(reader));
        }
        return results;
    }

    /// <summary>Loads a value set (by id / url / name) with compose, referenced systems, and binding rollups.</summary>
    public ValueSetDetail? GetValueSet(int packageKey, string idOrUrl)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {ValueSetSummaryColumns}, Key, Compose, StrongestBindingCore, " +
            "BindingCountCore, BindingCountExtended FROM ValueSets " +
            "WHERE PackageKey = $pk AND (Id = $x OR VersionedUrl = $x OR UnversionedUrl = $x OR Name = $x) " +
            "ORDER BY Key LIMIT 1";
        cmd.Parameters.AddWithValue("$pk", packageKey);
        cmd.Parameters.AddWithValue("$x", idOrUrl);

        ValueSetSummary summary;
        long key;
        List<ComposeRule> compose;
        string? strongest;
        int bindingCore;
        int bindingExtended;
        using (SqliteDataReader r = cmd.ExecuteReader())
        {
            if (!r.Read())
            {
                return null;
            }
            summary = ReadValueSetSummary(r);
            key = r.GetInt64(11);
            compose = FhirSpecJson.ParseCompose(GetNullableString(r, 12));
            strongest = GetNullableString(r, 13);
            bindingCore = r.GetInt32(14);
            bindingExtended = r.GetInt32(15);
        }

        List<string> systems = ReferencedSystems(conn, key);
        return new ValueSetDetail(summary, compose, systems, strongest, bindingCore, bindingExtended);
    }

    /// <summary>Returns a value set's expanded concept list.</summary>
    public IReadOnlyList<ValueSetConceptInfo>? GetExpansion(int packageKey, string idOrUrl)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        long? key = FindValueSetKey(conn, packageKey, idOrUrl);
        if (key is null)
        {
            return null;
        }

        List<ValueSetConceptInfo> concepts = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT System, Code, Display, Inactive, Abstract FROM ValueSetConcepts
            WHERE ValueSetKey = $k ORDER BY Key
            """;
        cmd.Parameters.AddWithValue("$k", key.Value);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            concepts.Add(new ValueSetConceptInfo(
                System: r.GetString(0),
                Code: r.GetString(1),
                Display: GetNullableString(r, 2),
                Inactive: r.GetInt64(3) != 0,
                Abstract: r.GetInt64(4) != 0));
        }
        return concepts;
    }

    /// <summary>Returns the elements that bind to a value set (reverse of element bindings).</summary>
    public IReadOnlyList<ElementBindingRef>? GetBindings(int packageKey, string idOrUrl)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        long? key = FindValueSetKey(conn, packageKey, idOrUrl);
        if (key is null)
        {
            return null;
        }

        List<ElementBindingRef> bindings = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.Name, e.Path, e.ValueSetBindingStrength
            FROM Elements e JOIN Structures s ON s.Key = e.StructureKey
            WHERE e.BindingValueSetKey = $k
            ORDER BY s.Name, e.Path
            """;
        cmd.Parameters.AddWithValue("$k", key.Value);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            bindings.Add(new ElementBindingRef(r.GetString(0), r.GetString(1), GetNullableString(r, 2)));
        }
        return bindings;
    }

    private static long? FindValueSetKey(SqliteConnection conn, int packageKey, string idOrUrl)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Key FROM ValueSets
            WHERE PackageKey = $pk AND (Id = $x OR VersionedUrl = $x OR UnversionedUrl = $x OR Name = $x)
            ORDER BY Key LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$pk", packageKey);
        cmd.Parameters.AddWithValue("$x", idOrUrl);
        object? result = cmd.ExecuteScalar();
        return result is not null and not DBNull ? Convert.ToInt64(result) : null;
    }

    private static List<string> ReferencedSystems(SqliteConnection conn, long valueSetKey)
    {
        List<string> systems = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT System FROM ValueSetSystems WHERE ValueSetKey = $k ORDER BY System
            """;
        cmd.Parameters.AddWithValue("$k", valueSetKey);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            systems.Add(r.GetString(0));
        }
        return systems;
    }

    private static ValueSetSummary ReadValueSetSummary(SqliteDataReader r) => new(
        Id: r.GetString(0),
        Name: r.GetString(1),
        Title: GetNullableString(r, 2),
        UnversionedUrl: r.GetString(3),
        VersionedUrl: r.GetString(4),
        Status: GetNullableString(r, 5),
        StandardStatus: GetNullableString(r, 6),
        WorkGroup: GetNullableString(r, 7),
        FhirMaturity: GetNullableInt(r, 8),
        ConceptCount: r.GetInt32(9),
        Description: GetNullableString(r, 10));
}
