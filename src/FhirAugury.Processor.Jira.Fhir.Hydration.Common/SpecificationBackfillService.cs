using System.Net.Http.Json;
using System.Net.Sockets;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common;

/// <summary>
/// Service-side <c>jira_processing_source_tickets.Specification</c>
/// backfill. Resolves rows whose Specification is empty / NULL by
/// fetching the canonical value from the Jira source service: HTTP
/// first, then optionally a SQLite-fallback file when configured. Rows
/// for which the Jira source returns an empty value are left empty so
/// the next sweep can self-heal once upstream is populated.
/// </summary>
/// <remarks>
/// <para>
/// Lifted from the preparer-side hydration project so the planner can
/// share the same backfill behavior. The hard-fail surfacing is a typed
/// result (<see cref="SpecificationBackfillResult.Failure"/>) instead
/// of a CLI exit code: callers (the startup hosted service vs. the
/// admin endpoint) decide how to react.
/// </para>
/// <para>
/// Idempotent: the eligibility query only returns rows whose
/// <c>Specification</c> is NULL or empty, so calling this against an
/// already-populated DB is a fast no-op.
/// </para>
/// </remarks>
public class SpecificationBackfillService(
    HttpClient httpClient,
    IOptions<HydrationOptions> options,
    ILogger<SpecificationBackfillService> logger)
{
    private const int BulkPageSize = 500;
    private const int SqliteInBatchSize = 500;

    public virtual async Task<SpecificationBackfillResult> RunAsync(string processorDbPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(processorDbPath);

        List<string> emptyKeys = await SpecificationBackfillQueries.ListEmptySpecificationKeysAsync(processorDbPath, ct).ConfigureAwait(false);
        if (emptyKeys.Count == 0)
        {
            logger.LogInformation("Specification backfill skipped: no jira_processing_source_tickets rows with empty Specification.");
            return new SpecificationBackfillResult(0, 0, 0, null);
        }

        string? jiraSourceDbPath = options.Value.JiraSourceDbPath;
        (Dictionary<string, string>? resolved, string? httpFailureReason) =
            await TryFetchFromHttpAsync(ct).ConfigureAwait(false);

        if (resolved is null && httpFailureReason is not null)
        {
            if (string.IsNullOrEmpty(jiraSourceDbPath))
            {
                string detail =
                    $"Jira source HTTP unreachable ({httpFailureReason}) and no Processing:Hydration:JiraSourceDbPath fallback configured.";
                logger.LogError("Specification backfill failed: {Detail}", detail);
                return new SpecificationBackfillResult(0, 0, 0, new SpecificationBackfillFailure(detail, httpFailureReason, null));
            }

            logger.LogWarning(
                "Jira source HTTP failed ({Reason}); falling back to JiraSourceDbPath '{Path}'.",
                httpFailureReason,
                jiraSourceDbPath);
            (Dictionary<string, string>? sqliteResolved, string? sqliteFailureReason) =
                await TryFetchFromSqliteAsync(jiraSourceDbPath, emptyKeys, ct).ConfigureAwait(false);
            if (sqliteResolved is null)
            {
                string detail =
                    $"Jira source HTTP unreachable ({httpFailureReason}) and SQLite fallback at '{jiraSourceDbPath}' unreachable ({sqliteFailureReason ?? "not found"}).";
                logger.LogError("Specification backfill failed: {Detail}", detail);
                return new SpecificationBackfillResult(0, 0, 0, new SpecificationBackfillFailure(detail, httpFailureReason, sqliteFailureReason));
            }

            resolved = sqliteResolved;
        }

        if (resolved is null)
        {
            // Defensive; HTTP returned no resolved map but also no failure reason.
            string detail = "Jira source returned no Specification data and no failure reason; cannot proceed.";
            logger.LogError("Specification backfill failed: {Detail}", detail);
            return new SpecificationBackfillResult(0, 0, 0, new SpecificationBackfillFailure(detail, null, null));
        }

        (int updated, int stillEmpty, int notFound) = await ApplyAsync(processorDbPath, emptyKeys, resolved, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Specification backfill complete: {Updated} updated, {StillEmpty} left empty, {NotFound} not found in Jira source.",
            updated,
            stillEmpty,
            notFound);
        return new SpecificationBackfillResult(updated, stillEmpty, notFound, null);
    }

    private async Task<(Dictionary<string, string>? Resolved, string? FailureReason)> TryFetchFromHttpAsync(CancellationToken ct)
    {
        Dictionary<string, string> resolved = new(StringComparer.Ordinal);
        const string requestPath = "api/v1/local-processing/tickets?type=fhir";
        try
        {
            int offset = 0;
            while (true)
            {
                JiraLocalProcessingListRequest request = new()
                {
                    Limit = BulkPageSize,
                    Offset = offset,
                };
                using HttpResponseMessage response = await httpClient
                    .PostAsJsonAsync(requestPath, request, ct)
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

    private static async Task<(Dictionary<string, string>? Resolved, string? FailureReason)> TryFetchFromSqliteAsync(
        string jiraSourceDbPath,
        IReadOnlyList<string> keys,
        CancellationToken ct)
    {
        if (!File.Exists(jiraSourceDbPath))
        {
            return (null, "file not found");
        }

        try
        {
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

            return (resolved, null);
        }
        catch (SqliteException ex)
        {
            return (null, ex.Message);
        }
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

/// <summary>
/// Result of a <see cref="SpecificationBackfillService.RunAsync"/> call.
/// <see cref="Failure"/> is non-null iff both upstreams (HTTP and the
/// optional <c>JiraSourceDbPath</c> SQLite fallback) were unreachable.
/// </summary>
public sealed record SpecificationBackfillResult(
    int Updated,
    int StillEmpty,
    int NotFound,
    SpecificationBackfillFailure? Failure);

/// <summary>
/// Describes a Specification-backfill upstream failure. The startup
/// hosted service surfaces this as a fatal startup error; the admin
/// endpoint surfaces it as HTTP 503.
/// </summary>
public sealed record SpecificationBackfillFailure(
    string Reason,
    string? HttpReason,
    string? SqliteReason);
