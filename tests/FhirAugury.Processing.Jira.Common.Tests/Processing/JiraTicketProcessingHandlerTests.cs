using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Processing;
using FhirAugury.Processing.Jira.Common.Tests.Authoring;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Processing;

public class JiraTicketProcessingHandlerTests
{
    [Fact]
    public async Task HandleAsync_SuccessMarksCompleteAndOptionallyMarksUpstream()
    {
        Fixture fixture = await Fixture.CreateAsync(new JiraAgentResult(0, "ok", "", TimeSpan.Zero, false));

        await fixture.Handler.ProcessAsync(fixture.Record, CancellationToken.None);

        Assert.Equal(ProcessingStatusValues.Complete, fixture.Record.ProcessingStatus);
        Assert.Equal(["FHIR-1:fhir"], fixture.Discovery.Marked);
    }

    [Fact]
    public async Task HandleAsync_NonZeroExitMarksErrorWithDetails()
    {
        Fixture fixture = await Fixture.CreateAsync(new JiraAgentResult(2, "", "bad", TimeSpan.Zero, false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler.ProcessAsync(fixture.Record, CancellationToken.None));

        Assert.Equal(ProcessingStatusValues.Error, fixture.Record.ProcessingStatus);
        Assert.Equal("bad", fixture.Record.ErrorMessage);
        Assert.Equal(2, fixture.Record.AgentExitCode);
    }

    [Fact]
    public async Task HandleAsync_CancellationDoesNotMarkComplete()
    {
        Fixture fixture = await Fixture.CreateAsync(new JiraAgentResult(-1, "", "", TimeSpan.Zero, true));

        await fixture.Handler.ProcessAsync(fixture.Record, CancellationToken.None);

        Assert.Null(fixture.Record.ProcessingStatus);
    }

    [Fact]
    public async Task AuthoringHandler_RequiresAcceptedReceiptAfterAgentExit()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        JiraAuthoringRunCreation creation =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        JiraAuthoringWorkItemStore queueStore = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem item = Assert.Single(
            await queueStore.GetPendingAsync(creation.Run.Id, 1, CancellationToken.None));
        AuthoringQueueClaim claim = Assert.IsType<AuthoringQueueClaim>(
            await queueStore.TryClaimAsync(item, DateTimeOffset.UtcNow, CancellationToken.None));
        JiraAuthoringWorkItemHandler handler = new(
            new JiraAgentCommandRenderer(fixture.Options),
            new ReceiptRunner(fixture.AuthoringStore, item),
            fixture.AuthoringStore,
            new EmptyJiraAgentExtensionTokenProvider(),
            Options.Create(new ProcessingServiceOptions
            {
                DatabasePath = fixture.DatabasePath,
                Ports = new FhirAugury.Common.Configuration.PortConfiguration { Http = 5171 },
            }));

        AuthoringWorkResult result = await handler.ProcessAsync(
            item,
            claim,
            CancellationToken.None);

        Assert.Equal(AuthoringWorkDisposition.Persisted, result.Disposition);
        Assert.NotNull(result.ReceiptId);
        Assert.Equal(creation.Run.Id, item.RunItem.RunId);
    }

    [Fact]
    public async Task AuthoringHandler_ExitZeroWithoutReceiptIsRetryable()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        JiraAuthoringRunCreation creation =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        JiraAuthoringWorkItemStore queueStore = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem item = Assert.Single(
            await queueStore.GetPendingAsync(creation.Run.Id, 1, CancellationToken.None));
        AuthoringQueueClaim claim = Assert.IsType<AuthoringQueueClaim>(
            await queueStore.TryClaimAsync(item, DateTimeOffset.UtcNow, CancellationToken.None));
        JiraAuthoringWorkItemHandler handler = new(
            new JiraAgentCommandRenderer(fixture.Options),
            new FakeRunner(new JiraAgentResult(0, "ok", "", TimeSpan.Zero, false)),
            fixture.AuthoringStore,
            new EmptyJiraAgentExtensionTokenProvider(),
            Options.Create(new ProcessingServiceOptions { DatabasePath = fixture.DatabasePath }));

        AuthoringWorkResult result = await handler.ProcessAsync(
            item,
            claim,
            CancellationToken.None);

