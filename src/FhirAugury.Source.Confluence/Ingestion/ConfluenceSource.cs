using System.Text;
using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Text;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static FhirAugury.Common.DateTimeHelper;
using static FhirAugury.Common.JsonElementHelper;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// Acquires Confluence content by reconciliation — discover, sweep, reconcile,
/// fill, tombstone, replay — and materializes the database from the cache.
/// </summary>
/// <remarks>
/// <para>
/// There is no watermark and no full/incremental split. What still needs
/// fetching is a pure function of (manifest, cache tree), so an interrupted run
/// simply leaves more to do and the next run picks it up. Runs converge.
/// </para>
/// <para>
/// Download writes <b>only</b> to the cache; replay is the only path that writes
/// the database. That is what makes cache self-sufficiency provable rather than
/// asserted.
/// </para>
/// </remarks>
public class ConfluenceSource
{
    /// <summary>The source name used for cache and sync-state rows.</summary>
    public const string SourceName = SourceSystems.Confluence;

    /// <summary>
    /// Sync-state sub-source written <b>only</b> by network reconciliation.
    /// </summary>
    /// <remarks>
    /// Scheduling reads this row exactly. Previously the lookup had no
    /// <c>SubSource</c> filter, so a cache-only rebuild could satisfy
    /// <c>MinSyncAge</c> and silently suppress the next network sync.
    /// </remarks>
    public const string SchedulingSubSource = "reconcile";

    private readonly ConfluenceServiceOptions _options;
    private readonly ConfluenceDatabase _database;
    private readonly IResponseCache _cache;
    private readonly ConfluenceSpaceDiscovery _discovery;
    private readonly ConfluenceSweep _sweep;
    private readonly ILogger<ConfluenceSource> _logger;
    private readonly ConfluenceFetch _fetch;
    private readonly ConfluenceBlobFetch _fetchBlob;

    public ConfluenceSource(
        IOptions<ConfluenceServiceOptions> optionsAccessor,
        IHttpClientFactory httpClientFactory,
        ConfluenceDatabase database,
        IResponseCache cache,
        ConfluenceSpaceDiscovery discovery,
        ConfluenceSweep sweep,
        ILogger<ConfluenceSource> logger)
        : this(optionsAccessor, database, cache, discovery, sweep, logger,
            ConfluenceHttp.CreateFetch(httpClientFactory, optionsAccessor.Value),
            ConfluenceHttp.CreateBlobFetch(httpClientFactory, optionsAccessor.Value))
    {
    }

    /// <summary>Test seam: supply the fetches directly.</summary>
    public ConfluenceSource(
        IOptions<ConfluenceServiceOptions> optionsAccessor,
        ConfluenceDatabase database,
        IResponseCache cache,
        ConfluenceSpaceDiscovery discovery,
        ConfluenceSweep sweep,
        ILogger<ConfluenceSource> logger,
        ConfluenceFetch fetch,
        ConfluenceBlobFetch? fetchBlob = null)
    {
        _options = optionsAccessor.Value;
        _database = database;
        _cache = cache;
        _discovery = discovery;
        _sweep = sweep;
        _logger = logger;
        _fetch = fetch;
        _fetchBlob = fetchBlob
            ?? ((_, _, _) => throw new InvalidOperationException("No blob fetch was configured."));
    }

