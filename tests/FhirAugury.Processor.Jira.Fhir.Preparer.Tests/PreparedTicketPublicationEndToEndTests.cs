using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using FhirAugury.Publishing.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    private static readonly string[] RevisedKeys =
        ["FHIR-10028", "FHIR-803"];
    private static readonly string[] PreservedKeys =
        [
            "FHIR-29212",
            "FHIR-804",
            "FHIR-805",
            "FHIR-806",
            "CDA-901",
            "CDA-902",
        ];
    private static readonly DateTimeOffset SelectedMaximum =
        new(2026, 9, 15, 1, 30, 0, TimeSpan.Zero);
    private const string MovedTicketType = "Technical Correction";

    [Fact]
    public void ReconciliationPublicationContract_HasDistinctProofPurpose()
    {
        Assert.Equal(
            PreparedTicketPublicationReconciliationContract.Purpose,
            PreparedTicketPublicationContract
                .PublicationReconciliationPurpose);
        Assert.NotEqual(
            PreparedTicketPublicationContract.PublicationRefreshPurpose,
            PreparedTicketPublicationContract
                .PublicationReconciliationPurpose);
    }

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

    [Fact]
    public async Task ChangedTicketReconciliation_AutomaticFinalizationDispatchesGroupingClosure()
    {
        using Fixture fixture = new(richGraph: true);
        Corpus corpus = await CreateCorpusAsync(fixture);
        PublicationHttpHandler handler = new(fixture, corpus.Updates);
        using HttpClient http = handler.CreateClient();
        StagedReconciliation staged =
            await StageChangedTicketReconciliationAsync(
                fixture,
                corpus,
                handler,
                http,
                stageGroupingAndCandidate: false);
        PreparedTicketGroupingDeltaDispatcher grouping = new(
            fixture.Database);
        PreparedTicketPublicationGroupingDelta delta =
            await grouping.PrepareAsync(staged.RunId);
        InProcessReconciliationGroupingDispatcher worker = new(
            fixture.Database,
            fixture.Store,
            grouping)
        {
            FailOnceForType = MovedTicketType,
        };
        PreparedTicketReconciliationGroupingStageAdapter stageAdapter =
            new(worker);
        IOptions<PreparerServiceOptions> options =
            Options.Create(new PreparerServiceOptions
            {
                SnapshotDirectory = fixture.SnapshotDirectory,
                SnapshotSchemaVersion =
                    PreparedTicketSnapshotSchemaV3.Version,
            });
        SqliteReviewSnapshotReconciler snapshotReconciler = new(
            fixture.Store);
        PreparedTicketSnapshotMaterializer materializer = new(
            fixture.Database,
            fixture.Store,
            snapshotReconciler,
            options);
        PreparedTicketPublicationRecoveryService recovery = new(
            fixture.Database,
            fixture.Store,
            NullLogger<PreparedTicketPublicationRecoveryService>.Instance);
        JiraAuthoringRunCoordinator coordinator =
            CreateCoordinator(fixture);
        PreparedTicketRunWorkflowRegistry workflows = new(
            fixture.Store,
            fixture.Database,
            new OrchestratorHydrationFetcher(http, NullLogger.Instance),
            staged.Planner,
            grouping,
            materializer,
            recovery,
            options,
            NullLogger<PreparedTicketRunWorkflowRegistry>.Instance,
            stageAdapter);
        PreparedTicketRunPostProcessor postProcessor = new(
            fixture.Database,
            fixture.Store,
            new AuthoringRunFinalizer(fixture.Store),
            snapshotReconciler,
            coordinator,
            new OrchestratorWorkGroupCatalogFetcher(http),
            worker,
            options,
            snapshotMaterializer: materializer,
            workflowRegistry: workflows);

        Assert.Equal(
            $"Request {RevisedKeys[0]}\n\nExact authored text: caf\u00e9.",
            fixture.Scalar<string>(
                $"SELECT RequestSummary FROM prepared_tickets WHERE Key = '{RevisedKeys[0]}'"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => postProcessor.FinalizeRunAsync(staged.RunId));
        AuthoringRunStageRecord[] interruptedStages =
            (await fixture.Store.GetRunStagesAsync(staged.RunId)).ToArray();
        Assert.Single(
            interruptedStages,
            stage => stage.Status == AuthoringStatusValues.Stages.Complete);
        Assert.Single(
            interruptedStages,
            stage => stage.Status == AuthoringStatusValues.Stages.Error);
        Assert.Equal(
            0,
            fixture.Scalar<long>(
                """
                SELECT COUNT(*)
                FROM prepared_ticket_topics
                WHERE ShortDescription = 'Reconciled shared container'
                """));

        AuthoringSnapshotDescriptor descriptor =
            Assert.IsType<AuthoringSnapshotDescriptor>(
                await postProcessor.FinalizeRunAsync(staged.RunId));

        Assert.Equal(staged.RunId, descriptor.RunId);
        Assert.Equal(delta.Impacts.Count + 1, worker.WorkItems.Count);
        Assert.Equal(delta.Impacts.Count, worker.Receipts.Count);
        Assert.Equal(
            1,
            worker.WorkItems.Count(workItem =>
                string.Equals(
                    workItem.Type,
                    "Change Request",
                    StringComparison.Ordinal)));
        Assert.Equal(
            2,
            worker.WorkItems.Count(workItem =>
                string.Equals(
                    workItem.Type,
                    MovedTicketType,
                    StringComparison.Ordinal)));
        Assert.All(
            worker.WorkItems,
            workItem => Assert.Equal(
                delta.OverlayCorpusFingerprint,
                workItem.OverlayCorpusFingerprint));
        Assert.All(
            await fixture.Store.GetRunStagesAsync(staged.RunId),
            stage =>
            {
                Assert.Equal(
                    PreparerDatabase
                        .PublicationReconciliationGroupingStageName,
                    stage.StageName);
                Assert.Equal(
                    AuthoringStatusValues.Stages.Complete,
                    stage.Status);
                Assert.Equal(
                    delta.OverlayCorpusFingerprint,
                    stage.InputFingerprint);
            });
        Assert.All(worker.CanonicalGroupingChecks, Assert.False);
        Assert.Equal(
            "Reconciled shared container",
            fixture.Scalar<string>(
                """
                SELECT ShortDescription
                FROM prepared_ticket_topics
                WHERE Specification = 'FHIR'
                  AND Type = 'Change Request'
                """));
        AuthoringSnapshotPublicationProof proof =
            Assert.IsType<AuthoringSnapshotPublicationProof>(descriptor.PublicationProof);
        Assert.Equal(3, proof.ContractVersion);
        PreparedTicketPublicationFingerprints fingerprints =
            await PreparedTicketPublicationFingerprintReader.ReadReconciliationAsync(
                Path.Combine(fixture.SnapshotDirectory, descriptor.FileName));
        Assert.Equal(proof.CorpusFingerprint, fingerprints.Corpus);
        Assert.Equal(proof.GroupingFingerprint, fingerprints.Grouping);
    }

    [Fact]
    public async Task CarryForwardGenerationAdvanceDuringCandidateMaterialization_RejectsPromotion()
    {
        using Fixture fixture = new(richGraph: true);
        Corpus corpus = await CreateCorpusAsync(fixture);
        PublicationHttpHandler handler = new(fixture, corpus.Updates);
        using HttpClient http = handler.CreateClient();
        StagedReconciliation staged =
            await StageChangedTicketReconciliationAsync(
                fixture,
                corpus,
                handler,
                http,
                stageGroupingAndCandidate: false);
        PreparedTicketGroupingDeltaDispatcher grouping = new(
            fixture.Database);
        InProcessReconciliationGroupingDispatcher worker = new(
            fixture.Database,
            fixture.Store,
            grouping);
        IOptions<PreparerServiceOptions> options =
            Options.Create(new PreparerServiceOptions
            {
                SnapshotDirectory = fixture.SnapshotDirectory,
                SnapshotSchemaVersion =
                    PreparedTicketSnapshotSchemaV3.Version,
            });
        PreparedTicketSnapshotMaterializer materializer = new(
            fixture.Database,
            fixture.Store,
            new SqliteReviewSnapshotReconciler(fixture.Store),
            options);
        string temporaryPath = Path.Combine(
            fixture.SnapshotDirectory,
            $"jira-fhir-{staged.RunId}.reconciliation.tmp");
        CandidateGenerationAdvancingFetcher advancingFetcher = new(
            staged.Comparison.Items.ToDictionary(
                item => item.TicketKey,
                item => item.CurrentSourceRevision,
                StringComparer.OrdinalIgnoreCase),
            long.Parse(
                staged.Comparison.StableJiraGeneration,
                CultureInfo.InvariantCulture),
            temporaryPath);
        AuthoringRetryPolicy retryPolicy = new(
            Options.Create(new ProcessingServiceOptions()));
        JiraAuthoringRunCoordinator coordinator =
            CreateCoordinator(fixture);
        PreparedTicketPublicationReconciliationPlanner planner = new(
            fixture.CreateBaselineReader(),
            advancingFetcher,
            fixture.Database,
            new AuthoringRunControlService(fixture.Store, retryPolicy),
            coordinator,
            new AuthoringRunSchedulerWakeSignal());
        PreparedTicketPublicationRecoveryService recovery = new(
            fixture.Database,
            fixture.Store,
            NullLogger<PreparedTicketPublicationRecoveryService>.Instance);
        PreparedTicketRunWorkflowRegistry workflows = new(
            fixture.Store,
            fixture.Database,
            new OrchestratorHydrationFetcher(http, NullLogger.Instance),
            planner,
            grouping,
            materializer,
            recovery,
            options,
            NullLogger<PreparedTicketRunWorkflowRegistry>.Instance,
            new PreparedTicketReconciliationGroupingStageAdapter(worker));
        AuthoringRunRecord run = Assert.IsType<AuthoringRunRecord>(
            await fixture.Store.GetRunAsync(staged.RunId));
        string[] expectedInvalidated = staged.Comparison.Items
            .OrderBy(
                item => item.TicketKey,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TicketKey, StringComparer.Ordinal)
            .Select(item => item.TicketKey)
            .ToArray();

        PreparedTicketPublicationReconciliationException error =
            await Assert.ThrowsAsync<
                PreparedTicketPublicationReconciliationException>(
                () => workflows.FinalizeReconciliationAsync(run));

        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .RevisionInvalidation,
            error.FailureCode);
        Assert.Equal(expectedInvalidated, error.TicketKeys);
        Assert.True(
            advancingFetcher
                .CandidateWasPresentWhenGenerationAdvanced);
        Assert.Equal(
            expectedInvalidated.Concat(expectedInvalidated),
            advancingFetcher.RequestedTicketKeys);
        Assert.All(
            new[]
            {
                temporaryPath,
                temporaryPath + "-journal",
                temporaryPath + "-wal",
                temporaryPath + "-shm",
            },
            path => Assert.False(File.Exists(path)));
        string finalPath = Path.Combine(
            fixture.SnapshotDirectory,
            $"jira-fhir-{staged.RunId}.db");
        Assert.False(File.Exists(finalPath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(
                fixture.SnapshotDirectory,
                "*",
                SearchOption.TopDirectoryOnly),
            path => Path.GetFileName(path).Contains(
                staged.RunId,
                StringComparison.Ordinal));
        Assert.All(
            new[]
            {
                "prepared_ticket_publication_snapshot_descriptors",
                "prepared_ticket_publication_reconciliation_proofs",
                "prepared_ticket_publication_reconciliation_journal",
                "authoring_review_snapshots",
            },
            table => Assert.Equal(
                0,
                fixture.Scalar<long>(
                    $"SELECT COUNT(*) FROM {table} WHERE RunId = '{staged.RunId}'")));
        Assert.Equal(
            0,
            fixture.Scalar<long>(
                $"""
                SELECT COUNT(*)
                FROM prepared_ticket_authoring_state
                WHERE RunId = '{staged.RunId}'
                """));
        Assert.Equal(
            0,
            fixture.Scalar<long>(
                """
                SELECT COUNT(*)
                FROM prepared_ticket_topics
                WHERE ShortDescription = 'Reconciled shared container'
                """));
        foreach (string key in RevisedKeys)
        {
            Assert.Equal(
                $"Request {key}\n\nExact authored text: caf\u00e9.",
                fixture.Scalar<string>(
                    $"SELECT RequestSummary FROM prepared_tickets WHERE Key = '{key}'"));
        }
        AuthoringRunRecord retained = Assert.IsType<AuthoringRunRecord>(
            await fixture.Store.GetRunAsync(staged.RunId));
        Assert.Equal(AuthoringStatusValues.Runs.Running, retained.Status);
        Assert.Null(retained.SnapshotId);
        Assert.All(
            await fixture.Store.GetRunItemsAsync(staged.RunId),
            item => Assert.Equal(
                AuthoringStatusValues.Items.Complete,
                item.Status));
        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues
                .Staged,
            fixture.Scalar<string>(
                $"""
                SELECT PromotionState
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = '{staged.RunId}'
                """));
        Assert.NotNull(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(
            1,
            fixture.Scalar<long>(
                $"""
                SELECT COUNT(*)
                FROM prepared_ticket_publication_reconciliation_fences
                WHERE RunId = '{staged.RunId}'
                """));
        Assert.True(
            fixture.Scalar<long>(
                $"""
                SELECT COUNT(*)
                FROM prepared_ticket_publication_staged_graphs
                WHERE RunId = '{staged.RunId}'
                """) > 0);
        Assert.True(
            fixture.Scalar<long>(
                $"""
                SELECT COUNT(*)
                FROM prepared_ticket_publication_staged_grouping
                WHERE RunId = '{staged.RunId}'
                """) > 0);
    }

    [Theory]
    [InlineData("candidate-materialized")]
    [InlineData("database-promoted")]
    [InlineData("snapshot-file-published")]
    [InlineData("snapshot-record-promoted")]
    [InlineData("snapshot-record-ready")]
    [InlineData("run-completed")]
    public async Task ChangedTicketReconciliationRecoversAfterEveryDurableBoundary(
        string interruptionBoundary)
    {
        using Fixture fixture = new(richGraph: true);
        Corpus corpus = await CreateCorpusAsync(fixture);
        PublicationHttpHandler handler = new(fixture, corpus.Updates);
        using HttpClient http = handler.CreateClient();
        AuthoringControlClient client = new(http);
        Publication original =
            await PublishAsync(fixture, client, corpus.Source.Descriptor);
        KeyValuePair<string, string>[] originalHashes =
            await ArtifactHashesAsync(fixture, original);
        Dictionary<string, TicketPreservation> preserved =
            await CaptureTicketPreservationAsync(
                fixture,
                original,
                PreservedKeys);
        byte[] cdaGroupingBytes;
        using (SqliteConnection snapshot =
               await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(
                   original.Pair.DatabasePath))
        {
            AssertOrderedFhirGrouping(snapshot);
            cdaGroupingBytes = ReadGroupingBytes(snapshot, "CDA");
        }

        StagedReconciliation staged =
            await StageChangedTicketReconciliationAsync(
                fixture,
                corpus,
                handler,
                http);

        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues
                .Staged,
            fixture.Scalar<string>(
                $"""
                SELECT PromotionState
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = '{staged.RunId}'
                """));
        InvalidOperationException prematureAbandonment =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Database
                    .AbandonPublicationReconciliationAsync(
                        staged.RunId,
                        "candidate is still recoverable"));
        Assert.Contains(
            "only be abandoned after canonical promotion",
            prematureAbandonment.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            $"Request {RevisedKeys[0]}\n\nExact authored text: caf\u00e9.",
            fixture.Scalar<string>(
                $"SELECT RequestSummary FROM prepared_tickets WHERE Key = '{RevisedKeys[0]}'"));

        PreparedTicketPublicationRecoveryService recovery = new(
            fixture.Database,
            fixture.Store,
            NullLogger<PreparedTicketPublicationRecoveryService>.Instance);
        AuthoringSnapshotDescriptor replacementDescriptor;
        if (interruptionBoundary == "candidate-materialized")
        {
            replacementDescriptor =
                await ResumeCandidateFinalizationAsync(
                    fixture,
                    staged,
                    http,
                    recovery);
        }
        else
        {
            PreparerDatabase.PublicationReconciliationPromotion promotion =
                await fixture.Database
                    .PromotePublicationReconciliationAsync(
                        staged.RunId,
                        Path.Combine(
                            fixture.SnapshotDirectory,
                            $"jira-fhir-{staged.RunId}.db"));
            Assert.Equal(
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .SnapshotPublishPending,
                fixture.Scalar<string>(
                    $"""
                    SELECT PromotionState
                    FROM prepared_ticket_publication_reconciliations
                    WHERE RunId = '{staged.RunId}'
                    """));
            Assert.Equal(
                $"Reconciled request {RevisedKeys[0]}",
                fixture.Scalar<string>(
                    $"SELECT RequestSummary FROM prepared_tickets WHERE Key = '{RevisedKeys[0]}'"));
            Assert.Equal(
                MovedTicketType,
                fixture.Scalar<string>(
                    $"""
                    SELECT Type
                    FROM prepared_jira_hydration
                    WHERE TicketKey = '{RevisedKeys[1]}'
                      AND JiraKey = TicketKey
                    """));
            Assert.Equal(
                originalHashes,
                await ArtifactHashesAsync(fixture, original));

            await AdvancePendingPromotionToBoundaryAsync(
                fixture,
                promotion,
                interruptionBoundary);
            replacementDescriptor =
                await recovery.RecoverAsync(staged.RunId);
        }
        await recovery.RecoverPendingAsync();

        Assert.Equal(staged.RunId, replacementDescriptor.RunId);
        Assert.Equal(
            PreparedTicketPublicationContract.PublicationReconciliationPurpose,
            replacementDescriptor.PublicationProof?.Purpose);
        Assert.Equal(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            replacementDescriptor.PublicationProof?.ContractVersion);
        Assert.Equal(
            staged.CorpusFingerprint,
            replacementDescriptor.PublicationProof?.CorpusFingerprint);
        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues.Ready,
            fixture.Scalar<string>(
                $"""
                SELECT PromotionState
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = '{staged.RunId}'
                """));
        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues.Ready,
            fixture.Scalar<string>(
                $"""
                SELECT State
                FROM prepared_ticket_publication_reconciliation_journal
                WHERE RunId = '{staged.RunId}'
                """));
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Empty(
            await fixture.Database
                .ListPendingPublicationReconciliationsAsync());
        Assert.All(
            new[]
            {
                "prepared_ticket_publication_staged_graphs",
                "prepared_ticket_publication_staged_hydration",
                "prepared_ticket_publication_staged_receipts",
                "prepared_ticket_publication_grouping_impacts",
                "prepared_ticket_publication_staged_grouping",
                "prepared_ticket_publication_grouping_stage_receipts",
                "prepared_ticket_publication_snapshot_descriptors",
                "prepared_ticket_publication_reconciliation_fences",
            },
            table => Assert.Equal(0, fixture.CountLive(table)));

        PreparedTicketPublicationReconciliationStatusResult status =
            await staged.Planner.GetStatusAsync(staged.RunId);
        Assert.Equal(AuthoringStatusValues.Runs.Completed, status.Run.Status);
        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues.Ready,
            status.Promotion.State);
        Assert.False(status.Promotion.MutationFenceHeld);
        Assert.Empty(status.InvalidatedTicketKeys);
        Assert.Equal(
            PreparedTicketPublicationContract.PublicationReconciliationPurpose,
            status.PublicationProof?.Purpose);
        Assert.Equal(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            status.PublicationProof?.ContractVersion);
        Assert.Equal(
            staged.CorpusFingerprint,
            status.PublicationProof?.CorpusFingerprint);
        Assert.Equal(
            replacementDescriptor.PublicationProof?.GroupingFingerprint,
            status.PublicationProof?.GroupingFingerprint);
        Assert.NotEqual(
            status.PublicationProof?.GroupingImpactFingerprint,
            status.PublicationProof?.GroupingFingerprint);

        VerifiedAuthoringSnapshotPair replacement =
            await client.DownloadSnapshotPairAsync(
                "Preparer",
                staged.RunId,
                Path.Combine(
                    fixture.DirectoryPath,
                    "reconciliation-downloads",
                    staged.RunId),
                CancellationToken.None);
        Assert.NotEqual(original.Pair.RunId, replacement.RunId);
        Assert.NotEqual(original.Pair.SnapshotId, replacement.SnapshotId);
        Assert.NotEqual(
            original.Pair.Manifest.DatabaseSha256,
            replacement.Manifest.DatabaseSha256);
        Assert.True(
            replacement.Descriptor.Sequence >
            original.Pair.Descriptor.Sequence);
        PreparedTicketPublicationFingerprints fingerprints =
            await PreparedTicketPublicationFingerprintReader.ReadReconciliationAsync(
                replacement.DatabasePath);
        Assert.Equal(
            replacementDescriptor.PublicationProof?.CorpusFingerprint,
            fingerprints.Corpus);
        Assert.Equal(
            replacementDescriptor.PublicationProof?.GroupingFingerprint,
            fingerprints.Grouping);

        using (SqliteConnection snapshot =
               await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(
                   replacement.DatabasePath))
        {
            Assert.Equal(
                staged.CorpusFingerprint,
                ReadCorpusFingerprint(snapshot));
            foreach ((string key, TicketPreservation expected) in preserved)
            {
                Assert.Equal(
                    expected.SnapshotBytes,
                    ReadTicketSnapshotBytes(snapshot, key));
                Assert.Equal(
                    expected.LiveFingerprint,
                    ReadLiveTicketFingerprint(fixture, key));
            }
            Assert.Equal(cdaGroupingBytes, ReadGroupingBytes(snapshot, "CDA"));
            AssertReconciledGrouping(snapshot);
            AssertRow(
                snapshot,
                "SELECT RequestSummary, ProposalA FROM prepared_tickets WHERE Key = 'FHIR-10028'",
                ["Reconciled request FHIR-10028", "Reconciled A FHIR-10028"]);
            AssertRow(
                snapshot,
                "SELECT RequestSummary, ProposalA FROM prepared_tickets WHERE Key = 'FHIR-803'",
                ["Reconciled request FHIR-803", "Reconciled A FHIR-803"]);
            AssertRow(
                snapshot,
                """
                SELECT Type, Specification
                FROM prepared_jira_hydration
                WHERE TicketKey = 'FHIR-803' AND JiraKey = TicketKey
                """,
                [MovedTicketType, "FHIR"]);
        }

        _ = await new AuthoringSnapshotPairVerifier().VerifyReadyPairAsync(
            "Preparer",
            original.Pair.RunId,
            original.Pair.DirectoryPath);
        Assert.Equal(
            originalHashes,
            await ArtifactHashesAsync(fixture, original));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciliationCandidate_ReadBackGroupingMustMatchProof(
        bool changeGroupingRows)
    {
        using Fixture fixture = new(richGraph: true);
        Corpus corpus = await CreateCorpusAsync(fixture);
        PublicationHttpHandler handler = new(fixture, corpus.Updates);
        using HttpClient http = handler.CreateClient();
        StagedReconciliation staged =
            await StageChangedTicketReconciliationAsync(fixture, corpus, handler, http);
        string originalDescriptor = fixture.Scalar<string>(
            $"""
            SELECT DescriptorJson
            FROM prepared_ticket_publication_snapshot_descriptors
            WHERE RunId = '{staged.RunId}'
            """);
        PreparedTicketPublicationCandidateSnapshot candidate =
            Assert.IsType<PreparedTicketPublicationCandidateSnapshot>(
                JsonSerializer.Deserialize<PreparedTicketPublicationCandidateSnapshot>(
                    originalDescriptor));
        PreparedTicketPublicationReconciliationProof proof =
            Assert.IsType<PreparedTicketPublicationReconciliationProof>(
                (await staged.Planner.GetStatusAsync(staged.RunId)).PublicationProof);
        PreparedTicketSnapshotMaterializer materializer = new(
            fixture.Database,
            fixture.Store,
            new SqliteReviewSnapshotReconciler(fixture.Store),
            Options.Create(new PreparerServiceOptions
            {
                SnapshotDirectory = fixture.SnapshotDirectory,
                SnapshotSchemaVersion = PreparedTicketSnapshotSchemaV3.Version,
            }));
        if (changeGroupingRows)
        {
            await MutateSnapshotAsync(candidate.TemporaryPath,
                "UPDATE prepared_ticket_topics SET ShortDescription = 'Changed candidate topic'");
            candidate = candidate with
            {
                Sha256 = await SqliteReviewSnapshotWriter.ComputeSha256Async(candidate.TemporaryPath),
                SizeBytes = new FileInfo(candidate.TemporaryPath).Length,
            };
        }
        else
        {
            candidate = candidate with { GroupingFingerprint = new string('0', 64) };
            proof = proof with { GroupingFingerprint = candidate.GroupingFingerprint };
            AuthoringRunRecord run = Assert.IsType<AuthoringRunRecord>(
                await fixture.Store.GetRunAsync(staged.RunId));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => materializer.MaterializeReconciliationCandidateAsync(run, proof));
        }

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => materializer.PersistTrustedReconciliationCandidateAsync(candidate, proof));
        Assert.Contains("complete grouping proof", error.Message, StringComparison.Ordinal);
        Assert.Equal(originalDescriptor, fixture.Scalar<string>(
            $"""
            SELECT DescriptorJson
            FROM prepared_ticket_publication_snapshot_descriptors
            WHERE RunId = '{staged.RunId}'
            """));
        Assert.Equal(0, fixture.Scalar<long>(
            $"SELECT COUNT(*) FROM authoring_review_snapshots WHERE RunId = '{staged.RunId}'"));
        Assert.NotNull(await fixture.Store.GetFencedRunAsync("jira-fhir"));
    }

    [Theory]
    [InlineData("reconciliation", 1)]
    [InlineData("reconciliation", 2)]
    [InlineData("reconciliation", 3)]
    [InlineData("snapshot", 1)]
    [InlineData("snapshot", 2)]
    [InlineData("snapshot", 3)]
    public async Task ReconciliationRecovery_RejectsLegacyOrMissingGroupingProof(
        string evidence,
        int contractVersion)
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation pending = await CreatePendingReconciliationAsync(fixture);
        await ExecuteDatabaseAsync(fixture, evidence == "reconciliation"
            ? """
              UPDATE prepared_ticket_publication_reconciliation_proofs
              SET ProofJson = json_remove(
                  json_set(ProofJson, '$.ContractVersion', @version),
                  '$.GroupingFingerprint')
              WHERE RunId = @runId
              """
            : """
              UPDATE authoring_review_snapshots
              SET PublicationProofJson = CASE WHEN @version = 3
                  THEN json_remove(PublicationProofJson, '$.groupingFingerprint')
                  ELSE json_set(PublicationProofJson, '$.contractVersion', @version)
                  END
              WHERE RunId = @runId
              """,
            ("@version", contractVersion),
            ("@runId", pending.RunId));
        Type expectedException = evidence == "snapshot"
            ? typeof(InvalidOperationException)
            : contractVersion < 3 ? typeof(NotSupportedException) : typeof(ArgumentException);
        Exception? promotionError = await Record.ExceptionAsync(
            () => fixture.Database.PromotePublicationReconciliationAsync(
                pending.RunId, pending.Promotion.FinalPath));
        Assert.IsType(expectedException, promotionError);

        PreparedTicketPublicationReconciliationException recoveryError =
            await Assert.ThrowsAsync<PreparedTicketPublicationReconciliationException>(
                () => CreateRecoveryService(fixture).RecoverAsync(pending.RunId));
        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes.PromotionRecoveryFailure,
            recoveryError.FailureCode);
        await AssertRecoveryFailureRetainsFenceAsync(fixture, pending.RunId);
        Assert.True(File.Exists(pending.Promotion.TemporaryPath));
        Assert.False(File.Exists(pending.Promotion.FinalPath));
    }

    [Fact]
    public async Task Recovery_WithMatchingProvenanceButModifiedCandidate_FailsAndRetainsFence()
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation pending =
            await CreatePendingReconciliationAsync(fixture);
        PreparerDatabase.PublicationReconciliationPromotion promotion =
            pending.Promotion;
        long originalSize = new FileInfo(promotion.TemporaryPath).Length;
        string originalProvenance = await ReadSnapshotProvenanceIdAsync(
            promotion.TemporaryPath);

        await MutateSnapshotAsync(
            promotion.TemporaryPath,
            """
            UPDATE prepared_tickets
            SET RequestSummary = 'Xeconciled request FHIR-10028'
            WHERE Key = 'FHIR-10028'
            """);

        Assert.Equal(promotion.SnapshotId, originalProvenance);
        Assert.Equal(
            promotion.SnapshotId,
            await ReadSnapshotProvenanceIdAsync(
                promotion.TemporaryPath));
        Assert.Equal(
            1,
            await ReadSnapshotProvenanceCountAsync(
                promotion.TemporaryPath));
        Assert.Equal(originalSize, new FileInfo(promotion.TemporaryPath).Length);
        Assert.NotEqual(
            promotion.CandidateSha256,
            await SqliteReviewSnapshotWriter.ComputeSha256Async(
                promotion.TemporaryPath));
        PreparedTicketPublicationRecoveryService recovery =
            CreateRecoveryService(fixture);

        PreparedTicketPublicationReconciliationException error =
            await Assert.ThrowsAsync<
                PreparedTicketPublicationReconciliationException>(
                () => recovery.RecoverAsync(pending.RunId));

        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .PromotionRecoveryFailure,
            error.FailureCode);
        Assert.True(File.Exists(promotion.TemporaryPath));
        Assert.False(File.Exists(promotion.FinalPath));
        Assert.Equal(
            promotion.SnapshotId,
            await ReadSnapshotProvenanceIdAsync(
                promotion.TemporaryPath));
        await AssertRecoveryFailureRetainsFenceAsync(
            fixture,
            pending.RunId);
    }

    [Theory]
    [InlineData("missing-candidate", false)]
    [InlineData("corrupt-candidate", false)]
    [InlineData("missing-provenance", false)]
    [InlineData("conflicting-final", false)]
    [InlineData("missing-snapshot-record", false)]
    [InlineData("snapshot-record-conflict", false)]
    [InlineData("checksum-mismatch", false)]
    [InlineData("missing-staging", true)]
    [InlineData("cancellation", false)]
    [InlineData("competing-recovery", true)]
    public async Task Recovery_AdverseEvidenceMatrix_UsesOnlyJournaledBytes(
        string scenario,
        bool recovers)
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation pending =
            await CreatePendingReconciliationAsync(fixture);
        PreparerDatabase.PublicationReconciliationPromotion promotion =
            pending.Promotion;
        PreparedTicketPublicationRecoveryService recovery =
            CreateRecoveryService(fixture);
        CancellationToken recoveryToken = CancellationToken.None;
        using CancellationTokenSource cancellation = new();

        switch (scenario)
        {
            case "missing-candidate":
                File.Delete(promotion.TemporaryPath);
                break;
            case "corrupt-candidate":
                await File.WriteAllBytesAsync(
                    promotion.TemporaryPath,
                    [0x53, 0x51, 0x4c, 0x69, 0x74, 0x65]);
                break;
            case "missing-provenance":
                await MutateSnapshotAsync(
                    promotion.TemporaryPath,
                    "DELETE FROM authoring_snapshot_provenance");
                break;
            case "conflicting-final":
                File.Copy(
                    promotion.TemporaryPath,
                    promotion.FinalPath,
                    overwrite: false);
                await MutateSnapshotAsync(
                    promotion.FinalPath,
                    """
                    UPDATE prepared_tickets
                    SET RequestSummary = 'Xeconciled request FHIR-10028'
                    WHERE Key = 'FHIR-10028'
                    """);
                break;
            case "missing-snapshot-record":
                await ExecuteDatabaseAsync(
                    fixture,
                    """
                    DELETE FROM authoring_review_snapshots
                    WHERE Id = @snapshotId
                    """,
                    ("@snapshotId", promotion.SnapshotId));
                break;
            case "snapshot-record-conflict":
                await ExecuteDatabaseAsync(
                    fixture,
                    """
                    UPDATE authoring_review_snapshots
                    SET Sequence = Sequence + 10000
                    WHERE Id = @snapshotId
                    """,
                    ("@snapshotId", promotion.SnapshotId));
                break;
            case "checksum-mismatch":
                await ExecuteDatabaseAsync(
                    fixture,
                    """
                    UPDATE authoring_review_snapshots
                    SET ChecksumSha256 = @checksum
                    WHERE Id = @snapshotId
                    """,
                    ("@checksum", new string('0', 64)),
                    ("@snapshotId", promotion.SnapshotId));
                break;
            case "missing-staging":
                await DeleteReconciliationStagingAsync(
                    fixture,
                    pending.RunId);
                break;
            case "cancellation":
                cancellation.Cancel();
                recoveryToken = cancellation.Token;
                break;
            case "competing-recovery":
                PreparedTicketPublicationRecoveryService competitor =
                    CreateRecoveryService(fixture);
                AuthoringSnapshotDescriptor[] descriptors =
                    await Task.WhenAll(
                        recovery.RecoverAsync(pending.RunId),
                        competitor.RecoverAsync(pending.RunId));
                Assert.All(
                    descriptors,
                    descriptor => Assert.Equal(
                        promotion.SnapshotId,
                        descriptor.SnapshotId));
                await AssertRecoverySucceededAsync(
                    fixture,
                    pending.RunId,
                    promotion);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(scenario),
                    scenario,
                    "Unknown recovery evidence scenario.");
        }

        if (recovers)
        {
            AuthoringSnapshotDescriptor descriptor =
                await recovery.RecoverAsync(
                    pending.RunId,
                    recoveryToken);
            Assert.Equal(promotion.SnapshotId, descriptor.SnapshotId);
            await AssertRecoverySucceededAsync(
                fixture,
                pending.RunId,
                promotion);
            return;
        }

        PreparedTicketPublicationReconciliationException error =
            await Assert.ThrowsAsync<
                PreparedTicketPublicationReconciliationException>(
                () => recovery.RecoverAsync(
                    pending.RunId,
                    recoveryToken));
        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .PromotionRecoveryFailure,
            error.FailureCode);
        await AssertRecoveryFailureRetainsFenceAsync(
            fixture,
            pending.RunId);
        if (scenario == "missing-provenance")
        {
            Assert.Equal(
                0,
                await ReadSnapshotProvenanceCountAsync(
                    promotion.TemporaryPath));
        }
    }

    [Fact]
    public async Task CanonicalUnpublishedAbandonmentIsAuditedAndRestrictsSnapshotWorkflows()
    {
        using Fixture fixture = new(richGraph: true);
        Corpus corpus = await CreateCorpusAsync(fixture);
        PublicationHttpHandler handler = new(fixture, corpus.Updates);
        using HttpClient http = handler.CreateClient();
        AuthoringControlClient client = new(http);
        Publication original =
            await PublishAsync(fixture, client, corpus.Source.Descriptor);
        KeyValuePair<string, string>[] originalHashes =
            await ArtifactHashesAsync(fixture, original);
        StagedReconciliation staged =
            await StageChangedTicketReconciliationAsync(
                fixture,
                corpus,
                handler,
                http);
        AuthoringRunRecord prequeuedSnapshot =
            await fixture.Store.CreateRunAsync(
                "jira-fhir",
                [new("FHIR-99000", "fhir", "revision-0")],
                databaseOnly: false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Database.AbandonPublicationReconciliationAsync(
                staged.RunId,
                "not yet database-promoted"));
        PreparerDatabase.PublicationReconciliationPromotion promotion =
            await fixture.Database.PromotePublicationReconciliationAsync(
                staged.RunId,
                Path.Combine(
                    fixture.SnapshotDirectory,
                    $"jira-fhir-{staged.RunId}.db"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Database.AbandonPublicationReconciliationAsync(
                staged.RunId,
                " "));

        DateTimeOffset abandonedAt =
            new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        const string reason =
            "operator accepted canonical data without replacement snapshot";
        await fixture.Database.AbandonPublicationReconciliationAsync(
            staged.RunId,
            reason,
            abandonedAt);

        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues
                .CanonicalUnpublished,
            fixture.Scalar<string>(
                $"""
                SELECT PromotionState
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = '{staged.RunId}'
                """));
        Assert.Equal(
            JsonSerializer.Serialize(new object?[]
            {
                abandonedAt.ToString("O", CultureInfo.InvariantCulture),
                reason,
            }),
            ReadAbandonmentAudit(fixture, staged.RunId));
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        AuthoringRunRecord abandonedRun =
            Assert.IsType<AuthoringRunRecord>(
                await fixture.Store.GetRunAsync(staged.RunId));
        Assert.Equal(
            AuthoringStatusValues.Runs.Abandoned,
            abandonedRun.Status);
        AuthoringRunControlStatus abandonedStatus =
            await new AuthoringRunControlService(
                fixture.Store,
                new AuthoringRetryPolicy(
                    Options.Create(new ProcessingServiceOptions())))
                .GetStatusAsync("jira-fhir", staged.RunId);
        Assert.True(abandonedStatus.Run.State!.IsTerminal);
        Assert.False(abandonedStatus.Run.State.IsRecoverable);
        Assert.False(File.Exists(promotion.FinalPath));
        Assert.True(File.Exists(promotion.TemporaryPath));
        Assert.Equal(
            $"Reconciled request {RevisedKeys[0]}",
            fixture.Scalar<string>(
                $"SELECT RequestSummary FROM prepared_tickets WHERE Key = '{RevisedKeys[0]}'"));

        PreparedTicketPublicationRefreshService restrictedRefreshService =
            fixture.CreateRefreshService(
                new OrchestratorHydrationFetcher(
                    http,
                    NullLogger.Instance));
        await AssertCanonicalUnpublishedRestrictionAsync(
            () => fixture.CreatePostProcessor(
                    restrictedRefreshService)
                .FinalizeRunAsync(prequeuedSnapshot.Id));
        PreparedTicketSnapshotMaterializer restrictedMaterializer = new(
            fixture.Database,
            fixture.Store,
            new SqliteReviewSnapshotReconciler(fixture.Store),
            Options.Create(new PreparerServiceOptions
            {
                SnapshotDirectory = fixture.SnapshotDirectory,
                SnapshotSchemaVersion =
                    PreparedTicketSnapshotSchemaV3.Version,
            }));
        await AssertCanonicalUnpublishedRestrictionAsync(
            () => restrictedMaterializer.MaterializeAsync(
                prequeuedSnapshot,
                PreparedTicketSnapshotSchemaResolver.Resolve(
                    PreparedTicketSnapshotSchemaV3.Version)));
        await AssertCanonicalUnpublishedRestrictionAsync(
            () => fixture.Store.TryAcquireMutationFenceAsync(
                "jira-fhir",
                prequeuedSnapshot.Id));
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        await fixture.Store.SupersedeRunAsync(
            prequeuedSnapshot.Id,
            "canonical-unpublished-restriction");

        await AssertCanonicalUnpublishedRestrictionAsync(
            () => fixture.Store.CreateRunAsync(
                "jira-fhir",
                [new("FHIR-99001", "fhir", "revision-1")],
                databaseOnly: false));
        PreparedTicketPublicationProtectedInventory inventory =
            await fixture.ReadCurrentAsync();
        PreparedTicketPublicationCorpusItem retained = inventory.Corpus[0];
        await AssertCanonicalUnpublishedRestrictionAsync(
            () => fixture.Store.CreateMaintenanceRunAsync(
                "jira-fhir",
                [
                    new AuthoringMaintenanceRunItem(
                        retained.TicketKey,
                        retained.ItemKind,
                        retained.ExpectedSourceRevision,
                        retained.ReceiptId),
                ],
                AuthoringRunPurposeValues.PublicationRefresh,
                databaseOnly: false,
                sourceRunId: corpus.Source.Run.Id));
        await AssertCanonicalUnpublishedRestrictionAsync(
            () => fixture.Database.CreatePublicationReconciliationAsync(
                staged.Comparison));

        PreparedTicketPublicationMaintenanceController controller = new(
            restrictedRefreshService,
            staged.Planner,
            new PreparedTicketPublicationRecoveryService(
                fixture.Database,
                fixture.Store,
                NullLogger<
                    PreparedTicketPublicationRecoveryService>.Instance),
            fixture.Database);
        ConflictObjectResult restrictedRefresh =
            Assert.IsType<ConflictObjectResult>(
                await controller.Start(
                    corpus.Source.Run.Id,
                    CancellationToken.None));
        PreparedTicketPublicationRefreshFailure restrictedFailure =
            Assert.IsType<PreparedTicketPublicationRefreshFailure>(
                restrictedRefresh.Value);
        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .CanonicalUnpublishedRestriction,
            restrictedFailure.Error);
        Assert.Equal(
            [staged.RunId],
            restrictedFailure.ConflictingRunIds);
        Assert.Equal(staged.RunId, restrictedFailure.RunId);

        AuthoringRunRecord databaseOnly = await fixture.Store.CreateRunAsync(
            "jira-fhir",
            [new("FHIR-99002", "fhir", "revision-1")],
            databaseOnly: true,
            maxActiveRuns: 1);
        Assert.True(databaseOnly.DatabaseOnly);
        Assert.Equal(AuthoringStatusValues.Runs.Queued, databaseOnly.Status);

        AuthoringRunRecord recoveryBypass =
            await fixture.Store.CreateMaintenanceRunAsync(
                "jira-fhir",
                [
                    new AuthoringMaintenanceRunItem(
                        retained.TicketKey,
                        retained.ItemKind,
                        retained.ExpectedSourceRevision,
                        retained.ReceiptId),
                ],
                AuthoringRunPurposeValues.CanonicalEpochRecovery,
                databaseOnly: false,
                sourceRunId: staged.RunId);
        Assert.Equal(
            AuthoringRunPurposeValues.CanonicalEpochRecovery,
            recoveryBypass.Purpose);
        AuthoringConflictException enriched =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => fixture.Database.EnsureSnapshotWorkflowAllowedAsync(
                    new AuthoringSnapshotWorkflowIntent(
                        "jira-fhir",
                        DatabaseOnly: false,
                        AuthoringRunPurposeValues.Authoring)));
        Assert.Equal(
            [staged.RunId, recoveryBypass.Id],
            enriched.RelatedRunIds);
        Assert.Equal(
            originalHashes,
            await ArtifactHashesAsync(fixture, original));
        _ = await new AuthoringSnapshotPairVerifier().VerifyReadyPairAsync(
            "Preparer",
            original.Pair.RunId,
            original.Pair.DirectoryPath);
    }

    [Fact]
    public async Task CanonicalUnpublishedRecovery_CreatesVerifiedSnapshotAndReenablesSnapshotRuns()
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation pending =
            await CreatePendingReconciliationAsync(fixture);
        DateTimeOffset abandonedAt =
            new(2026, 9, 17, 14, 0, 0, TimeSpan.Zero);
        const string abandonmentReason =
            "replacement file publication could not be completed";
        await fixture.Database.AbandonPublicationReconciliationAsync(
            pending.RunId,
            abandonmentReason,
            abandonedAt);
        string sourceAudit = ReadAbandonmentAudit(
            fixture,
            pending.RunId);
        byte[] priorDatabase = await File.ReadAllBytesAsync(
            pending.PriorPublication.DatabasePath);
        byte[] priorDescriptor = await File.ReadAllBytesAsync(
            pending.PriorPublication.DescriptorPath);
        string canonicalBefore = fixture.ReadAuthoredAndGroupingState();
        string metadataBefore = fixture.ReadPublicationMetadataState();
        string[] priorSnapshotIds =
            (await fixture.Store.GetSnapshotRecordsAsync())
            .Select(snapshot => snapshot.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        PreparedTicketCanonicalEpochRecoveryService recovery =
            CreateCanonicalEpochRecoveryService(fixture);
        PreparedTicketCanonicalEpochRecoveryStartResult started =
            await recovery.StartAsync(pending.RunId);
        PreparedTicketCanonicalEpochRecoveryStartResult duplicate =
            await recovery.StartAsync(pending.RunId);

        Assert.False(started.ExistingRun);
        Assert.True(duplicate.ExistingRun);
        Assert.Equal(
            started.Status.Run.RunId,
            duplicate.Status.Run.RunId);
        Assert.Equal(
            PreparedTicketCanonicalEpochRecoveryContract.Purpose,
            started.Status.Run.Purpose);
        Assert.Equal(pending.RunId, started.Status.Run.SourceRunId);
        Assert.Equal(
            started.Status.Items.Count,
            started.Status.Frozen.AcceptedTicketCount);
        Assert.All(
            started.Status.Items,
            item => Assert.Equal(
                AuthoringStatusValues.Items.Complete,
                item.Status));
        Assert.Empty(
            await fixture.Store.GetRunStagesAsync(
                started.Status.Run.RunId));
        Assert.True(started.Status.Recovery.MutationFenceHeld);

        PublicationHttpHandler noWorkerRequests = new(fixture, new Dictionary<string, DateTimeOffset>());
        using HttpClient recoveryHttp = noWorkerRequests.CreateClient();
        AuthoringSnapshotDescriptor descriptor =
            Assert.IsType<AuthoringSnapshotDescriptor>(
                await CreateCanonicalEpochRecoverySurfaces(
                    fixture, recovery, recoveryHttp).PostProcessor
                    .FinalizeRunAsync(started.Status.Run.RunId));
        Assert.Empty(noWorkerRequests.Requests);
        Assert.All(
            await fixture.Store.GetRunItemsAsync(started.Status.Run.RunId),
            item => Assert.Equal(0, item.AttemptCount));
        PreparedTicketCanonicalEpochRecoveryStatusResult completed =
            await recovery.GetStatusAsync(started.Status.Run.RunId);

        Assert.Equal(
            PreparedTicketCanonicalEpochRecoveryStateValues.Ready,
            completed.Recovery.State);
        Assert.Equal(
            AuthoringStatusValues.Runs.Completed,
            completed.Run.Status);
        Assert.False(completed.Recovery.MutationFenceHeld);
        Assert.Equal(
            descriptor.SnapshotId,
            completed.Snapshot?.SnapshotId);
        Assert.Equal(descriptor.Sha256, completed.Snapshot?.Sha256);
        Assert.Equal(
            PreparedTicketSnapshotSchemaV3.Version,
            descriptor.SchemaVersion);
        Assert.Equal(
            PreparedTicketPublicationContract
                .CanonicalEpochRecoveryPurpose,
            descriptor.PublicationProof?.Purpose);
        Assert.Equal(
            completed.Frozen.CorpusFingerprint,
            descriptor.PublicationProof?.CorpusFingerprint);
        Assert.Equal(
            completed.Frozen.GroupingFingerprint,
            descriptor.PublicationProof?.GroupingFingerprint);
        Assert.True(File.Exists(pending.Promotion.TemporaryPath));
        Assert.True(File.Exists(Path.Combine(
            fixture.SnapshotDirectory,
            descriptor.FileName)));
        Assert.Equal(
            descriptor.Sha256,
            await SqliteReviewSnapshotWriter.ComputeSha256Async(
                Path.Combine(
                    fixture.SnapshotDirectory,
                    descriptor.FileName)));
        PreparedTicketPublicationFingerprints snapshotFingerprints =
            await PreparedTicketPublicationFingerprintReader
                .ReadCanonicalEpochRecoveryAsync(
                Path.Combine(
                    fixture.SnapshotDirectory,
                    descriptor.FileName));
        Assert.Equal(
            completed.Frozen.CorpusFingerprint,
            snapshotFingerprints.Corpus);
        Assert.Equal(
            completed.Frozen.GroupingFingerprint,
            snapshotFingerprints.Grouping);

        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues
                .CanonicalUnpublished,
            fixture.Scalar<string>(
                $"""
                SELECT PromotionState
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = '{pending.RunId}'
                """));
        Assert.Equal(
            AuthoringStatusValues.Runs.Abandoned,
            (await fixture.Store.GetRunAsync(pending.RunId))!.Status);
        Assert.Equal(
            sourceAudit,
            ReadAbandonmentAudit(fixture, pending.RunId));
        Assert.Equal(canonicalBefore, fixture.ReadAuthoredAndGroupingState());
        Assert.Equal(metadataBefore, fixture.ReadPublicationMetadataState());
        Assert.Equal(priorDatabase, await File.ReadAllBytesAsync(
            pending.PriorPublication.DatabasePath));
        Assert.Equal(priorDescriptor, await File.ReadAllBytesAsync(
            pending.PriorPublication.DescriptorPath));
        Assert.NotEqual(pending.Promotion.SnapshotId, descriptor.SnapshotId);
        Assert.True(descriptor.Sequence > pending.Promotion.Sequence);
        Assert.Equal(
            1L,
            fixture.Scalar<long>(
                $"""
                SELECT COUNT(*)
                FROM prepared_ticket_canonical_epoch_recovery_resolutions
                WHERE RunId = '{completed.Run.RunId}'
                  AND SourceRunId = '{pending.RunId}'
                  AND SnapshotId = '{descriptor.SnapshotId}'
                """));
        Assert.Equal(
            priorSnapshotIds,
            (await fixture.Store.GetSnapshotRecordsAsync())
            .Where(snapshot => snapshot.Id != descriptor.SnapshotId)
            .Select(snapshot => snapshot.Id)
            .Order(StringComparer.Ordinal)
            .ToArray());

        await fixture.Database.EnsureSnapshotWorkflowAllowedAsync(
            new AuthoringSnapshotWorkflowIntent(
                "jira-fhir",
                DatabaseOnly: false,
                AuthoringRunPurposeValues.Authoring));
        AuthoringRunRecord nextSnapshotRun =
            await fixture.Store.CreateRunAsync(
                "jira-fhir",
                [new("FHIR-99003", "fhir", "revision-1")],
                databaseOnly: false);
        Assert.Equal(
            AuthoringStatusValues.Runs.Queued,
            nextSnapshotRun.Status);
    }

    [Fact]
    public async Task CanonicalEpochRecovery_FreezesDatabaseOnlyOutputAcceptedAfterAbandonment()
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation source = await CreateAbandonedReconciliationAsync(fixture);
        AuthoringRunRecord additional = await fixture.CreateAdditionalCurrentOutputAsync(
            "AdditionalCanonical", ["FHIR-99005"], groupOutput: false);
        string preserved = ReadLiveTicketFingerprint(fixture, "FHIR-99005");
        PreparedTicketCanonicalEpochRecoveryService recovery =
            CreateCanonicalEpochRecoveryService(fixture);

        PreparedTicketCanonicalEpochRecoveryStartResult started =
            await recovery.StartAsync(source.RunId);
        PreparerDatabase.CanonicalEpochRecoveryEvidence evidence =
            Assert.IsType<PreparerDatabase.CanonicalEpochRecoveryEvidence>(
                await fixture.Database.GetCanonicalEpochRecoveryEvidenceAsync(started.Status.Run.RunId));
        PreparedTicketPublicationCorpusItem included = Assert.Single(
            evidence.Recipe.Corpus, item => item.TicketKey == "FHIR-99005");
        Assert.Equal(additional.Id, included.ContributingRunId);
        Assert.Equal(source.Promotion.ReceiptCount + 1, started.Status.Frozen.AcceptedTicketCount);
        AuthoringSnapshotDescriptor snapshot =
            await recovery.RecoverAsync(started.Status.Run.RunId);
        Assert.Equal(started.Status.Frozen.AcceptedTicketCount, snapshot.ReceiptCount);
        Assert.Equal(preserved, ReadLiveTicketFingerprint(fixture, "FHIR-99005"));
        Assert.Equal(AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await fixture.Store.GetRunAsync(additional.Id))?.Status);
    }

    [Fact]
    public async Task CanonicalEpochRecovery_RejectsStaleAuthoringEpoch()
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation pending =
            await CreateAbandonedReconciliationAsync(fixture);
        fixture.Execute(
            """
            UPDATE authoring_processor_modes
            SET Epoch = Epoch + 1
            WHERE ProcessorKind = 'jira-fhir'
            """);
        PreparedTicketCanonicalEpochRecoveryService recovery =
            CreateCanonicalEpochRecoveryService(fixture);

        PreparedTicketCanonicalEpochRecoveryException error =
            await Assert.ThrowsAsync<
                PreparedTicketCanonicalEpochRecoveryException>(
                () => recovery.StartAsync(pending.RunId));

        Assert.Equal(
            PreparedTicketCanonicalEpochRecoveryFailureCodes
                .StaleCanonicalEpoch,
            error.FailureCode);
        Assert.Equal(
            0L,
            fixture.Scalar<long>(
                "SELECT COUNT(*) FROM prepared_ticket_canonical_epoch_recoveries"));
        await AssertCanonicalUnpublishedRestrictionAsync(
            () => fixture.Database.EnsureSnapshotWorkflowAllowedAsync(
                new AuthoringSnapshotWorkflowIntent(
                    "jira-fhir",
                    DatabaseOnly: false,
                    AuthoringRunPurposeValues.Authoring)));
    }

    [Fact]
    public async Task CanonicalEpochRecovery_ChangedCanonicalStateRetainsRunFenceAndRestriction()
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation pending =
            await CreateAbandonedReconciliationAsync(fixture);
        PreparedTicketCanonicalEpochRecoveryService recovery =
            CreateCanonicalEpochRecoveryService(fixture);
        PreparedTicketCanonicalEpochRecoveryStartResult started =
            await recovery.StartAsync(pending.RunId);
        fixture.Execute(
            """
            UPDATE prepared_tickets
            SET RequestSummary = RequestSummary || ' changed'
            WHERE Key = @ticketKey
            """,
            ("@ticketKey", FhirKeys[0]));

        PreparedTicketCanonicalEpochRecoveryException error =
            await Assert.ThrowsAsync<
                PreparedTicketCanonicalEpochRecoveryException>(
                () => recovery.RecoverAsync(
                    started.Status.Run.RunId));

        Assert.Equal(
            PreparedTicketCanonicalEpochRecoveryFailureCodes
                .CanonicalStateChanged,
            error.FailureCode);
        PreparedTicketCanonicalEpochRecoveryStatusResult status =
            await recovery.GetStatusAsync(started.Status.Run.RunId);
        Assert.Equal(
            AuthoringStatusValues.Runs.Error,
            status.Run.Status);
        Assert.True(status.Recovery.MutationFenceHeld);
        Assert.Equal(
            PreparedTicketCanonicalEpochRecoveryFailureCodes
                .CanonicalStateChanged,
            status.Recovery.FailureCode);
        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues
                .CanonicalUnpublished,
            fixture.Scalar<string>(
                $"""
                SELECT PromotionState
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = '{pending.RunId}'
                """));
        Assert.Equal(
            0L,
            fixture.Scalar<long>(
                "SELECT COUNT(*) FROM prepared_ticket_canonical_epoch_recovery_resolutions"));
    }

    [Fact]
    public async Task CanonicalEpochRecovery_ConflictingFinalFileRetriesSameRun()
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation pending =
            await CreateAbandonedReconciliationAsync(fixture);
        PreparedTicketCanonicalEpochRecoveryService recovery =
            CreateCanonicalEpochRecoveryService(fixture);
        PreparedTicketCanonicalEpochRecoveryStartResult started =
            await recovery.StartAsync(pending.RunId);
        string runId = started.Status.Run.RunId;
        await fixture.Store.MarkRunFinalizingAsync(runId);
        PreparerDatabase.CanonicalEpochRecoveryEvidence evidence =
            Assert.IsType<PreparerDatabase.CanonicalEpochRecoveryEvidence>(
                await fixture.Database
                    .GetCanonicalEpochRecoveryEvidenceAsync(runId));
        PreparedTicketPublicationCandidateSnapshot candidate =
            await CreateCanonicalEpochRecoveryMaterializer(fixture)
                .MaterializeCanonicalEpochRecoveryCandidateAsync(
                    Assert.IsType<AuthoringRunRecord>(
                        await fixture.Store.GetRunAsync(runId)),
                    evidence.Recipe,
                    PreparerDatabase.CreateCanonicalEpochRecoveryProof(
                        evidence.Recipe));
        await File.WriteAllTextAsync(
            candidate.FinalPath,
            "conflicting final bytes");

        PreparedTicketCanonicalEpochRecoveryException failure =
            await Assert.ThrowsAsync<
                PreparedTicketCanonicalEpochRecoveryException>(
                () => recovery.RecoverAsync(runId));
        Assert.Equal(
            PreparedTicketCanonicalEpochRecoveryFailureCodes
                .SnapshotRecoveryFailure,
            failure.FailureCode);
        PreparedTicketCanonicalEpochRecoveryStatusResult failed =
            await recovery.GetStatusAsync(runId);
        Assert.True(failed.Recovery.MutationFenceHeld);
        Assert.Equal(
            AuthoringStatusValues.Runs.Error,
            failed.Run.Status);
        Assert.Equal(
            0L,
            fixture.Scalar<long>(
                "SELECT COUNT(*) FROM prepared_ticket_canonical_epoch_recovery_resolutions"));

        File.Delete(candidate.FinalPath);
        AuthoringSnapshotDescriptor descriptor =
            await recovery.RecoverAsync(runId);
        PreparedTicketCanonicalEpochRecoveryStatusResult retried =
            await recovery.GetStatusAsync(runId);
        Assert.Equal(runId, descriptor.RunId);
        Assert.Equal(
            PreparedTicketCanonicalEpochRecoveryStateValues.Ready,
            retried.Recovery.State);
        Assert.False(retried.Recovery.MutationFenceHeld);
    }

    [Fact]
    public async Task CanonicalEpochRecovery_StartupResumesFilePublishedBeforeCas()
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation pending =
            await CreateAbandonedReconciliationAsync(fixture);
        PreparedTicketCanonicalEpochRecoveryService firstGeneration =
            CreateCanonicalEpochRecoveryService(fixture);
        PreparedTicketCanonicalEpochRecoveryStartResult started =
            await firstGeneration.StartAsync(pending.RunId);
        string runId = started.Status.Run.RunId;
        await fixture.Store.MarkRunFinalizingAsync(runId);
        PreparerDatabase.CanonicalEpochRecoveryEvidence evidence =
            Assert.IsType<PreparerDatabase.CanonicalEpochRecoveryEvidence>(
                await fixture.Database
                    .GetCanonicalEpochRecoveryEvidenceAsync(runId));
        PreparedTicketPublicationCandidateSnapshot candidate =
            await CreateCanonicalEpochRecoveryMaterializer(fixture)
                .MaterializeCanonicalEpochRecoveryCandidateAsync(
                    Assert.IsType<AuthoringRunRecord>(
                        await fixture.Store.GetRunAsync(runId)),
                    evidence.Recipe,
                    PreparerDatabase.CreateCanonicalEpochRecoveryProof(
                        evidence.Recipe));
        await PreparedTicketSnapshotMaterializer
            .ValidateCanonicalEpochRecoverySnapshotAsync(
                candidate,
                candidate.TemporaryPath);
        File.Move(
            candidate.TemporaryPath,
            candidate.FinalPath,
            overwrite: false);

        Assert.Equal(
            AuthoringStatusValues.Snapshots.Creating,
            fixture.Scalar<string>(
                $"""
                SELECT Status
                FROM authoring_review_snapshots
                WHERE Id = '{candidate.SnapshotId}'
                """));
        Assert.Equal(
            0L,
            fixture.Scalar<long>(
                "SELECT COUNT(*) FROM prepared_ticket_canonical_epoch_recovery_resolutions"));
        PreparedTicketCanonicalEpochRecoveryService restarted =
            CreateCanonicalEpochRecoveryService(fixture);
        await restarted.RecoverPendingAsync();

        PreparedTicketCanonicalEpochRecoveryStatusResult completed =
            await restarted.GetStatusAsync(runId);
        Assert.Equal(
            PreparedTicketCanonicalEpochRecoveryStateValues.Ready,
            completed.Recovery.State);
        Assert.Equal(
            candidate.SnapshotId,
            completed.Snapshot?.SnapshotId);
        Assert.True(File.Exists(candidate.FinalPath));
        Assert.False(File.Exists(candidate.TemporaryPath));
    }

    [Theory]
    [InlineData("epoch", "stale-canonical-epoch")]
    [InlineData("hydration", "canonical-state-changed")]
    [InlineData("grouping", "canonical-state-changed")]
    [InlineData("selection", "recovery-evidence-conflict")]
    public async Task CanonicalEpochRecovery_RevalidatesExactStateBeforeFinalCas(
        string mutation,
        string expectedFailure)
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation source = await CreateAbandonedReconciliationAsync(fixture);
        PreparedTicketCanonicalEpochRecoveryService recovery =
            CreateCanonicalEpochRecoveryService(fixture);
        string runId = (await recovery.StartAsync(source.RunId)).Status.Run.RunId;
        PreparedTicketPublicationCandidateSnapshot candidate =
            await CreateCanonicalEpochRecoveryCandidateAsync(fixture, runId);
        File.Move(candidate.TemporaryPath, candidate.FinalPath);
        fixture.Execute(mutation switch
        {
            "epoch" => "UPDATE authoring_processor_modes SET Epoch = Epoch + 1",
            "hydration" =>
                "UPDATE prepared_ticket_hydration SET DescriptionHtml = COALESCE(DescriptionHtml, '') || '<p>changed</p>'",
            "grouping" =>
                "UPDATE prepared_ticket_topics SET ShortDescription = ShortDescription || ' changed'",
            "selection" =>
                "UPDATE authoring_run_items SET AcceptedReceiptId = 'wrong-receipt' WHERE RunId = @runId",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        }, ("@runId", runId));

        PreparedTicketCanonicalEpochRecoveryException failure =
            await Assert.ThrowsAsync<PreparedTicketCanonicalEpochRecoveryException>(
                () => recovery.RecoverAsync(runId));

        Assert.Equal(expectedFailure, failure.FailureCode);
        await AssertCanonicalEpochRecoveryPendingAsync(fixture, recovery, runId);
        Assert.Equal(candidate.Sha256,
            await SqliteReviewSnapshotWriter.ComputeSha256Async(candidate.FinalPath));
    }

    [Theory]
    [InlineData("modified-candidate", "snapshot-recovery-failure")]
    [InlineData("missing-candidate", "snapshot-recovery-failure")]
    [InlineData("changed-provenance", "snapshot-recovery-failure")]
    [InlineData("missing-snapshot-record", "recovery-evidence-conflict")]
    public async Task CanonicalEpochRecovery_FailedEvidenceRetainsFenceAndSurvivesGenericStartup(
        string mutation,
        string expectedFailure)
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation source = await CreateAbandonedReconciliationAsync(fixture);
        PreparedTicketCanonicalEpochRecoveryService recovery =
            CreateCanonicalEpochRecoveryService(fixture);
        string runId = (await recovery.StartAsync(source.RunId)).Status.Run.RunId;
        PreparedTicketPublicationCandidateSnapshot candidate =
            await CreateCanonicalEpochRecoveryCandidateAsync(fixture, runId);
        string provenance = await ReadSnapshotProvenanceIdAsync(candidate.TemporaryPath);
        Assert.Equal(candidate.SnapshotId, provenance);
        Assert.Equal(candidate.Sha256,
            await SqliteReviewSnapshotWriter.ComputeSha256Async(candidate.TemporaryPath));
        switch (mutation)
        {
            case "modified-candidate":
                await MutateSnapshotAsync(candidate.TemporaryPath,
                    "UPDATE prepared_tickets SET RequestSummary = 'modified after provenance'");
                Assert.Equal(provenance, await ReadSnapshotProvenanceIdAsync(candidate.TemporaryPath));
                break;
            case "missing-candidate":
                File.Delete(candidate.TemporaryPath);
                break;
            case "changed-provenance":
                await MutateSnapshotAsync(candidate.TemporaryPath,
                    "UPDATE authoring_snapshot_provenance SET Sequence = Sequence + 1");
                break;
            case "missing-snapshot-record":
                fixture.Execute("DELETE FROM authoring_review_snapshots WHERE Id = @id",
                    ("@id", candidate.SnapshotId));
                break;
        }

        PreparedTicketCanonicalEpochRecoveryException failure =
            await Assert.ThrowsAsync<PreparedTicketCanonicalEpochRecoveryException>(
                () => recovery.RecoverAsync(runId));
        Assert.Equal(expectedFailure, failure.FailureCode);
        await AssertCanonicalEpochRecoveryPendingAsync(fixture, recovery, runId);
        string snapshotState = fixture.DumpTables("authoring_review_snapshots");
        string journal = fixture.DumpTables("prepared_ticket_canonical_epoch_recovery_journal");
        bool temporaryExists = File.Exists(candidate.TemporaryPath);

        PublicationHttpHandler handler = new(fixture, new Dictionary<string, DateTimeOffset>());
        using HttpClient http = handler.CreateClient();
        await CreateCanonicalEpochRecoverySurfaces(fixture, recovery, http)
            .PostProcessor.ReconcileSnapshotsOnStartupAsync(CancellationToken.None);

        Assert.Equal(snapshotState, fixture.DumpTables("authoring_review_snapshots"));
        Assert.Equal(journal, fixture.DumpTables("prepared_ticket_canonical_epoch_recovery_journal"));
        Assert.Equal(temporaryExists, File.Exists(candidate.TemporaryPath));
        PreparedTicketCanonicalEpochRecoveryStartResult duplicate =
            await recovery.StartAsync(source.RunId);
        Assert.True(duplicate.ExistingRun);
        Assert.Equal(runId, duplicate.Status.Run.RunId);
    }

    [Fact]
    public async Task CanonicalEpochRecovery_CasFailureRollsBackAndExplicitRetryReusesPublishedFile()
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation source = await CreateAbandonedReconciliationAsync(fixture);
        PreparedTicketCanonicalEpochRecoveryService recovery =
            CreateCanonicalEpochRecoveryService(fixture);
        string runId = (await recovery.StartAsync(source.RunId)).Status.Run.RunId;
        PreparedTicketPublicationCandidateSnapshot candidate =
            await CreateCanonicalEpochRecoveryCandidateAsync(fixture, runId);
        string sourceAudit = ReadAbandonmentAudit(fixture, source.RunId);
        fixture.Execute(
            $"""
            CREATE TRIGGER fail_recovery_completion
            BEFORE UPDATE OF Status ON authoring_runs
            WHEN NEW.Id = '{runId}' AND NEW.Status = 'completed'
            BEGIN
                SELECT RAISE(ABORT, 'synthetic final recovery CAS failure');
            END;
            """);
        PublicationHttpHandler handler = new(fixture, new Dictionary<string, DateTimeOffset>());
        using HttpClient http = handler.CreateClient();
        PreparedTicketPublicationMaintenanceController controller =
            CreateCanonicalEpochRecoverySurfaces(fixture, recovery, http).Controller;

        ConflictObjectResult refused = Assert.IsType<ConflictObjectResult>(
            await controller.RetryCanonicalEpochRecovery(runId, CancellationToken.None));
        Assert.Equal("snapshot-recovery-failure",
            Assert.IsType<PreparedTicketCanonicalEpochRecoveryFailure>(refused.Value).Error);
        await AssertCanonicalEpochRecoveryPendingAsync(fixture, recovery, runId);
        Assert.Equal("creating", fixture.Scalar<string>(
            $"SELECT Status FROM authoring_review_snapshots WHERE Id = '{candidate.SnapshotId}'"));
        Assert.Equal(0L, fixture.Scalar<long>(
            $"SELECT COUNT(*) FROM authoring_runs WHERE Id = '{runId}' AND SnapshotId IS NOT NULL"));
        Assert.True(File.Exists(candidate.FinalPath));
        Assert.False(File.Exists(candidate.TemporaryPath));
        Assert.Equal(candidate.Sha256,
            await SqliteReviewSnapshotWriter.ComputeSha256Async(candidate.FinalPath));

        fixture.Execute("DROP TRIGGER fail_recovery_completion");
        PreparedTicketCanonicalEpochRecoveryRetryResult retried =
            Assert.IsType<PreparedTicketCanonicalEpochRecoveryRetryResult>(
                Assert.IsType<OkObjectResult>(
                    await controller.RetryCanonicalEpochRecovery(runId, CancellationToken.None)).Value);
        Assert.Equal("ready", retried.Status.Recovery.State);
        Assert.Equal(candidate.SnapshotId, retried.Status.Snapshot?.SnapshotId);
        Assert.Equal(candidate.Sha256, retried.Status.Snapshot?.Sha256);
        Assert.Equal(sourceAudit, ReadAbandonmentAudit(fixture, source.RunId));
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(1L, fixture.Scalar<long>(
            "SELECT COUNT(*) FROM prepared_ticket_canonical_epoch_recovery_resolutions"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalEpochRecovery_StartupResumesAdmittedOrUnhashedReservedRun(
        bool reserveFirst)
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation source = await CreateAbandonedReconciliationAsync(fixture);
        PreparedTicketCanonicalEpochRecoveryService recovery =
            CreateCanonicalEpochRecoveryService(fixture);
        string runId = (await recovery.StartAsync(source.RunId)).Status.Run.RunId;
        string? reservedSnapshotId = null;
        if (reserveFirst)
        {
            await fixture.Store.MarkRunFinalizingAsync(runId);
            PreparerDatabase.CanonicalEpochRecoverySnapshotReservation reservation =
                await fixture.Database.ReserveCanonicalEpochRecoverySnapshotAsync(
                    runId,
                    Path.Combine(fixture.SnapshotDirectory, $"jira-fhir-{runId}.canonical-epoch-recovery.tmp"),
                    Path.Combine(fixture.SnapshotDirectory, $"jira-fhir-{runId}.db"),
                    PreparedTicketSnapshotSchemaV3.Version);
            reservedSnapshotId = reservation.SnapshotId;
            await File.WriteAllTextAsync(reservation.TemporaryPath, "unfinished untrusted backup");
        }

        PreparedTicketCanonicalEpochRecoveryService restarted =
            CreateCanonicalEpochRecoveryService(fixture);
        await restarted.RecoverPendingAsync();
        PreparedTicketCanonicalEpochRecoveryStatusResult ready =
            await restarted.GetStatusAsync(runId);
        Assert.Equal("ready", ready.Recovery.State);
        Assert.Equal(runId, ready.Snapshot?.RunId);
        if (reserveFirst)
        {
            Assert.Equal(reservedSnapshotId, ready.Snapshot?.SnapshotId);
        }
        await restarted.RecoverPendingAsync();
        Assert.Equal(
            JsonSerializer.Serialize(ready.Snapshot),
            JsonSerializer.Serialize(await restarted.RecoverAsync(runId)));
        Assert.Empty(await fixture.Database.ListPendingCanonicalEpochRecoveriesAsync());
        PreparedTicketCanonicalEpochRecoveryException duplicate =
            await Assert.ThrowsAsync<PreparedTicketCanonicalEpochRecoveryException>(
                () => restarted.StartAsync(source.RunId));
        Assert.Equal("canonical-epoch-already-recovered", duplicate.FailureCode);
        Assert.Equal(1L, fixture.Scalar<long>(
            "SELECT COUNT(*) FROM prepared_ticket_canonical_epoch_recoveries"));
    }

    [Fact]
    public async Task CanonicalEpochRecovery_CannotCompleteBeforeFinalFileVerification()
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation source = await CreateAbandonedReconciliationAsync(fixture);
        PreparedTicketCanonicalEpochRecoveryService recovery =
            CreateCanonicalEpochRecoveryService(fixture);
        string runId = (await recovery.StartAsync(source.RunId)).Status.Run.RunId;
        PreparedTicketPublicationCandidateSnapshot candidate =
            await CreateCanonicalEpochRecoveryCandidateAsync(fixture, runId);
        PreparerDatabase.CanonicalEpochRecoveryEvidence evidence = Assert.IsType<
            PreparerDatabase.CanonicalEpochRecoveryEvidence>(
                await fixture.Database.GetCanonicalEpochRecoveryEvidenceAsync(runId));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            fixture.Database.CompleteCanonicalEpochRecoveryAsync(
                candidate, PreparerDatabase.CreateCanonicalEpochRecoveryProof(evidence.Recipe)));

        Assert.Equal(0L, fixture.Scalar<long>(
            "SELECT COUNT(*) FROM prepared_ticket_canonical_epoch_recovery_resolutions"));
        Assert.Equal("creating", fixture.Scalar<string>(
            $"SELECT Status FROM authoring_review_snapshots WHERE Id = '{candidate.SnapshotId}'"));
        Assert.Equal(runId, (await fixture.Store.GetFencedRunAsync("jira-fhir"))?.Id);
    }

    [Fact]
    public async Task CanonicalEpochRecovery_MatchingFinalCompletesDespiteDisposableCandidateCleanupFailure()
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation source = await CreateAbandonedReconciliationAsync(fixture);
        PreparedTicketCanonicalEpochRecoveryService recovery =
            CreateCanonicalEpochRecoveryService(fixture);
        string runId = (await recovery.StartAsync(source.RunId)).Status.Run.RunId;
        PreparedTicketPublicationCandidateSnapshot candidate =
            await CreateCanonicalEpochRecoveryCandidateAsync(fixture, runId);
        File.Copy(candidate.TemporaryPath, candidate.FinalPath);
        using FileStream temporaryRead = new(
            candidate.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        AuthoringSnapshotDescriptor descriptor = await recovery.RecoverAsync(runId);

        Assert.Equal(candidate.SnapshotId, descriptor.SnapshotId);
        Assert.Equal(candidate.Sha256, descriptor.Sha256);
        Assert.Equal("ready", (await recovery.GetStatusAsync(runId)).Recovery.State);
        Assert.Equal("completed", (await fixture.Store.GetRunAsync(runId))?.Status);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
    }

    [Fact]
    public async Task CanonicalEpochRecovery_ControllerPreservesHistoryAndExposesLinkedRecovery()
    {
        using Fixture fixture = new(richGraph: true);
        PendingReconciliation source = await CreateAbandonedReconciliationAsync(fixture);
        PreparedTicketCanonicalEpochRecoveryService recovery =
            CreateCanonicalEpochRecoveryService(fixture);
        PreparedTicketPublicationReconciliationComparison comparison = Assert.IsType<
            PreparedTicketPublicationReconciliationComparison>(
                await fixture.Database.GetPublicationReconciliationComparisonAsync(source.RunId));
        PublicationHttpHandler handler = new(fixture, comparison.Items.ToDictionary(
            item => item.TicketKey,
            item => DateTimeOffset.Parse(item.CurrentSourceRevision, CultureInfo.InvariantCulture)));
        using HttpClient http = handler.CreateClient();
        PreparedTicketPublicationMaintenanceController controller =
            CreateCanonicalEpochRecoverySurfaces(fixture, recovery, http).Controller;
        Assert.IsType<NotFoundObjectResult>(
            await controller.StartCanonicalEpochRecovery("missing-source", CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(
            await controller.RetryCanonicalEpochRecovery("missing-recovery", CancellationToken.None));

        AcceptedResult accepted = Assert.IsType<AcceptedResult>(
            await controller.StartCanonicalEpochRecovery(source.RunId, CancellationToken.None));
        PreparedTicketCanonicalEpochRecoveryStartResult started = Assert.IsType<
            PreparedTicketCanonicalEpochRecoveryStartResult>(accepted.Value);
        string runId = started.Status.Run.RunId;
        Assert.EndsWith($"/{runId}/canonical-epoch-recovery", accepted.Location);
        PreparedTicketCanonicalEpochRecoveryStartResult duplicate = Assert.IsType<
            PreparedTicketCanonicalEpochRecoveryStartResult>(
                Assert.IsType<AcceptedResult>(
                    await controller.StartCanonicalEpochRecovery(source.RunId, CancellationToken.None)).Value);
        Assert.True(duplicate.ExistingRun);
        Assert.Equal(runId, duplicate.Status.Run.RunId);

        PreparedTicketPublicationReconciliationStatusResult historical = Assert.IsType<
            PreparedTicketPublicationReconciliationStatusResult>(
                Assert.IsType<OkObjectResult>(
                    await controller.GetReconciliationStatus(source.RunId, CancellationToken.None)).Value);
        Assert.Equal("abandoned", historical.Run.Status);
        Assert.Equal("canonical-unpublished", historical.Promotion.State);
        Assert.Equal(runId, historical.CanonicalEpochRecovery?.RunId);
        Assert.Null(historical.CanonicalEpochRecovery?.SnapshotId);
        Assert.IsType<OkObjectResult>(
            await controller.RetryCanonicalEpochRecovery(runId, CancellationToken.None));
        PreparedTicketPublicationReconciliationStatusResult resolved = Assert.IsType<
            PreparedTicketPublicationReconciliationStatusResult>(
                Assert.IsType<OkObjectResult>(
                    await controller.GetReconciliationStatus(source.RunId, CancellationToken.None)).Value);
        Assert.Equal(historical.Promotion, resolved.Promotion);
        Assert.Equal("abandoned", resolved.Run.Status);
        Assert.Equal("ready", resolved.CanonicalEpochRecovery?.State);
        Assert.NotNull(resolved.CanonicalEpochRecovery?.SnapshotId);
    }

    private sealed record Corpus(SourceResult Source, IReadOnlyDictionary<string, DateTimeOffset> Updates);
    private sealed record Publication(
        VerifiedAuthoringSnapshotPair DownloadedPair,
        VerifiedAuthoringSnapshotPair Pair,
        TicketSitePublishResult Site,
        string RendererPath);
    private sealed record TicketPreservation(
        byte[] SnapshotBytes,
        string LiveFingerprint);
    private sealed record StagedReconciliation(
        string RunId,
        PreparedTicketPublicationReconciliationComparison Comparison,
        PreparedTicketPublicationReconciliationPlanner Planner,
        string CorpusFingerprint);
    private sealed record PendingReconciliation(
        string RunId,
        PreparerDatabase.PublicationReconciliationPromotion Promotion,
        VerifiedAuthoringSnapshotPair PriorPublication);

    private sealed class CandidateGenerationAdvancingFetcher(
        IReadOnlyDictionary<string, string> revisions,
        long stableGeneration,
        string candidatePath)
        : OrchestratorHydrationFetcher(
            new HttpClient(),
            NullLogger.Instance)
    {
        private bool _generationAdvanced;

        public bool CandidateWasPresentWhenGenerationAdvanced { get; private set; }
        public List<string> RequestedTicketKeys { get; } = [];

        public override Task<PublicationMetadataFetchResult>
            FetchPublicationMetadataAsync(
                string ticketKey,
                DateTimeOffset hydratedAt,
                CancellationToken ct)
        {
            RequestedTicketKeys.Add(ticketKey);
            if (!_generationAdvanced && File.Exists(candidatePath))
            {
                _generationAdvanced = true;
                CandidateWasPresentWhenGenerationAdvanced = true;
            }
            return Task.FromResult(new PublicationMetadataFetchResult(
                ticketKey,
                hydratedAt,
                revisions[ticketKey],
                null,
                null,
                [],
                "FHIR",
                hydratedAt,
                _generationAdvanced
                    ? stableGeneration + 1
                    : stableGeneration,
                true,
                PublicDisplayNamePolicy.CurrentVersion,
                Failure: null,
                UpdatedAt: hydratedAt));
        }
    }

    private sealed class InProcessReconciliationGroupingDispatcher(
        PreparerDatabase database,
        AuthoringRunStore store,
        PreparedTicketGroupingDeltaDispatcher grouping)
        : IPreparedTicketGroupingDispatcher,
          IPreparedTicketReconciliationGroupingDispatcher
    {
        public List<PreparedTicketPublicationGroupingWorkItem> WorkItems
            { get; } = [];
        public List<AuthoringRunStageReceipt> Receipts { get; } = [];
        public List<bool> CanonicalGroupingChecks { get; } = [];
        public string? FailOnceForType { get; init; }
        private bool _failedOnce;

        public Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "Reconciliation used the ordinary canonical grouping worker path.");

        public async Task ReplaceGroupingAsync(
            PreparedTicketPublicationGroupingWorkItem workItem,
            AuthoringRunStageLease lease,
            CancellationToken ct)
        {
            WorkItems.Add(workItem);
            if (!_failedOnce &&
                string.Equals(
                    workItem.Type,
                    FailOnceForType,
                    StringComparison.Ordinal))
            {
                _failedOnce = true;
                throw new InvalidOperationException(
                    "Synthetic grouping worker interruption.");
            }
            PreparedTicketGroupingStageContext context =
                PreparedTicketGroupingDeltaDispatcher.CreateStageContext(
                    workItem,
                    lease);
            PreparedTicketClusteringSignalsController clustering = new(
                database,
                new PreparedTicketCorpusView(database),
                grouping);
            ActionResult<PreparedTicketClusteringSignalsDto>
                clusteringResult =
                    await clustering.GetReconciliationPartition(
                        workItem.WorkGroupClean,
                        workItem.Specification,
                        workItem.Type,
                        workItem.RunId,
                        lease.StageId,
                        lease.LeaseId,
                        workItem.OverlayCorpusFingerprint,
                        ct);
            PreparedTicketClusteringSignalsDto signals =
                Assert.IsType<PreparedTicketClusteringSignalsDto>(
                    Assert.IsType<OkObjectResult>(
                        clusteringResult.Result).Value);
            Assert.Equal(
                workItem.TicketKeys,
                signals.Tickets.Select(value => value.TicketKey));
            Assert.Equal(
                workItem.WorkGroupDisplay,
                signals.WorkGroupDisplay);
            foreach (string revisedTicketKey in
                     workItem.RevisedTicketKeys.Intersect(
                         workItem.TicketKeys,
                         StringComparer.OrdinalIgnoreCase))
            {
                Assert.StartsWith(
                    "Reconciled request",
                    Assert.Single(
                        signals.Tickets,
                        value => string.Equals(
                            value.TicketKey,
                            revisedTicketKey,
                            StringComparison.OrdinalIgnoreCase))
                        .RequestSummary,
                    StringComparison.Ordinal);
            }

            PreparedTicketHydrationController hydration = new(
                database,
                new PreparedTicketCorpusView(database),
                grouping);
            ActionResult<PreparedJiraHydrationListResponse>
                hydrationResult =
                    await hydration.GetReconciliationPartition(
                        workItem.WorkGroupClean,
                        workItem.Specification,
                        workItem.Type,
                        workItem.RunId,
                        lease.StageId,
                        lease.LeaseId,
                        workItem.OverlayCorpusFingerprint,
                        ct);
            PreparedJiraHydrationListResponse hydrated =
                Assert.IsType<PreparedJiraHydrationListResponse>(
                    Assert.IsType<OkObjectResult>(
                        hydrationResult.Result).Value);
            Assert.Equal(
                workItem.TicketKeys,
                hydrated.Items.Select(value => value.TicketKey));

            PreparedTicketGroupingPayload replacement =
                string.Equals(
                    workItem.Type,
                    "Change Request",
                    StringComparison.Ordinal)
                    ? CreateChangedSharedGrouping(workItem.PartitionKey)
                    : CreateEmptyGrouping(workItem.PartitionKey);
            PreparedTicketGroupingsController controller = new(
                database,
                store,
                grouping);
            ActionResult<PreparedTicketGroupingSaveResultDto> put =
                await controller.PutPartition(
                    workItem.WorkGroupClean,
                    workItem.Specification,
                    workItem.Type,
                    CreateGroupingPutRequest(replacement, context),
                    ct);
            PreparedTicketGroupingSaveResultDto saved =
                Assert.IsType<PreparedTicketGroupingSaveResultDto>(
                    Assert.IsType<OkObjectResult>(put.Result).Value);
            Receipts.Add(Assert.IsType<AuthoringRunStageReceipt>(
                saved.AuthoringReceipt));
            using SqliteConnection connection = database.OpenConnection();
            CanonicalGroupingChecks.Add(
                Scalar<long>(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM prepared_ticket_topics
                    WHERE ShortDescription =
                        'Reconciled shared container'
                    """) != 0);
        }
    }

    private static async Task<StagedReconciliation>
        StageChangedTicketReconciliationAsync(
            Fixture fixture,
            Corpus corpus,
            PublicationHttpHandler handler,
            HttpClient http,
            bool stageGroupingAndCandidate = true)
    {
        Dictionary<string, DateTimeOffset> revisedAt =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["FHIR-10028"] =
                    corpus.Updates["FHIR-10028"].AddDays(1).ToUniversalTime(),
                ["FHIR-803"] =
                    corpus.Updates["FHIR-803"].AddDays(1).ToUniversalTime(),
            };
        foreach ((string key, DateTimeOffset revision) in revisedAt)
        {
            ItemResponse current = handler.JiraItems[key];
            handler.JiraItems[key] = current with { UpdatedAt = revision };
            fixture.Execute(
                """
                UPDATE jira_processing_source_tickets
                SET LastUpdated = @revision
                WHERE Key = @key
                """,
                ("@revision",
                    revision.ToString("O", CultureInfo.InvariantCulture)),
                ("@key", key));
        }

        JiraAuthoringRunCoordinator coordinator =
            CreateCoordinator(fixture);
        AuthoringRetryPolicy retryPolicy = new(
            Options.Create(new ProcessingServiceOptions()));
        PreparedTicketPublicationReconciliationPlanner planner = new(
            fixture.CreateBaselineReader(),
            new OrchestratorHydrationFetcher(http, NullLogger.Instance),
            fixture.Database,
            new AuthoringRunControlService(fixture.Store, retryPolicy),
            coordinator,
            new AuthoringRunSchedulerWakeSignal());

        PreparedTicketPublicationReconciliationStartResult start =
            await planner.StartAsync(corpus.Source.Run.Id);

        Assert.Equal(8, start.Counts.AcceptedTicketCount);
        Assert.Equal(6, start.Counts.CarryForwardTicketCount);
        Assert.Equal(2, start.Counts.ReAuthorTicketCount);
        Assert.Equal("901", start.Comparison.StableJiraGeneration);
        Assert.Equal(
            RevisedKeys.Order(StringComparer.Ordinal),
            start.Comparison.Items
                .Where(item =>
                    item.Disposition ==
                    PreparedTicketPublicationReconciliationDispositionValues
                        .ReAuthor)
                .Select(item => item.TicketKey)
                .Order(StringComparer.Ordinal));
        Assert.All(
            start.Comparison.Items.Where(item =>
                !RevisedKeys.Contains(
                    item.TicketKey,
                    StringComparer.OrdinalIgnoreCase)),
            item => Assert.Equal(
                PreparedTicketPublicationReconciliationDispositionValues
                    .CarryForward,
                item.Disposition));
        Assert.Equal(
            2,
            handler.JiraRequests
                .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Count())
                .Distinct()
                .Single());
        Assert.Equal(
            FhirKeys.Concat(CdaKeys).Order(StringComparer.Ordinal),
            handler.JiraRequests
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal));

        foreach (string key in RevisedKeys)
        {
            AuthoringRunItemRecord item = Assert.Single(
                await fixture.Store.GetRunItemsAsync(start.Run.RunId),
                candidate => string.Equals(
                    candidate.BusinessKey,
                    key,
                    StringComparison.OrdinalIgnoreCase));
            AuthoringOperationClaim claim =
                Assert.IsType<AuthoringOperationClaim>(
                    await fixture.Store.ClaimItemAsync(
                        start.Run.RunId,
                        item.Id));
            PreparedTicketPayload payload =
                CreateRevisedPayload(key, revisedAt[key]);
            string contentHash =
                PreparedTicketAuthoringDtos.ComputeContentHash(payload);
            HydrationBatch hydration = CreateRevisedHydration(
                key,
                revisedAt[key],
                key == "FHIR-803"
                    ? MovedTicketType
                    : "Change Request");
            AuthoringReceiptAcceptance acceptance =
                await fixture.Store.AcceptResultWithReceiptAsync(
                    new(
                        start.Run.RunId,
                        item.Id,
                        claim.OperationId,
                        item.ExpectedSourceRevision,
                        contentHash),
                    claim.OperationToken,
                    async (connection, receiptId, ct) =>
                    {
                        await JiraProcessingSourceTicketStore
                            .EnsureCurrentSourceRevisionAsync(
                                connection,
                                key,
                                item.ItemKind,
                                item.ExpectedSourceRevision,
                                ct);
                        await fixture.Database
                            .StagePublicationReconciliationTicketAsync(
                                connection,
                                start.Run.RunId,
                                item.Id,
                                claim.OperationId,
                                receiptId,
                                item.ExpectedSourceRevision,
                                contentHash,
                                payload,
                                hydration,
                                ct: ct);
                    });
            Assert.False(acceptance.IsReplay);
            await fixture.Store.MarkItemCompleteAsync(
                item.Id,
                acceptance.Receipt.ReceiptId);
        }

        PreparedTicketPublicationReconciliationStatusResult authored =
            await planner.GetStatusAsync(start.Run.RunId);
        Assert.Empty(authored.InvalidatedTicketKeys);
        Assert.All(
            authored.Items,
            item => Assert.Equal(
                AuthoringStatusValues.Items.Complete,
                item.Status));
        Assert.Equal(
            $"Request {RevisedKeys[0]}\n\nExact authored text: caf\u00e9.",
            fixture.Scalar<string>(
                $"SELECT RequestSummary FROM prepared_tickets WHERE Key = '{RevisedKeys[0]}'"));

        PreparedTicketGroupingDeltaDispatcher grouping = new(
            fixture.Database);
        PreparedTicketPublicationGroupingDelta delta =
            await grouping.PrepareAsync(start.Run.RunId);
        PreparedTicketPublicationCorpusOverlay overlay =
            await fixture.Database.GetPublicationReconciliationCorpusAsync(
                start.Run.RunId);
        Assert.Equal(
            PreparedTicketPublicationContract.ComputeCorpusFingerprint(
                overlay.Tickets.Select(
                    ticket => ticket.ToPublicationCorpusItem())),
            overlay.CorpusFingerprint);
        Assert.Equal(
            overlay.CorpusFingerprint,
            delta.OverlayCorpusFingerprint);
        Assert.Equal(2, delta.Impacts.Count);
        PreparedTicketPublicationReconciliationGroupingImpact oldPartition =
            Assert.Single(
                delta.Impacts,
                impact => impact.PartitionKey.EndsWith(
                    "\u001fFHIR\u001fChange Request",
                    StringComparison.Ordinal));
        PreparedTicketPublicationReconciliationGroupingImpact newPartition =
            Assert.Single(
                delta.Impacts,
                impact => impact.PartitionKey.EndsWith(
                    $"\u001fFHIR\u001f{MovedTicketType}",
                    StringComparison.Ordinal));
        Assert.Equal(
            RevisedKeys.Order(StringComparer.Ordinal),
            oldPartition.RevisedTicketKeys.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["FHIR-803"],
            newPartition.RevisedTicketKeys);

        if (!stageGroupingAndCandidate)
        {
            return new(
                start.Run.RunId,
                start.Comparison,
                planner,
                delta.OverlayCorpusFingerprint);
        }

        PreparedTicketGroupingsController groupingController = new(
            fixture.Database,
            fixture.Store,
            grouping);
        foreach (PreparedTicketPublicationReconciliationGroupingImpact impact
                 in delta.Impacts)
        {
            AuthoringRunStageRecord stage =
                await fixture.Store.EnsureRunStageAsync(
                    start.Run.RunId,
                    PreparerDatabase
                        .PublicationReconciliationGroupingStageName,
                    impact.PartitionKey,
                    delta.OverlayCorpusFingerprint);
            AuthoringRunStageLease lease =
                Assert.IsType<AuthoringRunStageLease>(
                    await fixture.Store.TryStartRunStageAsync(stage.Id));
            PreparedTicketPublicationGroupingWorkItem workItem =
                await grouping.GetStageWorkItemAsync(
                    start.Run.RunId,
                    stage.Id,
                    lease.LeaseId,
                    impact.PartitionKey,
                    delta.OverlayCorpusFingerprint);
            PreparedTicketGroupingPayload replacement =
                string.Equals(
                    impact.PartitionKey,
                    oldPartition.PartitionKey,
                    StringComparison.Ordinal)
                    ? CreateChangedSharedGrouping(impact.PartitionKey)
                    : CreateEmptyGrouping(impact.PartitionKey);
            ActionResult<PreparedTicketGroupingSaveResultDto> put =
                await groupingController.PutPartition(
                    workItem.WorkGroupClean,
                    workItem.Specification,
                    workItem.Type,
                    CreateGroupingPutRequest(
                        replacement,
                        PreparedTicketGroupingDeltaDispatcher
                            .CreateStageContext(workItem, lease)),
                    CancellationToken.None);
            PreparedTicketGroupingSaveResultDto saved =
                Assert.IsType<PreparedTicketGroupingSaveResultDto>(
                    Assert.IsType<OkObjectResult>(put.Result).Value);
            Assert.Equal(stage.Id, saved.AuthoringReceipt?.StageId);
            await fixture.Store.CompleteRunStageAsync(
                stage.Id,
                lease.LeaseId);
        }
        PreparedTicketPublicationGroupingDelta complete =
            await grouping.PrepareAsync(start.Run.RunId);
        Assert.All(complete.Impacts, impact => Assert.True(impact.Complete));
        Assert.Equal(
            delta.Unaffected.CombinedFingerprint,
            complete.Unaffected.CombinedFingerprint);
        await fixture.Database.ValidatePublicationReconciliationUnaffectedAsync(
            start.Run.RunId);

        Assert.Equal(
            AuthoringStatusValues.Runs.Running,
            fixture.Scalar<string>(
                $"""
                SELECT Status
                FROM authoring_runs
                WHERE Id = '{start.Run.RunId}'
                """));
        await fixture.Store.MarkRunFinalizingAsync(start.Run.RunId);
        Assert.Equal(
            AuthoringStatusValues.Runs.Finalizing,
            fixture.Scalar<string>(
                $"""
                SELECT Status
                FROM authoring_runs
                WHERE Id = '{start.Run.RunId}'
                """));
        await fixture.Database.ValidatePublicationReconciliationUnaffectedAsync(
            start.Run.RunId);
        PreparedTicketPublicationGroupingDelta finalizingDelta =
            await grouping.PrepareAsync(start.Run.RunId);
        Assert.Equal(
            complete.Unaffected.ImpactedPartitionKeys,
            finalizingDelta.Unaffected.ImpactedPartitionKeys);
        Assert.Equal(
            complete.Unaffected.AuthoredRowsFingerprint,
            finalizingDelta.Unaffected.AuthoredRowsFingerprint);
        Assert.Equal(
            complete.Unaffected.ReceiptCoordinatesFingerprint,
            finalizingDelta.Unaffected.ReceiptCoordinatesFingerprint);
        Assert.Equal(
            complete.Unaffected.GroupingRowsFingerprint,
            finalizingDelta.Unaffected.GroupingRowsFingerprint);
        Assert.Equal(
            complete.Unaffected.CombinedFingerprint,
            finalizingDelta.Unaffected.CombinedFingerprint);
        PreparedTicketPublicationReconciliationProof proof =
            await grouping.CreateProofAsync(start.Run.RunId);
        Assert.Equal(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            proof.ContractVersion);
        Assert.Equal(delta.OverlayCorpusFingerprint, proof.CorpusFingerprint);
        Assert.NotEmpty(finalizingDelta.UnaffectedGroupingPartitions);
        Assert.Equal(
            PreparedTicketPublicationContract.ComputeReconciliationGroupingFingerprint(
                finalizingDelta.UnaffectedGroupingPartitions,
                finalizingDelta.Impacts),
            proof.GroupingFingerprint);
        Assert.Equal(
            PreparedTicketPublicationContract.ComputeGroupingImpactFingerprint(
                finalizingDelta.Impacts),
            proof.GroupingImpactFingerprint);
        Assert.NotEqual(proof.GroupingImpactFingerprint, proof.GroupingFingerprint);
        PreparedTicketSnapshotMaterializer materializer = new(
            fixture.Database,
            fixture.Store,
            new SqliteReviewSnapshotReconciler(fixture.Store),
            Options.Create(new PreparerServiceOptions
            {
                SnapshotDirectory = fixture.SnapshotDirectory,
                SnapshotSchemaVersion =
                    PreparedTicketSnapshotSchemaV3.Version,
            }));
        AuthoringRunRecord finalizing = Assert.IsType<AuthoringRunRecord>(
            await fixture.Store.GetRunAsync(start.Run.RunId));
        PreparedTicketPublicationCandidateSnapshot candidate =
            await materializer.MaterializeReconciliationCandidateAsync(
                finalizing,
                proof);
        Assert.True(File.Exists(candidate.TemporaryPath));
        Assert.Equal(
            PreparedTicketSnapshotSchemaV3.Version,
            candidate.SchemaVersion);
        Assert.Equal(
            delta.OverlayCorpusFingerprint,
            candidate.OverlayCorpusFingerprint);
        Assert.Equal(proof.GroupingFingerprint, candidate.GroupingFingerprint);
        Assert.Equal(proof.GroupingImpactFingerprint, candidate.GroupingImpactFingerprint);
        await using (SqliteConnection snapshot =
                     await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(
                         candidate.TemporaryPath))
        {
            PreparedTicketPublicationProtectedInventory inventory =
                await PreparedTicketPublicationProtectionReader.ReadSnapshotAsync(
                    snapshot,
                    candidate.SchemaVersion);
            Assert.Equal(proof.CorpusFingerprint, inventory.CorpusFingerprint);
            Assert.Equal(
                proof.GroupingFingerprint,
                PreparedTicketPublicationContract.ComputeGroupingFingerprint(
                    inventory.Grouping.Select(partition =>
                        new PreparedTicketPublicationGroupingPartition(
                            partition.PartitionKey,
                            partition.Fingerprint.OutputFingerprint))));
        }
        Assert.Equal(
            JsonSerializer.Serialize(candidate),
            fixture.Scalar<string>(
                $"""
                SELECT SnapshotDescriptorJson
                FROM prepared_ticket_publication_reconciliation_journal
                WHERE RunId = '{start.Run.RunId}'
                """));
        Assert.Equal(
            candidate.Sha256,
            fixture.Scalar<string>(
                $"""
                SELECT Sha256
                FROM prepared_ticket_publication_snapshot_descriptors
                WHERE RunId = '{start.Run.RunId}'
                """));
        Assert.Equal(
            0,
            fixture.Scalar<long>(
                $"""
                SELECT COUNT(*)
                FROM authoring_review_snapshots
                WHERE RunId = '{start.Run.RunId}'
                """));
        Assert.Equal(
            PreparedTicketPublicationContract.PublicationReconciliationPurpose,
            proof.Purpose);

        return new(
            start.Run.RunId,
            start.Comparison,
            planner,
            delta.OverlayCorpusFingerprint);
    }

    private static JiraAuthoringRunCoordinator CreateCoordinator(
        Fixture fixture)
        => new(
            fixture.Store,
            new JiraProcessingSourceTicketStore(
                fixture.Database.DatabasePath),
            new JiraProcessingFilterResolver(),
            Options.Create(new JiraProcessingOptions
            {
                AgentCliCommand = "agent {ticketKey}",
                JiraSourceAddress = "http://source",
                SourceTicketShape = "fhir",
                TicketStatusesToProcess = ["Triaged"],
            }));

    private static PreparedTicketPayload CreateRevisedPayload(
        string key,
        DateTimeOffset savedAt)
        => new()
        {
            Key = key,
            RequestSummary = $"Reconciled request {key}",
            CommentSummary = $"Reconciled comments {key}",
            LinkedTicketSummary = $"Reconciled linked tickets {key}",
            RelatedTicketSummary = $"Reconciled related tickets {key}",
            RelatedZulipSummary = $"Reconciled Zulip {key}",
            RelatedGitHubSummary = $"Reconciled GitHub {key}",
            ExistingProposed = $"Reconciled existing proposal {key}",
            ProposalA = $"Reconciled A {key}",
            ProposalAJustification = $"Reconciled reason A {key}",
            ProposalAImpact = PreparedTicketImpactValues.NonSubstantive,
            ProposalB = $"Reconciled B {key}",
            ProposalBJustification = $"Reconciled reason B {key}",
            ProposalBImpact = PreparedTicketImpactValues.NonSubstantive,
            ProposalC = $"Reconciled C {key}",
            ProposalCJustification = $"Reconciled reason C {key}",
            Recommendation =
                PreparedTicketRecommendationValues.ProposalA,
            RecommendationJustification =
                $"Reconciled recommendation {key}",
            SavedAt = savedAt,
        };

    private static HydrationBatch CreateRevisedHydration(
        string key,
        DateTimeOffset updatedAt,
        string type)
    {
        DateTimeOffset hydratedAt =
            new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        return new(
            key,
            new HydrationTicketRow(
                key,
                "Major",
                "Persuasive",
                $"Reconciled resolution {key}",
                "FHIR",
                "R6",
                "2026-09",
                "Correction",
                "Non-substantive",
                "reconciled",
                2,
                $"Reconciled description {key}",
                hydratedAt,
                "resolved",
                null,
                DescriptionHtml: $"<p>Reconciled description {key}</p>",
                ResolutionDescriptionHtml:
                    $"<p>Reconciled resolution {key}</p>",
                CreatedAt: updatedAt.AddYears(-1),
                Assignee: $"Assignee {key}",
                InPersonRequesters: [$"Requester {key}"],
                SourceProject: "FHIR",
                SourceLastSuccessfulRefreshAt:
                    PublicationHttpHandler.SourceRefreshedAt,
                SourceContentRevision: 901,
                SourceIsStable: true,
                StructuredReporter: $"Reporter {key}",
                PublicDisplayNamePolicyVersion:
                    PublicDisplayNamePolicy.CurrentVersion),
            [
                new HydrationJiraRow(
                    key,
                    key,
                    $"Reconciled title {key}",
                    "Triaged",
                    type,
                    "Major",
                    "Persuasive",
                    $"Reconciled resolution {key}",
                    "FHIR Infrastructure",
                    "FHIR",
                    updatedAt,
                    $"https://jira/{key}",
                    hydratedAt,
                    "resolved",
                    null,
                    DescriptionHtml:
                        $"<p>Reconciled Jira description {key}</p>",
                    ResolutionDescriptionHtml:
                        $"<p>Reconciled Jira resolution {key}</p>",
                    CreatedAt: updatedAt.AddYears(-1),
                    Assignee: $"Assignee {key}",
                    StructuredReporter: $"Reporter {key}",
                    PublicDisplayNamePolicyVersion:
                        PublicDisplayNamePolicy.CurrentVersion),
            ],
            [],
            [],
            [],
            []);
    }

    private static PreparedTicketGroupingPayload CreateChangedSharedGrouping(
        string partitionKey)
    {
        string[] coordinates = ParsePartitionKey(partitionKey);
        return new()
        {
            WorkGroupClean = coordinates[0],
            WorkGroupDisplay = "FHIR Infrastructure",
            Specification = coordinates[1],
            Type = coordinates[2],
            SavedAt =
                new DateTimeOffset(
                    2026, 9, 17, 10, 30, 0, TimeSpan.Zero),
            Topics =
            [
                new()
                {
                    ShortDescription =
                        "Reconciled shared container",
                    LongerDescription =
                        "Changed text for the shared discussion container.",
                    RenderOrderHint = 0,
                    LinkedTicketGroups =
                    [
                        new()
                        {
                            FirstTicketKey = "FHIR-29212",
                            Rationale =
                                "Reconciled order keeps unchanged authored content fixed.",
                            Members =
                            [
                                new()
                                {
                                    TicketKey = "FHIR-29212",
                                    Order = 0,
                                },
                                new()
                                {
                                    TicketKey = "FHIR-10028",
                                    Order = 1,
                                },
                            ],
                        },
                    ],
                    RemainingTicketKeys =
                        ["FHIR-806", "FHIR-805", "FHIR-804"],
                },
            ],
        };
    }

    private static PreparedTicketGroupingPayload CreateEmptyGrouping(
        string partitionKey)
    {
        string[] coordinates = ParsePartitionKey(partitionKey);
        return new()
        {
            WorkGroupClean = coordinates[0],
            WorkGroupDisplay = "FHIR Infrastructure",
            Specification = coordinates[1],
            Type = coordinates[2],
            SavedAt =
                new DateTimeOffset(
                    2026, 9, 17, 10, 30, 0, TimeSpan.Zero),
            Topics = [],
        };
    }

    private static PreparedTicketGroupingPutRequest CreateGroupingPutRequest(
        PreparedTicketGroupingPayload payload,
        PreparedTicketGroupingStageContext context)
        => new(
            payload.WorkGroupDisplay,
            payload.Topics.Select(topic =>
                new PreparedTicketGroupingTopicRequest(
                    topic.ShortDescription,
                    topic.LongerDescription,
                    topic.RenderOrderHint,
                    topic.LinkedTicketGroups.Select(group =>
                        new PreparedTicketGroupingLinkedGroupRequest(
                            group.FirstTicketKey,
                            group.Rationale,
                            group.Members.Select(member =>
                                new PreparedTicketGroupingMemberRequest(
                                    member.TicketKey,
                                    member.Order))
                                .ToArray()))
                        .ToArray(),
                    topic.RemainingTicketKeys.ToArray()))
                .ToArray(),
            context);

    private static string[] ParsePartitionKey(string partitionKey)
    {
        string[] coordinates = partitionKey.Split('\u001f');
        Assert.Equal(3, coordinates.Length);
        return coordinates;
    }

    private static async Task<Dictionary<string, TicketPreservation>>
        CaptureTicketPreservationAsync(
            Fixture fixture,
            Publication original,
            IEnumerable<string> keys)
    {
        Dictionary<string, TicketPreservation> result =
            new(StringComparer.OrdinalIgnoreCase);
        using SqliteConnection snapshot =
            await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(
                original.Pair.DatabasePath);
        foreach (string key in keys)
        {
            result.Add(
                key,
                new(
                    ReadTicketSnapshotBytes(snapshot, key),
                    ReadLiveTicketFingerprint(fixture, key)));
        }
        return result;
    }

    private static byte[] ReadTicketSnapshotBytes(
        SqliteConnection connection,
        string key)
    {
        (string Table, string KeyColumn)[] tables =
        [
            ("prepared_tickets", "Key"),
            ("prepared_ticket_repos", "TicketKey"),
            ("prepared_ticket_related_jira", "TicketKey"),
            ("prepared_ticket_related_zulip", "TicketKey"),
            ("prepared_ticket_related_github", "TicketKey"),
            ("prepared_ticket_hydration", "TicketKey"),
            ("prepared_jira_hydration", "TicketKey"),
            ("prepared_zulip_hydration", "TicketKey"),
            ("prepared_github_hydration", "TicketKey"),
            ("prepared_repo_hydration", "TicketKey"),
            ("prepared_ticket_jira_xref", "TicketKey"),
            ("prepared_ticket_jira_content", "TicketKey"),
            ("prepared_ticket_artifacts", "TicketKey"),
            ("prepared_ticket_pages", "TicketKey"),
            ("prepared_ticket_in_person_requesters", "TicketKey"),
        ];
        StringBuilder bytes = new();
        foreach ((string table, string keyColumn) in tables)
        {
            bytes.Append(table).Append('\n');
            foreach (string row in ReadRows(
                         connection,
                         $"SELECT * FROM {table} WHERE {keyColumn} = @key COLLATE NOCASE ORDER BY RowId",
                         ("@key", key)))
            {
                bytes.Append(row).Append('\n');
            }
        }
        return Encoding.UTF8.GetBytes(bytes.ToString());
    }

    private static string ReadLiveTicketFingerprint(
        Fixture fixture,
        string key)
    {
        using SqliteConnection connection = fixture.Database.OpenConnection();
        return Assert.Single(
            ReadRows(
                connection,
                """
                SELECT state.GraphHash, state.ReceiptContentHash,
                       state.RunId, state.RunItemId, state.OperationId,
                       item.AcceptedReceiptId, receipt.ContentHash,
                       receipt.ExpectedSourceRevision,
                       receipt.ObservedSourceRevision
                FROM prepared_ticket_authoring_state state
                INNER JOIN authoring_run_items item
                  ON item.RunId = state.RunId
                 AND item.Id = state.RunItemId
                INNER JOIN authoring_result_receipts receipt
                  ON receipt.Id = item.AcceptedReceiptId
                 AND receipt.RunId = item.RunId
                 AND receipt.RunItemId = item.Id
                WHERE state.TicketKey = @key COLLATE NOCASE
                """,
                ("@key", key)));
    }

    private static string ReadCorpusFingerprint(
        SqliteConnection connection)
    {
        List<PreparedTicketPublicationCorpusItem> corpus = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ticket.Key, receipt.Id, item.Id, item.RunId,
                   item.ItemKind, item.ExpectedSourceRevision
            FROM prepared_tickets ticket
            INNER JOIN authoring_run_items item
                ON item.BusinessKey = ticket.Key COLLATE NOCASE
               AND item.AcceptedReceiptId IS NOT NULL
               AND item.Status IN ('complete', 'superseded')
            INNER JOIN authoring_result_receipts receipt
                ON receipt.Id = item.AcceptedReceiptId
               AND receipt.RunId = item.RunId
               AND receipt.RunItemId = item.Id
               AND receipt.BusinessKey = item.BusinessKey COLLATE NOCASE
               AND receipt.ExpectedSourceRevision =
                   item.ExpectedSourceRevision
               AND receipt.ObservedSourceRevision =
                   receipt.ExpectedSourceRevision
            INNER JOIN authoring_runs run
                ON run.Id = item.RunId
               AND run.ProcessorKind = 'jira-fhir'
               AND run.AuthoringEpoch = receipt.AuthoringEpoch
            ORDER BY ticket.Key COLLATE NOCASE, ticket.Key
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            corpus.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }
        return PreparedTicketPublicationContract.ComputeCorpusFingerprint(
            corpus);
    }

    private static byte[] ReadGroupingBytes(
        SqliteConnection connection,
        string specification)
        => Encoding.UTF8.GetBytes(string.Join(
            "\n",
            ReadRows(
                connection,
                """
                SELECT t.*, g.*, m.*
                FROM prepared_ticket_topics t
                LEFT JOIN prepared_ticket_topic_groups g
                  ON g.TopicRowId = t.RowId
                LEFT JOIN prepared_ticket_topic_members m
                  ON m.TopicRowId = t.RowId
                 AND (m.TopicGroupRowId = g.RowId OR
                      m.TopicGroupRowId IS NULL AND g.RowId IS NULL)
                WHERE t.Specification = @specification
                ORDER BY t.RowId, g.RowId, m.RowId
                """,
                ("@specification", specification))));

    private static async Task<PendingReconciliation>
        CreatePendingReconciliationAsync(Fixture fixture)
    {
        Corpus corpus = await CreateCorpusAsync(fixture);
        PublicationHttpHandler handler = new(fixture, corpus.Updates);
        using HttpClient http = handler.CreateClient();
        AuthoringControlClient client = new(http);
        Publication original = await PublishAsync(
            fixture,
            client,
            corpus.Source.Descriptor);
        StagedReconciliation staged =
            await StageChangedTicketReconciliationAsync(
                fixture,
                corpus,
                handler,
                http);
        PreparerDatabase.PublicationReconciliationPromotion promotion =
            await fixture.Database.PromotePublicationReconciliationAsync(
                staged.RunId,
                Path.Combine(
                    fixture.SnapshotDirectory,
                    $"jira-fhir-{staged.RunId}.db"));
        PreparedTicketPublicationCandidateSnapshot candidate =
            JsonSerializer.Deserialize<
                PreparedTicketPublicationCandidateSnapshot>(
                    fixture.Scalar<string>(
                        $"""
                        SELECT DescriptorJson
                        FROM prepared_ticket_publication_snapshot_descriptors
                        WHERE RunId = '{staged.RunId}'
                        """))
            ?? throw new InvalidOperationException(
                "The pending reconciliation candidate descriptor is invalid.");
        Assert.Equal(promotion.SnapshotId, candidate.SnapshotId);
        Assert.Equal(promotion.Sequence, candidate.Sequence);
        Assert.Equal(promotion.AuthoringEpoch, candidate.AuthoringEpoch);
        Assert.Equal(promotion.ItemCount, candidate.ItemCount);
        Assert.Equal(promotion.ReceiptCount, candidate.ReceiptCount);
        Assert.Equal(promotion.SchemaVersion, candidate.SchemaVersion);
        Assert.Equal(promotion.FinalPath, candidate.FinalPath);
        Assert.Equal(promotion.CreatedAt, candidate.CreatedAt);
        Assert.Equal(promotion.CandidateSha256, candidate.Sha256);
        Assert.Equal(promotion.CandidateSizeBytes, candidate.SizeBytes);
        Assert.Equal(
            promotion.CandidateSha256,
            fixture.Scalar<string>(
                $"""
                SELECT ChecksumSha256
                FROM authoring_review_snapshots
                WHERE Id = '{promotion.SnapshotId}'
                """));
        Assert.Equal(
            promotion.SnapshotId,
            await ReadSnapshotProvenanceIdAsync(
                promotion.TemporaryPath));
        Assert.Equal(
            promotion.CandidateSha256,
            await SqliteReviewSnapshotWriter.ComputeSha256Async(
                promotion.TemporaryPath));
        return new(staged.RunId, promotion, original.Pair);
    }

    private static PreparedTicketPublicationRecoveryService
        CreateRecoveryService(Fixture fixture)
        => new(
            fixture.Database,
            fixture.Store,
            NullLogger<PreparedTicketPublicationRecoveryService>.Instance);

    private static async Task<PreparedTicketPublicationCandidateSnapshot>
        CreateCanonicalEpochRecoveryCandidateAsync(Fixture fixture, string runId)
    {
        await fixture.Store.MarkRunFinalizingAsync(runId);
        PreparerDatabase.CanonicalEpochRecoveryEvidence evidence =
            Assert.IsType<PreparerDatabase.CanonicalEpochRecoveryEvidence>(
                await fixture.Database.GetCanonicalEpochRecoveryEvidenceAsync(runId));
        return await CreateCanonicalEpochRecoveryMaterializer(fixture)
            .MaterializeCanonicalEpochRecoveryCandidateAsync(
                Assert.IsType<AuthoringRunRecord>(await fixture.Store.GetRunAsync(runId)),
                evidence.Recipe,
                PreparerDatabase.CreateCanonicalEpochRecoveryProof(evidence.Recipe));
    }

    private static async Task AssertCanonicalEpochRecoveryPendingAsync(
        Fixture fixture,
        PreparedTicketCanonicalEpochRecoveryService recovery,
        string runId)
    {
        PreparedTicketCanonicalEpochRecoveryStatusResult status =
            await recovery.GetStatusAsync(runId);
        Assert.Equal("error", status.Run.Status);
        Assert.Equal("snapshot-publish-pending", status.Recovery.State);
        Assert.True(status.Recovery.MutationFenceHeld);
        Assert.Null(status.Snapshot);
        Assert.Null(status.PublicationProof);
        Assert.Equal(0L, fixture.Scalar<long>(
            "SELECT COUNT(*) FROM prepared_ticket_canonical_epoch_recovery_resolutions"));
        await AssertCanonicalUnpublishedRestrictionAsync(() =>
            fixture.Database.EnsureSnapshotWorkflowAllowedAsync(
                new("jira-fhir", false, AuthoringRunPurposeValues.Authoring)));
    }

    private static (
        PreparedTicketRunPostProcessor PostProcessor,
        PreparedTicketPublicationMaintenanceController Controller)
        CreateCanonicalEpochRecoverySurfaces(
            Fixture fixture,
            PreparedTicketCanonicalEpochRecoveryService recovery,
            HttpClient http)
    {
        IOptions<PreparerServiceOptions> options = Options.Create(new PreparerServiceOptions
        {
            SnapshotDirectory = fixture.SnapshotDirectory,
            SnapshotSchemaVersion = PreparedTicketSnapshotSchemaV3.Version,
        });
        JiraAuthoringRunCoordinator coordinator = CreateCoordinator(fixture);
        OrchestratorHydrationFetcher fetcher = new(http, NullLogger.Instance);
        PreparedTicketPublicationReconciliationPlanner planner = new(
            fixture.CreateBaselineReader(),
            fetcher,
            fixture.Database,
            new AuthoringRunControlService(fixture.Store,
                new AuthoringRetryPolicy(Options.Create(new ProcessingServiceOptions()))),
            coordinator,
            new AuthoringRunSchedulerWakeSignal());
        PreparedTicketSnapshotMaterializer materializer =
            CreateCanonicalEpochRecoveryMaterializer(fixture);
        PreparedTicketPublicationRecoveryService reconciliationRecovery =
            CreateRecoveryService(fixture);
        PreparedTicketRunWorkflowRegistry workflows = new(
            fixture.Store,
            fixture.Database,
            fetcher,
            planner,
            new PreparedTicketGroupingDeltaDispatcher(fixture.Database),
            materializer,
            reconciliationRecovery,
            options,
            NullLogger<PreparedTicketRunWorkflowRegistry>.Instance,
            canonicalEpochRecoveryService: recovery);
        return (
            new(
                fixture.Database,
                fixture.Store,
                new AuthoringRunFinalizer(fixture.Store),
                new SqliteReviewSnapshotReconciler(fixture.Store),
                coordinator,
                new OrchestratorWorkGroupCatalogFetcher(http),
                new UnconfiguredPreparedTicketGroupingDispatcher(),
                options,
                snapshotMaterializer: materializer,
                workflowRegistry: workflows),
            new(
                fixture.CreateRefreshService(fetcher),
                planner,
                reconciliationRecovery,
                recovery,
                fixture.Database));
    }

    private static PreparedTicketCanonicalEpochRecoveryService
        CreateCanonicalEpochRecoveryService(Fixture fixture)
    {
        PreparedTicketSnapshotMaterializer materializer =
            CreateCanonicalEpochRecoveryMaterializer(fixture);
        AuthoringRunControlService runControl = new(
            fixture.Store,
            new AuthoringRetryPolicy(
                Options.Create(new ProcessingServiceOptions())));
        return new(
            fixture.Database,
            fixture.Store,
            runControl,
            materializer,
            new AuthoringRunSchedulerWakeSignal(),
            NullLogger<
                PreparedTicketCanonicalEpochRecoveryService>.Instance);
    }

    private static PreparedTicketSnapshotMaterializer
        CreateCanonicalEpochRecoveryMaterializer(Fixture fixture)
        => new(
            fixture.Database,
            fixture.Store,
            new SqliteReviewSnapshotReconciler(fixture.Store),
            Options.Create(new PreparerServiceOptions
            {
                SnapshotDirectory = fixture.SnapshotDirectory,
                SnapshotSchemaVersion =
                    PreparedTicketSnapshotSchemaV3.Version,
            }));

    private static async Task<PendingReconciliation>
        CreateAbandonedReconciliationAsync(Fixture fixture)
    {
        PendingReconciliation pending =
            await CreatePendingReconciliationAsync(fixture);
        await fixture.Database.AbandonPublicationReconciliationAsync(
            pending.RunId,
            "snapshot publication was abandoned",
            new DateTimeOffset(
                2026,
                9,
                17,
                14,
                0,
                0,
                TimeSpan.Zero));
        return pending;
    }

    private static async Task MutateSnapshotAsync(
        string path,
        string sql)
    {
        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        await using (SqliteCommand checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> ReadSnapshotProvenanceIdAsync(
        string path)
    {
        await using SqliteConnection connection =
            await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(path);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SnapshotId
            FROM authoring_snapshot_provenance
            """;
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<long> ReadSnapshotProvenanceCountAsync(
        string path)
    {
        await using SqliteConnection connection =
            await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(path);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM authoring_snapshot_provenance";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteDatabaseAsync(
        Fixture fixture,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteConnection connection =
            fixture.Database.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static Task DeleteReconciliationStagingAsync(
        Fixture fixture,
        string runId)
        => ExecuteDatabaseAsync(
            fixture,
            """
            DELETE FROM prepared_ticket_publication_staged_graphs
            WHERE RunId = @runId;
            DELETE FROM prepared_ticket_publication_staged_hydration
            WHERE RunId = @runId;
            DELETE FROM prepared_ticket_publication_staged_receipts
            WHERE RunId = @runId;
            DELETE FROM prepared_ticket_publication_grouping_impacts
            WHERE RunId = @runId;
            DELETE FROM prepared_ticket_publication_staged_grouping
            WHERE RunId = @runId;
            DELETE FROM prepared_ticket_publication_grouping_stage_receipts
            WHERE RunId = @runId;
            """,
            ("@runId", runId));

    private static async Task AssertRecoveryFailureRetainsFenceAsync(
        Fixture fixture,
        string runId)
    {
        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues
                .SnapshotPublishPending,
            fixture.Scalar<string>(
                $"""
                SELECT PromotionState
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = '{runId}'
                """));
        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues
                .SnapshotPublishPending,
            fixture.Scalar<string>(
                $"""
                SELECT State
                FROM prepared_ticket_publication_reconciliation_journal
                WHERE RunId = '{runId}'
                """));
        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .PromotionRecoveryFailure,
            fixture.Scalar<string>(
                $"""
                SELECT FailureCode
                FROM prepared_ticket_publication_reconciliation_journal
                WHERE RunId = '{runId}'
                """));
        Assert.NotNull(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(
            1,
            fixture.Scalar<long>(
                $"""
                SELECT COUNT(*)
                FROM prepared_ticket_publication_reconciliation_fences
                WHERE RunId = '{runId}'
                """));
        Assert.Contains(
            runId,
            await fixture.Database
                .ListPendingPublicationReconciliationsAsync());
    }

    private static async Task AssertRecoverySucceededAsync(
        Fixture fixture,
        string runId,
        PreparerDatabase.PublicationReconciliationPromotion promotion)
    {
        Assert.True(File.Exists(promotion.FinalPath));
        Assert.False(File.Exists(promotion.TemporaryPath));
        Assert.Equal(
            promotion.CandidateSizeBytes,
            new FileInfo(promotion.FinalPath).Length);
        Assert.Equal(
            promotion.CandidateSha256,
            await SqliteReviewSnapshotWriter.ComputeSha256Async(
                promotion.FinalPath));
        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues.Ready,
            fixture.Scalar<string>(
                $"""
                SELECT PromotionState
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = '{runId}'
                """));
        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues.Ready,
            fixture.Scalar<string>(
                $"""
                SELECT State
                FROM prepared_ticket_publication_reconciliation_journal
                WHERE RunId = '{runId}'
                """));
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(
            0,
            fixture.Scalar<long>(
                $"""
                SELECT COUNT(*)
                FROM prepared_ticket_publication_reconciliation_fences
                WHERE RunId = '{runId}'
                """));
        Assert.DoesNotContain(
            runId,
            await fixture.Database
                .ListPendingPublicationReconciliationsAsync());
    }

    private static async Task<AuthoringSnapshotDescriptor>
        ResumeCandidateFinalizationAsync(
            Fixture fixture,
            StagedReconciliation staged,
            HttpClient http,
            PreparedTicketPublicationRecoveryService recovery)
    {
        PreparedTicketGroupingDeltaDispatcher grouping = new(
            fixture.Database);
        PreparedTicketSnapshotMaterializer materializer = new(
            fixture.Database,
            fixture.Store,
            new SqliteReviewSnapshotReconciler(fixture.Store),
            Options.Create(new PreparerServiceOptions
            {
                SnapshotDirectory = fixture.SnapshotDirectory,
                SnapshotSchemaVersion =
                    PreparedTicketSnapshotSchemaV3.Version,
            }));
        PreparedTicketRunWorkflowRegistry workflows = new(
            fixture.Store,
            fixture.Database,
            new OrchestratorHydrationFetcher(http, NullLogger.Instance),
            staged.Planner,
            grouping,
            materializer,
            recovery,
            Options.Create(new PreparerServiceOptions
            {
                SnapshotDirectory = fixture.SnapshotDirectory,
                SnapshotSchemaVersion =
                    PreparedTicketSnapshotSchemaV3.Version,
            }),
            NullLogger<PreparedTicketRunWorkflowRegistry>.Instance);
        AuthoringRunRecord run = Assert.IsType<AuthoringRunRecord>(
            await fixture.Store.GetRunAsync(staged.RunId));
        return await workflows.FinalizeReconciliationAsync(run);
    }

    private static async Task AdvancePendingPromotionToBoundaryAsync(
        Fixture fixture,
        PreparerDatabase.PublicationReconciliationPromotion promotion,
        string boundary)
    {
        string[] supported =
        [
            "database-promoted",
            "snapshot-file-published",
            "snapshot-record-promoted",
            "snapshot-record-ready",
            "run-completed",
        ];
        Assert.Contains(boundary, supported);
        if (boundary == "database-promoted")
        {
            return;
        }

        await PublishPendingSnapshotFileAsync(promotion);
        if (boundary == "snapshot-file-published")
        {
            return;
        }

        string checksum =
            await SqliteReviewSnapshotWriter.ComputeSha256Async(
                promotion.FinalPath);
        long size = new FileInfo(promotion.FinalPath).Length;
        await fixture.Store.MarkSnapshotPromotedAsync(
            promotion.SnapshotId,
            checksum,
            size);
        if (boundary == "snapshot-record-promoted")
        {
            return;
        }

        _ = await fixture.Store.MarkSnapshotReadyAsync(
            promotion.SnapshotId);
        if (boundary == "snapshot-record-ready")
        {
            return;
        }

        await fixture.Store.CompleteRunAsync(
            promotion.RunId,
            promotion.SnapshotId);
    }

    private static async Task PublishPendingSnapshotFileAsync(
        PreparerDatabase.PublicationReconciliationPromotion promotion)
    {
        Assert.True(File.Exists(promotion.TemporaryPath));
        Assert.False(File.Exists(promotion.FinalPath));
        Assert.Equal(
            promotion.CandidateSizeBytes,
            new FileInfo(promotion.TemporaryPath).Length);
        Assert.Equal(
            promotion.CandidateSha256,
            await SqliteReviewSnapshotWriter.ComputeSha256Async(
                promotion.TemporaryPath));
        Directory.CreateDirectory(
            Assert.IsType<string>(
                Path.GetDirectoryName(promotion.FinalPath)));
        File.Move(
            promotion.TemporaryPath,
            promotion.FinalPath,
            overwrite: false);
    }

    private static void AssertReconciledGrouping(
        SqliteConnection connection)
    {
        AssertRow(
            connection,
            """
            SELECT ShortDescription, LongerDescription, RenderOrderHint
            FROM prepared_ticket_topics
            WHERE Specification = 'FHIR'
              AND Type = 'Change Request'
            """,
            [
                "Reconciled shared container",
                "Changed text for the shared discussion container.",
                0,
            ]);
        Assert.Equal(
            new object?[][]
            {
                [
                    "FHIR-29212",
                    0,
                    "FHIR-29212",
                    "Reconciled order keeps unchanged authored content fixed.",
                ],
                [
                    "FHIR-10028",
                    1,
                    "FHIR-29212",
                    "Reconciled order keeps unchanged authored content fixed.",
                ],
                ["FHIR-806", 0, null, null],
                ["FHIR-805", 1, null, null],
                ["FHIR-804", 2, null, null],
            }.Select(row => JsonSerializer.Serialize(row)),
            ReadRows(
                connection,
                """
                SELECT m.TicketKey, m.OrderInContainer,
                       g.FirstTicketKey, g.Rationale
                FROM prepared_ticket_topics t
                INNER JOIN prepared_ticket_topic_members m
                  ON m.TopicRowId = t.RowId
                LEFT JOIN prepared_ticket_topic_groups g
                  ON g.RowId = m.TopicGroupRowId
                 AND g.TopicRowId = t.RowId
                WHERE t.Specification = 'FHIR'
                  AND t.Type = 'Change Request'
                ORDER BY CASE WHEN g.RowId IS NULL THEN 1 ELSE 0 END,
                         m.OrderInContainer
                """));
        Assert.Equal(
            0,
            Scalar<long>(
                connection,
                """
                SELECT COUNT(*)
                FROM prepared_ticket_topic_members
                WHERE TicketKey = 'FHIR-803'
                """));
    }

    private static string ReadAbandonmentAudit(
        Fixture fixture,
        string runId)
    {
        using SqliteConnection connection = fixture.Database.OpenConnection();
        return Assert.Single(
            ReadRows(
                connection,
                $"""
                SELECT AbandonedAt, AbandonmentReason
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = '{runId}'
                """));
    }

    private static async Task AssertCanonicalUnpublishedRestrictionAsync(
        Func<Task> action)
    {
        AuthoringConflictException error =
            await Assert.ThrowsAsync<AuthoringConflictException>(action);
        Assert.Equal(
            AuthoringConflictCode.CanonicalUnpublishedRestriction,
            error.Code);
        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .CanonicalUnpublishedRestriction,
            AuthoringConflictException
                .CanonicalUnpublishedRestrictionCode);
    }

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
