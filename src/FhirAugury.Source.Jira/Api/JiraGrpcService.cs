using System.Globalization;
using System.Text;
using Fhiraugury;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Indexing;
using FhirAugury.Source.Jira.Ingestion;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Api;

/// <summary>
/// Implements both SourceService and JiraService gRPC contracts.
/// </summary>
public class JiraGrpcService(
    JiraDatabase database,
    JiraIngestionPipeline pipeline,
    IResponseCache cache,
    FhirAugury.Common.Ingestion.IngestionWorkQueue workQueue,
    JiraXRefRebuilder xrefRebuilder,
    JiraIndexer indexer,
    JiraIndexBuilder indexBuilder,
    IOptions<JiraServiceOptions> optionsAccessor)
    : SourceService.SourceServiceBase
{
    private readonly JiraServiceOptions options = optionsAccessor.Value;
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    // ── SourceService RPCs ────────────────────────────────────────

    public override Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(request.Query);

        if (string.IsNullOrEmpty(ftsQuery))
            return Task.FromResult(new SearchResponse { Query = request.Query });

        int limit = request.Limit > 0 ? Math.Min(request.Limit, 200) : 20;

        string sql = """
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

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        SearchResponse response = new SearchResponse { Query = request.Query };
        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            string key = reader.GetString(0);
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
        using SqliteConnection connection = database.OpenConnection();
        JiraIssueRecord issue = JiraIssueRecord.SelectSingle(connection, Key: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Issue {request.Id} not found"));

        ItemResponse response = new ItemResponse
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
            List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection, IssueKey: issue.Key);
            foreach (JiraCommentRecord c in comments)
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
        using SqliteConnection connection = database.OpenConnection();
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;
        string sortBy = !string.IsNullOrEmpty(request.SortBy) ? request.SortBy : "UpdatedAt";
        string sortOrder = request.SortOrder?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true ? "ASC" : "DESC";

        string sql = $"SELECT Key, Title, UpdatedAt, Status, Type, WorkGroup FROM jira_issues ORDER BY {sortBy} {sortOrder} LIMIT @limit OFFSET @offset";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            ItemSummary summary = new ItemSummary
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
        if (!string.IsNullOrEmpty(request.SeedSource) && request.SeedSource != "jira")
            return GetCrossSourceRelated(request, context);

        using SqliteConnection connection = database.OpenConnection();
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 50) : 10;

        SearchResponse response = new SearchResponse();

        // Get links
        List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, SourceKey: request.Id);
        List<JiraIssueLinkRecord> targetLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: request.Id);

        List<string> relatedKeys = links.Select(l => l.TargetKey)
            .Concat(targetLinks.Select(l => l.SourceKey))
            .Distinct()
            .Take(limit)
            .ToList();

        foreach (string? relKey in relatedKeys)
        {
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: relKey);
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

    private async Task<SearchResponse> GetCrossSourceRelated(GetRelatedRequest request, ServerCallContext context)
    {
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 50) : 10;

        if (request.SeedSource == "zulip")
        {
            using SqliteConnection connection = database.OpenConnection();

            // SeedId for Zulip is "streamId:topicName"
            string[] parts = request.SeedId.Split(':', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out int streamId))
            {
                string topicName = parts[1];
                List<ZulipXRefRecord> refs = ZulipXRefRecord.SelectList(connection,
                    StreamId: streamId, TopicName: topicName);

                HashSet<string> seen = [];
                SearchResponse response = new SearchResponse();

                foreach (ZulipXRefRecord zRef in refs)
                {
                    if (!seen.Add(zRef.SourceId)) continue;

                    JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: zRef.SourceId);
                    if (issue is null) continue;

                    response.Results.Add(new SearchResultItem
                    {
                        Source = "jira",
                        Id = issue.Key,
                        Title = issue.Title,
                        Score = 1.0,
                        Url = $"{options.BaseUrl}/browse/{issue.Key}",
                        UpdatedAt = Timestamp.FromDateTimeOffset(issue.UpdatedAt),
                    });

                    if (response.Results.Count >= limit) break;
                }

                response.TotalResults = response.Results.Count;
                return response;
            }
        }

        // Unknown seed source — fall back to FTS with reduced score
        SearchResponse ftsResult = await Search(new SearchRequest
        {
            Query = request.SeedId,
            Limit = limit,
        }, context);

        foreach (SearchResultItem item in ftsResult.Results)
            item.Score *= 0.3;

        return ftsResult;
    }

    public override Task<GetItemXRefResponse> GetItemCrossReferences(GetItemXRefRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        GetItemXRefResponse response = new GetItemXRefResponse();
        string direction = request.Direction?.ToLowerInvariant() ?? "both";

        if (request.Source == "jira")
        {
            // Jira-to-Jira links
            if (direction is "outgoing" or "both")
            {
                List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, SourceKey: request.Id);
                foreach (JiraIssueLinkRecord link in links)
                {
                    JiraIssueRecord? target = JiraIssueRecord.SelectSingle(connection, Key: link.TargetKey);
                    response.References.Add(new SourceCrossReference
                    {
                        SourceType = "jira",
                        SourceId = request.Id,
                        TargetType = "jira",
                        TargetId = link.TargetKey,
                        LinkType = "linked_issue",
                        SourceTitle = target?.Title ?? "",
                        SourceUrl = $"{options.BaseUrl}/browse/{link.TargetKey}",
                    });
                }
            }

            if (direction is "incoming" or "both")
            {
                List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, TargetKey: request.Id);
                foreach (JiraIssueLinkRecord link in links)
                {
                    JiraIssueRecord? source = JiraIssueRecord.SelectSingle(connection, Key: link.SourceKey);
                    response.References.Add(new SourceCrossReference
                    {
                        SourceType = "jira",
                        SourceId = link.SourceKey,
                        TargetType = "jira",
                        TargetId = request.Id,
                        LinkType = "linked_issue",
                        SourceTitle = source?.Title ?? "",
                        SourceUrl = $"{options.BaseUrl}/browse/{link.SourceKey}",
                    });
                }
            }

            // Cross-source references from Jira content
            if (direction is "outgoing" or "both")
            {
                foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: request.Id))
                {
                    response.References.Add(new SourceCrossReference
                    {
                        SourceType = "jira",
                        SourceId = request.Id,
                        TargetType = "zulip",
                        TargetId = r.TargetId,
                        LinkType = "mentions",
                        Context = r.Context ?? "",
                        SourceTitle = "",
                        SourceUrl = $"{options.BaseUrl}/browse/{request.Id}",
                    });
                }

                foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: request.Id))
                {
                    response.References.Add(new SourceCrossReference
                    {
                        SourceType = "jira",
                        SourceId = request.Id,
                        TargetType = "github",
                        TargetId = r.TargetId,
                        LinkType = "mentions",
                        Context = r.Context ?? "",
                        SourceTitle = "",
                        SourceUrl = $"{options.BaseUrl}/browse/{request.Id}",
                    });
                }

                foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: request.Id))
                {
                    response.References.Add(new SourceCrossReference
                    {
                        SourceType = "jira",
                        SourceId = request.Id,
                        TargetType = "confluence",
                        TargetId = r.TargetId,
                        LinkType = "mentions",
                        Context = r.Context ?? "",
                        SourceTitle = "",
                        SourceUrl = $"{options.BaseUrl}/browse/{request.Id}",
                    });
                }

                foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: request.Id))
                {
                    response.References.Add(new SourceCrossReference
                    {
                        SourceType = "jira",
                        SourceId = request.Id,
                        TargetType = "fhir",
                        TargetId = r.TargetId,
                        LinkType = "mentions",
                        Context = r.Context ?? "",
                        SourceTitle = "",
                        SourceUrl = $"{options.BaseUrl}/browse/{request.Id}",
                    });
                }
            }

            // Incoming Zulip references (Zulip topics that reference this Jira issue)
            if (direction is "incoming" or "both")
            {
                JiraIssueRecord? issueForIncoming = JiraIssueRecord.SelectSingle(connection, Key: request.Id);
                if (issueForIncoming is not null)
                {
                    // Check if any Zulip xref records reference this issue by stream/topic
                    // (incoming = some other source references this Jira issue via Zulip)
                }
            }

            // Spec artifact links
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: request.Id);
            if (issue?.Specification is not null)
            {
                JiraSpecArtifactRecord? specArtifact = JiraSpecArtifactRecord.SelectSingle(connection, SpecKey: issue.Specification);
                if (specArtifact?.GitUrl is not null)
                {
                    response.References.Add(new SourceCrossReference
                    {
                        SourceType = "jira",
                        SourceId = request.Id,
                        TargetType = "github",
                        TargetId = specArtifact.GitUrl,
                        LinkType = "spec_artifact",
                        Context = $"{specArtifact.SpecName} ({specArtifact.Family})",
                        SourceTitle = issue.Title,
                        SourceUrl = $"{options.BaseUrl}/browse/{request.Id}",
                    });
                }
            }
        }

        return Task.FromResult(response);
    }

    public override Task<SnapshotResponse> GetSnapshot(GetSnapshotRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        JiraIssueRecord issue = JiraIssueRecord.SelectSingle(connection, Key: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Issue {request.Id} not found"));

        string md = BuildMarkdownSnapshot(connection, issue, request.IncludeComments, request.IncludeInternalRefs);

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
        using SqliteConnection connection = database.OpenConnection();
        JiraIssueRecord issue = JiraIssueRecord.SelectSingle(connection, Key: request.Id)
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
        using SqliteConnection connection = database.OpenConnection();

        string sql = "SELECT Key, Title, Description, Labels, WorkGroup, Specification, UpdatedAt FROM jira_issues";
        List<SqliteParameter> parameters = new List<SqliteParameter>();

        if (request.Since is not null)
        {
            sql += " WHERE UpdatedAt >= @since";
            parameters.Add(new SqliteParameter("@since", request.Since.ToDateTimeOffset().ToString("o")));
        }

        sql += " ORDER BY UpdatedAt ASC";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            SearchableTextItem item = new SearchableTextItem
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
            List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection, IssueKey: key);
            foreach (JiraCommentRecord c in comments)
                item.TextFields.Add(c.Body);

            await responseStream.WriteAsync(item);
        }
    }

    public override async Task<IngestionStatusResponse> TriggerIngestion(TriggerIngestionRequest request, ServerCallContext context)
    {
        if (options.IngestionPaused)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Ingestion is paused"));

        string type = request.Type?.ToLowerInvariant() ?? "incremental";

        workQueue.Enqueue(ct => type switch
        {
            "full" => pipeline.RunFullIngestionAsync(request.Filter, ct),
            _ => pipeline.RunIncrementalIngestionAsync(ct),
        }, $"jira-{type}");

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
        return await FhirAugury.Common.Grpc.SourceServiceLifecycle.RebuildFromCacheAsync(
            async ct => (await pipeline.RebuildFromCacheAsync(ct)).ItemsProcessed,
            context.CancellationToken);
    }

    public override Task<StatsResponse> GetStats(StatsRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();

        int issueCount = JiraIssueRecord.SelectCount(connection);
        int commentCount = JiraCommentRecord.SelectCount(connection);
        long dbSize = database.GetDatabaseSizeBytes();
        CacheStats cacheStats = cache.GetStats(JiraCacheLayout.SourceName);

        StatsResponse response = new StatsResponse
        {
            Source = "jira",
            TotalItems = issueCount,
            TotalComments = commentCount,
            DatabaseSizeBytes = dbSize,
            CacheSizeBytes = cacheStats.TotalBytes,
        };

        // Sync state
        JiraSyncStateRecord? syncState = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);
        if (syncState is not null)
            response.LastSyncAt = Timestamp.FromDateTimeOffset(syncState.LastSyncAt);

        response.AdditionalCounts.Add("issue_links", JiraIssueLinkRecord.SelectCount(connection));
        response.AdditionalCounts.Add("spec_artifacts", JiraSpecArtifactRecord.SelectCount(connection));

        return Task.FromResult(response);
    }

    public override Task<HealthCheckResponse> HealthCheck(HealthCheckRequest request, ServerCallContext context)
    {
        return Task.FromResult(FhirAugury.Common.Grpc.SourceServiceLifecycle.BuildHealthCheck(database, pipeline));
    }

    // ── Helpers ──────────────────────────────────────────────────

    private IngestionStatusResponse GetCurrentStatus()
    {
        using SqliteConnection connection = database.OpenConnection();
        JiraSyncStateRecord? syncState = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);

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
        StringBuilder sb = new StringBuilder();
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
            List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection, IssueKey: issue.Key);
            if (comments.Count > 0)
            {
                sb.AppendLine("## Comments");
                sb.AppendLine();
                foreach (JiraCommentRecord c in comments)
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
            List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, SourceKey: issue.Key);
            List<JiraIssueLinkRecord> targetLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: issue.Key);

            if (links.Count > 0 || targetLinks.Count > 0)
            {
                sb.AppendLine("## Related Issues");
                sb.AppendLine();
                foreach (JiraIssueLinkRecord l in links)
                    sb.AppendLine($"- **{l.LinkType}** → {l.TargetKey}");
                foreach (JiraIssueLinkRecord l in targetLinks)
                    sb.AppendLine($"- **{l.LinkType}** ← {l.SourceKey}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public override Task<PeerIngestionAck> NotifyPeerIngestionComplete(
        PeerIngestionNotification request, ServerCallContext context)
    {
        if (request.Source.Equals("zulip", StringComparison.OrdinalIgnoreCase))
        {
            workQueue.Enqueue(ct =>
            {
                xrefRebuilder.ExtractAll(ct);
                return Task.CompletedTask;
            }, "rebuild-xrefs");

            return Task.FromResult(new PeerIngestionAck
                { Acknowledged = true, ActionTaken = "queued xref rebuild" });
        }

        return Task.FromResult(new PeerIngestionAck
            { Acknowledged = true, ActionTaken = "no action needed" });
    }

    public override Task<RebuildIndexResponse> RebuildIndex(
        RebuildIndexRequest request, ServerCallContext context)
    {
        string indexType = request.IndexType?.ToLowerInvariant() ?? "all";

        workQueue.Enqueue(ct =>
        {
            switch (indexType)
            {
                case "lookup-tables":
                    using (SqliteConnection conn = database.OpenConnection())
                        indexBuilder.RebuildIndexTables(conn);
                    break;
                case "cross-refs":
                    xrefRebuilder.ExtractAll(ct);
                    break;
                case "bm25":
                    indexer.RebuildFullIndex(ct);
                    break;
                case "fts":
                    database.RebuildFtsIndexes();
                    break;
                case "all":
                    using (SqliteConnection conn = database.OpenConnection())
                        indexBuilder.RebuildIndexTables(conn);
                    xrefRebuilder.ExtractAll(ct);
                    indexer.RebuildFullIndex(ct);
                    database.RebuildFtsIndexes();
                    break;
            }
            return Task.CompletedTask;
        }, $"rebuild-index-{indexType}");

        return Task.FromResult(new RebuildIndexResponse
            { Success = true, ActionTaken = $"queued {indexType} index rebuild" });
    }

    private static Timestamp? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        string str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
            ? Timestamp.FromDateTimeOffset(dt)
            : null;
    }
}

/// <summary>
/// Implements Jira-specific gRPC extensions from jira.proto.
/// </summary>
#pragma warning disable CS9113 // pipeline used in later phases for snapshot generation
public class JiraSpecificGrpcService(
    JiraDatabase database,
    JiraIngestionPipeline pipeline,
    IOptions<JiraServiceOptions> optionsAccessor)
    : JiraService.JiraServiceBase
#pragma warning restore CS9113
{
    private readonly JiraServiceOptions options = optionsAccessor.Value;

    public override async Task GetIssueComments(JiraGetCommentsRequest request, IServerStreamWriter<Fhiraugury.JiraComment> responseStream, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection, IssueKey: request.IssueKey);

        foreach (JiraCommentRecord c in comments)
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
        using SqliteConnection connection = database.OpenConnection();

        List<JiraIssueLinkRecord> outLinks = JiraIssueLinkRecord.SelectList(connection, SourceKey: request.IssueKey);
        List<JiraIssueLinkRecord> inLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: request.IssueKey);

        JiraIssueLinksResponse response = new JiraIssueLinksResponse();
        foreach (JiraIssueLinkRecord l in outLinks)
        {
            response.Links.Add(new Fhiraugury.JiraIssueLink
            {
                SourceKey = l.SourceKey,
                TargetKey = l.TargetKey,
                LinkType = l.LinkType,
            });
        }
        foreach (JiraIssueLinkRecord l in inLinks)
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
        using SqliteConnection connection = database.OpenConnection();
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

        using SqliteCommand cmd = new SqliteCommand(
            "SELECT Key, ProjectKey, Title, Type, Status, Priority, WorkGroup, Specification, UpdatedAt FROM jira_issues WHERE WorkGroup = @wg ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
            connection);
        cmd.Parameters.AddWithValue("@wg", request.WorkGroup);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            await responseStream.WriteAsync(ReadIssueSummary(reader));
    }

    public override async Task ListBySpecification(JiraSpecificationRequest request, IServerStreamWriter<JiraIssueSummary> responseStream, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

        using SqliteCommand cmd = new SqliteCommand(
            "SELECT Key, ProjectKey, Title, Type, Status, Priority, WorkGroup, Specification, UpdatedAt FROM jira_issues WHERE Specification = @spec ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
            connection);
        cmd.Parameters.AddWithValue("@spec", request.Specification);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            await responseStream.WriteAsync(ReadIssueSummary(reader));
    }

    public override async Task QueryIssues(JiraQueryRequest request, IServerStreamWriter<JiraIssueSummary> responseStream, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        (string? sql, List<SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader["Key"]?.ToString() ?? "";
            JiraIssueSummary summary = new JiraIssueSummary
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
                DateTimeOffset.TryParse(updatedStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset updated))
            {
                summary.UpdatedAt = Timestamp.FromDateTimeOffset(updated);
            }

            await responseStream.WriteAsync(summary);
        }
    }

    public override async Task ListSpecArtifacts(JiraListSpecArtifactsRequest request, IServerStreamWriter<SpecArtifactEntry> responseStream, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();

        string sql = "SELECT Family, SpecKey, SpecName, GitUrl, PublishedUrl, DefaultWorkgroup FROM jira_spec_artifacts";
        if (!string.IsNullOrEmpty(request.FamilyFilter))
            sql += " WHERE Family = @family";
        sql += " ORDER BY Family, SpecKey";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        if (!string.IsNullOrEmpty(request.FamilyFilter))
            cmd.Parameters.AddWithValue("@family", request.FamilyFilter);

        using SqliteDataReader reader = cmd.ExecuteReader();
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
        using SqliteConnection connection = database.OpenConnection();

        string sql = "SELECT Key FROM jira_issues";
        if (!string.IsNullOrEmpty(request.ProjectFilter))
            sql += " WHERE ProjectKey = @project";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        if (!string.IsNullOrEmpty(request.ProjectFilter))
            cmd.Parameters.AddWithValue("@project", request.ProjectFilter);

        JiraIssueNumbersResponse response = new JiraIssueNumbersResponse();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            int dashIndex = key.LastIndexOf('-');
            if (dashIndex >= 0 && int.TryParse(key.AsSpan(dashIndex + 1), out int number))
                response.IssueNumbers.Add(number);
        }

        return Task.FromResult(response);
    }

    public override Task<SnapshotResponse> GetIssueSnapshot(JiraSnapshotRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        JiraIssueRecord issue = JiraIssueRecord.SelectSingle(connection, Key: request.Key)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Issue {request.Key} not found"));

        StringBuilder sb = new StringBuilder();
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
            List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection, IssueKey: issue.Key);
            if (comments.Count > 0)
            {
                sb.AppendLine("## Comments");
                sb.AppendLine();
                foreach (JiraCommentRecord c in comments)
                {
                    sb.AppendLine($"**{c.Author}** ({c.CreatedAt:yyyy-MM-dd}):");
                    sb.AppendLine(c.Body);
                    sb.AppendLine();
                }
            }
        }

        if (request.IncludeInternalRefs)
        {
            List<JiraIssueLinkRecord> outLinks = JiraIssueLinkRecord.SelectList(connection, SourceKey: issue.Key);
            List<JiraIssueLinkRecord> inLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: issue.Key);
            if (outLinks.Count > 0 || inLinks.Count > 0)
            {
                sb.AppendLine("## Related Issues");
                sb.AppendLine();
                foreach (JiraIssueLinkRecord l in outLinks) sb.AppendLine($"- {l.LinkType} → {l.TargetKey}");
                foreach (JiraIssueLinkRecord l in inLinks) sb.AppendLine($"- {l.LinkType} ← {l.SourceKey}");
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
        string key = reader.GetString(0);
        JiraIssueSummary summary = new JiraIssueSummary
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
            DateTimeOffset.TryParse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset updated))
        {
            summary.UpdatedAt = Timestamp.FromDateTimeOffset(updated);
        }

        return summary;
    }
}
