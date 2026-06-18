using FhirAugury.Source.Fhir.Api;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Fhir.Readers;

public sealed partial class FhirSpecReader
{
    /// <summary>
    /// Resolves a canonical URL (versioned or unversioned) to an artifact across
    /// every artifact table in the package, returning the artifact kind + summary.
    /// </summary>
    public ResolveResult? Resolve(int packageKey, string url)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();

        // Structures carry their kind in ArtifactClass; check them first.
        ResolveResult? structure = ResolveStructure(conn, packageKey, url);
        if (structure is not null)
        {
            return structure;
        }

        // Table is a hard-coded literal in each call — never user input.
        foreach ((string table, string kind) in (ReadOnlySpan<(string, string)>)
        [
            ("Operations", "Operation"),
            ("SearchParameters", "SearchParameter"),
            ("ValueSets", "ValueSet"),
            ("CodeSystems", "CodeSystem"),
        ])
        {
            ResolveResult? hit = ResolveIn(conn, packageKey, url, table, kind);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static ResolveResult? ResolveStructure(SqliteConnection conn, int packageKey, string url)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ArtifactClass, Id, Name, Title, UnversionedUrl, VersionedUrl, Status, WorkGroup
            FROM Structures
            WHERE PackageKey = $pk AND (VersionedUrl = $u OR UnversionedUrl = $u)
            ORDER BY Key LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$pk", packageKey);
        cmd.Parameters.AddWithValue("$u", url);

        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }
        return new ResolveResult(
            Kind: r.GetString(0),
            Id: r.GetString(1),
            Name: r.GetString(2),
            Title: GetNullableString(r, 3),
            UnversionedUrl: r.GetString(4),
            VersionedUrl: r.GetString(5),
            Status: GetNullableString(r, 6),
            WorkGroup: GetNullableString(r, 7));
    }

    private static ResolveResult? ResolveIn(
        SqliteConnection conn, int packageKey, string url, string table, string kind)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT Id, Name, Title, UnversionedUrl, VersionedUrl, Status, WorkGroup FROM {table} " +
            "WHERE PackageKey = $pk AND (VersionedUrl = $u OR UnversionedUrl = $u) ORDER BY Key LIMIT 1";
        cmd.Parameters.AddWithValue("$pk", packageKey);
        cmd.Parameters.AddWithValue("$u", url);

        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }
        return new ResolveResult(
            Kind: kind,
            Id: r.GetString(0),
            Name: r.GetString(1),
            Title: GetNullableString(r, 2),
            UnversionedUrl: r.GetString(3),
            VersionedUrl: r.GetString(4),
            Status: GetNullableString(r, 5),
            WorkGroup: GetNullableString(r, 6));
    }
}
