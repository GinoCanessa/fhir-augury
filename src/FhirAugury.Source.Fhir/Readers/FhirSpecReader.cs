using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Fhir.Readers;

/// <summary>
/// Service-local read-only reader over the FHIR spec database. Split into partial
/// classes per artifact area (Structures, CodeSystems, ValueSets, Operations,
/// SearchParameters, Resolve, Search). This root partial holds shared helpers,
/// the release listing, and database-wide counts.
/// </summary>
public sealed partial class FhirSpecReader
{
    private readonly FhirSpecDatabase _db;
    private readonly FhirReleaseResolver _resolver;

    public FhirSpecReader(FhirSpecDatabase db, FhirReleaseResolver resolver)
    {
        _db = db;
        _resolver = resolver;
    }

    /// <summary>Lists all FHIR releases present in the spec database.</summary>
    public List<ReleaseInfo> ListReleases() => _resolver.ListReleaseInfos();

    /// <summary>Returns high-level artifact counts for the spec database.</summary>
    public FhirSpecCounts GetCounts()
    {
        if (!_db.Exists)
        {
            return new FhirSpecCounts(0, 0, 0, 0, 0, 0);
        }

        using SqliteConnection conn = _db.OpenConnection();
        return new FhirSpecCounts(
            Releases: CountTable(conn, "Packages"),
            Structures: CountTable(conn, "Structures"),
            CodeSystems: CountTable(conn, "CodeSystems"),
            ValueSets: CountTable(conn, "ValueSets"),
            Operations: CountTable(conn, "Operations"),
            SearchParameters: CountTable(conn, "SearchParameters"));
    }

    // Table name is always a hard-coded literal — never user input.
    private static int CountTable(SqliteConnection conn, string table)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static bool? GetNullableBool(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;
}
