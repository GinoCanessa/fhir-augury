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
    private const string NullableWorkgroupPartitionKey = "unattributed\u001fFHIR\u001fChange Request";
    private const string AttributedWorkgroupPartitionKey = "FHIRInfrastructure\u001fFHIR\u001fChange Request";

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
    [InlineData(1, null, null, "unattributed", "unattributed")]
    [InlineData(2, null, null, "unattributed", "unattributed")]
    [InlineData(3, null, null, "unattributed", "unattributed")]
    [InlineData(3, "", "", "unattributed", "unattributed")]
    [InlineData(3, "   ", "   ", "unattributed", "unattributed")]
    [InlineData(3, null, "fhir-i", "fhir-i", "fhir-i")]
    [InlineData(3, "   ", " fhir-i ", "fhir-i", "fhir-i")]
    [InlineData(3, " FHIR Infrastructure ", "fhir-i", "fhir-i", "FHIR Infrastructure")]
    public async Task ProtectionReadsNullableNoTopicWorkgroups(
        int schemaVersion,
        string? workGroup,
        string? workGroupClean,
        string expectedClean,
        string expectedDisplay)
    {
        string[] ticketKeys = ["FHIR-10041", "FHIR-10042"];
        using Fixture fixture = new(schemaVersion: schemaVersion);
        SourceResult source = string.IsNullOrEmpty(workGroupClean)
            ? await CreateNoTopicWorkgroupSourceAsync(fixture, workGroup, workGroupClean, ticketKeys)
            : await CreateNoTopicReaderSourceAsync(fixture, schemaVersion, workGroup, workGroupClean, ticketKeys);
        string path = Path.Combine(fixture.SnapshotDirectory, source.Descriptor.FileName);
        string digest = await SqliteReviewSnapshotWriter.ComputeSha256Async(path);
        Assert.Equal(source.Descriptor.Sha256, digest);
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            AssertStoredSelfWorkgroups(connection, ticketKeys, workGroup, workGroupClean);
            AssertNoStoredTopics(connection);
        }
        await using (SqliteConnection snapshot = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(path))
        {
            AssertStoredSelfWorkgroups(snapshot, ticketKeys, workGroup, workGroupClean);
            AssertNoStoredTopics(snapshot);
        }

        PreparedTicketPublicationBaseline? baseline = null;
        Exception? readError = await Record.ExceptionAsync(async () =>
        {
            baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        });
        Assert.True(readError is null, readError is PreparedTicketPublicationProtectionException protectionError
            ? $"{protectionError.FailureCode}: {protectionError}"
            : readError?.ToString());
        Assert.NotNull(baseline);
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();
        PreparedTicketPublicationProtectedInventory reverse = await fixture.ReadCurrentAsync(reverseEnumeration: true);

        Assert.Equal(source.Run.Id, baseline.Source.RunId);
        Assert.Equal(source.Descriptor.SnapshotId, baseline.Source.SnapshotId);
        Assert.Equal(digest, baseline.Source.SnapshotSha256);
        Assert.Equal(schemaVersion, baseline.Source.SchemaVersion);
        Assert.Equal(schemaVersion, baseline.Inventory.SchemaVersion);
        Assert.Equal(2, baseline.Source.ExportedTicketCount);
        IReadOnlyList<AuthoringRunItemRecord> sourceItems = await fixture.Store.GetRunItemsAsync(source.Run.Id);
        Assert.Equal(2, sourceItems.Count);
        PreparedTicketPublicationCorpusItem[] expectedCorpus = ticketKeys.Select(key =>
            new PreparedTicketPublicationCorpusItem(
                key, source.ReceiptIds[key],
                Assert.Single(sourceItems, item => item.BusinessKey == key).Id,
                source.Run.Id, "fhir", source.ExpectedRevisions[key])).ToArray();
        string expectedCorpusFingerprint = PreparedTicketPublicationContract.ComputeCorpusFingerprint(expectedCorpus);
        string expectedPartitionKey = $"{expectedClean}\u001fFHIR\u001fChange Request";
        PreparedTicketGroupingPayload expectedPayload = new()
        {
            WorkGroupClean = expectedClean,
            WorkGroupDisplay = expectedDisplay,
            Specification = "FHIR",
            Type = "Change Request",
            Topics = [],
        };
        PreparedTicketPublicationProtectedGroupingFingerprint expectedGrouping = new(
            expectedPartitionKey,
            expectedCorpusFingerprint,
            PreparedTicketPublicationContract.ComputeGroupingPartitionFingerprint(expectedPayload),
            PreparedTicketPublicationEnrichmentContract.ComputeProtectedContentFingerprint(
                Array.Empty<PreparedTicketPublicationProtectedRow>()));
        foreach (PreparedTicketPublicationProtectedInventory inventory in new[] { baseline.Inventory, current, reverse })
        {
            Assert.Equal(expectedCorpus, inventory.Corpus.OrderBy(item => item.TicketKey, StringComparer.Ordinal).ToArray());
            Assert.Equal(expectedCorpusFingerprint, inventory.CorpusFingerprint);
            PreparedTicketPublicationProtectedGrouping partition = Assert.Single(inventory.Grouping);
            Assert.Equal(expectedPartitionKey, partition.PartitionKey);
            Assert.Equal(expectedCorpus, partition.Corpus.OrderBy(item => item.TicketKey, StringComparer.Ordinal).ToArray());
            Assert.Empty(partition.Rows);
            Assert.Equal(expectedGrouping, partition.Fingerprint);
            AssertProtectedSelfWorkgroups(inventory, ticketKeys, workGroup, workGroupClean);
        }
        PreparedTicketPublicationPreservationComparison comparison =
            PreparedTicketPublicationProtectionReader.Compare(baseline, current);
        Assert.Equal(new AuthoringRunCorpusComparison(source.Descriptor.SnapshotId, 2, 2, 0), comparison.CorpusComparison);
        Assert.Empty(comparison.AdditionalTicketKeys);
        Assert.Empty(comparison.AdditionalGroupingPartitions);
        Assert.Equal(current.CorpusFingerprint, reverse.CorpusFingerprint);
        Assert.Equal(current.ProtectedContentFingerprint, reverse.ProtectedContentFingerprint);
        Assert.Equal(current.RetainedGroupingFingerprint, reverse.RetainedGroupingFingerprint);
        Assert.Equal(
            PreparedTicketPublicationEnrichmentContract.SerializeInput(
                PreparedTicketPublicationProtectionReader.CreateRecipeInput(baseline, current)),
            PreparedTicketPublicationEnrichmentContract.SerializeInput(
                PreparedTicketPublicationProtectionReader.CreateRecipeInput(baseline, reverse)));

        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            AssertStoredSelfWorkgroups(connection, ticketKeys, workGroup, workGroupClean);
            AssertNoStoredTopics(connection);
        }
        Assert.Equal(digest, await SqliteReviewSnapshotWriter.ComputeSha256Async(path));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unattributed")]
    public async Task NullableWorkgroupDriftStillRefusesProtectionAndAdmission(string workGroup)
    {
        string[] ticketKeys = ["FHIR-10043", "FHIR-10044"];
        using Fixture fixture = new();
        SourceResult source = await CreateNoTopicWorkgroupSourceAsync(fixture, null, null, ticketKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        PreparedTicketPublicationProtectedInventory before = await fixture.ReadCurrentAsync();
        PreparedTicketPublicationEnrichmentInput input =
            PreparedTicketPublicationProtectionReader.CreateRecipeInput(baseline, before);
        PreparedTicketPublicationProtectionReader.ValidateFrozen(input, before);
        AssertProtectedSelfWorkgroups(baseline.Inventory, ticketKeys, null, null);
        AssertProtectedSelfWorkgroups(before, ticketKeys, null, null);

        Assert.Equal(1, fixture.Execute(
            """
            UPDATE prepared_jira_hydration SET WorkGroup = @workGroup
            WHERE TicketKey = @ticketKey AND JiraKey = TicketKey
            """,
            ("@workGroup", workGroup), ("@ticketKey", ticketKeys[0])));
        string metadataBefore = fixture.ReadPublicationMetadataState();
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();

        Assert.Equal(before.CorpusFingerprint, current.CorpusFingerprint);
        Assert.Equal(
            Assert.Single(baseline.Inventory.Grouping).Fingerprint,
            Assert.Single(current.Grouping).Fingerprint);
        Assert.Equal(before.RetainedGroupingFingerprint, current.RetainedGroupingFingerprint);
        Assert.NotEqual(before.ProtectedContentFingerprint, current.ProtectedContentFingerprint);
        AssertProtectedSelfWorkgroups(current, [ticketKeys[0]], workGroup, null);
        AssertProtectedSelfWorkgroups(current, [ticketKeys[1]], null, null);
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            AssertStoredSelfWorkgroups(connection, [ticketKeys[0]], workGroup, null);
            AssertStoredSelfWorkgroups(connection, [ticketKeys[1]], null, null);
            AssertNoStoredTopics(connection);
        }
        PreparedTicketPublicationProtectionException comparisonError =
            Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
                PreparedTicketPublicationProtectionReader.Compare(baseline, current));
        Assert.Equal(PreparedTicketPublicationRefreshFailureCodes.OriginalOutputChanged, comparisonError.FailureCode);
        PreparedTicketPublicationProtectionException frozenError =
            Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
                PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current));
        Assert.Equal(PreparedTicketPublicationRefreshFailureCodes.FrozenProtectionDrift, frozenError.FailureCode);
        Assert.Equal(metadataBefore, fixture.ReadPublicationMetadataState());
        await AssertAdmissionRefusedWithoutMutationAsync(
            fixture, source, PreparedTicketPublicationRefreshFailureCodes.OriginalOutputChanged);
        await AssertSourceSnapshotUnchangedAsync(fixture, source);
    }

    [Theory]
    [InlineData("empty-type")]
    [InlineData("whitespace-type")]
    [InlineData("blob-display")]
    [InlineData("disagreeing-displays")]
    public async Task NullableNoTopicProjectionStillRejectsInvalidGraph(string change)
    {
        string[] ticketKeys = ["FHIR-10045", "FHIR-10046"];
        using Fixture fixture = new();
        SourceResult source = await CreateNoTopicWorkgroupSourceAsync(fixture, null, null, ticketKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        PreparedTicketPublicationProtectionReader.Compare(baseline, await fixture.ReadCurrentAsync());
        AssertProtectedSelfWorkgroups(baseline.Inventory, ticketKeys, null, null);
        (string column, object value, string detail) = change switch
        {
            "empty-type" => ("Type", "", "Grouping has invalid partition coordinates."),
            "whitespace-type" => ("Type", "   ", "Grouping has invalid partition coordinates."),
            "blob-display" => ("WorkGroup", (object)new byte[] { 70, 72, 73, 82 },
                "Coordinate 'prepared_jira_hydration.WorkGroup' is not text."),
            "disagreeing-displays" => ("WorkGroup", "FHIR Infrastructure",
                "Grouping has no unique workgroup display value."),
            _ => throw new InvalidOperationException("Unknown invalid-graph mutation."),
        };
        Assert.Equal(1, fixture.Execute(
            $"""
            UPDATE prepared_jira_hydration SET {column} = @value
            WHERE TicketKey = @ticketKey AND JiraKey = TicketKey
            """,
            ("@value", value), ("@ticketKey", ticketKeys[0])));
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            AssertNoStoredTopics(connection);
            AssertStoredSelfWorkgroups(connection, [ticketKeys[1]], null, null);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT typeof({column}) FROM prepared_jira_hydration
                WHERE TicketKey = @ticketKey AND JiraKey = TicketKey
                """;
            command.Parameters.AddWithValue("@ticketKey", ticketKeys[0]);
            Assert.Equal(change == "blob-display" ? "blob" : "text", command.ExecuteScalar());
        }
        string metadataBefore = fixture.ReadPublicationMetadataState();

        PreparedTicketPublicationProtectionException error =
            await Assert.ThrowsAsync<PreparedTicketPublicationProtectionException>(() => fixture.ReadCurrentAsync());

        Assert.Equal(PreparedTicketPublicationRefreshFailureCodes.InvalidAcceptedGraph, error.FailureCode);
        Assert.Equal(detail, error.Message);
        Assert.Equal(metadataBefore, fixture.ReadPublicationMetadataState());
        await AssertAdmissionRefusedWithoutMutationAsync(
            fixture, source, PreparedTicketPublicationRefreshFailureCodes.InvalidAcceptedGraph);
        await AssertSourceSnapshotUnchangedAsync(fixture, source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StoredTopicDisplaysRemainStrictAndAuthoritative(string? topicDisplay)
    {
        string[] ticketKeys = ["FHIR-10047", "FHIR-10048"];
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(ticketKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        PreparedTicketPublicationProtectedInventory before = await fixture.ReadCurrentAsync();
        PreparedTicketPublicationProtectionReader.Compare(baseline, before);
        PreparedTicketPublicationProtectedGrouping partition = Assert.Single(before.Grouping);
        Assert.Equal(Assert.Single(baseline.Inventory.Grouping).Fingerprint, partition.Fingerprint);
        Assert.Equal(2, partition.Corpus.Count);
        Assert.Single(partition.Rows, row => row.Table == "prepared_ticket_topics");
        Assert.Single(partition.Rows, row => row.Table == "prepared_ticket_topic_groups");
        Assert.Equal(2, partition.Rows.Count(row => row.Table == "prepared_ticket_topic_members"));
        Assert.Equal(1, fixture.CountLive("prepared_ticket_topics"));
        Assert.Equal(1, fixture.CountLive("prepared_ticket_topic_groups"));
        Assert.Equal(2, fixture.CountLive("prepared_ticket_topic_members"));
        AssertProtectedSelfWorkgroups(before, ticketKeys, "FHIR Infrastructure", "FHIRInfrastructure");
        string groupingBefore = fixture.ReadAuthoredAndGroupingState();

        Assert.Equal(1, fixture.Execute(
            """
            UPDATE prepared_jira_hydration SET WorkGroup = NULL
            WHERE TicketKey = @ticketKey AND JiraKey = TicketKey
            """,
            ("@ticketKey", ticketKeys[0])));
        PreparedTicketPublicationProtectedInventory withoutSelfDisplay = await fixture.ReadCurrentAsync();
        Assert.Equal(partition.Fingerprint, Assert.Single(withoutSelfDisplay.Grouping).Fingerprint);
        Assert.Equal(before.RetainedGroupingFingerprint, withoutSelfDisplay.RetainedGroupingFingerprint);
        AssertProtectedSelfWorkgroups(withoutSelfDisplay, [ticketKeys[0]], null, "FHIRInfrastructure");
        AssertProtectedSelfWorkgroups(withoutSelfDisplay, [ticketKeys[1]], "FHIR Infrastructure", "FHIRInfrastructure");
        Assert.Equal(groupingBefore, fixture.ReadAuthoredAndGroupingState());
        PreparedTicketPublicationProtectionException selfDrift =
            Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
                PreparedTicketPublicationProtectionReader.Compare(baseline, withoutSelfDisplay));
        Assert.Equal(PreparedTicketPublicationRefreshFailureCodes.OriginalOutputChanged, selfDrift.FailureCode);
        await AssertAdmissionRefusedWithoutMutationAsync(
            fixture, source, PreparedTicketPublicationRefreshFailureCodes.OriginalOutputChanged);

        // Restore the self row so the independent refusal changes only actual stored topic output.
        Assert.Equal(1, fixture.Execute(
            """
            UPDATE prepared_jira_hydration SET WorkGroup = @workGroup
            WHERE TicketKey = @ticketKey AND JiraKey = TicketKey
            """,
            ("@workGroup", "FHIR Infrastructure"), ("@ticketKey", ticketKeys[0])));
        Assert.Equal(before.ProtectedContentFingerprint, (await fixture.ReadCurrentAsync()).ProtectedContentFingerprint);
        long topicRowId = fixture.Scalar<long>("SELECT RowId FROM prepared_ticket_topics");
        if (topicDisplay is null && fixture.Scalar<int>(
            """
            SELECT "notnull" FROM pragma_table_info('prepared_ticket_topics')
            WHERE name = 'WorkGroupDisplay'
            """) == 1)
        {
            // The current schema rejects NULL before a reader can see it; do not relax that schema.
            string metadataBeforeNull = fixture.ReadPublicationMetadataState();
            SqliteException constraint = Assert.Throws<SqliteException>(() => fixture.Execute(
                "UPDATE prepared_ticket_topics SET WorkGroupDisplay = @display WHERE RowId = @rowId",
                ("@display", topicDisplay), ("@rowId", topicRowId)));
            Assert.Equal(19, constraint.SqliteErrorCode);
            Assert.Equal(1299, constraint.SqliteExtendedErrorCode);
            Assert.Contains("prepared_ticket_topics.WorkGroupDisplay", constraint.Message, StringComparison.Ordinal);
            Assert.Equal(metadataBeforeNull, fixture.ReadPublicationMetadataState());
            Assert.Equal(groupingBefore, fixture.ReadAuthoredAndGroupingState());
            PreparedTicketPublicationProtectedInventory unchanged = await fixture.ReadCurrentAsync();
            PreparedTicketPublicationProtectionReader.Compare(baseline, unchanged);
            Assert.Equal(before.ProtectedContentFingerprint, unchanged.ProtectedContentFingerprint);
            Assert.Equal(before.RetainedGroupingFingerprint, unchanged.RetainedGroupingFingerprint);
            await AssertSourceSnapshotUnchangedAsync(fixture, source);
            return;
        }
        Assert.Equal(1, fixture.Execute(
            "UPDATE prepared_ticket_topics SET WorkGroupDisplay = @display WHERE RowId = @rowId",
            ("@display", topicDisplay), ("@rowId", topicRowId)));
        Assert.Equal(topicDisplay is null ? "null" : "text",
            fixture.Scalar<string>("SELECT typeof(WorkGroupDisplay) FROM prepared_ticket_topics"));
        string metadataBefore = fixture.ReadPublicationMetadataState();

        PreparedTicketPublicationProtectionException topicError =
            await Assert.ThrowsAsync<PreparedTicketPublicationProtectionException>(() => fixture.ReadCurrentAsync());

        Assert.Equal(PreparedTicketPublicationRefreshFailureCodes.InvalidAcceptedGraph, topicError.FailureCode);
        Assert.Equal(topicDisplay is null
            ? "Required coordinate 'prepared_ticket_topics.WorkGroupDisplay' is null."
            : "Grouping has no unique workgroup display value.", topicError.Message);
        Assert.Equal(metadataBefore, fixture.ReadPublicationMetadataState());
        await AssertAdmissionRefusedWithoutMutationAsync(
            fixture, source, PreparedTicketPublicationRefreshFailureCodes.InvalidAcceptedGraph);
        await AssertSourceSnapshotUnchangedAsync(fixture, source);
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

    [Fact]
    public async Task NullableWorkgroupRefreshPreservesCumulativeCorpusAndSnapshot()
    {
        using Fixture fixture = new(richGraph: true);
        MixedWorkgroupSource mixed = await CreateMixedWorkgroupSourceAsync(fixture);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(mixed.Source.Run.Id);
        PreparedTicketPublicationProtectedInventory before = await fixture.ReadCurrentAsync();
        AssertMixedWorkgroupInventory(mixed, baseline.Inventory);
        AssertMixedWorkgroupInventory(mixed, before);
        PreparedTicketPublicationProtectionReader.Compare(baseline, before);
        string authoredBefore = fixture.ReadAuthoredAndGroupingState();
        int attemptsBefore = fixture.CountLive("authoring_run_attempts");
        string sourceSnapshotId = Assert.IsType<string>(mixed.Source.Run.SnapshotId);
        string sourcePath = Path.Combine(fixture.SnapshotDirectory, mixed.Source.Descriptor.FileName);
        string sourceDigest = await SqliteReviewSnapshotWriter.ComputeSha256Async(sourcePath);
        Assert.Equal(sourceSnapshotId, baseline.Source.SnapshotId);
        Assert.Equal(sourceDigest, baseline.Source.SnapshotSha256);
        Assert.Equal(3, attemptsBefore);
        List<string> jiraKeys = [];
        MetadataFetcher fetcher = MetadataFetcher.For(mixed.ExpectedRevisions);
        fetcher.OnJiraFetch = jiraKeys.Add;
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationMaintenanceController controller = new(service);

        AcceptedResult accepted = Assert.IsType<AcceptedResult>(
            await controller.Start(mixed.Source.Run.Id, CancellationToken.None));

        Assert.Equal(202, accepted.StatusCode);
        PreparedTicketPublicationRefreshResult started = Assert.IsType<PreparedTicketPublicationRefreshResult>(accepted.Value);
        Assert.Equal($"/processing/authoring/runs/{started.Run.RunId}", accepted.Location);
        PreparedTicketPublicationEnrichmentInput input =
            await AssertMixedRefreshAdmissionAsync(fixture, mixed, baseline, before, started);
        Assert.Equal(0, fetcher.CallCount);
        Assert.Equal(0, fetcher.ZulipCallCount);
        CountingGroupingDispatcher dispatcher = new(fixture.Database);

        AuthoringSnapshotDescriptor descriptor = Assert.IsType<AuthoringSnapshotDescriptor>(
            await fixture.CreatePostProcessor(service, dispatcher).FinalizeRunAsync(started.Run.RunId));

        Assert.Equal(0, dispatcher.CallCount);
        AssertMixedMetadataFetches(mixed, fetcher, jiraKeys);
        await AssertMixedRefreshCompletedAsync(fixture, mixed, baseline, input, started, descriptor);
        Assert.Equal(authoredBefore, fixture.ReadAuthoredAndGroupingState());
        Assert.Equal(attemptsBefore, fixture.CountLive("authoring_run_attempts"));
        Assert.Equal(sourceSnapshotId,
            Assert.IsType<AuthoringRunRecord>(await fixture.Store.GetRunAsync(mixed.Source.Run.Id)).SnapshotId);
        Assert.Equal(sourceDigest, await SqliteReviewSnapshotWriter.ComputeSha256Async(sourcePath));
    }

    [Fact]
    public async Task NullableWorkgroupRefreshRecoversCommittedMetadataWithoutRefetch()
    {
        using Fixture fixture = new(richGraph: true);
        MixedWorkgroupSource mixed = await CreateMixedWorkgroupSourceAsync(fixture);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(mixed.Source.Run.Id);
        PreparedTicketPublicationProtectedInventory before = await fixture.ReadCurrentAsync();
        AssertMixedWorkgroupInventory(mixed, baseline.Inventory);
        AssertMixedWorkgroupInventory(mixed, before);
        PreparedTicketPublicationProtectionReader.Compare(baseline, before);
        string authoredBefore = fixture.ReadAuthoredAndGroupingState();
        int attemptsBefore = fixture.CountLive("authoring_run_attempts");
        string sourcePath = Path.Combine(fixture.SnapshotDirectory, mixed.Source.Descriptor.FileName);
        string sourceDigest = await SqliteReviewSnapshotWriter.ComputeSha256Async(sourcePath);
        Assert.Equal(sourceDigest, baseline.Source.SnapshotSha256);
        List<string> jiraKeys = [];
        MetadataFetcher firstFetcher = MetadataFetcher.For(mixed.ExpectedRevisions);
        firstFetcher.OnJiraFetch = jiraKeys.Add;
        ThrowOnceAfterCommitHook hook = new();
        PreparedTicketPublicationRefreshService firstService = fixture.CreateRefreshService(firstFetcher, hook);
        PreparedTicketPublicationRefreshResult started = await firstService.StartAsync(mixed.Source.Run.Id);
        PreparedTicketPublicationEnrichmentInput input =
            await AssertMixedRefreshAdmissionAsync(fixture, mixed, baseline, before, started);
        CountingGroupingDispatcher firstDispatcher = new(fixture.Database);

        InvalidOperationException interruption = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreatePostProcessor(firstService, firstDispatcher).FinalizeRunAsync(started.Run.RunId));

        Assert.Equal("Simulated process interruption after metadata commit.", interruption.Message);
        Assert.Equal(1, hook.CallCount);
        Assert.Equal(0, firstDispatcher.CallCount);
        AssertMixedMetadataFetches(mixed, firstFetcher, jiraKeys);
        AuthoringRunRecord fenced = Assert.IsType<AuthoringRunRecord>(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(started.Run.RunId, fenced.Id);
        Assert.Equal(AuthoringStatusValues.Runs.Error, fenced.Status);
        string requestBefore = Assert.IsType<string>(fenced.RequestJson);
        AuthoringRunStageRecord metadataStage = Assert.Single(
            await fixture.Store.GetRunStagesAsync(started.Run.RunId),
            stage => stage.StageName == PreparedTicketPublicationEnrichmentContract.StageName);
        PreparedTicketPublicationRefreshReceiptRecord committed = Assert.IsType<PreparedTicketPublicationRefreshReceiptRecord>(
            await fixture.Database.GetMatchingPublicationRefreshReceiptAsync(
                started.Run.RunId, metadataStage.Id,
                PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(input), input.CorpusFingerprint));
        Assert.Equal(started.Run.RunId, committed.RunId);
        Assert.Equal(1, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
        Assert.DoesNotContain(await fixture.Store.GetSnapshotRecordsAsync(), record => record.RunId == started.Run.RunId);
        PreparedTicketPublicationProtectedInventory afterApply = await fixture.ReadCurrentAsync();
        AssertMixedWorkgroupInventory(mixed, afterApply);
        PreparedTicketPublicationProtectionReader.Compare(baseline, afterApply);
        PreparedTicketPublicationProtectionReader.ValidateFrozen(input, afterApply, started.Run.RunId);
        string metadataAfterApply = fixture.ReadPublicationMetadataState();
        Assert.Equal(authoredBefore, fixture.ReadAuthoredAndGroupingState());
        Assert.Equal(attemptsBefore, fixture.CountLive("authoring_run_attempts"));

        MetadataFetcher resumedFetcher = MetadataFetcher.For(mixed.ExpectedRevisions);
        resumedFetcher.ThrowWhenCalled = true;
        PreparedTicketPublicationRefreshService resumedService = fixture.CreateRefreshService(resumedFetcher);
        CountingGroupingDispatcher resumedDispatcher = new(fixture.Database);

        AuthoringSnapshotDescriptor descriptor = Assert.IsType<AuthoringSnapshotDescriptor>(
            await fixture.CreatePostProcessor(resumedService, resumedDispatcher).FinalizeRunAsync(started.Run.RunId));

        Assert.Equal(0, resumedFetcher.CallCount);
        Assert.Equal(0, resumedFetcher.ZulipCallCount);
        Assert.Equal(0, resumedDispatcher.CallCount);
        Assert.Equal(committed,
            await fixture.Database.GetPublicationRefreshReceiptAsync(started.Run.RunId, metadataStage.Id));
        Assert.Equal(requestBefore,
            Assert.IsType<AuthoringRunRecord>(await fixture.Store.GetRunAsync(started.Run.RunId)).RequestJson);
        Assert.Equal(metadataAfterApply, fixture.ReadPublicationMetadataState());
        await AssertMixedRefreshCompletedAsync(fixture, mixed, baseline, input, started, descriptor);
        Assert.Equal(authoredBefore, fixture.ReadAuthoredAndGroupingState());
        Assert.Equal(attemptsBefore, fixture.CountLive("authoring_run_attempts"));
        Assert.Equal(sourceDigest, await SqliteReviewSnapshotWriter.ComputeSha256Async(sourcePath));
    }

    private sealed record MixedWorkgroupSource(
        SourceResult NullableSource,
        SourceResult Source,
        string NullableTicketKey,
        string[] AttributedTicketKeys,
        IReadOnlyDictionary<string, string> ExpectedRevisions,
        IReadOnlyList<PreparedTicketPublicationCorpusItem> ExpectedCorpus);

    private static async Task<MixedWorkgroupSource> CreateMixedWorkgroupSourceAsync(Fixture fixture)
    {
        const string nullableTicketKey = "FHIR-10061";
        string[] attributedTicketKeys = ["FHIR-10062", "FHIR-10063"];
        SourceResult nullableSource;
        try
        {
            nullableSource = await CreateNoTopicWorkgroupSourceAsync(fixture, null, null, nullableTicketKey);
        }
        finally
        {
            fixture.BeforeSourceSnapshot = null;
        }
        Assert.Empty(await fixture.Database.GetRunPartitionsAsync(nullableSource.Run.Id));
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            AssertStoredSelfWorkgroups(connection, [nullableTicketKey], null, null);
            AssertNoStoredTopics(connection);
        }
        SourceResult source = await fixture.CreateSourceRunAtAsync(
            new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), attributedTicketKeys);
        Assert.Equal(1, nullableSource.Run.TotalItems);
        Assert.Equal(2, source.Run.TotalItems);
        Assert.Equal(2, (await fixture.Store.GetRunItemsAsync(source.Run.Id)).Count);
        Dictionary<string, string> revisions = nullableSource.ExpectedRevisions.Concat(source.ExpectedRevisions)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> receipts = nullableSource.ReceiptIds.Concat(source.ReceiptIds)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        AuthoringRunItemRecord[] items = (await fixture.Store.GetRunItemsAsync(nullableSource.Run.Id))
            .Concat(await fixture.Store.GetRunItemsAsync(source.Run.Id)).ToArray();
        PreparedTicketPublicationCorpusItem[] expectedCorpus = items
            .OrderBy(item => item.BusinessKey, StringComparer.Ordinal)
            .Select(item => new PreparedTicketPublicationCorpusItem(
                item.BusinessKey, receipts[item.BusinessKey], item.Id, item.RunId, "fhir", revisions[item.BusinessKey]))
            .ToArray();
        MixedWorkgroupSource mixed = new(
            nullableSource, source, nullableTicketKey, attributedTicketKeys, revisions, expectedCorpus);
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            AssertMixedWorkgroupStorage(connection, mixed);
        }
        string path = Path.Combine(fixture.SnapshotDirectory, source.Descriptor.FileName);
        await using (SqliteConnection snapshot = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(path))
        {
            AssertMixedWorkgroupStorage(snapshot, mixed);
        }
        await AssertSourceSnapshotUnchangedAsync(fixture, nullableSource);
        await AssertSourceSnapshotUnchangedAsync(fixture, source);
        return mixed;
    }

    private static void AssertMixedWorkgroupInventory(
        MixedWorkgroupSource mixed,
        PreparedTicketPublicationProtectedInventory inventory)
    {
        Assert.Equal(mixed.ExpectedCorpus, inventory.Corpus.OrderBy(item => item.TicketKey, StringComparer.Ordinal).ToArray());
        Assert.Equal(PreparedTicketPublicationContract.ComputeCorpusFingerprint(mixed.ExpectedCorpus), inventory.CorpusFingerprint);
        Assert.Equal(
            [AttributedWorkgroupPartitionKey, NullableWorkgroupPartitionKey],
            inventory.Grouping.Select(partition => partition.PartitionKey).Order(StringComparer.Ordinal).ToArray());
        PreparedTicketPublicationProtectedGrouping nullable = Assert.Single(
            inventory.Grouping, partition => partition.PartitionKey == NullableWorkgroupPartitionKey);
        PreparedTicketPublicationCorpusItem expectedNullable = Assert.Single(
            mixed.ExpectedCorpus, item => item.TicketKey == mixed.NullableTicketKey);
        Assert.Equal(expectedNullable, Assert.Single(nullable.Corpus));
        Assert.Empty(nullable.Rows);
        Assert.Equal(new PreparedTicketPublicationProtectedGroupingFingerprint(
            NullableWorkgroupPartitionKey,
            PreparedTicketPublicationContract.ComputeCorpusFingerprint([expectedNullable]),
            PreparedTicketPublicationContract.ComputeGroupingPartitionFingerprint(new PreparedTicketGroupingPayload
            {
                WorkGroupClean = "unattributed",
                WorkGroupDisplay = "unattributed",
                Specification = "FHIR",
                Type = "Change Request",
                Topics = [],
            }),
            PreparedTicketPublicationEnrichmentContract.ComputeProtectedContentFingerprint(
                Array.Empty<PreparedTicketPublicationProtectedRow>())), nullable.Fingerprint);
        PreparedTicketPublicationProtectedGrouping attributed = Assert.Single(
            inventory.Grouping, partition => partition.PartitionKey == AttributedWorkgroupPartitionKey);
        Assert.Equal(
            mixed.ExpectedCorpus.Where(item => item.TicketKey != mixed.NullableTicketKey).ToArray(),
            attributed.Corpus.OrderBy(item => item.TicketKey, StringComparer.Ordinal).ToArray());
        Assert.Single(attributed.Rows, row => row.Table == "prepared_ticket_topics");
        Assert.Single(attributed.Rows, row => row.Table == "prepared_ticket_topic_groups");
        Assert.Equal(2, attributed.Rows.Count(row => row.Table == "prepared_ticket_topic_members"));
        AssertProtectedSelfWorkgroups(inventory, [mixed.NullableTicketKey], null, null);
        AssertProtectedSelfWorkgroups(inventory, mixed.AttributedTicketKeys, "FHIR Infrastructure", "FHIRInfrastructure");
    }

    private static async Task<PreparedTicketPublicationEnrichmentInput> AssertMixedRefreshAdmissionAsync(
        Fixture fixture,
        MixedWorkgroupSource mixed,
        PreparedTicketPublicationBaseline baseline,
        PreparedTicketPublicationProtectedInventory before,
        PreparedTicketPublicationRefreshResult started)
    {
        Assert.NotEqual(mixed.Source.Run.Id, started.Run.RunId);
        Assert.NotEqual(mixed.NullableSource.Run.Id, started.Run.RunId);
        Assert.Equal(mixed.Source.Run.Id, started.Run.SourceRunId);
        Assert.Equal(AuthoringRunPurposeValues.PublicationRefresh, started.Run.Purpose);
        Assert.False(started.Run.DatabaseOnly);
        Assert.Equal(AuthoringStatusValues.Runs.Running, started.Run.Status);
        Assert.Equal(3, baseline.Source.ExportedTicketCount);
        Assert.Equal(new AuthoringRunCorpusComparison(mixed.Source.Descriptor.SnapshotId, 3, 3, 0), started.Run.CorpusComparison);
        Assert.Equal(3, started.Run.TotalItems);
        Assert.Equal(3, started.Run.CompletedItems);
        Assert.Equal(3, started.Items.Count);
        IReadOnlyList<AuthoringRunItemRecord> persistedItems = await fixture.Store.GetRunItemsAsync(started.Run.RunId);
        Assert.Equal(3, persistedItems.Count);
        foreach (PreparedTicketPublicationCorpusItem expected in mixed.ExpectedCorpus)
        {
            AuthoringRunItemStatus item = Assert.Single(started.Items, item => item.BusinessKey == expected.TicketKey);
            Assert.Equal(started.Run.RunId, item.RunId);
            Assert.Equal($"maintenance:{started.Run.RunId}:fhir", item.ItemKind);
            Assert.Equal(AuthoringStatusValues.Items.Complete, item.Status);
            Assert.Equal(expected.ReceiptId, item.AcceptedReceiptId);
            Assert.Equal(expected.ExpectedSourceRevision, item.ExpectedSourceRevision);
            Assert.Equal(0, item.AttemptCount);
            AuthoringRunItemRecord persistedItem = Assert.Single(persistedItems, value => value.Id == item.ItemId);
            Assert.Equal(item.BusinessKey, persistedItem.BusinessKey);
            Assert.Equal(item.AcceptedReceiptId, persistedItem.AcceptedReceiptId);
            Assert.Equal(item.ExpectedSourceRevision, persistedItem.ExpectedSourceRevision);
            Assert.Equal(AuthoringStatusValues.Items.Complete, persistedItem.Status);
            Assert.Equal(0, persistedItem.AttemptCount);
        }
        AuthoringRunRecord fenced = Assert.IsType<AuthoringRunRecord>(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(started.Run.RunId, fenced.Id);
        PreparedTicketPublicationEnrichmentInput input = Assert.IsType<PreparedTicketPublicationEnrichmentInput>(
            PreparerDatabase.ReadPublicationEnrichmentInput(fenced));
        Assert.Equal(PreparedTicketPublicationEnrichmentContract.RecipeName, input.RecipeName);
        Assert.Equal(1, input.RecipeVersion);
        Assert.Equal(baseline.Source, input.Source);
        Assert.Equal(baseline.Inventory.CorpusFingerprint, input.SourceCorpusFingerprint);
        Assert.Equal(mixed.ExpectedCorpus, input.Corpus.OrderBy(item => item.TicketKey, StringComparer.Ordinal).ToArray());
        Assert.Equal(before.CorpusFingerprint, input.CorpusFingerprint);
        Assert.Equal(before.ProtectedContentFingerprint, input.ProtectedContentFingerprint);
        Assert.Equal(before.RetainedGroupingFingerprint, input.RetainedGroupingFingerprint);
        Assert.Equal(
            before.Grouping.Select(partition => partition.Fingerprint).OrderBy(partition => partition.PartitionKey, StringComparer.Ordinal),
            input.Grouping.OrderBy(partition => partition.PartitionKey, StringComparer.Ordinal));
        Assert.Equal(6, input.ZulipReferences.Count);
        Assert.Equal(
            before.ZulipReferences.OrderBy(reference => reference.AssociationId, StringComparer.Ordinal),
            input.ZulipReferences.OrderBy(reference => reference.AssociationId, StringComparer.Ordinal));
        Assert.Empty(input.AdditionalTicketKeys);
        Assert.Equal(started.Run.CorpusComparison, AuthoringMaintenanceRunRequest.ReadCorpusComparison(fenced.RequestJson));
        PreparedTicketPublicationProtectionReader.ValidateFrozen(input, before);
        return input;
    }

    private static void AssertMixedMetadataFetches(
        MixedWorkgroupSource mixed,
        MetadataFetcher fetcher,
        IReadOnlyList<string> jiraKeys)
    {
        Assert.Equal(3, fetcher.CallCount);
        Assert.Equal(
            mixed.ExpectedCorpus.Select(item => item.TicketKey).ToArray(),
            jiraKeys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, fetcher.ZulipCallCount);
        Assert.Equal(
            ["12345", "stream::topic"],
            fetcher.ZulipCalls.Select(call => call.Reference).Order(StringComparer.Ordinal).ToArray());
        Assert.All(fetcher.ZulipCalls, call => Assert.Contains(call.TicketKey, mixed.ExpectedRevisions.Keys));
    }

    private static async Task AssertMixedRefreshCompletedAsync(
        Fixture fixture,
        MixedWorkgroupSource mixed,
        PreparedTicketPublicationBaseline baseline,
        PreparedTicketPublicationEnrichmentInput input,
        PreparedTicketPublicationRefreshResult started,
        AuthoringSnapshotDescriptor descriptor)
    {
        AuthoringRunRecord completed = Assert.IsType<AuthoringRunRecord>(await fixture.Store.GetRunAsync(started.Run.RunId));
        Assert.Equal(AuthoringStatusValues.Runs.Completed, completed.Status);
        Assert.Equal(AuthoringRunPurposeValues.PublicationRefresh, completed.Purpose);
        Assert.Equal(mixed.Source.Run.Id, completed.SourceRunId);
        Assert.False(completed.DatabaseOnly);
        Assert.Equal(descriptor.SnapshotId, completed.SnapshotId);
        Assert.Equal(started.Run.RunId, descriptor.RunId);
        Assert.NotEqual(mixed.Source.Descriptor.SnapshotId, descriptor.SnapshotId);
        Assert.True(descriptor.Sequence > mixed.Source.Descriptor.Sequence);
        Assert.Equal(PreparedTicketSnapshotSchemaV3.Version, descriptor.SchemaVersion);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(3, fixture.CountLive("authoring_runs"));
        Assert.Equal(
            PreparedTicketPublicationEnrichmentContract.SerializeInput(input),
            PreparedTicketPublicationEnrichmentContract.SerializeInput(
                Assert.IsType<PreparedTicketPublicationEnrichmentInput>(PreparerDatabase.ReadPublicationEnrichmentInput(completed))));
        IReadOnlyList<AuthoringRunItemRecord> items = await fixture.Store.GetRunItemsAsync(completed.Id);
        Assert.Equal(3, items.Count);
        foreach (AuthoringRunItemStatus original in started.Items)
        {
            AuthoringRunItemRecord item = Assert.Single(items, item => item.Id == original.ItemId);
            Assert.Equal(original.BusinessKey, item.BusinessKey);
            Assert.Equal(original.ItemKind, item.ItemKind);
            Assert.Equal(original.AcceptedReceiptId, item.AcceptedReceiptId);
            Assert.Equal(original.ExpectedSourceRevision, item.ExpectedSourceRevision);
            Assert.Equal(AuthoringStatusValues.Items.Complete, item.Status);
            Assert.Equal(0, item.AttemptCount);
        }
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();
        AssertMixedWorkgroupInventory(mixed, current);
        PreparedTicketPublicationProtectionReader.Compare(baseline, current);
        PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current, completed.Id);
        AssertGroupingRowsUnchanged(baseline.Inventory, current);
        Assert.Equal(6, current.ZulipReferences.Count);
        Assert.All(current.ZulipReferences, reference => Assert.NotNull(reference.HydrationId));

        IReadOnlyList<PreparedTicketRunPartition> partitions = await fixture.Database.GetRunPartitionsAsync(completed.Id);
        PreparedTicketRunPartition job = Assert.Single(partitions);
        Assert.Equal(AttributedWorkgroupPartitionKey, job.PartitionKey);
        Assert.Equal(mixed.AttributedTicketKeys, job.TicketKeys.Order(StringComparer.Ordinal).ToArray());
        PreparedTicketGroupingCertificationEvidence certification = Assert.Single(
            await fixture.Database.GetPublicationGroupingCertificationsAsync(completed.Id, partitions));
        Assert.Equal(completed.Id, certification.RefreshRunId);
        Assert.Equal(AttributedWorkgroupPartitionKey, certification.PartitionKey);
        Assert.Equal(mixed.Source.Run.Id, certification.SourceReceipt.RunId);
        Assert.Equal(certification.PartitionKey, certification.SourceReceipt.PartitionKey);
        Assert.False(certification.IsLegacyCertification);
        string originalOutputFingerprint = Assert.Single(
            baseline.Inventory.Grouping, partition => partition.PartitionKey == AttributedWorkgroupPartitionKey)
            .Fingerprint.OutputFingerprint;
        Assert.Equal(originalOutputFingerprint, certification.OutputFingerprint);
        Assert.Equal(originalOutputFingerprint, certification.SourceReceipt.OutputFingerprint);
        // Fingerprinted source receipts certify directly; private certification rows are legacy-only.
        Assert.Equal(0, fixture.CountLive("prepared_ticket_partition_certifications"));
        Assert.Null(await fixture.Database.GetPartitionCertificationAsync(completed.Id, NullableWorkgroupPartitionKey));
        Assert.Equal(1, fixture.CountLive("prepared_ticket_partition_receipts"));
        Assert.Equal(AttributedWorkgroupPartitionKey,
            fixture.Scalar<string>("SELECT PartitionKey FROM prepared_ticket_partition_receipts"));
        IReadOnlyList<AuthoringRunStageRecord> stages = await fixture.Store.GetRunStagesAsync(completed.Id);
        Assert.DoesNotContain(stages, stage => stage.PartitionKey == NullableWorkgroupPartitionKey || stage.StageName == "grouping");
        AuthoringRunStageRecord certificationStage = Assert.Single(
            stages, stage => stage.StageName == PreparerDatabase.GroupingCertificationStageName);
        Assert.Equal(AttributedWorkgroupPartitionKey, certificationStage.PartitionKey);
        Assert.Equal(job.InputFingerprint, certificationStage.InputFingerprint);
        Assert.Equal(AuthoringStatusValues.Stages.Complete, certificationStage.Status);
        AuthoringRunStageRecord metadataStage = Assert.Single(
            stages, stage => stage.StageName == PreparedTicketPublicationEnrichmentContract.StageName);
        Assert.Equal(AuthoringStatusValues.Stages.Complete, metadataStage.Status);
        Assert.NotNull(await fixture.Database.GetMatchingPublicationRefreshReceiptAsync(
            completed.Id, metadataStage.Id,
            PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(input), input.CorpusFingerprint));
        Assert.Equal(1, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));

        AuthoringSnapshotPublicationProof proof = Assert.IsType<AuthoringSnapshotPublicationProof>(descriptor.PublicationProof);
        Assert.Equal(1, proof.ContractVersion);
        Assert.Equal(PreparedTicketPublicationContract.PublicationRefreshPurpose, proof.Purpose);
        Assert.Equal(mixed.Source.Run.Id, proof.SourceRunId);
        Assert.Equal("jira", proof.SourceName);
        Assert.Equal(901, proof.SourceContentRevision);
        Assert.Equal(PublicDisplayNamePolicy.CurrentVersion, proof.PublicDisplayNamePolicyVersion);
        Assert.Equal(current.CorpusFingerprint, proof.CorpusFingerprint);
        Assert.Equal(PreparedTicketPublicationContract.ComputeGroupingFingerprint(
            [new PreparedTicketPublicationGroupingPartition(AttributedWorkgroupPartitionKey, originalOutputFingerprint)]),
            proof.GroupingFingerprint);
        AuthoringReviewSnapshotRecord record = Assert.IsType<AuthoringReviewSnapshotRecord>(
            await fixture.Store.GetReadySnapshotRecordAsync(completed.Id));
        Assert.Equal(AuthoringStatusValues.Snapshots.Ready, record.Status);
        Assert.Equal(descriptor.SnapshotId, record.Id);
        Assert.Equal(PreparedTicketSnapshotSchemaV3.Version, record.SchemaVersion);
        Assert.Equal(3, record.ItemCount);
        Assert.Equal(3, record.ReceiptCount);
        IReadOnlyList<AuthoringReviewSnapshotRecord> snapshots = await fixture.Store.GetSnapshotRecordsAsync();
        Assert.Equal(3, snapshots.Count);
        Assert.Equal(record.Id, Assert.Single(snapshots, snapshot => snapshot.RunId == completed.Id).Id);
        SqliteReviewSnapshotValidationResult validation = await SqliteReviewSnapshotValidator.ValidateAsync(record, requireReady: true);
        Assert.True(validation.IsValid, validation.Error);
        Assert.Equal(descriptor.Sha256, validation.ChecksumSha256);
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            AssertMixedWorkgroupStorage(connection, mixed);
            AssertRefreshedMixedMetadata(connection, mixed);
        }
        await using (SqliteConnection snapshot = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(record.Path))
        {
            PreparedTicketPublicationProtectedInventory exported =
                await PreparedTicketPublicationProtectionReader.ReadSnapshotAsync(snapshot, record.SchemaVersion);
            AssertMixedWorkgroupInventory(mixed, exported);
            AssertGroupingRowsUnchanged(baseline.Inventory, exported);
            AssertMixedWorkgroupStorage(snapshot, mixed);
            AssertRefreshedMixedMetadata(snapshot, mixed);
        }
        await AssertSourceSnapshotUnchangedAsync(fixture, mixed.NullableSource);
        await AssertSourceSnapshotUnchangedAsync(fixture, mixed.Source);
        Assert.False(File.Exists(record.Path + "-wal"));
        Assert.False(File.Exists(record.Path + "-shm"));
    }

    private static void AssertGroupingRowsUnchanged(
        PreparedTicketPublicationProtectedInventory before,
        PreparedTicketPublicationProtectedInventory after)
    {
        Assert.Equal(before.RetainedGroupingFingerprint, after.RetainedGroupingFingerprint);
        foreach (PreparedTicketPublicationProtectedGrouping original in before.Grouping)
        {
            PreparedTicketPublicationProtectedGrouping retained = Assert.Single(
                after.Grouping, partition => partition.PartitionKey == original.PartitionKey);
            Assert.Equal(original.Rows.Count, retained.Rows.Count);
            foreach (PreparedTicketPublicationProtectedRow row in original.Rows)
            {
                PreparedTicketPublicationProtectedRow actual = Assert.Single(
                    retained.Rows, value => value.Table == row.Table && value.Scope == row.Scope && value.Key == row.Key);
                Assert.Equal(
                    row.Values.OrderBy(value => value.Column, StringComparer.Ordinal),
                    actual.Values.OrderBy(value => value.Column, StringComparer.Ordinal));
            }
        }
    }

    private static void AssertMixedWorkgroupStorage(SqliteConnection connection, MixedWorkgroupSource mixed)
    {
        Assert.Equal(3, ScalarInt(connection, "SELECT COUNT(*) FROM prepared_tickets"));
        AssertStoredSelfWorkgroups(connection, [mixed.NullableTicketKey], null, null);
        AssertStoredSelfWorkgroups(connection, mixed.AttributedTicketKeys, "FHIR Infrastructure", "FHIRInfrastructure");
        Assert.Equal(1, ScalarInt(connection, "SELECT COUNT(*) FROM prepared_ticket_topics"));
        Assert.Equal(1, ScalarInt(connection, "SELECT COUNT(*) FROM prepared_ticket_topic_groups"));
        Assert.Equal(2, ScalarInt(connection, "SELECT COUNT(*) FROM prepared_ticket_topic_members"));
        using SqliteCommand membership = connection.CreateCommand();
        membership.CommandText = "SELECT COUNT(*) FROM prepared_ticket_topic_members WHERE TicketKey = @ticketKey";
        membership.Parameters.AddWithValue("@ticketKey", mixed.NullableTicketKey);
        Assert.Equal(0L, membership.ExecuteScalar());
        foreach (PreparedTicketPublicationCorpusItem expected in mixed.ExpectedCorpus)
        {
            Assert.Equal(
                (expected.ReceiptId, expected.ExpectedSourceRevision, expected.ExpectedSourceRevision, expected.ExpectedSourceRevision),
                ReadAcceptedRevisionCoordinates(connection, expected.ContributingRunId, expected.TicketKey));
        }
    }

    private static void AssertRefreshedMixedMetadata(SqliteConnection connection, MixedWorkgroupSource mixed)
    {
        foreach (PreparedTicketPublicationCorpusItem expected in mixed.ExpectedCorpus)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT p.Reporter, p.Assignee, p.SourceProject, p.SourceContentRevision, p.PublicDisplayNamePolicyVersion,
                       j.Reporter, j.Assignee, j.PublicDisplayNamePolicyVersion,
                       r.DisplayName, r.PublicDisplayNamePolicyVersion, p.SourceLastSuccessfulRefreshAt, j.UpdatedAt
                FROM prepared_ticket_hydration p
                INNER JOIN prepared_jira_hydration j ON j.TicketKey = p.TicketKey AND j.JiraKey = p.TicketKey
                INNER JOIN prepared_ticket_in_person_requesters r ON r.TicketKey = p.TicketKey
                WHERE p.TicketKey = @ticketKey
                """;
            command.Parameters.AddWithValue("@ticketKey", expected.TicketKey);
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal($"Reporter {expected.TicketKey}", reader.GetString(0));
            Assert.Equal($"Assignee {expected.TicketKey}", reader.GetString(1));
            Assert.Equal("FHIR", reader.GetString(2));
            Assert.Equal(901L, reader.GetInt64(3));
            Assert.Equal(PublicDisplayNamePolicy.CurrentVersion, reader.GetInt32(4));
            Assert.Equal($"Reporter {expected.TicketKey}", reader.GetString(5));
            Assert.Equal($"Assignee {expected.TicketKey}", reader.GetString(6));
            Assert.Equal(PublicDisplayNamePolicy.CurrentVersion, reader.GetInt32(7));
            Assert.Equal($"Requester {expected.TicketKey}", reader.GetString(8));
            Assert.Equal(PublicDisplayNamePolicy.CurrentVersion, reader.GetInt32(9));
            Assert.Equal("2026-09-14T12:00:00.0000000+00:00", reader.GetString(10));
            Assert.Equal(expected.ExpectedSourceRevision, reader.GetString(11));
            Assert.False(reader.Read());
        }
    }

    private static Task<SourceResult> CreateNoTopicWorkgroupSourceAsync(
        Fixture fixture,
        string? workGroup,
        string? workGroupClean,
        params string[] ticketKeys)
    {
        fixture.BeforeSourceSnapshot = _ => SetSelfWorkgroups(fixture, workGroup, workGroupClean, ticketKeys);
        return fixture.CreateSourceRunAsync(ticketKeys);
    }

    private static async Task<SourceResult> CreateNoTopicReaderSourceAsync(
        Fixture fixture,
        int schemaVersion,
        string? workGroup,
        string? workGroupClean,
        params string[] ticketKeys)
    {
        // These reader-only shapes hit ordinary grouping's separate canonical-clean boundary.
        // Accept real outputs and write their first verified snapshot directly, without grouping
        // stages, receipts, or certification. Null/null still uses ordinary finalization above.
        AuthoringRunRecord run = await fixture.Store.CreateRunAsync(
            "jira-fhir",
            ticketKeys.Select(key => new AuthoringRunItemDefinition(
                key, "fhir", "2026-09-01T00:00:00.0000000+00:00")).ToArray(),
            databaseOnly: false);
        Assert.True(await fixture.Store.TryAcquireMutationFenceAsync("jira-fhir", run.Id));
        IReadOnlyList<AuthoringRunItemRecord> items = await fixture.Store.GetRunItemsAsync(run.Id);
        Dictionary<string, string> receiptIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (AuthoringRunItemRecord item in items)
        {
            AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
                await fixture.Store.ClaimItemAsync(run.Id, item.Id));
            PreparedTicketPayload payload = new()
            {
                Key = item.BusinessKey,
                RequestSummary = "Request",
                ProposalA = "A",
                ProposalAImpact = PreparedTicketImpactValues.NonSubstantive,
                ProposalB = "B",
                ProposalBImpact = PreparedTicketImpactValues.NonSubstantive,
                ProposalC = "C",
                Recommendation = PreparedTicketRecommendationValues.ProposalA,
                RecommendationJustification = "Because",
            };
            string hash = PreparedTicketAuthoringDtos.ComputeContentHash(payload);
            AuthoringReceiptAcceptance acceptance = await fixture.Store.AcceptResultAsync(
                new(run.Id, item.Id, claim.OperationId, item.ExpectedSourceRevision, hash),
                claim.OperationToken,
                (connection, ct) => fixture.Database.SavePreparedTicketForAuthoringAsync(
                    connection, payload, hash, run.Id, item.Id, claim.OperationId, ct));
            receiptIds.Add(item.BusinessKey, acceptance.Receipt.ReceiptId);
            DateTimeOffset hydratedAt = new(2002, 2, 2, 0, 0, 0, TimeSpan.Zero);
            await fixture.Database.SaveHydrationAsync(new PreparedTicketHydrationBatch(
                item.BusinessKey,
                new(item.BusinessKey, null, null, null, "FHIR", null, null, null, null, null,
                    0, "Description", hydratedAt, "resolved", null),
                [
                    new(item.BusinessKey, item.BusinessKey, $"Title {item.BusinessKey}", "Triaged",
                        "Change Request", null, null, null, workGroup, "FHIR", hydratedAt, null,
                        hydratedAt, "resolved", null),
                ],
                [], [], [], []));
            await fixture.Store.MarkItemCompleteAsync(item.Id, acceptance.Receipt.ReceiptId);
        }
        SetSelfWorkgroups(fixture, workGroup, workGroupClean, ticketKeys);
        await fixture.Store.MarkRunFinalizingAsync(run.Id);
        SqliteReviewSnapshotWriter writer = new(fixture.Database.OpenConnection, fixture.Store);
        AuthoringSnapshotDescriptor descriptor = await writer.WriteAsync(new SqliteReviewSnapshotRequest(
            "jira-fhir", run.Id, fixture.SnapshotDirectory, schemaVersion, items.Count,
            await fixture.Database.GetSnapshotReceiptCountAsync(run.Id),
            await fixture.Database.GetSnapshotTableCountsAsync(schemaVersion),
            new PreparedTicketSnapshotSanitizer(run.Id, schemaVersion)));
        await fixture.Store.CompleteRunAsync(run.Id, descriptor.SnapshotId);
        Assert.Equal(0, fixture.CountLive("prepared_ticket_partition_receipts"));
        Assert.Equal(0, fixture.CountLive("prepared_ticket_partition_certifications"));
        Assert.Null(descriptor.PublicationProof);
        return new(
            Assert.IsType<AuthoringRunRecord>(await fixture.Store.GetRunAsync(run.Id)),
            descriptor,
            items.ToDictionary(item => item.BusinessKey, item => item.ExpectedSourceRevision, StringComparer.OrdinalIgnoreCase),
            receiptIds);
    }

    private static void SetSelfWorkgroups(
        Fixture fixture,
        string? workGroup,
        string? workGroupClean,
        IReadOnlyList<string> ticketKeys)
    {
        foreach (string ticketKey in ticketKeys)
        {
            Assert.Equal(1, fixture.Execute(
                """
                UPDATE prepared_jira_hydration
                SET WorkGroup = @workGroup, WorkGroupClean = @workGroupClean
                WHERE TicketKey = @ticketKey AND JiraKey = TicketKey
                """,
                ("@workGroup", workGroup), ("@workGroupClean", workGroupClean), ("@ticketKey", ticketKey)));
        }
    }

    private static async Task AssertAdmissionRefusedWithoutMutationAsync(
        Fixture fixture,
        SourceResult source,
        string expectedCode)
    {
        int runs = fixture.CountLive("authoring_runs");
        int requests = fixture.Scalar<int>("SELECT COUNT(*) FROM authoring_runs WHERE RequestJson IS NOT NULL");
        int attempts = fixture.CountLive("authoring_run_attempts");
        int snapshots = fixture.CountLive("authoring_review_snapshots");
        string metadataBefore = fixture.ReadPublicationMetadataState();
        string authoredBefore = fixture.ReadAuthoredAndGroupingState();
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ThrowWhenCalled = true;
        PreparedTicketPublicationMaintenanceController controller = new(fixture.CreateRefreshService(fetcher));

        ConflictObjectResult response = Assert.IsType<ConflictObjectResult>(
            await controller.Start(source.Run.Id, CancellationToken.None));

        Assert.Equal(409, response.StatusCode);
        PreparedTicketPublicationRefreshFailure failure = Assert.IsType<PreparedTicketPublicationRefreshFailure>(response.Value);
        Assert.Equal(expectedCode, failure.Error);
        Assert.Equal(runs, fixture.CountLive("authoring_runs"));
        Assert.Equal(requests, fixture.Scalar<int>("SELECT COUNT(*) FROM authoring_runs WHERE RequestJson IS NOT NULL"));
        Assert.Equal(attempts, fixture.CountLive("authoring_run_attempts"));
        Assert.Equal(snapshots, fixture.CountLive("authoring_review_snapshots"));
        Assert.Equal(metadataBefore, fixture.ReadPublicationMetadataState());
        Assert.Equal(authoredBefore, fixture.ReadAuthoredAndGroupingState());
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(0, fetcher.CallCount);
        Assert.Equal(0, fetcher.ZulipCallCount);
    }

    private static async Task AssertSourceSnapshotUnchangedAsync(Fixture fixture, SourceResult source)
    {
        string path = Path.Combine(fixture.SnapshotDirectory, source.Descriptor.FileName);
        Assert.Equal(source.Descriptor.Sha256, await SqliteReviewSnapshotWriter.ComputeSha256Async(path));
        Assert.Equal(source.Descriptor.SnapshotId,
            Assert.IsType<AuthoringRunRecord>(await fixture.Store.GetRunAsync(source.Run.Id)).SnapshotId);
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
    }

    private static void AssertStoredSelfWorkgroups(
        SqliteConnection connection,
        IReadOnlyList<string> ticketKeys,
        string? workGroup,
        string? workGroupClean)
    {
        foreach (string ticketKey in ticketKeys)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT WorkGroup, typeof(WorkGroup), WorkGroupClean, typeof(WorkGroupClean), Specification, Type
                FROM prepared_jira_hydration
                WHERE TicketKey = @ticketKey AND JiraKey = TicketKey
                """;
            command.Parameters.AddWithValue("@ticketKey", ticketKey);
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(workGroup is null, reader.IsDBNull(0));
            Assert.Equal(workGroup, reader.IsDBNull(0) ? null : reader.GetString(0));
            Assert.Equal(workGroup is null ? "null" : "text", reader.GetString(1));
            Assert.Equal(workGroupClean is null, reader.IsDBNull(2));
            Assert.Equal(workGroupClean, reader.IsDBNull(2) ? null : reader.GetString(2));
            Assert.Equal(workGroupClean is null ? "null" : "text", reader.GetString(3));
            Assert.Equal("FHIR", reader.GetString(4));
            Assert.Equal("Change Request", reader.GetString(5));
            Assert.False(reader.Read());
        }
    }

    private static void AssertProtectedSelfWorkgroups(
        PreparedTicketPublicationProtectedInventory inventory,
        IReadOnlyList<string> ticketKeys,
        string? workGroup,
        string? workGroupClean)
    {
        foreach (string ticketKey in ticketKeys)
        {
            PreparedTicketPublicationProtectedRow row = Assert.Single(inventory.Rows, row =>
                row.Table == "prepared_jira_hydration" && row.Scope == ticketKey &&
                row.Values.Any(value => value.Column == "JiraKey" && value.Value == ticketKey));
            Assert.Equal(
                new PreparedTicketPublicationProtectedValue("WorkGroup", workGroup is null ? "null" : "text", workGroup),
                Assert.Single(row.Values, value => value.Column == "WorkGroup"));
            Assert.Equal(
                new PreparedTicketPublicationProtectedValue("WorkGroupClean", workGroupClean is null ? "null" : "text", workGroupClean),
                Assert.Single(row.Values, value => value.Column == "WorkGroupClean"));
        }
    }

    private static void AssertNoStoredTopics(SqliteConnection connection)
    {
        Assert.Equal(0, ScalarInt(connection, "SELECT COUNT(*) FROM prepared_ticket_topics"));
        Assert.Equal(0, ScalarInt(connection, "SELECT COUNT(*) FROM prepared_ticket_topic_groups"));
        Assert.Equal(0, ScalarInt(connection, "SELECT COUNT(*) FROM prepared_ticket_topic_members"));
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
