using System.Globalization;
using System.Text;
using Fhiraugury;
using FhirAugury.Common.Caching;
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

namespace FhirAugury.Source.Confluence.Api;

/// <summary>
/// Implements the SourceService gRPC contract for the Confluence source.
/// </summary>
public class ConfluenceGrpcService(
    ConfluenceDatabase database,
    ConfluenceIngestionPipeline pipeline,
    IResponseCache cache,
    ConfluenceServiceOptions options)
    : SourceService.SourceServiceBase
{
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

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        var response = new SearchResponse { Query = request.Query };
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var pageId = reader.GetString(0);
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
        using var connection = database.OpenConnection();
        var page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.Id} not found"));

        var response = new ItemResponse
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
            var pageDbId = page.Id;
            var comments = ConfluenceCommentRecord.SelectList(connection, PageId: pageDbId);
            foreach (var c in comments)
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
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

        var sql = "SELECT ConfluenceId, Title, LastModifiedAt, SpaceKey, Url FROM confluence_pages ORDER BY LastModifiedAt DESC LIMIT @limit OFFSET @offset";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var pageId = reader.GetString(0);
            var summary = new ItemSummary
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
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 50) : 10;
        var response = new SearchResponse();

        // Find related pages via page links
        var outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: request.Id);
        var inLinks = ConfluencePageLinkRecord.SelectList(connection, TargetPageId: request.Id);

        var relatedIds = outLinks.Select(l => l.TargetPageId)
            .Concat(inLinks.Select(l => l.SourcePageId))
            .Distinct()
            .Take(limit)
            .ToList();

        foreach (var relId in relatedIds)
        {
            var page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: relId);
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

    public override Task<SnapshotResponse> GetSnapshot(GetSnapshotRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.Id} not found"));

        var md = BuildPageMarkdownSnapshot(connection, page, request.IncludeComments, request.IncludeInternalRefs);

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
        using var connection = database.OpenConnection();
        var page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: request.Id)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.Id} not found"));

        var content = request.Format?.Equals("storage", StringComparison.OrdinalIgnoreCase) == true
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
        using var connection = database.OpenConnection();

        var sql = "SELECT ConfluenceId, Title, BodyPlain, Labels, SpaceKey, LastModifiedAt FROM confluence_pages";
        var parameters = new List<SqliteParameter>();

        if (request.Since is not null)
        {
            sql += " WHERE LastModifiedAt >= @since";
            parameters.Add(new SqliteParameter("@since", request.Since.ToDateTimeOffset().ToString("o")));
        }

        sql += " ORDER BY LastModifiedAt ASC";

        using var cmd = new SqliteCommand(sql, connection);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var pageId = reader.GetString(0);
            var item = new SearchableTextItem
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
        var type = request.Type?.ToLowerInvariant() ?? "incremental";

        _ = type switch
        {
            "full" => Task.Run(() => pipeline.RunFullIngestionAsync(context.CancellationToken)),
            _ => Task.Run(() => pipeline.RunIncrementalIngestionAsync(context.CancellationToken)),
        };

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

        var pageCount = ConfluencePageRecord.SelectCount(connection);
        var commentCount = ConfluenceCommentRecord.SelectCount(connection);
        var spaceCount = ConfluenceSpaceRecord.SelectCount(connection);
        var dbSize = database.GetDatabaseSizeBytes();
        var cacheStats = cache.GetStats(ConfluenceCacheLayout.SourceName);

        var response = new StatsResponse
        {
            Source = "confluence",
            TotalItems = pageCount,
            TotalComments = commentCount,
            DatabaseSizeBytes = dbSize,
            CacheSizeBytes = cacheStats.TotalBytes,
        };

        var syncState = ConfluenceSyncStateRecord.SelectSingle(connection, SourceName: ConfluenceSource.SourceName);
        if (syncState is not null)
            response.LastSyncAt = Timestamp.FromDateTimeOffset(syncState.LastSyncAt);

        response.AdditionalCounts.Add("spaces", spaceCount);
        response.AdditionalCounts.Add("page_links", ConfluencePageLinkRecord.SelectCount(connection));

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
        var syncState = ConfluenceSyncStateRecord.SelectSingle(connection, SourceName: ConfluenceSource.SourceName);

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
        var sb = new StringBuilder();
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
            var comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
            if (comments.Count > 0)
            {
                sb.AppendLine("## Comments");
                sb.AppendLine();
                foreach (var c in comments)
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
            var outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: page.ConfluenceId);
            var inLinks = ConfluencePageLinkRecord.SelectList(connection, TargetPageId: page.ConfluenceId);

            if (outLinks.Count > 0 || inLinks.Count > 0)
            {
                sb.AppendLine("## Linked Pages");
                sb.AppendLine();
                foreach (var l in outLinks)
                {
                    var target = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: l.TargetPageId);
                    sb.AppendLine($"- **{l.LinkType}** → {target?.Title ?? l.TargetPageId}");
                }
                foreach (var l in inLinks)
                {
                    var source = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: l.SourcePageId);
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
        var str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? Timestamp.FromDateTimeOffset(dt)
            : null;
    }
}

