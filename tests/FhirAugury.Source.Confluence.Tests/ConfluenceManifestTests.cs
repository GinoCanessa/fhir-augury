using System.Text.Json;
using FhirAugury.Source.Confluence.Cache;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Pins the on-disk vocabulary introduced by slot 0827-01: cache key shapes,
/// tombstone mapping, and round-trip fidelity for the manifest, space catalog,
/// sweep attempt, and cached-artifact envelope.
/// </summary>
/// <remarks>
/// The malformed-input cases matter more than they look. A corrupt manifest must
/// degrade to <c>unknown</c>, never crash ingestion, so every <c>FromJson</c>
/// here is asserted to return null rather than throw.
/// </remarks>
public class ConfluenceManifestTests
{
    // ── Cache layout ──────────────────────────────────────────────────

    [Fact]
    public void CacheKeys_AreTypeSegmentedUnderTheirSpace()
    {
        Assert.Equal("spaces/FHIR/pages/123.json", ConfluenceCacheLayout.GetPageCacheKey("FHIR", "123"));
        Assert.Equal("spaces/FHIR/comments/456.json", ConfluenceCacheLayout.GetCommentCacheKey("FHIR", "456"));
        Assert.Equal("spaces/FHIR/attachments/789.json", ConfluenceCacheLayout.GetAttachmentMetaCacheKey("FHIR", "789"));
        Assert.Equal("spaces/FHIR/attachments/789.bin", ConfluenceCacheLayout.GetAttachmentBlobCacheKey("FHIR", "789"));
    }

    [Fact]
    public void SpaceCacheKey_LivesInsideTheSpaceDirectoryRatherThanCollidingWithIt()
    {
        string spaceKey = ConfluenceCacheLayout.GetSpaceCacheKey("FHIR");
        string pageKey = ConfluenceCacheLayout.GetPageCacheKey("FHIR", "1");

        Assert.Equal("spaces/FHIR/_space.json", spaceKey);
        Assert.StartsWith("spaces/FHIR/", pageKey, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataKeys_AreFilteredByTheCacheMetaPrefix()
    {
        // FileSystemResponseCache.EnumerateKeys skips files named _meta_*.json,
        // so these never surface as cache keys.
        Assert.Equal("spaces/FHIR/_meta_manifest.json", ConfluenceCacheLayout.GetManifestCacheKey("FHIR"));
        Assert.Equal("spaces/FHIR/_meta_sweep_attempt.json", ConfluenceCacheLayout.GetSweepAttemptCacheKey("FHIR"));
        Assert.Equal("_meta_space_catalog.json", ConfluenceCacheLayout.GetSpaceCatalogCacheKey());
    }

    [Fact]
    public void VanishedKey_PreservesTheOriginalSubPath()
    {
        Assert.Equal(
            "spaces/FHIR/_vanished/pages/123.json",
            ConfluenceCacheLayout.GetVanishedCacheKey("spaces/FHIR/pages/123.json"));

        // Metadata and bytes for the same attachment must not collide.
        Assert.Equal(
            "spaces/FHIR/_vanished/attachments/789.json",
            ConfluenceCacheLayout.GetVanishedCacheKey("spaces/FHIR/attachments/789.json"));
        Assert.Equal(
            "spaces/FHIR/_vanished/attachments/789.bin",
            ConfluenceCacheLayout.GetVanishedCacheKey("spaces/FHIR/attachments/789.bin"));
    }

    [Fact]
    public void VanishedKey_IsIdempotentAndDetectable()
    {
        string once = ConfluenceCacheLayout.GetVanishedCacheKey("spaces/FHIR/pages/1.json");
        string twice = ConfluenceCacheLayout.GetVanishedCacheKey(once);

        Assert.Equal(once, twice);
        Assert.True(ConfluenceCacheLayout.IsVanishedKey(once));
        Assert.False(ConfluenceCacheLayout.IsVanishedKey("spaces/FHIR/pages/1.json"));
    }

    [Fact]
    public void VanishedKey_FallsBackToAPrefixForNonSpaceKeys()
    {
        Assert.Equal("_vanished/loose.json", ConfluenceCacheLayout.GetVanishedCacheKey("loose.json"));
    }

    [Fact]
    public void Profiles_ArePerTypeSoOneBumpDoesNotReclassifyEverything()
    {
        Assert.Equal(ConfluenceCacheLayout.PageProfile, ConfluenceCacheLayout.GetProfile(ContentTypes.Page));
        Assert.Equal(ConfluenceCacheLayout.CommentProfile, ConfluenceCacheLayout.GetProfile(ContentTypes.Comment));
        Assert.Equal(ConfluenceCacheLayout.AttachmentProfile, ConfluenceCacheLayout.GetProfile(ContentTypes.Attachment));

        Assert.NotEqual(ConfluenceCacheLayout.PageProfile, ConfluenceCacheLayout.CommentProfile);
        Assert.NotEqual(ConfluenceCacheLayout.PageProfile, ConfluenceCacheLayout.AttachmentProfile);
    }

    [Fact]
    public void GetProfile_RejectsAnUnknownContentType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfluenceCacheLayout.GetProfile("blogpost"));
    }

