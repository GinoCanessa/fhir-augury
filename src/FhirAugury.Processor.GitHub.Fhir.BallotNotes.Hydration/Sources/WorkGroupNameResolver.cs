using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;

/// <summary>
/// Resolves a canonical HL7 work group <c>code</c> (e.g. <c>pa</c>) to its
/// human-readable display name via the read-only <c>hl7_workgroups</c> table in
/// <c>github.db</c>. An unresolved code is returned verbatim as its own display
/// name. Lookups are memoized in a caller-supplied cache so a unit resolving
/// many codes (the consolidated datatypes surface) opens no extra queries.
/// </summary>
public static class WorkGroupNameResolver
{
    /// <summary>
    /// Returns the display name for <paramref name="code"/>, falling back to the
    /// code itself when no <c>hl7_workgroups</c> row matches (or on any query
    /// error). Results are cached in <paramref name="cache"/>.
    /// </summary>
    public static string Resolve(SqliteConnection connection, string code, IDictionary<string, string> cache)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(cache);

        if (string.IsNullOrWhiteSpace(code)) return code;
        if (cache.TryGetValue(code, out string? cached)) return cached;

        string display = code;
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Name FROM hl7_workgroups WHERE Code = $code COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("$code", code);
            object? result = cmd.ExecuteScalar();
            if (result is string name && !string.IsNullOrWhiteSpace(name))
            {
                display = name;
            }
        }
        catch (SqliteException)
        {
            // Best-effort: a missing table / schema drift keeps the raw code.
        }

        cache[code] = display;
        return display;
    }
}
