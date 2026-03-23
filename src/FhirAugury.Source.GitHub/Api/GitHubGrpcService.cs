using System.Globalization;
using System.Text;
using Fhiraugury;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Text;
using FhirAugury.Source.GitHub.Cache;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Indexing;
using FhirAugury.Source.GitHub.Ingestion;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Api;

/// <summary>
/// Implements SourceService gRPC contract for the GitHub source.
/// </summary>
public class GitHubGrpcService(
    GitHubDatabase database,
    GitHubIngestionPipeline pipeline,
    IResponseCache cache,
    FhirAugury.Common.Ingestion.IngestionWorkQueue workQueue,
    IOptions<GitHubServiceOptions> optionsAccessor)
    : SourceService.SourceServiceBase
{
    private readonly GitHubServiceOptions _options = optionsAccessor.Value;
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    // ── SourceService RPCs ────────────────────────────────────────

    public override Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var ftsQuery = FtsQueryHelper.SanitizeFtsQuery(request.Query);

        if (string.IsNullOrEmpty(ftsQuery))
            return Task.FromResult(new SearchResponse { Query = request.Query });

        var limit = request.Limit > 0 ? Math.Min(request.Limit, 200) : 20;

        var sql = """
            SELECT gi.UniqueKey, gi.Title,
                   snippet(github_issues_fts, 1, '<b>', '</b>', '...', 20) as Snippet,
                   github_issues_fts.rank,
                   gi.State, gi.UpdatedAt
            FROM github_issues_fts
            JOIN github_issues gi ON gi.Id = github_issues_fts.rowid
            WHERE github_issues_fts MATCH @query
            ORDER BY github_issues_fts.rank
            LIMIT @limit OFFSET @offset
            """;

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        var response = new SearchResponse { Query = request.Query };
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var uniqueKey = reader.GetString(0);
            response.Results.Add(new SearchResultItem
            {
                Source = "github",
                Id = uniqueKey,
                Title = reader.GetString(1),
                Snippet = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Score = -reader.GetDouble(3),
                Url = BuildIssueUrl(uniqueKey),
                UpdatedAt = ParseTimestamp(reader, 5),
            });
        }

        response.TotalResults = response.Results.Count;
        return Task.FromResult(response);
    }

    public override Task<ItemResponse> GetItem(GetItemRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Issue {request.Id} not found"));

        var response = new ItemResponse
        {
            Source = "github",
            Id = issue.UniqueKey,
            Title = issue.Title,
            Content = request.IncludeContent ? (issue.Body ?? "") : "",
            Url = BuildIssueUrl(issue.UniqueKey),
            CreatedAt = Timestamp.FromDateTimeOffset(issue.CreatedAt),
            UpdatedAt = Timestamp.FromDateTimeOffset(issue.UpdatedAt),
        };

        response.Metadata.Add("state", issue.State);
        response.Metadata.Add("is_pull_request", issue.IsPullRequest.ToString());
        if (issue.Author is not null) response.Metadata.Add("author", issue.Author);
        if (issue.Labels is not null) response.Metadata.Add("labels", issue.Labels);
        if (issue.Assignees is not null) response.Metadata.Add("assignees", issue.Assignees);
        if (issue.Milestone is not null) response.Metadata.Add("milestone", issue.Milestone);
        if (issue.MergeState is not null) response.Metadata.Add("merge_state", issue.MergeState);

        if (request.IncludeComments)
        {
            var comments = GitHubCommentRecord.SelectList(connection,
                RepoFullName: issue.RepoFullName, IssueNumber: issue.Number);
            foreach (var c in comments)
            {
                response.Comments.Add(new Comment
                {
                    Id = c.Id.ToString(),
                    Author = c.Author,
                    Body = c.Body,
                    CreatedAt = Timestamp.FromDateTimeOffset(c.CreatedAt),
                });
            }
        }

        return Task.FromResult(response);
    }

    public override async Task ListItems(ListItemsRequest request, IServerStreamWriter<ItemSummary> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;
        var sortBy = !string.IsNullOrEmpty(request.SortBy) ? request.SortBy : "UpdatedAt";
        var sortOrder = request.SortOrder?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true ? "ASC" : "DESC";

        var sql = $"SELECT UniqueKey, Title, UpdatedAt, State, IsPullRequest FROM github_issues ORDER BY {sortBy} {sortOrder} LIMIT @limit OFFSET @offset";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var uniqueKey = reader.GetString(0);
            var summary = new ItemSummary
            {
                Id = uniqueKey,
                Title = reader.GetString(1),
                Url = BuildIssueUrl(uniqueKey),
                UpdatedAt = ParseTimestamp(reader, 2),
            };
            summary.Metadata.Add("state", reader.IsDBNull(3) ? "" : reader.GetString(3));
            summary.Metadata.Add("is_pull_request", reader.GetBoolean(4).ToString());

            await responseStream.WriteAsync(summary);
        }
    }

    public override Task<SearchResponse> GetRelated(GetRelatedRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 50) : 10;

        var response = new SearchResponse();

        // Find related items via Jira references sharing the same Jira keys
        var refs = GitHubJiraRefRecord.SelectList(connection, SourceId: request.Id);
        var relatedIds = new HashSet<string>();

        foreach (var jiraRef in refs)
        {
            var sameKeyRefs = GitHubJiraRefRecord.SelectList(connection, JiraKey: jiraRef.JiraKey);
            foreach (var r in sameKeyRefs)
            {
                if (r.SourceId != request.Id)
                    relatedIds.Add(r.SourceId);
            }
        }

        foreach (var relatedId in relatedIds.Take(limit))
        {
            var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: relatedId);
            if (issue is null) continue;

            response.Results.Add(new SearchResultItem
            {
                Source = "github",
                Id = issue.UniqueKey,
                Title = issue.Title,
                Url = BuildIssueUrl(issue.UniqueKey),
                UpdatedAt = Timestamp.FromDateTimeOffset(issue.UpdatedAt),
            });
        }

        response.TotalResults = response.Results.Count;
        return Task.FromResult(response);
    }

    public override Task<SnapshotResponse> GetSnapshot(GetSnapshotRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Issue {request.Id} not found"));

        var md = BuildMarkdownSnapshot(connection, issue, request.IncludeComments, request.IncludeInternalRefs);

        return Task.FromResult(new SnapshotResponse
        {
            Id = issue.UniqueKey,
            Source = "github",
            Markdown = md,
            Url = BuildIssueUrl(issue.UniqueKey),
        });
    }

    public override Task<ContentResponse> GetContent(GetContentRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Issue {request.Id} not found"));

        return Task.FromResult(new ContentResponse
        {
            Id = issue.UniqueKey,
            Source = "github",
            Content = issue.Body ?? "",
            Format = string.IsNullOrEmpty(request.Format) ? "markdown" : request.Format,
            Url = BuildIssueUrl(issue.UniqueKey),
        });
    }

    public override async Task StreamSearchableText(StreamTextRequest request, IServerStreamWriter<SearchableTextItem> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var sql = "SELECT UniqueKey, Title, Body, Labels, UpdatedAt FROM github_issues";
        var parameters = new List<SqliteParameter>();

        if (request.Since is not null)
        {
            sql += " WHERE UpdatedAt >= @since";
            parameters.Add(new SqliteParameter("@since", request.Since.ToDateTimeOffset().ToString("o")));
        }

        sql += " ORDER BY UpdatedAt ASC";

        using var cmd = new SqliteCommand(sql, connection);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var uniqueKey = reader.GetString(0);
            var item = new SearchableTextItem
            {
                Source = "github",
                Id = uniqueKey,
                Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                UpdatedAt = ParseTimestamp(reader, 4),
            };

            for (int i = 1; i <= 3; i++)
            {
                if (!reader.IsDBNull(i))
                    item.TextFields.Add(reader.GetString(i));
            }

            // Include comments
            var issueKey = uniqueKey;
            var parts = issueKey.Split('#');
            if (parts.Length == 2 && int.TryParse(parts[1], out var issueNumber))
            {
                var comments = GitHubCommentRecord.SelectList(connection,
                    RepoFullName: parts[0], IssueNumber: issueNumber);
                foreach (var c in comments)
                    item.TextFields.Add(c.Body);
            }

            await responseStream.WriteAsync(item);
        }
    }

    public override async Task<IngestionStatusResponse> TriggerIngestion(TriggerIngestionRequest request, ServerCallContext context)
    {
        var type = request.Type?.ToLowerInvariant() ?? "incremental";

        workQueue.Enqueue(ct => type switch
        {
            "full" => pipeline.RunFullIngestionAsync(request.Filter, ct),
            _ => pipeline.RunIncrementalIngestionAsync(ct),
        }, $"github-{type}");

        await Task.Delay(100, context.CancellationToken);
        return GetCurrentStatus();
    }

    public override Task<IngestionStatusResponse> GetIngestionStatus(IngestionStatusRequest request, ServerCallContext context)
    {
        return Task.FromResult(GetCurrentStatus());
    }

    public override async Task<RebuildResponse> RebuildFromCache(RebuildRequest request, ServerCallContext context)
    {
        return await FhirAugury.Common.Grpc.SourceServiceLifecycle.RebuildFromCacheAsync(
            async ct => (await pipeline.RebuildFromCacheAsync(ct)).ItemsProcessed,
            context.CancellationToken);
    }

    public override Task<StatsResponse> GetStats(StatsRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var issueCount = GitHubIssueRecord.SelectCount(connection);
        var commentCount = GitHubCommentRecord.SelectCount(connection);
        var dbSize = database.GetDatabaseSizeBytes();
        var cacheStats = cache.GetStats(GitHubCacheLayout.SourceName);

        var response = new StatsResponse
        {
            Source = "github",
            TotalItems = issueCount,
            TotalComments = commentCount,
            DatabaseSizeBytes = dbSize,
            CacheSizeBytes = cacheStats.TotalBytes,
        };

        var syncState = GitHubSyncStateRecord.SelectSingle(connection, SourceName: GitHubSource.SourceName);
        if (syncState is not null)
            response.LastSyncAt = Timestamp.FromDateTimeOffset(syncState.LastSyncAt);

        response.AdditionalCounts.Add("repos", GitHubRepoRecord.SelectCount(connection));
        response.AdditionalCounts.Add("commits", GitHubCommitRecord.SelectCount(connection));
        response.AdditionalCounts.Add("commit_files", GitHubCommitFileRecord.SelectCount(connection));
        response.AdditionalCounts.Add("jira_refs", GitHubJiraRefRecord.SelectCount(connection));
        response.AdditionalCounts.Add("spec_file_maps", GitHubSpecFileMapRecord.SelectCount(connection));

        return Task.FromResult(response);
    }

    public override Task<HealthCheckResponse> HealthCheck(HealthCheckRequest request, ServerCallContext context)
    {
        return Task.FromResult(FhirAugury.Common.Grpc.SourceServiceLifecycle.BuildHealthCheck(database, pipeline));
    }

    // ── Helpers ──────────────────────────────────────────────────

    private IngestionStatusResponse GetCurrentStatus()
    {
        using var connection = database.OpenConnection();
        var syncState = GitHubSyncStateRecord.SelectSingle(connection, SourceName: GitHubSource.SourceName);

        return new IngestionStatusResponse
        {
            Source = "github",
            Status = pipeline.IsRunning ? pipeline.CurrentStatus : (syncState?.Status ?? "unknown"),
            LastSyncAt = syncState is not null ? Timestamp.FromDateTimeOffset(syncState.LastSyncAt) : null,
            ItemsTotal = syncState?.ItemsIngested ?? 0,
            LastError = syncState?.LastError ?? "",
            SyncSchedule = _options.SyncSchedule,
        };
    }

    private static string BuildMarkdownSnapshot(
        SqliteConnection connection, GitHubIssueRecord issue, bool includeComments, bool includeRefs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {issue.UniqueKey}: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine($"**State:** {issue.State}  ");
        sb.AppendLine($"**Type:** {(issue.IsPullRequest ? "Pull Request" : "Issue")}  ");
        if (issue.Author is not null) sb.AppendLine($"**Author:** {issue.Author}  ");
        if (issue.Assignees is not null) sb.AppendLine($"**Assignees:** {issue.Assignees}  ");
        if (issue.Labels is not null) sb.AppendLine($"**Labels:** {issue.Labels}  ");
        if (issue.Milestone is not null) sb.AppendLine($"**Milestone:** {issue.Milestone}  ");
        if (issue.MergeState is not null) sb.AppendLine($"**Merge State:** {issue.MergeState}  ");
        if (issue.HeadBranch is not null) sb.AppendLine($"**Head Branch:** {issue.HeadBranch}  ");
        if (issue.BaseBranch is not null) sb.AppendLine($"**Base Branch:** {issue.BaseBranch}  ");
        sb.AppendLine($"**Created:** {issue.CreatedAt:yyyy-MM-dd}  ");
        sb.AppendLine($"**Updated:** {issue.UpdatedAt:yyyy-MM-dd}  ");
        if (issue.ClosedAt is not null) sb.AppendLine($"**Closed:** {issue.ClosedAt:yyyy-MM-dd}  ");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(issue.Body))
        {
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(issue.Body);
            sb.AppendLine();
        }

        if (includeComments)
        {
            var comments = GitHubCommentRecord.SelectList(connection,
                RepoFullName: issue.RepoFullName, IssueNumber: issue.Number);
            if (comments.Count > 0)
            {
                sb.AppendLine("## Comments");
                sb.AppendLine();
                foreach (var c in comments)
                {
                    sb.AppendLine($"### {c.Author} ({c.CreatedAt:yyyy-MM-dd})");
                    sb.AppendLine();
                    sb.AppendLine(c.Body);
                    sb.AppendLine();
                }
            }
        }

        if (includeRefs)
        {
            var jiraRefs = GitHubJiraRefRecord.SelectList(connection, SourceId: issue.UniqueKey);
            if (jiraRefs.Count > 0)
            {
                sb.AppendLine("## Jira References");
                sb.AppendLine();
                foreach (var r in jiraRefs)
                    sb.AppendLine($"- {r.JiraKey}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    internal static string BuildIssueUrl(string uniqueKey)
    {
        // UniqueKey format: "owner/repo#number"
        var hashIdx = uniqueKey.IndexOf('#');
        if (hashIdx < 0) return $"https://github.com/{uniqueKey}";
        var repo = uniqueKey[..hashIdx];
        var number = uniqueKey[(hashIdx + 1)..];
        return $"https://github.com/{repo}/issues/{number}";
    }

    private static Timestamp? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? Timestamp.FromDateTimeOffset(dt)
            : null;
    }

    internal static string SanitizeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
    }
}