    /// <summary>
    /// One convergent pass: discover spaces, sweep them, reconcile against the
    /// cache, fill the gaps, tombstone what disappeared, then replay.
    /// </summary>
    public async Task<IngestionResult> ReconcileAsync(ConfluenceReconcilePolicy policy, CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        List<string> errors = [];
        int fetched = 0;
        int failed = 0;

        ConfluenceSpaceCatalog catalog = await _discovery.DiscoverAsync(ct);

        foreach (string spaceKey in catalog.Keys)
        {
            if (ct.IsCancellationRequested) break;

            ConfluenceSweepResult result = await _sweep.SweepSpaceAsync(spaceKey, ct);
            if (!result.Succeeded)
            {
                errors.Add($"sweep:{spaceKey}: {result.Error}");
            }
        }

        foreach (string spaceKey in catalog.Keys)
        {
            if (ct.IsCancellationRequested) break;

            ConfluenceReconcilePlan plan = ConfluenceReconciler.Reconcile(spaceKey, _cache, policy);
            (int spaceFetched, int spaceFailed) = await FillAsync(spaceKey, plan, errors, ct);
            fetched += spaceFetched;
            failed += spaceFailed;
        }

        // Tombstoning runs only after every space has been swept and filled: a
        // page that moves from space A to space B leaves A's manifest and joins
        // B's, and tombstoning A's copy mid-run would discard content B has not
        // yet fetched.
        int tombstoned = await TombstoneAsync(catalog, ct);

        IngestionResult replay = await LoadFromCacheAsync(ct);
        await WriteCacheMetadataAsync(catalog, ct);

        _logger.LogInformation(
            "Confluence reconcile complete: {Spaces} spaces, {Fetched} fetched, {Failed} failed, {Tombstoned} tombstoned",
            catalog.Spaces.Count, fetched, failed, tombstoned);

        return new IngestionResult(
            replay.ItemsProcessed,
            replay.ItemsNew,
            replay.ItemsUpdated,
            failed + replay.ItemsFailed,
            [.. errors, .. replay.Errors],
            startedAt)
        {
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Computes the standing verdict for every catalogued space.</summary>
    public IReadOnlyList<ConfluenceReconcilePlan> ReconcileReport(ConfluenceReconcilePolicy policy)
    {
        ConfluenceSpaceCatalog? catalog = ConfluenceReconciler.ReadSpaceCatalog(_cache);
        if (catalog is null)
        {
            return [];
        }

        return [.. catalog.Keys.Select(key => ConfluenceReconciler.Reconcile(key, _cache, policy))];
    }

    /// <summary>The reconcile policy implied by current configuration.</summary>
    public ConfluenceReconcilePolicy BuildPolicy(bool forceRefetchAll = false) => new()
    {
        AttachmentMaxBytes = _options.AttachmentMaxBytes,
        ForceRefetchAll = forceRefetchAll,
    };

    // ── Fill ──────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches everything the plan says is missing or stale, one independently
    /// retryable unit at a time, writing only to the cache.
    /// </summary>
    private async Task<(int Fetched, int Failed)> FillAsync(
        string spaceKey, ConfluenceReconcilePlan plan, List<string> errors, CancellationToken ct)
    {
        int fetched = 0;
        int failed = 0;

        foreach (ConfluenceReconcileItem item in plan.ToFetch)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await FillItemAsync(spaceKey, item, ct);
                fetched++;
            }
            catch (Exception ex)
            {
                // An expired mid-run credential aborts the run rather than
                // producing thousands of per-item failures that a later
                // reconcile would misread as mass deletion.
                ConfluenceAuthFailure.ThrowIfAuthFailure(ex);

                failed++;
                errors.Add($"fill:{spaceKey}:{item.Entry.Type}:{item.Entry.Id}: {ex.Message}");
                _logger.LogWarning(ex, "Failed to fill {Type} {Id} in space {SpaceKey}",
                    item.Entry.Type, item.Entry.Id, spaceKey);
            }
        }

        foreach (ConfluenceReconcileItem item in plan.BlobsToFetch)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                if (await FillBlobAsync(item, ct))
                {
                    fetched++;
                }
            }
            catch (Exception ex)
            {
                ConfluenceAuthFailure.ThrowIfAuthFailure(ex);

                failed++;
                errors.Add($"blob:{spaceKey}:{item.Entry.Id}: {ex.Message}");
                _logger.LogWarning(ex, "Failed to download attachment bytes for {Id} in space {SpaceKey}",
                    item.Entry.Id, spaceKey);
            }
        }

        return (fetched, failed);
    }

    private async Task FillItemAsync(string spaceKey, ConfluenceReconcileItem item, CancellationToken ct)
    {
        string profile = ConfluenceCacheLayout.GetProfile(item.Entry.Type);
        string url = $"{_options.BaseUrl}/rest/api/content/{Uri.EscapeDataString(item.Entry.Id)}" +
                     $"?expand={Uri.EscapeDataString(profile)}";

        // Archived content needs an explicit status. Without this, an archived
        // entry would be enumerated by the sweep and then fail forever in the
        // fill, leaving it permanently Missing.
        if (item.Entry.IsArchived)
        {
            url += "&status=archived";
        }

        string json = await _fetch(url, ct);

        using JsonDocument document = JsonDocument.Parse(json);
        ConfluenceCachedArtifact artifact = ConfluenceCachedArtifact.Wrap(
            document.RootElement,
            item.Entry.Type,
            spaceKey,
            item.Entry.Version,
            item.Entry.FileSize);

        await WriteCacheAsync(item.CacheKey, artifact.ToJson(), ct);
    }

    /// <summary>
    /// Downloads an attachment's bytes straight to the cache.
    /// </summary>
    /// <remarks>
    /// The cap is enforced <b>on the wire</b>, not only from the manifest:
    /// <c>extensions.fileSize</c> is preflight metadata that can be absent,
    /// zero, or simply wrong, so a manifest-only gate would let an unbounded
    /// blob through. The transfer is rejected on <c>Content-Length</c> when that
    /// is available and aborted mid-copy by a counting stream when it is not.
    /// </remarks>
    private async Task<bool> FillBlobAsync(ConfluenceReconcileItem item, CancellationToken ct)
    {
        if (item.BlobCacheKey is null || string.IsNullOrEmpty(item.Entry.DownloadPath))
        {
            return false;
        }

        string url = item.Entry.DownloadPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? item.Entry.DownloadPath
            : $"{_options.BaseUrl.TrimEnd('/')}/{item.Entry.DownloadPath.TrimStart('/')}";

        return await _fetchBlob(url, _options.AttachmentMaxBytes, async stream =>
        {
            await _cache.PutAsync(SourceName, item.BlobCacheKey, stream, ct);
        });
    }

    // ── Tombstones ────────────────────────────────────────────────────

    /// <summary>
    /// Moves every cached artifact that its space's manifest no longer claims
    /// under <c>_vanished/</c>. Nothing is ever hard-deleted: absence can also
    /// mean "not visible to the credential this run used", and a
    /// permission-scoped false positive must not cost bytes already paid for.
    /// </summary>
    private async Task<int> TombstoneAsync(ConfluenceSpaceCatalog catalog, CancellationToken ct)
    {
        HashSet<string> tracked = new(catalog.Keys, StringComparer.Ordinal);
        int moved = 0;

        // Untracked spaces are swept up too; otherwise a space dropped from
        // configuration keeps its content live in the cache forever.
        List<string> spacesOnDisk = [.. EnumerateCachedSpaceKeys().Union(tracked, StringComparer.Ordinal)];

        foreach (string spaceKey in spacesOnDisk)
        {
            if (ct.IsCancellationRequested) break;

            bool isTracked = tracked.Contains(spaceKey);
            ConfluenceManifest? manifest = isTracked
                ? ConfluenceReconciler.ReadManifest(spaceKey, _cache)
                : null;

            // A tracked space with no trustworthy manifest is left alone:
            // "we do not know" must never be read as "it is gone".
            if (isTracked && manifest is not { Complete: true })
            {
                continue;
            }

            HashSet<string> claimed = manifest is null ? [] : ClaimedKeys(spaceKey, manifest);

            foreach (string key in ConfluenceReconciler.EnumerateContentKeys(spaceKey, _cache).ToList())
            {
                if (claimed.Contains(key)) continue;

                await MoveToVanishedAsync(key, ct);
                moved++;
            }
        }

        return moved;
    }

    private static HashSet<string> ClaimedKeys(string spaceKey, ConfluenceManifest manifest)
    {
        HashSet<string> claimed = new(StringComparer.Ordinal);

        foreach (ConfluenceManifestEntry entry in manifest.Entries)
        {
            claimed.Add(ConfluenceCacheLayout.GetCacheKey(entry.Type, spaceKey, entry.Id));
            if (entry.Type == ContentTypes.Attachment)
            {
                claimed.Add(ConfluenceCacheLayout.GetAttachmentBlobCacheKey(spaceKey, entry.Id));
            }
        }

        return claimed;
    }

    private async Task MoveToVanishedAsync(string key, CancellationToken ct)
    {
        if (!_cache.TryGet(SourceName, key, out Stream? stream))
        {
            return;
        }

        using (stream)
        {
            await _cache.PutAsync(SourceName, ConfluenceCacheLayout.GetVanishedCacheKey(key), stream, ct);
        }

        _cache.Remove(SourceName, key);
    }

    private HashSet<string> EnumerateCachedSpaceKeys()
    {
        HashSet<string> keys = new(StringComparer.Ordinal);

        foreach (string key in _cache.EnumerateKeys(SourceName, ConfluenceCacheLayout.SpacesSegment))
        {
            string[] segments = key.Split('/');
            if (segments.Length > 2 && segments[0] == ConfluenceCacheLayout.SpacesSegment)
            {
                keys.Add(segments[1]);
            }
        }

        return keys;
    }

    // ── Replay ────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the database from the cache, driven by the space catalog and
    /// each space's manifest.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> <c>cache.EnumerateKeys</c>-driven.
    /// <c>FileSystemResponseCache</c> filters metadata files by <em>file name</em>
    /// only, so its <c>_meta_</c> exclusion does not cover anything under
    /// <c>_vanished/</c>. Iterating manifests is what keeps tombstones out of
    /// the database.
    /// </remarks>
    public Task<IngestionResult> LoadFromCacheAsync(CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = [];

        ConfluenceSpaceCatalog? catalog = ConfluenceReconciler.ReadSpaceCatalog(_cache);
        if (catalog is null)
        {
            _logger.LogWarning("No Confluence space catalog on disk; nothing to replay");
            return Task.FromResult(
                new IngestionResult(0, 0, 0, 0, errors, startedAt) { CompletedAt = DateTimeOffset.UtcNow });
        }

        using SqliteConnection connection = _database.OpenConnection();
        HashSet<string> allManifestPageIds = new(StringComparer.Ordinal);
        HashSet<string> allManifestAttachmentIds = new(StringComparer.Ordinal);

        foreach (string spaceKey in catalog.Keys)
        {
            if (ct.IsCancellationRequested) break;

            UpsertSpaceFromCache(connection, spaceKey);

            ConfluenceManifest? manifest = ConfluenceReconciler.ReadManifest(spaceKey, _cache);
            if (manifest is null)
            {
                _logger.LogDebug("Space {SpaceKey} has no manifest; skipping replay for it", spaceKey);
                continue;
            }

            foreach (ConfluenceManifestEntry entry in manifest.OfType(ContentTypes.Page))
            {
                if (ct.IsCancellationRequested) break;

                allManifestPageIds.Add(entry.Id);

                using JsonDocument? document = TryReadPayload(
                    ConfluenceCacheLayout.GetPageCacheKey(spaceKey, entry.Id));

                if (document is null) continue;

                switch (ProcessPage(document.RootElement, spaceKey, entry, connection))
                {
                    case PageResult.New: itemsNew++; itemsProcessed++; break;
                    case PageResult.Updated: itemsUpdated++; itemsProcessed++; break;
                    default:
                        itemsFailed++;
                        errors.Add($"replay:{spaceKey}:page:{entry.Id}");
                        break;
                }
            }

            ReplayComments(connection, spaceKey, manifest, ct);
            ReplayAttachments(connection, spaceKey, manifest, allManifestAttachmentIds, ct);
        }

        // Deletion runs after every space is materialized, so a page that moved
        // between two tracked spaces is re-homed rather than mistaken for a
        // disappearance.
        DeleteAbsent(connection, catalog, allManifestPageIds, allManifestAttachmentIds, ct);

        _logger.LogInformation(
            "Confluence replay complete: {Processed} pages, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return Task.FromResult(
            new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt)
            {
                CompletedAt = DateTimeOffset.UtcNow,
            });
    }

    /// <summary>
    /// Replaces a page's comment set — but only when it actually changed.
    /// </summary>
    /// <remarks>
    /// Comments carry no Confluence identity column, so keeping replay
    /// idempotent without a schema change means deleting by
    /// <c>ConfluencePageId</c> and re-inserting in ascending Confluence comment
    /// id order. Doing that unconditionally would reshuffle the database ids
    /// that <c>PagesController</c> exposes on every single replay, so an
    /// unchanged set is detected and left alone. That both keeps ids stable for
    /// untouched pages and avoids rewriting every comment row on each pass.
    /// </remarks>
    private void ReplayComments(
        SqliteConnection connection, string spaceKey, ConfluenceManifest manifest, CancellationToken ct)
    {
        IEnumerable<IGrouping<string, ConfluenceManifestEntry>> byPage = manifest
            .OfType(ContentTypes.Comment)
            .Where(e => !string.IsNullOrEmpty(e.ContainerId))
            .GroupBy(e => e.ContainerId!, StringComparer.Ordinal);

        foreach (IGrouping<string, ConfluenceManifestEntry> group in byPage)
        {
            if (ct.IsCancellationRequested) break;

            ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: group.Key);
            if (page is null) continue;

            List<ConfluenceCommentRecord> desired = [];

            foreach (ConfluenceManifestEntry entry in group.OrderBy(e => e.Id, StringComparer.Ordinal))
            {
                using JsonDocument? document = TryReadPayload(
                    ConfluenceCacheLayout.GetCommentCacheKey(spaceKey, entry.Id));

                if (document is null) continue;

                JsonElement root = document.RootElement;
                desired.Add(new ConfluenceCommentRecord
                {
                    Id = 0,
                    PageId = page.Id,
                    ConfluencePageId = group.Key,
                    Author = GetNestedString(root, "version", "by", "displayName") ?? "unknown",
                    CreatedAt = ParseDate(GetNestedString(root, "version", "when")),
                    Body = ConfluenceContentParser.ToPlainText(
                        GetNestedString(root, "body", "storage", "value")),
                });
            }

            if (IsUnchanged(connection, group.Key, page.Id, desired))
            {
                continue;
            }

            DeleteCommentsForPage(connection, group.Key);

            foreach (ConfluenceCommentRecord record in desired)
            {
                record.Id = ConfluenceCommentRecord.GetIndex();
            }

            desired.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
        }
    }

    private static bool IsUnchanged(
        SqliteConnection connection,
        string confluencePageId,
        int pageId,
        List<ConfluenceCommentRecord> desired)
    {
        List<ConfluenceCommentRecord> existing =
            [.. ConfluenceCommentRecord.SelectList(connection, ConfluencePageId: confluencePageId)
                .OrderBy(c => c.Id)];

        if (existing.Count != desired.Count)
        {
            return false;
        }

        for (int i = 0; i < existing.Count; i++)
        {
            if (existing[i].PageId != pageId
                || existing[i].Author != desired[i].Author
                || existing[i].CreatedAt != desired[i].CreatedAt
                || existing[i].Body != desired[i].Body)
            {
                return false;
            }
        }

        return true;
    }

    private static void DeleteCommentsForPage(SqliteConnection connection, string confluencePageId)
    {
        // Deliberately not the generated single-value Delete overload: it never
        // binds its parameter (see GitHubBackfillCheckpointStore.DeleteRow).
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM confluence_comments WHERE ConfluencePageId = @pageId";
        cmd.Parameters.AddWithValue("@pageId", confluencePageId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Upserts one row per attachment the manifest names — <b>including</b>
    /// those whose bytes were skipped by policy, whose row records the size and
    /// download URL with a null <c>CacheKey</c> so an oversized attachment stays
    /// discoverable and fetchable by hand.
    /// </summary>
    private void ReplayAttachments(
        SqliteConnection connection,
        string spaceKey,
        ConfluenceManifest manifest,
        HashSet<string> seenIds,
        CancellationToken ct)
    {
        foreach (ConfluenceManifestEntry entry in manifest.OfType(ContentTypes.Attachment))
        {
            if (ct.IsCancellationRequested) break;

            seenIds.Add(entry.Id);

            if (string.IsNullOrEmpty(entry.ContainerId)) continue;

            ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(
                connection, ConfluenceId: entry.ContainerId);
            if (page is null) continue;

            string blobKey = ConfluenceCacheLayout.GetAttachmentBlobCacheKey(spaceKey, entry.Id);
            bool hasBlob = _cache.TryGet(SourceName, blobKey, out Stream? blob);
            blob?.Dispose();

            ConfluenceAttachmentRecord record = new()
            {
                Id = ConfluenceAttachmentRecord.GetIndex(),
                PageId = page.Id,
                ConfluencePageId = entry.ContainerId,
                ConfluenceAttachmentId = entry.Id,
                FileName = entry.Title,
                MediaType = entry.MediaType,
                FileSizeBytes = entry.FileSize,
                VersionNumber = entry.Version,
                CreatedAt = entry.When ?? DateTimeOffset.MinValue,
                DownloadUrl = string.IsNullOrEmpty(entry.DownloadPath)
                    ? null
                    : $"{_options.BaseUrl}{entry.DownloadPath}",
                CacheKey = hasBlob ? blobKey : null,
            };

            ConfluenceAttachmentRecord? existing = ConfluenceAttachmentRecord.SelectSingle(
                connection, ConfluenceAttachmentId: entry.Id);

            if (existing is not null)
            {
                record.Id = existing.Id;
                ConfluenceAttachmentRecord.Update(connection, record);
            }
            else
            {
                ConfluenceAttachmentRecord.Insert(connection, record, ignoreDuplicates: true);
            }
        }
    }

    /// <summary>Removes rows for content no manifest names any more.</summary>
    /// <remarks>
    /// Without this, replay would be upsert-only and a tombstoned page would
    /// stay queryable and indexed forever — the disappearance goal met in the
    /// cache and silently missed in the database.
    /// </remarks>
    private static void DeleteAbsent(
        SqliteConnection connection,
        ConfluenceSpaceCatalog catalog,
        HashSet<string> manifestPageIds,
        HashSet<string> manifestAttachmentIds,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        using SqliteTransaction transaction = connection.BeginTransaction();

        try
        {
            CreateIdTable(connection, transaction, "manifest_page_ids", manifestPageIds);
            CreateIdTable(connection, transaction, "manifest_attachment_ids", manifestAttachmentIds);
            CreateIdTable(connection, transaction, "catalog_space_keys", catalog.Keys);

            Execute(connection, transaction, """
                CREATE TEMP TABLE vanished_pages AS
                    SELECT ConfluenceId FROM confluence_pages
                    WHERE ConfluenceId NOT IN (SELECT Id FROM manifest_page_ids);

                DELETE FROM confluence_comments
                    WHERE ConfluencePageId IN (SELECT ConfluenceId FROM vanished_pages);

                DELETE FROM confluence_attachments
                    WHERE ConfluenceAttachmentId NOT IN (SELECT Id FROM manifest_attachment_ids)
                       OR ConfluencePageId IN (SELECT ConfluenceId FROM vanished_pages);

                DELETE FROM confluence_page_links
                    WHERE SourcePageId IN (SELECT ConfluenceId FROM vanished_pages)
                       OR TargetPageId IN (SELECT ConfluenceId FROM vanished_pages);

                DELETE FROM xref_jira
                    WHERE ContentType = 'page' AND SourceId IN (SELECT ConfluenceId FROM vanished_pages);
                DELETE FROM xref_zulip
                    WHERE ContentType = 'page' AND SourceId IN (SELECT ConfluenceId FROM vanished_pages);
                DELETE FROM xref_github
                    WHERE ContentType = 'page' AND SourceId IN (SELECT ConfluenceId FROM vanished_pages);
                DELETE FROM xref_fhir_element
                    WHERE ContentType = 'page' AND SourceId IN (SELECT ConfluenceId FROM vanished_pages);

                DELETE FROM confluence_pages
                    WHERE ConfluenceId IN (SELECT ConfluenceId FROM vanished_pages);

                DELETE FROM confluence_spaces
                    WHERE Key NOT IN (SELECT Id FROM catalog_space_keys);

                DROP TABLE vanished_pages;
                DROP TABLE manifest_page_ids;
                DROP TABLE manifest_attachment_ids;
                DROP TABLE catalog_space_keys;
                """);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void CreateIdTable(
        SqliteConnection connection, SqliteTransaction transaction, string name, IEnumerable<string> ids)
    {
        Execute(connection, transaction, $"CREATE TEMP TABLE {name} (Id TEXT PRIMARY KEY);");

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"INSERT OR IGNORE INTO {name} (Id) VALUES (@id)";
        SqliteParameter parameter = cmd.Parameters.Add("@id", SqliteType.Text);

        foreach (string id in ids)
        {
            parameter.Value = id;
            cmd.ExecuteNonQuery();
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ── Materialization ───────────────────────────────────────────────

    private PageResult ProcessPage(
        JsonElement pageJson, string spaceKey, ConfluenceManifestEntry entry, SqliteConnection connection)
    {
        try
        {
            string pageId = GetString(pageJson, "id") ?? entry.Id;
            string title = GetString(pageJson, "title") ?? entry.Title;

            string? bodyStorage = GetNestedString(pageJson, "body", "storage", "value");
            string bodyPlain = ConfluenceContentParser.ToPlainText(bodyStorage);

            ConfluencePageRecord record = new()
            {
                Id = ConfluencePageRecord.GetIndex(),
                ConfluenceId = pageId,
                SpaceKey = spaceKey,
                Title = title,
                Status = entry.Status,
                ParentId = ReadParentId(pageJson),
                BodyStorage = bodyStorage,
                BodyPlain = bodyPlain,
                Labels = ReadLabels(pageJson),
                VersionNumber = ReadVersionNumber(pageJson, entry),
                LastModifiedBy = GetNestedString(pageJson, "version", "by", "displayName"),
                LastModifiedAt = ParseDate(GetNestedString(pageJson, "version", "when")),
                Url = ReadUrl(pageJson, pageId),
            };

            ConfluencePageRecord? existing = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
            bool isNew;

            if (existing is not null)
            {
                record.Id = existing.Id;
                ConfluencePageRecord.Update(connection, record);
                isNew = false;
            }
            else
            {
                ConfluencePageRecord.Insert(connection, record, ignoreDuplicates: true);
                isNew = true;
            }

            ReplacePageLinks(connection, pageId, bodyStorage);

            // The inline xref_* extraction that used to live here was dead work:
            // ConfluenceXRefRebuilder.RebuildAll, called from PostIngestion in the
            // same run, opens by deleting all four tables and re-deriving them.
            return isNew ? PageResult.New : PageResult.Updated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process page {PageId}", entry.Id);
            return PageResult.Failed;
        }
    }

    private static void ReplacePageLinks(SqliteConnection connection, string pageId, string? bodyStorage)
    {
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM confluence_page_links WHERE SourcePageId = @pageId";
            cmd.Parameters.AddWithValue("@pageId", pageId);
            cmd.ExecuteNonQuery();
        }

        List<ConfluencePageLinkRecord> toInsert =
        [
            .. ConfluenceLinkExtractor.ExtractLinks(bodyStorage)
                .Select(link => new ConfluencePageLinkRecord
                {
                    Id = ConfluencePageLinkRecord.GetIndex(),
                    SourcePageId = pageId,
                    TargetPageId = link.TargetPageId,
                    LinkType = link.LinkType,
                }),
        ];

        toInsert.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
    }

    private static string? ReadParentId(JsonElement pageJson) =>
        pageJson.TryGetProperty("ancestors", out JsonElement ancestors)
        && ancestors.ValueKind == JsonValueKind.Array
        && ancestors.GetArrayLength() > 0
            ? GetString(ancestors[ancestors.GetArrayLength() - 1], "id")
            : null;

    private static string? ReadLabels(JsonElement pageJson)
    {
        if (!pageJson.TryGetProperty("metadata", out JsonElement metadata)
            || !metadata.TryGetProperty("labels", out JsonElement labels)
            || !labels.TryGetProperty("results", out JsonElement results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string> names = [.. results.EnumerateArray()
            .Select(label => GetString(label, "name"))
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()];

        return names.Count > 0 ? string.Join(",", names) : null;
    }

    private static int ReadVersionNumber(JsonElement pageJson, ConfluenceManifestEntry entry) =>
        pageJson.TryGetProperty("version", out JsonElement version)
        && version.TryGetProperty("number", out JsonElement number)
        && number.TryGetInt32(out int parsed)
            ? parsed
            : entry.Version;

    private string ReadUrl(JsonElement pageJson, string pageId) =>
        GetNestedString(pageJson, "_links", "webui") is { Length: > 0 } webui
            ? $"{_options.BaseUrl}{webui}"
            : $"{_options.BaseUrl}/pages/{pageId}";

    private void UpsertSpaceFromCache(SqliteConnection connection, string spaceKey)
    {
        using JsonDocument? document = TryReadJson(ConfluenceCacheLayout.GetSpaceCacheKey(spaceKey));

        ConfluenceSpaceRecord record = new()
        {
            Id = ConfluenceSpaceRecord.GetIndex(),
            Key = spaceKey,
            Name = document is null ? spaceKey : GetString(document.RootElement, "name") ?? spaceKey,
            Description = document is null
                ? null
                : GetNestedString(document.RootElement, "description", "plain", "value"),
            Url = $"{_options.BaseUrl}/display/{spaceKey}",
            LastFetchedAt = DateTimeOffset.UtcNow,
        };

        ConfluenceSpaceRecord? existing = ConfluenceSpaceRecord.SelectSingle(connection, Key: spaceKey);
        if (existing is not null)
        {
            record.Id = existing.Id;
            ConfluenceSpaceRecord.Update(connection, record);
        }
        else
        {
            ConfluenceSpaceRecord.Insert(connection, record, ignoreDuplicates: true);
        }
    }

    // ── Cache I/O ─────────────────────────────────────────────────────

    /// <summary>
    /// Re-written at the end of <b>every</b> run, with <c>TotalFiles</c> derived
    /// by summing the manifests rather than counted by the writer, so it cannot
    /// drift from the cache it describes.
    /// </summary>
    private async Task WriteCacheMetadataAsync(ConfluenceSpaceCatalog catalog, CancellationToken ct)
    {
        int totalFiles = 0;

        foreach (string spaceKey in catalog.Keys)
        {
            ConfluenceManifest? manifest = ConfluenceReconciler.ReadManifest(spaceKey, _cache);
            totalFiles += manifest?.Entries.Count ?? 0;
        }

        await CacheMetadataService.WriteMetadataAsync(
            _cache.RootPath,
            ConfluenceCacheLayout.MetadataFileName,
            new ConfluenceCacheMetadata
            {
                LastSyncDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                LastSyncTimestamp = DateTimeOffset.UtcNow,
                TotalFiles = totalFiles,
                Format = "json",
            },
            ct);
    }

    private async Task WriteCacheAsync(string key, string json, CancellationToken ct)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        await _cache.PutAsync(SourceName, key, stream, ct);
    }

    /// <summary>Reads a cached artifact and returns its unwrapped payload.</summary>
    /// <remarks>
    /// Goes through <see cref="ConfluenceCachedArtifact.FromJson"/> rather than
    /// hand-reading a property name, so the envelope's serialized shape stays
    /// the type's business and cannot drift out of sync here.
    /// </remarks>
    private JsonDocument? TryReadPayload(string key)
    {
        ConfluenceCachedArtifact? artifact = ConfluenceCachedArtifact.FromJson(TryReadText(key));

        if (artifact?.Payload is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(artifact.Payload.ToJsonString());
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Cached artifact {Key} has an unreadable payload; treating it as absent", key);
            return null;
        }
    }

    private string? TryReadText(string key)
    {
        if (!_cache.TryGet(SourceName, key, out Stream? stream))
        {
            return null;
        }

        using (stream)
        using (StreamReader reader = new(stream))
        {
            return reader.ReadToEnd();
        }
    }

    private JsonDocument? TryReadJson(string key)
    {
        if (!_cache.TryGet(SourceName, key, out Stream? stream))
        {
            return null;
        }

        using (stream)
        {
            try
            {
                return JsonDocument.Parse(stream);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Cached artifact {Key} is malformed; treating it as absent", key);
                return null;
            }
        }
    }

    private enum PageResult { New, Updated, Failed }
}

/// <summary>Summary of one ingestion run.</summary>
public record IngestionResult(
    int ItemsProcessed,
    int ItemsNew,
    int ItemsUpdated,
    int ItemsFailed,
    List<string> Errors,
    DateTimeOffset StartedAt)
{
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}
