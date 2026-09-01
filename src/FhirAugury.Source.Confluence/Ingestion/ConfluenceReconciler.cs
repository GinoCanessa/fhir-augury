using FhirAugury.Common.Caching;
using FhirAugury.Source.Confluence.Cache;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>How one cached artifact stands relative to its manifest entry.</summary>
public enum ConfluenceArtifactState
{
    /// <summary>The manifest names it and the cache holds it at the current version and profile.</summary>
    Current,

    /// <summary>The manifest names it and the cache does not hold it.</summary>
    Missing,

    /// <summary>The cache holds an older version than the manifest reports.</summary>
    Stale,

    /// <summary>
    /// The version matches but the cached copy was fetched under a different
    /// expand set for its type. Widening capture reclassifies old items here and
    /// they converge, rather than forcing a re-download from zero.
    /// </summary>
    StaleByFidelity,

    /// <summary>
    /// An attachment blob we have deliberately not downloaded because its
    /// recorded size exceeds the policy cap. Never applies to metadata.
    /// </summary>
    SkippedByPolicy,
}

/// <summary>A space's standing completeness verdict.</summary>
public enum ConfluenceSpaceVerdict
{
    /// <summary>
    /// No manifest, a malformed one, an incomplete sweep, or a failed sweep
    /// attempt more recent than the last good manifest. A space we have never
    /// successfully enumerated reports this — never <c>Complete</c>, never empty.
    /// </summary>
    Unknown,

    /// <summary>Everything the manifest names is cached at the current version and profile.</summary>
    Complete,

    /// <summary>
    /// Every item is accounted for, but one or more attachment blobs were
    /// excluded by policy. A distinct value rather than a footnote on
    /// <see cref="Complete"/>, because the verdict travels alone to surfaces
    /// that carry no skip counts.
    /// </summary>
    CompleteWithSkips,

    /// <summary>Something the manifest names is missing or stale.</summary>
    Partial,
}

/// <summary>
/// Inputs the reconciler needs from configuration, passed in so the reconciler
/// itself stays a pure function with no <c>IOptions</c> dependency.
/// </summary>
public sealed record ConfluenceReconcilePolicy
{
    /// <summary>
    /// Attachment blobs larger than this are not downloaded; <c>0</c> means
    /// unlimited. The cap gates <em>downloading</em>, not <em>keeping</em>, so
    /// lowering it never makes the cache smaller.
    /// </summary>
    public long AttachmentMaxBytes { get; init; } = 104_857_600;

    /// <summary>
    /// The <c>type=full</c> policy: treat cached bodies as stale so they are
    /// refetched. Deliberately does <b>not</b> apply to attachment blobs — their
    /// bytes are immutable for a given version and are already length-checked on
    /// every pass, so re-pulling hundreds of gigabytes would be waste, not rigour.
    /// </summary>
    public bool ForceRefetchAll { get; init; }

    public static ConfluenceReconcilePolicy Default => new();
}

/// <summary>One manifest entry, classified against the cache.</summary>
public sealed record ConfluenceReconcileItem
{
    public required ConfluenceManifestEntry Entry { get; init; }

    /// <summary>Cache key of the JSON artifact (metadata envelope for attachments).</summary>
    public required string CacheKey { get; init; }

    /// <summary>State of the JSON artifact.</summary>
    public required ConfluenceArtifactState State { get; init; }

    /// <summary>Cache key of the attachment's bytes; null for pages and comments.</summary>
    public string? BlobCacheKey { get; init; }

    /// <summary>
    /// State of the attachment's bytes, tracked separately from its metadata so
    /// an oversized attachment still produces a database row and a search
    /// document — only its bytes are absent.
    /// </summary>
    public ConfluenceArtifactState? BlobState { get; init; }

    /// <summary>True when this item needs a fetch to converge.</summary>
    public bool NeedsFetch => State is ConfluenceArtifactState.Missing
        or ConfluenceArtifactState.Stale
        or ConfluenceArtifactState.StaleByFidelity;

    /// <summary>True when this item's bytes need a fetch to converge.</summary>
    public bool NeedsBlobFetch => BlobState is ConfluenceArtifactState.Missing
        or ConfluenceArtifactState.Stale
        or ConfluenceArtifactState.StaleByFidelity;
}

/// <summary>The reconciler's answer for one space.</summary>
public sealed record ConfluenceReconcilePlan
{
    public required string SpaceKey { get; init; }

