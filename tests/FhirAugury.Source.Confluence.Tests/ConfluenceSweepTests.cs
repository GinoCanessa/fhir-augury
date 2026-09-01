using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Drives <see cref="ConfluenceSweep"/> and <see cref="ConfluenceSpaceDiscovery"/>
/// through their injected fetch seam from canned JSON, so the whole acquisition
/// half is testable with no network.
/// </summary>
/// <remarks>
/// The load-bearing assertions are the negative ones: a mid-sweep failure must
/// write <b>no</b> manifest and leave any existing one untouched, because a
/// stale <c>Complete</c> is the false confidence this design exists to remove.
/// </remarks>
public class ConfluenceSweepTests : IDisposable
{
    private const string Space = "FHIR";
    private const string BaseUrl = "https://confluence.test";

    private readonly string _root;
    private readonly FileSystemResponseCache _cache;
    private readonly List<string> _requested = [];

    public ConfluenceSweepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"confluence-sweep-{Guid.NewGuid():N}");
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

    // ── Fixtures ──────────────────────────────────────────────────────

    private static ConfluenceServiceOptions Options(
        List<string>? spaces = null,
        string sweepMaxAge = "00:00:00",
        int sweepPageSize = 2) => new()
        {
            BaseUrl = BaseUrl,
            Spaces = spaces,
            SweepPageSize = sweepPageSize,
            SpaceSweepMaxAge = sweepMaxAge,
        };

    private ConfluenceSweep Sweep(ConfluenceFetch fetch, ConfluenceServiceOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            _cache, NullLogger<ConfluenceSweep>.Instance, fetch);

    private ConfluenceSpaceDiscovery Discovery(ConfluenceFetch fetch, ConfluenceServiceOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            _cache, NullLogger<ConfluenceSpaceDiscovery>.Instance, fetch);

    /// <summary>A fetch that answers from a URL-substring → body table.</summary>
    private ConfluenceFetch Canned(params (string Match, string Body)[] routes) =>
        (url, _) =>
        {
            _requested.Add(url);
            foreach ((string match, string body) in routes)
            {
                if (url.Contains(match, StringComparison.Ordinal))
                {
                    return Task.FromResult(body);
                }
            }

            return Task.FromResult(Envelope());
        };

    private static string Envelope(string? next = null, params string[] results)
    {
        string links = next is null ? "{}" : JsonSerializer.Serialize(new { next });
        return $"{{\"results\":[{string.Join(",", results)}],\"size\":{results.Length},\"limit\":2,\"_links\":{links}}}";
    }

    private static string PageJson(string id, int version = 1, string status = "current") =>
        JsonSerializer.Serialize(new
        {
            id,
            type = "page",
            status,
            title = $"Page {id}",
            version = new { number = version, when = "2026-08-01T00:00:00.000Z" },
        });

    private static string CommentJson(string id, string containerId) =>
        JsonSerializer.Serialize(new
        {
            id,
            type = "comment",
            status = "current",
            title = $"Re: {containerId}",
            version = new { number = 1, when = "2026-08-02T00:00:00.000Z" },
            container = new { id = containerId, type = "page" },
        });

    private static string AttachmentJson(string id, string containerId, long? fileSize) =>
        JsonSerializer.Serialize(new
        {
            id,
            type = "attachment",
            status = "current",
            title = $"file-{id}.pdf",
            version = new { number = 1, when = "2026-08-03T00:00:00.000Z" },
            container = new { id = containerId, type = "page" },
            extensions = new { mediaType = "application/pdf", fileSize },
            _links = new { download = $"/download/attachments/{containerId}/file-{id}.pdf?version=1" },
        }, SkipNulls);

    private static string SpaceJson(string key, string name) =>
        JsonSerializer.Serialize(new { id = 1, key, name, type = "global", status = "current" });

    /// <summary>Omits nulls so an absent fileSize is genuinely absent, not <c>null</c>.</summary>
    private static readonly JsonSerializerOptions SkipNulls = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private ConfluenceManifest? ReadManifest() => ConfluenceReconciler.ReadManifest(Space, _cache);

    private ConfluenceSweepAttempt? ReadAttempt() => ConfluenceReconciler.ReadSweepAttempt(Space, _cache);

    private void Seed(string key, string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        _cache.PutAsync(ConfluenceCacheLayout.SourceName, key, stream, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    // ── Sweep ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SweepSpace_PaginatesToExhaustionAndWritesACompleteManifest()
    {
        ConfluenceFetch fetch = (url, _) =>
        {
            _requested.Add(url);

            if (url.Contains("type=page", StringComparison.Ordinal) && !url.Contains("start=2", StringComparison.Ordinal))
                return Task.FromResult(Envelope("/rest/api/content?type=page&start=2", PageJson("1"), PageJson("2")));
            if (url.Contains("type=page", StringComparison.Ordinal))
                return Task.FromResult(Envelope(null, PageJson("3")));
            if (url.Contains("type%3Dcomment", StringComparison.Ordinal))
                return Task.FromResult(Envelope(null, CommentJson("10", "1")));
            if (url.Contains("type%3Dattachment", StringComparison.Ordinal))
                return Task.FromResult(Envelope(null, AttachmentJson("20", "1", 4096)));

            return Task.FromResult(Envelope());
        };

        ConfluenceSweepResult result = await Sweep(fetch).SweepSpaceAsync(Space, CancellationToken.None);

        Assert.True(result.Succeeded);
        ConfluenceManifest manifest = ReadManifest()!;
        Assert.True(manifest.Complete);
        Assert.Equal(3, manifest.OfType(ContentTypes.Page).Count());
        Assert.Single(manifest.OfType(ContentTypes.Comment));
        Assert.Single(manifest.OfType(ContentTypes.Attachment));
        Assert.Equal(ConfluenceSweepOutcome.Succeeded, ReadAttempt()!.Outcome);
    }

    [Fact]
    public async Task SweepSpace_CapturesEveryFieldTheFillAndReplayNeed()
    {
        ConfluenceFetch fetch = Canned(
            ("type=page", Envelope(null, PageJson("1", version: 7))),
            ("type%3Dcomment", Envelope(null, CommentJson("10", "1"))),
            ("type%3Dattachment", Envelope(null, AttachmentJson("20", "1", 4096))));

        await Sweep(fetch).SweepSpaceAsync(Space, CancellationToken.None);

        ConfluenceManifest manifest = ReadManifest()!;

        ConfluenceManifestEntry page = manifest.Entries.Single(e => e.Id == "1");
        Assert.Equal(7, page.Version);
        Assert.Equal("Page 1", page.Title);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), page.When);

        ConfluenceManifestEntry comment = manifest.Entries.Single(e => e.Id == "10");
        Assert.Equal("1", comment.ContainerId);

        ConfluenceManifestEntry attachment = manifest.Entries.Single(e => e.Id == "20");
        Assert.Equal("1", attachment.ContainerId);
        Assert.Equal("application/pdf", attachment.MediaType);
        Assert.Equal(4096, attachment.FileSize);
        Assert.Equal("/download/attachments/1/file-20.pdf?version=1", attachment.DownloadPath);
    }

    [Fact]
    public async Task SweepSpace_TreatsAnAbsentFileSizeAsNullRatherThanZero()
    {
        ConfluenceFetch fetch = Canned(
            ("type=page", Envelope()),
            ("type%3Dcomment", Envelope()),
            ("type%3Dattachment", Envelope(null, AttachmentJson("20", "1", fileSize: null))));

        await Sweep(fetch).SweepSpaceAsync(Space, CancellationToken.None);

        Assert.Null(ReadManifest()!.Entries.Single().FileSize);
    }

    [Fact]
    public async Task SweepSpace_MidSweepThrow_WritesNoManifestAndRecordsAFailedAttempt()
    {
        ConfluenceFetch fetch = (url, _) =>
        {
            _requested.Add(url);
            if (url.Contains("type%3Dattachment", StringComparison.Ordinal))
                throw new HttpRequestException("connection reset");
            if (url.Contains("type=page", StringComparison.Ordinal))
                return Task.FromResult(Envelope(null, PageJson("1")));
            return Task.FromResult(Envelope());
        };

        ConfluenceSweepResult result = await Sweep(fetch).SweepSpaceAsync(Space, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(ReadManifest());
        Assert.Equal(ConfluenceSweepOutcome.Failed, ReadAttempt()!.Outcome);
        Assert.Contains("connection reset", ReadAttempt()!.Error);
    }

    [Fact]
    public async Task SweepSpace_MidSweepThrow_LeavesAnExistingManifestUntouched()
    {
        ConfluenceManifest previous = new()
        {
            SpaceKey = Space,
            SweptAt = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
            Complete = true,
            Entries = [new ConfluenceManifestEntry { Id = "1", Type = ContentTypes.Page }],
        };
        Seed(ConfluenceCacheLayout.GetManifestCacheKey(Space), previous.ToJson());

        ConfluenceFetch fetch = (_, _) => throw new HttpRequestException("boom");

        await Sweep(fetch).SweepSpaceAsync(Space, CancellationToken.None);

        ConfluenceManifest kept = ReadManifest()!;
        Assert.Equal(previous.SweptAt, kept.SweptAt);
        Assert.Single(kept.Entries);
    }

    [Fact]
    public async Task FailedSweepOverAGoodManifest_MakesTheReconcilerReportUnknown()
    {
        // The end-to-end point of the attempt record: a stale Complete must not
        // survive a failure that came after it.
        ConfluenceManifest previous = new()
        {
            SpaceKey = Space,
            SweptAt = DateTimeOffset.UtcNow.AddHours(-1),
            Complete = true,
            Entries = [],
        };
        Seed(ConfluenceCacheLayout.GetManifestCacheKey(Space), previous.ToJson());

        Assert.Equal(
            ConfluenceSpaceVerdict.Complete,
            ConfluenceReconciler.Reconcile(Space, _cache, ConfluenceReconcilePolicy.Default).Verdict);

        await Sweep((_, _) => throw new HttpRequestException("boom")).SweepSpaceAsync(Space, CancellationToken.None);

        Assert.Equal(
            ConfluenceSpaceVerdict.Unknown,
            ConfluenceReconciler.Reconcile(Space, _cache, ConfluenceReconcilePolicy.Default).Verdict);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task SweepSpace_AuthFailure_AbortsTheRunRatherThanAccumulatingErrors(HttpStatusCode status)
    {
        ConfluenceFetch fetch = (_, _) => throw new HttpRequestException("nope", null, status);

        await Assert.ThrowsAsync<ConfluenceAuthFailureException>(
            () => Sweep(fetch).SweepSpaceAsync(Space, CancellationToken.None));

        Assert.Null(ReadManifest());
    }

    [Fact]
    public async Task SweepSpace_Cancellation_IsRecordedAsAFailedAttempt()
    {
        using CancellationTokenSource cts = new();
        ConfluenceFetch fetch = (url, _) =>
        {
            cts.Cancel();
            return Task.FromResult(Envelope(null, PageJson("1")));
        };

        ConfluenceSweepResult result = await Sweep(fetch).SweepSpaceAsync(Space, cts.Token);

        Assert.False(result.Succeeded);
        Assert.Null(ReadManifest());
        Assert.Equal(ConfluenceSweepOutcome.Failed, ReadAttempt()!.Outcome);
    }

    [Fact]
    public async Task SweepSpace_TagsArchivedEntriesRatherThanDroppingThem()
    {
        ConfluenceFetch fetch = Canned(
            ("type=page", Envelope(null, PageJson("1"), PageJson("2", status: "archived"))),
            ("type%3Dcomment", Envelope()),
            ("type%3Dattachment", Envelope()));

        await Sweep(fetch).SweepSpaceAsync(Space, CancellationToken.None);

        ConfluenceManifest manifest = ReadManifest()!;
        Assert.Equal(2, manifest.Entries.Count);
        Assert.True(manifest.Entries.Single(e => e.Id == "2").IsArchived);
        Assert.False(manifest.Entries.Single(e => e.Id == "1").IsArchived);
    }

    [Fact]
    public async Task SweepSpace_AttemptIsWrittenBeforeTheStreamsAreRead()
    {
        ConfluenceSweepAttempt? observed = null;
        ConfluenceFetch fetch = (_, _) =>
        {
            observed ??= ReadAttempt();
            return Task.FromResult(Envelope());
        };

        await Sweep(fetch).SweepSpaceAsync(Space, CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(ConfluenceSweepOutcome.Running, observed.Outcome);
    }

    [Fact]
    public async Task SweepSpace_HonoursSpaceSweepMaxAgeBySkippingAYoungManifest()
    {
        ConfluenceManifest fresh = new()
        {
            SpaceKey = Space,
            SweptAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Complete = true,
            Entries = [new ConfluenceManifestEntry { Id = "1", Type = ContentTypes.Page }],
        };
        Seed(ConfluenceCacheLayout.GetManifestCacheKey(Space), fresh.ToJson());

        ConfluenceSweepResult result = await Sweep(
            (_, _) => throw new InvalidOperationException("must not fetch"),
            Options(sweepMaxAge: "01:00:00")).SweepSpaceAsync(Space, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.SkippedAsFresh);
        Assert.Empty(_requested);
    }

    [Fact]
    public async Task SweepSpace_DefaultMaxAgeSweepsEveryRun()
    {
        Seed(ConfluenceCacheLayout.GetManifestCacheKey(Space), new ConfluenceManifest
        {
            SpaceKey = Space,
            SweptAt = DateTimeOffset.UtcNow,
            Complete = true,
        }.ToJson());

        ConfluenceSweepResult result = await Sweep(Canned()).SweepSpaceAsync(Space, CancellationToken.None);

        Assert.False(result.SkippedAsFresh);
        Assert.NotEmpty(_requested);
    }

    [Fact]
    public async Task SweepSpace_UsesTheConfiguredSweepPageSize()
    {
        await Sweep(Canned(), Options(sweepPageSize: 175)).SweepSpaceAsync(Space, CancellationToken.None);

        Assert.All(_requested, url => Assert.Contains("limit=175", url, StringComparison.Ordinal));
    }

    // ── Discovery ─────────────────────────────────────────────────────

    [Fact]
    public async Task Discovery_EnumeratesToExhaustionAndWritesTheCatalog()
    {
        ConfluenceFetch fetch = (url, _) =>
        {
            _requested.Add(url);
            return Task.FromResult(url.Contains("start=2", StringComparison.Ordinal)
                ? Envelope(null, SpaceJson("SOA", "Service Oriented Architecture"))
                : Envelope("/rest/api/space?start=2", SpaceJson("FHIR", "FHIR"), SpaceJson("FHIRI", "FHIR Infra")));
        };

        ConfluenceSpaceCatalog catalog = await Discovery(fetch).DiscoverAsync(CancellationToken.None);

        Assert.True(catalog.Complete);
        Assert.Equal(["FHIR", "FHIRI", "SOA"], catalog.Keys);

        ConfluenceSpaceCatalog persisted = ConfluenceReconciler.ReadSpaceCatalog(_cache)!;
        Assert.Equal(["FHIR", "FHIRI", "SOA"], persisted.Keys);
        Assert.Equal("Service Oriented Architecture", persisted.Spaces.Single(s => s.Key == "SOA").Name);
    }

    [Fact]
    public async Task Discovery_CachesEachSpaceSoReplayCanReconstructItExactly()
    {
        await Discovery(Canned(("/rest/api/space", Envelope(null, SpaceJson("FHIR", "FHIR")))))
            .DiscoverAsync(CancellationToken.None);

        Assert.True(_cache.TryGet(
            ConfluenceCacheLayout.SourceName, ConfluenceCacheLayout.GetSpaceCacheKey("FHIR"), out Stream? stream));
        stream!.Dispose();
    }

    [Fact]
    public async Task Discovery_MidEnumerationThrow_WritesNoCatalog()
    {
        ConfluenceFetch fetch = (url, _) =>
        {
            _requested.Add(url);
            if (url.Contains("start=2", StringComparison.Ordinal))
                throw new HttpRequestException("connection reset");
            return Task.FromResult(Envelope("/rest/api/space?start=2", SpaceJson("FHIR", "FHIR")));
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => Discovery(fetch).DiscoverAsync(CancellationToken.None));

        Assert.Null(ConfluenceReconciler.ReadSpaceCatalog(_cache));
    }

    [Fact]
    public async Task Discovery_ExplicitEmptySpaces_WritesAnEmptyCatalogAndFetchesNothing()
    {
        ConfluenceSpaceCatalog catalog = await Discovery(
            (_, _) => throw new InvalidOperationException("must not fetch"),
            Options(spaces: [])).DiscoverAsync(CancellationToken.None);

        Assert.True(catalog.Complete);
        Assert.Empty(catalog.Spaces);
        Assert.NotNull(ConfluenceReconciler.ReadSpaceCatalog(_cache));
        Assert.Empty(_requested);
    }

    [Fact]
    public async Task Discovery_ExplicitSpaceList_BecomesTheCatalogWithoutEnumeratingTheInstance()
    {
        ConfluenceFetch fetch = (url, _) =>
        {
            _requested.Add(url);
            return Task.FromResult(url.EndsWith("/SOA", StringComparison.Ordinal)
                ? SpaceJson("SOA", "Service Oriented Architecture")
                : SpaceJson("FHIR", "FHIR"));
        };

        ConfluenceSpaceCatalog catalog = await Discovery(fetch, Options(spaces: ["FHIR", "SOA"]))
            .DiscoverAsync(CancellationToken.None);

        Assert.Equal(["FHIR", "SOA"], catalog.Keys);
        Assert.Equal(2, _requested.Count);
        Assert.All(_requested, url => Assert.Contains("/rest/api/space/", url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Discovery_KeepsAConfiguredSpaceWhoseMetadataCallFails()
    {
        // Dropping it would silently tombstone everything in that space.
        ConfluenceFetch fetch = (url, _) =>
        {
            _requested.Add(url);
            if (url.EndsWith("/SOA", StringComparison.Ordinal))
                throw new HttpRequestException("500");
            return Task.FromResult(SpaceJson("FHIR", "FHIR"));
        };

        ConfluenceSpaceCatalog catalog = await Discovery(fetch, Options(spaces: ["FHIR", "SOA"]))
            .DiscoverAsync(CancellationToken.None);

        Assert.Equal(["FHIR", "SOA"], catalog.Keys);
    }

    [Fact]
    public async Task Discovery_AuthFailureOnAConfiguredSpace_AbortsTheRun()
    {
        ConfluenceFetch fetch = (_, _) =>
            throw new HttpRequestException("nope", null, HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<ConfluenceAuthFailureException>(
            () => Discovery(fetch, Options(spaces: ["FHIR"])).DiscoverAsync(CancellationToken.None));
    }

    // ── Auth failure helper ───────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void AuthFailure_RecognizesOnly401And403(HttpStatusCode status, bool expected)
    {
        Assert.Equal(expected, ConfluenceAuthFailure.IsAuthFailure(
            new HttpRequestException("x", null, status)));
    }

    [Fact]
    public void AuthFailure_LooksThroughWrappedExceptions()
    {
        Exception wrapped = new InvalidOperationException(
            "outer", new HttpRequestException("inner", null, HttpStatusCode.Forbidden));

        Assert.True(ConfluenceAuthFailure.IsAuthFailure(wrapped));
    }

    [Fact]
    public void AuthFailure_IgnoresAnExceptionWithNoStatusCode()
    {
        Assert.False(ConfluenceAuthFailure.IsAuthFailure(new HttpRequestException("socket closed")));
    }
}
