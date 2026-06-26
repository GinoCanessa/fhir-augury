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
    ILogger<GitHubCliProvider> logger) : IGitHubDataProvider
{
    private readonly GitHubServiceOptions _options = optionsAccessor.Value;

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
    public async Task<IngestionResult> DownloadBackfillAsync(string? repoFilter = null, CancellationToken ct = default)
    {
        List<string> repos = repoFilter is not null ? [repoFilter] : GetEffectiveRepositories();
        return await DownloadReposBackfillAsync(repos, ct);
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

        foreach (string repoFullName in repos)
        {
            if (ct.IsCancellationRequested) break;

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
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed gh api --paginate for {Repo}", repoFullName);
                errors.Add($"repo:{repoFullName} - {ex.Message}");
            }
        }

        logger.LogInformation(
            "Full download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt);
    }

    // ── Incremental sync via gh issue list / gh pr list ───────────────────

    private Task<IngestionResult> DownloadReposIncrementalAsync(
        List<string> repos, DateTimeOffset since, CancellationToken ct)
    {
        string sinceStr = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        return DownloadReposListAsync(repos, _options.GhCli.Limit, $"updated:>={sinceStr}", ct);
    }

    private Task<IngestionResult> DownloadReposBackfillAsync(List<string> repos, CancellationToken ct)
    {
        // No search filter ⇒ full PR/issue history (drops the updated:>= bound).
        return DownloadReposListAsync(repos, _options.GhCli.BackfillLimit, searchFilter: null, ct);
    }

    /// <summary>
    /// Shared <c>gh issue list</c> / <c>gh pr list</c> fetch body. The only
    /// difference between the incremental and backfill paths is
    /// <paramref name="searchFilter"/> (a <c>updated:&gt;=</c> bound vs. none) and
    /// the <paramref name="limit"/>.
    /// </summary>
    private async Task<IngestionResult> DownloadReposListAsync(
        List<string> repos, int limit, string? searchFilter, CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = [];

        using SqliteConnection connection = database.OpenConnection();

        foreach (string repoFullName in repos)
        {
            if (ct.IsCancellationRequested) break;

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
            }
            else
            {
                string issueArgs = BuildListArgs("issue list", repoArgs, limit, searchFilter, IssueListFields);
                try
                {
                    await foreach (JsonElement json in runner.StreamArrayAsync(issueArgs, ct))
                    {
                        (ProcessOutcome outcome, string? error) = ProcessCliIssue(json, repoFullName, connection);
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

                        if (outcome != ProcessOutcome.Failed)
                        {
                            int issueNumber = json.GetProperty("number").GetInt32();
                            await FetchCommentsAsync(connection, repoFullName, issueNumber, isPr: false, ct, errors);
                        }
                    }
                }
                catch (Exception ex) when (ex.Message.Contains("has disabled issues", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Skipping issues for {Repo} (issues are disabled)", repoFullName);
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
                await foreach (JsonElement json in runner.StreamArrayAsync(prArgs, ct))
                {
                    (ProcessOutcome outcome, string? error) = ProcessCliPr(json, repoFullName, connection);
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

                    if (outcome != ProcessOutcome.Failed)
                    {
                        int prNumber = json.GetProperty("number").GetInt32();
                        await FetchCommentsAsync(connection, repoFullName, prNumber, isPr: true, ct, errors);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch PRs for {Repo}", repoFullName);
                errors.Add($"prs:{repoFullName} - {ex.Message}");
            }
        }

        logger.LogInformation(
            "List download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt);
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