    public required ConfluenceSpaceVerdict Verdict { get; init; }

    public IReadOnlyList<ConfluenceReconcileItem> Items { get; init; } = [];

    /// <summary>Cache keys present on disk that no manifest entry claims.</summary>
    public IReadOnlyList<string> VanishedKeys { get; init; } = [];

    /// <summary>When the manifest behind this plan was swept.</summary>
    public DateTimeOffset? SweptAt { get; init; }

    /// <summary>Outcome of the most recent recorded sweep attempt.</summary>
    public ConfluenceSweepOutcome? LastSweepOutcome { get; init; }

    /// <summary>Why the verdict is <see cref="ConfluenceSpaceVerdict.Unknown"/>, when it is.</summary>
    public string? UnknownReason { get; init; }

    /// <summary>Cached artifacts that could not be read at all this pass.</summary>
    public int ReadFailures { get; init; }

    public int ManifestItemCount => Items.Count;

    public int CachedCount => Items.Count(i => i.State == ConfluenceArtifactState.Current);

    public int MissingCount => Items.Count(i => i.State == ConfluenceArtifactState.Missing)
        + Items.Count(i => i.BlobState == ConfluenceArtifactState.Missing);

    public int StaleCount => Items.Count(i => i.State is ConfluenceArtifactState.Stale
        or ConfluenceArtifactState.StaleByFidelity);

    public int SkippedByPolicyCount => Items.Count(i => i.BlobState == ConfluenceArtifactState.SkippedByPolicy);

    public long SkippedByPolicyBytes => Items
        .Where(i => i.BlobState == ConfluenceArtifactState.SkippedByPolicy)
        .Sum(i => i.Entry.FileSize ?? 0);

    public int VanishedCount => VanishedKeys.Count;

    public int AttachmentCount => Items.Count(i => i.Entry.Type == ContentTypes.Attachment);

    /// <summary>Items that must be fetched for this space to converge.</summary>
    public IEnumerable<ConfluenceReconcileItem> ToFetch => Items.Where(i => i.NeedsFetch);

    /// <summary>Attachments whose bytes must be fetched for this space to converge.</summary>
    public IEnumerable<ConfluenceReconcileItem> BlobsToFetch => Items.Where(i => i.NeedsBlobFetch);
}

/// <summary>
/// Turns "is my cache complete?" into a pure function of (manifest, cache tree).
/// No HTTP, no SQLite, no <c>IOptions</c> — so the answer is computable offline,
/// at any moment, including part-way through a multi-hour pull.
/// </summary>
public static class ConfluenceReconciler
{
    private const string TempSuffix = ".tmp";

    private static readonly string[] ContentSegments =
        ["pages", "comments", "attachments"];

    /// <summary>Classifies one space's manifest against the cache.</summary>
    public static ConfluenceReconcilePlan Reconcile(
        string spaceKey,
        ConfluenceManifest? manifest,
        ConfluenceSweepAttempt? lastAttempt,
        IResponseCache cache,
        ConfluenceReconcilePolicy policy)
    {
        // A profile bump is NOT an Unknown. The manifest records the profiles in
        // force when it was swept; a difference just means the space is due a
        // re-sweep, after which its entries classify StaleByFidelity and
        // converge. Only absence, malformedness or incompleteness is Unknown.
        string? unknownReason = ResolveUnknownReason(manifest, lastAttempt);

        if (manifest is null || unknownReason is not null)
        {
            return new ConfluenceReconcilePlan
            {
                SpaceKey = spaceKey,
                Verdict = ConfluenceSpaceVerdict.Unknown,
                Items = manifest is null ? [] : ClassifyEntries(manifest, cache, policy, out _),
                VanishedKeys = [],
                SweptAt = manifest?.SweptAt,
                LastSweepOutcome = lastAttempt?.Outcome,
                UnknownReason = unknownReason ?? "no manifest has been written for this space",
            };
        }

        List<ConfluenceReconcileItem> items = ClassifyEntries(manifest, cache, policy, out int readFailures);
        List<string> vanished = FindVanished(spaceKey, manifest, cache);

        return new ConfluenceReconcilePlan
        {
            SpaceKey = spaceKey,
            Verdict = ResolveVerdict(items),
            Items = items,
            VanishedKeys = vanished,
            SweptAt = manifest.SweptAt,
            LastSweepOutcome = lastAttempt?.Outcome,
            ReadFailures = readFailures,
        };
    }

