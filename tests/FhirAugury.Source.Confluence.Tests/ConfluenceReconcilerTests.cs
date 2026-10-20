using System.Text;
using System.Text.Json;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Ingestion;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Pins the classification table and the verdict rules that make completeness a
/// pure function of (manifest, cache tree).
/// </summary>
/// <remarks>
/// Every cache tree here is created by the test in a temp directory — no
/// checked-in fixtures, per <c>AGENTS.md</c>.
/// </remarks>
public class ConfluenceReconcilerTests : IDisposable
{
    private const string Space = "FHIR";

    private readonly string _root;
    private readonly FileSystemResponseCache _cache;

    public ConfluenceReconcilerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"confluence-reconcile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _cache = new FileSystemResponseCache(_root);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private void Write(string key, string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        _cache.PutAsync(ConfluenceCacheLayout.SourceName, key, stream, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private void WriteBytes(string key, int length)
    {
        using MemoryStream stream = new(new byte[length]);
        _cache.PutAsync(ConfluenceCacheLayout.SourceName, key, stream, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private void WriteArtifact(
        ConfluenceManifestEntry entry,
        int? cachedVersion = null,
        string? profileOverride = null,
        long? fileSize = null)
    {
        using JsonDocument payload = JsonDocument.Parse($$"""{"id":"{{entry.Id}}","title":"{{entry.Title}}"}""");

        ConfluenceCachedArtifact artifact = ConfluenceCachedArtifact.Wrap(
            payload.RootElement, entry.Type, Space, cachedVersion ?? entry.Version,
            fileSize ?? entry.FileSize);

        if (profileOverride is not null)
        {
            artifact = artifact with { Profile = profileOverride };
        }

        Write(ConfluenceCacheLayout.GetCacheKey(entry.Type, Space, entry.Id), artifact.ToJson());
    }

    private void WriteManifest(ConfluenceManifest manifest) =>
        Write(ConfluenceCacheLayout.GetManifestCacheKey(Space), manifest.ToJson());

    private void WriteAttempt(ConfluenceSweepAttempt attempt) =>
        Write(ConfluenceCacheLayout.GetSweepAttemptCacheKey(Space), attempt.ToJson());

    private static ConfluenceManifestEntry Page(string id, int version = 1, string status = ConfluenceEntryStatus.Current) =>
        new() { Id = id, Type = ContentTypes.Page, Title = $"Page {id}", Version = version, Status = status };

    private static ConfluenceManifestEntry Comment(string id, string containerId, int version = 1) =>
        new() { Id = id, Type = ContentTypes.Comment, Title = $"Re: {containerId}", Version = version, ContainerId = containerId };

    private static ConfluenceManifestEntry Attachment(string id, string containerId, long? fileSize, int version = 1) =>
        new()
        {
            Id = id,
            Type = ContentTypes.Attachment,
            Title = $"file-{id}.bin",
            Version = version,
            ContainerId = containerId,
            FileSize = fileSize,
            MediaType = "application/octet-stream",
        };

    private static ConfluenceManifest Manifest(params ConfluenceManifestEntry[] entries) => new()
    {
        SpaceKey = Space,
        Profiles = ConfluenceManifestProfiles.Current,
        SweptAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero),
        Complete = true,
        Entries = [.. entries],
    };

    private ConfluenceReconcilePlan Reconcile(ConfluenceReconcilePolicy? policy = null) =>
        ConfluenceReconciler.Reconcile(Space, _cache, policy ?? ConfluenceReconcilePolicy.Default);

    // ── Verdicts ──────────────────────────────────────────────────────

    [Fact]
    public void FullCache_ReportsComplete()
    {
        ConfluenceManifestEntry page = Page("100");
        ConfluenceManifestEntry comment = Comment("200", "100");
        WriteManifest(Manifest(page, comment));
        WriteArtifact(page);
        WriteArtifact(comment);

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceSpaceVerdict.Complete, plan.Verdict);
        Assert.Equal(2, plan.CachedCount);
        Assert.Equal(0, plan.MissingCount);
        Assert.Empty(plan.VanishedKeys);
    }

