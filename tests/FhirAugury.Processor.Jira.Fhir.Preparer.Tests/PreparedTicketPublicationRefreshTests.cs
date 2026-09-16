using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static FhirAugury.Processor.Jira.Fhir.Preparer.Tests.PreparedTicketPublicationTestFixture;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

[Collection(PreparedTicketPublicationTestCollection.Name)]
public sealed class PreparedTicketPublicationRefreshTests
{
    [Fact]
    public async Task StartReturnsLinkedSnapshotProducingRun()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-701");
        MetadataFetcher fetcher =
            MetadataFetcher.For(source.ExpectedRevisions);
        PreparedTicketPublicationRefreshService service =
            fixture.CreateRefreshService(fetcher);

        PreparedTicketPublicationRefreshResult result =
            await service.StartAsync(source.Run.Id);

        Assert.Equal(
            AuthoringRunPurposeValues.PublicationRefresh,
            result.Run.Purpose);
        Assert.Equal(source.Run.Id, result.Run.SourceRunId);
        Assert.False(result.Run.DatabaseOnly);
        Assert.Equal(AuthoringStatusValues.Runs.Running, result.Run.Status);
        AuthoringRunItemStatus item = Assert.Single(result.Items);
        Assert.Equal(AuthoringStatusValues.Items.Complete, item.Status);
        Assert.Equal(source.ReceiptIds["FHIR-701"], item.AcceptedReceiptId);
        Assert.StartsWith(
            $"maintenance:{result.Run.RunId}:",
            item.ItemKind,
            StringComparison.Ordinal);
        AuthoringRunRecord fenced = Assert.IsType<AuthoringRunRecord>(
            await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(result.Run.RunId, fenced.Id);
        PreparedTicketPublicationEnrichmentInput input = Assert.IsType<PreparedTicketPublicationEnrichmentInput>(
            PreparerDatabase.ReadPublicationEnrichmentInput(fenced));
        Assert.Equal(source.Descriptor.SnapshotId, input.Source.SnapshotId);
        Assert.Equal(new AuthoringRunCorpusComparison(source.Descriptor.SnapshotId, 1, 1, 0), result.Run.CorpusComparison);
    }

    [Fact]
    public async Task StartSignalsSchedulerWakeWithoutWaitingForSyncInterval()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-705");
        AuthoringRunSchedulerWakeSignal wakeSignal = new();
        System.Reflection.MethodInfo waitMethod =
            typeof(AuthoringRunSchedulerWakeSignal).GetMethod(
                "WaitAsync",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The scheduler wake wait primitive was not found.");
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(5));
        Task wake = (Task)(waitMethod.Invoke(
            wakeSignal,
            [timeout.Token])
            ?? throw new InvalidOperationException(
                "The scheduler wake wait primitive returned no task."));
        PreparedTicketPublicationRefreshService service =
            fixture.CreateRefreshService(
                MetadataFetcher.For(source.ExpectedRevisions),
                wakeSignal: wakeSignal);

        _ = await service.StartAsync(source.Run.Id);

        await wake.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task EndpointReturnsAcceptedWithAuthoritativeRunLocation()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-707");
        PreparedTicketPublicationMaintenanceController controller = new(
            fixture.CreateRefreshService(
                MetadataFetcher.For(source.ExpectedRevisions)));

        AcceptedResult accepted = Assert.IsType<AcceptedResult>(
            await controller.Start(
                source.Run.Id,
                CancellationToken.None));

