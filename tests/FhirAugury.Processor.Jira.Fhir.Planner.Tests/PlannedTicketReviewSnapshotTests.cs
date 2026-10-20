using System.Net;
using System.Text;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Api;
using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;
using FhirAugury.Processor.Jira.Fhir.Planner.Controllers;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Planner.Processing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannedTicketReviewSnapshotTests
{
    [Fact]
    public async Task FinalizeRunProducesSanitizedCurrentRunSnapshot()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await fixture.CreateCompletedRunAsync();

        AuthoringSnapshotDescriptor descriptor =
            (await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id))!;
        using SqliteConnection snapshot = OpenReadOnly(
            Path.Combine(fixture.SnapshotDirectory, descriptor.FileName));

        Assert.Equal("ok", Scalar<string>(snapshot, "PRAGMA integrity_check"));
        Assert.Equal(
            0,
            Scalar<int>(
                snapshot,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('authoring_run_attempts', 'authoring_processor_modes', 'planned_ticket_authoring_state', 'planned_ticket_applier_projection_pending')"));
        Assert.Equal(
            "<p>request</p>",
            Scalar<string>(
                snapshot,
                "SELECT DescriptionHtml FROM planned_ticket_jira_content WHERE TicketKey = 'FHIR-1'"));
        Assert.Equal(item.Id, Scalar<string>(snapshot, "SELECT Id FROM authoring_run_items"));
        Assert.Equal(1, Scalar<int>(snapshot, "SELECT COUNT(*) FROM planned_ticket_partition_receipts"));
        Assert.Equal(1, Scalar<int>(snapshot, "SELECT COUNT(*) FROM jira_review_workgroups"));
    }

    [Fact]
    public async Task SnapshotPreservesValidSingletonTopic()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) = await fixture.CreateCompletedRunAsync();

        AuthoringSnapshotDescriptor descriptor =
            (await fixture.CreatePostProcessor(
                new SingletonGroupingDispatcher(fixture.Database)).FinalizeRunAsync(run.Id))!;
        using SqliteConnection snapshot = OpenReadOnly(
            Path.Combine(fixture.SnapshotDirectory, descriptor.FileName));

        Assert.Equal(1, Scalar<int>(snapshot, "SELECT COUNT(*) FROM planned_ticket_topics"));
        Assert.Equal(1, Scalar<int>(snapshot, "SELECT COUNT(*) FROM planned_ticket_topic_members"));
    }

    [Fact]
    public async Task FinalizeRun_AllSupersededItemsCompletesNormally()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateSupersededRunAsync(databaseOnly: true);

        AuthoringSnapshotDescriptor? descriptor =
            await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id);

        Assert.Null(descriptor);
        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
        Assert.Null(await fixture.AuthoringStore.GetFencedRunAsync("jira-fhir"));
    }

    [Fact]
    public async Task FinalizeRun_InitialRevalidationRetiresSupersededLegacyRows()
    {
        using Fixture fixture = new(activate: false);
        await fixture.Database.SavePlannedTicketAsync(new PlannedTicketPayload
        {
            Key = "FHIR-1",
            Resolution = "Persuasive",
            ResolutionSummary = "summary",
            FeatureProposal = "proposal",
            DesignRationale = "rationale",
        });
        await fixture.Database.ClassifyLegacyPlannedTicketsAsync();
        await fixture.ActivateAsync();
        (AuthoringRunRecord run, _) =
            await fixture.CreateSupersededRunAsync(databaseOnly: true);
        fixture.MarkAsInitialRevalidation(run.Id);

        await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id);

        Assert.Equal(0, await fixture.Database.CountLegacyUnverifiedAsync());
        AuthoringProcessorModeRecord mode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        Assert.False(mode.RevalidationRequired);
        using SqliteConnection connection = fixture.Database.OpenConnection();
        Assert.Equal(
            "superseded",
            Scalar<string>(
                connection,
                "SELECT Classification FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
    }

    [Fact]
    public async Task InitialRevalidation_SupersededPredecessorAndLaterReplacement_CompletesCutover()
    {
        using Fixture fixture = new(activate: false);
        await fixture.Database.SavePlannedTicketAsync(CreatePayload("FHIR-1"));
        await fixture.Database.SavePlannedTicketAsync(CreatePayload("FHIR-2"));
        await fixture.Database.SavePlannedTicketAsync(CreatePayload("FHIR-3"));
        Assert.Equal(
            3,
            await fixture.Database.ClassifyLegacyPlannedTicketsAsync());
        await fixture.ActivateAsync();
        DateTimeOffset firstRevision =
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord sourceA =
            await fixture.SeedSourceAsync("FHIR-1", firstRevision);
        JiraProcessingSourceTicketRecord sourceB =
            await fixture.SeedSourceAsync("FHIR-2", firstRevision);
        JiraProcessingSourceTicketRecord sourceC =
            await fixture.SeedSourceAsync("FHIR-3", firstRevision);
        JiraAuthoringRunCreation initial =
            await fixture.CreateRunAsync(
                [sourceA, sourceB, sourceC],
                databaseOnly: true);
        fixture.MarkAsInitialRevalidation(initial.Run.Id);
        AuthoringRunItemRecord itemA =
            initial.Items.Single(item => item.BusinessKey == "FHIR-1");
        AuthoringRunItemRecord itemC =
            initial.Items.Single(item => item.BusinessKey == "FHIR-3");
        AuthoringOperationClaim claimA =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    initial.Run.Id,
                    itemA.Id));
        await fixture.AuthoringStore.MarkClaimErrorAsync(
            itemA.Id,
            claimA.OperationId,
            "not actionable");
        await fixture.AuthoringStore.SupersedeErroredItemAsync(
            initial.Run.Id,
            itemA.Id,
            "not actionable");
        await fixture.CompleteItemAsync(
            initial.Run,
            itemC,
            CreatePayload("FHIR-3"));

        string predecessorReceiptHash;
        string predecessorOperationId;
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            predecessorReceiptHash = Scalar<string>(
                connection,
                "SELECT ReceiptContentHash FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-3'");
            predecessorOperationId = Scalar<string>(
                connection,
                "SELECT OperationId FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-3'");
        }

        await fixture.SeedSourceAsync(
            "FHIR-2",
            firstRevision.AddDays(1),
            title: "Updated B");
        Assert.True(await fixture.ReplaceStaleInitialRunAsync(initial.Run.Id));
        AuthoringProcessorModeRecord replacementMode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        AuthoringRunRecord replacement = Assert.IsType<AuthoringRunRecord>(
            await fixture.AuthoringStore.GetRunAsync(
                replacementMode.RevalidationRunId!));
        AuthoringRunItemRecord replacementB = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(replacement.Id));
        Assert.Equal("FHIR-2", replacementB.BusinessKey);
        AuthoringOperationClaim replacementClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    replacement.Id,
                    replacementB.Id));
        await fixture.AuthoringStore.MarkClaimErrorAsync(
            replacementB.Id,
            replacementClaim.OperationId,
            "not actionable");
        await fixture.AuthoringStore.SupersedeErroredItemAsync(
            replacement.Id,
            replacementB.Id,
            "not actionable");
        IReadOnlyList<AuthoringRunItemRecord> terminalLineage =
            await fixture.AuthoringStore.GetRevalidationSupersededItemsAsync(
                replacement.Id);
        string fingerprintInput = string.Join(
            "\n",
            terminalLineage.Select(item =>
                $"{item.RunId}:{item.Id}:{item.BusinessKey}:{item.ItemKind}:{item.ExpectedSourceRevision}"));
        string expectedFingerprint =
            AuthoringResultHasher.HashNormalizedUtf8(fingerprintInput);

        AuthoringSnapshotDescriptor? descriptor =
            await fixture.CreatePostProcessor().FinalizeRunAsync(
                replacement.Id);

        Assert.NotNull(descriptor);
        Assert.Equal(
            AuthoringStatusValues.Runs.Completed,
            (await fixture.AuthoringStore.GetRunAsync(replacement.Id))!.Status);
        Assert.Null(
            await fixture.AuthoringStore.GetFencedRunAsync("jira-fhir"));
        AuthoringProcessorModeRecord completedMode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        Assert.False(completedMode.RevalidationRequired);
        Assert.Null(completedMode.RevalidationRunId);
        AuthoringRunStageRecord retirementStage = Assert.Single(
            await fixture.AuthoringStore.GetRunStagesAsync(replacement.Id),
            stage => stage.StageName == "revalidation-retirement");
        Assert.Equal(expectedFingerprint, retirementStage.InputFingerprint);
        Assert.Contains(itemA.Id, fingerprintInput, StringComparison.Ordinal);
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            Assert.Equal(
                "superseded",
                Scalar<string>(
                    connection,
                    "SELECT Classification FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
            Assert.Equal(
                initial.Run.Id,
                Scalar<string>(
                    connection,
                    "SELECT RunId FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
            Assert.Equal(
                itemA.Id,
                Scalar<string>(
                    connection,
                    "SELECT RunItemId FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
            Assert.Equal(
                "receipt-backed",
                Scalar<string>(
                    connection,
                    "SELECT Classification FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-3'"));
            Assert.Equal(
                predecessorReceiptHash,
                Scalar<string>(
                    connection,
                    "SELECT ReceiptContentHash FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-3'"));
            Assert.Equal(
                predecessorOperationId,
                Scalar<string>(
                    connection,
                    "SELECT OperationId FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-3'"));
        }
    }

    [Fact]
    public async Task ReplacementRunPartitionsIncludeRetainedPredecessorTickets()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync();
        fixture.MarkAsInitialRevalidation(run.Id);
        AuthoringRunRecord replacement =
            await fixture.AuthoringStore.ReplaceRevalidationRunAsync(
                "jira-fhir",
                run.Id,
                [new("FHIR-2", "fhir", "revision-2")]);

        PlannedTicketRunPartition partition = Assert.Single(
            await fixture.Database.GetRunPartitionsAsync(replacement.Id));

        Assert.Equal("FHIRInfrastructure", partition.WorkGroupClean);
        Assert.Equal(["FHIR-1"], partition.TicketKeys);
    }

    [Fact]
    public async Task FinalizeRunRefusesGroupingWithoutDurableReceipt()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) = await fixture.CreateCompletedRunAsync();

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.CreatePostProcessor(
                    new NoOpGroupingDispatcher()).FinalizeRunAsync(run.Id));

        Assert.Contains("durable receipt", error.Message, StringComparison.Ordinal);
        AuthoringRunStageRecord grouping = Assert.Single(
            await fixture.AuthoringStore.GetRunStagesAsync(run.Id),
            stage => stage.StageName == "grouping");
        Assert.Equal(AuthoringStatusValues.Stages.Error, grouping.Status);
    }

    [Fact]
    public void PreviewGroupingDispatcherUsesSupportedPromptInvocation()
    {
        PreviewPlannedTicketGroupingDispatcher dispatcher = new(
            Options.Create(new PlannerServiceOptions()),
            NullLogger<PreviewPlannedTicketGroupingDispatcher>.Instance);
        PlannedTicketRunPartition partition = new(
            "FHIRInfrastructure",
            "FHIR Infrastructure",
            "FHIR",
            "Change Request",
            PlannerDatabase.GetPartitionKey(
                "FHIRInfrastructure",
                "FHIR",
                "Change Request"),
            "fingerprint",
            ["FHIR-1"]);

        System.Diagnostics.ProcessStartInfo startInfo =
            dispatcher.CreateStartInfo(
                "run-1",
                partition,
                new AuthoringRunStageLease("stage-1", "lease-1", 1));

        Assert.Equal(
            ["-p", "/planner-topic-groupings", "--allow-all"],
            startInfo.ArgumentList);
        Assert.Equal(
            "1",
            startInfo.Environment["FHIR_AUGURY_GROUPING_WORKER"]);
        Assert.Equal(
            "http://localhost:5172",
            startInfo.Environment["FHIR_AUGURY_GROUPING_PROCESSOR_URL"]);
    }

    [Fact]
    public async Task RunScopedGroupingEndpointReturnsMatchingDurableReceipt()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) = await fixture.CreateCompletedRunAsync();
        PlannedTicketRunPartition partition = Assert.Single(
            await fixture.Database.GetRunPartitionsAsync(run.Id));
        AuthoringRunStageRecord stage =
            await fixture.AuthoringStore.EnsureRunStageAsync(
                run.Id,
                "grouping",
                partition.PartitionKey,
                partition.InputFingerprint);
        AuthoringRunStageLease lease = Assert.IsType<AuthoringRunStageLease>(
            await fixture.AuthoringStore.TryStartRunStageAsync(stage.Id));
        PlannedTicketTopicsController controller =
            new(fixture.Database, fixture.AuthoringStore);

        IActionResult result = await controller.PutTopics(
            new PlannedTicketTopicGroupingRequest
            {
                WorkGroupClean = partition.WorkGroupClean,
                WorkGroupDisplay = partition.WorkGroupDisplay,
                Specification = partition.Specification,
                Type = partition.Type,
                Authoring = new PlannedTicketGroupingStageContext(
                    run.Id,
                    stage.Id,
                    lease.LeaseId,
                    partition.InputFingerprint),
            },
            CancellationToken.None);

        AuthoringRunStageReceipt receipt =
            Assert.IsType<AuthoringRunStageReceipt>(
                Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(run.Id, receipt.RunId);
        Assert.Equal(stage.Id, receipt.StageId);
        Assert.Equal(partition.PartitionKey, receipt.PartitionKey);
        Assert.Equal(partition.InputFingerprint, receipt.InputFingerprint);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static PlannedTicketPayload CreatePayload(string key)
        => new()
        {
            Key = key,
            Resolution = "Persuasive",
            ResolutionSummary = "summary",
            FeatureProposal = "proposal",
            DesignRationale = "rationale",
        };

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly JiraProcessingSourceTicketStore _sourceStore;
        private readonly JiraAuthoringRunCoordinator _coordinator;

        public Fixture(bool activate = true)
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                $"fhir-augury-planner-snapshot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            string databasePath = Path.Combine(_directory, "planner.db");
            SnapshotDirectory = Path.Combine(_directory, "snapshots");
            Database = new PlannerDatabase(databasePath, NullLogger<PlannerDatabase>.Instance);
            Database.Initialize();
            AuthoringStore = new AuthoringRunStore(Database);
            _sourceStore = new JiraProcessingSourceTicketStore(databasePath);
            _coordinator = new JiraAuthoringRunCoordinator(
                AuthoringStore,
                _sourceStore,
                new JiraConfiguredTicketSelector(_sourceStore, new TestJiraTicketLabelMatcher()),
                new JiraProcessingFilterResolver(),
                Options.Create(new JiraProcessingOptions
                {
                    AgentCliCommand = "agent {ticketKey}",
                    JiraSourceAddress = "http://source",
                    SourceTicketShape = "fhir",
                    TicketStatusesToProcess = ["Resolved - change required"],
                }));
            if (activate)
            {
                ActivateAsync().GetAwaiter().GetResult();
            }
        }

        public PlannerDatabase Database { get; }
        public AuthoringRunStore AuthoringStore { get; }
        public string SnapshotDirectory { get; }

        public async Task ActivateAsync()
        {
            await AuthoringStore.EnsureProcessorModeAsync(_coordinator.ProcessorKind);
            AuthoringProcessorModeRecord mode =
                await AuthoringStore.GetProcessorModeAsync(_coordinator.ProcessorKind);
            if (mode.Mode == AuthoringStatusValues.ProcessorModes.Legacy)
            {
                await AuthoringStore.TransitionProcessorModeAsync(
                    _coordinator.ProcessorKind,
                    AuthoringStatusValues.ProcessorModes.Legacy,
                    AuthoringStatusValues.ProcessorModes.CuttingOver);
                await AuthoringStore.TransitionProcessorModeAsync(
                    _coordinator.ProcessorKind,
                    AuthoringStatusValues.ProcessorModes.CuttingOver,
                    AuthoringStatusValues.ProcessorModes.RunBacked);
            }
        }

        public Task<JiraProcessingSourceTicketRecord> SeedSourceAsync(
            string key,
            DateTimeOffset revision,
            string title = "Title")
            => _sourceStore.UpsertAsync(
                new JiraIssueSummaryEntry
                {
                    Key = key,
                    ProjectKey = "FHIR",
                    Title = title,
                    Type = "Change Request",
                    Status = "Resolved - change required",
                    WorkGroup = "FHIR Infrastructure",
                    Specification = "FHIR",
                    UpdatedAt = revision,
                },
                "fhir",
                false,
                CancellationToken.None);

        public async Task<JiraAuthoringRunCreation> CreateRunAsync(
            IReadOnlyCollection<JiraProcessingSourceTicketRecord> sources,
            bool databaseOnly)
        {
            JiraAuthoringRunCreation creation =
                await _coordinator.CreateExplicitRunAsync(
                    sources,
                    databaseOnly);
            Assert.True(await AuthoringStore.TryAcquireMutationFenceAsync(
                _coordinator.ProcessorKind,
                creation.Run.Id));
            return creation;
        }

        public Task<bool> ReplaceStaleInitialRunAsync(string runId)
            => _coordinator.SupersedeStaleItemsAsync(runId);

        public async Task CompleteItemAsync(
            AuthoringRunRecord run,
            AuthoringRunItemRecord item,
            PlannedTicketPayload payload)
        {
            AuthoringOperationClaim claim =
                Assert.IsType<AuthoringOperationClaim>(
                    await AuthoringStore.ClaimItemAsync(
                        run.Id,
                        item.Id));
            string hash = PlannedTicketAuthoringDtos.ComputeContentHash(
                payload);
            AuthoringReceiptAcceptance receipt =
                await AuthoringStore.AcceptResultAsync(
                    new AuthoringResultSubmission(
                        run.Id,
                        item.Id,
                        claim.OperationId,
                        item.ExpectedSourceRevision,
                        hash),
                    claim.OperationToken,
                    (connection, ct) =>
                        Database.SavePlannedTicketForAuthoringAsync(
                            connection,
                            payload,
                            hash,
                            run.Id,
                            item.Id,
                            claim.OperationId,
                            ct));
            await AuthoringStore.MarkItemCompleteAsync(
                item.Id,
                receipt.Receipt.ReceiptId);
        }

        public async Task<(AuthoringRunRecord Run, AuthoringRunItemRecord Item)> CreateCompletedRunAsync()
        {
            JiraProcessingSourceTicketRecord source = await _sourceStore.UpsertAsync(
                new JiraIssueSummaryEntry
                {
                    Key = "FHIR-1",
                    ProjectKey = "FHIR",
                    Title = "Title",
                    Type = "Change Request",
                    Status = "Resolved - change required",
                    WorkGroup = "FHIR Infrastructure",
                    Specification = "FHIR",
                    UpdatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                },
                "fhir",
                false,
                CancellationToken.None);
            JiraAuthoringRunCreation creation =
                await _coordinator.CreateOneItemRunAsync(source, databaseOnly: false);
            Assert.True(await AuthoringStore.TryAcquireMutationFenceAsync(
                _coordinator.ProcessorKind,
                creation.Run.Id));
            AuthoringRunItemRecord item = Assert.Single(creation.Items);
            AuthoringOperationClaim claim =
                (await AuthoringStore.ClaimItemAsync(creation.Run.Id, item.Id))!;
            PlannedTicketPayload payload = new()
            {
                Key = "FHIR-1",
                Resolution = "Persuasive",
                ResolutionSummary = "summary",
                FeatureProposal = "proposal",
                DesignRationale = "rationale",
            };
            string hash = PlannedTicketAuthoringDtos.ComputeContentHash(payload);
            AuthoringReceiptAcceptance receipt = await AuthoringStore.AcceptResultAsync(
                new AuthoringResultSubmission(
                    creation.Run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    hash),
                claim.OperationToken,
                (connection, ct) => Database.SavePlannedTicketForAuthoringAsync(
                    connection,
                    payload,
                    hash,
                    creation.Run.Id,
                    item.Id,
                    claim.OperationId,
                    ct));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await Database.SaveHydrationAsync(
                new HydrationBatch(
                    "FHIR-1",
                    new HydrationTicketRow(
                        "FHIR-1",
                        "Major",
                        "Persuasive",
                        "resolution",
                        "FHIR",
                        null,
                        null,
                        null,
                        null,
                        null,
                        0,
                        "request",
                        now,
                        "resolved",
                        null,
                        "<p>request</p>",
                        "<p>resolution</p>",
                        "Ada",
                        now.AddDays(-1),
                        null,
                        null),
                    [
                        new HydrationJiraRow(
                            "FHIR-1",
                            "FHIR-1",
                            "Title",
                            "Resolved - change required",
                            "Change Request",
                            "Major",
                            "Persuasive",
                            "resolution",
                            "FHIR Infrastructure",
                            "FHIR",
                            now,
                            "https://jira/FHIR-1",
                            now,
                            "resolved",
                            null),
                    ],
                    [],
                    [],
                    [],
                    []),
                CancellationToken.None);
            await AuthoringStore.MarkItemCompleteAsync(item.Id, receipt.Receipt.ReceiptId);
            return (creation.Run, item);
        }

        public async Task<(AuthoringRunRecord Run, AuthoringRunItemRecord Item)> CreateSupersededRunAsync(
            bool databaseOnly)
        {
            JiraProcessingSourceTicketRecord source = await _sourceStore.UpsertAsync(
                new JiraIssueSummaryEntry
                {
                    Key = "FHIR-1",
                    ProjectKey = "FHIR",
                    Title = "Title",
                    Type = "Change Request",
                    Status = "Resolved - change required",
                    WorkGroup = "FHIR Infrastructure",
                    Specification = "FHIR",
                    UpdatedAt = new DateTimeOffset(
                        2026,
                        9,
                        1,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                },
                "fhir",
                false,
                CancellationToken.None);
            JiraAuthoringRunCreation creation =
                await _coordinator.CreateOneItemRunAsync(source, databaseOnly);
            Assert.True(await AuthoringStore.TryAcquireMutationFenceAsync(
                _coordinator.ProcessorKind,
                creation.Run.Id));
            AuthoringRunItemRecord item = Assert.Single(creation.Items);
            AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
                await AuthoringStore.ClaimItemAsync(creation.Run.Id, item.Id));
            await AuthoringStore.MarkClaimErrorAsync(
                item.Id,
                claim.OperationId,
                "worker failure");
            await AuthoringStore.SupersedeErroredItemAsync(
                creation.Run.Id,
                item.Id,
                "not actionable");
            return (creation.Run, item);
        }

        public void MarkAsInitialRevalidation(string runId)
        {
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE authoring_processor_modes
                SET RevalidationRequired = 1,
                    RevalidationRunId = @runId
                WHERE ProcessorKind = 'jira-fhir'
                """;
            command.Parameters.AddWithValue("@runId", runId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public PlannedTicketRunPostProcessor CreatePostProcessor(
            IPlannedTicketGroupingDispatcher? groupingDispatcher = null)
        {
            HttpClient client = new(new WorkGroupHandler())
            {
                BaseAddress = new Uri("http://localhost/"),
            };
            return new PlannedTicketRunPostProcessor(
                Database,
                AuthoringStore,
                new AuthoringRunFinalizer(AuthoringStore),
                new SqliteReviewSnapshotReconciler(AuthoringStore),
                _coordinator,
                new OrchestratorWorkGroupCatalogFetcher(client),
                groupingDispatcher ?? new EmptyGroupingDispatcher(Database),
                Options.Create(new PlannerServiceOptions
                {
                    SnapshotDirectory = SnapshotDirectory,
                    SnapshotSchemaVersion = 1,
                    ReconcileSnapshotsOnStartup = true,
                }));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class SingletonGroupingDispatcher(PlannerDatabase database)
        : IPlannedTicketGroupingDispatcher
    {
        public Task ReplaceGroupingAsync(
            string runId,
            PlannedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
            => database.SaveTopicGroupingForRunAsync(
                new PlannedTicketTopicGroupingPayload
                {
                    WorkGroupClean = partition.WorkGroupClean,
                    WorkGroupDisplay = partition.WorkGroupDisplay,
                    Specification = partition.Specification,
                    Type = partition.Type,
                    Topics =
                    [
                        new PlannedTicketTopicPayload
                        {
                            ShortDescription = "Singleton",
                            LongerDescription = "One independently applicable ticket.",
                            RemainingTicketKeys = [partition.TicketKeys.Single()],
                        },
                    ],
                },
                runId,
                lease.StageId,
                lease.LeaseId,
                partition.InputFingerprint,
                ct);
    }

    private sealed class EmptyGroupingDispatcher(PlannerDatabase database)
        : IPlannedTicketGroupingDispatcher
    {
        public Task ReplaceGroupingAsync(
            string runId,
            PlannedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
            => database.SaveTopicGroupingForRunAsync(
                new PlannedTicketTopicGroupingPayload
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

    private sealed class NoOpGroupingDispatcher
        : IPlannedTicketGroupingDispatcher
    {
        public Task ReplaceGroupingAsync(
            string runId,
            PlannedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class WorkGroupHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"workGroups":[{"code":"fhir-i","name":"FHIR Infrastructure","retired":false,"totalFileCount":1,"totalArtifactCount":1,"repos":[]}]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
    }
}
