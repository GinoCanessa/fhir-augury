using System.Text.Json;
using FhirAugury.Common.Caching;
using FhirAugury.Source.GitHub.Cache;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Fetches issues, PRs, comments, and repo metadata using the <c>gh</c> CLI tool.
/// <para>
/// Full sync uses <c>gh api --paginate</c> (REST-identical JSON, reuses <see cref="GitHubIssueMapper"/>).
/// Incremental sync uses <c>gh issue list</c> / <c>gh pr list</c> for richer fields.
/// Comments are fetched per-issue via <c>gh issue view --json comments</c>.
/// PR review comments are fetched via <c>gh pr view --json reviews</c>.
/// </para>
/// </summary>
public class GitHubCliProvider(
    IOptions<GitHubServiceOptions> optionsAccessor,
    GhCliRunner runner,
    GitHubDatabase database,
    IResponseCache cache,
    GitHubBackfillCheckpointStore checkpointStore,
    ILogger<GitHubCliProvider> logger) : IGitHubDataProvider
{
    private readonly GitHubServiceOptions _options = optionsAccessor.Value;

    /// <summary>Hard ceiling on the pending-retry set, so a pathological run cannot bloat the cursor.</summary>
    internal const int MaxPendingRetry = 1000;

    // Fields requested from gh issue list
    internal const string IssueListFields = "number,title,body,state,author,assignees,labels,milestone,createdAt,updatedAt,closedAt,url";

    // Fields requested from gh pr list
    internal const string PrListFields = "number,title,body,state,author,assignees,labels,milestone,createdAt,updatedAt,closedAt,mergedAt,headRefName,baseRefName,isDraft,url";

    /// <summary>
    /// Builds a <c>gh issue list</c> / <c>gh pr list</c> argument string. When
    /// <paramref name="searchFilter"/> is null (history backfill) the
    /// <c>-S "&lt;filter&gt;"</c> clause is omitted entirely, so the full history
    /// is fetched; the incremental path passes <c>updated:&gt;=&lt;ts&gt;</c>.
    /// </summary>
    internal static string BuildListArgs(string command, string repoArgs, int limit, string? searchFilter, string fields)
    {
        string searchClause = searchFilter is not null ? $" -S \"{searchFilter}\"" : "";
        return $"{command} {repoArgs} --state all --limit {limit}{searchClause} --json {fields}";
    }

    /// <inheritdoc />
    public async Task<IngestionResult> DownloadAllAsync(string? repoFilter = null, CancellationToken ct = default)
    {
        List<string> repos = repoFilter is not null ? [repoFilter] : GetEffectiveRepositories();
        return await DownloadReposFullAsync(repos, ct);
    }

    /// <inheritdoc />
    public async Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        List<string> repos = GetEffectiveRepositories();
        return await DownloadReposIncrementalAsync(repos, since, ct);
    }

    /// <inheritdoc />
    public async Task<IngestionResult> DownloadBackfillAsync(
        string? repoFilter = null,
        GitHubBackfillCursor? resumeFrom = null,
        CancellationToken ct = default)
    {
        List<string> repos = repoFilter is not null ? [repoFilter] : GetEffectiveRepositories();
        return await DownloadReposBackfillAsync(repos, resumeFrom, ct);
    }

    /// <inheritdoc />
    public Task<IngestionResult> LoadFromCacheAsync(CancellationToken ct = default)
    {
        // Cache format is normalized to REST API JSON, so we reuse GitHubIssueMapper
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = [];

        using SqliteConnection connection = database.OpenConnection();

        foreach (string key in cache.EnumerateKeys(GitHubCacheLayout.SourceName))
        {
            if (ct.IsCancellationRequested) break;
            if (key.StartsWith(GitHubCacheLayout.ReposSubDir + "/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!key.EndsWith("." + GitHubCacheLayout.JsonExtension, StringComparison.OrdinalIgnoreCase)) continue;
            if (!cache.TryGet(GitHubCacheLayout.SourceName, key, out Stream? stream) || stream is null) continue;

            using (stream)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(stream);
                    JsonElement root = doc.RootElement;

                    if (!root.TryGetProperty("issues", out JsonElement issues)) continue;
                    string repoFullName = root.TryGetProperty("repo", out JsonElement repoEl) ? repoEl.GetString() ?? "" : "";

                    foreach (JsonElement issueJson in issues.EnumerateArray())
                    {
                        GitHubIssueRecord record = GitHubIssueMapper.MapIssue(issueJson, repoFullName);
                        GitHubIssueRecord? existing = GitHubIssueRecord.SelectSingle(connection, UniqueKey: record.UniqueKey);

                        if (existing is not null)
                        {
                            record.Id = existing.Id;
                            GitHubIssueRecord.Update(connection, record);
                            itemsUpdated++;
                        }
                        else
                        {
                            GitHubIssueRecord.Insert(connection, record, ignoreDuplicates: true);
                            itemsNew++;
                        }
                        itemsProcessed++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to process cached file {Key}", key);
                    itemsFailed++;
                    errors.Add($"{key}: {ex.Message}");
                }
            }
        }

        logger.LogInformation(
            "Cache ingestion complete: {Processed} processed, {New} new, {Updated} updated",
            itemsProcessed, itemsNew, itemsUpdated);

        return Task.FromResult(new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt));
    }

    // ── Full sync via gh api --paginate (REST-identical JSON) ─────────────

    private async Task<IngestionResult> DownloadReposFullAsync(List<string> repos, CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = [];

        using SqliteConnection connection = database.OpenConnection();

        bool canceled = false;

        foreach (string repoFullName in repos)
        {
            if (ct.IsCancellationRequested) { canceled = true; break; }

            try
            {
                logger.LogInformation("Fetching repository via gh CLI: {Repo}", repoFullName);

                // Fetch and upsert repo metadata
                await FetchRepoMetadataAsync(connection, repoFullName, ct, errors);

                // Full sync: use gh api --paginate for REST-identical JSON
                string apiPath = $"/repos/{repoFullName}/issues?state=all&per_page=100&sort=updated&direction=asc";

                logger.LogInformation("Running gh api --paginate for {Repo}", repoFullName);

                try
                {
                    await foreach (JsonElement issueJson in runner.StreamPaginatedApiAsync(apiPath, ct))
                    {
                        ct.ThrowIfCancellationRequested();

                        (ProcessOutcome outcome, string? error) = ProcessRestIssue(issueJson, repoFullName, connection);
                        itemsProcessed++;

                        switch (outcome)
                        {
                            case ProcessOutcome.New: itemsNew++; break;
                            case ProcessOutcome.Updated: itemsUpdated++; break;
                            case ProcessOutcome.Failed:
                                itemsFailed++;
                                if (error is not null) errors.Add(error);
                                break;
                        }

                        // Fetch comments for this issue
                        if (outcome != ProcessOutcome.Failed)
                        {
                            int issueNumber = issueJson.GetProperty("number").GetInt32();
                            bool isPr = issueJson.TryGetProperty("pull_request", out _);
                            await FetchCommentsAsync(connection, repoFullName, issueNumber, isPr, ct, errors);
                        }

                        if (itemsProcessed % 1000 == 0)
                            logger.LogInformation("Download progress: {Count} issues processed", itemsProcessed);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed gh api --paginate for {Repo}", repoFullName);
                    errors.Add($"repo:{repoFullName} - {ex.Message}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                canceled = true;
                break;
            }
        }

        if (canceled)
        {
            logger.LogInformation(
                "Full download canceled after {Processed} item(s): {New} new, {Updated} updated, {Failed} failed",
                itemsProcessed, itemsNew, itemsUpdated, itemsFailed);
        }
        else
        {
            logger.LogInformation(
                "Full download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
                itemsProcessed, itemsNew, itemsUpdated, itemsFailed);
        }

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt)
        {
            Canceled = canceled,
        };
    }

    // ── Incremental sync via gh issue list / gh pr list ───────────────────

    private Task<IngestionResult> DownloadReposIncrementalAsync(
        List<string> repos, DateTimeOffset since, CancellationToken ct)
    {
        string sinceStr = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        return DownloadReposListAsync(
            repos, _options.GhCli.Limit, $"updated:>={sinceStr}", resumeFrom: null, isBackfill: false, ct);
    }

    private Task<IngestionResult> DownloadReposBackfillAsync(
        List<string> repos, GitHubBackfillCursor? resumeFrom, CancellationToken ct)
    {
        // No search filter ⇒ full PR/issue history (drops the updated:>= bound).
        return DownloadReposListAsync(
            repos, _options.GhCli.BackfillLimit, searchFilter: null, resumeFrom, isBackfill: true, ct);
    }

    /// <summary>
    /// Shared <c>gh issue list</c> / <c>gh pr list</c> fetch body. The incremental path
    /// passes a <c>updated:&gt;=</c> bound and no cursor; the backfill path passes no filter,
    /// the larger limit, and (on a resume) a <see cref="GitHubBackfillCursor"/> whose
    /// watermark lets already-completed items skip their detail fetch.
    /// </summary>
    /// <remarks>
    /// Under <paramref name="isBackfill"/> this method owns terminal state: it writes the
    /// <c>backfill:&lt;repo&gt;</c> marker only when both phases enumerated to exhaustion with
    /// nothing left to repair, and otherwise checkpoints progress for the next pass.
    /// </remarks>
    private async Task<IngestionResult> DownloadReposListAsync(
        List<string> repos, int limit, string? searchFilter,
        GitHubBackfillCursor? resumeFrom, bool isBackfill, CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        ListCounters counters = new ListCounters();
        List<string> errors = [];

        using SqliteConnection connection = database.OpenConnection();

        bool canceled = false;

        foreach (string repoFullName in repos)
        {
            if (ct.IsCancellationRequested) { canceled = true; break; }

            RepoBackfillState state = new RepoBackfillState(isBackfill ? resumeFrom : null);
            int incomingPendingCount = state.PendingRetry.Count;
            bool repoCanceled = false;

            try
            {
                logger.LogInformation(
                    "List sync via gh CLI for {Repo} (filter: {Filter})",
                    repoFullName, searchFilter ?? "<full history>");
                string repoArgs = runner.BuildRepoArgs(repoFullName);

                // Fetch repo metadata so we know whether issues are enabled
                await FetchRepoMetadataAsync(connection, repoFullName, ct, errors);
                GitHubRepoRecord? repoRecord = GitHubRepoRecord.SelectSingle(connection, FullName: repoFullName);
                bool hasIssues = repoRecord?.HasIssues ?? true; // assume enabled if unknown

                // Fetch issues (skip when the repo has issues disabled)
                if (!hasIssues)
                {
                    logger.LogInformation("Skipping issues for {Repo} (issues are disabled)", repoFullName);
                    state.IssuesPhaseComplete = true;
                }
                else
                {
                    string issueArgs = BuildListArgs("issue list", repoArgs, limit, searchFilter, IssueListFields);
                    try
                    {
                        await RunListPhaseAsync(
                            connection, repoFullName, issueArgs, isPr: false, limit,
                            state, counters, errors, isBackfill, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex.Message.Contains("has disabled issues", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogInformation("Skipping issues for {Repo} (issues are disabled)", repoFullName);
                        state.IssuesPhaseComplete = true;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to fetch issues for {Repo}", repoFullName);
                        errors.Add($"issues:{repoFullName} - {ex.Message}");
                    }
                }

                // Fetch PRs
                string prArgs = BuildListArgs("pr list", repoArgs, limit, searchFilter, PrListFields);
                try
                {
                    await RunListPhaseAsync(
                        connection, repoFullName, prArgs, isPr: true, limit,
                        state, counters, errors, isBackfill, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to fetch PRs for {Repo}", repoFullName);
                    errors.Add($"prs:{repoFullName} - {ex.Message}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                canceled = true;
                repoCanceled = true;
            }
            finally
            {
                if (isBackfill)
                {
                    FinalizeRepoBackfill(
                        repoFullName, state, counters, errors, incomingPendingCount, repoCanceled);
                }
            }

            if (canceled) break;
        }

        if (canceled)
        {
            logger.LogInformation(
                "List download canceled after {Processed} item(s): {New} new, {Updated} updated, {Failed} failed",
                counters.Processed, counters.New, counters.Updated, counters.Failed);
        }
        else
        {
            logger.LogInformation(
                "List download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
                counters.Processed, counters.New, counters.Updated, counters.Failed);
        }

        return new IngestionResult(
            counters.Processed, counters.New, counters.Updated, counters.Failed, errors, startedAt)
        {
            Canceled = canceled,
        };
    }

    /// <summary>
    /// Runs one list phase (issues or PRs) for a single repo, descending by item number so a
    /// single watermark describes everything already done.
    /// </summary>
    private async Task RunListPhaseAsync(
        SqliteConnection connection, string repoFullName, string args, bool isPr, int limit,
        RepoBackfillState state, ListCounters counters, List<string> errors,
        bool isBackfill, CancellationToken ct)
    {
        int? watermark = isPr ? state.PrsCompletedAbove : state.IssuesCompletedAbove;
        bool phaseAlreadyComplete = isPr ? state.PrsPhaseComplete : state.IssuesPhaseComplete;

        // A finished phase with nothing to repair needs no list fetch at all. When repairs are
        // outstanding the list is still cheap (one point) and is the only way to learn which
        // side of the issue/PR split each pending number falls on.
        if (isBackfill && phaseAlreadyComplete && state.PendingRetry.Count == 0)
        {
            logger.LogInformation(
                "Skipping {Phase} phase for {Repo}: already complete with no pending retries",
                isPr ? "PR" : "issue", repoFullName);
            return;
        }

        List<JsonElement> items = [];
        await foreach (JsonElement json in runner.StreamArrayAsync(args, ct))
        {
            ct.ThrowIfCancellationRequested();
            items.Add(json);
        }

        // Enforce the descending invariant in our own code rather than inheriting gh's
        // default ordering, which the watermark would otherwise silently depend on.
        items.Sort((a, b) => b.GetProperty("number").GetInt32()
            .CompareTo(a.GetProperty("number").GetInt32()));

        if (isBackfill && (watermark is not null || state.PendingRetry.Count > 0))
        {
            logger.LogInformation(
                "Resuming backfill for {Repo}: first item #{First}, watermark #{Watermark}, {PendingCount} pending retry",
                repoFullName,
                items.Count > 0 ? items[0].GetProperty("number").GetInt32() : 0,
                watermark,
                state.PendingRetry.Count);
        }

        int? lastCompleted = null;
        int sinceCheckpoint = 0;
        bool ranToExhaustion = false;

        try
        {
            foreach (JsonElement json in items)
            {
                ct.ThrowIfCancellationRequested();

                // The list row is upserted unconditionally — the JSON is already in hand, so
                // re-applying it costs nothing and keeps metadata fresh on a resume.
                (ProcessOutcome outcome, string? error) = isPr
                    ? ProcessCliPr(json, repoFullName, connection)
                    : ProcessCliIssue(json, repoFullName, connection);

                counters.Processed++;

                switch (outcome)
                {
                    case ProcessOutcome.New: counters.New++; break;
                    case ProcessOutcome.Updated: counters.Updated++; break;
                    case ProcessOutcome.Failed:
                        counters.Failed++;
                        if (error is not null) errors.Add(error);
                        break;
                }

                int number = json.GetProperty("number").GetInt32();

                if (outcome == ProcessOutcome.Failed)
                {
                    if (isBackfill) state.AddPendingRetry(number);
                    continue;
                }

                bool alreadyDone = watermark is int w
                    && number >= w
                    && !state.PendingRetry.Contains(number);

                if (isBackfill && alreadyDone)
                {
                    // Traversing the already-completed prefix: no detail fetch, and no
                    // watermark update, so the existing watermark cannot regress.
                    continue;
                }

                int errorsBefore = errors.Count;
                await FetchCommentsAsync(connection, repoFullName, number, isPr, ct, errors);

                if (!isBackfill) continue;

                if (errors.Count > errorsBefore)
                {
                    state.AddPendingRetry(number);
                }
                else
                {
                    state.PendingRetry.Remove(number);

                    // Advance only past items whose detail work returned clean, and never
                    // once the retry set has overflowed — beyond that point failures can no
                    // longer be recorded, so nothing below may be claimed as done.
                    if (!state.PendingOverflowed)
                        lastCompleted = number;
                }

                if (++sinceCheckpoint >= Math.Max(1, _options.GhCli.BackfillCheckpointInterval))
                {
                    sinceCheckpoint = 0;
                    CommitWatermark(state, isPr, lastCompleted, watermark);
                    checkpointStore.WriteCheckpoint(
                        repoFullName, state.ToCursor(), counters.Processed,
                        errors.Count > 0 ? errors[^1] : null);
                }
            }

            ranToExhaustion = true;
        }
        finally
        {
            // In a finally so an interrupted item is never counted as done and a cancelled
            // pass still persists everything that genuinely completed.
            if (isBackfill)
            {
                CommitWatermark(state, isPr, lastCompleted, watermark);

                bool truncated = items.Count >= limit;
                if (truncated)
                {
                    logger.LogWarning(
                        "{Phase} list for {Repo} returned {Count} item(s), equal to GhCli.BackfillLimit — " +
                        "the list was truncated and older items are unreachable; phase left incomplete. " +
                        "Raise GhCli.BackfillLimit to close the gap",
                        isPr ? "PR" : "Issue", repoFullName, items.Count);
                }

                bool complete = ranToExhaustion && !ct.IsCancellationRequested && !truncated;

                if (isPr) state.PrsPhaseComplete = complete;
                else state.IssuesPhaseComplete = complete;
            }
        }
    }

    /// <summary>
    /// Folds this pass's lowest cleanly-completed item into the phase watermark. Uses
    /// <see cref="Math.Min(int, int)"/> so the watermark only ever descends, which keeps it
    /// correct while traversing an already-completed prefix.
    /// </summary>
    private static void CommitWatermark(RepoBackfillState state, bool isPr, int? lastCompleted, int? existing)
    {
        if (lastCompleted is not int completed) return;

        int value = Math.Min(completed, existing ?? int.MaxValue);

        if (isPr) state.PrsCompletedAbove = value;
        else state.IssuesCompletedAbove = value;
    }

    /// <summary>
    /// Writes the repo's terminal marker or its resume checkpoint, applying the stall valve
    /// that stops a permanently unfetchable item from recreating a backfill that never ends.
    /// </summary>
    private void FinalizeRepoBackfill(
        string repoFullName, RepoBackfillState state, ListCounters counters,
        List<string> errors, int incomingPendingCount, bool repoCanceled)
    {
        GitHubBackfillCursor cursor = state.ToCursor();

        if (!repoCanceled && cursor.IsComplete)
        {
            checkpointStore.MarkComplete(repoFullName, counters.Processed);
            return;
        }

        if (!repoCanceled &&
            cursor.PendingRetry.Length > 0 &&
            cursor.IssuesPhaseComplete &&
            cursor.PrsPhaseComplete &&
            cursor.PendingRetry.Length >= incomingPendingCount)
        {
            state.StalledRepairPasses++;
            cursor = state.ToCursor();

            if (state.StalledRepairPasses >= Math.Max(1, _options.GhCli.BackfillMaxRepairPasses))
            {
                logger.LogWarning(
                    "Backfill for {Repo} stalled after {Passes} repair pass(es); marking complete and " +
                    "abandoning {Count} unfetchable item(s): {Numbers}",
                    repoFullName, state.StalledRepairPasses, cursor.PendingRetry.Length,
                    string.Join(", ", cursor.PendingRetry.Take(50)));

                checkpointStore.MarkComplete(repoFullName, counters.Processed);
                return;
            }
        }

        checkpointStore.WriteCheckpoint(
            repoFullName, cursor, counters.Processed, errors.Count > 0 ? errors[^1] : null);
    }

    /// <summary>Mutable per-repo counters, shared across both list phases.</summary>
    private sealed class ListCounters
    {
        public int Processed;
        public int New;
        public int Updated;
        public int Failed;
    }

    /// <summary>Mutable working copy of a <see cref="GitHubBackfillCursor"/> for one repo.</summary>
    private sealed class RepoBackfillState
    {
        public RepoBackfillState(GitHubBackfillCursor? resumeFrom)
        {
            IssuesCompletedAbove = resumeFrom?.IssuesCompletedAbove;
            PrsCompletedAbove = resumeFrom?.PrsCompletedAbove;
            IssuesPhaseComplete = resumeFrom?.IssuesPhaseComplete ?? false;
            PrsPhaseComplete = resumeFrom?.PrsPhaseComplete ?? false;
            StalledRepairPasses = resumeFrom?.StalledRepairPasses ?? 0;
            PendingRetry = new SortedSet<int>(resumeFrom?.PendingRetry ?? []);
        }

        public int? IssuesCompletedAbove;
        public int? PrsCompletedAbove;
        public bool IssuesPhaseComplete;
        public bool PrsPhaseComplete;
        public int StalledRepairPasses;
        public readonly SortedSet<int> PendingRetry;

        /// <summary>True once the retry set hit its cap and a failure could not be recorded.</summary>
        public bool PendingOverflowed { get; private set; }

        public void AddPendingRetry(int number)
        {
            if (PendingRetry.Contains(number)) return;

            if (PendingRetry.Count >= MaxPendingRetry)
            {
                PendingOverflowed = true;
                return;
            }

            PendingRetry.Add(number);
        }

        public GitHubBackfillCursor ToCursor() => new GitHubBackfillCursor
        {
            IssuesCompletedAbove = IssuesCompletedAbove,
            PrsCompletedAbove = PrsCompletedAbove,
            IssuesPhaseComplete = IssuesPhaseComplete,
            PrsPhaseComplete = PrsPhaseComplete,
            PendingRetry = [.. PendingRetry],
            StalledRepairPasses = StalledRepairPasses,
        };
    }

    // ── Process individual items ─────────────────────────────────────────

    /// <summary>Processes an issue from REST-identical JSON (gh api --paginate).</summary>
    private (ProcessOutcome Outcome, string? Error) ProcessRestIssue(
        JsonElement json, string repoFullName, SqliteConnection connection)
    {
        string uniqueKey = string.Empty;
        try
        {
            GitHubIssueRecord record = GitHubIssueMapper.MapIssue(json, repoFullName);
            uniqueKey = record.UniqueKey;
            return UpsertIssue(connection, record);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process issue {Key}", uniqueKey);
            return (ProcessOutcome.Failed, $"{uniqueKey}: {ex.Message}");
        }
    }

    /// <summary>Processes an issue from gh CLI JSON (gh issue list).</summary>
    private (ProcessOutcome Outcome, string? Error) ProcessCliIssue(
        JsonElement json, string repoFullName, SqliteConnection connection)
    {
        string uniqueKey = string.Empty;
        try
        {
            GitHubIssueRecord record = GhCliIssueMapper.MapIssue(json, repoFullName);
            uniqueKey = record.UniqueKey;
            return UpsertIssue(connection, record);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process issue {Key}", uniqueKey);
            return (ProcessOutcome.Failed, $"{uniqueKey}: {ex.Message}");
        }
    }

    /// <summary>Processes a PR from gh CLI JSON (gh pr list).</summary>
    private (ProcessOutcome Outcome, string? Error) ProcessCliPr(
        JsonElement json, string repoFullName, SqliteConnection connection)
    {
        string uniqueKey = string.Empty;
        try
        {
            GitHubIssueRecord record = GhCliIssueMapper.MapPullRequest(json, repoFullName);
            uniqueKey = record.UniqueKey;
            return UpsertIssue(connection, record);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process PR {Key}", uniqueKey);
            return (ProcessOutcome.Failed, $"{uniqueKey}: {ex.Message}");
        }
    }

    private static (ProcessOutcome Outcome, string? Error) UpsertIssue(
        SqliteConnection connection, GitHubIssueRecord record)
    {
        GitHubIssueRecord? existing = GitHubIssueRecord.SelectSingle(connection, UniqueKey: record.UniqueKey);
        if (existing is not null)
        {
            record.Id = existing.Id;
            GitHubIssueRecord.Update(connection, record);
            return (ProcessOutcome.Updated, null);
        }

        GitHubIssueRecord.Insert(connection, record, ignoreDuplicates: true);
        return (ProcessOutcome.New, null);
    }

    // ── Repo metadata ────────────────────────────────────────────────────

    private async Task FetchRepoMetadataAsync(
        SqliteConnection connection, string repoFullName, CancellationToken ct, List<string> errors)
    {
        try
        {
            string repoArgs = runner.BuildRepoArgs(repoFullName);
            string args = $"repo view {repoFullName} --json name,nameWithOwner,description,hasIssuesEnabled,owner,defaultBranchRef";
            using JsonDocument doc = await runner.RunAsync(args, ct);
            GitHubRepoRecord record = GhCliIssueMapper.MapRepo(doc.RootElement);

            GitHubRepoRecord? existing = GitHubRepoRecord.SelectSingle(connection, FullName: record.FullName);
            if (existing is not null)
            {
                record.Id = existing.Id;
                GitHubRepoRecord.Update(connection, record);
            }
            else
            {
                GitHubRepoRecord.Insert(connection, record, ignoreDuplicates: true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch repo metadata for {Repo}", repoFullName);
            errors.Add($"repo_metadata:{repoFullName} - {ex.Message}");
        }
    }

    // ── Comments & reviews ───────────────────────────────────────────────

    private async Task FetchCommentsAsync(
        SqliteConnection connection, string repoFullName, int issueNumber, bool isPr,
        CancellationToken ct, List<string> errors)
    {
        // Look up the issue's DB ID
        GitHubIssueRecord? issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: $"{repoFullName}#{issueNumber}");
        int issueDbId = issue?.Id ?? 0;

        // Fetch regular comments
        try
        {
            string repoArgs = runner.BuildRepoArgs(repoFullName);
            string cmd = isPr ? "pr" : "issue";
            string args = $"{cmd} view {issueNumber} {repoArgs} --json comments";
            using JsonDocument doc = await runner.RunAsync(args, ct);

            if (doc.RootElement.TryGetProperty("comments", out JsonElement comments))
            {
                foreach (JsonElement commentJson in comments.EnumerateArray())
                {
                    GitHubCommentRecord comment = GhCliIssueMapper.MapComment(
                        commentJson, issueDbId, repoFullName, issueNumber);
                    GitHubCommentRecord.Insert(connection, comment, ignoreDuplicates: true);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch comments for {Repo}#{Number}", repoFullName, issueNumber);
            errors.Add($"comments:{repoFullName}#{issueNumber} - {ex.Message}");
        }

        // Fetch PR review comments
        if (isPr)
        {
            try
            {
                string repoArgs = runner.BuildRepoArgs(repoFullName);
                string args = $"pr view {issueNumber} {repoArgs} --json reviews";
                using JsonDocument doc = await runner.RunAsync(args, ct);

                if (doc.RootElement.TryGetProperty("reviews", out JsonElement reviews))
                {
                    foreach (JsonElement reviewJson in reviews.EnumerateArray())
                    {
                        // Only include reviews that have a body (non-empty review comments)
                        string? body = reviewJson.TryGetProperty("body", out JsonElement bodyEl)
                            ? bodyEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(body)) continue;

                        GitHubCommentRecord review = GhCliIssueMapper.MapReview(
                            reviewJson, issueDbId, repoFullName, issueNumber);
                        GitHubCommentRecord.Insert(connection, review, ignoreDuplicates: true);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch PR reviews for {Repo}#{Number}", repoFullName, issueNumber);
                errors.Add($"reviews:{repoFullName}#{issueNumber} - {ex.Message}");
            }

            await SyncPrCommitLinksAsync(connection, repoFullName, issueNumber, ct, errors);

            // Fetch inline (line-anchored) review-thread comments via REST so the
            // full PR conversation is captured. These flow through the existing
            // xref/BM25 passes (typed as ContentTypes.Comment) unchanged.
            try
            {
                await foreach (JsonElement commentJson in runner.StreamPaginatedApiAsync(
                    $"/repos/{repoFullName}/pulls/{issueNumber}/comments?per_page=100", ct))
                {
                    GitHubCommentRecord comment = GhCliIssueMapper.MapReviewThreadComment(
                        commentJson, issueDbId, repoFullName, issueNumber);
                    GitHubCommentRecord.Insert(connection, comment, ignoreDuplicates: true);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch PR review-thread comments for {Repo}#{Number}", repoFullName, issueNumber);
                errors.Add($"review_comments:{repoFullName}#{issueNumber} - {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Synchronises <c>github_commit_pr_links</c> for a single PR using
    /// <c>gh pr view {n} --json commits,baseRefName,mergedAt</c>. Uses
    /// delete-then-insert (replace) semantics so a force-push that rewrites the
    /// PR's commit set does not leave stale links, and also backfills the PR
    /// row's <c>BaseBranch</c>/<c>MergeState</c> (null after a full REST sync,
    /// which omits <c>base.ref</c>/<c>merged_at</c>) so the primary-PR rule is
    /// deterministic regardless of sync path.
    /// </summary>
    /// <remarks>
    /// PR fetch runs before <c>PostIngestionAsync</c> extracts commits from the
    /// clone, so some linked SHAs may not yet have a <c>github_commits</c> row.
    /// The link table does not FK to <c>github_commits</c>, so writing links
    /// ahead of the commit rows is correct; resolution only joins to commits
    /// that exist.
    /// </remarks>
    private async Task SyncPrCommitLinksAsync(
        SqliteConnection connection, string repoFullName, int issueNumber,
        CancellationToken ct, List<string> errors)
    {
        try
        {
            string repoArgs = runner.BuildRepoArgs(repoFullName);
            string args = $"pr view {issueNumber} {repoArgs} --json commits,baseRefName,mergedAt";
            using JsonDocument doc = await runner.RunAsync(args, ct);
            JsonElement root = doc.RootElement;

            using (SqliteCommand del = connection.CreateCommand())
            {
                del.CommandText = "DELETE FROM github_commit_pr_links WHERE RepoFullName = @repo AND PrNumber = @n";
                del.Parameters.AddWithValue("@repo", repoFullName);
                del.Parameters.AddWithValue("@n", issueNumber);
                del.ExecuteNonQuery();
            }

            if (root.TryGetProperty("commits", out JsonElement commits) &&
                commits.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement commitJson in commits.EnumerateArray())
                {
                    if (!commitJson.TryGetProperty("oid", out JsonElement oidEl)) continue;
                    string? oid = oidEl.GetString();
                    if (string.IsNullOrEmpty(oid)) continue;

                    GitHubCommitPrLinkRecord link = GhCliIssueMapper.MapCommitPrLink(oid, issueNumber, repoFullName);
                    GitHubCommitPrLinkRecord.Insert(connection, link, ignoreDuplicates: true);
                }
            }

            // Backfill BaseBranch/MergeState on the PR row when a full sync left them null.
            GitHubIssueRecord? pr = GitHubIssueRecord.SelectSingle(connection, UniqueKey: $"{repoFullName}#{issueNumber}");
            if (pr is not null)
            {
                string? baseRefName = root.TryGetProperty("baseRefName", out JsonElement brEl) ? brEl.GetString() : null;
                string? mergedAt = root.TryGetProperty("mergedAt", out JsonElement maEl) ? maEl.GetString() : null;

                bool changed = false;
                if (pr.BaseBranch is null && !string.IsNullOrEmpty(baseRefName))
                {
                    pr.BaseBranch = baseRefName;
                    changed = true;
                }
                if (pr.MergeState is null && !string.IsNullOrEmpty(mergedAt))
                {
                    pr.MergeState = "merged";
                    changed = true;
                }
                if (changed) GitHubIssueRecord.Update(connection, pr);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync commit-PR links for {Repo}#{Number}", repoFullName, issueNumber);
            errors.Add($"commit_pr_links:{repoFullName}#{issueNumber} - {ex.Message}");
        }
    }

    private List<string> GetEffectiveRepositories()
    {
        return _options.GetAllRepositoryNames();
    }

    private enum ProcessOutcome { New, Updated, Failed }
}
