using System.Globalization;
using System.Text;
using Fhiraugury;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using FhirAugury.Source.Confluence.Indexing;
using FhirAugury.Source.Confluence.Ingestion;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Api;

/// <summary>
/// Implements the SourceService gRPC contract for the Confluence source.
/// </summary>
public class ConfluenceGrpcService(
    ConfluenceDatabase database,
    ConfluenceIngestionPipeline pipeline,
    IResponseCache cache,
    FhirAugury.Common.Ingestion.IngestionWorkQueue workQueue,
    ConfluenceXRefRebuilder xrefRebuilder,
    ConfluenceLinkRebuilder linkRebuilder,
    ConfluenceIndexer indexer,
    IOptions<ConfluenceServiceOptions> optionsAccessor)
    : SourceService.SourceServiceBase
{
    private readonly ConfluenceServiceOptions options = optionsAccessor.Value;
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
            SELECT cp.ConfluenceId, cp.Title,
                   snippet(confluence_pages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                   confluence_pages_fts.rank,
                   cp.SpaceKey, cp.LastModifiedAt
            FROM confluence_pages_fts
            JOIN confluence_pages cp ON cp.Id = confluence_pages_fts.rowid
            WHERE confluence_pages_fts MATCH @query
            ORDER BY confluence_pages_fts.rank
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
            string pageId = reader.GetString(0);
            response.Results.Add(new SearchResultItem
            {
                Source = "confluence",
                Id = pageId,
                Title = reader.GetString(1),
                Snippet = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Score = -reader.GetDouble(3),
                Url = $"{options.BaseUrl}/pages/{pageId}",
                UpdatedAt = ParseTimestamp(reader, 5),
            });
        }

        response.TotalResults = response.Results.Count;
        return Task.FromResult(response);
    }

    public override Task<ItemResponse> GetItem(GetItemRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        ConfluencePageRecord page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.Id} not found"));

        ItemResponse response = new ItemResponse
        {
            Source = "confluence",
            Id = page.ConfluenceId,
            Title = page.Title,
            Content = request.IncludeContent ? (page.BodyPlain ?? "") : "",
            Url = page.Url ?? $"{options.BaseUrl}/pages/{page.ConfluenceId}",
            CreatedAt = Timestamp.FromDateTimeOffset(page.LastModifiedAt),
            UpdatedAt = Timestamp.FromDateTimeOffset(page.LastModifiedAt),
        };

        response.Metadata.Add("space_key", page.SpaceKey);
        response.Metadata.Add("version_number", page.VersionNumber.ToString());
        if (page.LastModifiedBy is not null) response.Metadata.Add("last_modified_by", page.LastModifiedBy);
        if (page.Labels is not null) response.Metadata.Add("labels", page.Labels);
        if (page.ParentId is not null) response.Metadata.Add("parent_id", page.ParentId);

        if (request.IncludeComments)
        {
            int pageDbId = page.Id;
            List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: pageDbId);
            foreach (ConfluenceCommentRecord c in comments)
            {
                response.Comments.Add(new Comment
                {
                    Id = c.Id.ToString(),
                    Author = c.Author,
                    Body = c.Body ?? "",
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

        string sql = "SELECT ConfluenceId, Title, LastModifiedAt, SpaceKey, Url FROM confluence_pages ORDER BY LastModifiedAt DESC LIMIT @limit OFFSET @offset";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string pageId = reader.GetString(0);
            ItemSummary summary = new ItemSummary
            {
                Id = pageId,
                Title = reader.GetString(1),
                Url = reader.IsDBNull(4) ? $"{options.BaseUrl}/pages/{pageId}" : reader.GetString(4),
                UpdatedAt = ParseTimestamp(reader, 2),
            };
            summary.Metadata.Add("space_key", reader.IsDBNull(3) ? "" : reader.GetString(3));

            await responseStream.WriteAsync(summary);
        }
    }

    public override Task<SearchResponse> GetRelated(GetRelatedRequest request, ServerCallContext context)
    {
        if (!string.IsNullOrEmpty(request.SeedSource) && request.SeedSource != "confluence")
            return GetCrossSourceRelated(request, context);

        using SqliteConnection connection = database.OpenConnection();
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 50) : 10;
        SearchResponse response = new SearchResponse();

        // Find related pages via page links
        List<ConfluencePageLinkRecord> outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: request.Id);
        List<ConfluencePageLinkRecord> inLinks = ConfluencePageLinkRecord.SelectList(connection, TargetPageId: request.Id);

        List<string> relatedIds = outLinks.Select(l => l.TargetPageId)
            .Concat(inLinks.Select(l => l.SourcePageId))
            .Distinct()
            .Take(limit)
            .ToList();

        foreach (string? relId in relatedIds)
        {
            ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: relId);
            if (page is null) continue;

            response.Results.Add(new SearchResultItem
            {
                Source = "confluence",
                Id = page.ConfluenceId,
                Title = page.Title,
                Url = page.Url ?? $"{options.BaseUrl}/pages/{page.ConfluenceId}",
                UpdatedAt = Timestamp.FromDateTimeOffset(page.LastModifiedAt),
            });
        }

        response.TotalResults = response.Results.Count;
        return Task.FromResult(response);
    }

    private Task<SearchResponse> GetCrossSourceRelated(GetRelatedRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 50) : 10;
        SearchResponse response = new SearchResponse();

        if (request.SeedSource == "jira")
        {
            // Find Confluence pages that reference this Jira ticket
            List<JiraXRefRecord> refs = JiraXRefRecord.SelectList(connection, JiraKey: request.SeedId);
            HashSet<string> seen = [];
            foreach (JiraXRefRecord jiraRef in refs)
            {
                if (!seen.Add(jiraRef.SourceId)) continue;
                if (seen.Count > limit) break;

                ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: jiraRef.SourceId);
                if (page is null) continue;

                response.Results.Add(new SearchResultItem
                {
                    Source = "confluence",
                    Id = page.ConfluenceId,
                    Title = page.Title,
                    Url = page.Url ?? $"{options.BaseUrl}/pages/{page.ConfluenceId}",
                    Score = 1.0,
                    UpdatedAt = Timestamp.FromDateTimeOffset(page.LastModifiedAt),
                });
            }
        }
        else
        {
            // Unknown seed source — fall back to FTS with SeedId, scores × 0.3
            string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(request.SeedId);
            if (!string.IsNullOrEmpty(ftsQuery))
            {
                string sql = """
                    SELECT cp.ConfluenceId, cp.Title, confluence_pages_fts.rank, cp.Url, cp.LastModifiedAt
                    FROM confluence_pages_fts
                    JOIN confluence_pages cp ON cp.Id = confluence_pages_fts.rowid
                    WHERE confluence_pages_fts MATCH @query
                    ORDER BY confluence_pages_fts.rank
                    LIMIT @limit
                    """;

                using SqliteCommand cmd = new SqliteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@query", ftsQuery);
                cmd.Parameters.AddWithValue("@limit", limit);

                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string pageId = reader.GetString(0);
                    double rank = reader.GetDouble(2);
                    response.Results.Add(new SearchResultItem
                    {
                        Source = "confluence",
                        Id = pageId,
                        Title = reader.GetString(1),
                        Url = reader.IsDBNull(3) ? $"{options.BaseUrl}/pages/{pageId}" : reader.GetString(3),
                        Score = Math.Abs(rank) * 0.3,
                        UpdatedAt = ParseTimestamp(reader, 4),
                    });
                }
            }
        }

        response.TotalResults = response.Results.Count;
        return Task.FromResult(response);
    }

    public override Task<GetItemXRefResponse> GetItemCrossReferences(GetItemXRefRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        GetItemXRefResponse response = new GetItemXRefResponse();
        string direction = request.Direction?.ToLowerInvariant() ?? "both";

        if (request.Source == "confluence" && direction is "outgoing" or "both")
        {
            ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: request.Id);
            string sourceTitle = page?.Title ?? "";
            string sourceUrl = page?.Url ?? "";

            foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, SourceId: request.Id))
            {
                response.References.Add(new SourceCrossReference
                {
                    SourceType = "confluence", SourceId = request.Id,
                    TargetType = "jira", TargetId = r.JiraKey,
                    LinkType = "mentions", Context = r.Context ?? "",
                    SourceTitle = sourceTitle, SourceUrl = sourceUrl,
                });
            }

            foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: request.Id))
            {
                response.References.Add(new SourceCrossReference
                {
                    SourceType = "confluence", SourceId = request.Id,
                    TargetType = "zulip", TargetId = r.TargetId,
                    LinkType = "mentions", Context = r.Context ?? "",
                    SourceTitle = sourceTitle, SourceUrl = sourceUrl,
                });
            }

            foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: request.Id))
            {
                response.References.Add(new SourceCrossReference
                {
                    SourceType = "confluence", SourceId = request.Id,
                    TargetType = "github", TargetId = r.TargetId,
                    LinkType = "mentions", Context = r.Context ?? "",
                    SourceTitle = sourceTitle, SourceUrl = sourceUrl,
                });
            }

            foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: request.Id))
            {
                response.References.Add(new SourceCrossReference
                {
                    SourceType = "confluence", SourceId = request.Id,
                    TargetType = "fhir", TargetId = r.TargetId,
                    LinkType = "mentions", Context = r.Context ?? "",
                    SourceTitle = sourceTitle, SourceUrl = sourceUrl,
                });
            }
        }

        if (request.Source == "jira" && direction is "incoming" or "both")
        {
            List<JiraXRefRecord> refs = JiraXRefRecord.SelectList(connection, JiraKey: request.Id);
            HashSet<string> seen = [];
            foreach (JiraXRefRecord jiraRef in refs)
            {
                if (!seen.Add(jiraRef.SourceId)) continue;
                ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: jiraRef.SourceId);
                if (page is null) continue;

                response.References.Add(new SourceCrossReference
                {
                    SourceType = "confluence",
                    SourceId = jiraRef.SourceId,
                    TargetType = "jira",
                    TargetId = request.Id,
                    LinkType = "mentions",
                    Context = jiraRef.Context ?? "",
                    SourceTitle = page.Title,
                    SourceUrl = page.Url ?? "",
                });
            }
        }

        return Task.FromResult(response);
    }

    public override Task<SnapshotResponse> GetSnapshot(GetSnapshotRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        ConfluencePageRecord page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.Id} not found"));

        string md = BuildPageMarkdownSnapshot(connection, page, request.IncludeComments, request.IncludeInternalRefs);

        return Task.FromResult(new SnapshotResponse
        {
            Id = page.ConfluenceId,
            Source = "confluence",
            Markdown = md,
            Url = page.Url ?? $"{options.BaseUrl}/pages/{page.ConfluenceId}",
        });
    }

    public override Task<ContentResponse> GetContent(GetContentRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        ConfluencePageRecord page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.Id} not found"));

        string content = request.Format?.Equals("storage", StringComparison.OrdinalIgnoreCase) == true
            ? (page.BodyStorage ?? "")
            : (page.BodyPlain ?? "");

        return Task.FromResult(new ContentResponse
        {
            Id = page.ConfluenceId,
            Source = "confluence",
            Content = content,
            Format = string.IsNullOrEmpty(request.Format) ? "text" : request.Format,
            Url = page.Url ?? $"{options.BaseUrl}/pages/{page.ConfluenceId}",
        });
    }

    public override async Task StreamSearchableText(StreamTextRequest request, IServerStreamWriter<SearchableTextItem> responseStream, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();

        string sql = "SELECT ConfluenceId, Title, BodyPlain, Labels, SpaceKey, LastModifiedAt FROM confluence_pages";
        List<SqliteParameter> parameters = new List<SqliteParameter>();

        if (request.Since is not null)
        {
            sql += " WHERE LastModifiedAt >= @since";
            parameters.Add(new SqliteParameter("@since", request.Since.ToDateTimeOffset().ToString("o")));
        }

        sql += " ORDER BY LastModifiedAt ASC";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string pageId = reader.GetString(0);
            SearchableTextItem item = new SearchableTextItem
            {
                Source = "confluence",
                Id = pageId,
                Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                UpdatedAt = ParseTimestamp(reader, 5),
            };

            for (int i = 1; i <= 4; i++)
            {
                if (!reader.IsDBNull(i))
                    item.TextFields.Add(reader.GetString(i));
            }

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
            "full" => pipeline.RunFullIngestionAsync(ct),
            _ => pipeline.RunIncrementalIngestionAsync(ct),
        }, $"confluence-{type}");

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

        int pageCount = ConfluencePageRecord.SelectCount(connection);
        int commentCount = ConfluenceCommentRecord.SelectCount(connection);
        int spaceCount = ConfluenceSpaceRecord.SelectCount(connection);
        long dbSize = database.GetDatabaseSizeBytes();
        CacheStats cacheStats = cache.GetStats(ConfluenceCacheLayout.SourceName);

        StatsResponse response = new StatsResponse
        {
            Source = "confluence",
            TotalItems = pageCount,
            TotalComments = commentCount,
            DatabaseSizeBytes = dbSize,
            CacheSizeBytes = cacheStats.TotalBytes,
        };

        ConfluenceSyncStateRecord? syncState = ConfluenceSyncStateRecord.SelectSingle(connection, SourceName: ConfluenceSource.SourceName);
        if (syncState is not null)
            response.LastSyncAt = Timestamp.FromDateTimeOffset(syncState.LastSyncAt);

        response.AdditionalCounts.Add("spaces", spaceCount);
        response.AdditionalCounts.Add("page_links", ConfluencePageLinkRecord.SelectCount(connection));

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
        ConfluenceSyncStateRecord? syncState = ConfluenceSyncStateRecord.SelectSingle(connection, SourceName: ConfluenceSource.SourceName);

        return new IngestionStatusResponse
        {
            Source = "confluence",
            Status = pipeline.IsRunning ? pipeline.CurrentStatus : (syncState?.Status ?? "unknown"),
            LastSyncAt = syncState is not null ? Timestamp.FromDateTimeOffset(syncState.LastSyncAt) : null,
            ItemsTotal = syncState?.ItemsIngested ?? 0,
            LastError = syncState?.LastError ?? "",
            SyncSchedule = options.SyncSchedule,
        };
    }

    private string BuildPageMarkdownSnapshot(
        SqliteConnection connection, ConfluencePageRecord page, bool includeComments, bool includeRefs)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"# {page.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Space:** {page.SpaceKey}  ");
        sb.AppendLine($"**Version:** {page.VersionNumber}  ");
        if (page.LastModifiedBy is not null) sb.AppendLine($"**Last Modified By:** {page.LastModifiedBy}  ");
        sb.AppendLine($"**Last Modified:** {page.LastModifiedAt:yyyy-MM-dd}  ");
        if (page.Labels is not null) sb.AppendLine($"**Labels:** {page.Labels}  ");
        sb.AppendLine($"**URL:** {page.Url}  ");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(page.BodyPlain))
        {
            sb.AppendLine("## Content");
            sb.AppendLine();
            sb.AppendLine(page.BodyPlain);
            sb.AppendLine();
        }

        if (includeComments)
        {
            List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
            if (comments.Count > 0)
            {
                sb.AppendLine("## Comments");
                sb.AppendLine();
                foreach (ConfluenceCommentRecord c in comments)
                {
                    sb.AppendLine($"### {c.Author} ({c.CreatedAt:yyyy-MM-dd})");
                    sb.AppendLine();
                    sb.AppendLine(c.Body ?? "");
                    sb.AppendLine();
                }
            }
        }

        if (includeRefs)
        {
            List<ConfluencePageLinkRecord> outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: page.ConfluenceId);
            List<ConfluencePageLinkRecord> inLinks = ConfluencePageLinkRecord.SelectList(connection, TargetPageId: page.ConfluenceId);

            if (outLinks.Count > 0 || inLinks.Count > 0)
            {
                sb.AppendLine("## Linked Pages");
                sb.AppendLine();
                foreach (ConfluencePageLinkRecord l in outLinks)
                {
                    ConfluencePageRecord? target = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: l.TargetPageId);
                    sb.AppendLine($"- **{l.LinkType}** → {target?.Title ?? l.TargetPageId}");
                }
                foreach (ConfluencePageLinkRecord l in inLinks)
                {
                    ConfluencePageRecord? source = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: l.SourcePageId);
                    sb.AppendLine($"- **{l.LinkType}** ← {source?.Title ?? l.SourcePageId}");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static Timestamp? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        string str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
            ? Timestamp.FromDateTimeOffset(dt)
            : null;
    }

    public override Task<PeerIngestionAck> NotifyPeerIngestionComplete(
        PeerIngestionNotification request, ServerCallContext context)
    {
        if (request.Source.Equals("jira", StringComparison.OrdinalIgnoreCase))
        {
            workQueue.Enqueue(ct =>
            {
                xrefRebuilder.RebuildAll(ct);
                return Task.CompletedTask;
            }, "rebuild-xrefs");

            return Task.FromResult(new PeerIngestionAck
                { Acknowledged = true, ActionTaken = "queued cross-ref index rebuild" });
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
                case "bm25":
                    indexer.RebuildFullIndex(ct);
                    break;
                case "cross-refs":
                    xrefRebuilder.RebuildAll(ct);
                    break;
                case "page-links":
                    linkRebuilder.RebuildAll(ct);
                    break;
                case "fts":
                    database.RebuildFtsIndexes();
                    break;
                case "all":
                    xrefRebuilder.RebuildAll(ct);
                    linkRebuilder.RebuildAll(ct);
                    indexer.RebuildFullIndex(ct);
                    database.RebuildFtsIndexes();
                    break;
            }
            return Task.CompletedTask;
        }, $"rebuild-index-{indexType}");

        return Task.FromResult(new RebuildIndexResponse
            { Success = true, ActionTaken = $"queued {indexType} index rebuild" });
    }
}