    /// <summary>Reads a space's manifest from the cache, degrading to null.</summary>
    public static ConfluenceManifest? ReadManifest(string spaceKey, IResponseCache cache) =>
        ConfluenceManifest.FromJson(ReadText(cache, ConfluenceCacheLayout.GetManifestCacheKey(spaceKey)));

    /// <summary>Reads a space's last sweep attempt from the cache, degrading to null.</summary>
    public static ConfluenceSweepAttempt? ReadSweepAttempt(string spaceKey, IResponseCache cache) =>
        ConfluenceSweepAttempt.FromJson(ReadText(cache, ConfluenceCacheLayout.GetSweepAttemptCacheKey(spaceKey)));

    /// <summary>Reads the instance space catalog from the cache, degrading to null.</summary>
    public static ConfluenceSpaceCatalog? ReadSpaceCatalog(IResponseCache cache) =>
        ConfluenceSpaceCatalog.FromJson(ReadText(cache, ConfluenceCacheLayout.GetSpaceCatalogCacheKey()));

    /// <summary>Convenience overload that loads the manifest and attempt itself.</summary>
    public static ConfluenceReconcilePlan Reconcile(
        string spaceKey,
        IResponseCache cache,
        ConfluenceReconcilePolicy policy) =>
        Reconcile(spaceKey, ReadManifest(spaceKey, cache), ReadSweepAttempt(spaceKey, cache), cache, policy);

    private static string? ResolveUnknownReason(
        ConfluenceManifest? manifest,
        ConfluenceSweepAttempt? lastAttempt)
    {
        if (manifest is null)
        {
            return "no manifest has been written for this space";
        }

        if (!manifest.Complete)
        {
            return "the last sweep did not run to exhaustion";
        }

        // Without this clause a space whose current sweep just failed would keep
        // reporting Complete off a stale manifest — the exact false confidence
        // manifest reconciliation exists to eliminate.
        if (lastAttempt is { Outcome: ConfluenceSweepOutcome.Failed }
            && lastAttempt.StartedAt >= manifest.SweptAt)
        {
            return $"the most recent sweep attempt failed ({lastAttempt.Error ?? "no detail recorded"})";
        }

        return null;
    }

    private static ConfluenceSpaceVerdict ResolveVerdict(List<ConfluenceReconcileItem> items)
    {
        bool anyGap = items.Any(i => i.NeedsFetch || i.NeedsBlobFetch);
        if (anyGap)
        {
            return ConfluenceSpaceVerdict.Partial;
        }

        bool anySkip = items.Any(i => i.BlobState == ConfluenceArtifactState.SkippedByPolicy);
        return anySkip ? ConfluenceSpaceVerdict.CompleteWithSkips : ConfluenceSpaceVerdict.Complete;
    }

    private static List<ConfluenceReconcileItem> ClassifyEntries(
        ConfluenceManifest manifest,
        IResponseCache cache,
        ConfluenceReconcilePolicy policy,
        out int readFailures)
    {
        int failures = 0;
        List<ConfluenceReconcileItem> items = new(manifest.Entries.Count);

        foreach (ConfluenceManifestEntry entry in manifest.Entries)
        {
            string cacheKey = ConfluenceCacheLayout.GetCacheKey(entry.Type, manifest.SpaceKey, entry.Id);
            ConfluenceCachedArtifact? artifact;

            try
            {
                artifact = ConfluenceCachedArtifact.FromJson(ReadText(cache, cacheKey));
            }
            catch (IOException)
            {
                // A transient read — e.g. racing an atomic replacement — is not
                // evidence of a gap. Count it and treat the item as missing for
                // this pass; the next pass sees the settled file.
                failures++;
                artifact = null;
            }

            ConfluenceArtifactState state = ClassifyArtifact(entry, artifact, policy);

            if (entry.Type != ContentTypes.Attachment)
            {
                items.Add(new ConfluenceReconcileItem { Entry = entry, CacheKey = cacheKey, State = state });
                continue;
            }

            string blobKey = ConfluenceCacheLayout.GetAttachmentBlobCacheKey(manifest.SpaceKey, entry.Id);
            items.Add(new ConfluenceReconcileItem
            {
                Entry = entry,
                CacheKey = cacheKey,
                State = state,
                BlobCacheKey = blobKey,
                BlobState = ClassifyBlob(entry, artifact, blobKey, cache, policy),
            });
        }

        readFailures = failures;
        return items;
    }