    // ── Cached artifact envelope ──────────────────────────────────────

    [Fact]
    public void Artifact_RoundTripsAndPreservesItsPayloadVerbatim()
    {
        const string RawPayload = """
            {"id":"123","type":"page","title":"Some Page","version":{"number":7},"nested":{"deep":[1,2,3]}}
            """;

        using JsonDocument document = JsonDocument.Parse(RawPayload);
        ConfluenceCachedArtifact artifact = ConfluenceCachedArtifact.Wrap(
            document.RootElement, ContentTypes.Page, "FHIR", version: 7);

        ConfluenceCachedArtifact? parsed = ConfluenceCachedArtifact.FromJson(artifact.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(ConfluenceCacheLayout.PageProfile, parsed.Profile);
        Assert.Equal(7, parsed.Version);
        Assert.Equal(ContentTypes.Page, parsed.Type);
        Assert.Equal("FHIR", parsed.SpaceKey);
        Assert.Equal("123", parsed.Payload!["id"]!.GetValue<string>());
        Assert.Equal("Some Page", parsed.Payload["title"]!.GetValue<string>());
        Assert.Equal(3, parsed.Payload["nested"]!["deep"]!.AsArray().Count);
    }

    [Fact]
    public void Artifact_DistinguishesAbsentFileSizeFromZero()
    {
        using JsonDocument document = JsonDocument.Parse("""{"id":"1"}""");

        ConfluenceCachedArtifact absent = ConfluenceCachedArtifact.Wrap(
            document.RootElement, ContentTypes.Attachment, "FHIR", version: 1, fileSize: null);
        ConfluenceCachedArtifact empty = ConfluenceCachedArtifact.Wrap(
            document.RootElement, ContentTypes.Attachment, "FHIR", version: 1, fileSize: 0);

        Assert.Null(ConfluenceCachedArtifact.FromJson(absent.ToJson())!.FileSize);
        Assert.Equal(0, ConfluenceCachedArtifact.FromJson(empty.ToJson())!.FileSize);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    public void Artifact_FromJson_DegradesToNullWithoutThrowing(string? json)
    {
        Assert.Null(ConfluenceCachedArtifact.FromJson(json));
    }

    // ── Manifest ──────────────────────────────────────────────────────

    private static ConfluenceManifest SampleManifest() => new()
    {
        SpaceKey = "FHIR",
        Profiles = ConfluenceManifestProfiles.Current,
        SweptAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero),
        Complete = true,
        Entries =
        [
            new ConfluenceManifestEntry
            {
                Id = "100",
                Type = ContentTypes.Page,
                Title = "Live Page",
                Version = 3,
                When = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                Status = ConfluenceEntryStatus.Current,
                ParentId = "1",
            },
            new ConfluenceManifestEntry
            {
                Id = "101",
                Type = ContentTypes.Page,
                Title = "Old Page",
                Version = 9,
                Status = ConfluenceEntryStatus.Archived,
            },
            new ConfluenceManifestEntry
            {
                Id = "200",
                Type = ContentTypes.Comment,
                Title = "Re: Live Page",
                Version = 1,
                ContainerId = "100",
            },
            new ConfluenceManifestEntry
            {
                Id = "300",
                Type = ContentTypes.Attachment,
                Title = "deck.pptx",
                Version = 2,
                ContainerId = "100",
                MediaType = "application/vnd.ms-powerpoint",
                FileSize = 4096,
                DownloadPath = "/download/attachments/100/deck.pptx?version=2",
            },
        ],
    };

    [Fact]
    public void Manifest_RoundTripsEveryEntryField()
    {
        ConfluenceManifest? parsed = ConfluenceManifest.FromJson(SampleManifest().ToJson());

        Assert.NotNull(parsed);
        Assert.Equal("FHIR", parsed.SpaceKey);
        Assert.True(parsed.Complete);
        Assert.Equal(4, parsed.Entries.Count);
        Assert.Equal(ConfluenceCacheLayout.PageProfile, parsed.Profiles.Page);
        Assert.Equal(ConfluenceCacheLayout.CommentProfile, parsed.Profiles.Comment);
        Assert.Equal(ConfluenceCacheLayout.AttachmentProfile, parsed.Profiles.Attachment);

        ConfluenceManifestEntry attachment = parsed.Entries.Single(e => e.Id == "300");
        Assert.Equal(ContentTypes.Attachment, attachment.Type);
        Assert.Equal("100", attachment.ContainerId);
        Assert.Equal(4096, attachment.FileSize);
        Assert.Equal("application/vnd.ms-powerpoint", attachment.MediaType);
        Assert.Equal("/download/attachments/100/deck.pptx?version=2", attachment.DownloadPath);

        ConfluenceManifestEntry page = parsed.Entries.Single(e => e.Id == "100");
        Assert.Equal("1", page.ParentId);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), page.When);
    }

    [Fact]
    public void Manifest_PreservesArchivedStatusRatherThanDroppingTheEntry()
    {
        ConfluenceManifest parsed = ConfluenceManifest.FromJson(SampleManifest().ToJson())!;

        ConfluenceManifestEntry archived = parsed.Entries.Single(e => e.Id == "101");
        Assert.True(archived.IsArchived);
        Assert.Equal(ConfluenceEntryStatus.Archived, archived.Status);
        Assert.False(parsed.Entries.Single(e => e.Id == "100").IsArchived);
    }

    [Fact]
    public void Manifest_OfType_PartitionsEntries()
    {
        ConfluenceManifest manifest = SampleManifest();

        Assert.Equal(2, manifest.OfType(ContentTypes.Page).Count());
        Assert.Single(manifest.OfType(ContentTypes.Comment));
        Assert.Single(manifest.OfType(ContentTypes.Attachment));
    }

    [Fact]
    public void Manifest_IncompleteIsDistinguishableFromEmpty()
    {
        ConfluenceManifest incomplete = new() { SpaceKey = "FHIR", Complete = false, Entries = [] };
        ConfluenceManifest completeButEmpty = new() { SpaceKey = "FHIR", Complete = true, Entries = [] };

        ConfluenceManifest? a = ConfluenceManifest.FromJson(incomplete.ToJson());
        ConfluenceManifest? b = ConfluenceManifest.FromJson(completeButEmpty.ToJson());

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.False(a.Complete);
        Assert.True(b.Complete);
        Assert.Empty(a.Entries);
        Assert.Empty(b.Entries);
    }

    [Fact]
    public void Manifest_NullableFileSizeSurvivesTheRoundTrip()
    {
        ConfluenceManifest manifest = new()
        {
            SpaceKey = "FHIR",
            Complete = true,
            Entries =
            [
                new ConfluenceManifestEntry { Id = "1", Type = ContentTypes.Attachment, FileSize = null },
                new ConfluenceManifestEntry { Id = "2", Type = ContentTypes.Attachment, FileSize = 0 },
            ],
        };

        ConfluenceManifest parsed = ConfluenceManifest.FromJson(manifest.ToJson())!;

        Assert.Null(parsed.Entries.Single(e => e.Id == "1").FileSize);
        Assert.Equal(0, parsed.Entries.Single(e => e.Id == "2").FileSize);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("\"a string\"")]
    public void Manifest_FromJson_DegradesToNullWithoutThrowing(string? json)
    {
        Assert.Null(ConfluenceManifest.FromJson(json));
    }

    // ── Space catalog ─────────────────────────────────────────────────

    [Fact]
    public void SpaceCatalog_RoundTrips()
    {
        ConfluenceSpaceCatalog catalog = new()
        {
            DiscoveredAt = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero),
            Complete = true,
            Spaces =
            [
                new ConfluenceCatalogedSpace { Key = "FHIR", Name = "FHIR" },
                new ConfluenceCatalogedSpace { Key = "SOA", Name = "Service Oriented Architecture" },
            ],
        };

        ConfluenceSpaceCatalog? parsed = ConfluenceSpaceCatalog.FromJson(catalog.ToJson());

        Assert.NotNull(parsed);
        Assert.True(parsed.Complete);
        Assert.Equal(["FHIR", "SOA"], parsed.Keys);
        Assert.Equal("Service Oriented Architecture", parsed.Spaces.Single(s => s.Key == "SOA").Name);
    }

    [Fact]
    public void SpaceCatalog_ExplicitlyEmptyIsDistinguishableFromNeverDiscovered()
    {
        ConfluenceSpaceCatalog empty = new() { Complete = true, Spaces = [] };

        ConfluenceSpaceCatalog? parsed = ConfluenceSpaceCatalog.FromJson(empty.ToJson());

        Assert.NotNull(parsed);
        Assert.True(parsed.Complete);
        Assert.Empty(parsed.Spaces);
        Assert.Null(ConfluenceSpaceCatalog.FromJson(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    public void SpaceCatalog_FromJson_DegradesToNullWithoutThrowing(string? json)
    {
        Assert.Null(ConfluenceSpaceCatalog.FromJson(json));
    }

    // ── Sweep attempt ─────────────────────────────────────────────────

    [Fact]
    public void SweepAttempt_RoundTripsEveryOutcome()
    {
        foreach (ConfluenceSweepOutcome outcome in Enum.GetValues<ConfluenceSweepOutcome>())
        {
            ConfluenceSweepAttempt attempt = new()
            {
                SpaceKey = "FHIR",
                StartedAt = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero),
                FinishedAt = outcome == ConfluenceSweepOutcome.Running
                    ? null
                    : new DateTimeOffset(2026, 8, 27, 10, 5, 0, TimeSpan.Zero),
                Outcome = outcome,
                Error = outcome == ConfluenceSweepOutcome.Failed ? "socket closed" : null,
            };

            ConfluenceSweepAttempt? parsed = ConfluenceSweepAttempt.FromJson(attempt.ToJson());

            Assert.NotNull(parsed);
            Assert.Equal(outcome, parsed.Outcome);
            Assert.Equal("FHIR", parsed.SpaceKey);
            Assert.Equal(attempt.FinishedAt, parsed.FinishedAt);
            Assert.Equal(attempt.Error, parsed.Error);
        }
    }

    [Fact]
    public void SweepAttempt_DefaultsToRunning()
    {
        ConfluenceSweepAttempt attempt = new() { SpaceKey = "FHIR" };

        Assert.Equal(ConfluenceSweepOutcome.Running, attempt.Outcome);
        Assert.Null(attempt.FinishedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    public void SweepAttempt_FromJson_DegradesToNullWithoutThrowing(string? json)
    {
        Assert.Null(ConfluenceSweepAttempt.FromJson(json));
    }
}