        PreparedTicketPublicationRefreshResult result =
            Assert.IsType<PreparedTicketPublicationRefreshResult>(
                accepted.Value);
        Assert.Equal(
            $"/processing/authoring/runs/{result.Run.RunId}",
            accepted.Location);
        Assert.Equal(source.Run.Id, result.Run.SourceRunId);
        Assert.Equal(
            AuthoringRunPurposeValues.PublicationRefresh,
            result.Run.Purpose);
    }

    [Fact]
    public async Task RefreshUpdatesOnlyPublicationMetadataAndSnapshotProof()
    {
        using Fixture fixture = new();
        SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-701", "FHIR-702");
        fixture.MakeGroupingReceiptsLegacy(source.Run.Id);
        string protectedBefore = fixture.ReadAuthoredAndGroupingState();
        MetadataFetcher fetcher =
            MetadataFetcher.For(source.ExpectedRevisions);
        PreparedTicketPublicationRefreshService service =
            fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationRefreshResult started =
            await service.StartAsync(source.Run.Id);
        CountingGroupingDispatcher dispatcher =
            new(fixture.Database);

        AuthoringSnapshotDescriptor descriptor =
            Assert.IsType<AuthoringSnapshotDescriptor>(
                await fixture.CreatePostProcessor(
                        service,
                        dispatcher)
                    .FinalizeRunAsync(started.Run.RunId));

        Assert.Equal(0, dispatcher.CallCount);
        Assert.Equal(2, fetcher.CallCount);
        Assert.Equal(
            protectedBefore,
            fixture.ReadAuthoredAndGroupingState());
        Assert.Equal(
            PreparedTicketSnapshotSchemaV3.Version,
            descriptor.SchemaVersion);
        Assert.True(descriptor.Sequence > source.Descriptor.Sequence);
        Assert.Equal(
            AuthoringStatusValues.Runs.Completed,
            (await fixture.Store.GetRunAsync(started.Run.RunId))!.Status);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));

        AuthoringSnapshotPublicationProof proof =
            Assert.IsType<AuthoringSnapshotPublicationProof>(
                descriptor.PublicationProof);
        Assert.Equal(
            PreparedTicketPublicationContract.CurrentVersion,
            proof.ContractVersion);
        Assert.Equal(
            PreparedTicketPublicationContract.PublicationRefreshPurpose,
            proof.Purpose);
        Assert.Equal(source.Run.Id, proof.SourceRunId);
        Assert.Equal(
            PreparedTicketPublicationContract.JiraSourceName,
            proof.SourceName);
        Assert.Equal(901, proof.SourceContentRevision);
        Assert.Equal(TimeSpan.Zero, proof.SourceLastSuccessfulRefreshAt.Offset);
        Assert.Equal(TimeSpan.Zero, proof.CapturedAt.Offset);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            proof.PublicDisplayNamePolicyVersion);

        PreparedTicketPublicationRefreshInventory inventory =
            await fixture.Database.GetPublicationRefreshInventoryAsync();
        Assert.Equal(inventory.CorpusFingerprint, proof.CorpusFingerprint);
        IReadOnlyList<PreparedTicketRunPartition> partitions =
            await fixture.Database.GetRunPartitionsAsync(
                started.Run.RunId);
        IReadOnlyList<PreparedTicketGroupingCertificationEvidence>
            certifications =
                await fixture.Database
                    .GetPublicationGroupingCertificationsAsync(
                        started.Run.RunId,
                        partitions);
        Assert.All(
            certifications,
            certification => Assert.True(
                certification.IsLegacyCertification));
        Assert.Equal(
            PreparedTicketPublicationContract.ComputeGroupingFingerprint(
                certifications.Select(certification =>
                    new PreparedTicketPublicationGroupingPartition(
                        certification.PartitionKey,
                        certification.OutputFingerprint))),
            proof.GroupingFingerprint);

        using SqliteConnection snapshot = OpenReadOnly(
            Path.Combine(fixture.SnapshotDirectory, descriptor.FileName));
        Assert.Equal(
            2,
            ScalarInt(
                snapshot,
                "SELECT COUNT(*) FROM prepared_tickets"));
        Assert.Equal(
            1,
            ScalarInt(
                snapshot,
                $"""
                SELECT COUNT(*)
                FROM prepared_ticket_partition_receipts
                WHERE RunId = '{source.Run.Id}'
                """));
        Assert.Equal(
            2,
            ScalarInt(
                snapshot,
                $"""
                SELECT COUNT(*)
                FROM authoring_runs
                WHERE Id IN ('{source.Run.Id}', '{started.Run.RunId}')
                """));
        Assert.Equal(
            1,
            ScalarInt(
                snapshot,
                $"""
                SELECT COUNT(*)
                FROM authoring_run_input_provenance
                WHERE RunId = '{started.Run.RunId}'
                  AND ContentRevision = 901
                """));
        Assert.Equal(
            0,
            ScalarInt(
                snapshot,
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name IN (
                      'prepared_ticket_publication_refresh_receipts',
                      'prepared_ticket_partition_certifications')
                """));
        Assert.Equal(
            0,
            ScalarInt(
                snapshot,
                """
                SELECT COUNT(*)
                FROM pragma_table_info('authoring_runs')
                WHERE name IN ('Purpose', 'SourceRunId')
                """));
        Assert.Equal(
            "Reporter FHIR-701",
            ScalarString(
                snapshot,
                """
                SELECT Reporter
                FROM prepared_ticket_hydration
                WHERE TicketKey = 'FHIR-701'
                """));
        Assert.Equal(
            1,
            fixture.CountLive(
                "prepared_ticket_partition_certifications"));
    }

    [Fact]
    public async Task EquivalentOffsetRefreshPreservesFrozenRevisionCoordinates()
    {
        const string ticketKey = "FHIR-10028";
        const string frozenRevision =
            "2025-07-17T16:12:12.0000000-05:00";
        const string observedRevision =
            "2025-07-17T21:12:12.0000000+00:00";
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAtAsync(
            new DateTimeOffset(
                2025,
                7,
                17,
                16,
                12,
                12,
                TimeSpan.FromHours(-5)),
            ticketKey);
        Assert.Equal(
            frozenRevision,
            source.ExpectedRevisions[ticketKey]);

        (
            string ReceiptId,
            string ItemExpectedRevision,
            string ReceiptExpectedRevision,
            string ReceiptObservedRevision) frozenCoordinates;
        using (SqliteConnection connection =
               fixture.Database.OpenConnection())
        {
            frozenCoordinates = ReadAcceptedRevisionCoordinates(
                connection,
                source.Run.Id,
                ticketKey);
        }
        Assert.Equal(
            (
                source.ReceiptIds[ticketKey],
                frozenRevision,
                frozenRevision,
                frozenRevision),
            frozenCoordinates);

        MetadataFetcher fetcher =
            MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ObservedRevisions[ticketKey] = observedRevision;
        PreparedTicketPublicationRefreshService service =
            fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationRefreshResult started =
            await service.StartAsync(source.Run.Id);

        AuthoringSnapshotDescriptor descriptor =
            Assert.IsType<AuthoringSnapshotDescriptor>(
                await fixture.CreatePostProcessor(service)
                    .FinalizeRunAsync(started.Run.RunId));

        Assert.Equal(1, fetcher.CallCount);
        Assert.Equal(
            PreparedTicketSnapshotSchemaV3.Version,
            descriptor.SchemaVersion);
        Assert.NotNull(descriptor.PublicationProof);
        Assert.Equal(
            AuthoringStatusValues.Runs.Completed,
            (await fixture.Store.GetRunAsync(started.Run.RunId))!.Status);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(
            1,
            fixture.CountLive(
                "prepared_ticket_publication_refresh_receipts"));

        using (SqliteConnection connection =
               fixture.Database.OpenConnection())
        {
            Assert.Equal(
                frozenCoordinates,
                ReadAcceptedRevisionCoordinates(
                    connection,
                    source.Run.Id,
                    ticketKey));
            Assert.Equal(
                frozenRevision,
                ReadRunItemExpectedRevision(
                    connection,
                    started.Run.RunId,
                    ticketKey));
        }

        using SqliteConnection snapshot = OpenReadOnly(
            Path.Combine(fixture.SnapshotDirectory, descriptor.FileName));
        Assert.Equal(
            frozenCoordinates,
            ReadAcceptedRevisionCoordinates(
                snapshot,
                source.Run.Id,
                ticketKey));
        Assert.Equal(
            frozenRevision,
            ReadRunItemExpectedRevision(
                snapshot,
                started.Run.RunId,
                ticketKey));
    }

    [Fact]
    public async Task SnapshotRetainsNonContributingSourceRunLineage()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-706");
        SourceResult current = await fixture.CreateSourceRunAtAsync(
            new DateTimeOffset(
                2026,
                9,
                2,
                0,
                0,
                0,
                TimeSpan.Zero),
            "FHIR-706");
        PreparedTicketPublicationRefreshService service =
            fixture.CreateRefreshService(
                MetadataFetcher.For(current.ExpectedRevisions));
        PreparedTicketPublicationRefreshResult started =
            await fixture.CreateLegacyRefreshRunAsync(source.Run.Id);

        AuthoringSnapshotDescriptor descriptor =
            Assert.IsType<AuthoringSnapshotDescriptor>(
                await fixture.CreatePostProcessor(service)
                    .FinalizeRunAsync(started.Run.RunId));

        Assert.Equal(
            source.Run.Id,
            descriptor.PublicationProof!.SourceRunId);
        using SqliteConnection snapshot = OpenReadOnly(
            Path.Combine(fixture.SnapshotDirectory, descriptor.FileName));
        Assert.Equal(
            3,
            ScalarInt(
                snapshot,
                $"""
                SELECT COUNT(*)
                FROM authoring_runs
                WHERE Id IN (
                    '{source.Run.Id}',
                    '{current.Run.Id}',
                    '{started.Run.RunId}')
                """));
        Assert.Equal(
            current.Run.Id,
            ScalarString(
                snapshot,
                """
                SELECT item.RunId
                FROM authoring_run_items item
                INNER JOIN authoring_result_receipts receipt
                    ON receipt.Id = item.AcceptedReceiptId
                   AND receipt.RunId = item.RunId
                WHERE item.BusinessKey = 'FHIR-706'
                """));
    }

    [Fact]
    public async Task ChangedRevisionTerminatesAndReleasesFence()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-710");
        MetadataFetcher fetcher =
            MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ObservedRevisions["FHIR-710"] =
            "2026-09-01T00:00:01.0000000+00:00";
        PreparedTicketPublicationRefreshService service =
            fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationRefreshResult started =
            await service.StartAsync(source.Run.Id);
        string publicationBefore =
            fixture.ReadPublicationMetadataState();

        AuthoringSnapshotDescriptor? descriptor =
            await fixture.CreatePostProcessor(service)
                .FinalizeRunAsync(started.Run.RunId);

        Assert.Null(descriptor);
        AuthoringRunRecord superseded =
            Assert.IsType<AuthoringRunRecord>(
                await fixture.Store.GetRunAsync(started.Run.RunId));
        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            superseded.Status);
        Assert.Contains(
            "Retain the existing publication and inspect the conflict",
            superseded.Error,
            StringComparison.Ordinal);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(
            publicationBefore,
            fixture.ReadPublicationMetadataState());
        Assert.DoesNotContain(
            await fixture.Store.GetSnapshotRecordsAsync(),
            record => record.RunId == started.Run.RunId);
        Assert.Equal(
            0,
            fixture.CountLive(
                "prepared_ticket_publication_refresh_receipts"));
    }

    [Fact]
    public async Task MixedSourceGenerationIsRejected()
    {
        using Fixture fixture = new();
        SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-720", "FHIR-721");
        MetadataFetcher fetcher =
            MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ContentRevisions["FHIR-721"] = 902;
        PreparedTicketPublicationRefreshService service =
            fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationRefreshResult started =
            await service.StartAsync(source.Run.Id);

        Assert.Null(
            await fixture.CreatePostProcessor(service)
                .FinalizeRunAsync(started.Run.RunId));

        AuthoringRunRecord superseded =
            (await fixture.Store.GetRunAsync(started.Run.RunId))!;
        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            superseded.Status);
        Assert.Contains(
            PreparedTicketPublicationRefreshFailureCodes
                .SourceGenerationConflict,
            superseded.Error,
            StringComparison.Ordinal);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(
            0,
            fixture.CountLive(
                "prepared_ticket_publication_refresh_receipts"));
    }

    [Fact]
    public async Task CrashAfterApplyReusesDurableReceipt()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-730");
        MetadataFetcher firstFetcher =
            MetadataFetcher.For(source.ExpectedRevisions);
        ThrowOnceAfterCommitHook hook = new();
        PreparedTicketPublicationRefreshService firstService =
            fixture.CreateRefreshService(firstFetcher, hook);
        PreparedTicketPublicationRefreshResult started =
            await firstService.StartAsync(source.Run.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.CreatePostProcessor(firstService)
                .FinalizeRunAsync(started.Run.RunId));

        Assert.Equal(1, firstFetcher.CallCount);
        Assert.Equal(1, hook.CallCount);
        Assert.Equal(
            AuthoringStatusValues.Runs.Error,
            (await fixture.Store.GetRunAsync(started.Run.RunId))!.Status);
        Assert.Equal(
            1,
            fixture.CountLive(
                "prepared_ticket_publication_refresh_receipts"));
        Assert.Equal(
            started.Run.RunId,
            (await fixture.Store.GetFencedRunAsync("jira-fhir"))!.Id);

        MetadataFetcher secondFetcher =
            MetadataFetcher.For(source.ExpectedRevisions);
        secondFetcher.ThrowWhenCalled = true;
        PreparedTicketPublicationRefreshService resumedService =
            fixture.CreateRefreshService(secondFetcher);
        AuthoringSnapshotDescriptor descriptor =
            Assert.IsType<AuthoringSnapshotDescriptor>(
                await fixture.CreatePostProcessor(resumedService)
                    .FinalizeRunAsync(started.Run.RunId));

        Assert.Equal(0, secondFetcher.CallCount);
        Assert.NotNull(descriptor.PublicationProof);
        Assert.Equal(
            AuthoringStatusValues.Runs.Completed,
            (await fixture.Store.GetRunAsync(started.Run.RunId))!.Status);
        Assert.Equal(
            1,
            fixture.CountLive(
                "prepared_ticket_publication_refresh_receipts"));
    }

    [Fact]
    public async Task PublicationRefreshResumesAfterInterruptedPromotion()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-735");
        PreparedTicketPublicationRefreshService service =
            fixture.CreateRefreshService(
                MetadataFetcher.For(source.ExpectedRevisions));
        PreparedTicketPublicationRefreshResult started =
            await service.StartAsync(source.Run.Id);
        PreparedTicketRunPostProcessor postProcessor =
            fixture.CreatePostProcessor(service);
        AuthoringSnapshotDescriptor first =
            Assert.IsType<AuthoringSnapshotDescriptor>(
                await postProcessor.FinalizeRunAsync(
                    started.Run.RunId));
        fixture.SimulateReadySnapshotBeforeRunCompletion(
            started.Run.RunId);

        AuthoringSnapshotDescriptor resumed =
            Assert.IsType<AuthoringSnapshotDescriptor>(
                await postProcessor.FinalizeRunAsync(
                    started.Run.RunId));

        Assert.Equal(first.SnapshotId, resumed.SnapshotId);
        Assert.Equal(first.Sequence, resumed.Sequence);
        Assert.Single(
            await fixture.Store.GetSnapshotRecordsAsync(),
            record =>
                record.RunId == started.Run.RunId &&
                record.Status ==
                    AuthoringStatusValues.Snapshots.Ready);
        AuthoringRunRecord completed =
            (await fixture.Store.GetRunAsync(started.Run.RunId))!;
        Assert.Equal(
            AuthoringStatusValues.Runs.Completed,
            completed.Status);
        Assert.Equal(first.SnapshotId, completed.SnapshotId);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
    }

    [Fact]
    public async Task LostMetadataStageLeaseResumesFromDurableReceipt()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-737");
        MetadataFetcher firstFetcher =
            MetadataFetcher.For(source.ExpectedRevisions);
        LoseStageLeaseAfterCommitHook hook =
            new(fixture.Database, fixture.Store);
        PreparedTicketPublicationRefreshService firstService =
            fixture.CreateRefreshService(firstFetcher, hook);
        PreparedTicketPublicationRefreshResult started =
            await firstService.StartAsync(source.Run.Id);

        AuthoringConflictException leaseLost =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => fixture.CreatePostProcessor(firstService)
                    .FinalizeRunAsync(started.Run.RunId));

        Assert.Equal(
            AuthoringConflictCode.StageLeaseLost,
            leaseLost.Code);
        Assert.Equal(1, firstFetcher.CallCount);
        Assert.Equal(
            AuthoringStatusValues.Runs.Finalizing,
            (await fixture.Store.GetRunAsync(started.Run.RunId))!.Status);

        MetadataFetcher resumedFetcher =
            MetadataFetcher.For(source.ExpectedRevisions);
        resumedFetcher.ThrowWhenCalled = true;
        PreparedTicketPublicationRefreshService resumedService =
            fixture.CreateRefreshService(resumedFetcher);
        AuthoringSnapshotDescriptor descriptor =
            Assert.IsType<AuthoringSnapshotDescriptor>(
                await fixture.CreatePostProcessor(resumedService)
                    .FinalizeRunAsync(started.Run.RunId));

        Assert.Equal(0, resumedFetcher.CallCount);
        Assert.NotNull(descriptor.PublicationProof);
        Assert.Equal(
            AuthoringStatusValues.Runs.Completed,
            (await fixture.Store.GetRunAsync(started.Run.RunId))!.Status);
    }

    [Fact]
    public async Task StartRejectsInvalidSourceAndReportsActiveRefresh()
    {
        using Fixture fixture = new();
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => fixture.CreateRefreshService(
                    MetadataFetcher.For(
                        new Dictionary<string, string>()))
                .StartAsync("missing"));

        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-740");
        PreparedTicketPublicationRefreshService service =
            fixture.CreateRefreshService(
                MetadataFetcher.For(source.ExpectedRevisions));
        PreparedTicketPublicationRefreshResult first =
            await service.StartAsync(source.Run.Id);

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => service.StartAsync(source.Run.Id));

        Assert.Equal(
            AuthoringConflictCode.MutationFenceUnavailable,
            conflict.Code);
        Assert.Equal([first.Run.RunId], conflict.RelatedRunIds);
    }

    [Theory]
    [InlineData("protected-value", PreparedTicketPublicationRefreshFailureCodes.OriginalOutputChanged)]
    [InlineData("grouping", PreparedTicketPublicationRefreshFailureCodes.OriginalGroupingChanged)]
    [InlineData("missing-parent", PreparedTicketPublicationRefreshFailureCodes.InvalidAcceptedGraph)]
    [InlineData("missing-snapshot", PreparedTicketPublicationRefreshFailureCodes.InvalidSourceSnapshot)]
    public async Task EndpointReportsTypedPreservationRefusalWithoutAdmittingRun(string change, string expectedCode)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801", "FHIR-802");
        switch (change)
        {
            case "protected-value":
                fixture.Execute("UPDATE prepared_jira_hydration SET DescriptionHtml = 'changed' WHERE JiraKey = TicketKey");
                break;
            case "grouping":
                fixture.Execute("UPDATE prepared_ticket_topics SET ShortDescription = 'changed topic'");
                break;
            case "missing-parent":
                fixture.Execute("DELETE FROM prepared_ticket_hydration WHERE TicketKey = 'FHIR-801'");
                break;
            case "missing-snapshot":
                string path = Path.Combine(fixture.SnapshotDirectory, source.Descriptor.FileName);
                File.Move(path, path + ".unavailable");
                break;
        }
        int runCount = fixture.CountLive("authoring_runs");
        string before = fixture.ReadPublicationMetadataState();
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ThrowWhenCalled = true;
        PreparedTicketPublicationMaintenanceController controller = new(fixture.CreateRefreshService(fetcher));

        ConflictObjectResult response = Assert.IsType<ConflictObjectResult>(
            await controller.Start(source.Run.Id, CancellationToken.None));

        PreparedTicketPublicationRefreshFailure failure = Assert.IsType<PreparedTicketPublicationRefreshFailure>(response.Value);
        Assert.Equal(expectedCode, failure.Error);
        Assert.Equal(runCount, fixture.CountLive("authoring_runs"));
        Assert.Equal(before, fixture.ReadPublicationMetadataState());
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(0, fetcher.CallCount);
        Assert.Equal(0, fetcher.ZulipCallCount);
    }

    [Fact]
    public async Task StartDisclosesAdditionalCurrentOutputAndFreezesItsAcceptedReferences()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801", "FHIR-802");
        AuthoringRunRecord additional = await fixture.CreateAdditionalCurrentOutputAsync("R5", ["FHIR-901", "FHIR-902"]);
        Dictionary<string, string> revisions = new(source.ExpectedRevisions, StringComparer.OrdinalIgnoreCase);
        foreach (AuthoringRunItemRecord item in await fixture.Store.GetRunItemsAsync(additional.Id))
        {
            revisions.Add(item.BusinessKey, item.ExpectedSourceRevision);
        }
        string protectedBefore = fixture.ReadAuthoredAndGroupingState();
        MetadataFetcher fetcher = MetadataFetcher.For(revisions);
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(fetcher);

        PreparedTicketPublicationRefreshResult result = await service.StartAsync(source.Run.Id);

        Assert.Equal(new AuthoringRunCorpusComparison(source.Descriptor.SnapshotId, 2, 4, 2), result.Run.CorpusComparison);
        AuthoringRunRecord persisted = (await fixture.Store.GetRunAsync(result.Run.RunId))!;
        PreparedTicketPublicationEnrichmentInput input = Assert.IsType<PreparedTicketPublicationEnrichmentInput>(
            PreparerDatabase.ReadPublicationEnrichmentInput(persisted));
        Assert.Equal(["FHIR-901", "FHIR-902"], input.AdditionalTicketKeys);
        Assert.Equal(8, input.ZulipReferences.Count);
        Assert.Equal(result.Run.CorpusComparison, AuthoringMaintenanceRunRequest.ReadCorpusComparison(persisted.RequestJson));
        Assert.NotNull(await fixture.CreatePostProcessor(service).FinalizeRunAsync(persisted.Id));
        Assert.Equal(protectedBefore, fixture.ReadAuthoredAndGroupingState());
        Assert.Equal(4, fetcher.CallCount);
        Assert.Equal(2, fetcher.ZulipCallCount);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        connection.Open();
        return connection;
    }

    private static int ScalarInt(
        SqliteConnection connection,
        string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string ScalarString(
        SqliteConnection connection,
        string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static (
        string ReceiptId,
        string ItemExpectedRevision,
        string ReceiptExpectedRevision,
        string ReceiptObservedRevision) ReadAcceptedRevisionCoordinates(
            SqliteConnection connection,
            string runId,
            string ticketKey)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT receipt.Id, item.ExpectedSourceRevision,
                   receipt.ExpectedSourceRevision,
                   receipt.ObservedSourceRevision
            FROM authoring_run_items item
            INNER JOIN authoring_result_receipts receipt
                ON receipt.Id = item.AcceptedReceiptId
            WHERE item.RunId = @runId
              AND item.BusinessKey = @ticketKey COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@ticketKey", ticketKey);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        (
            string ReceiptId,
            string ItemExpectedRevision,
            string ReceiptExpectedRevision,
            string ReceiptObservedRevision) result =
            (
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3));
        Assert.False(reader.Read());
        return result;
    }

    private static string ReadRunItemExpectedRevision(
        SqliteConnection connection,
        string runId,
        string ticketKey)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ExpectedSourceRevision
            FROM authoring_run_items
            WHERE RunId = @runId
              AND BusinessKey = @ticketKey COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@ticketKey", ticketKey);
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private sealed class ThrowOnceAfterCommitHook
        : IPreparedTicketPublicationRefreshInterruptionHook
    {
        public int CallCount { get; private set; }

        public Task AfterMetadataCommitAsync(
            string runId,
            PreparedTicketPublicationRefreshReceiptRecord receipt,
            CancellationToken ct)
        {
            CallCount++;
            throw new InvalidOperationException(
                "Simulated process interruption after metadata commit.");
        }
    }

    private sealed class LoseStageLeaseAfterCommitHook(
        PreparerDatabase database,
        AuthoringRunStore store)
        : IPreparedTicketPublicationRefreshInterruptionHook
    {
        public async Task AfterMetadataCommitAsync(
            string runId,
            PreparedTicketPublicationRefreshReceiptRecord receipt,
            CancellationToken ct)
        {
            AuthoringRunStageLease replacement =
                Assert.IsType<AuthoringRunStageLease>(
                    await store.TryStartRunStageAsync(
                        receipt.StageId,
                        TimeSpan.FromTicks(1),
                        now: DateTimeOffset.UtcNow.AddMinutes(1),
                        ct: ct));
            using SqliteConnection connection =
                database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE authoring_run_stages
                SET LeaseAcquiredAt =
                    '2000-01-01T00:00:00.0000000+00:00'
                WHERE Id = @stageId AND LeaseId = @leaseId
                """;
            command.Parameters.AddWithValue(
                "@stageId",
                receipt.StageId);
            command.Parameters.AddWithValue(
                "@leaseId",
                replacement.LeaseId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
    }

}
