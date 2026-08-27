using FhirAugury.Common;

namespace FhirAugury.Source.Confluence.Cache;

/// <summary>Constants for Confluence cache file layout and naming conventions.</summary>
/// <remarks>
/// The layout is deliberately type-segmented — <c>pages/</c>, <c>comments/</c>,
/// <c>attachments/</c> — so a manifest entry maps to exactly one key, tombstones
/// can preserve their original sub-path, and an attachment's metadata and bytes
/// can sit side by side without colliding.
/// </remarks>
public static class ConfluenceCacheLayout
{
    /// <summary>The source name used as the cache subdirectory.</summary>
    public const string SourceName = SourceSystems.Confluence;

    /// <summary>Extension for JSON API responses.</summary>
    public const string JsonExtension = "json";

    /// <summary>Extension for attachment binary payloads.</summary>
    public const string BlobExtension = "bin";

    /// <summary>Metadata file name for Confluence cache state.</summary>
    public const string MetadataFileName = "_meta_confluence.json";

    /// <summary>
    /// Path segment that holds tombstoned artifacts. Files are <b>moved</b> here
    /// rather than deleted, because absence from a manifest can also mean
    /// "not visible to the credential this run used".
    /// </summary>
    public const string VanishedSegment = "_vanished";

    /// <summary>Top-level directory holding one sub-directory per space.</summary>
    public const string SpacesSegment = "spaces";

    private const string PagesSegment = "pages";
    private const string CommentsSegment = "comments";
    private const string AttachmentsSegment = "attachments";

    // ── Fidelity profiles ─────────────────────────────────────────────
    // Per-type rather than one global string, so widening the attachment
    // expand set does not reclassify every cached page and comment as stale.

    /// <summary>Expand set every cached page is fetched under.</summary>
    public const string PageProfile = "body.storage,version,ancestors,metadata.labels,space";

    /// <summary>Expand set every cached comment is fetched under.</summary>
    public const string CommentProfile = "body.storage,version,container";

    /// <summary>Expand set every cached attachment's metadata is fetched under.</summary>
    public const string AttachmentProfile = "version,container,metadata";

    /// <summary>Returns the fidelity profile currently in force for a content type.</summary>
    public static string GetProfile(string contentType) => contentType switch
    {
        ContentTypes.Page => PageProfile,
        ContentTypes.Comment => CommentProfile,
        ContentTypes.Attachment => AttachmentProfile,
        _ => throw new ArgumentOutOfRangeException(nameof(contentType), contentType, "Unknown Confluence content type."),
    };

    // ── Content keys ──────────────────────────────────────────────────

    /// <summary>Gets the cache key for a page within a space.</summary>
    public static string GetPageCacheKey(string spaceKey, string pageId)
        => $"{SpacesSegment}/{spaceKey}/{PagesSegment}/{pageId}.{JsonExtension}";

    /// <summary>Gets the cache key for a comment within a space.</summary>
    public static string GetCommentCacheKey(string spaceKey, string commentId)
        => $"{SpacesSegment}/{spaceKey}/{CommentsSegment}/{commentId}.{JsonExtension}";

    /// <summary>Gets the cache key for an attachment's metadata envelope.</summary>
    public static string GetAttachmentMetaCacheKey(string spaceKey, string attachmentId)
        => $"{SpacesSegment}/{spaceKey}/{AttachmentsSegment}/{attachmentId}.{JsonExtension}";

    /// <summary>Gets the cache key for an attachment's raw bytes.</summary>
    /// <remarks>
    /// A blob carries no envelope of its own; its currency is derived from the
    /// sibling metadata envelope plus a length check.
    /// </remarks>
    public static string GetAttachmentBlobCacheKey(string spaceKey, string attachmentId)
        => $"{SpacesSegment}/{spaceKey}/{AttachmentsSegment}/{attachmentId}.{BlobExtension}";

    /// <summary>Gets the cache key for an item of the given content type.</summary>
    public static string GetCacheKey(string contentType, string spaceKey, string id) => contentType switch
    {
        ContentTypes.Page => GetPageCacheKey(spaceKey, id),
        ContentTypes.Comment => GetCommentCacheKey(spaceKey, id),
        ContentTypes.Attachment => GetAttachmentMetaCacheKey(spaceKey, id),
        _ => throw new ArgumentOutOfRangeException(nameof(contentType), contentType, "Unknown Confluence content type."),
    };

    // ── Metadata keys ─────────────────────────────────────────────────

    /// <summary>Gets the cache key for space metadata.</summary>
    /// <remarks>
    /// Lives <em>inside</em> the space directory so it cannot collide with the
    /// directory itself, which the previous <c>spaces/{key}.json</c> shape did.
    /// </remarks>
    public static string GetSpaceCacheKey(string spaceKey)
        => $"{SpacesSegment}/{spaceKey}/_space.{JsonExtension}";

    /// <summary>Gets the cache key for a space's sweep manifest.</summary>
    public static string GetManifestCacheKey(string spaceKey)
        => $"{SpacesSegment}/{spaceKey}/_meta_manifest.{JsonExtension}";

    /// <summary>Gets the cache key for a space's most recent sweep attempt record.</summary>
    public static string GetSweepAttemptCacheKey(string spaceKey)
        => $"{SpacesSegment}/{spaceKey}/_meta_sweep_attempt.{JsonExtension}";

    /// <summary>Gets the cache key for the instance-level catalog of tracked spaces.</summary>
    public static string GetSpaceCatalogCacheKey()
        => $"_meta_space_catalog.{JsonExtension}";

    // ── Tombstones ────────────────────────────────────────────────────

    /// <summary>
    /// Maps a live cache key to its tombstone key, <b>preserving the original
    /// relative sub-path</b> under a <c>_vanished/</c> segment:
    /// <c>spaces/FHIR/pages/1.json</c> becomes
    /// <c>spaces/FHIR/_vanished/pages/1.json</c>.
    /// </summary>
    /// <remarks>
    /// A flat <c>_vanished/{id}.json</c> could not hold both an attachment's
    /// metadata and its bytes, and would collide when the same id disappeared
    /// twice from different spaces.
    /// </remarks>
    public static string GetVanishedCacheKey(string originalKey)
    {
        if (IsVanishedKey(originalKey))
        {
            return originalKey;
        }

        string[] segments = originalKey.Split('/');

        // spaces/{key}/... -> spaces/{key}/_vanished/...
        if (segments.Length > 2 && segments[0] == SpacesSegment)
        {
            return string.Join('/', segments[..2].Append(VanishedSegment).Concat(segments[2..]));
        }

        return $"{VanishedSegment}/{originalKey}";
    }

    /// <summary>True when the key already sits under a <c>_vanished/</c> segment.</summary>
    public static bool IsVanishedKey(string key) =>
        key.Split('/').Contains(VanishedSegment, StringComparer.Ordinal);
}
