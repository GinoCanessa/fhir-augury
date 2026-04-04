using System.Globalization;
using System.Text;
using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Http;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Common.Text;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Indexing;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Api;

/// <summary>HTTP Minimal API endpoints for the Jira source service.</summary>
public static class JiraHttpApi
{
    public static IEndpointRouteBuilder MapJiraHttpApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/v1");

        MapSearchEndpoints(api);
        MapItemEndpoints(api);
        MapCrossReferenceEndpoints(api);
        MapQueryEndpoints(api);
        MapIngestionEndpoints(api);
        MapLifecycleEndpoints(api);

        return app;
    }

    // ── Search ──────────────────────────────────────────────────────

    private static void MapSearchEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/search", (string? q, int? limit, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            using SqliteConnection connection = db.OpenConnection();
            string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(q);
            if (string.IsNullOrEmpty(ftsQuery))
                return Results.Ok(new SearchResponse(q, 0, [], null));

            int maxResults = Math.Min(limit ?? 20, 200);

            string sql = """
                SELECT ji.Key, ji.Title,
                       snippet(jira_issues_fts, 1, '<b>', '</b>', '...', 20) as Snippet,
                       jira_issues_fts.rank, ji.Status, ji.UpdatedAt
                FROM jira_issues_fts
                JOIN jira_issues ji ON ji.Id = jira_issues_fts.rowid
                WHERE jira_issues_fts MATCH @query
                ORDER BY jira_issues_fts.rank
                LIMIT @limit
                """;

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", maxResults);

            List<SearchResult> results = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                results.Add(new SearchResult
                {
                    Source = SourceSystems.Jira,
                    Id = key,
                    Title = reader.GetString(1),
                    Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Score = -reader.GetDouble(3),
                    Url = $"{options.BaseUrl}/browse/{key}",
                    UpdatedAt = ParseTimestamp(reader, 5),
                    Metadata = new Dictionary<string, string>
                    {
                        ["status"] = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    },
                });
            }

            return Results.Ok(new SearchResponse(q, results.Count, results, null));
        });
    }

    // ── Items ────────────────────────────────────────────────────────

    private static void MapItemEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/items/{key}", (string key, bool? includeContent, bool? includeComments, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
            if (issue is null)
                return Results.NotFound(new { error = $"Issue {key} not found" });

            Dictionary<string, string> metadata = new()
            {
                ["status"] = issue.Status,
                ["type"] = issue.Type,
                ["priority"] = issue.Priority,
            };
            if (issue.WorkGroup is not null) metadata["work_group"] = issue.WorkGroup;
            if (issue.Specification is not null) metadata["specification"] = issue.Specification;
            if (issue.Resolution is not null) metadata["resolution"] = issue.Resolution;
            if (issue.Assignee is not null) metadata["assignee"] = issue.Assignee;
            if (issue.Reporter is not null) metadata["reporter"] = issue.Reporter;
            if (issue.Labels is not null) metadata["labels"] = issue.Labels;
            if (issue.ResolutionDescription is not null) metadata["resolution_description"] = issue.ResolutionDescription;

            List<CommentInfo>? comments = null;
            if (includeComments == true)
            {
                List<JiraCommentRecord> commentRecords = JiraCommentRecord.SelectList(connection, IssueKey: key);
                comments = commentRecords.Select(c => new CommentInfo(
                    c.Id.ToString(), c.Author, c.Body, c.CreatedAt, null)).ToList();
            }

            ItemResponse response = new ItemResponse
            {
                Source = SourceSystems.Jira,
                Id = issue.Key,
                Title = issue.Title,
                Content = includeContent == true ? issue.Description : null,
                Url = $"{options.BaseUrl}/browse/{issue.Key}",
                CreatedAt = issue.CreatedAt,
                UpdatedAt = issue.UpdatedAt,
                Metadata = metadata,
                Comments = comments,
            };

            return Results.Ok(response);
        });

        api.MapGet("/items", (int? limit, int? offset, string? sortBy, string? sortOrder, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            int maxResults = Math.Min(limit ?? 50, 500);
            int skip = Math.Max(offset ?? 0, 0);

            using SqliteCommand cmd = new SqliteCommand(
                "SELECT Key, Title, Status, Type, UpdatedAt FROM jira_issues ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
                connection);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            cmd.Parameters.AddWithValue("@offset", skip);

            List<ItemSummary> items = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                items.Add(new ItemSummary
                {
                    Id = key,
                    Title = reader.GetString(1),
                    Url = $"{options.BaseUrl}/browse/{key}",
                    UpdatedAt = ParseTimestamp(reader, 4),
                    Metadata = new Dictionary<string, string>
                    {
                        ["status"] = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        ["type"] = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    },
                });
            }

            return Results.Ok(new ItemListResponse(items.Count, items));
        });

        api.MapGet("/items/{key}/related", (string key, string? seedSource, int? limit, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            int maxResults = Math.Min(limit ?? 10, 50);

            // Cross-source related: Zulip seed
            if (!string.IsNullOrEmpty(seedSource) &&
                !string.Equals(seedSource, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(seedSource, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = key.Split(':', 2);
                    if (parts.Length == 2 && int.TryParse(parts[0], out int streamId))
                    {
                        string topicName = parts[1];
                        List<ZulipXRefRecord> refs = ZulipXRefRecord.SelectList(connection,
                            StreamId: streamId, TopicName: topicName);

                        HashSet<string> seen = [];
                        List<RelatedItem> crossItems = [];
                        foreach (ZulipXRefRecord zRef in refs)
                        {
                            if (!seen.Add(zRef.SourceId)) continue;
                            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: zRef.SourceId);
                            if (issue is null) continue;
                            crossItems.Add(new RelatedItem
                            {
                                Source = SourceSystems.Jira,
                                Id = issue.Key,
                                Title = issue.Title,
                                Url = $"{options.BaseUrl}/browse/{issue.Key}",
                                RelevanceScore = 1.0,
                                Relationship = "zulip_xref",
                            });
                            if (crossItems.Count >= maxResults) break;
                        }
                        return Results.Ok(new FindRelatedResponse(seedSource, key, null, crossItems));
                    }
                }

                // Unknown cross-source — no results
                return Results.Ok(new FindRelatedResponse(seedSource, key, null, []));
            }

            // Same-source related via issue links
            List<JiraIssueLinkRecord> outLinks = JiraIssueLinkRecord.SelectList(connection, SourceKey: key);
            List<JiraIssueLinkRecord> inLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: key);

            IEnumerable<(string Key, string LinkType)> relatedKeys = outLinks.Select(l => (Key: l.TargetKey, l.LinkType))
                .Concat(inLinks.Select(l => (Key: l.SourceKey, l.LinkType)))
                .DistinctBy(x => x.Key)
                .Take(maxResults);

            List<RelatedItem> results = [];
            foreach ((string relKey, string linkType) in relatedKeys)
            {
                JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: relKey);
                if (issue is null) continue;
                results.Add(new RelatedItem
                {
                    Source = SourceSystems.Jira,
                    Id = issue.Key,
                    Title = issue.Title,
                    Url = $"{options.BaseUrl}/browse/{issue.Key}",
                    Relationship = linkType,
                });
            }

            JiraIssueRecord? seedIssue = JiraIssueRecord.SelectSingle(connection, Key: key);
            return Results.Ok(new FindRelatedResponse(SourceSystems.Jira, key, seedIssue?.Title, results));
        });

        api.MapGet("/items/{key}/snapshot", (string key, bool? includeComments, bool? includeRefs, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
            if (issue is null)
                return Results.NotFound(new { error = $"Issue {key} not found" });

            string md = BuildMarkdownSnapshot(connection, issue, includeComments ?? true, includeRefs ?? true);

            return Results.Ok(new SnapshotResponse(issue.Key, SourceSystems.Jira, md,
                $"{options.BaseUrl}/browse/{issue.Key}", "issue"));
        });

        api.MapGet("/items/{key}/content", (string key, string? format, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
            if (issue is null)
                return Results.NotFound(new { error = $"Issue {key} not found" });

            return Results.Ok(new ContentResponse(issue.Key, SourceSystems.Jira,
                issue.Description ?? "", format ?? "text",
                $"{options.BaseUrl}/browse/{issue.Key}", null, "issue"));
        });

        api.MapGet("/items/{key}/comments", (string key, JiraDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
            if (issue is null)
                return Results.NotFound(new { error = $"Issue {key} not found" });

            List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection, IssueKey: key);
            List<JiraCommentEntry> entries = comments.Select(c =>
                new JiraCommentEntry(c.Id.ToString(), c.IssueKey, c.Author, c.Body, c.CreatedAt)).ToList();

            return Results.Ok(entries);
        });

        api.MapGet("/items/{key}/links", (string key, JiraDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            List<JiraIssueLinkRecord> outLinks = JiraIssueLinkRecord.SelectList(connection, SourceKey: key);
            List<JiraIssueLinkRecord> inLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: key);

            List<JiraIssueLinkEntry> links = outLinks.Select(l => new JiraIssueLinkEntry(l.SourceKey, l.TargetKey, l.LinkType))
                .Concat(inLinks.Select(l => new JiraIssueLinkEntry(l.SourceKey, l.TargetKey, l.LinkType)))
                .ToList();

            return Results.Ok(links);
        });
    }

    // ── Cross-References ─────────────────────────────────────────────

    private static void MapCrossReferenceEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/xref/{key}", (string key, string? direction, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            string dir = direction?.ToLowerInvariant() ?? "both";
            List<SourceCrossReference> references = [];

            // Jira-to-Jira links (outgoing)
            if (dir is "outgoing" or "both")
            {
                List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, SourceKey: key);
                foreach (JiraIssueLinkRecord link in links)
                {
                    JiraIssueRecord? target = JiraIssueRecord.SelectSingle(connection, Key: link.TargetKey);
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, key,
                        SourceSystems.Jira, link.TargetKey,
                        "linked_issue", null, "issue",
                        target?.Title, $"{options.BaseUrl}/browse/{link.TargetKey}"));
                }
            }

            // Jira-to-Jira links (incoming)
            if (dir is "incoming" or "both")
            {
                List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, TargetKey: key);
                foreach (JiraIssueLinkRecord link in links)
                {
                    JiraIssueRecord? source = JiraIssueRecord.SelectSingle(connection, Key: link.SourceKey);
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, link.SourceKey,
                        SourceSystems.Jira, key,
                        "linked_issue", null, "issue",
                        source?.Title, $"{options.BaseUrl}/browse/{link.SourceKey}"));
                }
            }

            // Cross-source outgoing references
            if (dir is "outgoing" or "both")
            {
                foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: key))
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, key,
                        SourceSystems.Zulip, r.TargetId,
                        "mentions", r.Context, "issue", null, null));
                }

                foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: key))
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, key,
                        SourceSystems.GitHub, r.TargetId,
                        "mentions", r.Context, "issue", null, null));
                }

                foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: key))
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, key,
                        SourceSystems.Confluence, r.TargetId,
                        "mentions", r.Context, "issue", null, null));
                }

                foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: key))
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, key,
                        SourceSystems.Fhir, r.TargetId,
                        "mentions", r.Context, "issue", null, null));
                }
            }

            // Spec artifact links
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
            if (issue?.Specification is not null)
            {
                JiraSpecArtifactRecord? specArtifact = JiraSpecArtifactRecord.SelectSingle(connection, SpecKey: issue.Specification);
                if (specArtifact?.GitUrl is not null)
                {
                    references.Add(new SourceCrossReference(
                        SourceSystems.Jira, key,
                        SourceSystems.GitHub, specArtifact.GitUrl,
                        "spec_artifact", $"{specArtifact.SpecName} ({specArtifact.Family})",
                        "issue", issue.Title, $"{options.BaseUrl}/browse/{key}"));
                }
            }

            return Results.Ok(new CrossReferenceResponse(SourceSystems.Jira, key, dir, references));
        });
    }

    // ── Query / Filter ───────────────────────────────────────────────

    private static void MapQueryEndpoints(RouteGroupBuilder api)
    {
        api.MapPost("/query", (JiraQueryRequest request, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            (string sql, List<SqliteParameter> parameters) = JiraQueryBuilder.Build(request);

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

            List<JiraIssueSummaryEntry> results = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string key = reader["Key"]?.ToString() ?? "";
                DateTimeOffset? updatedAt = null;
                if (reader["UpdatedAt"] is string updatedStr &&
                    DateTimeOffset.TryParse(updatedStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset updated))
                {
                    updatedAt = updated;
                }

                results.Add(new JiraIssueSummaryEntry
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
                    UpdatedAt = updatedAt,
                });
            }

            return Results.Ok(results);
        });

        api.MapGet("/work-groups/{group}", (string group, int? limit, int? offset, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            int maxResults = Math.Min(limit ?? 50, 500);
            int skip = Math.Max(offset ?? 0, 0);

            using SqliteCommand cmd = new SqliteCommand(
                "SELECT Key, ProjectKey, Title, Type, Status, Priority, WorkGroup, Specification, UpdatedAt FROM jira_issues WHERE WorkGroup = @wg ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
                connection);
            cmd.Parameters.AddWithValue("@wg", group);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            cmd.Parameters.AddWithValue("@offset", skip);

            List<JiraIssueSummaryEntry> results = ReadIssueSummaries(cmd, options);
            return Results.Ok(results);
        });

        api.MapGet("/specifications/{spec}", (string spec, int? limit, int? offset, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            int maxResults = Math.Min(limit ?? 50, 500);
            int skip = Math.Max(offset ?? 0, 0);

            using SqliteCommand cmd = new SqliteCommand(
                "SELECT Key, ProjectKey, Title, Type, Status, Priority, WorkGroup, Specification, UpdatedAt FROM jira_issues WHERE Specification = @spec ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
                connection);
            cmd.Parameters.AddWithValue("@spec", spec);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            cmd.Parameters.AddWithValue("@offset", skip);

            List<JiraIssueSummaryEntry> results = ReadIssueSummaries(cmd, options);
            return Results.Ok(results);
        });

        api.MapGet("/spec-artifacts", (string? family, JiraDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            string sql = "SELECT Family, SpecKey, SpecName, GitUrl, PublishedUrl, DefaultWorkgroup FROM jira_spec_artifacts";
            if (!string.IsNullOrEmpty(family))
                sql += " WHERE Family = @family";
            sql += " ORDER BY Family, SpecKey";

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            if (!string.IsNullOrEmpty(family))
                cmd.Parameters.AddWithValue("@family", family);

            List<SpecArtifactEntry> results = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SpecArtifactEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            return Results.Ok(results);
        });

        api.MapGet("/issue-numbers", (string? project, JiraDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            string sql = "SELECT Key FROM jira_issues";
            if (!string.IsNullOrEmpty(project))
                sql += " WHERE ProjectKey = @project";

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            if (!string.IsNullOrEmpty(project))
                cmd.Parameters.AddWithValue("@project", project);

            List<int> issueNumbers = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                int dashIndex = key.LastIndexOf('-');
                if (dashIndex >= 0 && int.TryParse(key.AsSpan(dashIndex + 1), out int number))
                    issueNumbers.Add(number);
            }

            return Results.Ok(new IssueNumbersResponse(issueNumbers));
        });
    }

    // ── Ingestion ────────────────────────────────────────────────────

    private static void MapIngestionEndpoints(RouteGroupBuilder api)
    {
        api.MapPost("/ingest", async (HttpRequest req, JiraIngestionPipeline pipeline) =>
        {
            string type = req.Query["type"].FirstOrDefault() ?? "incremental";
            try
            {
                IngestionResult result = type == "full"
                    ? await pipeline.RunFullIngestionAsync(ct: req.HttpContext.RequestAborted)
                    : await pipeline.RunIncrementalIngestionAsync(req.HttpContext.RequestAborted);

                return Results.Ok(new
                {
                    result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated, result.ItemsFailed,
                    errors = result.Errors,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        api.MapPost("/rebuild", async (JiraIngestionPipeline pipeline) =>
        {
            try
            {
                IngestionResult result = await pipeline.RebuildFromCacheAsync();
                return Results.Ok(new RebuildResponse(true, result.ItemsProcessed, 0, null));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        api.MapPost("/rebuild-index", (
            HttpRequest req,
            IngestionWorkQueue workQueue,
            JiraDatabase database,
            JiraIndexBuilder indexBuilder,
            JiraXRefRebuilder xrefRebuilder,
            JiraIndexer indexer,
            IIndexTracker indexTracker) =>
        {
            string indexType = (req.Query["type"].FirstOrDefault() ?? "all").ToLowerInvariant();

            workQueue.Enqueue(ct =>
            {
                RebuildIndexByType(indexType, database, indexBuilder, xrefRebuilder, indexer, indexTracker, ct);
                return Task.CompletedTask;
            }, $"rebuild-index-{indexType}");

            return Results.Ok(new RebuildIndexResponse(true, $"queued {indexType} index rebuild", null, null));
        });

        api.MapPost("/notify-peer", (PeerIngestionNotification notification, IngestionWorkQueue workQueue, JiraXRefRebuilder xrefRebuilder) =>
        {
            workQueue.Enqueue(ct =>
            {
                xrefRebuilder.RebuildAll(ct);
                return Task.CompletedTask;
            }, "rebuild-xrefs");

            return Results.Ok(new PeerIngestionAck(true));
        });
    }

    // ── Lifecycle / Status ───────────────────────────────────────────

    private static void MapLifecycleEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/status", (JiraIngestionPipeline pipeline, JiraDatabase db, IIndexTracker indexTracker) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            JiraSyncStateRecord? syncState = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);

            IngestionStatusResponse status = new IngestionStatusResponse(
                SourceSystems.Jira,
                pipeline.IsRunning ? pipeline.CurrentStatus : (syncState?.Status ?? "unknown"),
                syncState?.LastSyncAt,
                syncState?.ItemsIngested ?? 0,
                0,
                syncState?.LastError,
                pipeline.IsRunning ? pipeline.CurrentStatus : null,
                HttpServiceLifecycle.ToIndexStatuses(indexTracker.GetAllStatuses()));

            return Results.Ok(status);
        });

        api.MapGet("/stats", (JiraDatabase db, IResponseCache cache) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            int issueCount = JiraIssueRecord.SelectCount(connection);
            int commentCount = JiraCommentRecord.SelectCount(connection);
            int linkCount = JiraIssueLinkRecord.SelectCount(connection);
            int specCount = JiraSpecArtifactRecord.SelectCount(connection);
            long dbSize = db.GetDatabaseSizeBytes();
            CacheStats cacheStats = cache.GetStats(JiraCacheLayout.SourceName);

            JiraSyncStateRecord? syncState = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);

            return Results.Ok(new StatsResponse
            {
                Source = SourceSystems.Jira,
                TotalItems = issueCount,
                TotalComments = commentCount,
                DatabaseSizeBytes = dbSize,
                CacheSizeBytes = cacheStats.TotalBytes,
                CacheFiles = cacheStats.FileCount,
                LastSyncAt = syncState?.LastSyncAt,
                AdditionalCounts = new Dictionary<string, int>
                {
                    ["issue_links"] = linkCount,
                    ["spec_artifacts"] = specCount,
                },
            });
        });

        api.MapGet("/health", (JiraDatabase db, JiraIngestionPipeline pipeline) =>
        {
            return Results.Ok(HttpServiceLifecycle.BuildHealthCheck(db, pipeline));
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static DateTimeOffset? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        string str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
            ? dt
            : null;
    }

    private static List<JiraIssueSummaryEntry> ReadIssueSummaries(SqliteCommand cmd, JiraServiceOptions options)
    {
        List<JiraIssueSummaryEntry> results = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader["Key"]?.ToString() ?? "";
            DateTimeOffset? updatedAt = null;
            if (reader["UpdatedAt"] is string updatedStr &&
                DateTimeOffset.TryParse(updatedStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset updated))
            {
                updatedAt = updated;
            }

            results.Add(new JiraIssueSummaryEntry
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
                UpdatedAt = updatedAt,
            });
        }
        return results;
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

    private static void RebuildIndexByType(
        string indexType,
        JiraDatabase database,
        JiraIndexBuilder indexBuilder,
        JiraXRefRebuilder xrefRebuilder,
        JiraIndexer indexer,
        IIndexTracker indexTracker,
        CancellationToken ct)
    {
        switch (indexType)
        {
            case "lookup-tables":
                RebuildSingleIndex("lookup-tables", indexTracker, () =>
                {
                    using SqliteConnection conn = database.OpenConnection();
                    indexBuilder.RebuildIndexTables(conn);
                });
                break;
            case "cross-refs":
                RebuildSingleIndex("cross-refs", indexTracker, () => xrefRebuilder.RebuildAll(ct));
                break;
            case "bm25":
                RebuildSingleIndex("bm25", indexTracker, () => indexer.RebuildFullIndex(ct));
                break;
            case "fts":
                RebuildSingleIndex("fts", indexTracker, () => database.RebuildFtsIndexes());
                break;
            case "all":
                RebuildSingleIndex("lookup-tables", indexTracker, () =>
                {
                    using SqliteConnection conn = database.OpenConnection();
                    indexBuilder.RebuildIndexTables(conn);
                });
                RebuildSingleIndex("cross-refs", indexTracker, () => xrefRebuilder.RebuildAll(ct));
                RebuildSingleIndex("bm25", indexTracker, () => indexer.RebuildFullIndex(ct));
                RebuildSingleIndex("fts", indexTracker, () => database.RebuildFtsIndexes());
                break;
        }
    }

    private static void RebuildSingleIndex(string name, IIndexTracker tracker, Action action)
    {
        tracker.MarkStarted(name);
        try
        {
            action();
            tracker.MarkCompleted(name);
        }
        catch (Exception ex)
        {
            tracker.MarkFailed(name, ex.Message);
            throw;
        }
    }
}
