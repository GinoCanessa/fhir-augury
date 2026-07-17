using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Processing;
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
