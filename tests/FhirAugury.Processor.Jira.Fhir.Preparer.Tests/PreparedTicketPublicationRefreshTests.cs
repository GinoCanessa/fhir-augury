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

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

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
            await service.StartAsync(source.Run.Id);

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
        fetcher.ObservedRevisions["FHIR-710"] = "changed";
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
            "Ordinary re-authoring is required",
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

    private sealed record SourceResult(
        AuthoringRunRecord Run,
        AuthoringSnapshotDescriptor Descriptor,
        IReadOnlyDictionary<string, string> ExpectedRevisions,
        IReadOnlyDictionary<string, string> ReceiptIds);

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly JiraProcessingSourceTicketStore _sourceStore;
        private readonly JiraAuthoringRunCoordinator _coordinator;
        private readonly IOptions<PreparerServiceOptions> _options;

        public Fixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                $"fhir-augury-publication-refresh-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            string databasePath =
                Path.Combine(_directory, "preparer.db");
            SnapshotDirectory =
                Path.Combine(_directory, "snapshots");
            Database = new PreparerDatabase(
                databasePath,
                NullLogger<PreparerDatabase>.Instance);
            Database.Initialize();
            Store = new AuthoringRunStore(Database);
            _sourceStore =
                new JiraProcessingSourceTicketStore(databasePath);
            IOptions<JiraProcessingOptions> jiraOptions =
                Options.Create(
                    new JiraProcessingOptions
                    {
                        AgentCliCommand = "agent {ticketKey}",
                        JiraSourceAddress = "http://source",
                        SourceTicketShape = "fhir",
                        TicketStatusesToProcess = ["Triaged"],
                    });
            _coordinator = new JiraAuthoringRunCoordinator(
                Store,
                _sourceStore,
                new JiraProcessingFilterResolver(),
                jiraOptions);
            _options = Options.Create(
                new PreparerServiceOptions
                {
                    SnapshotDirectory = SnapshotDirectory,
                    SnapshotSchemaVersion =
                        PreparedTicketSnapshotSchemaV3.Version,
                });
            ActivateAsync().GetAwaiter().GetResult();
        }

        public PreparerDatabase Database { get; }
        public AuthoringRunStore Store { get; }
        public string SnapshotDirectory { get; }

        public async Task<SourceResult> CreateSourceRunAsync(
            params string[] ticketKeys)
            => await CreateSourceRunAtAsync(
                new DateTimeOffset(
                    2026,
                    9,
                    1,
                    0,
                    0,
                    0,
                    TimeSpan.Zero),
                ticketKeys);

        public async Task<SourceResult> CreateSourceRunAtAsync(
            DateTimeOffset sourceUpdatedAt,
            params string[] ticketKeys)
        {
            List<JiraProcessingSourceTicketRecord> sources = [];
            foreach (string ticketKey in ticketKeys)
            {
                sources.Add(
                    await _sourceStore.UpsertAsync(
                        new JiraIssueSummaryEntry
                        {
                            Key = ticketKey,
                            ProjectKey = "FHIR",
                            Title = $"Title {ticketKey}",
                            Type = "Change Request",
                            Status = "Triaged",
                            WorkGroup = "FHIR-I",
                            Specification = "FHIR",
                            UpdatedAt = sourceUpdatedAt,
                        },
                        "fhir",
                        false,
                        new DateTimeOffset(
                            2001,
                            1,
                            1,
                            0,
                            0,
                            0,
                            TimeSpan.Zero),
                        1,
                        CancellationToken.None));
            }

            JiraAuthoringRunCreation creation =
                await _coordinator.CreateExplicitRunAsync(
                    sources,
                    databaseOnly: false);
            Assert.True(await Store.TryAcquireMutationFenceAsync(
                _coordinator.ProcessorKind,
                creation.Run.Id));
            Dictionary<string, string> revisions =
                new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> receiptIds =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (AuthoringRunItemRecord item in creation.Items)
            {
                revisions[item.BusinessKey] =
                    item.ExpectedSourceRevision;
                AuthoringOperationClaim claim =
                    Assert.IsType<AuthoringOperationClaim>(
                        await Store.ClaimItemAsync(
                            creation.Run.Id,
                            item.Id));
                PreparedTicketPayload payload =
                    CreatePayload(item.BusinessKey);
                string contentHash =
                    PreparedTicketAuthoringDtos.ComputeContentHash(
                        payload);
                AuthoringReceiptAcceptance acceptance =
                    await Store.AcceptResultAsync(
                        new AuthoringResultSubmission(
                            creation.Run.Id,
                            item.Id,
                            claim.OperationId,
                            item.ExpectedSourceRevision,
                            contentHash),
                        claim.OperationToken,
                        (connection, ct) =>
                            Database.SavePreparedTicketForAuthoringAsync(
                                connection,
                                payload,
                                contentHash,
                                creation.Run.Id,
                                item.Id,
                                claim.OperationId,
                                ct));
                receiptIds[item.BusinessKey] =
                    acceptance.Receipt.ReceiptId;
                await Database.SaveHydrationAsync(
                    CreateHydration(item.BusinessKey));
                await Store.MarkItemCompleteAsync(
                    item.Id,
                    acceptance.Receipt.ReceiptId);
            }

            AuthoringSnapshotDescriptor descriptor =
                Assert.IsType<AuthoringSnapshotDescriptor>(
                    await CreateOrdinaryPostProcessor()
                        .FinalizeRunAsync(creation.Run.Id));
            return new SourceResult(
                (await Store.GetRunAsync(creation.Run.Id))!,
                descriptor,
                revisions,
                receiptIds);
        }

        public PreparedTicketPublicationRefreshService
            CreateRefreshService(
                MetadataFetcher fetcher,
                IPreparedTicketPublicationRefreshInterruptionHook?
                    hook = null,
                AuthoringRunSchedulerWakeSignal? wakeSignal = null)
        {
            AuthoringRetryPolicy retryPolicy = new(
                Options.Create(new ProcessingServiceOptions()));
            return new PreparedTicketPublicationRefreshService(
                Database,
                Store,
                new AuthoringRunControlService(Store, retryPolicy),
                _coordinator,
                fetcher,
                wakeSignal ?? new AuthoringRunSchedulerWakeSignal(),
                NullLogger<
                    PreparedTicketPublicationRefreshService>.Instance,
                hook);
        }

        public PreparedTicketRunPostProcessor CreatePostProcessor(
            PreparedTicketPublicationRefreshService service,
            IPreparedTicketGroupingDispatcher? dispatcher = null)
            => CreatePostProcessorCore(
                dispatcher ?? new RejectingGroupingDispatcher(),
                service);

        public void MakeGroupingReceiptsLegacy(string runId)
        {
            using SqliteConnection connection =
                Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE prepared_ticket_partition_receipts
                SET OutputFingerprint = NULL
                WHERE RunId = @runId
                """;
            command.Parameters.AddWithValue("@runId", runId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public int CountLive(string table)
        {
            using SqliteConnection connection =
                Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public void SimulateReadySnapshotBeforeRunCompletion(
            string runId)
        {
            using SqliteConnection connection =
                Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE authoring_runs
                SET Status = 'finalizing',
                    SnapshotId = NULL,
                    CompletedAt = NULL
                WHERE Id = @runId;
                INSERT INTO authoring_mutation_fences(
                    ProcessorKind, RunId, LeaseId, AcquiredAt)
                VALUES(
                    'jira-fhir', @runId, @leaseId, @acquiredAt);
                """;
            command.Parameters.AddWithValue("@runId", runId);
            command.Parameters.AddWithValue(
                "@leaseId",
                Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue(
                "@acquiredAt",
                DateTimeOffset.UtcNow.ToString("O"));
            Assert.Equal(2, command.ExecuteNonQuery());
        }

        public string ReadAuthoredAndGroupingState()
            => DumpTables(
                "prepared_tickets",
                "prepared_ticket_repos",
                "prepared_ticket_related_jira",
                "prepared_ticket_related_zulip",
                "prepared_ticket_related_github",
                "prepared_ticket_topics",
                "prepared_ticket_topic_groups",
                "prepared_ticket_topic_members",
                "prepared_ticket_partition_receipts");

        public string ReadPublicationMetadataState()
            => DumpTables(
                "prepared_ticket_hydration",
                "prepared_jira_hydration",
                "prepared_ticket_in_person_requesters",
                "authoring_run_input_provenance");

        private PreparedTicketRunPostProcessor
            CreateOrdinaryPostProcessor()
            => CreatePostProcessorCore(
                new SavingGroupingDispatcher(Database),
                publicationRefreshService: null);

        private PreparedTicketRunPostProcessor CreatePostProcessorCore(
            IPreparedTicketGroupingDispatcher dispatcher,
            PreparedTicketPublicationRefreshService?
                publicationRefreshService)
        {
            HttpClient workGroupClient =
                new(new WorkGroupHandler())
                {
                    BaseAddress = new Uri("http://localhost/"),
                };
            SqliteReviewSnapshotReconciler reconciler = new(Store);
            return new PreparedTicketRunPostProcessor(
                Database,
                Store,
                new AuthoringRunFinalizer(Store),
                reconciler,
                _coordinator,
                new OrchestratorWorkGroupCatalogFetcher(
                    workGroupClient),
                dispatcher,
                _options,
                publicationRefreshService,
                new PreparedTicketSnapshotMaterializer(
                    Database,
                    Store,
                    reconciler,
                    _options));
        }

        private async Task ActivateAsync()
        {
            await Store.EnsureProcessorModeAsync(
                _coordinator.ProcessorKind);
            await Store.TransitionProcessorModeAsync(
                _coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.Legacy,
                AuthoringStatusValues.ProcessorModes.CuttingOver);
            await Store.TransitionProcessorModeAsync(
                _coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.CuttingOver,
                AuthoringStatusValues.ProcessorModes.RunBacked);
        }

        private string DumpTables(params string[] tables)
        {
            using SqliteConnection connection =
                Database.OpenConnection();
            List<string> results = [];
            foreach (string table in tables)
            {
                using SqliteCommand command =
                    connection.CreateCommand();
                command.CommandText =
                    $"SELECT * FROM {table} ORDER BY RowId";
                using SqliteDataReader reader =
                    command.ExecuteReader();
                List<string> rows = [];
                while (reader.Read())
                {
                    rows.Add(
                        string.Join(
                            "\u001f",
                            Enumerable.Range(0, reader.FieldCount)
                                .Select(index =>
                                    reader.IsDBNull(index)
                                        ? "<null>"
                                        : Convert.ToString(
                                            reader.GetValue(index),
                                            System.Globalization
                                                .CultureInfo
                                                .InvariantCulture) ??
                                          string.Empty)));
                }
                results.Add($"{table}\n{string.Join("\n", rows)}");
            }
            return string.Join("\n--\n", results);
        }

        public void Dispose()
        {
            Database.Dispose();
            SqliteConnection.ClearAllPools();
            TestFileCleanup.SafeDeleteDirectory(_directory);
        }
    }

    private sealed class MetadataFetcher
        : OrchestratorHydrationFetcher
    {
        private MetadataFetcher(
            IReadOnlyDictionary<string, string> expectedRevisions)
            : base(
                new HttpClient(),
                NullLogger.Instance)
        {
            ObservedRevisions =
                new Dictionary<string, string>(
                    expectedRevisions,
                    StringComparer.OrdinalIgnoreCase);
            ContentRevisions = expectedRevisions.Keys.ToDictionary(
                key => key,
                _ => 901L,
                StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<string, string> ObservedRevisions { get; }
        public Dictionary<string, long> ContentRevisions { get; }
        public int CallCount { get; private set; }
        public bool ThrowWhenCalled { get; set; }

        public static MetadataFetcher For(
            IReadOnlyDictionary<string, string> expectedRevisions)
            => new(expectedRevisions);

        public override Task<PublicationMetadataFetchResult>
            FetchPublicationMetadataAsync(
                string ticketKey,
                DateTimeOffset hydratedAt,
                CancellationToken ct)
        {
            CallCount++;
            if (ThrowWhenCalled)
            {
                throw new InvalidOperationException(
                    "Metadata fetch should have been skipped.");
            }
            string revision = ObservedRevisions[ticketKey];
            return Task.FromResult(
                new PublicationMetadataFetchResult(
                    ticketKey,
                    hydratedAt,
                    revision,
                    $"Reporter {ticketKey}",
                    $"Assignee {ticketKey}",
                    [$"Requester {ticketKey}"],
                    ticketKey[..ticketKey.IndexOf(
                        '-',
                        StringComparison.Ordinal)],
                    new DateTimeOffset(
                        2026,
                        9,
                        14,
                        12,
                        0,
                        0,
                        TimeSpan.Zero),
                    ContentRevisions[ticketKey],
                    true,
                    PublicDisplayNamePolicy.CurrentVersion,
                    Failure: null));
        }
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

    private sealed class SavingGroupingDispatcher(
        PreparerDatabase database)
        : IPreparedTicketGroupingDispatcher
    {
        public async Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
        {
            await database.SaveGroupingForRunAsync(
                new PreparedTicketGroupingPayload
                {
                    WorkGroupClean = partition.WorkGroupClean,
                    WorkGroupDisplay = partition.WorkGroupDisplay,
                    Specification = partition.Specification,
                    Type = partition.Type,
                    Topics = [],
                },
                runId,
                lease.StageId,
                lease.LeaseId,
                partition.InputFingerprint,
                ct);
        }
    }

    private sealed class CountingGroupingDispatcher(
        PreparerDatabase database)
        : IPreparedTicketGroupingDispatcher
    {
        public int CallCount { get; private set; }

        public async Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
        {
            CallCount++;
            await new SavingGroupingDispatcher(database)
                .ReplaceGroupingAsync(
                    runId,
                    partition,
                    lease,
                    ct);
        }
    }

    private sealed class RejectingGroupingDispatcher
        : IPreparedTicketGroupingDispatcher
    {
        public Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "Publication refresh dispatched grouping work.");
    }

    private sealed class WorkGroupHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"workGroups":[{"code":"fhir-i","name":"FHIR Infrastructure","retired":false,"totalFileCount":1,"totalArtifactCount":1,"repos":[]}]}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                });
    }

    private static PreparedTicketPayload CreatePayload(string key)
        => new()
        {
            Key = key,
            RequestSummary = "Request",
            ProposalA = "A",
            ProposalAImpact =
                PreparedTicketImpactValues.NonSubstantive,
            ProposalB = "B",
            ProposalBImpact =
                PreparedTicketImpactValues.NonSubstantive,
            ProposalC = "C",
            Recommendation =
                PreparedTicketRecommendationValues.ProposalA,
            RecommendationJustification = "Because",
        };

    private static PreparedTicketHydrationBatch CreateHydration(
        string key)
    {
        DateTimeOffset hydratedAt =
            new(2002, 2, 2, 0, 0, 0, TimeSpan.Zero);
        return new PreparedTicketHydrationBatch(
            key,
            new PreparedTicketHydrationRow(
                key,
                "Major",
                "Persuasive",
                "Resolution",
                "FHIR",
                null,
                null,
                null,
                null,
                null,
                0,
                "Description",
                hydratedAt,
                "resolved",
                null,
                Reporter: "Legacy Reporter",
                Assignee: "Legacy Assignee",
                SourceProject: "FHIR",
                SourceLastSuccessfulRefreshAt:
                    hydratedAt.AddDays(-1),
                SourceContentRevision: 2,
                PublicDisplayNamePolicyVersion:
                    PublicDisplayNamePolicy.CurrentVersion),
            [
                new PreparedJiraHydrationRow(
                    key,
                    key,
                    $"Title {key}",
                    "Triaged",
                    "Change Request",
                    "Major",
                    "Persuasive",
                    "Resolution",
                    "FHIR Infrastructure",
                    "FHIR",
                    hydratedAt,
                    $"https://jira/{key}",
                    hydratedAt,
                    "resolved",
                    null,
                    Reporter: "Legacy Reporter",
                    Assignee: "Legacy Assignee",
                    PublicDisplayNamePolicyVersion:
                        PublicDisplayNamePolicy.CurrentVersion),
            ],
            [],
            [],
            [],
            [],
            [
                new PreparedTicketInPersonRequesterRow(
                    key,
                    "Legacy Requester",
                    PublicDisplayNamePolicy.CurrentVersion),
            ]);
    }
}
