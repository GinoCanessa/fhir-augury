using System.Text.Json;
using System.Text.Json.Nodes;

namespace FhirAugury.Source.Confluence.Cache;

/// <summary>
/// The envelope every cached Confluence JSON artifact is written inside.
/// </summary>
/// <remarks>
/// <para>
/// This is the persisted per-item state that makes <c>Stale</c> and
/// <c>StaleByFidelity</c> computable at all: without it there is nowhere on disk
/// to record which expand set a given cached file was fetched under, and the
/// reconciler could only ever guess.
/// </para>
/// <para>
/// The raw API response is preserved verbatim under <see cref="Payload"/>, so
/// nothing is lost. The cost is that a cached file is no longer byte-identical
/// to the API response — a small, deliberate reduction in reversibility, paid
/// so that widening the capture set later converges instead of forcing a
/// re-download.
/// </para>
/// <para>
/// Attachment <b>bytes</b> cannot carry an envelope. A blob is accompanied by
/// its metadata envelope, whose <see cref="Version"/> and <see cref="Profile"/>
/// describe the blob as well; the blob counts as current only when that
/// envelope is current <em>and</em> the blob exists at the recorded length.
/// </para>
/// </remarks>
public sealed record ConfluenceCachedArtifact
{
    /// <summary>Fidelity profile (expand set) this artifact was fetched under.</summary>
    public string Profile { get; init; } = string.Empty;

    /// <summary>Confluence version number of the item at fetch time.</summary>
    public int Version { get; init; }

    /// <summary>When this artifact was fetched.</summary>
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>Content type: <c>page</c>, <c>comment</c>, or <c>attachment</c>.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Space this artifact belongs to.</summary>
    public string SpaceKey { get; init; } = string.Empty;

    /// <summary>
    /// For an attachment metadata envelope, the recorded byte length of the
    /// sibling blob. Null when unknown or not an attachment — an absent size
    /// must stay distinguishable from a legitimate zero-byte attachment.
    /// </summary>
    public long? FileSize { get; init; }

    /// <summary>The raw API response, unmodified.</summary>
    public JsonNode? Payload { get; init; }

    /// <summary>Serializes this envelope for the cache.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(this, ConfluenceCacheJsonContext.Default.ConfluenceCachedArtifact);

    /// <summary>
    /// Parses a cached envelope. Returns <see langword="null"/> for null, blank,
    /// or malformed input and never throws — a corrupt cache file must classify
    /// as missing rather than crash ingestion.
    /// </summary>
    public static ConfluenceCachedArtifact? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, ConfluenceCacheJsonContext.Default.ConfluenceCachedArtifact);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Wraps a raw API response element in an envelope.</summary>
    public static ConfluenceCachedArtifact Wrap(
        JsonElement payload,
        string contentType,
        string spaceKey,
        int version,
        long? fileSize = null,
        DateTimeOffset? fetchedAt = null) =>
        new()
        {
            Profile = ConfluenceCacheLayout.GetProfile(contentType),
            Version = version,
            FetchedAt = fetchedAt ?? DateTimeOffset.UtcNow,
            Type = contentType,
            SpaceKey = spaceKey,
            FileSize = fileSize,
            Payload = JsonNode.Parse(payload.GetRawText()),
        };
}
