using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class FhirTicketPrepHandlerTests
{
    [Fact]
    public async Task HandleAsync_RendersTicketKeyAndDbPathTokens()
    {
        using HandlerFixture fixture = new(async (command, context, database) =>
        {
            await database.SavePreparedTicketAsync(SamplePayload(context.TicketKey));
            Assert.Contains("FHIR-123", command.Arguments);
            Assert.Contains(context.DatabasePath, command.Arguments);
            return new JiraAgentResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(1), false);
        });

        await fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None);

        Assert.Equal(ProcessingStatusValues.Complete, fixture.Item.ProcessingStatus);
    }

    [Fact]
    public async Task HandleAsync_SuccessRequiresPreparedTicketRow()
    {
        using HandlerFixture fixture = new((_, _, _) => Task.FromResult(new JiraAgentResult(0, string.Empty, string.Empty, TimeSpan.Zero, false)));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None));

        Assert.Contains("did not persist", error.Message, StringComparison.Ordinal);
        Assert.Equal(ProcessingStatusValues.Error, fixture.Item.ProcessingStatus);
    }

    [Fact]
    public async Task HandleAsync_NonZeroExitReturnsProcessingError()
    {
        using HandlerFixture fixture = new((_, _, _) => Task.FromResult(new JiraAgentResult(2, string.Empty, "bad", TimeSpan.Zero, false)));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None));

        Assert.Equal("bad", error.Message);
        Assert.Equal(ProcessingStatusValues.Error, fixture.Item.ProcessingStatus);
        Assert.Equal(2, fixture.Item.AgentExitCode);
    }

    [Fact]
    public async Task HandleAsync_CommandNotFoundReturnsProcessingError()
    {
        using HandlerFixture fixture = new((_, _, _) => throw new FileNotFoundException("missing copilot"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None));

        Assert.Contains("missing copilot", error.Message, StringComparison.Ordinal);
        Assert.Equal(ProcessingStatusValues.Error, fixture.Item.ProcessingStatus);
    }

    [Fact]
    public async Task HandleAsync_CancellationDoesNotMarkSuccess()
    {
        using HandlerFixture fixture = new((_, _, _) => Task.FromResult(new JiraAgentResult(-1, string.Empty, string.Empty, TimeSpan.Zero, true)));

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None));

        Assert.NotEqual(ProcessingStatusValues.Complete, fixture.Item.ProcessingStatus);
    }

    [Fact]
    public async Task HandleAsync_Success_StampsNonEmptyCompletionId()
    {
        using HandlerFixture fixture = new(async (_, context, database) =>
        {
            await database.SavePreparedTicketAsync(SamplePayload(context.TicketKey));
            return new JiraAgentResult(0, string.Empty, string.Empty, TimeSpan.Zero, false);
        });

        await fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(fixture.Item.CompletionId));
    }

    [Fact]
    public async Task HandleAsync_Failure_LeavesCompletionIdNull()
    {
        using HandlerFixture fixture = new((_, _, _) => Task.FromResult(new JiraAgentResult(2, string.Empty, "bad", TimeSpan.Zero, false)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None));

        Assert.Null(fixture.Item.CompletionId);
    }

    private static PreparedTicketPayload SamplePayload(string key) => new()
    {
        Key = key,
        RequestSummary = "request",
        ProposalA = "proposal a",
        ProposalAImpact = "Non-substantive",
        ProposalB = "proposal b",
        ProposalBImpact = "Compatible, substantive",
        ProposalC = "proposal c",
        Recommendation = "A",
        RecommendationJustification = "because",
    };

    private sealed class HandlerFixture : IDisposable
    {
        private readonly string _directory;

        public HandlerFixture(Func<JiraAgentCommand, JiraAgentCommandContext, PreparerDatabase, Task<JiraAgentResult>> runAsync)
        {
            _directory = Path.Combine(Environment.CurrentDirectory, "temp", "handler-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "preparer.db");
            Database = new PreparerDatabase(DatabasePath, NullLogger<PreparerDatabase>.Instance);
            Database.Initialize();
            JiraProcessingSourceTicketStore store = new(DatabasePath);
            JiraProcessingOptions jiraOptions = new()
            {
                AgentCliCommand = "fake --ticket {ticketKey} --db {dbPath}",
                JiraSourceAddress = "http://localhost:5160",
            };
            ProcessingServiceOptions processingOptions = new() { DatabasePath = DatabasePath };
            FakeRunner runner = new((command, context) => runAsync(command, context, Database));
            FakeDiscoveryClient discovery = new();
            Handler = new FhirTicketPrepHandler(
                new JiraAgentCommandRenderer(Options.Create(jiraOptions)),
                runner,
                store,
                discovery,
                Database,
                Options.Create(processingOptions),
                NullLogger<FhirTicketPrepHandler>.Instance);
            Item = new JiraProcessingSourceTicketRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Key = "FHIR-123",
                Title = "Ticket",
                Project = "FHIR",
                Status = "Triaged",
                WorkGroup = "FHIR-I",
                Type = "Change Request",
                SourceTicketShape = "fhir",
                LastSyncedAt = DateTimeOffset.UtcNow,
            };
        }

        public string DatabasePath { get; }
        public PreparerDatabase Database { get; }
        public FhirTicketPrepHandler Handler { get; }
        public JiraProcessingSourceTicketRecord Item { get; }

        public void Dispose()
        {
            Database.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FakeRunner(Func<JiraAgentCommand, JiraAgentCommandContext, Task<JiraAgentResult>> runAsync) : IJiraAgentCliRunner
    {
        public Task<JiraAgentResult> RunAsync(JiraAgentCommand command, JiraAgentCommandContext context, CancellationToken ct) => runAsync(command, context);
    }

    private sealed class FakeDiscoveryClient : IJiraTicketDiscoveryClient
    {
        public Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(ResolvedJiraProcessingFilters filters, CancellationToken ct) => Task.FromResult<IReadOnlyList<JiraIssueSummaryEntry>>([]);
        public Task<JiraIssueSummaryEntry?> GetTicketAsync(string key, string sourceTicketShape, CancellationToken ct) => Task.FromResult<JiraIssueSummaryEntry?>(null);
        public Task MarkProcessedAsync(string key, string sourceTicketShape, CancellationToken ct) => Task.CompletedTask;
    }
}
