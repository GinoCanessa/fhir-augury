using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;
using FhirAugury.Processor.Jira.Fhir.Planner.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerTicketHandlerTests
{
    [Fact]
    public async Task RenderCommand_IncludesTicketDbAndRepoFilters()
    {
        using HandlerFixture fixture = new(async (command, context, database) =>
        {
            await InsertTicketAsync(database, context.TicketKey);
            Assert.Contains("FHIR-123", command.Arguments);
            Assert.Equal(context.DatabasePath, Path.GetFullPath(context.DatabasePath));
            Assert.Contains(context.DatabasePath, command.Arguments);
            Assert.Contains("[\"HL7/fhir\"]", command.Arguments);
            return new JiraAgentResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(1), false);
        }, ["HL7/fhir"]);

        await fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None);

        Assert.Equal(ProcessingStatusValues.Complete, fixture.Item.ProcessingStatus);
    }

    [Fact]
    public async Task Success_PreservesAgentWrittenRowsAndMarksComplete()
    {
        using HandlerFixture fixture = new(async (_, context, database) =>
        {
            await InsertTicketAsync(database, context.TicketKey);
            return new JiraAgentResult(0, string.Empty, string.Empty, TimeSpan.Zero, false);
        });

        await fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None);

        Assert.True(await fixture.Database.PlanExistsAsync(fixture.Item.Key));
        Assert.Equal(ProcessingStatusValues.Complete, fixture.Item.ProcessingStatus);
    }

    [Fact]
    public async Task Failure_DeletesPartialRowsAndMarksError()
    {
        using HandlerFixture fixture = new(async (_, context, database) =>
        {
            await InsertTicketAsync(database, context.TicketKey);
            return new JiraAgentResult(2, string.Empty, "bad plan", TimeSpan.Zero, false);
        });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None));

        Assert.Equal("bad plan", error.Message);
        Assert.False(await fixture.Database.PlanExistsAsync(fixture.Item.Key));
        Assert.Equal(ProcessingStatusValues.Error, fixture.Item.ProcessingStatus);
        Assert.Equal(2, fixture.Item.AgentExitCode);
    }

    [Fact]
    public async Task ProcessRunnerError_DeletesPartialRowsAndMarksError()
    {
        using HandlerFixture fixture = new(async (_, context, database) =>
        {
            await InsertTicketAsync(database, context.TicketKey);
            throw new FileNotFoundException("missing copilot");
        });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None));

        Assert.Contains("missing copilot", error.Message, StringComparison.Ordinal);
        Assert.False(await fixture.Database.PlanExistsAsync(fixture.Item.Key));
        Assert.Equal(ProcessingStatusValues.Error, fixture.Item.ProcessingStatus);
    }

    [Fact]
    public async Task Success_StampsNonEmptyCompletionId()
    {
        using HandlerFixture fixture = new(async (_, context, database) =>
        {
            await InsertTicketAsync(database, context.TicketKey);
            return new JiraAgentResult(0, string.Empty, string.Empty, TimeSpan.Zero, false);
        });

        await fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(fixture.Item.CompletionId));
    }

    [Fact]
    public async Task Failure_LeavesCompletionIdNull()
    {
        using HandlerFixture fixture = new(async (_, context, database) =>
        {
            await InsertTicketAsync(database, context.TicketKey);
            return new JiraAgentResult(2, string.Empty, "bad plan", TimeSpan.Zero, false);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handler.ProcessAsync(fixture.Item, CancellationToken.None));

        Assert.Null(fixture.Item.CompletionId);
    }

    private static async Task InsertTicketAsync(PlannerDatabase database, string key)
    {
        await using SqliteConnection connection = database.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO planned_tickets (Id, Key, Resolution, ResolutionSummary, FeatureProposal, DesignRationale, SavedAt) VALUES (@id, @key, @resolution, @summary, @proposal, @rationale, @savedAt)";
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@resolution", "raw");
        command.Parameters.AddWithValue("@summary", "summary");
        command.Parameters.AddWithValue("@proposal", "proposal");
        command.Parameters.AddWithValue("@rationale", "rationale");
        command.Parameters.AddWithValue("@savedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class HandlerFixture : IDisposable
    {
        private readonly string _directory;

        public HandlerFixture(Func<JiraAgentCommand, JiraAgentCommandContext, PlannerDatabase, Task<JiraAgentResult>> runAsync, List<string>? repoFilters = null)
        {
            _directory = Path.Combine(Environment.CurrentDirectory, "temp", "planner-handler-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "planner.db");
            Database = new PlannerDatabase(DatabasePath, NullLogger<PlannerDatabase>.Instance);
            Database.Initialize();
            JiraProcessingSourceTicketStore store = new(DatabasePath);
            JiraProcessingOptions jiraOptions = new()
            {
                AgentCliCommand = "fake --ticket {ticketKey} --db {dbPath} --repos {repoFilters}",
                JiraSourceAddress = "http://localhost:5160",
            };
            ProcessingServiceOptions processingOptions = new() { DatabasePath = DatabasePath };
            PlannerOptions plannerOptions = new() { RepoFilters = repoFilters };
            FakeRunner runner = new((command, context) => runAsync(command, context, Database));
            FakeDiscoveryClient discovery = new();
            PlannerAgentCommandTokenProvider tokenProvider = new(Options.Create(plannerOptions));
            Handler = new PlannerTicketHandler(
                new JiraAgentCommandRenderer(Options.Create(jiraOptions)),
                runner,
                store,
                discovery,
                tokenProvider,
                Database,
                Options.Create(processingOptions),
                Options.Create(plannerOptions),
                NullLogger<PlannerTicketHandler>.Instance);
            Item = new JiraProcessingSourceTicketRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Key = "FHIR-123",
                Title = "Ticket",
                Project = "FHIR",
                Status = "Resolved - change required",
                WorkGroup = "FHIR-I",
                Type = "Change Request",
                SourceTicketShape = "fhir",
                LastSyncedAt = DateTimeOffset.UtcNow,
            };
        }

        public string DatabasePath { get; }
        public PlannerDatabase Database { get; }
        public PlannerTicketHandler Handler { get; }
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
