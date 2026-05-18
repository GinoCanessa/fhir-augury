using Microsoft.Data.Sqlite;

namespace FhirAugury.Processing.Jira.Common.Database;

/// <summary>
/// Connection-scoped helpers for the <c>jira_processing_source_tickets.Specification</c>
/// backfill. The Specification column is owned by
/// <see cref="JiraProcessingSourceTicketStore"/>; these helpers exist so the
/// preparer-side hydration sweeper can query / update rows without
/// duplicating raw SQL.
/// </summary>
public static class SpecificationBackfillQueries
{
    /// <summary>
    /// Returns every <c>Key</c> from <c>jira_processing_source_tickets</c>
    /// whose <c>Specification</c> is NULL or an empty string. Output is
    /// unordered (insertion order); callers that need a stable order
    /// should sort.
    /// </summary>
    public static async Task<List<string>> ListEmptySpecificationKeysAsync(
        string dbPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        List<string> keys = [];
        await using SqliteConnection connection = new($"Data Source={dbPath}");
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Key FROM jira_processing_source_tickets " +
            "WHERE Specification = '' OR Specification IS NULL";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }
}