/// <summary>
/// Implements GitHub-specific gRPC extensions from github.proto.
/// </summary>
#pragma warning disable CS9113 // pipeline and options used in later phases
public class GitHubSpecificGrpcService(
    GitHubDatabase database,
    GitHubIngestionPipeline pipeline,
    IOptions<GitHubServiceOptions> optionsAccessor,
    ArtifactFileMapper artifactMapper)
    : GitHubService.GitHubServiceBase
#pragma warning restore CS9113
{
    private readonly GitHubServiceOptions _options = optionsAccessor.Value;
    public override async Task GetIssueComments(GitHubGetCommentsRequest request, IServerStreamWriter<GitHubComment> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var comments = GitHubCommentRecord.SelectList(connection,
            RepoFullName: request.RepoFullName, IssueNumber: request.IssueNumber);

        foreach (var c in comments)
        {
            await responseStream.WriteAsync(new GitHubComment
            {
                Id = c.Id.ToString(),
                RepoFullName = c.RepoFullName,
                IssueNumber = c.IssueNumber,
                Author = c.Author,
                Body = c.Body,
                CreatedAt = Timestamp.FromDateTimeOffset(c.CreatedAt),
                Url = $"https://github.com/{c.RepoFullName}/issues/{c.IssueNumber}#issuecomment-{c.Id}",
            });
        }
    }

    public override Task<GitHubPullRequest> GetPullRequestDetails(GitHubGetPRRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var uniqueKey = $"{request.RepoFullName}#{request.Number}";
        var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: uniqueKey)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"PR {uniqueKey} not found"));

        if (!issue.IsPullRequest)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"{uniqueKey} is not a pull request"));

        return Task.FromResult(new GitHubPullRequest
        {
            Issue = MapToProtoIssue(issue),
            Merged = issue.MergeState == "merged",
            MergeCommitSha = "",
        });
    }

    public override async Task GetRelatedCommits(GitHubGetCommitsRequest request, IServerStreamWriter<GitHubCommit> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        // Find commits via PR links
        var prLinks = GitHubCommitPrLinkRecord.SelectList(connection,
            PrNumber: request.IssueNumber, RepoFullName: request.RepoFullName);

        var writtenShas = new HashSet<string>();

        foreach (var link in prLinks)
        {
            if (!writtenShas.Add(link.CommitSha)) continue;
            var commit = GitHubCommitRecord.SelectSingle(connection, Sha: link.CommitSha);
            if (commit is not null)
                await responseStream.WriteAsync(MapToProtoCommit(commit, connection));
        }

        // Also find commits mentioning this issue number in messages
        var issueRef = $"#{request.IssueNumber}";
        using var cmd = new SqliteCommand(
            "SELECT Sha FROM github_commits WHERE RepoFullName = @repo AND Message LIKE @pattern LIMIT 100",
            connection);
        cmd.Parameters.AddWithValue("@repo", request.RepoFullName);
        cmd.Parameters.AddWithValue("@pattern", $"%{issueRef}%");

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sha = reader.GetString(0);
            if (!writtenShas.Add(sha)) continue;
            var commit = GitHubCommitRecord.SelectSingle(connection, Sha: sha);
            if (commit is not null)
                await responseStream.WriteAsync(MapToProtoCommit(commit, connection));
        }
    }

    public override Task<GitHubPullRequest> GetPullRequestForCommit(GitHubGetPRForCommitRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var link = GitHubCommitPrLinkRecord.SelectSingle(connection, CommitSha: request.Sha);
        if (link is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"No PR found for commit {request.Sha}"));

        var uniqueKey = $"{link.RepoFullName}#{link.PrNumber}";
        var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: uniqueKey)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"PR {uniqueKey} not found"));

        return Task.FromResult(new GitHubPullRequest
        {
            Issue = MapToProtoIssue(issue),
            Merged = issue.MergeState == "merged",
        });
    }

    public override async Task GetCommitsForPullRequest(GitHubGetCommitsForPRRequest request, IServerStreamWriter<GitHubCommit> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var links = GitHubCommitPrLinkRecord.SelectList(connection,
            PrNumber: request.PrNumber, RepoFullName: request.RepoFullName);

        foreach (var link in links)
        {
            var commit = GitHubCommitRecord.SelectSingle(connection, Sha: link.CommitSha);
            if (commit is not null)
                await responseStream.WriteAsync(MapToProtoCommit(commit, connection));
        }
    }

    public override Task<SearchResponse> SearchCommits(SearchRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var ftsQuery = FtsQueryHelper.SanitizeFtsQuery(request.Query);

        if (string.IsNullOrEmpty(ftsQuery))
            return Task.FromResult(new SearchResponse { Query = request.Query });

        var limit = request.Limit > 0 ? Math.Min(request.Limit, 200) : 20;

        var sql = """
            SELECT gc.Sha, gc.Message, gc.Author, gc.Date, gc.Url,
                   github_commits_fts.rank
            FROM github_commits_fts
            JOIN github_commits gc ON gc.Id = github_commits_fts.rowid
            WHERE github_commits_fts MATCH @query
            ORDER BY github_commits_fts.rank
            LIMIT @limit OFFSET @offset
            """;

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        var response = new SearchResponse { Query = request.Query };
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            response.Results.Add(new SearchResultItem
            {
                Source = "github-commit",
                Id = reader.GetString(0),
                Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Score = -reader.GetDouble(5),
                Url = reader.IsDBNull(4) ? "" : reader.GetString(4),
            });
        }

        response.TotalResults = response.Results.Count;
        return Task.FromResult(response);
    }

    public override async Task GetJiraReferences(GitHubGetJiraRefsRequest request, IServerStreamWriter<GitHubJiraRef> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var sql = "SELECT SourceType, SourceId, RepoFullName, JiraKey, Context FROM github_jira_refs WHERE 1=1";
        var parameters = new List<SqliteParameter>();

        if (!string.IsNullOrEmpty(request.RepoFullName))
        {
            sql += " AND RepoFullName = @repo";
            parameters.Add(new SqliteParameter("@repo", request.RepoFullName));
        }

        if (!string.IsNullOrEmpty(request.JiraKeyFilter))
        {
            sql += " AND JiraKey = @jiraKey";
            parameters.Add(new SqliteParameter("@jiraKey", request.JiraKeyFilter));
        }

        sql += " ORDER BY JiraKey";

        using var cmd = new SqliteCommand(sql, connection);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            await responseStream.WriteAsync(new GitHubJiraRef
            {
                SourceType = reader.GetString(0),
                SourceId = reader.GetString(1),
                RepoFullName = reader.GetString(2),
                JiraKey = reader.GetString(3),
                Context = reader.IsDBNull(4) ? "" : reader.GetString(4),
            });
        }
    }

    public override async Task ListRepositories(GitHubListReposRequest request, IServerStreamWriter<GitHubRepo> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var repos = GitHubRepoRecord.SelectList(connection);

        foreach (var repo in repos)
        {
            // Count issues and PRs
            int issueCount = 0, prCount = 0;
            using (var cmd = new SqliteCommand(
                "SELECT IsPullRequest, COUNT(*) FROM github_issues WHERE RepoFullName = @repo GROUP BY IsPullRequest",
                connection))
            {
                cmd.Parameters.AddWithValue("@repo", repo.FullName);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetBoolean(0))
                        prCount = reader.GetInt32(1);
                    else
                        issueCount = reader.GetInt32(1);
                }
            }

            await responseStream.WriteAsync(new GitHubRepo
            {
                FullName = repo.FullName,
                Description = repo.Description ?? "",
                IssueCount = issueCount,
                PrCount = prCount,
                Url = $"https://github.com/{repo.FullName}",
                HasIssues = repo.HasIssues,
            });
        }
    }

    public override async Task ListByLabel(GitHubLabelRequest request, IServerStreamWriter<GitHubIssueSummary> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

        var sql = "SELECT UniqueKey, RepoFullName, Number, IsPullRequest, Title, State, UpdatedAt FROM github_issues WHERE Labels LIKE @label";
        if (!string.IsNullOrEmpty(request.RepoFullName))
            sql += " AND RepoFullName = @repo";
        sql += " ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@label", $"%{request.Label}%");
        if (!string.IsNullOrEmpty(request.RepoFullName))
            cmd.Parameters.AddWithValue("@repo", request.RepoFullName);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            await responseStream.WriteAsync(ReadIssueSummary(reader));
        }
    }

    public override async Task ListByMilestone(GitHubMilestoneRequest request, IServerStreamWriter<GitHubIssueSummary> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

        var sql = "SELECT UniqueKey, RepoFullName, Number, IsPullRequest, Title, State, UpdatedAt FROM github_issues WHERE Milestone = @milestone";
        if (!string.IsNullOrEmpty(request.RepoFullName))
            sql += " AND RepoFullName = @repo";
        sql += " ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@milestone", request.Milestone);
        if (!string.IsNullOrEmpty(request.RepoFullName))
            cmd.Parameters.AddWithValue("@repo", request.RepoFullName);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            await responseStream.WriteAsync(ReadIssueSummary(reader));
        }
    }

    public override async Task QueryByArtifact(GitHubArtifactQueryRequest request, IServerStreamWriter<GitHubCommit> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var repoFullName = !string.IsNullOrEmpty(request.Repo) ? request.Repo : "HL7/fhir";
        var filePaths = artifactMapper.ResolveFilePaths(
            connection, repoFullName,
            request.ArtifactKey, request.ArtifactId, request.PageKey, request.ElementPath);

        if (filePaths.Count == 0) return;

        // Build SQL to find commits that changed any of the resolved file paths
        var placeholders = string.Join(",", filePaths.Select((_, i) => $"@path{i}"));
        var sql = $"""
            SELECT DISTINCT gc.Sha, gc.Message, gc.Author, gc.Date, gc.Url, gc.RepoFullName
            FROM github_commit_files gcf
            JOIN github_commits gc ON gc.Sha = gcf.CommitSha
            WHERE gcf.FilePath IN ({placeholders})
            AND gc.RepoFullName = @repo
            """;

        if (request.After is not null)
            sql += " AND gc.Date >= @after";
        if (request.Before is not null)
            sql += " AND gc.Date <= @before";

        sql += " ORDER BY gc.Date DESC";

        var limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;
        sql += " LIMIT @limit";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@repo", repoFullName);
        for (int i = 0; i < filePaths.Count; i++)
            cmd.Parameters.AddWithValue($"@path{i}", filePaths[i]);
        if (request.After is not null)
            cmd.Parameters.AddWithValue("@after", request.After.ToDateTimeOffset().ToString("o"));
        if (request.Before is not null)
            cmd.Parameters.AddWithValue("@before", request.Before.ToDateTimeOffset().ToString("o"));
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sha = reader.GetString(0);
            var protoCommit = new GitHubCommit
            {
                Sha = sha,
                Message = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Author = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Date = ParseTimestampDirect(reader, 3),
                Url = reader.IsDBNull(4) ? "" : reader.GetString(4),
            };

            // Include changed files for this commit
            var files = GitHubCommitFileRecord.SelectList(connection, CommitSha: sha);
            foreach (var f in files)
                protoCommit.ChangedFiles.Add(f.FilePath);

            // Optionally include PRs
            if (request.IncludePrs)
            {
                var prLinks = GitHubCommitPrLinkRecord.SelectList(connection, CommitSha: sha);
                foreach (var link in prLinks)
                    protoCommit.ChangedFiles.Add($"PR#{link.PrNumber}");
            }

            await responseStream.WriteAsync(protoCommit);
        }
    }

    public override Task<SnapshotResponse> GetIssueSnapshot(GitHubSnapshotRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var uniqueKey = $"{request.RepoFullName}#{request.Number}";
        var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: uniqueKey)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Issue {uniqueKey} not found"));

        var sb = new StringBuilder();
        sb.AppendLine($"# {issue.UniqueKey}: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine($"| Field | Value |");
        sb.AppendLine($"|-------|-------|");
        sb.AppendLine($"| State | {issue.State} |");
        sb.AppendLine($"| Type | {(issue.IsPullRequest ? "Pull Request" : "Issue")} |");
        if (issue.Author is not null) sb.AppendLine($"| Author | {issue.Author} |");
        if (issue.Assignees is not null) sb.AppendLine($"| Assignees | {issue.Assignees} |");
        if (issue.Labels is not null) sb.AppendLine($"| Labels | {issue.Labels} |");
        if (issue.Milestone is not null) sb.AppendLine($"| Milestone | {issue.Milestone} |");
        if (issue.MergeState is not null) sb.AppendLine($"| Merge State | {issue.MergeState} |");
        sb.AppendLine($"| Created | {issue.CreatedAt:yyyy-MM-dd} |");
        sb.AppendLine($"| Updated | {issue.UpdatedAt:yyyy-MM-dd} |");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(issue.Body))
        {
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(issue.Body);
            sb.AppendLine();
        }

        if (request.IncludeComments)
        {
            var comments = GitHubCommentRecord.SelectList(connection,
                RepoFullName: issue.RepoFullName, IssueNumber: issue.Number);
            if (comments.Count > 0)
            {
                sb.AppendLine("## Comments");
                sb.AppendLine();
                foreach (var c in comments)
                {
                    sb.AppendLine($"**{c.Author}** ({c.CreatedAt:yyyy-MM-dd}):");
                    sb.AppendLine(c.Body);
                    sb.AppendLine();
                }
            }
        }

        if (request.IncludeInternalRefs)
        {
            var jiraRefs = GitHubJiraRefRecord.SelectList(connection, SourceId: uniqueKey);
            if (jiraRefs.Count > 0)
            {
                sb.AppendLine("## Jira References");
                sb.AppendLine();
                foreach (var r in jiraRefs) sb.AppendLine($"- {r.JiraKey}");
            }
        }

        return Task.FromResult(new SnapshotResponse
        {
            Id = uniqueKey,
            Source = "github",
            Markdown = sb.ToString(),
            Url = GitHubGrpcService.BuildIssueUrl(uniqueKey),
        });
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static GitHubIssue MapToProtoIssue(GitHubIssueRecord issue)
    {
        var protoIssue = new GitHubIssue
        {
            Id = issue.Id,
            RepoFullName = issue.RepoFullName,
            Number = issue.Number,
            IsPullRequest = issue.IsPullRequest,
            Title = issue.Title,
            Body = issue.Body ?? "",
            State = issue.State,
            Author = issue.Author ?? "",
            Labels = issue.Labels ?? "",
            Assignees = issue.Assignees ?? "",
            Milestone = issue.Milestone ?? "",
            CreatedAt = Timestamp.FromDateTimeOffset(issue.CreatedAt),
            UpdatedAt = Timestamp.FromDateTimeOffset(issue.UpdatedAt),
            Url = GitHubGrpcService.BuildIssueUrl(issue.UniqueKey),
            MergeState = issue.MergeState ?? "",
            HeadBranch = issue.HeadBranch ?? "",
            BaseBranch = issue.BaseBranch ?? "",
        };

        if (issue.ClosedAt is not null)
            protoIssue.ClosedAt = Timestamp.FromDateTimeOffset(issue.ClosedAt.Value);

        return protoIssue;
    }

    private static GitHubCommit MapToProtoCommit(GitHubCommitRecord commit, SqliteConnection connection)
    {
        var protoCommit = new GitHubCommit
        {
            Sha = commit.Sha,
            Message = commit.Message,
            Author = commit.Author,
            Date = Timestamp.FromDateTimeOffset(commit.Date),
            Url = commit.Url,
        };

        var files = GitHubCommitFileRecord.SelectList(connection, CommitSha: commit.Sha);
        foreach (var f in files)
            protoCommit.ChangedFiles.Add(f.FilePath);

        return protoCommit;
    }

    private static GitHubIssueSummary ReadIssueSummary(SqliteDataReader reader)
    {
        var uniqueKey = reader.GetString(0);
        var summary = new GitHubIssueSummary
        {
            RepoFullName = reader.IsDBNull(1) ? "" : reader.GetString(1),
            Number = reader.GetInt32(2),
            IsPullRequest = reader.GetBoolean(3),
            Title = reader.IsDBNull(4) ? "" : reader.GetString(4),
            State = reader.IsDBNull(5) ? "" : reader.GetString(5),
            Url = GitHubGrpcService.BuildIssueUrl(uniqueKey),
        };

        if (!reader.IsDBNull(6) &&
            DateTimeOffset.TryParse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated))
        {
            summary.UpdatedAt = Timestamp.FromDateTimeOffset(updated);
        }

        return summary;
    }

    private static Timestamp? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? Timestamp.FromDateTimeOffset(dt)
            : null;
    }

    private static Timestamp ParseTimestampDirect(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return Timestamp.FromDateTimeOffset(DateTimeOffset.MinValue);
        var str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? Timestamp.FromDateTimeOffset(dt)
            : Timestamp.FromDateTimeOffset(DateTimeOffset.MinValue);
    }
}
