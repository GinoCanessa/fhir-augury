using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FhirAugury.Common.IO;
using FhirAugury.Common.Text;
using FhirAugury.DevUi.Configuration;
using FhirAugury.DevUi.Models;
using FhirAugury.DevUi.Services;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Publishing.Tickets;
using Microsoft.Data.Sqlite;
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReconstructsLegacyDiscussionManifestWithoutRewritingFiles(
        int rendererSchemaVersion)
    {
        ReviewSiteStore first = CreateStore();
        first.EnsureRoots();
        ReviewSiteCoordinates coordinates =
            first.GetCoordinates("prepare", "run-10");
        VerifiedAuthoringSnapshotPair pair =
            await CreateVerifiedPairAsync(
                coordinates.SnapshotPairDirectory,
                "Preparer",
                "run-10",
                schemaVersion: 2);
        DateTimeOffset sourceRefresh =
            new(2026, 9, 8, 5, 0, 0, TimeSpan.Zero);
        await WriteSiteAsync(
            coordinates,
            pair,
            displayTitle:
                "Tickets for Discussion - Built September 08, 2026",
            jiraSourceLastSuccessfulRefreshAt: sourceRefresh,
            rendererSchemaVersion: rendererSchemaVersion);
        string assetsDirectory = Path.Combine(coordinates.SiteDirectory, "assets");
        Directory.CreateDirectory(assetsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(assetsDirectory, "app.js"),
            $"window.legacyRendererVersion = {rendererSchemaVersion};");
        Dictionary<string, string> originalFiles = ReadFileHashes();

        ReviewSiteStore restarted = CreateStore();
        ReviewSitePublication? publication =
            await restarted.TryReconstructAsync(
                "prepare",
                "run-10");

        Assert.NotNull(publication);
        Assert.True(publication.Reconstructed);
        Assert.Equal(
            coordinates.Workflow.SiteTitle,
            publication.Manifest.Title);
        Assert.Equal(
            "Tickets for Discussion - Built September 08, 2026",
            publication.Manifest.DisplayTitle);
        Assert.Equal(
            sourceRefresh,
            publication.Manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Equal(
            rendererSchemaVersion,
            publication.Manifest.RendererSchemaVersion);
        Assert.Null(publication.Manifest.DiscussionCorpus);
        Assert.Null(publication.Manifest.DiscussionReadiness);
        AssertFileHashesUnchanged(originalFiles);
        using ReviewSiteFileProvider provider = new(restarted);
        Assert.True(
            provider.GetFileInfo(
                    "/prepare/run-10/discussion/index.html")
                .Exists);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReconstructsRendererV3ExactCorpusAndStableBaseTitleAfterRestart(
        int snapshotSchemaVersion)
    {
        ReviewSiteStore first = CreateStore();
        first.EnsureRoots();
        ReviewSiteCoordinates coordinates = first.GetCoordinates("prepare", "corpus-run");
        (VerifiedAuthoringSnapshotPair pair, TicketSiteManifest original) =
            await PublishDiscussionSiteAsync(coordinates, snapshotSchemaVersion);
        Dictionary<string, string> originalFiles = ReadFileHashes();

        ReviewSitePublication publication = Assert.IsType<ReviewSitePublication>(
            await CreateStore().TryReconstructAsync("prepare", "corpus-run"));

        Assert.True(publication.Reconstructed);
        Assert.Equal(
            TicketSiteManifest.ToSummaryJson(original),
            TicketSiteManifest.ToSummaryJson(publication.Manifest));
        Assert.Equal("Tickets for Discussion", publication.Manifest.Title);
        Assert.Equal("Tickets for Discussion - Sept 15, 2026", publication.Manifest.DisplayTitle);
        Assert.Equal(snapshotSchemaVersion, publication.Manifest.SnapshotSchemaVersion);
        Assert.Equal(3, publication.Manifest.RendererSchemaVersion);
        Assert.Equal(1, pair.Descriptor.ItemCount);
        Assert.Equal(2, publication.Manifest.IncludedItemCount);
        DiscussionCorpusSummary expected = new(
            2, 2, 2,
            new DateTimeOffset(2026, 9, 15, 0, 30, 0, TimeSpan.Zero),
            DiscussionDateCoverage.Complete,
            snapshotSchemaVersion == 3 ? 1 : 0,
            0,
            snapshotSchemaVersion == 3 ? 1 : 0,
            [
                new("github", 0, 0, 0, 0),
                new("jira", 0, 0, 0, 0),
                new("jira-xref", 0, 0, 0, 0),
                new("repo", 3, 1, 1, 1),
                new("zulip", 0, 0, 0, 0),
            ]);
        Assert.Equal(
            JsonSerializer.Serialize(expected, JsonOptions),
            JsonSerializer.Serialize(publication.Manifest.DiscussionCorpus, JsonOptions));
        Assert.Equal(
            snapshotSchemaVersion == 3,
            Assert.IsType<DiscussionPublicationReadiness>(publication.Manifest.DiscussionReadiness).IsReady);
        Assert.Equal(
            snapshotSchemaVersion == 1
                ? (DateTimeOffset?)null
                : new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero),
            publication.Manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Equal(original.BuildIdentity, publication.Manifest.BuildIdentity);
        AssertFileHashesUnchanged(originalFiles);
    }

    [Theory]
    [InlineData("missing-corpus")]
    [InlineData("missing-readiness")]
    [InlineData("unknown-renderer")]
    [InlineData("ticket-count")]
    [InlineData("project-count")]
    [InlineData("date-coverage")]
    [InlineData("missing-maximum")]
    [InlineData("non-utc-maximum")]
    [InlineData("people-count")]
    [InlineData("link-partition")]
    [InlineData("different-valid-coverage")]
    [InlineData("different-display-title")]
    [InlineData("different-build-identity")]
    public async Task RejectsInvalidRendererV3ManifestWithoutDiscardingItsError(
        string defect)
    {
        ReviewSiteStore store = CreateStore();
        store.EnsureRoots();
        ReviewSiteCoordinates coordinates = store.GetCoordinates("prepare", "invalid-corpus-run");
        (_, TicketSiteManifest original) = await PublishDiscussionSiteAsync(coordinates);
        DiscussionCorpusSummary corpus = Assert.IsType<DiscussionCorpusSummary>(original.DiscussionCorpus);
        TicketSiteManifest invalid = defect switch
        {
            "missing-corpus" => original with { DiscussionCorpus = null },
            "missing-readiness" => original with { DiscussionReadiness = null },
            "unknown-renderer" => original with { RendererSchemaVersion = 4 },
            "ticket-count" => original with { DiscussionCorpus = corpus with { TicketCount = 1 } },
            "project-count" => original with { DiscussionCorpus = corpus with { ExportedProjectCount = 3 } },
            "date-coverage" => original with { DiscussionCorpus = corpus with { DateCoverage = DiscussionDateCoverage.Partial } },
            "missing-maximum" => original with { DiscussionCorpus = corpus with { MaxJiraUpdatedAt = null } },
            "non-utc-maximum" => original with
            {
                DiscussionCorpus = corpus with
                {
                    MaxJiraUpdatedAt = new DateTimeOffset(2026, 9, 14, 20, 30, 0, TimeSpan.FromHours(-4)),
                },
            },
            "people-count" => original with { DiscussionCorpus = corpus with { TicketsWithPublicRequester = 3 } },
            "link-partition" => original with
            {
                DiscussionCorpus = corpus with
                {
                    LinksByKind = corpus.LinksByKind
                        .Select(links => links.Kind == "repo"
                            ? links with { ResolvedSafeLinks = 2 }
                            : links)
                        .ToArray(),
                },
            },
            "different-valid-coverage" => original with
            {
                DiscussionCorpus = corpus with { TicketsWithPublicReporter = 0 },
            },
            "different-display-title" => original with { DisplayTitle = original.Title },
            "different-build-identity" => original with { BuildIdentity = new string('0', 64) },
            _ => throw new InvalidOperationException($"Unknown manifest defect '{defect}'."),
        };
        string invalidJson = TicketSiteManifest.ToSummaryJson(invalid);
        await File.WriteAllTextAsync(coordinates.SiteManifestPath, invalidJson);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.TryReconstructAsync("prepare", "invalid-corpus-run"));

        Assert.Contains("Discussion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(invalidJson, await File.ReadAllTextAsync(coordinates.SiteManifestPath));
    }

    [Fact]
    public async Task MatchingManifestAndPresentationChangesCannotReuseCommittedBuildIdentity()
    {
        ReviewSiteStore store = CreateStore();
        store.EnsureRoots();
        ReviewSiteCoordinates coordinates = store.GetCoordinates("prepare", "changed-corpus-run");
        (_, TicketSiteManifest original) = await PublishDiscussionSiteAsync(coordinates);
        DiscussionCorpusSummary corpus = Assert.IsType<DiscussionCorpusSummary>(original.DiscussionCorpus);
        Assert.Equal(1, corpus.TicketsWithPublicReporter);
        TicketSiteManifest changed = original with
        {
            DiscussionCorpus = corpus with { TicketsWithPublicReporter = 0 },
        };
        await File.WriteAllTextAsync(coordinates.SiteManifestPath, TicketSiteManifest.ToSummaryJson(changed));
        string indexPath = Path.Combine(coordinates.SiteDirectory, "index.html");
        string html = await File.ReadAllTextAsync(indexPath);
        const string originalFact = "\"ticketsWithPublicReporter\":1";
        Assert.Contains(originalFact, html, StringComparison.Ordinal);
        await File.WriteAllTextAsync(indexPath,
            html.Replace(originalFact, "\"ticketsWithPublicReporter\":0", StringComparison.Ordinal));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.TryReconstructAsync("prepare", "changed-corpus-run"));

        Assert.Contains("build identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original.BuildIdentity, changed.BuildIdentity);
    }

    [Fact]
    public async Task MalformedRendererV3CorpusJsonIsNotTreatedAsLegacy()
    {
        ReviewSiteStore store = CreateStore();
        store.EnsureRoots();
        ReviewSiteCoordinates coordinates = store.GetCoordinates("prepare", "malformed-corpus-run");
        (_, TicketSiteManifest manifest) = await PublishDiscussionSiteAsync(coordinates);
        JsonObject json = Assert.IsType<JsonObject>(
            JsonNode.Parse(TicketSiteManifest.ToSummaryJson(manifest)));
        json["discussionCorpus"] = "not a corpus summary";
        string malformed = json.ToJsonString();
        await File.WriteAllTextAsync(coordinates.SiteManifestPath, malformed);

        await Assert.ThrowsAsync<JsonException>(
            () => store.TryReconstructAsync("prepare", "malformed-corpus-run"));

        Assert.Equal(malformed, await File.ReadAllTextAsync(coordinates.SiteManifestPath));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("presentation")]
    public async Task RendererV3ReconstructionRequiresMatchingCommittedArtifacts(string artifact)
    {
        ReviewSiteStore store = CreateStore();
        store.EnsureRoots();
        ReviewSiteCoordinates coordinates = store.GetCoordinates("prepare", "artifact-run");
        (_, TicketSiteManifest manifest) = await PublishDiscussionSiteAsync(coordinates);
        if (artifact == "version")
        {
            StagedDirectoryVersion wrongVersion = StagedDirectoryVersion.Snapshot(
                "ticket-site:preparer", manifest.ProcessorKind, manifest.SnapshotSequence,
                "unrelated-snapshot", manifest.BuildIdentity);
            await File.WriteAllTextAsync(
                Path.Combine(coordinates.SiteDirectory, StagedDirectoryPublisher.VersionFileName),
                JsonSerializer.Serialize(wrongVersion, JsonOptions));
        }
        else
        {
            await File.WriteAllTextAsync(
                Path.Combine(coordinates.SiteDirectory, "index.html"),
                "<!doctype html><title>Unrelated output</title>");
        }

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.TryReconstructAsync("prepare", "artifact-run"));

        Assert.Contains(artifact, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsDiscussionManifestWhenDisplayTitleReplacesStableTitle()
    {
        ReviewSiteStore store = CreateStore();
        store.EnsureRoots();
        ReviewSiteCoordinates coordinates =
            store.GetCoordinates("prepare", "run-11");
        VerifiedAuthoringSnapshotPair pair =
            await CreateVerifiedPairAsync(
                coordinates.SnapshotPairDirectory,
                "Preparer",
                "run-11",
                schemaVersion: 2);
        string displayTitle =
            "Tickets for Discussion - Built September 08, 2026";
        await WriteSiteAsync(
            coordinates,
            pair,
            stableTitle: displayTitle,
            displayTitle: displayTitle,
            jiraSourceLastSuccessfulRefreshAt:
                new DateTimeOffset(
                    2026,
                    9,
                    8,
                    5,
                    0,
                    0,
                    TimeSpan.Zero),
            rendererSchemaVersion: 2);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.TryReconstructAsync(
                    "prepare",
                    "run-11"));

        Assert.Contains(
            "workflow-bound snapshot",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
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

    private Dictionary<string, string> ReadFileHashes() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(_root, path),
                path => Hash(File.ReadAllBytes(path)),
                StringComparer.Ordinal);

    private void AssertFileHashesUnchanged(Dictionary<string, string> before)
    {
        Dictionary<string, string> after = ReadFileHashes();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach ((string path, string hash) in before)
        {
            Assert.Equal(hash, after[path]);
        }
    }

    private static async Task<(VerifiedAuthoringSnapshotPair Pair, TicketSiteManifest Manifest)>
        PublishDiscussionSiteAsync(
            ReviewSiteCoordinates coordinates,
            int schemaVersion = 3)
    {
        Directory.CreateDirectory(coordinates.SnapshotPairDirectory);
        string databasePath = Path.Combine(coordinates.SnapshotPairDirectory, "snapshot.db");
        AuthoringSnapshotSchemaCatalog catalog = PreparedTicketSnapshotSchemaResolver.Resolve(schemaVersion);
        DateTimeOffset createdAt = new(2026, 9, 25, 14, 0, 0, TimeSpan.Zero);
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        await using (SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ToString()))
        {
            await connection.OpenAsync();
            foreach (AuthoringSnapshotTableSchema table in catalog.Tables)
            {
                await using SqliteCommand create = connection.CreateCommand();
                create.CommandText = $"CREATE TABLE \"{table.Name}\" (" +
                    string.Join(", ", table.Columns.Select(column => column == "RowId"
                        ? "\"RowId\" INTEGER PRIMARY KEY"
                        : $"\"{column}\"")) + ")";
                await create.ExecuteNonQueryAsync();
            }

            await using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText =
                """
                INSERT INTO authoring_runs
                    (Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly, TotalItems,
                     CreatedAt, StartedAt, CompletedAt, SnapshotId)
                VALUES
                    (@runId, 'jira-fhir', 7, 'finalizing', 0, 1, @createdAt, @createdAt, NULL, @snapshotId),
                    ('historical-run', 'jira-fhir', 7, 'completed', 0, 1, @createdAt, @createdAt, @createdAt, NULL);
                INSERT INTO authoring_run_items
                    (Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                     AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt, CompletedAt)
                VALUES
                    ('item-1', @runId, 'FHIR-1001', 'ticket', 'rev-1', 'complete', 'receipt-1', 1, @createdAt, @createdAt, @createdAt),
                    ('item-2', 'historical-run', 'BALLOT-2002', 'ticket', 'rev-2', 'complete', 'receipt-2', 1, @createdAt, @createdAt, @createdAt);
                INSERT INTO authoring_result_receipts
                    (Id, OperationId, RunId, RunItemId, BusinessKey, ContentHash,
                     ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt)
                VALUES
                    ('receipt-1', 'operation-1', @runId, 'item-1', 'FHIR-1001', 'hash-1', 'rev-1', 'rev-1', 7, @createdAt),
                    ('receipt-2', 'operation-2', 'historical-run', 'item-2', 'BALLOT-2002', 'hash-2', 'rev-2', 'rev-2', 7, @createdAt);
                INSERT INTO prepared_tickets
                    (Id, Key, RequestSummary, CommentSummary, LinkedTicketSummary, RelatedTicketSummary,
                     RelatedZulipSummary, RelatedGitHubSummary, ExistingProposed, ProposalA,
                     ProposalAJustification, ProposalAImpact, ProposalB, ProposalBJustification,
                     ProposalBImpact, ProposalC, ProposalCJustification, Recommendation,
                     RecommendationJustification, SavedAt)
                VALUES
                    ('prepared-1', 'FHIR-1001', 'Request one', '', '', '', '', '', '', 'A', 'Because',
                     'Non-substantive', 'B', 'Because', 'Compatible, substantive', 'C', 'Because', 'A', 'Because', @createdAt),
                    ('prepared-2', 'BALLOT-2002', 'Request two', '', '', '', '', '', '', 'A', 'Because',
                     'Non-substantive', 'B', 'Because', 'Compatible, substantive', 'C', 'Because', 'A', 'Because', @createdAt);
                INSERT INTO prepared_ticket_hydration
                    (Id, TicketKey, Priority, Resolution, Specification, CommentCount,
                     DescriptionPlain, Reporter, HydratedAt, HydrationStatus)
                VALUES
                    ('hydration-1', 'FHIR-1001', 'Major', 'Persuasive', 'FHIR', 0, 'Request one', 'Ada Lovelace', @createdAt, 'resolved'),
                    ('hydration-2', 'BALLOT-2002', 'Major', 'Persuasive', 'FHIR', 0, 'Request two', '', @createdAt, 'resolved');
                INSERT INTO prepared_jira_hydration
                    (Id, TicketKey, JiraKey, Title, Status, Type, WorkGroup, WorkGroupClean,
                     Specification, UpdatedAt, Url, HydratedAt, HydrationStatus)
                VALUES
                    ('jira-1', 'FHIR-1001', 'FHIR-1001', 'First ticket', 'Open', 'Change Request',
                     'FHIR Infrastructure', 'FHIRInfrastructure', 'FHIR', '2026-09-14T23:00:00+00:00',
                     'https://jira.hl7.org/browse/FHIR-1001', @createdAt, 'resolved'),
                    ('jira-2', 'BALLOT-2002', 'BALLOT-2002', 'Second ticket', 'Open', 'Change Request',
                     'FHIR Infrastructure', 'FHIRInfrastructure', 'FHIR', '2026-09-14T20:30:00-04:00',
                     'https://jira.hl7.org/browse/BALLOT-2002', @createdAt, 'resolved');
                INSERT INTO prepared_ticket_repos (Id, TicketKey, Repo, RepoCategory, Justification)
                VALUES
                    ('repo-1', 'FHIR-1001', 'hl7/fhir', 'core', 'Resolved repository'),
                    ('repo-2', 'FHIR-1001', 'hl7/retained', 'core', 'Last known repository'),
                    ('repo-3', 'FHIR-1001', 'hl7/missing', 'core', 'Unresolved repository');
                INSERT INTO prepared_repo_hydration
                    (Id, TicketKey, Repo, Url, HydratedAt, HydrationStatus, HydrationReason)
                VALUES
                    ('repo-hydration-1', 'FHIR-1001', 'hl7/fhir', 'https://github.com/hl7/fhir', @createdAt, 'resolved', NULL),
                    ('repo-hydration-2', 'FHIR-1001', 'hl7/retained', 'https://github.com/hl7/retained', @createdAt, 'unresolved', 'Lookup failed; retained URL'),
                    ('repo-hydration-3', 'FHIR-1001', 'hl7/missing', NULL, @createdAt, 'unresolved', 'No usable URL');
                INSERT INTO prepared_ticket_partition_receipts
                    (RunId, StageId, PartitionKey, InputFingerprint, TopicRows, TopicGroupRows, MemberRows, PersistedAt)
                VALUES
                    (@runId, 'grouping', 'FHIRInfrastructure|FHIR|Change Request', 'grouping-fingerprint', 0, 0, 0, @createdAt);
                INSERT INTO jira_review_workgroups (Code, Name, NameClean, UpdatedAt)
                VALUES ('fhir-i', 'FHIR Infrastructure', 'FHIRInfrastructure', @createdAt);
                """;
            seed.Parameters.AddWithValue("@runId", coordinates.RunId);
            seed.Parameters.AddWithValue("@snapshotId", $"snapshot-{coordinates.RunId}");
            seed.Parameters.AddWithValue("@createdAt", createdAt.ToString("O"));
            await seed.ExecuteNonQueryAsync();
            if (schemaVersion >= 2)
            {
                seed.CommandText =
                    """
                    UPDATE prepared_ticket_hydration
                    SET SourceProject = CASE TicketKey WHEN 'FHIR-1001' THEN 'FHIR' ELSE 'BALLOT' END,
                        Assignee = '', SourceLastSuccessfulRefreshAt = '2026-09-20T12:00:00+00:00',
                        SourceContentRevision = 42;
                    INSERT INTO authoring_run_input_provenance
                        (RunId, Source, LatestSuccessfulRefreshAt, ContentRevision, CapturedAt)
                    VALUES
                        (@runId, 'Jira', '2026-09-20T12:00:00+00:00', 42, @createdAt),
                        ('historical-run', 'Jira', '2026-09-20T12:00:00+00:00', 42, @createdAt);
                    INSERT INTO prepared_ticket_in_person_requesters (TicketKey, DisplayName)
                    VALUES ('FHIR-1001', 'Grace Hopper'), ('FHIR-1001', 'Katherine Johnson');
                    """;
                await seed.ExecuteNonQueryAsync();
            }
            if (schemaVersion == 3)
            {
                seed.CommandText =
                    """
                    UPDATE prepared_ticket_hydration SET PublicDisplayNamePolicyVersion = @policy;
                    UPDATE prepared_jira_hydration SET PublicDisplayNamePolicyVersion = @policy;
                    UPDATE prepared_ticket_in_person_requesters SET PublicDisplayNamePolicyVersion = @policy;
                    """;
                seed.Parameters.AddWithValue("@policy", PublicDisplayNamePolicy.CurrentVersion);
                await seed.ExecuteNonQueryAsync();
            }
            foreach (string table in catalog.CountedTables)
            {
                await using SqliteCommand count = connection.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
                counts[table] = Convert.ToInt64(await count.ExecuteScalarAsync());
            }
            seed.CommandText =
                """
                INSERT INTO authoring_snapshot_provenance
                    (SnapshotId, ProcessorKind, RunId, AuthoringEpoch, Sequence, SchemaVersion,
                     ItemCount, ReceiptCount, TableCountsJson, CreatedAt)
                VALUES (@snapshotId, 'jira-fhir', @runId, 7, 4, @schemaVersion, 1, 2, @counts, @createdAt);
                """;
            seed.Parameters.AddWithValue("@schemaVersion", schemaVersion);
            seed.Parameters.AddWithValue("@counts", JsonSerializer.Serialize(counts));
            await seed.ExecuteNonQueryAsync();
        }

        VerifiedAuthoringSnapshotPair pair = await CreateVerifiedPairAsync(
            coordinates.SnapshotPairDirectory, "Preparer", coordinates.RunId, schemaVersion,
            await File.ReadAllBytesAsync(databasePath), counts, itemCount: 1, createdAt: createdAt);
        TicketSitePublishResult result = await new TicketSitePublisher().PublishAsync(
            new TicketSitePublishRequest(
                pair, TicketSiteKind.Discussion, coordinates.SiteRoot, coordinates.Workflow.SiteTitle,
                Filters: TicketSiteFilters.None));
        return (pair, result.Manifest);
    }

    private static async Task<VerifiedAuthoringSnapshotPair>
        CreateVerifiedPairAsync(
            string directory,
            string serviceName,
            string runId,
            int schemaVersion = 1,
            byte[]? databaseBytes = null,
            IReadOnlyDictionary<string, long>? tableCounts = null,
            int itemCount = 2,
            DateTimeOffset? createdAt = null)
    {
        Directory.CreateDirectory(directory);
        string databaseFileName = "snapshot.db";
        string descriptorFileName =
            $"{databaseFileName}.descriptor.json";
        databaseBytes ??= "verified snapshot bytes"u8.ToArray();
        string databaseSha = Hash(databaseBytes);
        AuthoringSnapshotDescriptor descriptor = new(
            "jira-fhir",
            runId,
            $"snapshot-{runId}",
            7,
            4,
            schemaVersion,
            databaseSha,
            databaseBytes.LongLength,
            itemCount,
            2,
            tableCounts ?? new Dictionary<string, long>(),
            databaseFileName,
            createdAt ?? DateTimeOffset.UtcNow);
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
        string? outputPath = null,
        string? stableTitle = null,
        string? displayTitle = null,
        DateTimeOffset? jiraSourceLastSuccessfulRefreshAt = null,
        int? rendererSchemaVersion = null)
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
            stableTitle ?? coordinates.Workflow.SiteTitle,
            "assets-v1",
            "build-v1",
            outputPath ?? coordinates.SiteDirectory,
            DateTimeOffset.UtcNow,
            displayTitle,
            jiraSourceLastSuccessfulRefreshAt,
            rendererSchemaVersion);
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
