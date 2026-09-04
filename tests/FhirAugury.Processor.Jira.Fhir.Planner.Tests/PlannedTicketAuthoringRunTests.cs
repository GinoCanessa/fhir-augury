using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processor.Jira.Fhir.Planner.Api;
using FhirAugury.Processor.Jira.Fhir.Planner.Controllers;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannedTicketAuthoringRunTests
{
    [Fact]
    public async Task SubmitResultLegacyModeRejectsActivation()
    {
        using Fixture fixture = new(activate: false);
        PlannedTicketPayload payload = CreatePayload("FHIR-1", "legacy");

        IActionResult result = await fixture.Controller.SubmitResult(
            "run",
            "item",
            "token",
            new PlannedTicketAuthoringResultRequest(
                new AuthoringResultSubmission(
                    "run",
                    "item",
                    "operation",
                    "revision",
                    PlannedTicketAuthoringDtos.ComputeContentHash(payload)),
                payload),
            CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("authoring-not-activated", conflict.Value!.ToString());
    }

    [Fact]
    public async Task RunBackedModeRefusesLegacyGroupingWriter()
    {
        using Fixture fixture = new();
        PlannedTicketTopicsController controller =
            new(fixture.Database, fixture.AuthoringStore);

        IActionResult result = await controller.PutTopics(
            new PlannedTicketTopicGroupingRequest
            {
                WorkGroupClean = "FHIRInfrastructure",
                WorkGroupDisplay = "FHIR Infrastructure",
                Specification = "FHIR Core",
                Type = "Change Request",
            },
            CancellationToken.None);

        ConflictObjectResult conflict =
            Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("run-backed-write-required", conflict.Value!.ToString());
    }

    [Fact]
    public async Task SubmitResult_PersistsReceiptAndExactApplierCoordinatesAndReplays()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync();
        PlannedTicketPayload payload = CreatePayload("FHIR-1", "first");
        PlannedTicketAuthoringResultRequest request = fixture.Request(run, item, claim, payload);

        OkObjectResult firstResult = Assert.IsType<OkObjectResult>(
            await fixture.Controller.SubmitResult(
                run.Id,
                item.Id,
                claim.OperationToken,
                request,
                CancellationToken.None));
        OkObjectResult replayResult = Assert.IsType<OkObjectResult>(
            await fixture.Controller.SubmitResult(
                run.Id,
                item.Id,
                claim.OperationToken,
                request,
                CancellationToken.None));
        AuthoringReceiptAcceptance first =
            Assert.IsType<AuthoringReceiptAcceptance>(firstResult.Value);
        AuthoringReceiptAcceptance replay =
            Assert.IsType<AuthoringReceiptAcceptance>(replayResult.Value);

        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Equal(
            first.Receipt.ReceiptId,
            fixture.Scalar<string>("SELECT CompletionId FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
        Assert.Equal(
            first.Receipt.PersistedAt,
            fixture.ScalarDate("SELECT CompletedProcessingAt FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
        Assert.Equal(
            "complete",
            fixture.Scalar<string>("SELECT ProcessingStatus FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
    }

    [Fact]
    public async Task SubmitResult_StaleRevisionPreservesLastGoodGraphAndApplierCoordinates()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync();
        PlannedTicketPayload firstPayload = CreatePayload("FHIR-1", "first");
        AuthoringReceiptAcceptance first = Assert.IsType<AuthoringReceiptAcceptance>(
            Assert.IsType<OkObjectResult>(
                await fixture.Controller.SubmitResult(
                    run.Id,
                    item.Id,
                    claim.OperationToken,
                    fixture.Request(run, item, claim, firstPayload),
                    CancellationToken.None)).Value);

        PlannedTicketPayload stalePayload = CreatePayload("FHIR-1", "stale");
        PlannedTicketAuthoringResultRequest stale = new(
            new AuthoringResultSubmission(
                run.Id,
                item.Id,
                claim.OperationId,
                "changed-source",
                PlannedTicketAuthoringDtos.ComputeContentHash(stalePayload)),
            stalePayload);
        IActionResult result = await fixture.Controller.SubmitResult(
            run.Id,
            item.Id,
            claim.OperationToken,
            stale,
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(
            "first",
            fixture.Scalar<string>("SELECT ResolutionSummary FROM planned_tickets WHERE Key = 'FHIR-1'"));
        Assert.Equal(
            first.Receipt.ReceiptId,
            fixture.Scalar<string>("SELECT CompletionId FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
        Assert.Equal(
            first.Receipt.PersistedAt,
            fixture.ScalarDate("SELECT CompletedProcessingAt FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
    }

    [Fact]
    public async Task SubmitResult_RevalidatesCurrentSourceRevisionInReceiptTransaction()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync();
        await fixture.AdvanceSourceRevisionAsync();

        IActionResult result = await fixture.Controller.SubmitResult(
            run.Id,
            item.Id,
            claim.OperationToken,
            fixture.Request(
                run,
                item,
                claim,
                CreatePayload("FHIR-1", "stale")),
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(
            0,
            fixture.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_result_receipts"));
        Assert.Equal(
            0,
            fixture.Scalar<int>(
                "SELECT COUNT(*) FROM planned_tickets"));
    }

    [Fact]
    public async Task ChangedAcceptedResultReplacesBothApplierCoordinates()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord firstRun, AuthoringRunItemRecord firstItem, AuthoringOperationClaim firstClaim) =
            await fixture.CreateClaimAsync();
        AuthoringReceiptAcceptance first = Assert.IsType<AuthoringReceiptAcceptance>(
            Assert.IsType<OkObjectResult>(
                await fixture.Controller.SubmitResult(
                    firstRun.Id,
                    firstItem.Id,
                    firstClaim.OperationToken,
                    fixture.Request(firstRun, firstItem, firstClaim, CreatePayload("FHIR-1", "first")),
                    CancellationToken.None)).Value);
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            firstItem.Id,
            first.Receipt.ReceiptId);
        await fixture.AuthoringStore.MarkRunFinalizingAsync(firstRun.Id);
        await fixture.AuthoringStore.CompleteRunAsync(firstRun.Id, snapshotId: null);
        await fixture.AdvanceSourceRevisionAsync();

        (AuthoringRunRecord secondRun, AuthoringRunItemRecord secondItem, AuthoringOperationClaim secondClaim) =
            await fixture.CreateClaimAsync();
        AuthoringReceiptAcceptance second = Assert.IsType<AuthoringReceiptAcceptance>(
            Assert.IsType<OkObjectResult>(
                await fixture.Controller.SubmitResult(
                    secondRun.Id,
                    secondItem.Id,
                    secondClaim.OperationToken,
                    fixture.Request(secondRun, secondItem, secondClaim, CreatePayload("FHIR-1", "second")),
                    CancellationToken.None)).Value);

        Assert.NotEqual(first.Receipt.ReceiptId, second.Receipt.ReceiptId);
        Assert.Equal(
            second.Receipt.ReceiptId,
            fixture.Scalar<string>("SELECT CompletionId FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
        Assert.Equal(
            second.Receipt.PersistedAt,
            fixture.ScalarDate("SELECT CompletedProcessingAt FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
    }

    [Fact]
    public async Task IdenticalReceiptBackedResultPreservesApplierCoordinates()
    {
        using Fixture fixture = new();
        PlannedTicketPayload payload = CreatePayload("FHIR-1", "same");
        (AuthoringRunRecord firstRun, AuthoringRunItemRecord firstItem, AuthoringOperationClaim firstClaim) =
            await fixture.CreateClaimAsync();
        AuthoringReceiptAcceptance first = Assert.IsType<AuthoringReceiptAcceptance>(
            Assert.IsType<OkObjectResult>(
                await fixture.Controller.SubmitResult(
                    firstRun.Id,
                    firstItem.Id,
                    firstClaim.OperationToken,
                    fixture.Request(firstRun, firstItem, firstClaim, payload),
                    CancellationToken.None)).Value);
        await fixture.AuthoringStore.MarkItemCompleteAsync(firstItem.Id, first.Receipt.ReceiptId);
        await fixture.AuthoringStore.MarkRunFinalizingAsync(firstRun.Id);
        await fixture.AuthoringStore.CompleteRunAsync(firstRun.Id, snapshotId: null);
        await fixture.AdvanceSourceRevisionAsync();

        (AuthoringRunRecord secondRun, AuthoringRunItemRecord secondItem, AuthoringOperationClaim secondClaim) =
            await fixture.CreateClaimAsync();
        AuthoringReceiptAcceptance second = Assert.IsType<AuthoringReceiptAcceptance>(
            Assert.IsType<OkObjectResult>(
                await fixture.Controller.SubmitResult(
                    secondRun.Id,
                    secondItem.Id,
                    secondClaim.OperationToken,
                    fixture.Request(secondRun, secondItem, secondClaim, payload),
                    CancellationToken.None)).Value);

        Assert.NotEqual(first.Receipt.ReceiptId, second.Receipt.ReceiptId);
        Assert.Equal(
            first.Receipt.ReceiptId,
            fixture.Scalar<string>(
                "SELECT CompletionId FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
        Assert.Equal(
            first.Receipt.PersistedAt,
            fixture.ScalarDate(
                "SELECT CompletedProcessingAt FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
    }

    [Fact]
    public async Task MovingSameTextBetweenChildSectionsChangesApplierCoordinates()
    {
        using Fixture fixture = new();
        PlannedTicketPayload firstPayload = CreatePayload("FHIR-1", "same");
        firstPayload.ChangeValidations =
        [
            new PlannedTicketChangeValidationPayload
            {
                TicketRepoId = "repo-1",
                RepoKey = "HL7/fhir",
                ValidationSequence = 0,
                Action = "same child text",
            },
        ];
        (AuthoringRunRecord firstRun, AuthoringRunItemRecord firstItem, AuthoringOperationClaim firstClaim) =
            await fixture.CreateClaimAsync();
        AuthoringReceiptAcceptance first = Assert.IsType<AuthoringReceiptAcceptance>(
            Assert.IsType<OkObjectResult>(
                await fixture.Controller.SubmitResult(
                    firstRun.Id,
                    firstItem.Id,
                    firstClaim.OperationToken,
                    fixture.Request(firstRun, firstItem, firstClaim, firstPayload),
                    CancellationToken.None)).Value);
        await fixture.AuthoringStore.MarkItemCompleteAsync(firstItem.Id, first.Receipt.ReceiptId);
        await fixture.AuthoringStore.MarkRunFinalizingAsync(firstRun.Id);
        await fixture.AuthoringStore.CompleteRunAsync(firstRun.Id, snapshotId: null);
        await fixture.AdvanceSourceRevisionAsync();

        PlannedTicketPayload secondPayload = CreatePayload("FHIR-1", "same");
        secondPayload.TestingConsiderations =
        [
            new PlannedTicketTestingConsiderationPayload
            {
                TicketRepoId = "repo-1",
                RepoKey = "HL7/fhir",
                ConsiderationSequence = 0,
                Consideration = "same child text",
            },
        ];
        (AuthoringRunRecord secondRun, AuthoringRunItemRecord secondItem, AuthoringOperationClaim secondClaim) =
            await fixture.CreateClaimAsync();
        AuthoringReceiptAcceptance second = Assert.IsType<AuthoringReceiptAcceptance>(
            Assert.IsType<OkObjectResult>(
                await fixture.Controller.SubmitResult(
                    secondRun.Id,
                    secondItem.Id,
                    secondClaim.OperationToken,
                    fixture.Request(secondRun, secondItem, secondClaim, secondPayload),
                    CancellationToken.None)).Value);

        Assert.Equal(
            second.Receipt.ReceiptId,
            fixture.Scalar<string>(
                "SELECT CompletionId FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
        Assert.Equal(
            second.Receipt.PersistedAt,
            fixture.ScalarDate(
                "SELECT CompletedProcessingAt FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
    }

    [Fact]
    public async Task IdenticalLegacyRevalidationPreservesCapturedApplierCoordinates()
    {
        using Fixture fixture = new(activate: false);
        const string legacyCompletionId = "legacy-completion";
        DateTimeOffset legacyCompletedAt =
            new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        fixture.SetLegacyCompletion(legacyCompletionId, legacyCompletedAt);
        PlannedTicketPayload payload = CreatePayload("FHIR-1", "legacy");
        payload.RepoChanges =
        [
            new PlannedTicketRepoChangePayload
            {
                TicketRepoId = "legacy-link",
                RepoKey = "HL7/fhir",
                ChangeSequence = 0,
                FilePath = "source/example.html",
                ChangeTitle = "Change",
                ChangeDescription = "Description",
                ReplacementLines = ["line a", "line b"],
                Reason = "Reason",
            },
        ];
        await fixture.Database.SavePlannedTicketAsync(payload);
        fixture.Execute(
            """UPDATE planned_ticket_repo_changes SET ReplacementLines = '[ "line a",  "line b" ]' WHERE IssueKey = 'FHIR-1'""");
        await fixture.Database.ClassifyLegacyPlannedTicketsAsync();
        payload.RepoChanges[0].TicketRepoId = "new-link";
        await fixture.ActivateAsync();
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync(activate: false);

        AuthoringReceiptAcceptance accepted = Assert.IsType<AuthoringReceiptAcceptance>(
            Assert.IsType<OkObjectResult>(
                await fixture.Controller.SubmitResult(
                    run.Id,
                    item.Id,
                    claim.OperationToken,
                    fixture.Request(run, item, claim, payload),
                    CancellationToken.None)).Value);

        Assert.False(accepted.IsReplay);
        Assert.Equal(
            legacyCompletionId,
            fixture.Scalar<string>("SELECT CompletionId FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
        Assert.Equal(
            legacyCompletedAt,
            fixture.ScalarDate("SELECT CompletedProcessingAt FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
        Assert.Equal(1, fixture.Scalar<int>("SELECT COUNT(*) FROM authoring_result_receipts"));
    }

    private static PlannedTicketPayload CreatePayload(string key, string summary)
        => new()
        {
            Key = key,
            Resolution = "Persuasive",
            ResolutionSummary = summary,
            FeatureProposal = "proposal",
            DesignRationale = "rationale",
            Repos =
            [
                new PlannedTicketRepoPayload
                {
                    RepoKey = "HL7/fhir",
                    RepoRevision = "abc",
                    Justification = "primary",
                },
            ],
        };

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly JiraProcessingSourceTicketStore _sourceStore;

        public Fixture(bool activate = true)
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                $"fhir-augury-planner-authoring-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "planner.db");
            Database = new PlannerDatabase(path, NullLogger<PlannerDatabase>.Instance);
            Database.Initialize();
            AuthoringStore = new AuthoringRunStore(Database);
            _sourceStore = new JiraProcessingSourceTicketStore(path);
            Coordinator = new JiraAuthoringRunCoordinator(
                AuthoringStore,
                _sourceStore,
                new JiraProcessingFilterResolver(),
                Options.Create(new JiraProcessingOptions
                {
                    AgentCliCommand = "agent {ticketKey}",
                    JiraSourceAddress = "http://source",
                    SourceTicketShape = "fhir",
                    TicketStatusesToProcess = ["Resolved - change required"],
                }));
            Source = _sourceStore.UpsertAsync(
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
                CancellationToken.None).GetAwaiter().GetResult();
            if (activate)
            {
                ActivateAsync().GetAwaiter().GetResult();
            }
            Controller = new PlannedTicketAuthoringRunsController(
                AuthoringStore,
                Coordinator,
                Database);
        }

        public PlannerDatabase Database { get; }
        public AuthoringRunStore AuthoringStore { get; }
        public JiraAuthoringRunCoordinator Coordinator { get; }
        public PlannedTicketAuthoringRunsController Controller { get; }
        public JiraProcessingSourceTicketRecord Source { get; private set; }

        public async Task<(AuthoringRunRecord Run, AuthoringRunItemRecord Item, AuthoringOperationClaim Claim)> CreateClaimAsync(
            bool activate = false)
        {
            if (activate)
            {
                await ActivateAsync();
            }
            JiraAuthoringRunCreation creation = await Coordinator.CreateOneItemRunAsync(Source);
            AuthoringRunItemRecord item = Assert.Single(creation.Items);
            AuthoringOperationClaim claim =
                (await AuthoringStore.ClaimItemAsync(creation.Run.Id, item.Id))!;
            return (creation.Run, item, claim);
        }

        public PlannedTicketAuthoringResultRequest Request(
            AuthoringRunRecord run,
            AuthoringRunItemRecord item,
            AuthoringOperationClaim claim,
            PlannedTicketPayload payload)
            => new(
                new AuthoringResultSubmission(
                    run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    PlannedTicketAuthoringDtos.ComputeContentHash(payload)),
                payload);

        public async Task ActivateAsync()
        {
            await AuthoringStore.EnsureProcessorModeAsync(Coordinator.ProcessorKind);
            await AuthoringStore.TransitionProcessorModeAsync(
                Coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.Legacy,
                AuthoringStatusValues.ProcessorModes.CuttingOver);
            await AuthoringStore.TransitionProcessorModeAsync(
                Coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.CuttingOver,
                AuthoringStatusValues.ProcessorModes.RunBacked);
        }

        public async Task AdvanceSourceRevisionAsync()
        {
            Source = await _sourceStore.UpsertAsync(
                new JiraIssueSummaryEntry
                {
                    Key = "FHIR-1",
                    ProjectKey = "FHIR",
                    Title = "Title",
                    Type = "Change Request",
                    Status = "Resolved - change required",
                    WorkGroup = "FHIR Infrastructure",
                    Specification = "FHIR",
                    UpdatedAt = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero),
                },
                "fhir",
                false,
                CancellationToken.None);
        }

        public void SetLegacyCompletion(string completionId, DateTimeOffset completedAt)
        {
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE jira_processing_source_tickets
                SET ProcessingStatus = 'complete',
                    CompletionId = @completionId,
                    CompletedProcessingAt = @completedAt
                WHERE Key = 'FHIR-1'
                """;
            command.Parameters.AddWithValue("@completionId", completionId);
            command.Parameters.AddWithValue("@completedAt", completedAt.ToString("O"));
            command.ExecuteNonQuery();
        }

        public T Scalar<T>(string sql)
        {
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
        }

        public void Execute(string sql)
        {
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public DateTimeOffset ScalarDate(string sql)
            => DateTimeOffset.Parse(Scalar<string>(sql), null, System.Globalization.DateTimeStyles.RoundtripKind);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
