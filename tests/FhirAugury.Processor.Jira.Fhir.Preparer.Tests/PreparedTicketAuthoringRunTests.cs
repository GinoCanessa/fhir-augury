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
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketAuthoringRunTests
{
    [Fact]
    public async Task SubmitResult_LegacyModeRejectsActivation()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync(activate: true);
        await fixture.AuthoringStore.TransitionProcessorModeAsync(
            fixture.Coordinator.ProcessorKind,
            AuthoringStatusValues.ProcessorModes.RunBacked,
            AuthoringStatusValues.ProcessorModes.RunBacked);
        using Fixture legacyFixture = new();
        PreparedTicketPayload payload = CreatePayload("FHIR-1");
        PreparedTicketAuthoringRunsController controller = legacyFixture.CreateController();

        IActionResult result = await controller.SubmitResult(
            run.Id,
            item.Id,
            claim.OperationToken,
            new PreparedTicketAuthoringResultRequest(
                new AuthoringResultSubmission(
                    run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    PreparedTicketAuthoringDtos.ComputeContentHash(payload)),
                payload),
            CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("authoring-not-activated", conflict.Value!.ToString());
    }

    [Fact]
    public async Task SubmitResult_PersistsDomainGraphAndReceiptAtomicallyAndReplays()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync();
        PreparedTicketPayload payload = CreatePayload("FHIR-1");
        string contentHash = PreparedTicketAuthoringDtos.ComputeContentHash(payload);
        PreparedTicketAuthoringResultRequest request = new(
            new AuthoringResultSubmission(
                run.Id,
                item.Id,
                claim.OperationId,
                "2026-09-01T00:00:00+00:00",
                contentHash),
            payload);
        PreparedTicketAuthoringRunsController controller = fixture.CreateController();

        OkObjectResult firstResult = Assert.IsType<OkObjectResult>(
            await controller.SubmitResult(
                run.Id,
                item.Id,
                claim.OperationToken,
                request,
                CancellationToken.None));
        OkObjectResult replayResult = Assert.IsType<OkObjectResult>(
            await controller.SubmitResult(
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
        Assert.True(await fixture.Database.PreparedTicketExistsAsync("FHIR-1"));
        Assert.Equal(1, fixture.Scalar<int>("SELECT COUNT(*) FROM authoring_result_receipts"));
        Assert.Equal(
            "receipt-backed",
            fixture.Scalar<string>(
                "SELECT Classification FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));

        await fixture.AuthoringStore.MarkItemCompleteAsync(
            item.Id,
            first.Receipt.ReceiptId);
        await fixture.AuthoringStore.MarkRunFinalizingAsync(run.Id);
        await fixture.AuthoringStore.CompleteRunAsync(run.Id, snapshotId: null);
        payload.RequestSummary = "Legacy overwrite";
        await Assert.ThrowsAsync<AuthoringConflictException>(
            () => fixture.Database.SavePreparedTicketAsync(payload));
        Assert.Equal(
            "receipt-backed",
            fixture.Scalar<string>(
                "SELECT Classification FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
    }

    [Fact]
    public async Task SubmitResult_SourceRevisionConflictLeavesNoDomainOrReceiptRows()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync();
        PreparedTicketPayload payload = CreatePayload("FHIR-1");
        PreparedTicketAuthoringRunsController controller = fixture.CreateController();

        IActionResult result = await controller.SubmitResult(
            run.Id,
            item.Id,
            claim.OperationToken,
            new PreparedTicketAuthoringResultRequest(
                new AuthoringResultSubmission(
                    run.Id,
                    item.Id,
                    claim.OperationId,
                    "newer-source-revision",
                    PreparedTicketAuthoringDtos.ComputeContentHash(payload)),
                payload),
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.False(await fixture.Database.PreparedTicketExistsAsync("FHIR-1"));
        Assert.Equal(0, fixture.Scalar<int>("SELECT COUNT(*) FROM authoring_result_receipts"));
    }

    [Fact]
    public async Task SubmitResult_RevalidatesCurrentSourceRevisionInReceiptTransaction()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync();
        await fixture.AdvanceSourceRevisionAsync();
        PreparedTicketPayload payload = CreatePayload("FHIR-1");

        IActionResult result = await fixture.CreateController().SubmitResult(
            run.Id,
            item.Id,
            claim.OperationToken,
            new PreparedTicketAuthoringResultRequest(
                new AuthoringResultSubmission(
                    run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    PreparedTicketAuthoringDtos.ComputeContentHash(payload)),
                payload),
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.False(await fixture.Database.PreparedTicketExistsAsync("FHIR-1"));
        Assert.Equal(
            0,
            fixture.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_result_receipts"));
    }

    [Fact]
    public async Task GroupingWriterRequiresRunStageCoordinates()
    {
        using Fixture fixture = new();
        await fixture.CreateClaimAsync();
        PreparedTicketGroupingsController controller =
            new(fixture.Database, fixture.AuthoringStore);

        ActionResult<PreparedTicketGroupingSaveResultDto> result =
            await controller.PutPartition(
                "FHIRInfrastructure",
                "FHIR Core",
                "Change Request",
                new PreparedTicketGroupingPutRequest(
                    "FHIR Infrastructure",
                    []),
                CancellationToken.None);

        ConflictObjectResult conflict =
            Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("authoring-stage-required", conflict.Value!.ToString());
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

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly JiraProcessingSourceTicketStore _sourceStore;

        public Fixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                $"fhir-augury-preparer-authoring-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "preparer.db");
            Database = new PreparerDatabase(path, NullLogger<PreparerDatabase>.Instance);
            Database.Initialize();
            AuthoringStore = new AuthoringRunStore(Database);
            _sourceStore = new JiraProcessingSourceTicketStore(path);
            IOptions<JiraProcessingOptions> options = Options.Create(new JiraProcessingOptions
            {
                AgentCliCommand = "agent {ticketKey}",
                JiraSourceAddress = "http://source",
                SourceTicketShape = "fhir",
                TicketStatusesToProcess = ["Triaged"],
            });
            Coordinator = new JiraAuthoringRunCoordinator(
                AuthoringStore,
                _sourceStore,
                new JiraProcessingFilterResolver(),
                options);
        }

        public PreparerDatabase Database { get; }
        public AuthoringRunStore AuthoringStore { get; }
        public JiraAuthoringRunCoordinator Coordinator { get; }

        public async Task<(AuthoringRunRecord Run, AuthoringRunItemRecord Item, AuthoringOperationClaim Claim)> CreateClaimAsync(
            bool activate = true)
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
            if (activate)
            {
                await ActivateAsync();
            }
            JiraAuthoringRunCreation creation = await Coordinator.CreateOneItemRunAsync(source);
            Assert.True(await AuthoringStore.TryAcquireMutationFenceAsync(
                Coordinator.ProcessorKind,
                creation.Run.Id));
            AuthoringRunItemRecord item = Assert.Single(creation.Items);
            AuthoringOperationClaim claim = (await AuthoringStore.ClaimItemAsync(
                creation.Run.Id,
                item.Id))!;
            return (creation.Run, item, claim);
        }

        public PreparedTicketAuthoringRunsController CreateController()
            => new(AuthoringStore, Coordinator, Database);

        public Task<JiraProcessingSourceTicketRecord> AdvanceSourceRevisionAsync()
            => _sourceStore.UpsertAsync(
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
                        2,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                },
                "fhir",
                false,
                CancellationToken.None);

        public T Scalar<T>(string sql)
        {
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
        }

        private async Task ActivateAsync()
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
