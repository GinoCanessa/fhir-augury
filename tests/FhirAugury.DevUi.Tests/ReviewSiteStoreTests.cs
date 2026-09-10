using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.DevUi.Configuration;
using FhirAugury.DevUi.Models;
using FhirAugury.DevUi.Services;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Publishing.Tickets;
using Microsoft.Extensions.Options;

namespace FhirAugury.DevUi.Tests;

public sealed class ReviewSiteStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"fhir-augury-review-store-{Guid.NewGuid():N}");

    public ReviewSiteStoreTests() =>
        Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ResolvesOnlyRunScopedPairAndSiteCoordinates()
    {
        ReviewSiteStore store = CreateStore();
        store.EnsureRoots();

        ReviewSiteCoordinates coordinates =
            store.GetCoordinates("prepare", "run-123");

        Assert.Equal(
            Path.Combine(
                _root,
                "cache",
                "snapshots",
                "prepare",
                "run-123"),
            coordinates.SnapshotPairDirectory);
        Assert.Equal(
            Path.Combine(
                _root,
                "cache",
                "sites",
                "prepare",
                "run-123",
                "discussion",
                TicketSiteManifest.FileName),
            coordinates.SiteManifestPath);
        Assert.Equal(
            "/review-sites/prepare/run-123/discussion/",
            coordinates.SiteUrl);
    }

    [Fact]
    public async Task StaticProviderServesOnlyValidatedSiteCoordinates()
    {
        ReviewSiteStore store = CreateStore();
        store.EnsureRoots();
        ReviewSiteCoordinates coordinates =
            store.GetCoordinates("prepare", "run-123");
        Directory.CreateDirectory(coordinates.SiteDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(
                coordinates.SiteDirectory,
                "index.html"),
            "<!doctype html>");
        await File.WriteAllTextAsync(
            Path.Combine(
                _root,
                "cache",
                "sites",
                "unscoped.html"),
            "not a review site");
        using ReviewSiteFileProvider provider = new(store);

        Assert.True(
            provider.GetFileInfo(
                    "/prepare/run-123/discussion/index.html")
                .Exists);
        Assert.False(
            provider.GetFileInfo("/unscoped.html").Exists);
        Assert.False(
            provider.GetFileInfo(
                    "/prepare/run-123/applying/index.html")
                .Exists);
        Assert.False(
            provider.GetFileInfo(
                    "/prepare/run-123/discussion/../index.html")
                .Exists);
    }

    [Fact]
    public async Task StaticProviderServesOnlyPublisherChooserArtifactsAtRunRoot()
    {
        ReviewSiteStore store = CreateStore();
        store.EnsureRoots();
        ReviewSiteCoordinates coordinates =
            store.GetCoordinates("prepare", "run-123");
        string assetsDirectory =
            Path.Combine(coordinates.SiteRoot, "assets");
        Directory.CreateDirectory(assetsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(coordinates.SiteRoot, "index.html"),
            "<!doctype html>");
        await File.WriteAllTextAsync(
            Path.Combine(assetsDirectory, "chooser.css"),
            "body {}");
        await File.WriteAllTextAsync(
            Path.Combine(coordinates.SiteRoot, "other.html"),
            "<!doctype html>");
        await File.WriteAllTextAsync(
            Path.Combine(assetsDirectory, "other.css"),
            "body {}");
        using ReviewSiteFileProvider provider = new(store);

        Assert.True(
            provider.GetFileInfo(
                    "/prepare/run-123/index.html")
                .Exists);
        Assert.True(
            provider.GetFileInfo(
                    "/prepare/run-123/assets/chooser.css")
                .Exists);
        Assert.False(
            provider.GetFileInfo(
                    "/prepare/run-123/other.html")
                .Exists);
        Assert.False(
            provider.GetFileInfo(
                    "/prepare/run-123/assets/other.css")
                .Exists);
    }

    [Fact]
    public async Task StaticProviderRejectsDescendantReparsePoints()
    {
        string reparsePoint = Path.Combine(
            _root,
            "cache",
            "sites",
            "prepare",
            "run-1",
            "discussion",
            "assets");
        ReviewSiteStore store =
            CreateStore(new ReparseFileSystemInspector(
                reparsePoint));
        store.EnsureRoots();
        Directory.CreateDirectory(reparsePoint);
        await File.WriteAllTextAsync(
            Path.Combine(reparsePoint, "app.css"),
            "body {}");
        using ReviewSiteFileProvider provider = new(store);

        Assert.False(
            provider.GetFileInfo(
                    "/prepare/run-1/discussion/assets/app.css")
                .Exists);
    }

    [Fact]
    public async Task StaticProviderRejectsChooserReparsePoints()
    {
        string reparsePoint = Path.Combine(
            _root,
            "cache",
            "sites",
            "prepare",
            "run-1",
            "assets");
        ReviewSiteStore store =
            CreateStore(new ReparseFileSystemInspector(
                reparsePoint));
        store.EnsureRoots();
        Directory.CreateDirectory(reparsePoint);
        await File.WriteAllTextAsync(
            Path.Combine(reparsePoint, "chooser.css"),
            "body {}");
        using ReviewSiteFileProvider provider = new(store);

        Assert.False(
            provider.GetFileInfo(
                    "/prepare/run-1/assets/chooser.css")
                .Exists);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("run:stream")]
    [InlineData("../other")]
    [InlineData("trailing.")]
    public void RejectsReservedAdsAndTraversalRunIdentifiers(
        string runId)
    {
        ReviewSiteStore store = CreateStore();
        store.EnsureRoots();

        Assert.ThrowsAny<ArgumentException>(
            () => store.GetCoordinates("prepare", runId));
    }

    [Fact]
    public void RejectsSimulatedReparseTraversal()
    {
        string reparsePoint = Path.Combine(
            _root,
            "cache",
            "sites",
            "prepare");
        ReviewSiteStore store =
            CreateStore(new ReparseFileSystemInspector(
                reparsePoint));
        store.EnsureRoots();

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(
                () => store.GetCoordinates(
                    "prepare",
                    "run-1"));

        Assert.Contains(
            "reparse point",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifiedPairIsBoundToWorkflowServiceAndRun()
    {
        ReviewSiteStore store = CreateStore();
        store.EnsureRoots();
        ReviewSiteCoordinates coordinates =
            store.GetCoordinates("prepare", "run-1");
        await CreateVerifiedPairAsync(
            coordinates.SnapshotPairDirectory,
            "Planner",
            "run-1");

        VerifiedAuthoringSnapshotPair? opened =
            await store.TryOpenVerifiedPairAsync(
                coordinates);

        Assert.Null(opened);
    }

    [Fact]
    public async Task ReconstructsExactManifestAfterRestart()
    {
        ReviewSiteStore first = CreateStore();
        first.EnsureRoots();
        ReviewSiteCoordinates coordinates =
            first.GetCoordinates("plan", "run-9");
        VerifiedAuthoringSnapshotPair pair =
            await CreateVerifiedPairAsync(
                coordinates.SnapshotPairDirectory,
                "Planner",
                "run-9");
        await WriteSiteAsync(coordinates, pair);

        ReviewSiteStore restarted = CreateStore();
        ReviewSitePublication? publication =
            await restarted.TryReconstructAsync(
                "plan",
                "run-9");

        Assert.NotNull(publication);
        Assert.True(publication.Reconstructed);
        Assert.Equal(
            pair.SnapshotId,
            publication.Manifest.SnapshotId);
        Assert.Equal(
            coordinates.SiteUrl,
            publication.Coordinates.SiteUrl);
    }

    [Fact]
    public async Task DoesNotScanForManifestOutsideExactSubSite()
    {
        ReviewSiteStore store = CreateStore();
        store.EnsureRoots();
        ReviewSiteCoordinates coordinates =
            store.GetCoordinates("prepare", "run-2");
        Directory.CreateDirectory(coordinates.SiteRoot);
        await File.WriteAllTextAsync(
            Path.Combine(
                coordinates.SiteRoot,
                TicketSiteManifest.FileName),
            "{}");

        ReviewSitePublication? publication =
            await store.TryReconstructAsync(
                "prepare",
                "run-2");

        Assert.Null(publication);
    }

    [Fact]
    public async Task RejectsManifestWithDifferentOutputCoordinates()
    {
        ReviewSiteStore store = CreateStore();
        store.EnsureRoots();
        ReviewSiteCoordinates coordinates =
            store.GetCoordinates("prepare", "run-3");
        VerifiedAuthoringSnapshotPair pair =
            await CreateVerifiedPairAsync(
                coordinates.SnapshotPairDirectory,
                "Preparer",
                "run-3");
        await WriteSiteAsync(
            coordinates,
            pair,
            outputPath: Path.Combine(_root, "elsewhere"));

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.TryReconstructAsync(
                    "prepare",
                    "run-3"));

        Assert.Contains(
            "coordinates",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private ReviewSiteStore CreateStore(
        IFileSystemInspector? fileSystem = null)
    {
        string cache = Path.Combine(_root, "cache");
        DevUiOptions options = new()
        {
            CacheRoot = cache,
            SnapshotCacheRoot =
                Path.Combine(cache, "snapshots"),
            ReviewSitesRoot =
                Path.Combine(cache, "sites"),
        };
        return new ReviewSiteStore(
            Options.Create(options),
            new TicketWorkflowCatalog(),
            fileSystem ??
                new PhysicalFileSystemInspector());
    }

    private static async Task<VerifiedAuthoringSnapshotPair>
        CreateVerifiedPairAsync(
            string directory,
            string serviceName,
            string runId)
    {
        Directory.CreateDirectory(directory);
        string databaseFileName = "snapshot.db";
        string descriptorFileName =
            $"{databaseFileName}.descriptor.json";
        byte[] databaseBytes = "verified snapshot bytes"u8.ToArray();
        string databaseSha = Hash(databaseBytes);
        AuthoringSnapshotDescriptor descriptor = new(
            "jira-fhir",
            runId,
            $"snapshot-{runId}",
            7,
            4,
            1,
            databaseSha,
            databaseBytes.LongLength,
            2,
            2,
            new Dictionary<string, long>(),
            databaseFileName,
            DateTimeOffset.UtcNow);
        byte[] descriptorBytes =
            JsonSerializer.SerializeToUtf8Bytes(
                descriptor,
                JsonOptions);
        AuthoringSnapshotPairManifest ready = new(
            AuthoringSnapshotPairManifest.CurrentFormatVersion,
            serviceName,
            runId,
            descriptor.SnapshotId,
            descriptorFileName,
            databaseFileName,
            databaseBytes.LongLength,
            Hash(descriptorBytes),
            databaseSha);

        await File.WriteAllBytesAsync(
            Path.Combine(directory, databaseFileName),
            databaseBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(directory, descriptorFileName),
            descriptorBytes);
        await File.WriteAllTextAsync(
            Path.Combine(
                directory,
                AuthoringSnapshotPairManifest.ReadyFileName),
            JsonSerializer.Serialize(ready, JsonOptions));
        return await new AuthoringSnapshotPairVerifier()
            .VerifyReadyPairAsync(
                serviceName,
                runId,
                directory);
    }

    private static async Task WriteSiteAsync(
        ReviewSiteCoordinates coordinates,
        VerifiedAuthoringSnapshotPair pair,
        string? outputPath = null)
    {
        Directory.CreateDirectory(
            coordinates.SiteDirectory);
        TicketSiteManifest manifest = new(
            coordinates.Workflow.SiteKind ==
                TicketSiteKind.Discussion
                ? "preparer"
                : "planner",
            pair.Descriptor.ProcessorKind,
            pair.RunId,
            pair.SnapshotId,
            pair.Descriptor.AuthoringEpoch,
            pair.Descriptor.Sequence,
            pair.Descriptor.SchemaVersion,
            pair.Descriptor.Sha256,
            pair.Descriptor.SizeBytes,
            pair.Descriptor.Sha256,
            pair.Descriptor.SizeBytes,
            pair.Descriptor.ItemCount,
            pair.Descriptor.ReceiptCount,
            pair.Descriptor.TableCounts,
            new TicketSiteManifestFilters(null, null, null),
            coordinates.Workflow.SiteTitle,
            "assets-v1",
            "build-v1",
            outputPath ?? coordinates.SiteDirectory,
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(
            coordinates.SiteManifestPath,
            TicketSiteManifest.ToSummaryJson(manifest));
        await File.WriteAllTextAsync(
            Path.Combine(
                coordinates.SiteDirectory,
                "index.html"),
            "<!doctype html>");
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();

    private sealed class ReparseFileSystemInspector(
        string reparsePoint) : IFileSystemInspector
    {
        public bool Exists(string path) =>
            DevUiPathGuard.PathsEqual(path, reparsePoint) ||
            File.Exists(path) ||
            Directory.Exists(path);

        public FileAttributes GetAttributes(string path) =>
            DevUiPathGuard.PathsEqual(path, reparsePoint)
                ? FileAttributes.Directory |
                    FileAttributes.ReparsePoint
                : File.GetAttributes(path);

        public void CreateDirectory(string path) =>
            Directory.CreateDirectory(path);
    }
}