    private static ConfluenceArtifactState ClassifyArtifact(
        ConfluenceManifestEntry entry,
        ConfluenceCachedArtifact? artifact,
        ConfluenceReconcilePolicy policy)
    {
        if (artifact is null)
        {
            return ConfluenceArtifactState.Missing;
        }

        if (entry.Version > artifact.Version)
        {
            return ConfluenceArtifactState.Stale;
        }

        if (artifact.Profile != ConfluenceCacheLayout.GetProfile(entry.Type))
        {
            return ConfluenceArtifactState.StaleByFidelity;
        }

        return policy.ForceRefetchAll ? ConfluenceArtifactState.Stale : ConfluenceArtifactState.Current;
    }

    private static ConfluenceArtifactState ClassifyBlob(
        ConfluenceManifestEntry entry,
        ConfluenceCachedArtifact? artifact,
        string blobKey,
        IResponseCache cache,
        ConfluenceReconcilePolicy policy)
    {
        long? expectedSize = artifact?.FileSize ?? entry.FileSize;
        long? actualSize = TryGetLength(cache, blobKey);

        // Presence is checked before policy on purpose: a blob already on disk
        // stays Current under a later, lower cap. The cap gates downloading, not
        // keeping, so a policy change never tombstones bytes we already hold.
        if (actualSize is not null && (expectedSize is null || actualSize == expectedSize))
        {
            return ConfluenceArtifactState.Current;
        }

        if (IsOverCap(expectedSize, policy))
        {
            // Never Missing on policy grounds — it is not a gap we intend to
            // close — and never Current when absent.
            return ConfluenceArtifactState.SkippedByPolicy;
        }

        return ConfluenceArtifactState.Missing;
    }

    private static bool IsOverCap(long? size, ConfluenceReconcilePolicy policy) =>
        policy.AttachmentMaxBytes > 0 && size is not null && size > policy.AttachmentMaxBytes;

    private static List<string> FindVanished(
        string spaceKey,
        ConfluenceManifest manifest,
        IResponseCache cache)
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

        List<string> vanished = [];

        foreach (string key in EnumerateContentKeys(spaceKey, cache))
        {
            if (!claimed.Contains(key))
            {
                vanished.Add(key);
            }
        }

        vanished.Sort(StringComparer.Ordinal);
        return vanished;
    }

    /// <summary>
    /// Cache keys under a space that represent content artifacts. Skips
    /// tombstones, space metadata, and <c>*.tmp</c> files — <c>AtomicFileWriter</c>
    /// writes <c>path + ".tmp"</c> before its move, and a mid-write temp file is
    /// neither a cached artifact nor a tombstone candidate.
    /// </summary>
    public static IEnumerable<string> EnumerateContentKeys(string spaceKey, IResponseCache cache)
    {
        string subPath = $"{ConfluenceCacheLayout.SpacesSegment}/{spaceKey}";

        foreach (string key in cache.EnumerateKeys(ConfluenceCacheLayout.SourceName, subPath))
        {
            if (IsContentKey(key))
            {
                yield return key;
            }
        }
    }

    private static bool IsContentKey(string key)
    {
        if (key.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)
            || ConfluenceCacheLayout.IsVanishedKey(key))
        {
            return false;
        }

        // spaces/{key}/{pages|comments|attachments}/{id}.{ext}
        string[] segments = key.Split('/');
        return segments.Length == 4
            && segments[0] == ConfluenceCacheLayout.SpacesSegment
            && ContentSegments.Contains(segments[2], StringComparer.Ordinal);
    }

    private static long? TryGetLength(IResponseCache cache, string key)
    {
        try
        {
            if (!cache.TryGet(ConfluenceCacheLayout.SourceName, key, out Stream? stream))
            {
                return null;
            }

            using (stream)
            {
                return stream.Length;
            }
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? ReadText(IResponseCache cache, string key)
    {
        if (!cache.TryGet(ConfluenceCacheLayout.SourceName, key, out Stream? stream))
        {
            return null;
        }

        using (stream)
        using (StreamReader reader = new(stream))
        {
            return reader.ReadToEnd();
        }
    }
}
