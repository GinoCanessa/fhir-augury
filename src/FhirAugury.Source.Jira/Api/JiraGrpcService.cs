using System.Globalization;
using System.Text;
using Fhiraugury;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Indexing;
using FhirAugury.Source.Jira.Ingestion;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Api;

/// <summary>
/// Implements both SourceService and JiraService gRPC contracts.
/// </summary>
public class JiraGrpcService(
    JiraDatabase database,
    JiraIngestionPipeline pipeline,
    IResponseCache cache,
    JiraServiceOptions options)
    : SourceService.SourceServiceBase
{
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    // ── SourceService RPCs ────────────────────────────────────────

    public override Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var ftsQuery = SanitizeFtsQuery(request.Query);

        if (string.IsNullOrEmpty(ftsQuery))
            return Task.FromResult(new SearchResponse { Query = request.Query });

        var limit = request.Limit > 0 ? Math.Min(request.Limit, 200) : 20;

        var sql = """
            SELECT ji.Key, ji.Title,
                   snippet(jira_issues_fts, 1, '<b>', '</b>', '...', 20) as Snippet,
                   jira_issues_fts.rank,
                   ji.Status, ji.UpdatedAt
            FROM jira_issues_fts
            JOIN jira_issues ji ON ji.Id = jira_issues_fts.rowid
            WHERE jira_issues_fts MATCH @query
            ORDER BY jira_issues_fts.rank
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
            var key = reader.GetString(0);
            response.Results.Add(new SearchResultItem
            {
                Source = "jira",
                Id = key,
                Title = reader.GetString(1),
                Snippet = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Score = -reader.GetDouble(3),
                Url = $"{options.BaseUrl}/browse/{key}",
                UpdatedAt = ParseTimestamp(reader, 5),
            });
        }

        response.TotalResults = response.Results.Count;
        return Task.FromResult(response);
    }

    public override Task<ItemResponse> GetItem(GetItemRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var issue = JiraIssueRecord.SelectSingle(connection, Key: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Issue {request.Id} not found"));

        var response = new ItemResponse
        {
            Source = "jira",
            Id = issue.Key,
            Title = issue.Title,
            Content = request.IncludeContent ? (issue.Description ?? "") : "",
            Url = $"{options.BaseUrl}/browse/{issue.Key}",
            CreatedAt = Timestamp.FromDateTimeOffset(issue.CreatedAt),
            UpdatedAt = Timestamp.FromDateTimeOffset(issue.UpdatedAt),
        };

        response.Metadata.Add("status", issue.Status);
        response.Metadata.Add("type", issue.Type);
        response.Metadata.Add("priority", issue.Priority);
        if (issue.WorkGroup is not null) response.Metadata.Add("work_group", issue.WorkGroup);
        if (issue.Specification is not null) response.Metadata.Add("specification", issue.Specification);
        if (issue.Resolution is not null) response.Metadata.Add("resolution", issue.Resolution);
        if (issue.Assignee is not null) response.Metadata.Add("assignee", issue.Assignee);
        if (issue.Reporter is not null) response.Metadata.Add("reporter", issue.Reporter);

        if (request.IncludeComments)
        {
            var comments = JiraCommentRecord.SelectList(connection, IssueKey: issue.Key);
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

        var sql = $"SELECT Key, Title, UpdatedAt, Status, Type, WorkGroup FROM jira_issues ORDER BY {sortBy} {sortOrder} LIMIT @limit OFFSET @offset";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var summary = new ItemSummary
            {
                Id = key,
                Title = reader.GetString(1),
                Url = $"{options.BaseUrl}/browse/{key}",
                UpdatedAt = ParseTimestamp(reader, 2),
            };
            summary.Metadata.Add("status", reader.IsDBNull(3) ? "" : reader.GetString(3));
            summary.Metadata.Add("type", reader.IsDBNull(4) ? "" : reader.GetString(4));

            await responseStream.WriteAsync(summary);
        }
    }

    public override Task<SearchResponse> GetRelated(GetRelatedRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 50) : 10;

        var response = new SearchResponse();

        // Get links
        var links = JiraIssueLinkRecord.SelectList(connection, SourceKey: request.Id);
        var targetLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: request.Id);

        var relatedKeys = links.Select(l => l.TargetKey)
            .Concat(targetLinks.Select(l => l.SourceKey))
            .Distinct()
            .Take(limit)
            .ToList();

        foreach (var relKey in relatedKeys)
        {
            var issue = JiraIssueRecord.SelectSingle(connection, Key: relKey);
            if (issue is null) continue;

            response.Results.Add(new SearchResultItem
            {
                Source = "jira",
                Id = issue.Key,
                Title = issue.Title,
                Url = $"{options.BaseUrl}/browse/{issue.Key}",
                UpdatedAt = Timestamp.FromDateTimeOffset(issue.UpdatedAt),
            });
        }

        response.TotalResults = response.Results.Count;
        return Task.FromResult(response);
    }

    public override Task<SnapshotResponse> GetSnapshot(GetSnapshotRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var issue = JiraIssueRecord.SelectSingle(connection, Key: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Issue {request.Id} not found"));

        var md = BuildMarkdownSnapshot(connection, issue, request.IncludeComments, request.IncludeInternalRefs);

        return Task.FromResult(new SnapshotResponse
        {
            Id = issue.Key,
            Source = "jira",
            Markdown = md,
            Url = $"{options.BaseUrl}/browse/{issue.Key}",
        });
    }

    public override Task<ContentResponse> GetContent(GetContentRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var issue = JiraIssueRecord.SelectSingle(connection, Key: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Issue {request.Id} not found"));

        return Task.FromResult(new ContentResponse
        {
            Id = issue.Key,
            Source = "jira",
            Content = issue.Description ?? "",
            Format = string.IsNullOrEmpty(request.Format) ? "text" : request.Format,
            Url = $"{options.BaseUrl}/browse/{issue.Key}",
        });
    }

    public override async Task StreamSearchableText(StreamTextRequest request, IServerStreamWriter<SearchableTextItem> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var sql = "SELECT Key, Title, Description, Labels, WorkGroup, Specification, UpdatedAt FROM jira_issues";
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
            var key = reader.GetString(0);
            var item = new SearchableTextItem
            {
                Source = "jira",
                Id = key,
                Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                UpdatedAt = ParseTimestamp(reader, 6),
            };

            for (int i = 1; i <= 5; i++)
            {
                if (!reader.IsDBNull(i))
                    item.TextFields.Add(reader.GetString(i));
            }

            // Include comments
            var comments = JiraCommentRecord.SelectList(connection, IssueKey: key);
            foreach (var c in comments)
                item.TextFields.Add(c.Body);

            await responseStream.WriteAsync(item);
        }
    }

    public override async Task<IngestionStatusResponse> TriggerIngestion(TriggerIngestionRequest request, ServerCallContext context)
    {
        var type = request.Type?.ToLowerInvariant() ?? "incremental";

        _ = type switch
        {
            "full" => Task.Run(() => pipeline.RunFullIngestionAsync(request.Filter, context.CancellationToken)),
            _ => Task.Run(() => pipeline.RunIncrementalIngestionAsync(context.CancellationToken)),
        };

        // Wait briefly then return status
        await Task.Delay(100, context.CancellationToken);
        return GetCurrentStatus();
    }

    public override Task<IngestionStatusResponse> GetIngestionStatus(IngestionStatusRequest request, ServerCallContext context)
    {
        return Task.FromResult(GetCurrentStatus());
    }

    public override async Task<RebuildResponse> RebuildFromCache(RebuildRequest request, ServerCallContext context)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await pipeline.RebuildFromCacheAsync(context.CancellationToken);
            return new RebuildResponse
            {
                Success = true,
                ItemsLoaded = result.ItemsProcessed,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
        catch (Exception ex)
        {
            return new RebuildResponse
            {
                Success = false,
                Error = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
    }

    public override Task<StatsResponse> GetStats(StatsRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var issueCount = JiraIssueRecord.SelectCount(connection);
        var commentCount = JiraCommentRecord.SelectCount(connection);
        var dbSize = database.GetDatabaseSizeBytes();
        var cacheStats = cache.GetStats(JiraCacheLayout.SourceName);

        var response = new StatsResponse
        {
            Source = "jira",
            TotalItems = issueCount,
            TotalComments = commentCount,
            DatabaseSizeBytes = dbSize,
            CacheSizeBytes = cacheStats.TotalBytes,
        };

        // Sync state
        var syncState = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);
        if (syncState is not null)
            response.LastSyncAt = Timestamp.FromDateTimeOffset(syncState.LastSyncAt);

        response.AdditionalCounts.Add("issue_links", JiraIssueLinkRecord.SelectCount(connection));
        response.AdditionalCounts.Add("spec_artifacts", JiraSpecArtifactRecord.SelectCount(connection));

        return Task.FromResult(response);
    }

    public override Task<HealthCheckResponse> HealthCheck(HealthCheckRequest request, ServerCallContext context)
    {
        var integrity = database.CheckIntegrity();
        return Task.FromResult(new HealthCheckResponse
        {
            Status = integrity == "ok" ? "healthy" : "degraded",
            Version = "2.0.0",
            UptimeSeconds = (DateTimeOffset.UtcNow - StartTime).TotalSeconds,
            Message = pipeline.IsRunning ? $"Ingestion in progress: {pipeline.CurrentStatus}" : "OK",
        });
    }

    // ── Helpers ──────────────────────────────────────────────────

    private IngestionStatusResponse GetCurrentStatus()
    {
        using var connection = database.OpenConnection();
        var syncState = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);

        return new IngestionStatusResponse
        {
            Source = "jira",
            Status = pipeline.IsRunning ? pipeline.CurrentStatus : (syncState?.Status ?? "unknown"),
            LastSyncAt = syncState is not null ? Timestamp.FromDateTimeOffset(syncState.LastSyncAt) : null,
            ItemsTotal = syncState?.ItemsIngested ?? 0,
            LastError = syncState?.LastError ?? "",
            SyncSchedule = options.SyncSchedule,
        };
    }

    private static string BuildMarkdownSnapshot(
        SqliteConnection connection, JiraIssueRecord issue, bool includeComments, bool includeRefs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {issue.Key}: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Status:** {issue.Status}  ");
        sb.AppendLine($"**Type:** {issue.Type}  ");
        sb.AppendLine($"**Priority:** {issue.Priority}  ");
        if (issue.Resolution is not null) sb.AppendLine($"**Resolution:** {issue.Resolution}  ");
        if (issue.Assignee is not null) sb.AppendLine($"**Assignee:** {issue.Assignee}  ");
        if (issue.Reporter is not null) sb.AppendLine($"**Reporter:** {issue.Reporter}  ");
        if (issue.WorkGroup is not null) sb.AppendLine($"**Work Group:** {issue.WorkGroup}  ");
        if (issue.Specification is not null) sb.AppendLine($"**Specification:** {issue.Specification}  ");
        if (issue.Labels is not null) sb.AppendLine($"**Labels:** {issue.Labels}  ");
        sb.AppendLine($"**Created:** {issue.CreatedAt:yyyy-MM-dd}  ");
        sb.AppendLine($"**Updated:** {issue.UpdatedAt:yyyy-MM-dd}  ");
        if (issue.ResolvedAt is not null) sb.AppendLine($"**Resolved:** {issue.ResolvedAt:yyyy-MM-dd}  ");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(issue.Description))
        {
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(issue.Description);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(issue.ResolutionDescription))
        {
            sb.AppendLine("## Resolution Description");
            sb.AppendLine();
            sb.AppendLine(issue.ResolutionDescription);
            sb.AppendLine();
        }

        if (includeComments)
        {
            var comments = JiraCommentRecord.SelectList(connection, IssueKey: issue.Key);
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
            var links = JiraIssueLinkRecord.SelectList(connection, SourceKey: issue.Key);
            var targetLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: issue.Key);

            if (links.Count > 0 || targetLinks.Count > 0)
            {
                sb.AppendLine("## Related Issues");
                sb.AppendLine();
                foreach (var l in links)
                    sb.AppendLine($"- **{l.LinkType}** → {l.TargetKey}");
                foreach (var l in targetLinks)
                    sb.AppendLine($"- **{l.LinkType}** ← {l.SourceKey}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static Timestamp? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? Timestamp.FromDateTimeOffset(dt)
            : null;
    }

    private static string SanitizeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
    }
}

/// <summary>
/// Implements Jira-specific gRPC extensions from jira.proto.
/// </summary>
#pragma warning disable CS9113 // pipeline used in later phases for snapshot generation
public class JiraSpecificGrpcService(
    JiraDatabase database,
    JiraIngestionPipeline pipeline,
    JiraServiceOptions options)
    : JiraService.JiraServiceBase
#pragma warning restore CS9113
{
    public override async Task GetIssueComments(JiraGetCommentsRequest request, IServerStreamWriter<Fhiraugury.JiraComment> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var comments = JiraCommentRecord.SelectList(connection, IssueKey: request.IssueKey);

        foreach (var c in comments)
        {
            await responseStream.WriteAsync(new Fhiraugury.JiraComment
            {
                Id = c.Id.ToString(),
                IssueKey = c.IssueKey,
                Author = c.Author,
                Body = c.Body,
                CreatedAt = Timestamp.FromDateTimeOffset(c.CreatedAt),
            });
        }
    }

    public override Task<JiraIssueLinksResponse> GetIssueLinks(JiraGetLinksRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var outLinks = JiraIssueLinkRecord.SelectList(connection, SourceKey: request.IssueKey);
        var inLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: request.IssueKey);

        var response = new JiraIssueLinksResponse();
        foreach (var l in outLinks)
        {
            response.Links.Add(new Fhiraugury.JiraIssueLink
            {
                SourceKey = l.SourceKey,
                TargetKey = l.TargetKey,
                LinkType = l.LinkType,
            });
        }
        foreach (var l in inLinks)
        {
            response.Links.Add(new Fhiraugury.JiraIssueLink
            {
                SourceKey = l.SourceKey,
                TargetKey = l.TargetKey,
                LinkType = l.LinkType,
            });
        }

        return Task.FromResult(response);
    }

    public override async Task ListByWorkGroup(JiraWorkGroupRequest request, IServerStreamWriter<JiraIssueSummary> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

        using var cmd = new SqliteCommand(
            "SELECT Key, ProjectKey, Title, Type, Status, Priority, WorkGroup, Specification, UpdatedAt FROM jira_issues WHERE WorkGroup = @wg ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
            connection);
        cmd.Parameters.AddWithValue("@wg", request.WorkGroup);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            await responseStream.WriteAsync(ReadIssueSummary(reader));
    }

    public override async Task ListBySpecification(JiraSpecificationRequest request, IServerStreamWriter<JiraIssueSummary> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

        using var cmd = new SqliteCommand(
            "SELECT Key, ProjectKey, Title, Type, Status, Priority, WorkGroup, Specification, UpdatedAt FROM jira_issues WHERE Specification = @spec ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
            connection);
        cmd.Parameters.AddWithValue("@spec", request.Specification);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            await responseStream.WriteAsync(ReadIssueSummary(reader));
    }

    public override async Task QueryIssues(JiraQueryRequest request, IServerStreamWriter<JiraIssueSummary> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var (sql, parameters) = JiraQueryBuilder.Build(request);

        using var cmd = new SqliteCommand(sql, connection);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader["Key"]?.ToString() ?? "";
            var summary = new JiraIssueSummary
            {
                Key = key,
                ProjectKey = reader["ProjectKey"]?.ToString() ?? "",
                Title = reader["Title"]?.ToString() ?? "",
                Type = reader["Type"]?.ToString() ?? "",
                Status = reader["Status"]?.ToString() ?? "",
                Priority = reader["Priority"]?.ToString() ?? "",
                WorkGroup = reader["WorkGroup"]?.ToString() ?? "",
                Specification = reader["Specification"]?.ToString() ?? "",
                Url = $"{options.BaseUrl}/browse/{key}",
            };

            if (reader["UpdatedAt"] is string updatedStr &&
                DateTimeOffset.TryParse(updatedStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated))
            {
                summary.UpdatedAt = Timestamp.FromDateTimeOffset(updated);
            }

            await responseStream.WriteAsync(summary);
        }
    }

    public override async Task ListSpecArtifacts(JiraListSpecArtifactsRequest request, IServerStreamWriter<SpecArtifactEntry> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var sql = "SELECT Family, SpecKey, SpecName, GitUrl, PublishedUrl, DefaultWorkgroup FROM jira_spec_artifacts";
        if (!string.IsNullOrEmpty(request.FamilyFilter))
            sql += " WHERE Family = @family";
        sql += " ORDER BY Family, SpecKey";

        using var cmd = new SqliteCommand(sql, connection);
        if (!string.IsNullOrEmpty(request.FamilyFilter))
            cmd.Parameters.AddWithValue("@family", request.FamilyFilter);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            await responseStream.WriteAsync(new SpecArtifactEntry
            {
                Family = reader.GetString(0),
                SpecKey = reader.GetString(1),
                SpecName = reader.GetString(2),
                GitUrl = reader.IsDBNull(3) ? "" : reader.GetString(3),
                PublishedUrl = reader.IsDBNull(4) ? "" : reader.GetString(4),
                DefaultWorkgroup = reader.IsDBNull(5) ? "" : reader.GetString(5),
            });
        }
    }

    public override Task<JiraIssueNumbersResponse> GetIssueNumbers(JiraGetIssueNumbersRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var sql = "SELECT Key FROM jira_issues";
        if (!string.IsNullOrEmpty(request.ProjectFilter))
            sql += " WHERE ProjectKey = @project";

        using var cmd = new SqliteCommand(sql, connection);
        if (!string.IsNullOrEmpty(request.ProjectFilter))
            cmd.Parameters.AddWithValue("@project", request.ProjectFilter);

        var response = new JiraIssueNumbersResponse();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var dashIndex = key.LastIndexOf('-');
            if (dashIndex >= 0 && int.TryParse(key.AsSpan(dashIndex + 1), out var number))
                response.IssueNumbers.Add(number);
        }

        return Task.FromResult(response);
    }

    public override Task<SnapshotResponse> GetIssueSnapshot(JiraSnapshotRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var issue = JiraIssueRecord.SelectSingle(connection, Key: request.Key)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Issue {request.Key} not found"));

        var sb = new StringBuilder();
        sb.AppendLine($"# {issue.Key}: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine($"| Field | Value |");
        sb.AppendLine($"|-------|-------|");
        sb.AppendLine($"| Status | {issue.Status} |");
        sb.AppendLine($"| Type | {issue.Type} |");
        sb.AppendLine($"| Priority | {issue.Priority} |");
        if (issue.Resolution is not null) sb.AppendLine($"| Resolution | {issue.Resolution} |");
        if (issue.Assignee is not null) sb.AppendLine($"| Assignee | {issue.Assignee} |");
        if (issue.Reporter is not null) sb.AppendLine($"| Reporter | {issue.Reporter} |");
        if (issue.WorkGroup is not null) sb.AppendLine($"| Work Group | {issue.WorkGroup} |");
        if (issue.Specification is not null) sb.AppendLine($"| Specification | {issue.Specification} |");
        if (issue.Labels is not null) sb.AppendLine($"| Labels | {issue.Labels} |");
        sb.AppendLine($"| Created | {issue.CreatedAt:yyyy-MM-dd} |");
        sb.AppendLine($"| Updated | {issue.UpdatedAt:yyyy-MM-dd} |");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(issue.Description))
        {
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(issue.Description);
            sb.AppendLine();
        }

        if (request.IncludeComments)
        {
            var comments = JiraCommentRecord.SelectList(connection, IssueKey: issue.Key);
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
            var outLinks = JiraIssueLinkRecord.SelectList(connection, SourceKey: issue.Key);
            var inLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: issue.Key);
            if (outLinks.Count > 0 || inLinks.Count > 0)
            {
                sb.AppendLine("## Related Issues");
                sb.AppendLine();
                foreach (var l in outLinks) sb.AppendLine($"- {l.LinkType} → {l.TargetKey}");
                foreach (var l in inLinks) sb.AppendLine($"- {l.LinkType} ← {l.SourceKey}");
            }
        }

        return Task.FromResult(new SnapshotResponse
        {
            Id = issue.Key,
            Source = "jira",
            Markdown = sb.ToString(),
            Url = $"{options.BaseUrl}/browse/{issue.Key}",
        });
    }

    private JiraIssueSummary ReadIssueSummary(SqliteDataReader reader)
    {
        var key = reader.GetString(0);
        var summary = new JiraIssueSummary
        {
            Key = key,
            ProjectKey = reader.IsDBNull(1) ? "" : reader.GetString(1),
            Title = reader.IsDBNull(2) ? "" : reader.GetString(2),
            Type = reader.IsDBNull(3) ? "" : reader.GetString(3),
            Status = reader.IsDBNull(4) ? "" : reader.GetString(4),
            Priority = reader.IsDBNull(5) ? "" : reader.GetString(5),
            WorkGroup = reader.IsDBNull(6) ? "" : reader.GetString(6),
            Specification = reader.IsDBNull(7) ? "" : reader.GetString(7),
            Url = $"{options.BaseUrl}/browse/{key}",
        };

        if (!reader.IsDBNull(8) &&
            DateTimeOffset.TryParse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated))
        {
            summary.UpdatedAt = Timestamp.FromDateTimeOffset(updated);
        }

        return summary;
    }
}
