using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using FhirAugury.Publishing.Tickets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using static FhirAugury.Processor.Jira.Fhir.Preparer.Tests.PreparedTicketPublicationTestFixture;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

[Collection(PreparedTicketPublicationTestCollection.Name)]
public sealed class PreparedTicketPublicationEndToEndTests
{
    private const string BaseTitle = "Tickets for Discussion";
    private const string RetainedThreadUrl = "https://chat.fhir.org/#narrow/stream/7/topic/topic";
    private static readonly string[] FhirKeys =
        ["FHIR-10028", "FHIR-29212", "FHIR-803", "FHIR-804", "FHIR-805", "FHIR-806"];
    private static readonly string[] CdaKeys = ["CDA-901", "CDA-902"];
    private static readonly DateTimeOffset SelectedMaximum =
        new(2026, 9, 15, 1, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnrichmentPublishesNewSiteWithoutChangingOriginalGraphOrArtifacts()
    {
        using Fixture fixture = new(richGraph: true);
        Corpus corpus = await CreateCorpusAsync(fixture);
        PublicationHttpHandler handler = new(fixture, corpus.Updates);
        using HttpClient http = handler.CreateClient();
        AuthoringControlClient client = new(http);
        TicketSiteFilters filters = new(Project: "FHIR");
        Publication original = await PublishAsync(fixture, client, corpus.Source.Descriptor, filters);
        KeyValuePair<string, string>[] originalHashes = await ArtifactHashesAsync(fixture, original);
        PreparedTicketPublicationBaseline baseline =
            await fixture.CreateBaselineReader().ReadAsync(corpus.Source.Run.Id);
        PreparedTicketPublicationProtectedInventory accepted = await fixture.ReadCurrentAsync();
        Assert.Equal(8, accepted.Corpus.Count);
        Assert.Equal(3, accepted.Corpus.Select(item => item.ContributingRunId).Distinct().Count());
        Dictionary<string, string[]> protectedBefore = ReadLiveProtectedRows(fixture);
        string privateBefore = ReadPrivateState(fixture);
        using (SqliteConnection snapshot = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(original.Pair.DatabasePath))
        {
            AssertRowsRetained(ReadProtectedRows(snapshot), protectedBefore);
            AssertAuthoredValues(snapshot, [.. FhirKeys, .. CdaKeys]);
            AssertOrderedFhirGrouping(snapshot);
            AssertRow(snapshot,
                "SELECT Reporter, Assignee, UpdatedAt FROM prepared_jira_hydration WHERE TicketKey = 'FHIR-10028' AND JiraKey = TicketKey",
                ["Legacy Reporter", "Legacy Assignee", "2002-02-02T00:00:00.0000000+00:00"]);
        }

        OrchestratorHydrationFetcher fetcher = new(http, NullLogger.Instance);
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationRefreshResult admitted = await service.StartAsync(corpus.Source.Run.Id);
        AssertMaintenanceItems(admitted.Items, accepted);
        PreparedTicketPublicationEnrichmentInput input = Assert.IsType<PreparedTicketPublicationEnrichmentInput>(
            PreparerDatabase.ReadPublicationEnrichmentInput(
                Assert.IsType<AuthoringRunRecord>(
                    await fixture.Store.GetRunAsync(admitted.Run.RunId))));
        Assert.Equal("publication-enrichment", PreparedTicketPublicationEnrichmentContract.RecipeName);
        Assert.Equal(1, input.RecipeVersion);
        CountingGroupingDispatcher grouping = new(fixture.Database);

        AuthoringSnapshotDescriptor refreshed = Assert.IsType<AuthoringSnapshotDescriptor>(
            await fixture.CreatePostProcessor(service, grouping).FinalizeRunAsync(admitted.Run.RunId));
        Publication publication = await PublishAsync(fixture, client, refreshed, filters);

        AuthoringRunResponse status = await client.GetAsync("Preparer", admitted.Run.RunId, CancellationToken.None);
        Assert.Equal("completed", status.Run.Status);
        Assert.True(status.Run.State!.IsTerminal);
        Assert.Equal("publication-refresh", status.Run.Purpose);
        Assert.Equal(corpus.Source.Run.Id, status.Run.SourceRunId);
        Assert.Equal(new AuthoringRunCorpusComparison(corpus.Source.Descriptor.SnapshotId, 8, 8, 0),
            status.Run.CorpusComparison);
        AssertMaintenanceItems(status.Items, accepted);
        Assert.Equal(0, grouping.CallCount);
        Assert.Equal(privateBefore, ReadPrivateState(fixture));
        AssertProtectedStatesEqual(protectedBefore, ReadLiveProtectedRows(fixture));
        PreparedTicketPublicationProtectionReader.Compare(baseline, await fixture.ReadCurrentAsync());
        PreparedTicketPublicationProtectionReader.ValidateFrozen(input, await fixture.ReadCurrentAsync(), refreshed.RunId);
        Assert.Contains(await fixture.Store.GetRunStagesAsync(refreshed.RunId),
            stage => stage.StageName == "publication-enrichment-v1");
        Assert.DoesNotContain(await fixture.Store.GetRunStagesAsync(refreshed.RunId),
            stage => stage.StageName is "grouping" or "publication-metadata");
        Assert.Equal(corpus.Updates.Keys.Order(StringComparer.Ordinal),
            handler.JiraRequests.Order(StringComparer.Ordinal));
        Assert.Equal(["12345", "stream::topic"], handler.ZulipRequests.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("unaccepted-reference", handler.ZulipRequests);

        AssertNewPublication(original, publication);
        Assert.Equal(3, refreshed.SchemaVersion);
        Assert.Equal(1, refreshed.PublicationProof!.ContractVersion);
        Assert.Equal(corpus.Source.Run.Id, refreshed.PublicationProof.SourceRunId);
        Assert.Equal(901, refreshed.PublicationProof.SourceContentRevision);
        await AssertSnapshotsPreserveGraphAsync(fixture, original, publication, [.. FhirKeys, .. CdaKeys]);
        await AssertEnrichedMetadataAsync(fixture, publication, corpus.Updates);
        await AssertDiscussionProjectionAsync(publication);
        Assert.Equal(originalHashes, await ArtifactHashesAsync(fixture, original));
    }

    [Theory]
    [InlineData("protected-output")]
    [InlineData("jira-revision")]
    public async Task PreservationOrRevisionConflictLeavesOriginalPublicationUntouched(string conflict)
    {
        using Fixture fixture = new(richGraph: true);
        Corpus corpus = await CreateCorpusAsync(fixture);
        PublicationHttpHandler handler = new(fixture, corpus.Updates);
        using HttpClient http = handler.CreateClient();
        AuthoringControlClient client = new(http);
        Publication original = await PublishAsync(fixture, client, corpus.Source.Descriptor);
        KeyValuePair<string, string>[] originalHashes = await ArtifactHashesAsync(fixture, original);
        if (conflict == "protected-output")
        {
            fixture.Execute(
                """
                UPDATE authoring_result_receipts SET Id = 'replacement-receipt' WHERE Id = @receipt;
                UPDATE authoring_run_items SET AcceptedReceiptId = 'replacement-receipt' WHERE AcceptedReceiptId = @receipt;
                """,
                ("@receipt", corpus.Source.ReceiptIds["FHIR-803"]));
        }
        else
        {
            ItemResponse originalItem = handler.JiraItems["FHIR-803"];
            handler.JiraItems["FHIR-803"] = originalItem with { UpdatedAt = originalItem.UpdatedAt!.Value.AddSeconds(1) };
        }
        Dictionary<string, string[]> protectedBefore = ReadLiveProtectedRows(fixture);
        string privateBefore = ReadPrivateState(fixture);
        string metadataBefore = fixture.ReadPublicationMetadataState();
        int runsBefore = fixture.CountLive("authoring_runs");
        string[] snapshotsBefore = (await fixture.Store.GetSnapshotRecordsAsync()).Select(row => row.Id).Order().ToArray();
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(new(http, NullLogger.Instance));
        CountingGroupingDispatcher grouping = new(fixture.Database);

        if (conflict == "protected-output")
        {
            PreparedTicketPublicationProtectionException error =
                await Assert.ThrowsAsync<PreparedTicketPublicationProtectionException>(
                    () => service.StartAsync(corpus.Source.Run.Id));
            Assert.Equal("original-output-changed", error.FailureCode);
            Assert.Equal(runsBefore, fixture.CountLive("authoring_runs"));
            Assert.Empty(handler.JiraRequests);
        }
        else
        {
            PreparedTicketPublicationRefreshResult admitted = await service.StartAsync(corpus.Source.Run.Id);
            Assert.Null(await fixture.CreatePostProcessor(service, grouping).FinalizeRunAsync(admitted.Run.RunId));
            AuthoringRunResponse status = await client.GetAsync("Preparer", admitted.Run.RunId, CancellationToken.None);
            Assert.Equal("superseded", status.Run.Status);
            Assert.True(status.Run.State!.IsTerminal);
            Assert.Contains("source-revision-mismatch", status.Run.Error, StringComparison.Ordinal);
            Assert.Contains("Retain the existing publication and inspect the conflict", status.Run.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("Ordinary re-authoring is required", status.Run.Error, StringComparison.Ordinal);
            Assert.All(status.Items, item =>
            {
                Assert.Equal("complete", item.Status);
                Assert.NotNull(item.AcceptedReceiptId);
                Assert.Equal(0, item.AttemptCount);
            });
            Assert.Equal(runsBefore + 1, fixture.CountLive("authoring_runs"));
            Assert.Contains("FHIR-803", handler.JiraRequests);
        }

        Assert.Empty(handler.ZulipRequests);
        Assert.Equal(0, grouping.CallCount);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(0, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
        Assert.Equal(snapshotsBefore,
            (await fixture.Store.GetSnapshotRecordsAsync()).Select(row => row.Id).Order().ToArray());
        Assert.Equal(privateBefore, ReadPrivateState(fixture));
        Assert.Equal(metadataBefore, fixture.ReadPublicationMetadataState());
        AssertProtectedStatesEqual(protectedBefore, ReadLiveProtectedRows(fixture));
        _ = await new AuthoringSnapshotPairVerifier().VerifyReadyPairAsync(
            "Preparer", original.Pair.RunId, original.Pair.DirectoryPath);
        using SqliteConnection snapshot = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(original.Pair.DatabasePath);
        AssertAuthoredValues(snapshot, [.. FhirKeys, .. CdaKeys]);
        AssertOrderedFhirGrouping(snapshot);
        using SqliteConnection renderer = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(original.RendererPath);
        Assert.Equal(8, Scalar<long>(renderer, "SELECT COUNT(*) FROM tickets"));
        Assert.Equal(originalHashes, await ArtifactHashesAsync(fixture, original));
    }

    [Fact]
    public async Task AdditionalCurrentOutputIsDisclosedWithoutReplacingOriginalOutput()
    {
        using Fixture fixture = new(richGraph: true);
        Corpus corpus = await CreateCorpusAsync(fixture);
        PublicationHttpHandler handler = new(fixture, corpus.Updates);
        using HttpClient http = handler.CreateClient();
        AuthoringControlClient client = new(http);
        Publication original = await PublishAsync(fixture, client, corpus.Source.Descriptor);
        KeyValuePair<string, string>[] originalHashes = await ArtifactHashesAsync(fixture, original);
        Dictionary<string, string[]> originalGraph = ReadLiveProtectedRows(fixture);
        string[] additionalKeys = ["FHIR-901", "FHIR-902"];
        DateTimeOffset additionalUpdatedAt = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
        await fixture.CreateAdditionalCurrentOutputAsync(
            "US Core", additionalKeys, sourceUpdatedAt: additionalUpdatedAt);
        handler.AddJiraItems(additionalKeys.ToDictionary(key => key, _ => additionalUpdatedAt));
        Dictionary<string, string[]> protectedBefore = ReadLiveProtectedRows(fixture);
        AssertRowsRetained(originalGraph, protectedBefore);
        string privateBefore = ReadPrivateState(fixture);
        PreparedTicketPublicationProtectedInventory accepted = await fixture.ReadCurrentAsync();
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(new(http, NullLogger.Instance));
        CountingGroupingDispatcher grouping = new(fixture.Database);

        PreparedTicketPublicationRefreshResult admitted = await service.StartAsync(corpus.Source.Run.Id);
        AuthoringRunResponse admittedStatus = await client.GetAsync("Preparer", admitted.Run.RunId, CancellationToken.None);
        AuthoringRunCorpusComparison comparison = new(corpus.Source.Descriptor.SnapshotId, 8, 10, 2);
        Assert.Equal(comparison, admittedStatus.Run.CorpusComparison);
        AssertMaintenanceItems(admittedStatus.Items, accepted);
        Assert.Contains(admittedStatus.Items, item => item.BusinessKey == "FHIR-901");
        Assert.Contains(admittedStatus.Items, item => item.BusinessKey == "FHIR-902");
        string publicStatusJson = JsonSerializer.Serialize(admittedStatus, JsonSerializerOptions.Web);
        Assert.Contains("\"additionalTicketCount\":2", publicStatusJson, StringComparison.Ordinal);
        Assert.DoesNotContain("protectedRows", publicStatusJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recipeInput", publicStatusJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Repo reason", publicStatusJson, StringComparison.Ordinal);

        AuthoringSnapshotDescriptor refreshed = Assert.IsType<AuthoringSnapshotDescriptor>(
            await fixture.CreatePostProcessor(service, grouping).FinalizeRunAsync(admitted.Run.RunId));
        Publication publication = await PublishAsync(fixture, client, refreshed);
        AuthoringRunResponse completedStatus = await client.GetAsync("Preparer", admitted.Run.RunId, CancellationToken.None);

        Assert.Equal("completed", completedStatus.Run.Status);
        Assert.Equal(comparison, completedStatus.Run.CorpusComparison);
        AssertMaintenanceItems(completedStatus.Items, accepted);
        Assert.Equal(0, grouping.CallCount);
        Assert.Equal(privateBefore, ReadPrivateState(fixture));
        AssertProtectedStatesEqual(protectedBefore, ReadLiveProtectedRows(fixture));
        AssertNewPublication(original, publication);
        await AssertSnapshotsPreserveGraphAsync(fixture, original, publication, [.. FhirKeys, .. CdaKeys]);
        Assert.Equal(10, publication.Site.Manifest.DiscussionCorpus!.TicketCount);
        Assert.Equal(2, publication.Site.Manifest.DiscussionCorpus.ExportedProjectCount);
        Assert.Equal(8, original.Site.Manifest.DiscussionCorpus!.TicketCount);
        using SqliteConnection snapshot = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(publication.Pair.DatabasePath);
        AssertAuthoredValues(snapshot, additionalKeys);
        using SqliteConnection renderer = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(publication.RendererPath);
        Assert.Equal(
            new[] { "FHIR-901", "FHIR-902" }.Select(key => JsonSerializer.Serialize(new object?[] { key, $"Request {key}\n\nExact authored text: caf\u00e9." })),
            ReadRows(renderer, "SELECT Key, RequestSummary FROM tickets WHERE Specification = 'US Core' ORDER BY Key"));
        Assert.Equal(10, handler.JiraRequests.Count);
        Assert.Equal(["12345", "stream::topic"], handler.ZulipRequests.Order(StringComparer.Ordinal));
        Assert.Contains($"/api/v1/processing-services/Preparer/authoring/runs/{admitted.Run.RunId}", handler.Requests);
        Assert.Equal(originalHashes, await ArtifactHashesAsync(fixture, original));
    }

    private sealed record Corpus(SourceResult Source, IReadOnlyDictionary<string, DateTimeOffset> Updates);
    private sealed record Publication(
        VerifiedAuthoringSnapshotPair DownloadedPair,
        VerifiedAuthoringSnapshotPair Pair,
        TicketSitePublishResult Site,
        string RendererPath);

    private static async Task<Corpus> CreateCorpusAsync(Fixture fixture)
    {
        fixture.BeforeSourceSnapshot = _ => fixture.Execute(
            """
            UPDATE prepared_jira_hydration SET UpdatedAt = '2030-01-01T00:00:00.0000000+00:00'
            WHERE TicketKey <> JiraKey;
            UPDATE prepared_zulip_hydration SET MessageCount = 0
            WHERE TicketKey = 'FHIR-29212' AND ZulipThreadId = 'stream::topic';
            """);
        DateTimeOffset earlierUpdate = new(2026, 9, 3, 18, 30, 0, TimeSpan.FromHours(5.5));
        DateTimeOffset selectedUpdate = new(2026, 9, 14, 23, 30, 0, TimeSpan.FromHours(-2));
        DateTimeOffset excludedUpdate = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        _ = await fixture.CreateSourceRunAtAsync(earlierUpdate, FhirKeys[..2]);
        await fixture.CreateAdditionalCurrentOutputAsync("CDA", CdaKeys, sourceUpdatedAt: excludedUpdate);
        SourceResult source = await fixture.CreateSourceRunAtAsync(selectedUpdate, FhirKeys[2..]);
        Dictionary<string, DateTimeOffset> updates = FhirKeys[..2].ToDictionary(key => key, _ => earlierUpdate);
        foreach (string key in FhirKeys[2..]) updates.Add(key, selectedUpdate);
        foreach (string key in CdaKeys) updates.Add(key, excludedUpdate);
        return new(source, updates);
    }

    private static async Task<Publication> PublishAsync(
        Fixture fixture, AuthoringControlClient client, AuthoringSnapshotDescriptor descriptor, TicketSiteFilters? filters = null)
    {
        VerifiedAuthoringSnapshotPair downloaded = await client.DownloadSnapshotPairAsync(
            "Preparer", descriptor.RunId, Path.Combine(fixture.DirectoryPath, "downloads", descriptor.RunId), CancellationToken.None);
        string pairDirectory = Path.Combine(fixture.DirectoryPath, "materialized", descriptor.RunId);
        AuthoringSnapshotPairMaterialization materialized = await AuthoringSnapshotPairMaterializer.MaterializeAsync(
            downloaded, Path.Combine(pairDirectory, downloaded.Manifest.DatabaseFileName),
            Path.Combine(pairDirectory, downloaded.Manifest.DescriptorFileName));
        Assert.Equal(descriptor.SnapshotId, materialized.Descriptor.SnapshotId);
        File.Copy(Assert.IsType<string>(downloaded.ReadyManifestPath),
            Path.Combine(pairDirectory, AuthoringSnapshotPairManifest.ReadyFileName));
        VerifiedAuthoringSnapshotPair pair = await new AuthoringSnapshotPairVerifier().VerifyReadyPairAsync(
            "Preparer", descriptor.RunId, pairDirectory);
        Assert.Equal(downloaded.Manifest, pair.Manifest);
        TicketSitePublishResult site = await new TicketSitePublisher().PublishAsync(new(
            pair, TicketSiteKind.Discussion, Path.Combine(fixture.DirectoryPath, "sites", descriptor.RunId), BaseTitle, filters));
        Assert.Equal(TicketSitePublishOutcome.Published, site.Outcome);
        string html = await File.ReadAllTextAsync(Path.Combine(site.SiteOutputPath, "index.html"));
        const string marker = "window.__DB__='";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The publisher did not embed its renderer database.");
        start += marker.Length;
        int end = html.IndexOf('\'', start);
        Assert.True(end > start);
        await using MemoryStream compressed = new(Convert.FromBase64String(html[start..end]));
        await using GZipStream gzip = new(compressed, CompressionMode.Decompress);
        using MemoryStream database = new();
        await gzip.CopyToAsync(database);
        byte[] bytes = database.ToArray();
        Assert.Equal(site.Manifest.EmbeddedDbSha256, Hash(bytes));
        Assert.Equal(site.Manifest.EmbeddedDbSizeBytes, bytes.LongLength);
        string rendererDirectory = Path.Combine(fixture.DirectoryPath, "renderers");
        Directory.CreateDirectory(rendererDirectory);
        string rendererPath = Path.Combine(rendererDirectory, $"{descriptor.RunId}.db");
        await File.WriteAllBytesAsync(rendererPath, bytes);
        Assert.Contains($"assets/state.js?v={site.Manifest.RendererAssetsVersion}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"module\"", html, StringComparison.Ordinal);
        Assert.Equal(site.Manifest.BuildIdentity, site.Manifest.ComputeBuildIdentity());
        return new(downloaded, pair, site, rendererPath);
    }

    private static void AssertMaintenanceItems(
        IReadOnlyList<AuthoringRunItemStatus> items, PreparedTicketPublicationProtectedInventory accepted)
    {
        Assert.Equal(accepted.Corpus.Count, items.Count);
        foreach (PreparedTicketPublicationCorpusItem ticket in accepted.Corpus)
        {
            AuthoringRunItemStatus item = Assert.Single(items, item => item.BusinessKey == ticket.TicketKey);
            Assert.Equal(ticket.ReceiptId, item.AcceptedReceiptId);
            Assert.Equal(ticket.ExpectedSourceRevision, item.ExpectedSourceRevision);
            Assert.Equal("complete", item.Status);
            Assert.Equal(0, item.AttemptCount);
        }
    }

    private static void AssertNewPublication(Publication original, Publication publication)
    {
        Assert.NotEqual(original.Pair.RunId, publication.Pair.RunId);
        Assert.NotEqual(original.Pair.SnapshotId, publication.Pair.SnapshotId);
        Assert.NotEqual(original.Pair.Manifest.DatabaseSha256, publication.Pair.Manifest.DatabaseSha256);
        Assert.NotEqual(original.Pair.DirectoryPath, publication.Pair.DirectoryPath);
        Assert.NotEqual(original.Site.SiteOutputPath, publication.Site.SiteOutputPath);
        Assert.NotEqual(original.Site.Manifest.BuildIdentity, publication.Site.Manifest.BuildIdentity);
        Assert.True(publication.Pair.Descriptor.Sequence > original.Pair.Descriptor.Sequence);
        Assert.Equal(publication.Pair.RunId, publication.Site.Manifest.RunId);
        Assert.Equal(publication.Pair.SnapshotId, publication.Site.Manifest.SnapshotId);
        Assert.Equal(publication.Pair.Descriptor.Sequence, publication.Site.Manifest.SnapshotSequence);
    }

    private static async Task AssertSnapshotsPreserveGraphAsync(
        Fixture fixture, Publication original, Publication publication, string[] originalKeys)
    {
        using SqliteConnection before = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(original.Pair.DatabasePath);
        using SqliteConnection after = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(publication.Pair.DatabasePath);
        using SqliteConnection live = fixture.Database.OpenConnection();
        AssertRowsRetained(ReadProtectedRows(before), ReadProtectedRows(after));
        AssertRowsRetained(ReadProtectedRows(after), ReadProtectedRows(live));
        foreach (SqliteConnection connection in new[] { before, after, live })
        {
            AssertAuthoredValues(connection, originalKeys);
            AssertOrderedFhirGrouping(connection);
        }
    }

    private static async Task AssertEnrichedMetadataAsync(
        Fixture fixture, Publication publication, IReadOnlyDictionary<string, DateTimeOffset> updates)
    {
        using SqliteConnection snapshot = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(publication.Pair.DatabasePath);
        using SqliteConnection live = fixture.Database.OpenConnection();
        foreach (SqliteConnection connection in new[] { live, snapshot })
        {
            foreach ((string key, DateTimeOffset updatedAt) in updates)
            {
                AssertRow(connection,
                    "SELECT UpdatedAt, PublicDisplayNamePolicyVersion FROM prepared_jira_hydration WHERE TicketKey = @key AND JiraKey = TicketKey",
                    [updatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), PublicDisplayNamePolicy.CurrentVersion],
                    ("@key", key));
                AssertRow(connection,
                    "SELECT Reporter, Assignee, SourceContentRevision FROM prepared_ticket_hydration WHERE TicketKey = @key",
                    [key == "FHIR-806" ? null : $"Reporter {key}",
                        key is "FHIR-29212" or "FHIR-806" ? null : $"Assignee {key}", 901],
                    ("@key", key));
                AssertRow(connection,
                    "SELECT Url, HydrationStatus, FirstMessageAt, LastMessageAt FROM prepared_zulip_hydration WHERE TicketKey = @key AND ZulipThreadId = '12345'",
                    [PublicationHttpHandler.MessageUrl, "resolved", null, "2026-09-10T12:00:00.0000000+00:00"],
                    ("@key", key));
                ZulipReferenceHydrationOutcome numeric = Assert.IsType<ZulipReferenceHydrationOutcome>(
                    ZulipReferenceHydrationReason.Read(Scalar<string>(connection,
                        "SELECT HydrationReason FROM prepared_zulip_hydration WHERE TicketKey = @key AND ZulipThreadId = '12345'", ("@key", key))).Metadata);
                Assert.Equal(ZulipReferenceLookupOutcome.Resolved, numeric.LatestOutcome);
                Assert.Equal(ZulipReferenceBacking.TypedResolver, numeric.Backing);
                Assert.Equal([ZulipReferenceDiagnosticCode.InvalidTimestamp], numeric.Diagnostics);
                AssertRow(connection,
                    "SELECT Url, MessageCount, FirstMessageExcerpt, HydratedAt, HydrationStatus FROM prepared_zulip_hydration WHERE TicketKey = @key AND ZulipThreadId = 'stream::topic'",
                    [RetainedThreadUrl, key == "FHIR-29212" ? 0 : 3, "An indexed excerpt", "2002-02-02T00:00:00.0000000+00:00", "unresolved"],
                    ("@key", key));
                ZulipReferenceHydrationOutcome failed = Assert.IsType<ZulipReferenceHydrationOutcome>(
                    ZulipReferenceHydrationReason.Read(Scalar<string>(connection,
                        "SELECT HydrationReason FROM prepared_zulip_hydration WHERE TicketKey = @key AND ZulipThreadId = 'stream::topic'", ("@key", key))).Metadata);
                Assert.Equal(ZulipReferenceLookupOutcome.NotFound, failed.LatestOutcome);
                Assert.Equal(key == "FHIR-29212" ? ZulipReferenceBacking.Unverified : ZulipReferenceBacking.LegacyIndexedContext, failed.Backing);
            }
        }
    }

    private static async Task AssertDiscussionProjectionAsync(Publication publication)
    {
        TicketSiteManifest manifest = publication.Site.Manifest;
        Assert.Equal(BaseTitle, manifest.Title);
        Assert.Equal("Tickets for Discussion - Sept 15, 2026 (filtered: project=FHIR)", manifest.DisplayTitle);
        Assert.Equal(PublicationHttpHandler.SourceRefreshedAt, manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Equal(3, manifest.RendererSchemaVersion);
        Assert.True(manifest.DiscussionReadiness!.IsReady);
        Assert.Equal("publication-refresh", manifest.DiscussionReadiness.Evidence);
        DiscussionCorpusSummary summary = Assert.IsType<DiscussionCorpusSummary>(manifest.DiscussionCorpus);
        Assert.Equal(6, summary.TicketCount);
        Assert.Equal(1, summary.ExportedProjectCount);
        Assert.Equal(6, summary.ValidJiraUpdatedAtCount);
        Assert.Equal(SelectedMaximum, summary.MaxJiraUpdatedAt);
        Assert.Equal(DiscussionDateCoverage.Complete, summary.DateCoverage);
        Assert.Equal(5, summary.TicketsWithPublicReporter);
        Assert.Equal(4, summary.TicketsWithPublicAssignee);
        Assert.Equal(4, summary.TicketsWithPublicRequester);
        Assert.Equal(
            new DiscussionLinkCoverage[]
            {
                new("github", 6, 0, 0, 6), new("jira", 6, 6, 0, 0), new("jira-xref", 6, 6, 0, 0),
                new("repo", 6, 6, 0, 0), new("zulip", 12, 6, 6, 0),
            }, summary.LinksByKind);
        Assert.Contains(publication.Site.Warnings, warning => warning.Contains("reporter=5/6", StringComparison.Ordinal));
        Assert.Contains(publication.Site.Warnings, warning => warning.Contains("6 unresolved with retained safe URLs", StringComparison.Ordinal));
        Assert.DoesNotContain(publication.Site.Warnings, warning => warning.Contains("readiness is degraded", StringComparison.Ordinal));

        using SqliteConnection renderer = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(publication.RendererPath);
        AssertRow(renderer, "SELECT RendererSchemaVersion, BaseTitle, SiteName, CorpusSummaryJson FROM site_metadata",
            [3, BaseTitle, manifest.DisplayTitle, JsonSerializer.Serialize(summary, JsonSerializerOptions.Web)]);
        Assert.Equal(FhirKeys.Select(key => JsonSerializer.Serialize(new object?[] { key })),
            ReadRows(renderer, "SELECT Key FROM tickets ORDER BY Key"));
        AssertRow(renderer, "SELECT JiraUpdatedAt FROM tickets WHERE Key = 'FHIR-803'",
            ["2026-09-15T01:30:00.0000000+00:00"]);
        AssertRow(renderer, "SELECT JiraUpdatedAt FROM tickets WHERE Key = 'FHIR-10028'",
            ["2026-09-03T13:00:00.0000000+00:00"]);
        AssertRow(renderer, "SELECT DisplayName, Availability FROM ticket_people WHERE TicketKey = 'FHIR-10028' AND Role = 'reporter'",
            ["Reporter FHIR-10028", "available"]);
        AssertRow(renderer, "SELECT DisplayName, Availability FROM ticket_people WHERE TicketKey = 'FHIR-29212' AND Role = 'assignee'",
            [null, "available"]);
        AssertRow(renderer, "SELECT DisplayName FROM ticket_people WHERE TicketKey = 'FHIR-10028' AND Role = 'in-person-requester'",
            ["Requester FHIR-10028"]);
        AssertRow(renderer, "SELECT Url, HydrationStatus, Justification FROM related_items WHERE TicketKey = 'FHIR-29212' AND Kind = 'repo'",
            ["https://github.com/HL7/fhir", "resolved", "Repo reason FHIR-29212"]);
        AssertRow(renderer, "SELECT Url, HydrationStatus, Justification FROM related_items WHERE TicketKey = 'FHIR-29212' AND Kind = 'github'",
            [null, "resolved", "GitHub reason FHIR-29212"]);
        AssertRow(renderer, "SELECT Url, HydrationStatus, HydrationReason FROM related_items WHERE TicketKey = 'FHIR-10028' AND Kind = 'zulip' AND ItemKey = '12345'",
            [PublicationHttpHandler.MessageUrl, "resolved", "Zulip diagnostic: invalid optional source timestamp."]);
        AssertRow(renderer, "SELECT Url, HydrationStatus, HydrationReason FROM related_items WHERE TicketKey = 'FHIR-10028' AND Kind = 'zulip' AND ItemKey = 'stream::topic'",
            [RetainedThreadUrl, "unresolved", "Zulip lookup failed: reference not found. Last-known source-backed link and context retained; the latest lookup is unresolved."]);
        AssertRow(renderer, "SELECT Url, HydrationStatus, HydrationReason FROM related_items WHERE TicketKey = 'FHIR-29212' AND Kind = 'zulip' AND ItemKey = 'stream::topic'",
            [RetainedThreadUrl, "unresolved", "Zulip lookup failed: reference not found. Unverified link and context retained; source backing is not established."]);
        Assert.Equal(0, Scalar<long>(renderer,
            """
            SELECT COUNT(*) FROM summary_sources s
            LEFT JOIN related_items r ON r.TicketKey = s.TicketKey AND r.ItemKey = s.SourceKey AND r.Kind = 'zulip'
            WHERE s.SummaryKind = 'related-zulip' AND (r.ItemKey IS NULL OR s.Url IS NOT r.Url)
            """));
        foreach (string key in FhirKeys)
        {
            AssertRow(renderer,
                "SELECT RequestSummary, ProposalA, ProposalB, ProposalC, RecommendationJustification FROM tickets WHERE Key = @key",
                [$"Request {key}\n\nExact authored text: caf\u00e9.", $"Proposal A {key}", $"Proposal B {key}", $"Proposal C {key}", $"Recommendation {key}"],
                ("@key", key));
        }
    }

    private static void AssertAuthoredValues(SqliteConnection connection, string[] keys)
    {
        foreach (string key in keys)
        {
            AssertRow(connection,
                """
                SELECT Key, RequestSummary, CommentSummary, LinkedTicketSummary, RelatedTicketSummary,
                       RelatedZulipSummary, RelatedGitHubSummary, ExistingProposed, ProposalA,
                       ProposalAJustification, ProposalAImpact, ProposalB, ProposalBJustification,
                       ProposalBImpact, ProposalC, ProposalCJustification, Recommendation, RecommendationJustification
                FROM prepared_tickets WHERE Key = @key
                """,
                [key, $"Request {key}\n\nExact authored text: caf\u00e9.", $"Comments {key}", $"Linked {key}",
                    $"Related {key}", $"Zulip {key}", $"GitHub {key}", $"Existing proposal {key}",
                    $"Proposal A {key}", $"Reason A {key}", PreparedTicketImpactValues.NonSubstantive,
                    $"Proposal B {key}", $"Reason B {key}", PreparedTicketImpactValues.NonSubstantive,
                    $"Proposal C {key}", $"Reason C {key}", PreparedTicketRecommendationValues.ProposalA,
                    $"Recommendation {key}"],
                ("@key", key));
            AssertRow(connection, "SELECT Repo, RepoCategory, Justification FROM prepared_ticket_repos WHERE TicketKey = @key",
                ["HL7/fhir", "specification", $"Repo reason {key}"], ("@key", key));
            AssertRow(connection, "SELECT AssociatedTicketKey, LinkType, Justification FROM prepared_ticket_related_jira WHERE TicketKey = @key",
                ["FHIR-99999", "linked", $"Jira reason {key}"], ("@key", key));
            Assert.Equal(
                new[]
                {
                    JsonSerializer.Serialize(new[] { "12345", $"Missing hydration reason {key}" }),
                    JsonSerializer.Serialize(new[] { "stream::topic", $"Zulip reason {key}" }),
                },
                ReadRows(connection, "SELECT ZulipThreadId, Justification FROM prepared_ticket_related_zulip WHERE TicketKey = @key ORDER BY ZulipThreadId", ("@key", key)));
            AssertRow(connection, "SELECT GitHubItemId, Justification FROM prepared_ticket_related_github WHERE TicketKey = @key",
                ["HL7/fhir#17", $"GitHub reason {key}"], ("@key", key));
        }
    }

    private static void AssertOrderedFhirGrouping(SqliteConnection connection)
    {
        object?[][] expected =
        [
            ["Second topic", "Second topic details", 1, null, null, null, "FHIR-805", 0],
            ["Second topic", "Second topic details", 1, null, null, null, "FHIR-806", 1],
            ["First topic", "First topic details\n\nKeep this text.", 2, "FHIR-10028", "Group reason 0", 0, "FHIR-10028", 0],
            ["First topic", "First topic details\n\nKeep this text.", 2, "FHIR-10028", "Group reason 0", 0, "FHIR-29212", 1],
            ["First topic", "First topic details\n\nKeep this text.", 2, "FHIR-803", "Group reason 2", 1, "FHIR-803", 0],
            ["First topic", "First topic details\n\nKeep this text.", 2, "FHIR-803", "Group reason 2", 1, "FHIR-804", 1],
        ];
        Assert.Equal(expected.Select(row => JsonSerializer.Serialize(row)),
            ReadRows(connection,
                """
                SELECT t.ShortDescription, t.LongerDescription, t.RenderOrderHint,
                       g.FirstTicketKey, g.Rationale, g.OrderInTopic, m.TicketKey, m.OrderInContainer
                FROM prepared_ticket_topics t
                JOIN prepared_ticket_topic_members m ON m.TopicRowId = t.RowId
                LEFT JOIN prepared_ticket_topic_groups g ON g.RowId = m.TopicGroupRowId AND g.TopicRowId = t.RowId
                WHERE t.Specification = 'FHIR'
                ORDER BY t.RenderOrderHint, g.OrderInTopic, m.OrderInContainer
                """));
    }

    // These raw values independently check the protection reader's fingerprint/classification contract.
    private static Dictionary<string, string[]> ReadProtectedRows(SqliteConnection connection)
    {
        string[] wholeTables =
        [
            "prepared_tickets", "prepared_ticket_repos", "prepared_ticket_related_jira",
            "prepared_ticket_related_zulip", "prepared_ticket_related_github", "prepared_github_hydration",
            "prepared_repo_hydration", "prepared_ticket_jira_xref", "prepared_ticket_jira_content",
            "prepared_ticket_artifacts", "prepared_ticket_pages", "prepared_ticket_topics",
            "prepared_ticket_topic_groups", "prepared_ticket_topic_members", "prepared_ticket_partition_receipts",
            "authoring_result_receipts", "jira_review_workgroups",
        ];
        Dictionary<string, string[]> rows = wholeTables.ToDictionary(table => table, table => ReadPublicTable(table));
        rows.Add("parent-protected", ReadRows(connection,
            """
            SELECT RowId, Id, TicketKey, Priority, Resolution, ResolutionDescriptionPlain, Specification,
                   RaisedInVersion, SelectedBallot, ChangeCategory, Impact, Labels, CommentCount,
                   DescriptionPlain, DescriptionHtml, ResolutionDescriptionHtml, CreatedAt,
                   RelatedArtifactsRaw, RelatedPagesRaw, HydrationStatus, HydrationReason
            FROM prepared_ticket_hydration ORDER BY RowId
            """));
        rows.Add("jira-protected", ReadRows(connection,
            """
            SELECT RowId, Id, TicketKey, JiraKey, Title, Status, Type, Priority, Resolution,
                   ResolutionDescriptionPlain, WorkGroup, WorkGroupClean, Specification, Url,
                   DescriptionHtml, ResolutionDescriptionHtml, CreatedAt, RelatedArtifactsRaw,
                   RelatedPagesRaw, HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_jira_hydration ORDER BY RowId
            """));
        rows.Add("related-jira", ReadPublicTable("prepared_jira_hydration", "JiraKey <> TicketKey"));
        rows.Add("unaccepted-zulip", ReadPublicTable("prepared_zulip_hydration", "ZulipThreadId = 'unaccepted-reference'"));
        rows.Add("contributing-runs", ReadRows(connection,
            """
            SELECT Id, ProcessorKind, AuthoringEpoch, DatabaseOnly, TotalItems, CreatedAt, StartedAt
            FROM authoring_runs WHERE Id IN (SELECT RunId FROM authoring_result_receipts) ORDER BY Id
            """));
        rows.Add("accepted-items", ReadRows(connection,
            """
            SELECT Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                   AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt, CompletedAt
            FROM authoring_run_items
            WHERE RunId IN (SELECT RunId FROM authoring_result_receipts) ORDER BY Id
            """));
        rows.Add("historical-provenance", ReadRows(connection,
            """
            SELECT * FROM authoring_run_input_provenance
            WHERE RunId IN (SELECT RunId FROM authoring_result_receipts) ORDER BY RowId
            """));
        return rows;

        string[] ReadPublicTable(string table, string? predicate = null)
        {
            string columns = string.Join(", ", PreparedTicketSnapshotSchemaV3.Catalog.Tables
                .Single(schema => schema.Name == table).Columns);
            string where = predicate is null ? string.Empty : $" WHERE {predicate}";
            return ReadRows(connection, $"SELECT {columns} FROM {table}{where} ORDER BY RowId");
        }
    }

    private static Dictionary<string, string[]> ReadLiveProtectedRows(Fixture fixture)
    {
        using SqliteConnection connection = fixture.Database.OpenConnection();
        return ReadProtectedRows(connection);
    }

    private static string ReadPrivateState(Fixture fixture)
    {
        using SqliteConnection connection = fixture.Database.OpenConnection();
        return fixture.DumpTables(
            "authoring_run_attempts", "prepared_ticket_authoring_state", "prepared_ticket_run_item_partitions", "authoring_result_receipts")
            + JsonSerializer.Serialize(ReadRows(connection,
                "SELECT * FROM authoring_runs WHERE Id IN (SELECT RunId FROM authoring_result_receipts) ORDER BY RowId"))
            + JsonSerializer.Serialize(ReadRows(connection,
                "SELECT * FROM authoring_run_items WHERE RunId IN (SELECT RunId FROM authoring_result_receipts) ORDER BY RowId"));
    }

    private static void AssertRowsRetained(
        IReadOnlyDictionary<string, string[]> expected, IReadOnlyDictionary<string, string[]> actual)
    {
        foreach ((string table, string[] rows) in expected)
        {
            Assert.All(rows, row => Assert.Contains(row, actual[table]));
        }
    }

    private static void AssertProtectedStatesEqual(
        IReadOnlyDictionary<string, string[]> expected, IReadOnlyDictionary<string, string[]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach ((string table, string[] rows) in expected) Assert.Equal(rows, actual[table]);
    }

    private static async Task<KeyValuePair<string, string>[]> ArtifactHashesAsync(Fixture fixture, Publication publication)
    {
        string[] directories = [publication.DownloadedPair.DirectoryPath, publication.Pair.DirectoryPath, publication.Site.OutputRoot];
        List<KeyValuePair<string, string>> hashes = [];
        foreach (string path in directories.SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Append(Path.Combine(fixture.SnapshotDirectory, publication.Pair.Descriptor.FileName))
            .Order(StringComparer.Ordinal))
        {
            hashes.Add(new(path, Hash(await File.ReadAllBytesAsync(path))));
        }
        return hashes.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AssertRow(SqliteConnection connection, string sql, object?[] expected, params (string Name, object? Value)[] parameters)
        => Assert.Equal(JsonSerializer.Serialize(expected), Assert.Single(ReadRows(connection, sql, parameters)));

    private static T Scalar<T>(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static string[] ReadRows(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray()));
        }
        return rows.ToArray();
    }
}
