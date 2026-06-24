using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;

/// <summary>
/// Resolves an artifact's owning work group from the read-only FHIR spec
/// reference DBs (<c>Structures.WorkGroup</c>). The current-build
/// <c>fhir-r6.db</c> is preferred; the published multi-release <c>fhir-spec.db</c>
/// is the fallback. Returns a canonical work group code, or <c>null</c> when no
/// row matches (or a DB is absent / schema-drifted). Best-effort.
/// </summary>
public static class SpecDbWorkGroupReader
{
    /// <summary>
    /// Returns the canonical owning work group code for the artifact named
    /// <paramref name="artifactName"/>, trying <paramref name="fhirR6DbPath"/>
    /// first and then <paramref name="fhirSpecDbPath"/>, or <c>null</c>.
    /// </summary>
    public static string? Resolve(
        string? fhirR6DbPath,
        string? fhirSpecDbPath,
        string artifactName,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(artifactName)) return null;

        string? code = QueryStructures(fhirR6DbPath, artifactName, logger);
        if (!string.IsNullOrWhiteSpace(code)) return code;

        return QueryStructures(fhirSpecDbPath, artifactName, logger);
    }

    private static string? QueryStructures(string? dbPath, string artifactName, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return null;

        try
        {
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString;

            using SqliteConnection connection = new(connectionString);
            connection.Open();

            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT WorkGroup FROM Structures " +
                "WHERE Name = $n COLLATE NOCASE AND WorkGroup IS NOT NULL AND WorkGroup <> '' LIMIT 1";
            cmd.Parameters.AddWithValue("$n", artifactName);
            object? result = cmd.ExecuteScalar();
            return result is string code && !string.IsNullOrWhiteSpace(code) ? code : null;
        }
        catch (SqliteException ex)
        {
            logger?.LogDebug(ex, "Spec-DB owning-WG lookup failed against {Db}", dbPath);
            return null;
        }
    }
}
