using System.Text;
using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>Per-space outcome of one sweep pass.</summary>
public sealed record ConfluenceSweepResult
{
    public required string SpaceKey { get; init; }

    /// <summary>True when every stream enumerated to exhaustion and a manifest was written.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>True when the space was skipped because its manifest is still young.</summary>
    public bool SkippedAsFresh { get; init; }

    public ConfluenceManifest? Manifest { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// The cheap, body-less half of acquisition: enumerates everything that
/// <em>should</em> exist in a space into a per-space manifest.
/// </summary>
/// <remarks>
/// A manifest is written <b>only</b> when that space's sweep reached exhaustion
/// without cancellation or error. A space that failed mid-sweep keeps its
/// previous manifest but now carries a failed attempt record, which the
/// reconciler turns into <c>Unknown</c> rather than a stale <c>Complete</c>.
/// </remarks>
public class ConfluenceSweep
{
    private readonly ConfluenceServiceOptions _options;
    private readonly IResponseCache _cache;
    private readonly ILogger<ConfluenceSweep> _logger;
    private readonly ConfluenceFetch _fetch;

    public ConfluenceSweep(
        IOptions<ConfluenceServiceOptions> optionsAccessor,
        IHttpClientFactory httpClientFactory,
        IResponseCache cache,
        ILogger<ConfluenceSweep> logger)
        : this(optionsAccessor, cache, logger,
            ConfluenceHttp.CreateFetch(httpClientFactory, optionsAccessor.Value))
    {
    }

    /// <summary>Test seam: supply the fetch directly.</summary>
    public ConfluenceSweep(
        IOptions<ConfluenceServiceOptions> optionsAccessor,
        IResponseCache cache,
        ILogger<ConfluenceSweep> logger,
        ConfluenceFetch fetch)
    {
        _options = optionsAccessor.Value;
        _cache = cache;
        _logger = logger;
        _fetch = fetch;
    }

    /// <summary>Sweeps one space's three body-less streams.</summary>
    public async Task<ConfluenceSweepResult> SweepSpaceAsync(string spaceKey, CancellationToken ct)
    {
        if (ShouldSkipAsFresh(spaceKey, out ConfluenceManifest? existing))
        {
            _logger.LogDebug("Skipping sweep of {SpaceKey}; manifest is younger than SpaceSweepMaxAge", spaceKey);
            return new ConfluenceSweepResult
            {
                SpaceKey = spaceKey,
                Succeeded = true,
                SkippedAsFresh = true,
                Manifest = existing,
            };
        }

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        await WriteAttemptAsync(new ConfluenceSweepAttempt
        {
            SpaceKey = spaceKey,
            StartedAt = startedAt,
            Outcome = ConfluenceSweepOutcome.Running,
        }, ct);

        List<ConfluenceManifestEntry> entries = [];

        try
        {
            await foreach (JsonElement element in EnumeratePagesAsync(spaceKey, ct))
            {
                entries.Add(ToPageEntry(element));
            }

            await foreach (JsonElement element in EnumerateCommentsAsync(spaceKey, ct))
            {
                entries.Add(ToCommentEntry(element));
            }

            await foreach (JsonElement element in EnumerateAttachmentsAsync(spaceKey, ct))
            {
                entries.Add(ToAttachmentEntry(element));
            }
        }
        catch (Exception ex)
        {
            // An expired credential or an edge challenge aborts the whole run
            // rather than turning every remaining space into an apparent mass
            // deletion.
            ConfluenceRunStop.ThrowIfRunMustStop(ex);

            string error = ex is OperationCanceledException ? "cancelled mid-sweep" : ex.Message;
            _logger.LogWarning(ex, "Sweep of space {SpaceKey} did not reach exhaustion", spaceKey);

            await WriteAttemptAsync(new ConfluenceSweepAttempt
            {
                SpaceKey = spaceKey,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                Outcome = ConfluenceSweepOutcome.Failed,
                Error = error,
            }, CancellationToken.None);

            // The previous manifest is deliberately left untouched.
            return new ConfluenceSweepResult { SpaceKey = spaceKey, Succeeded = false, Error = error };
        }

        ConfluenceManifest manifest = new()
        {
            SpaceKey = spaceKey,
            Profiles = ConfluenceManifestProfiles.Current,
            SweptAt = DateTimeOffset.UtcNow,
            Complete = true,
            Entries = entries,
        };

        await WriteManifestAsync(manifest, ct);
        await WriteAttemptAsync(new ConfluenceSweepAttempt
        {
            SpaceKey = spaceKey,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.UtcNow,
            Outcome = ConfluenceSweepOutcome.Succeeded,
        }, ct);

        _logger.LogInformation(
            "Swept space {SpaceKey}: {Pages} pages, {Comments} comments, {Attachments} attachments",
            spaceKey,
            entries.Count(e => e.Type == ContentTypes.Page),
            entries.Count(e => e.Type == ContentTypes.Comment),
            entries.Count(e => e.Type == ContentTypes.Attachment));

        return new ConfluenceSweepResult { SpaceKey = spaceKey, Succeeded = true, Manifest = manifest };
    }

    // ── Streams ───────────────────────────────────────────────────────

    private IAsyncEnumerable<JsonElement> EnumeratePagesAsync(string spaceKey, CancellationToken ct)
    {
        // status is left at the server default (current). Probing found archived
        // pages unreachable on this instance, and status=any admits trashed
        // content — see docs/technical/confluence-api-notes.md.
        string url = $"{_options.BaseUrl}/rest/api/content" +
                     $"?spaceKey={Uri.EscapeDataString(spaceKey)}&type=page&expand=version" +
                     $"&limit={_options.SweepPageSize}";

        return ConfluenceHttp.EnumerateResultsAsync(_fetch, _options.BaseUrl, url, ct);
    }

    private IAsyncEnumerable<JsonElement> EnumerateCommentsAsync(string spaceKey, CancellationToken ct)
    {
        // container.id is reliably populated here, so comments are a third
        // space-wide stream rather than one child/comment call per page.
        string cql = Uri.EscapeDataString($"space=\"{spaceKey}\" and type=comment");
        string url = $"{_options.BaseUrl}/rest/api/content/search" +
                     $"?cql={cql}&expand=version,container&limit={_options.SweepPageSize}";

        return ConfluenceHttp.EnumerateResultsAsync(_fetch, _options.BaseUrl, url, ct);
    }

    private IAsyncEnumerable<JsonElement> EnumerateAttachmentsAsync(string spaceKey, CancellationToken ct)
    {
        string cql = Uri.EscapeDataString($"space=\"{spaceKey}\" and type=attachment");
        string url = $"{_options.BaseUrl}/rest/api/content/search" +
                     $"?cql={cql}&expand=version,container,metadata&limit={_options.SweepPageSize}";

        return ConfluenceHttp.EnumerateResultsAsync(_fetch, _options.BaseUrl, url, ct);
    }

    // ── Entry projection ──────────────────────────────────────────────

    private static ConfluenceManifestEntry ToPageEntry(JsonElement element) => new()
    {
        Id = JsonElementHelper.GetString(element, "id") ?? string.Empty,
        Type = ContentTypes.Page,
        Title = JsonElementHelper.GetString(element, "title") ?? string.Empty,
        Version = ReadVersion(element),
        When = ReadWhen(element),
        Status = ReadStatus(element),
    };

    private static ConfluenceManifestEntry ToCommentEntry(JsonElement element) => new()
    {
        Id = JsonElementHelper.GetString(element, "id") ?? string.Empty,
        Type = ContentTypes.Comment,
        Title = JsonElementHelper.GetString(element, "title") ?? string.Empty,
        Version = ReadVersion(element),
        When = ReadWhen(element),
        Status = ReadStatus(element),
        ContainerId = JsonElementHelper.GetNestedString(element, "container", "id"),
    };

    private static ConfluenceManifestEntry ToAttachmentEntry(JsonElement element) => new()
    {
        Id = JsonElementHelper.GetString(element, "id") ?? string.Empty,
        Type = ContentTypes.Attachment,
        Title = JsonElementHelper.GetString(element, "title") ?? string.Empty,
        Version = ReadVersion(element),
        When = ReadWhen(element),
        Status = ReadStatus(element),
        ContainerId = JsonElementHelper.GetNestedString(element, "container", "id"),
        MediaType = JsonElementHelper.GetNestedString(element, "extensions", "mediaType"),
        FileSize = ReadFileSize(element),
        DownloadPath = JsonElementHelper.GetNestedString(element, "_links", "download"),
    };

    private static int ReadVersion(JsonElement element) =>
        element.TryGetProperty("version", out JsonElement version)
        && version.TryGetProperty("number", out JsonElement number)
        && number.TryGetInt32(out int parsed)
            ? parsed
            : 1;

    private static DateTimeOffset? ReadWhen(JsonElement element) =>
        DateTimeOffset.TryParse(JsonElementHelper.GetNestedString(element, "version", "when"), out DateTimeOffset when)
            ? when
            : null;

    private static string ReadStatus(JsonElement element) =>
        JsonElementHelper.GetString(element, "status") switch
        {
            ConfluenceEntryStatus.Archived => ConfluenceEntryStatus.Archived,
            _ => ConfluenceEntryStatus.Current,
        };

    /// <summary>
    /// Null when Confluence reports no size at all, which must stay
    /// distinguishable from a legitimate zero-byte attachment.
    /// </summary>
    private static long? ReadFileSize(JsonElement element) =>
        element.TryGetProperty("extensions", out JsonElement extensions)
        && extensions.TryGetProperty("fileSize", out JsonElement size)
        && size.ValueKind == JsonValueKind.Number
        && size.TryGetInt64(out long parsed)
            ? parsed
            : null;

    // ── Persistence ───────────────────────────────────────────────────

    private bool ShouldSkipAsFresh(string spaceKey, out ConfluenceManifest? existing)
    {
        existing = null;
        TimeSpan maxAge = _options.GetSpaceSweepMaxAge();
        if (maxAge <= TimeSpan.Zero)
        {
            return false;
        }

        existing = ConfluenceReconciler.ReadManifest(spaceKey, _cache);
        return existing is { Complete: true } && DateTimeOffset.UtcNow - existing.SweptAt < maxAge;
    }

    private Task WriteManifestAsync(ConfluenceManifest manifest, CancellationToken ct) =>
        WriteAsync(ConfluenceCacheLayout.GetManifestCacheKey(manifest.SpaceKey), manifest.ToJson(), ct);

    private Task WriteAttemptAsync(ConfluenceSweepAttempt attempt, CancellationToken ct) =>
        WriteAsync(ConfluenceCacheLayout.GetSweepAttemptCacheKey(attempt.SpaceKey), attempt.ToJson(), ct);

    private async Task WriteAsync(string key, string json, CancellationToken ct)
    {
        // IResponseCache.PutAsync goes through AtomicFileWriter, so a manifest is
        // never observed half-written.
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        await _cache.PutAsync(ConfluenceCacheLayout.SourceName, key, stream, ct);
    }
}
