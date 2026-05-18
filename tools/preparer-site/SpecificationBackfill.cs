using System.Net.Http.Json;
using System.Net.Sockets;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.PreparerSite;

/// <summary>
/// One-off backfill for the <c>Specification</c> column on
/// <c>jira_processing_source_tickets</c>. Walks rows whose Specification
/// is empty / NULL, fetches the canonical value from the Jira source
/// service (HTTP first; <c>--jira-source-db</c> SQLite fallback), and
/// writes the resolved value back in place. Rows for which the Jira
/// source returns an empty value are left as <c>""</c> so the next run
/// can self-heal once upstream is populated.
/// </summary>
internal static class SpecificationBackfill
{
    // Duplicated from WorkGroupResolver.DefaultJiraSourceAddress; intentional
    // until preparer-site takes a project reference that exposes
    // PreparerJiraProcessingDefaults (see WorkGroupResolver TODO).
    private const string DefaultJiraSourceAddress = "http://localhost:5160";

    private const int BulkPageSize = 500;
    private const int SqliteInBatchSize = 500;

    public static async Task<int> RunAsync(
        string dbPath,
        CliOptions options,
        TextWriter stderr,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stderr);

        // Construct the store so EnsureSchema runs and guarantees the
        // Specification column exists on legacy DBs before we query/update it.
        _ = new JiraProcessingSourceTicketStore(dbPath);

        List<string> emptyKeys = await SelectEmptyKeysAsync(dbPath, ct).ConfigureAwait(false);
        if (emptyKeys.Count == 0)
        {
            await stderr.WriteLineAsync(
                "No jira_processing_source_tickets rows with empty Specification; nothing to do.")
                .ConfigureAwait(false);
            return 0;
        }

        string baseUrl = options.JiraSourceUrl ?? DefaultJiraSourceAddress;
        (Dictionary<string, string>? resolved, string? httpFailureReason) = await TryFetchFromHttpAsync(baseUrl, ct).ConfigureAwait(false);

        if (resolved is null)
        {
            if (httpFailureReason is not null)
            {
                if (string.IsNullOrEmpty(options.JiraSourceDbPath))
                {
                    await stderr.WriteLineAsync(
                        $"Jira source unreachable ({httpFailureReason}); pass --jira-source <url> or --jira-source-db <path>.")
                        .ConfigureAwait(false);
                    return 1;
                }

                await stderr.WriteLineAsync(
                    $"Jira source HTTP failed ({httpFailureReason}); falling back to --jira-source-db.")
                    .ConfigureAwait(false);
                resolved = await TryFetchFromSqliteAsync(options.JiraSourceDbPath, emptyKeys, ct).ConfigureAwait(false);
            }
        }

        if (resolved is null)
        {
            await stderr.WriteLineAsync(
                "Jira source unreachable; pass --jira-source <url> or --jira-source-db <path>.")
                .ConfigureAwait(false);
            return 1;
        }

        (int updated, int stillEmpty, int notFound) = await ApplyAsync(dbPath, emptyKeys, resolved, ct).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            $"Backfilled Specification on {updated} rows ({stillEmpty} left empty, {notFound} not found in Jira source).")
            .ConfigureAwait(false);
        return 0;
    }

    private static async Task<List<string>> SelectEmptyKeysAsync(string dbPath, CancellationToken ct)
    {
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

    private static async Task<(Dictionary<string, string>? Resolved, string? FailureReason)> TryFetchFromHttpAsync(
        string baseUrl,
        CancellationToken ct)
    {
        Dictionary<string, string> resolved = new(StringComparer.Ordinal);
        string requestUrl = baseUrl.TrimEnd('/') + "/api/v1/local-processing/tickets?type=fhir";
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
            int offset = 0;
            while (true)
            {
                JiraLocalProcessingListRequest request = new()
                {
                    Limit = BulkPageSize,
                    Offset = offset,
                };
                using HttpResponseMessage response = await client
                    .PostAsJsonAsync(requestUrl, request, ct)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (null, $"HTTP {(int)response.StatusCode}");
                }

                JiraLocalProcessingListResponse? page = await response.Content
                    .ReadFromJsonAsync<JiraLocalProcessingListResponse>(cancellationToken: ct)
                    .ConfigureAwait(false);
                if (page is null || page.Results.Count == 0)
                {
                    break;
                }

                foreach (JiraIssueSummaryEntry entry in page.Results)
                {
                    resolved[entry.Key] = entry.Specification ?? string.Empty;
                }

                offset += page.Results.Count;
                if (offset >= page.Total)
                {
                    break;
                }
            }

            return (resolved, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "timeout");
        }
        catch (SocketException ex)
        {
            return (null, ex.Message);
        }
    }

    private static async Task<Dictionary<string, string>?> TryFetchFromSqliteAsync(
        string jiraSourceDbPath,
        IReadOnlyList<string> keys,
        CancellationToken ct)
    {
        if (!File.Exists(jiraSourceDbPath))
        {
            return null;
        }

        Dictionary<string, string> resolved = new(StringComparer.Ordinal);
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = jiraSourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        for (int start = 0; start < keys.Count; start += SqliteInBatchSize)
        {
            int end = Math.Min(start + SqliteInBatchSize, keys.Count);
            await using SqliteCommand cmd = connection.CreateCommand();
            List<string> placeholders = [];
            for (int i = start; i < end; i++)
            {
                string param = $"@k{i}";
                placeholders.Add(param);
                cmd.Parameters.AddWithValue(param, keys[i]);
            }
            cmd.CommandText = $"SELECT Key, Specification FROM jira_issues WHERE Key IN ({string.Join(", ", placeholders)})";
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string key = reader.GetString(0);
                string spec = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                resolved[key] = spec;
            }
        }

        return resolved;
    }

    private static async Task<(int Updated, int StillEmpty, int NotFound)> ApplyAsync(
        string dbPath,
        IReadOnlyList<string> emptyKeys,
        IReadOnlyDictionary<string, string> resolved,
        CancellationToken ct)
    {
        int updated = 0;
        int stillEmpty = 0;
        int notFound = 0;

        await using SqliteConnection connection = new($"Data Source={dbPath}");
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteTransaction tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        foreach (string key in emptyKeys)
        {
            if (!resolved.TryGetValue(key, out string? spec))
            {
                notFound++;
                continue;
            }

            if (string.IsNullOrEmpty(spec))
            {
                stillEmpty++;
                continue;
            }

            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE jira_processing_source_tickets SET Specification = @s WHERE Key = @k";
            cmd.Parameters.AddWithValue("@s", spec);
            cmd.Parameters.AddWithValue("@k", key);
            int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (affected > 0)
            {
                updated++;
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (updated, stillEmpty, notFound);
    }
}