/// <summary>
/// Implements Confluence-specific gRPC extensions from confluence.proto.
/// </summary>
public class ConfluenceSpecificGrpcService(
    ConfluenceDatabase database,
    ConfluenceServiceOptions options)
    : ConfluenceService.ConfluenceServiceBase
{
    public override async Task GetPageComments(ConfluenceGetCommentsRequest request, IServerStreamWriter<ConfluenceComment> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var page = ConfluencePageRecord.SelectSingle(connection, Id: request.PageId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.PageId} not found"));

        var comments = ConfluenceCommentRecord.SelectList(connection, PageId: request.PageId);

        foreach (var c in comments)
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
        using var connection = database.OpenConnection();

        // Find the parent page's ConfluenceId
        var parentPage = ConfluencePageRecord.SelectSingle(connection, Id: request.PageId);
        if (parentPage is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.PageId} not found"));

        var sql = "SELECT Id, ConfluenceId, SpaceKey, Title, Url, LastModifiedAt FROM confluence_pages WHERE ParentId = @parentId";
        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@parentId", parentPage.ConfluenceId);

        using var reader = cmd.ExecuteReader();
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
        using var connection = database.OpenConnection();

        var current = ConfluencePageRecord.SelectSingle(connection, Id: request.PageId);
        if (current is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.PageId} not found"));

        // Walk up the ancestor chain
        var ancestors = new List<ConfluencePageSummary>();
        var visited = new HashSet<string>();
        var parentId = current.ParentId;

        while (!string.IsNullOrEmpty(parentId) && !visited.Contains(parentId))
        {
            visited.Add(parentId);
            var parent = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: parentId);
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
        foreach (var ancestor in ancestors)
            await responseStream.WriteAsync(ancestor);
    }

    public override async Task ListSpaces(ConfluenceListSpacesRequest request, IServerStreamWriter<ConfluenceSpace> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var spaces = ConfluenceSpaceRecord.SelectList(connection);

        foreach (var space in spaces)
        {
            // Count pages in this space
            using var cmd = new SqliteCommand("SELECT COUNT(*) FROM confluence_pages WHERE SpaceKey = @key", connection);
            cmd.Parameters.AddWithValue("@key", space.Key);
            var pageCount = Convert.ToInt32(cmd.ExecuteScalar());

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
        using var connection = database.OpenConnection();

        var page = ConfluencePageRecord.SelectSingle(connection, Id: request.PageId);
        if (page is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.PageId} not found"));

        var linkedPageIds = new List<string>();

        var direction = request.Direction?.ToLowerInvariant() ?? "both";

        if (direction is "outgoing" or "both")
        {
            var outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: page.ConfluenceId);
            linkedPageIds.AddRange(outLinks.Select(l => l.TargetPageId));
        }

        if (direction is "incoming" or "both")
        {
            var inLinks = ConfluencePageLinkRecord.SelectList(connection, TargetPageId: page.ConfluenceId);
            linkedPageIds.AddRange(inLinks.Select(l => l.SourcePageId));
        }

        foreach (var linkedId in linkedPageIds.Distinct())
        {
            var linked = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: linkedId);
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
        using var connection = database.OpenConnection();

        var limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;
        var offset = Math.Max(request.Offset, 0);

        var sql = "SELECT Id, ConfluenceId, SpaceKey, Title, Url, LastModifiedAt FROM confluence_pages WHERE Labels LIKE @label";
        var parameters = new List<SqliteParameter> { new("@label", $"%{request.Label}%") };

        if (!string.IsNullOrEmpty(request.SpaceKey))
        {
            sql += " AND SpaceKey = @spaceKey";
            parameters.Add(new SqliteParameter("@spaceKey", request.SpaceKey));
        }

        sql += " ORDER BY LastModifiedAt DESC LIMIT @limit OFFSET @offset";
        parameters.Add(new SqliteParameter("@limit", limit));
        parameters.Add(new SqliteParameter("@offset", offset));

        using var cmd = new SqliteCommand(sql, connection);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        using var reader = cmd.ExecuteReader();
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
        using var connection = database.OpenConnection();
        var page = ConfluencePageRecord.SelectSingle(connection, Id: request.PageId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Page {request.PageId} not found"));

        var sb = new StringBuilder();
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
            var comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
            if (comments.Count > 0)
            {
                sb.AppendLine("## Comments");
                sb.AppendLine();
                foreach (var c in comments)
                {
                    sb.AppendLine($"**{c.Author}** ({c.CreatedAt:yyyy-MM-dd}): {c.Body}");
                    sb.AppendLine();
                }
            }
        }

        if (request.IncludeInternalRefs)
        {
            var outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: page.ConfluenceId);
            if (outLinks.Count > 0)
            {
                sb.AppendLine("## Internal References");
                sb.AppendLine();
                foreach (var l in outLinks)
                {
                    var target = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: l.TargetPageId);
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
        var str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? Timestamp.FromDateTimeOffset(dt)
            : null;
    }
}
