using FhirAugury.Common.WorkGroups;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;

/// <summary>
/// Resolves a free-form work-group name — a raw Jira <c>work_group</c> value
/// such as <c>"Orders and Observations"</c> or <c>"Orders and Observations
/// (OO)"</c> — to its canonical short HL7 work-group <c>code</c> (e.g.
/// <c>oo</c>) via the read-only <c>hl7_workgroups</c> table in <c>github.db</c>.
/// This is the reverse of <see cref="WorkGroupNameResolver"/> and shares the
/// multi-basis matching style of
/// <c>FhirAugury.Source.GitHub.Ingestion.WorkGroupResolver.Resolve</c>:
/// it matches on the canonical <c>Code</c>, the display <c>Name</c>, and the
/// cleaned <c>NameClean</c> basis, additionally stripping a trailing
/// parenthetical (which frequently carries the short code) so suffix-laden
/// values still resolve.
/// </summary>
/// <remarks>
/// Returns <c>null</c> when no row matches (or on any query error), so the
/// applied-by lineage falls back to <see cref="Hl7WorkGroupNameCleaner.Clean"/>
/// only when the registry cannot place the value — keeping the applied codes on
/// the same short-code basis as the Listed/Index lineages. Lookups (including
/// misses) are memoized in a caller-supplied cache.
/// </remarks>
public static class WorkGroupCodeResolver
{
    /// <summary>
    /// Returns the canonical short code for <paramref name="name"/>, or
    /// <c>null</c> when unresolved. Results (including misses) are cached in
    /// <paramref name="cache"/>, keyed on the trimmed raw input.
    /// </summary>
    public static string? Resolve(SqliteConnection connection, string? name, IDictionary<string, string?> cache)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(cache);

        if (string.IsNullOrWhiteSpace(name)) return null;

        string raw = name.Trim();
        if (cache.TryGetValue(raw, out string? cached)) return cached;

        string? code = null;
        try
        {
            string stripped = StripTrailingParenthetical(raw, out string? parenContent);
            string clean = Hl7WorkGroupNameCleaner.Clean(raw);
            string strippedClean = Hl7WorkGroupNameCleaner.Clean(stripped);

            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT Code FROM hl7_workgroups WHERE " +
                "Code = $raw COLLATE NOCASE " +
                "OR Name = $raw COLLATE NOCASE " +
                "OR NameClean = $clean COLLATE NOCASE " +
                "OR Name = $stripped COLLATE NOCASE " +
                "OR NameClean = $strippedClean COLLATE NOCASE " +
                "OR Code = $paren COLLATE NOCASE " +
                "LIMIT 1";
            cmd.Parameters.AddWithValue("$raw", raw);
            cmd.Parameters.AddWithValue("$clean", NullIfEmpty(clean));
            cmd.Parameters.AddWithValue("$stripped", NullIfEmpty(stripped));
            cmd.Parameters.AddWithValue("$strippedClean", NullIfEmpty(strippedClean));
            cmd.Parameters.AddWithValue("$paren", NullIfEmpty(parenContent));

            object? result = cmd.ExecuteScalar();
            if (result is string c && !string.IsNullOrWhiteSpace(c)) code = c;
        }
        catch (SqliteException)
        {
            // Best-effort: a missing table / schema drift yields null and the
            // caller falls back to the cleaned name.
        }

        cache[raw] = code;
        return code;
    }

    private static string StripTrailingParenthetical(string value, out string? parenContent)
    {
        parenContent = null;
        int open = value.LastIndexOf('(');
        int close = value.LastIndexOf(')');
        if (open >= 0 && close == value.Length - 1 && close > open)
        {
            parenContent = value[(open + 1)..close].Trim();
            return value[..open].Trim();
        }

        return value;
    }

    private static object NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? DBNull.Value : value;
}