    [Fact]
    public void MissingPage_ReportsPartialAndNamesTheId()
    {
        ConfluenceManifestEntry present = Page("100");
        ConfluenceManifestEntry absent = Page("101");
        WriteManifest(Manifest(present, absent));
        WriteArtifact(present);

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceSpaceVerdict.Partial, plan.Verdict);
        Assert.Equal(1, plan.MissingCount);
        Assert.Equal("101", plan.ToFetch.Single().Entry.Id);
    }

    [Fact]
    public void NoManifest_ReportsUnknownEvenWhenTheTreeHoldsFiles()
    {
        // The subtle invariant of the whole shape: files on disk are not evidence
        // of completeness. Only a manifest can say what should be there.
        WriteArtifact(Page("100"));
        WriteArtifact(Page("101"));

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceSpaceVerdict.Unknown, plan.Verdict);
        Assert.Contains("no manifest", plan.UnknownReason);
    }

    [Fact]
    public void IncompleteManifest_ReportsUnknown()
    {
        ConfluenceManifestEntry page = Page("100");
        WriteManifest(Manifest(page) with { Complete = false });
        WriteArtifact(page);

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceSpaceVerdict.Unknown, plan.Verdict);
        Assert.Contains("exhaustion", plan.UnknownReason);
    }

    [Fact]
    public void MalformedManifest_ReportsUnknownWithoutThrowing()
    {
        Write(ConfluenceCacheLayout.GetManifestCacheKey(Space), "{ not json");

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceSpaceVerdict.Unknown, plan.Verdict);
    }

    [Fact]
    public void GoodManifestPlusLaterFailedSweep_ReportsUnknownNotComplete()
    {
        ConfluenceManifestEntry page = Page("100");
        ConfluenceManifest manifest = Manifest(page);
        WriteManifest(manifest);
        WriteArtifact(page);
        WriteAttempt(new ConfluenceSweepAttempt
        {
            SpaceKey = Space,
            StartedAt = manifest.SweptAt.AddHours(1),
            FinishedAt = manifest.SweptAt.AddHours(1).AddMinutes(2),
            Outcome = ConfluenceSweepOutcome.Failed,
            Error = "socket closed",
        });

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceSpaceVerdict.Unknown, plan.Verdict);
        Assert.Contains("socket closed", plan.UnknownReason);
    }

    [Fact]
    public void GoodManifestPlusEarlierFailedSweep_StaysComplete()
    {
        // The failure predates the manifest, so the manifest already supersedes it.
        ConfluenceManifestEntry page = Page("100");
        ConfluenceManifest manifest = Manifest(page);
        WriteManifest(manifest);
        WriteArtifact(page);
        WriteAttempt(new ConfluenceSweepAttempt
        {
            SpaceKey = Space,
            StartedAt = manifest.SweptAt.AddHours(-3),
            FinishedAt = manifest.SweptAt.AddHours(-3),
            Outcome = ConfluenceSweepOutcome.Failed,
        });

        Assert.Equal(ConfluenceSpaceVerdict.Complete, Reconcile().Verdict);
    }

    [Fact]
    public void GoodManifestPlusSucceededSweep_StaysComplete()
    {
        ConfluenceManifestEntry page = Page("100");
        ConfluenceManifest manifest = Manifest(page);
        WriteManifest(manifest);
        WriteArtifact(page);
        WriteAttempt(new ConfluenceSweepAttempt
        {
            SpaceKey = Space,
            StartedAt = manifest.SweptAt,
            FinishedAt = manifest.SweptAt.AddMinutes(1),
            Outcome = ConfluenceSweepOutcome.Succeeded,
        });

        Assert.Equal(ConfluenceSpaceVerdict.Complete, Reconcile().Verdict);
    }

    // ── Staleness ─────────────────────────────────────────────────────

    [Fact]
    public void HigherManifestVersion_ClassifiesStale()
    {
        ConfluenceManifestEntry page = Page("100", version: 5);
        WriteManifest(Manifest(page));
        WriteArtifact(page, cachedVersion: 3);

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceArtifactState.Stale, plan.Items.Single().State);
        Assert.Equal(ConfluenceSpaceVerdict.Partial, plan.Verdict);
    }

    [Fact]
    public void ProfileBump_ReclassifiesOnlyThatType()
    {
        ConfluenceManifestEntry page = Page("100");
        ConfluenceManifestEntry comment = Comment("200", "100");
        ConfluenceManifestEntry attachment = Attachment("300", "100", fileSize: 16);

        WriteManifest(Manifest(page, comment, attachment));
        WriteArtifact(page, profileOverride: "an-older-page-expand-set");
        WriteArtifact(comment);
        WriteArtifact(attachment);
        WriteBytes(ConfluenceCacheLayout.GetAttachmentBlobCacheKey(Space, "300"), 16);

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceArtifactState.StaleByFidelity, plan.Items.Single(i => i.Entry.Id == "100").State);
        Assert.Equal(ConfluenceArtifactState.Current, plan.Items.Single(i => i.Entry.Id == "200").State);
        Assert.Equal(ConfluenceArtifactState.Current, plan.Items.Single(i => i.Entry.Id == "300").State);
    }

    [Fact]
    public void ProfileBump_IsNotAnUnknown()
    {
        // A manifest swept under different profiles just means "due a re-sweep".
        // Reading it as data loss would make every profile bump look catastrophic.
        ConfluenceManifestEntry page = Page("100");
        WriteManifest(Manifest(page) with
        {
            Profiles = new ConfluenceManifestProfiles { Page = "something-older" },
        });
        WriteArtifact(page);

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.NotEqual(ConfluenceSpaceVerdict.Unknown, plan.Verdict);
    }

    [Fact]
    public void ForceRefetchAll_ReclassifiesCurrentAsStale()
    {
        ConfluenceManifestEntry page = Page("100");
        WriteManifest(Manifest(page));
        WriteArtifact(page);

        Assert.Equal(ConfluenceSpaceVerdict.Complete, Reconcile().Verdict);

        ConfluenceReconcilePlan forced = Reconcile(new ConfluenceReconcilePolicy { ForceRefetchAll = true });

        Assert.Equal(ConfluenceArtifactState.Stale, forced.Items.Single().State);
        Assert.Equal(ConfluenceSpaceVerdict.Partial, forced.Verdict);
    }

    // ── Archived ──────────────────────────────────────────────────────

    [Fact]
    public void ArchivedEntry_IsNeverVanished()
    {
        // Absence from the manifest is the ONLY route to Vanished. This is the
        // correctness requirement that made archived pages in scope at all.
        ConfluenceManifestEntry archived = Page("101", status: ConfluenceEntryStatus.Archived);
        WriteManifest(Manifest(Page("100"), archived));
        WriteArtifact(Page("100"));
        WriteArtifact(archived);

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Empty(plan.VanishedKeys);
        Assert.Equal(ConfluenceArtifactState.Current, plan.Items.Single(i => i.Entry.Id == "101").State);
        Assert.Equal(ConfluenceSpaceVerdict.Complete, plan.Verdict);
    }

    [Fact]
    public void ArchivedEntryWithNoCacheFile_IsMissingNotVanished()
    {
        ConfluenceManifestEntry archived = Page("101", status: ConfluenceEntryStatus.Archived);
        WriteManifest(Manifest(archived));

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceArtifactState.Missing, plan.Items.Single().State);
        Assert.Empty(plan.VanishedKeys);
    }

    // ── Vanished ──────────────────────────────────────────────────────

    [Fact]
    public void CacheFileAbsentFromManifest_IsVanished()
    {
        ConfluenceManifestEntry kept = Page("100");
        ConfluenceManifestEntry orphan = Page("999");
        WriteManifest(Manifest(kept));
        WriteArtifact(kept);
        WriteArtifact(orphan);

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal([ConfluenceCacheLayout.GetPageCacheKey(Space, "999")], plan.VanishedKeys);
        Assert.Equal(ConfluenceSpaceVerdict.Complete, plan.Verdict);
    }

    [Fact]
    public void TempFile_IsNeitherVanishedNorCounted()
    {
        ConfluenceManifestEntry page = Page("100");
        WriteManifest(Manifest(page));
        WriteArtifact(page);
        Write(ConfluenceCacheLayout.GetPageCacheKey(Space, "101") + ".tmp", "{partial");

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Empty(plan.VanishedKeys);
        Assert.Equal(1, plan.ManifestItemCount);
    }

    [Fact]
    public void AlreadyTombstonedFile_IsNotVanishedAgain()
    {
        ConfluenceManifestEntry page = Page("100");
        WriteManifest(Manifest(page));
        WriteArtifact(page);
        Write(ConfluenceCacheLayout.GetVanishedCacheKey(ConfluenceCacheLayout.GetPageCacheKey(Space, "999")), "{}");

        Assert.Empty(Reconcile().VanishedKeys);
    }

    [Fact]
    public void SpaceMetadataFile_IsNotVanished()
    {
        ConfluenceManifestEntry page = Page("100");
        WriteManifest(Manifest(page));
        WriteArtifact(page);
        Write(ConfluenceCacheLayout.GetSpaceCacheKey(Space), """{"key":"FHIR"}""");

        Assert.Empty(Reconcile().VanishedKeys);
    }

    // ── Attachments ───────────────────────────────────────────────────

    [Fact]
    public void AttachmentWithMetadataButNoBlob_IsMissing()
    {
        ConfluenceManifestEntry attachment = Attachment("300", "100", fileSize: 2048);
        WriteManifest(Manifest(attachment));
        WriteArtifact(attachment);

        ConfluenceReconcilePlan plan = Reconcile();

        ConfluenceReconcileItem item = plan.Items.Single();
        Assert.Equal(ConfluenceArtifactState.Current, item.State);
        Assert.Equal(ConfluenceArtifactState.Missing, item.BlobState);
        Assert.Equal(ConfluenceSpaceVerdict.Partial, plan.Verdict);
    }

    [Fact]
    public void AttachmentBlobWithMismatchedLength_IsMissing()
    {
        ConfluenceManifestEntry attachment = Attachment("300", "100", fileSize: 2048);
        WriteManifest(Manifest(attachment));
        WriteArtifact(attachment);
        WriteBytes(ConfluenceCacheLayout.GetAttachmentBlobCacheKey(Space, "300"), 1024);

        Assert.Equal(ConfluenceArtifactState.Missing, Reconcile().Items.Single().BlobState);
    }

    [Fact]
    public void AttachmentWithMatchingBlob_IsCurrent()
    {
        ConfluenceManifestEntry attachment = Attachment("300", "100", fileSize: 2048);
        WriteManifest(Manifest(attachment));
        WriteArtifact(attachment);
        WriteBytes(ConfluenceCacheLayout.GetAttachmentBlobCacheKey(Space, "300"), 2048);

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceArtifactState.Current, plan.Items.Single().BlobState);
        Assert.Equal(ConfluenceSpaceVerdict.Complete, plan.Verdict);
    }

    [Fact]
    public void OversizedAttachment_SkipsOnlyItsBlob()
    {
        // The metadata still follows the ordinary path, so an oversized
        // attachment is swept, cached, replayed and indexed — only its bytes
        // are absent. Conflating the two would leave no database row at all.
        ConfluenceManifestEntry attachment = Attachment("300", "100", fileSize: 500_000_000);
        WriteManifest(Manifest(attachment));
        WriteArtifact(attachment);

        ConfluenceReconcilePlan plan = Reconcile();
        ConfluenceReconcileItem item = plan.Items.Single();

        Assert.Equal(ConfluenceArtifactState.Current, item.State);
        Assert.Equal(ConfluenceArtifactState.SkippedByPolicy, item.BlobState);
        Assert.Equal(ConfluenceSpaceVerdict.CompleteWithSkips, plan.Verdict);
        Assert.Equal(1, plan.SkippedByPolicyCount);
        Assert.Equal(500_000_000, plan.SkippedByPolicyBytes);
    }

    [Fact]
    public void OversizedAttachmentBlob_IsNeverQueuedForFetch()
    {
        ConfluenceManifestEntry attachment = Attachment("300", "100", fileSize: 500_000_000);
        WriteManifest(Manifest(attachment));
        WriteArtifact(attachment);

        Assert.Empty(Reconcile().BlobsToFetch);
    }

    [Fact]
    public void LoweredCap_LeavesAnAlreadyDownloadedBlobCurrent()
    {
        // The cap gates downloading, not keeping.
        ConfluenceManifestEntry attachment = Attachment("300", "100", fileSize: 4096);
        WriteManifest(Manifest(attachment));
        WriteArtifact(attachment);
        WriteBytes(ConfluenceCacheLayout.GetAttachmentBlobCacheKey(Space, "300"), 4096);

        ConfluenceReconcilePlan plan = Reconcile(new ConfluenceReconcilePolicy { AttachmentMaxBytes = 1024 });

        Assert.Equal(ConfluenceArtifactState.Current, plan.Items.Single().BlobState);
        Assert.Equal(ConfluenceSpaceVerdict.Complete, plan.Verdict);
        Assert.Empty(plan.VanishedKeys);
    }

    [Fact]
    public void RaisedCap_FlipsSkippedByPolicyToMissing()
    {
        ConfluenceManifestEntry attachment = Attachment("300", "100", fileSize: 200_000_000);
        WriteManifest(Manifest(attachment));
        WriteArtifact(attachment);

        Assert.Equal(ConfluenceArtifactState.SkippedByPolicy, Reconcile().Items.Single().BlobState);

        ConfluenceReconcilePlan raised = Reconcile(new ConfluenceReconcilePolicy { AttachmentMaxBytes = 0 });

        Assert.Equal(ConfluenceArtifactState.Missing, raised.Items.Single().BlobState);
        Assert.Single(raised.BlobsToFetch);
    }

    [Fact]
    public void AbsentFileSize_IsAttemptedRatherThanSkipped()
    {
        // An absent size must not be read as "unbounded, therefore skip"; the
        // wire-level guards in the fill are what stop a surprise.
        ConfluenceManifestEntry attachment = Attachment("300", "100", fileSize: null);
        WriteManifest(Manifest(attachment));
        WriteArtifact(attachment);

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceArtifactState.Missing, plan.Items.Single().BlobState);
        Assert.Single(plan.BlobsToFetch);
    }

    [Fact]
    public void ZeroByteAttachment_IsCurrentWhenItsEmptyBlobExists()
    {
        ConfluenceManifestEntry attachment = Attachment("300", "100", fileSize: 0);
        WriteManifest(Manifest(attachment));
        WriteArtifact(attachment);
        WriteBytes(ConfluenceCacheLayout.GetAttachmentBlobCacheKey(Space, "300"), 0);

        Assert.Equal(ConfluenceArtifactState.Current, Reconcile().Items.Single().BlobState);
    }

    [Fact]
    public void AttachmentBlob_IsNotItselfClassifiedVanished()
    {
        ConfluenceManifestEntry attachment = Attachment("300", "100", fileSize: 8);
        WriteManifest(Manifest(attachment));
        WriteArtifact(attachment);
        WriteBytes(ConfluenceCacheLayout.GetAttachmentBlobCacheKey(Space, "300"), 8);

        Assert.Empty(Reconcile().VanishedKeys);
    }

    // ── Reporting surface ─────────────────────────────────────────────

    [Fact]
    public void Plan_ReportsCountsTheReportEndpointNeeds()
    {
        ConfluenceManifestEntry current = Page("100");
        ConfluenceManifestEntry missing = Page("101");
        ConfluenceManifestEntry stale = Page("102", version: 4);
        ConfluenceManifestEntry oversized = Attachment("300", "100", fileSize: 500_000_000);

        WriteManifest(Manifest(current, missing, stale, oversized));
        WriteArtifact(current);
        WriteArtifact(stale, cachedVersion: 1);
        WriteArtifact(oversized);
        WriteArtifact(Page("999"));

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(4, plan.ManifestItemCount);
        Assert.Equal(2, plan.CachedCount);          // 100 and the oversized attachment's metadata
        Assert.Equal(1, plan.MissingCount);         // 101
        Assert.Equal(1, plan.StaleCount);           // 102
        Assert.Equal(1, plan.SkippedByPolicyCount); // 300's blob
        Assert.Equal(1, plan.VanishedCount);        // 999
        Assert.Equal(1, plan.AttachmentCount);
        Assert.Equal(ConfluenceSpaceVerdict.Partial, plan.Verdict);
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero), plan.SweptAt);
    }

    [Fact]
    public void NeverSweptSpace_IsUnknownNotEmpty()
    {
        ConfluenceReconcilePlan plan = ConfluenceReconciler.Reconcile(
            "NEVERSWEPT", _cache, ConfluenceReconcilePolicy.Default);

        Assert.Equal(ConfluenceSpaceVerdict.Unknown, plan.Verdict);
        Assert.Equal(0, plan.ManifestItemCount);
        Assert.NotNull(plan.UnknownReason);
    }

    [Fact]
    public void CompleteButEmptyManifest_ReportsComplete()
    {
        WriteManifest(Manifest());

        ConfluenceReconcilePlan plan = Reconcile();

        Assert.Equal(ConfluenceSpaceVerdict.Complete, plan.Verdict);
        Assert.Equal(0, plan.ManifestItemCount);
    }
}
