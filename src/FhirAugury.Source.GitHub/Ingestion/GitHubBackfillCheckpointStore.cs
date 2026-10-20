using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Owns the two <c>sync_state</c> namespaces that describe per-repo history-backfill state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Terminal</b> state lives under <see cref="MarkerPrefix"/> with <c>Status = "success"</c>,
/// and is written only when a repo's corpus is genuinely complete. <b>In-flight</b> state
/// lives under the separate <see cref="ProgressPrefix"/> with the cursor JSON in
/// <c>LastCursor</c>.
/// </para>
/// <para>
/// Two prefixes rather than one row is a deliberate rollback-safety choice: an older binary
/// treats <i>any</i> <c>backfill:&lt;repo&gt;</c> row as complete, so partial state must never
/// live under that prefix or a rollback would silently skip the backfill forever.
/// </para>
/// <para>
/// <see cref="GitHubSyncStateReader.GetMostRecentOperational"/> allowlists
/// <c>["incremental", "full", "rebuild"]</c>, so both prefixes are already excluded from
/// operational reads and need no change there.
/// </para>
/// </remarks>
public class GitHubBackfillCheckpointStore(
    GitHubDatabase database,
    ILogger<GitHubBackfillCheckpointStore> logger)
{
    /// <summary>Prefix for the terminal completion marker. Never holds partial state.</summary>
    public const string MarkerPrefix = "backfill:";

    /// <summary>Prefix for the in-flight progress row that carries the resume cursor.</summary>
    public const string ProgressPrefix = "backfill-progress:";

    /// <summary>Status written when a pass ended early with work still to do.</summary>
    public const string StatusPartial = "partial";

    /// <summary>Status written when both phases are exhausted but items await repair.</summary>
    public const string StatusRepairRequired = "repair_required";

    /// <summary>Status written on the terminal marker; the only value that gates backfill.</summary>
    public const string StatusSuccess = "success";

    /// <summary>Reads the resume cursor for a repo, or null when there is no progress row.</summary>
    public GitHubBackfillCursor? ReadCursor(string repo)
    {
        using SqliteConnection connection = database.OpenConnection();
        GitHubSyncStateRecord? row = GitHubSyncStateRecord.SelectSingle(
            connection, SourceName: IGitHubDataProvider.SourceName, SubSource: ProgressPrefix + repo);

        return GitHubBackfillCursor.FromJson(row?.LastCursor);
    }

    /// <summary>
    /// Returns repos with a terminal <c>backfill:&lt;repo&gt;</c> marker at
    /// <c>Status = "success"</c>. Progress rows are deliberately ignored, so a repo that is
    /// merely part-way through is still reported as needing backfill.
    /// </summary>
    public HashSet<string> GetCompletedRepos()
    {
        using SqliteConnection connection = database.OpenConnection();

        return GitHubSyncStateRecord
            .SelectList(connection, SourceName: IGitHubDataProvider.SourceName)
            .Where(r => r.SubSource.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            .Where(r => !r.SubSource.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            .Where(r => string.Equals(r.Status, StatusSuccess, StringComparison.Ordinal))
            .Select(r => r.SubSource[MarkerPrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Upserts the in-flight progress row. Never writes a <see cref="MarkerPrefix"/> row, so
    /// a checkpointed repo always remains in the needs-backfill set.
    /// </summary>
    public void WriteCheckpoint(string repo, GitHubBackfillCursor cursor, int itemsIngested, string? lastError)
    {
        string status = cursor.PendingRetry.Length > 0 && cursor.IssuesPhaseComplete && cursor.PrsPhaseComplete
            ? StatusRepairRequired
            : StatusPartial;

        using SqliteConnection connection = database.OpenConnection();
        Upsert(connection, ProgressPrefix + repo, status, cursor.ToJson(), itemsIngested, lastError);

        logger.LogDebug(
            "Checkpointed backfill for {Repo}: status={Status}, issues>={Issues}, prs>={Prs}, pending={Pending}",
            repo, status, cursor.IssuesCompletedAbove, cursor.PrsCompletedAbove, cursor.PendingRetry.Length);
    }

    /// <summary>
    /// Writes the terminal <c>backfill:&lt;repo&gt;</c> marker and deletes the progress row,
    /// so the repo drops out of the needs-backfill set permanently.
    /// </summary>
    public void MarkComplete(string repo, int itemsIngested)
    {
        using SqliteConnection connection = database.OpenConnection();

        Upsert(connection, MarkerPrefix + repo, StatusSuccess, lastCursor: null, itemsIngested, lastError: null);
        DeleteRow(connection, ProgressPrefix + repo);

        logger.LogInformation("Backfill complete for {Repo}: {Count} item(s) ingested", repo, itemsIngested);
    }

    /// <summary>
    /// Removes both the progress row and any terminal marker, so the next pass re-backfills
    /// the repo from the top. Used by the forced-re-backfill path.
    /// </summary>
    public void ClearProgress(string repo)
    {
        using SqliteConnection connection = database.OpenConnection();

        DeleteRow(connection, ProgressPrefix + repo);
        DeleteRow(connection, MarkerPrefix + repo);

        logger.LogInformation("Cleared backfill progress and completion marker for {Repo}", repo);
    }

    private static void Upsert(
        SqliteConnection connection, string subSource, string status,
        string? lastCursor, int itemsIngested, string? lastError)
    {
        GitHubSyncStateRecord? existing = GitHubSyncStateRecord.SelectSingle(
            connection, SourceName: IGitHubDataProvider.SourceName, SubSource: subSource);

        GitHubSyncStateRecord row = new GitHubSyncStateRecord
        {
            Id = existing?.Id ?? GitHubSyncStateRecord.GetIndex(),
            SourceName = IGitHubDataProvider.SourceName,
            SubSource = subSource,
            LastSyncAt = DateTimeOffset.UtcNow,
            LastCursor = lastCursor,
            ItemsIngested = itemsIngested,
            SyncSchedule = null,
            NextScheduledAt = null,
            Status = status,
            LastError = lastError,
        };

        if (existing is not null)
            GitHubSyncStateRecord.Update(connection, row);
        else
            GitHubSyncStateRecord.Insert(connection, row);
    }

    private static void DeleteRow(SqliteConnection connection, string subSource)
    {
        // Deliberately not GitHubSyncStateRecord.Delete(connection, record): the generated
        // single-value overload never binds its $Id parameter, so it deletes nothing and
        // then throws on rowsAffected == 0.
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM sync_state WHERE SourceName = @source AND SubSource = @sub";
        cmd.Parameters.AddWithValue("@source", IGitHubDataProvider.SourceName);
        cmd.Parameters.AddWithValue("@sub", subSource);
        cmd.ExecuteNonQuery();
    }
}
