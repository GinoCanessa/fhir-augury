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
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketReviewSnapshotTests
{
    [Fact]
    public async Task FinalizeRun_ProducesSecretFreeCanonicalSnapshot()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await fixture.CreateCompletedRunAsync(databaseOnly: false);
        PreparedTicketRunPostProcessor postProcessor = fixture.CreatePostProcessor();

        AuthoringSnapshotDescriptor descriptor =
            (await postProcessor.FinalizeRunAsync(run.Id))!;

        Assert.Equal(
            AuthoringStatusValues.Runs.Completed,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
        Assert.Equal(1, descriptor.TableCounts["prepared_ticket_partition_receipts"]);
        string snapshotPath = Path.Combine(fixture.SnapshotDirectory, descriptor.FileName);
        using SqliteConnection snapshot = OpenReadOnly(snapshotPath);
        Assert.Equal("ok", Scalar<string>(snapshot, "PRAGMA integrity_check"));
        Assert.Equal(
            0,
            Scalar<int>(
                snapshot,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('authoring_run_attempts', 'authoring_processor_modes', 'prepared_ticket_authoring_state')"));
        Assert.Equal(
            "<p>request</p>",
            Scalar<string>(
                snapshot,
                "SELECT DescriptionHtml FROM prepared_ticket_jira_content WHERE TicketKey = 'FHIR-1'"));
        Assert.Equal(
            "Patient",
            Scalar<string>(
                snapshot,
                "SELECT Value FROM prepared_ticket_artifacts WHERE TicketKey = 'FHIR-1'"));
        Assert.Equal(
            "patient.html",
            Scalar<string>(
                snapshot,
                "SELECT Value FROM prepared_ticket_pages WHERE TicketKey = 'FHIR-1'"));
        Assert.Equal(1, Scalar<int>(snapshot, "SELECT COUNT(*) FROM prepared_ticket_partition_receipts"));
        Assert.Equal(1, Scalar<int>(snapshot, "SELECT COUNT(*) FROM jira_review_workgroups"));
        Assert.Equal(item.Id, Scalar<string>(snapshot, "SELECT Id FROM authoring_run_items"));
    }

    [Fact]
    public async Task FinalizeRun_BlocksSnapshotUntilLegacyRowsAreRevalidated()
    {
        using Fixture fixture = new(activate: false);
        await fixture.Database.SavePreparedTicketAsync(CreatePayload("FHIR-999"));
        await fixture.Database.ClassifyLegacyPreparedTicketsAsync();
        await fixture.ActivateAsync();
        (AuthoringRunRecord run, _) = await fixture.CreateCompletedRunAsync(databaseOnly: false);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.CreatePostProcessor().FinalizeRunAsync(run.Id));

        Assert.Contains("legacy revalidation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task FinalizeRun_ResumesRunAlreadyMarkedFinalizing()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: true);
        await fixture.AuthoringStore.MarkRunFinalizingAsync(run.Id);

        AuthoringSnapshotDescriptor? descriptor =
            await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id);

        Assert.Null(descriptor);
        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
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
        await fixture.Database.SavePreparedTicketAsync(CreatePayload("FHIR-1"));
        await fixture.Database.ClassifyLegacyPreparedTicketsAsync();
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
                "SELECT Classification FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
    }

    [Fact]
    public async Task InitialRevalidation_SupersededPredecessorAndLaterReplacement_CompletesCutover()
    {
        using Fixture fixture = new(activate: false);
        await fixture.Database.SavePreparedTicketAsync(CreatePayload("FHIR-1"));
        await fixture.Database.SavePreparedTicketAsync(CreatePayload("FHIR-2"));
        await fixture.Database.SavePreparedTicketAsync(CreatePayload("FHIR-3"));
        Assert.Equal(
            3,
            await fixture.Database.ClassifyLegacyPreparedTicketsAsync());
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
                "SELECT ReceiptContentHash FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-3'");
            predecessorOperationId = Scalar<string>(
                connection,
                "SELECT OperationId FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-3'");
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
        string expectedFingerprint = AuthoringResultHasher.HashNormalizedUtf8(
            string.Join(
                "\n",
                terminalLineage.Select(item =>
                    $"{item.RunId}:{item.Id}:{item.BusinessKey}:{item.ItemKind}:{item.ExpectedSourceRevision}")));

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
        Assert.Contains(
            itemA.Id,
            string.Join(
                "\n",
                terminalLineage.Select(item =>
                    $"{item.RunId}:{item.Id}:{item.BusinessKey}:{item.ItemKind}:{item.ExpectedSourceRevision}")),
            StringComparison.Ordinal);
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            Assert.Equal(
                "superseded",
                Scalar<string>(
                    connection,
                    "SELECT Classification FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
            Assert.Equal(
                initial.Run.Id,
                Scalar<string>(
                    connection,
                    "SELECT RunId FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
            Assert.Equal(
                itemA.Id,
                Scalar<string>(
                    connection,
                    "SELECT RunItemId FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
            Assert.Equal(
                "receipt-backed",
                Scalar<string>(
                    connection,
                    "SELECT Classification FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-3'"));
            Assert.Equal(
                predecessorReceiptHash,
                Scalar<string>(
                    connection,
                    "SELECT ReceiptContentHash FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-3'"));
            Assert.Equal(
                predecessorOperationId,
                Scalar<string>(
                    connection,
                    "SELECT OperationId FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-3'"));
        }
    }

    [Fact]
    public async Task FinalizeRun_ReplacesInitialRevalidationChangedDuringStages()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: false);
        fixture.MarkAsInitialRevalidation(run.Id);

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(() =>
                fixture.CreatePostProcessor(
                    new SourceChangingGroupingDispatcher(
                        fixture.Database,
                        fixture.AdvanceSourceRevisionAsync))
                    .FinalizeRunAsync(run.Id));

        Assert.Equal(AuthoringConflictCode.SourceRevisionMismatch, conflict.Code);
        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
        AuthoringProcessorModeRecord mode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        Assert.True(mode.RevalidationRequired);
        Assert.NotEqual(run.Id, mode.RevalidationRunId);
        AuthoringRunItemRecord replacement = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(mode.RevalidationRunId!));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero).ToString("O"),
            replacement.ExpectedSourceRevision);
    }

    [Fact]
    public async Task FinalizeRun_RejectsTamperedReceiptProvenance()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: false);
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE authoring_result_receipts
                SET ObservedSourceRevision = 'forged'
                WHERE RunId = @runId
                """;
            command.Parameters.AddWithValue("@runId", run.Id);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.CreatePostProcessor().FinalizeRunAsync(run.Id));

        Assert.Contains(
            "valid current receipt provenance",
            error.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            AuthoringStatusValues.Runs.Running,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task ReplacementRunPartitionsIncludeRetainedPredecessorTickets()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: false);
        fixture.MarkAsInitialRevalidation(run.Id);
        AuthoringRunRecord replacement =
            await fixture.AuthoringStore.ReplaceRevalidationRunAsync(
                "jira-fhir",
                run.Id,
                [new("FHIR-2", "fhir", "revision-2")]);

        PreparedTicketRunPartition partition = Assert.Single(
            await fixture.Database.GetRunPartitionsAsync(replacement.Id));

        Assert.Equal("FHIRInfrastructure", partition.WorkGroupClean);
        Assert.Equal(["FHIR-1"], partition.TicketKeys);
    }

    [Fact]
    public async Task FinalizeRun_RefusesGroupingWithoutDurableReceipt()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: true);

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
    public void PreviewGroupingDispatcher_UsesSupportedPromptInvocation()
    {
        PreviewPreparedTicketGroupingDispatcher dispatcher = new(
            Options.Create(new PreparerServiceOptions()),
            NullLogger<PreviewPreparedTicketGroupingDispatcher>.Instance);
        PreparedTicketRunPartition partition = new(
            "FHIRInfrastructure",
            "FHIR Infrastructure",
            "FHIR",
            "Change Request",
            PreparerDatabase.GetPartitionKey(
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
            ["-p", "/topic-groupings", "--allow-all"],
            startInfo.ArgumentList);
        Assert.Equal(
            "1",
            startInfo.Environment["FHIR_AUGURY_GROUPING_WORKER"]);
        Assert.Equal(
            "http://localhost:5171",
            startInfo.Environment["FHIR_AUGURY_GROUPING_PROCESSOR_URL"]);
    }

    [Fact]
    public async Task RunScopedGroupingEndpointReturnsMatchingDurableReceipt()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: true);
        PreparedTicketRunPartition partition = Assert.Single(
            await fixture.Database.GetRunPartitionsAsync(run.Id));
        AuthoringRunStageRecord stage =
            await fixture.AuthoringStore.EnsureRunStageAsync(
                run.Id,
                "grouping",
                partition.PartitionKey,
                partition.InputFingerprint);
        AuthoringRunStageLease lease = Assert.IsType<AuthoringRunStageLease>(
            await fixture.AuthoringStore.TryStartRunStageAsync(stage.Id));
        PreparedTicketGroupingsController controller =
            new(fixture.Database, fixture.AuthoringStore);

        ActionResult<PreparedTicketGroupingSaveResultDto> result =
            await controller.PutPartition(
                partition.WorkGroupClean,
                partition.Specification,
                partition.Type,
                new PreparedTicketGroupingPutRequest(
                    partition.WorkGroupDisplay,
                    [],
                    new PreparedTicketGroupingStageContext(
                        run.Id,
                        stage.Id,
                        lease.LeaseId,
                        partition.InputFingerprint)),
                CancellationToken.None);

        PreparedTicketGroupingSaveResultDto body =
            Assert.IsType<PreparedTicketGroupingSaveResultDto>(
                Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(run.Id, body.AuthoringReceipt!.RunId);
        Assert.Equal(stage.Id, body.AuthoringReceipt.StageId);
        Assert.Equal(partition.PartitionKey, body.AuthoringReceipt.PartitionKey);
        Assert.Equal(
            partition.InputFingerprint,
            body.AuthoringReceipt.InputFingerprint);
    }

    [Fact]
    public async Task SnapshotFiltering_ReclassifiesSurvivorFromInvalidLinkedGroup()
    {
        using Fixture fixture = new(activate: false);
        await fixture.Database.SavePreparedTicketAsync(CreatePayload("FHIR-2"));
        using (SqliteConnection cleanup = fixture.Database.OpenConnection())
        using (SqliteCommand clearState = cleanup.CreateCommand())
        {
            clearState.CommandText =
                "DELETE FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-2'";
            clearState.ExecuteNonQuery();
        }
        await fixture.ActivateAsync();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: false);
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO prepared_ticket_topics(
                    Id, WorkGroupClean, WorkGroupDisplay, Specification, Type,
                    ShortDescription, LongerDescription, RenderOrderHint, SavedAt)
                VALUES(
                    'topic-1', 'Other', 'Other', 'Other',
                    'Other', 'Topic', 'Topic', NULL, @at);
                INSERT INTO prepared_ticket_topic_groups(
                    Id, TopicRowId, FirstTicketKey, Rationale, OrderInTopic, SavedAt)
                SELECT 'group-1', RowId, 'FHIR-2', 'linked', 0, @at
                FROM prepared_ticket_topics WHERE Id = 'topic-1';
                INSERT INTO prepared_ticket_topic_members(
                    Id, TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer)
                SELECT 'member-1', t.RowId, g.RowId, 'FHIR-2', 0
                FROM prepared_ticket_topics t, prepared_ticket_topic_groups g
                WHERE t.Id = 'topic-1' AND g.Id = 'group-1';
                INSERT INTO prepared_ticket_topic_members(
                    Id, TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer)
                SELECT 'member-2', t.RowId, g.RowId, 'FHIR-1', 1
                FROM prepared_ticket_topics t, prepared_ticket_topic_groups g
                WHERE t.Id = 'topic-1' AND g.Id = 'group-1';
                """;
            command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        AuthoringSnapshotDescriptor descriptor =
            (await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id))!;
        using SqliteConnection snapshot = OpenReadOnly(
            Path.Combine(fixture.SnapshotDirectory, descriptor.FileName));

        Assert.Equal(1, Scalar<int>(snapshot, "SELECT COUNT(*) FROM prepared_tickets"));
        Assert.Equal(0, Scalar<int>(snapshot, "SELECT COUNT(*) FROM prepared_ticket_topic_groups"));
        Assert.Equal(0, Scalar<int>(snapshot, "SELECT COUNT(*) FROM prepared_ticket_topic_members"));
        Assert.Equal(0, Scalar<int>(snapshot, "SELECT COUNT(*) FROM prepared_ticket_topics"));
    }

    private static PreparedTicketPayload CreatePayload(string key)
        => new()
        {
            Key = key,
            RequestSummary = "Request",
            ProposalA = "A",
            ProposalAImpact = PreparedTicketImpactValues.NonSubstantive,
            ProposalB = "B",
            ProposalBImpact = PreparedTicketImpactValues.NonSubstantive,
            ProposalC = "C",
            Recommendation = PreparedTicketRecommendationValues.ProposalA,
            RecommendationJustification = "Because",
        };

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

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly JiraProcessingSourceTicketStore _sourceStore;
        private readonly JiraAuthoringRunCoordinator _coordinator;

        public Fixture(bool activate = true)
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                $"fhir-augury-preparer-snapshot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            string databasePath = Path.Combine(_directory, "preparer.db");
            SnapshotDirectory = Path.Combine(_directory, "snapshots");
            Database = new PreparerDatabase(
                databasePath,
                NullLogger<PreparerDatabase>.Instance);
            Database.Initialize();
            AuthoringStore = new AuthoringRunStore(Database);
            _sourceStore = new JiraProcessingSourceTicketStore(databasePath);
            IOptions<JiraProcessingOptions> jiraOptions = Options.Create(new JiraProcessingOptions
            {
                AgentCliCommand = "agent {ticketKey}",
                JiraSourceAddress = "http://source",
                SourceTicketShape = "fhir",
                TicketStatusesToProcess = ["Triaged"],
            });
            _coordinator = new JiraAuthoringRunCoordinator(
                AuthoringStore,
                _sourceStore,
                new JiraProcessingFilterResolver(),
                jiraOptions);
            if (activate)
            {
                ActivateAsync().GetAwaiter().GetResult();
            }
        }

        public PreparerDatabase Database { get; }
        public AuthoringRunStore AuthoringStore { get; }
        public string SnapshotDirectory { get; }

        public async Task ActivateAsync()
        {
            await AuthoringStore.EnsureProcessorModeAsync(
                _coordinator.ProcessorKind);
            AuthoringProcessorModeRecord mode =
                await AuthoringStore.GetProcessorModeAsync(
                    _coordinator.ProcessorKind);
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
                    Status = "Triaged",
                    WorkGroup = "FHIR-I",
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
            PreparedTicketPayload payload)
        {
            AuthoringOperationClaim claim =
                Assert.IsType<AuthoringOperationClaim>(
                    await AuthoringStore.ClaimItemAsync(
                        run.Id,
                        item.Id));
            string hash =
                PreparedTicketAuthoringDtos.ComputeContentHash(payload);
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
                        Database.SavePreparedTicketForAuthoringAsync(
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

        public async Task<(AuthoringRunRecord Run, AuthoringRunItemRecord Item)> CreateCompletedRunAsync(
            bool databaseOnly)
        {
            JiraProcessingSourceTicketRecord source = await _sourceStore.UpsertAsync(
                new JiraIssueSummaryEntry
                {
                    Key = "FHIR-1",
                    ProjectKey = "FHIR",
                    Title = "Title",
                    Type = "Change Request",
                    Status = "Triaged",
                    WorkGroup = "FHIR-I",
                    Specification = "FHIR",
                    UpdatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
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
            AuthoringOperationClaim claim =
                (await AuthoringStore.ClaimItemAsync(creation.Run.Id, item.Id))!;
            PreparedTicketPayload payload = CreatePayload("FHIR-1");
            string hash = FhirAugury.Processor.Jira.Fhir.Preparer.Api.PreparedTicketAuthoringDtos
                .ComputeContentHash(payload);
            AuthoringReceiptAcceptance receipt = await AuthoringStore.AcceptResultAsync(
                new AuthoringResultSubmission(
                    creation.Run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    hash),
                claim.OperationToken,
                (connection, ct) => Database.SavePreparedTicketForAuthoringAsync(
                    connection,
                    payload,
                    hash,
                    creation.Run.Id,
                    item.Id,
                    claim.OperationId,
                    ct));

            DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
            await Database.SaveHydrationAsync(
                new PreparedTicketHydrationBatch(
                    "FHIR-1",
                    new PreparedTicketHydrationRow(
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
                        hydratedAt,
                        "resolved",
                        null,
                        "<p>request</p>",
                        "<p>resolution</p>",
                        "Ada",
                        hydratedAt.AddDays(-1),
                        "Patient",
                        "patient.html"),
                    [
                        new PreparedJiraHydrationRow(
                            "FHIR-1",
                            "FHIR-1",
                            "Title",
                            "Triaged",
                            "Change Request",
                            "Major",
                            "Persuasive",
                            "resolution",
                            "FHIR Infrastructure",
                            "FHIR",
                            hydratedAt,
                            "https://jira/FHIR-1",
                            hydratedAt,
                            "resolved",
                            null),
                    ],
                    [],
                    [],
                    [],
                    []));
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
                    Status = "Triaged",
                    WorkGroup = "FHIR-I",
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

        public async Task AdvanceSourceRevisionAsync(CancellationToken ct)
        {
            await _sourceStore.UpsertAsync(
                new JiraIssueSummaryEntry
                {
                    Key = "FHIR-1",
                    ProjectKey = "FHIR",
                    Title = "Updated title",
                    Type = "Change Request",
                    Status = "Triaged",
                    WorkGroup = "FHIR-I",
                    Specification = "FHIR",
                    UpdatedAt = new DateTimeOffset(
                        2026,
                        9,
                        2,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                },
                "fhir",
                false,
                ct);
        }

        public PreparedTicketRunPostProcessor CreatePostProcessor(
            IPreparedTicketGroupingDispatcher? groupingDispatcher = null)
        {
            HttpClient client = new(new WorkGroupHandler())
            {
                BaseAddress = new Uri("http://localhost/"),
            };
            PreparerServiceOptions options = new()
            {
                SnapshotDirectory = SnapshotDirectory,
                SnapshotSchemaVersion = 1,
                ReconcileSnapshotsOnStartup = true,
            };
            return new PreparedTicketRunPostProcessor(
                Database,
                AuthoringStore,
                new AuthoringRunFinalizer(AuthoringStore),
                new SqliteReviewSnapshotReconciler(AuthoringStore),
                _coordinator,
                new OrchestratorWorkGroupCatalogFetcher(client),
                groupingDispatcher ?? new EmptyGroupingDispatcher(Database),
                Options.Create(options));
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

    private sealed class EmptyGroupingDispatcher(PreparerDatabase database)
        : IPreparedTicketGroupingDispatcher
    {
        public async Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
        {
            _ = await database.SaveGroupingForRunAsync(
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

    private sealed class SourceChangingGroupingDispatcher(
        PreparerDatabase database,
        Func<CancellationToken, Task> changeSourceRevision)
        : IPreparedTicketGroupingDispatcher
    {
        private readonly EmptyGroupingDispatcher _inner = new(database);
        private bool _changed;

        public async Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
        {
            if (!_changed)
            {
                await changeSourceRevision(ct);
                _changed = true;
            }
            await _inner.ReplaceGroupingAsync(runId, partition, lease, ct);
        }
    }

    private sealed class NoOpGroupingDispatcher
        : IPreparedTicketGroupingDispatcher
    {
        public Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class WorkGroupHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string json =
                """{"workGroups":[{"code":"fhir-i","name":"FHIR Infrastructure","retired":false,"totalFileCount":1,"totalArtifactCount":1,"repos":[]}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