/// <summary>
/// Implements Confluence-specific gRPC extensions from confluence.proto.
/// </summary>
public class ConfluenceSpecificGrpcService(
    ConfluenceDatabase database,
    IOptions<ConfluenceServiceOptions> optionsAccessor)
    : ConfluenceService.ConfluenceServiceBase
{
    private readonly ConfluenceServiceOptions options = optionsAccessor.Value;

    public override async Task GetPageComments(ConfluenceGetCommentsRequest request, IServerStreamWriter<ConfluenceComment> responseStream, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        ConfluencePageRecord page = ConfluencePageRecord.SelectSingle(connection, Id: request.PageId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.PageId} not found"));

        List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: request.PageId);

        foreach (ConfluenceCommentRecord c in comments)
        {
            await responseStream.WriteAsync(new ConfluenceComment
            {
                Id = c.Id.ToString(),
                PageId = c.PageId,
                Author = c.Author,
                Body = c.Body ?? "",
                CreatedAt = Timestamp.FromDateTimeOffset(c.CreatedAt),
                Url = page.Url ?? "",
            });
        }
    }

    public override async Task GetPageChildren(ConfluenceGetChildrenRequest request, IServerStreamWriter<ConfluencePageSummary> responseStream, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();

        // Find the parent page's ConfluenceId
        ConfluencePageRecord? parentPage = ConfluencePageRecord.SelectSingle(connection, Id: request.PageId);
        if (parentPage is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.PageId} not found"));

        string sql = "SELECT Id, ConfluenceId, SpaceKey, Title, Url, LastModifiedAt FROM confluence_pages WHERE ParentId = @parentId";
        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@parentId", parentPage.ConfluenceId);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            await responseStream.WriteAsync(new ConfluencePageSummary
            {
                Id = reader.GetInt32(0),
                SpaceKey = reader.GetString(2),
                Title = reader.GetString(3),
                Url = reader.IsDBNull(4) ? "" : reader.GetString(4),
                LastModifiedAt = ParseTimestamp(reader, 5),
            });
        }
    }

    public override async Task GetPageAncestors(ConfluenceGetAncestorsRequest request, IServerStreamWriter<ConfluencePageSummary> responseStream, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();

        ConfluencePageRecord? current = ConfluencePageRecord.SelectSingle(connection, Id: request.PageId);
        if (current is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.PageId} not found"));

        // Walk up the ancestor chain
        List<ConfluencePageSummary> ancestors = new List<ConfluencePageSummary>();
        HashSet<string> visited = new HashSet<string>();
        string? parentId = current.ParentId;

        while (!string.IsNullOrEmpty(parentId) && !visited.Contains(parentId))
        {
            visited.Add(parentId);
            ConfluencePageRecord? parent = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: parentId);
            if (parent is null) break;

            ancestors.Add(new ConfluencePageSummary
            {
                Id = parent.Id,
                SpaceKey = parent.SpaceKey,
                Title = parent.Title,
                Url = parent.Url ?? "",
                LastModifiedAt = Timestamp.FromDateTimeOffset(parent.LastModifiedAt),
            });

            parentId = parent.ParentId;
        }

        // Reverse to give root-first order
        ancestors.Reverse();
        foreach (ConfluencePageSummary ancestor in ancestors)
            await responseStream.WriteAsync(ancestor);
    }

    public override async Task ListSpaces(ConfluenceListSpacesRequest request, IServerStreamWriter<ConfluenceSpace> responseStream, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        List<ConfluenceSpaceRecord> spaces = ConfluenceSpaceRecord.SelectList(connection);

        foreach (ConfluenceSpaceRecord space in spaces)
        {
            // Count pages in this space
            using SqliteCommand cmd = new SqliteCommand("SELECT COUNT(*) FROM confluence_pages WHERE SpaceKey = @key", connection);
            cmd.Parameters.AddWithValue("@key", space.Key);
            int pageCount = Convert.ToInt32(cmd.ExecuteScalar());

            await responseStream.WriteAsync(new ConfluenceSpace
            {
                Key = space.Key,
                Name = space.Name,
                Description = space.Description ?? "",
                Url = space.Url ?? "",
                PageCount = pageCount,
            });
        }
    }

    public override async Task GetLinkedPages(ConfluenceLinkedPagesRequest request, IServerStreamWriter<ConfluencePageSummary> responseStream, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();

        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, Id: request.PageId);
        if (page is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.PageId} not found"));

        List<string> linkedPageIds = new List<string>();

        string direction = request.Direction?.ToLowerInvariant() ?? "both";

        if (direction is "outgoing" or "both")
        {
            List<ConfluencePageLinkRecord> outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: page.ConfluenceId);
            linkedPageIds.AddRange(outLinks.Select(l => l.TargetPageId));
        }

        if (direction is "incoming" or "both")
        {
            List<ConfluencePageLinkRecord> inLinks = ConfluencePageLinkRecord.SelectList(connection, TargetPageId: page.ConfluenceId);
            linkedPageIds.AddRange(inLinks.Select(l => l.SourcePageId));
        }

        foreach (string? linkedId in linkedPageIds.Distinct())
        {
            ConfluencePageRecord? linked = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: linkedId);
            if (linked is null) continue;

            await responseStream.WriteAsync(new ConfluencePageSummary
            {
                Id = linked.Id,
                SpaceKey = linked.SpaceKey,
                Title = linked.Title,
                Url = linked.Url ?? "",
                LastModifiedAt = Timestamp.FromDateTimeOffset(linked.LastModifiedAt),
            });
        }
    }

    public override async Task GetPagesByLabel(ConfluenceLabelRequest request, IServerStreamWriter<ConfluencePageSummary> responseStream, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();

        int limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;
        int offset = Math.Max(request.Offset, 0);

        string sql = "SELECT Id, ConfluenceId, SpaceKey, Title, Url, LastModifiedAt FROM confluence_pages WHERE Labels LIKE @label";
        List<SqliteParameter> parameters = new List<SqliteParameter> { new("@label", $"%{request.Label}%") };

        if (!string.IsNullOrEmpty(request.SpaceKey))
        {
            sql += " AND SpaceKey = @spaceKey";
            parameters.Add(new SqliteParameter("@spaceKey", request.SpaceKey));
        }

        sql += " ORDER BY LastModifiedAt DESC LIMIT @limit OFFSET @offset";
        parameters.Add(new SqliteParameter("@limit", limit));
        parameters.Add(new SqliteParameter("@offset", offset));

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            await responseStream.WriteAsync(new ConfluencePageSummary
            {
                Id = reader.GetInt32(0),
                SpaceKey = reader.GetString(2),
                Title = reader.GetString(3),
                Url = reader.IsDBNull(4) ? "" : reader.GetString(4),
                LastModifiedAt = ParseTimestamp(reader, 5),
            });
        }
    }

    public override Task<SnapshotResponse> GetPageSnapshot(ConfluenceSnapshotRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        ConfluencePageRecord page = ConfluencePageRecord.SelectSingle(connection, Id: request.PageId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.PageId} not found"));

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"# {page.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Space:** {page.SpaceKey} | **Version:** {page.VersionNumber}");
        if (page.LastModifiedBy is not null) sb.AppendLine($"**Modified by:** {page.LastModifiedBy} on {page.LastModifiedAt:yyyy-MM-dd}");
        if (page.Labels is not null) sb.AppendLine($"**Labels:** {page.Labels}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(page.BodyPlain))
        {
            sb.AppendLine(page.BodyPlain);
            sb.AppendLine();
        }

        if (request.IncludeComments)
        {
            List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
            if (comments.Count > 0)
            {
                sb.AppendLine("## Comments");
                sb.AppendLine();
                foreach (ConfluenceCommentRecord c in comments)
                {
                    sb.AppendLine($"**{c.Author}** ({c.CreatedAt:yyyy-MM-dd}): {c.Body}");
                    sb.AppendLine();
                }
            }
        }

        if (request.IncludeInternalRefs)
        {
            List<ConfluencePageLinkRecord> outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: page.ConfluenceId);
            if (outLinks.Count > 0)
            {
                sb.AppendLine("## Internal References");
                sb.AppendLine();
                foreach (ConfluencePageLinkRecord l in outLinks)
                {
                    ConfluencePageRecord? target = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: l.TargetPageId);
                    sb.AppendLine($"- [{target?.Title ?? l.TargetPageId}]({options.BaseUrl}/pages/{l.TargetPageId})");
                }
                sb.AppendLine();
            }
        }

        return Task.FromResult(new SnapshotResponse
        {
            Id = page.ConfluenceId,
            Source = "confluence",
            Markdown = sb.ToString(),
            Url = page.Url ?? $"{options.BaseUrl}/pages/{page.ConfluenceId}",
        });
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
