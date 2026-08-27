using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirAugury.Source.Confluence.Cache;

/// <summary>Instance-level record of which spaces are tracked at all.</summary>
/// <remarks>
/// Per-space manifests can never answer a question about a space they do not
/// mention. Without this catalog, a space that is later archived — or dropped
/// from an explicit <c>Spaces</c> list — would keep its stale manifest and
/// replay forever.
/// </remarks>
public sealed record ConfluenceSpaceCatalog
{
    /// <summary>When discovery ran.</summary>
    public DateTimeOffset DiscoveredAt { get; init; }

    /// <summary>True only when discovery enumerated to exhaustion.</summary>
    public bool Complete { get; init; }

    /// <summary>The tracked spaces, keyed by space key.</summary>
    public List<ConfluenceCatalogedSpace> Spaces { get; init; } = [];

    /// <summary>Space keys in the catalog.</summary>
    [JsonIgnore]
    public IEnumerable<string> Keys => Spaces.Select(s => s.Key);

    /// <inheritdoc cref="ConfluenceCachedArtifact.ToJson" />
    public string ToJson() =>
        JsonSerializer.Serialize(this, ConfluenceCacheJsonContext.Default.ConfluenceSpaceCatalog);

    /// <inheritdoc cref="ConfluenceCachedArtifact.FromJson" />
    public static ConfluenceSpaceCatalog? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, ConfluenceCacheJsonContext.Default.ConfluenceSpaceCatalog);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One tracked space in a <see cref="ConfluenceSpaceCatalog"/>.</summary>
public sealed record ConfluenceCatalogedSpace
{
    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}

/// <summary>Outcome of a single space's sweep attempt.</summary>
public enum ConfluenceSweepOutcome
{
    /// <summary>The sweep started but has not recorded an end.</summary>
    Running,

    /// <summary>The sweep enumerated every stream to exhaustion.</summary>
    Succeeded,

    /// <summary>The sweep threw, was cancelled, or stopped short.</summary>
    Failed,
}

/// <summary>
/// Record of the most recent sweep attempt for one space, written at the
/// <b>start</b> of the sweep and updated at its end.
/// </summary>
/// <remarks>
/// This is what separates "the last sweep failed" from "the last good manifest
/// said complete". Without it, a space whose current sweep just failed would
/// keep reporting <c>Complete</c> off a stale manifest — precisely the false
/// confidence manifest reconciliation exists to eliminate.
/// </remarks>
public sealed record ConfluenceSweepAttempt
{
    public string SpaceKey { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public ConfluenceSweepOutcome Outcome { get; init; } = ConfluenceSweepOutcome.Running;

    public string? Error { get; init; }

    /// <inheritdoc cref="ConfluenceCachedArtifact.ToJson" />
    public string ToJson() =>
        JsonSerializer.Serialize(this, ConfluenceCacheJsonContext.Default.ConfluenceSweepAttempt);

    /// <inheritdoc cref="ConfluenceCachedArtifact.FromJson" />
    public static ConfluenceSweepAttempt? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, ConfluenceCacheJsonContext.Default.ConfluenceSweepAttempt);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Everything that <em>should</em> exist in one space, as of the last sweep that
/// ran to exhaustion.
/// </summary>
/// <remarks>
/// Completeness stops being a claim the writer makes about its own success and
/// becomes a pure function of (manifest, cache files), computable offline at any
/// moment.
/// </remarks>
public sealed record ConfluenceManifest
{
    public string SpaceKey { get; init; } = string.Empty;

    /// <summary>The per-type fidelity profiles in force when this sweep ran.</summary>
    public ConfluenceManifestProfiles Profiles { get; init; } = new();

    public DateTimeOffset SweptAt { get; init; }

    /// <summary>True only when the sweep enumerated every stream to exhaustion.</summary>
    public bool Complete { get; init; }

    public List<ConfluenceManifestEntry> Entries { get; init; } = [];

    /// <summary>Entries of a given content type.</summary>
    public IEnumerable<ConfluenceManifestEntry> OfType(string contentType) =>
        Entries.Where(e => e.Type == contentType);

    /// <inheritdoc cref="ConfluenceCachedArtifact.ToJson" />
    public string ToJson() =>
        JsonSerializer.Serialize(this, ConfluenceCacheJsonContext.Default.ConfluenceManifest);

    /// <inheritdoc cref="ConfluenceCachedArtifact.FromJson" />
    public static ConfluenceManifest? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, ConfluenceCacheJsonContext.Default.ConfluenceManifest);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The three per-type fidelity profiles recorded on a manifest.</summary>
public sealed record ConfluenceManifestProfiles
{
    public string Page { get; init; } = ConfluenceCacheLayout.PageProfile;

    public string Comment { get; init; } = ConfluenceCacheLayout.CommentProfile;

    public string Attachment { get; init; } = ConfluenceCacheLayout.AttachmentProfile;

    /// <summary>Returns the recorded profile for a content type.</summary>
    public string For(string contentType) => contentType switch
    {
        ContentTypes.Page => Page,
        ContentTypes.Comment => Comment,
        ContentTypes.Attachment => Attachment,
        _ => string.Empty,
    };

    /// <summary>The profiles currently in force in code.</summary>
    public static ConfluenceManifestProfiles Current => new();
}

/// <summary>Status of a manifest entry as reported by Confluence.</summary>
public static class ConfluenceEntryStatus
{
    public const string Current = "current";
    public const string Archived = "archived";
}

/// <summary>One item a space's sweep says should exist.</summary>
public sealed record ConfluenceManifestEntry
{
    public string Id { get; init; } = string.Empty;

    /// <summary><c>page</c>, <c>comment</c>, or <c>attachment</c>.</summary>
    public string Type { get; init; } = ContentTypes.Page;

    public string Title { get; init; } = string.Empty;

    public int Version { get; init; }

    /// <summary>Version timestamp reported by Confluence.</summary>
    public DateTimeOffset? When { get; init; }

    /// <summary><c>current</c> or <c>archived</c>.</summary>
    public string Status { get; init; } = ConfluenceEntryStatus.Current;

    /// <summary>Owning page id for comments and attachments; null for pages.</summary>
    public string? ContainerId { get; init; }

    /// <summary>Parent page id, for pages.</summary>
    public string? ParentId { get; init; }

    /// <summary>Attachment media type.</summary>
    public string? MediaType { get; init; }

    /// <summary>
    /// Attachment byte length. <b>Nullable on purpose:</b>
    /// <c>extensions.fileSize</c> can be absent, and an absent size must be
    /// distinguishable from a legitimate zero-byte attachment — otherwise the
    /// size cap would silently skip every attachment whose size Confluence
    /// declined to report.
    /// </summary>
    public long? FileSize { get; init; }

    /// <summary>Site-relative download path for an attachment.</summary>
    public string? DownloadPath { get; init; }

    /// <summary>True when Confluence reported this item as archived.</summary>
    [JsonIgnore]
    public bool IsArchived => Status == ConfluenceEntryStatus.Archived;
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(ConfluenceCachedArtifact))]
[JsonSerializable(typeof(ConfluenceManifest))]
[JsonSerializable(typeof(ConfluenceSpaceCatalog))]
[JsonSerializable(typeof(ConfluenceSweepAttempt))]
internal sealed partial class ConfluenceCacheJsonContext : JsonSerializerContext;