        Assert.Equal(AuthoringWorkDisposition.RetryableError, result.Disposition);
        Assert.Contains("without an accepted persistence receipt", result.Error);
    }

    [Fact]
    public async Task AuthoringHandler_AcceptedReceiptWinsOverNonzeroExit()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        JiraAuthoringRunCreation creation =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        JiraAuthoringWorkItemStore queueStore = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem item = Assert.Single(
            await queueStore.GetPendingAsync(creation.Run.Id, 1, CancellationToken.None));
        AuthoringQueueClaim claim = Assert.IsType<AuthoringQueueClaim>(
            await queueStore.TryClaimAsync(item, DateTimeOffset.UtcNow, CancellationToken.None));
        JiraAuthoringWorkItemHandler handler = new(
            new JiraAgentCommandRenderer(fixture.Options),
            new ReceiptRunner(
                fixture.AuthoringStore,
                item,
                new JiraAgentResult(2, "", "response lost", TimeSpan.Zero, false)),
            fixture.AuthoringStore,
            new EmptyJiraAgentExtensionTokenProvider(),
            Options.Create(new ProcessingServiceOptions { DatabasePath = fixture.DatabasePath }));

        AuthoringWorkResult result = await handler.ProcessAsync(
            item,
            claim,
            CancellationToken.None);

        Assert.Equal(AuthoringWorkDisposition.Persisted, result.Disposition);
        Assert.NotNull(result.ReceiptId);
    }

    [Fact]
    public async Task AuthoringHandler_PostPersistenceStageRunsOnlyUnderReceiptLease()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        JiraAuthoringRunCreation creation =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        JiraAuthoringWorkItemStore queueStore = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem authoringItem = Assert.Single(
            await queueStore.GetPendingAsync(creation.Run.Id, 1, CancellationToken.None));
        AuthoringQueueClaim authoringClaim = Assert.IsType<AuthoringQueueClaim>(
            await queueStore.TryClaimAsync(authoringItem, DateTimeOffset.UtcNow, CancellationToken.None));
        AuthoringReceiptAcceptance accepted = await fixture.AuthoringStore.AcceptResultAsync(
            new AuthoringResultSubmission(
                creation.Run.Id,
                authoringItem.RunItem.Id,
                authoringClaim.OperationId,
                authoringItem.RunItem.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            authoringClaim.OperationToken);
        JiraAuthoringWorkItem persistedItem = Assert.Single(
            await queueStore.GetPendingAsync(creation.Run.Id, 1, CancellationToken.None));
        AuthoringQueueClaim receiptLease = Assert.IsType<AuthoringQueueClaim>(
            await queueStore.TryClaimAsync(persistedItem, DateTimeOffset.UtcNow, CancellationToken.None));
        JiraAuthoringWorkItemHandler handler = new(
            new JiraAgentCommandRenderer(fixture.Options),
            new ThrowingRunner(),
            fixture.AuthoringStore,
            new EmptyJiraAgentExtensionTokenProvider(),
            Options.Create(new ProcessingServiceOptions { DatabasePath = fixture.DatabasePath }));

        AuthoringWorkResult result = await handler.ProcessAsync(
            persistedItem,
            receiptLease,
            CancellationToken.None);

        Assert.Equal(AuthoringWorkDisposition.Complete, result.Disposition);
        Assert.Equal(accepted.Receipt.ReceiptId, result.ReceiptId);
    }

    [Fact]
    public async Task AuthoringHandler_FrozenWorkDoesNotConsultLabelMatcher()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        fixture.Options.Value.LabelsToInclude = ["cohort"];
        await fixture.SeedAsync(
            "FHIR-1", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        fixture.Matcher.Enqueue(["FHIR-1"]);
        JiraAuthoringRunCreation creation = Assert.IsType<JiraAuthoringRunCreation>(
            await fixture.Coordinator.CreateScheduledRunAsync());
        fixture.Options.Value.LabelsToInclude = ["different-cohort"];
        fixture.Options.Value.LabelsToExclude = ["cohort"];
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind, creation.Run.Id));
        JiraAuthoringWorkItemStore queue = new(fixture.AuthoringStore, fixture.SourceStore);
        JiraAuthoringWorkItem item = Assert.Single(
            await queue.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        DateTimeOffset startedAt = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringQueueClaim firstClaim = Assert.IsType<AuthoringQueueClaim>(
            await queue.TryClaimAsync(item, startedAt, CancellationToken.None));
        IOptions<ProcessingServiceOptions> options = Options.Create(
            new ProcessingServiceOptions { DatabasePath = fixture.DatabasePath });
        JiraAuthoringWorkItemHandler failingHandler = new(
            new JiraAgentCommandRenderer(fixture.Options),
            new FakeRunner(new JiraAgentResult(2, "", "retryable failure", TimeSpan.Zero, false)),
            fixture.AuthoringStore,
            new EmptyJiraAgentExtensionTokenProvider(),
            options);

        AuthoringWorkResult failure = await failingHandler.ProcessAsync(item, firstClaim, CancellationToken.None);
        Assert.Equal(AuthoringWorkDisposition.RetryableError, failure.Disposition);
        await queue.ApplyResultAsync(
            item, firstClaim, failure, startedAt.AddSeconds(1), CancellationToken.None);
        Assert.Equal(1, (await fixture.AuthoringStore.ReconcileErroredItemsAsync(
            creation.Run.Id, startedAt.AddMinutes(2))).RetriedItems);
        JiraAuthoringWorkItem retryItem = Assert.Single(
            await queue.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        AuthoringQueueClaim secondClaim = Assert.IsType<AuthoringQueueClaim>(
            await queue.TryClaimAsync(retryItem, startedAt.AddMinutes(2), CancellationToken.None));
        JiraAuthoringWorkItemHandler acceptingHandler = new(
            new JiraAgentCommandRenderer(fixture.Options),
            new ReceiptRunner(fixture.AuthoringStore, retryItem),
            fixture.AuthoringStore,
            new EmptyJiraAgentExtensionTokenProvider(),
            options);
        AuthoringWorkResult result = await acceptingHandler.ProcessAsync(
            retryItem, secondClaim, CancellationToken.None);

        Assert.Equal(AuthoringWorkDisposition.Persisted, result.Disposition);
        Assert.NotNull(result.ReceiptId);
        Assert.Equal(2, secondClaim.AttemptNumber);
        Assert.Equal(item.RunItem.Id, retryItem.RunItem.Id);
        Assert.Equal(item.RunItem.ExpectedSourceRevision, retryItem.RunItem.ExpectedSourceRevision);
        Assert.Equal(result.ReceiptId, Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id)).AcceptedReceiptId);
        Assert.Single(fixture.Matcher.Calls);
        Assert.Equal(2, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
        Assert.Equal(0, fixture.Scalar("SELECT SUM(ProcessingAttemptCount) FROM jira_processing_source_tickets"));
    }

    private sealed class Fixture
    {
        public required JiraTicketProcessingHandler Handler { get; init; }
        public required JiraProcessingSourceTicketRecord Record { get; init; }
        public required FakeDiscovery Discovery { get; init; }

        public static async Task<Fixture> CreateAsync(JiraAgentResult result)
        {
            string dbPath = Path.Combine(AppContext.BaseDirectory, $"jira-handler-{Guid.NewGuid():N}.db");
            JiraProcessingSourceTicketStore store = new(dbPath);
            JiraProcessingSourceTicketRecord record = await store.UpsertAsync(new JiraIssueSummaryEntry
            {
                Key = "FHIR-1",
                ProjectKey = "FHIR",
                Title = "Title",
                Type = "Change Request",
                Status = "Triaged",
                WorkGroup = "FHIR-I",
            }, "fhir", false, CancellationToken.None);
            FakeDiscovery discovery = new();
            JiraTicketProcessingHandler handler = new(
                new JiraAgentCommandRenderer(Options.Create(new JiraProcessingOptions { AgentCliCommand = "agent {ticketKey}", JiraSourceAddress = "http://source", MarkUpstreamProcessedOnSuccess = true })),
                new FakeRunner(result),
                store,
                discovery,
                new EmptyJiraAgentExtensionTokenProvider(),
                Options.Create(new ProcessingServiceOptions { DatabasePath = dbPath }));
            return new Fixture { Handler = handler, Record = record, Discovery = discovery };
        }
    }

    private sealed class FakeRunner(JiraAgentResult result) : IJiraAgentCliRunner
    {
        public Task<JiraAgentResult> RunAsync(JiraAgentCommand command, JiraAgentCommandContext context, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class ReceiptRunner(
        AuthoringRunStore store,
        JiraAuthoringWorkItem item,
        JiraAgentResult? result = null) : IJiraAgentCliRunner
    {
        public async Task<JiraAgentResult> RunAsync(
            JiraAgentCommand command,
            JiraAgentCommandContext context,
            CancellationToken ct)
        {
            await store.AcceptResultAsync(
                new AuthoringResultSubmission(
                    item.RunItem.RunId,
                    item.RunItem.Id,
                    context.OperationId!,
                    item.RunItem.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("payload")),
                context.OperationToken!,
                ct: ct);
            return result ?? new JiraAgentResult(0, "ok", "", TimeSpan.Zero, false);
        }

    }

    private sealed class ThrowingRunner : IJiraAgentCliRunner
    {
        public Task<JiraAgentResult> RunAsync(
            JiraAgentCommand command,
            JiraAgentCommandContext context,
            CancellationToken ct)
            => throw new InvalidOperationException("Authoring must not relaunch after receipt acceptance.");
    }

    private sealed class FakeDiscovery : IJiraTicketDiscoveryClient
    {
        public List<string> Marked { get; } = [];
        public Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(FhirAugury.Processing.Jira.Common.Filtering.ResolvedJiraProcessingFilters filters, CancellationToken ct) => Task.FromResult<IReadOnlyList<JiraIssueSummaryEntry>>([]);
        public Task<JiraIssueSummaryEntry?> GetTicketAsync(string key, string sourceTicketShape, CancellationToken ct) => Task.FromResult<JiraIssueSummaryEntry?>(null);
        public Task MarkProcessedAsync(string key, string sourceTicketShape, CancellationToken ct)
        {
            Marked.Add($"{key}:{sourceTicketShape}");
            return Task.CompletedTask;
        }
    }
}
