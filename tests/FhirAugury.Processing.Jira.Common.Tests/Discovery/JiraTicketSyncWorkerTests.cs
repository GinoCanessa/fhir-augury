using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processing.Jira.Common.Tests.Authoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Discovery;

public class JiraTicketSyncWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_RunsSyncOnce_WhenLifecycleRunning()
    {
        Fixture fixture = Fixture.Create(startRunning: true, [CreateTicket("FHIR-1"), CreateTicket("FHIR-2")]);

        await fixture.RunForAsync(TimeSpan.FromMilliseconds(250));

        Assert.True(fixture.Discovery.CallCount >= 1);
        Assert.NotNull(await fixture.Store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None));
        Assert.NotNull(await fixture.Store.GetByKeyAsync("FHIR-2", "fhir", CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_SkipsSync_WhenLifecyclePaused()
    {
        Fixture fixture = Fixture.Create(startRunning: false, [CreateTicket("FHIR-1")]);

        await fixture.RunForAsync(TimeSpan.FromMilliseconds(250));

        Assert.Equal(0, fixture.Discovery.CallCount);
        Assert.Null(await fixture.Store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesLooping_AfterSyncThrows()
    {
        FakeDiscovery discovery = new(
            [
                _ => throw new InvalidOperationException("boom"),
                _ => Task.FromResult<IReadOnlyList<JiraIssueSummaryEntry>>([CreateTicket("FHIR-99")]),
            ]);
        Fixture fixture = Fixture.CreateWithDiscovery(startRunning: true, discovery);

        await fixture.RunForAsync(TimeSpan.FromMilliseconds(400));

        Assert.True(discovery.CallCount >= 2);
        Assert.NotNull(await fixture.Store.GetByKeyAsync("FHIR-99", "fhir", CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_SelectionUnavailableRetriesWithoutConsumingAuthoringAttempts()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        fixture.Options.Value.LabelsToInclude = ["cohort"];
        JiraIssueSummaryEntry[] tickets = [CreateTicket("FHIR-1"), CreateTicket("FHIR-2")];
        TaskCompletionSource retryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowRetry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeDiscovery discovery = new(
        [
            _ => Task.FromResult<IReadOnlyList<JiraIssueSummaryEntry>>(tickets),
            async _ =>
            {
                retryEntered.TrySetResult();
                await allowRetry.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return tickets;
            },
        ]);
        IOptions<ProcessingServiceOptions> options = Options.Create(new ProcessingServiceOptions
        {
            DatabasePath = fixture.DatabasePath,
            StartProcessingOnStartup = true,
            SyncSchedule = "00:00:00.050",
        });
        ProcessingLifecycleService lifecycle = new(options);
        JiraTicketSelectionUnavailableException failure = new("Jira selection is unavailable.");
        fixture.Matcher.Enqueue((_, _, _) => throw failure);
        fixture.Matcher.Enqueue((keys, _, _) =>
        {
            lifecycle.Stop();
            return Task.FromResult(keys);
        });
        JiraTicketSyncService syncService = new(
            discovery,
            fixture.SourceStore,
            fixture.AuthoringStore,
            fixture.Coordinator,
            new JiraProcessingFilterResolver(),
            fixture.Options,
            NullLogger<JiraTicketSyncService>.Instance);
        SignalingLogger logger = new();
        using JiraTicketSyncWorker worker = new(syncService, lifecycle, options, logger);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.Same(failure, await logger.Failure.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(fixture.Matcher.Calls);
            Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
            Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_items"));
            Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
            foreach (JiraIssueSummaryEntry ticket in tickets)
            {
                JiraProcessingSourceTicketRecord source = Assert.IsType<JiraProcessingSourceTicketRecord>(
                    await fixture.SourceStore.GetByKeyAsync(ticket.Key, "fhir", CancellationToken.None));
                Assert.Equal(ticket.UpdatedAt, source.LastUpdated);
                Assert.Null(source.ProcessingStatus);
                Assert.Null(source.ProcessingError);
                Assert.Equal(0, source.ProcessingAttemptCount);
            }

            allowRetry.SetResult();
            await logger.Success.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, discovery.CallCount);
            Assert.Equal([true, true], discovery.RunBackedRequests);
            Assert.Equal(2, fixture.Matcher.Calls.Count);
            Assert.All(fixture.Matcher.Calls, call =>
            {
                Assert.Equal(["FHIR-1", "FHIR-2"], call.Keys);
                Assert.Equal(["cohort"], call.Filters.LabelsToInclude);
            });
            AuthoringRunRecord run = Assert.IsType<AuthoringRunRecord>(
                await fixture.AuthoringStore.GetOldestQueuedRunAsync(fixture.Coordinator.ProcessorKind));
            IReadOnlyList<AuthoringRunItemRecord> items = await fixture.AuthoringStore.GetRunItemsAsync(run.Id);
            Assert.Equal(2, items.Count);
            Assert.All(items, item =>
            {
                Assert.Equal(AuthoringStatusValues.Items.Pending, item.Status);
                Assert.Equal(0, item.AttemptCount);
                Assert.Null(item.Error);
            });
            Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
            Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
            Assert.Equal(0, fixture.Scalar("SELECT SUM(ProcessingAttemptCount) FROM jira_processing_source_tickets"));
            Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
                fixture.Coordinator.ProcessorKind, run.Id));
            AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(run.Id, items[0].Id));
            Assert.Equal(1, claim.AttemptNumber);
        }
        finally
        {
            allowRetry.TrySetResult();
            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stopTimeout.Token);
        }
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteAsync_ExitsCleanly_OnCancellation()
    {
        Fixture fixture = Fixture.Create(startRunning: true, [], syncSchedule: "00:00:30");

        await fixture.Worker.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await fixture.Worker.StopAsync(CancellationToken.None);

        Assert.NotNull(fixture.Worker.ExecuteTask);
        Assert.True(fixture.Worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteAsync_Returns_WhenSyncScheduleIsInvalid()
    {
        Fixture fixture = Fixture.Create(startRunning: true, [CreateTicket("FHIR-1")], syncSchedule: "not-a-timespan");

        await fixture.Worker.StartAsync(CancellationToken.None);
        Assert.NotNull(fixture.Worker.ExecuteTask);
        await fixture.Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(1));
        await fixture.Worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, fixture.Discovery.CallCount);
    }

    private static JiraIssueSummaryEntry CreateTicket(string key) => new()
    {
        Key = key,
        ProjectKey = "FHIR",
        Title = "Title",
        Type = "Change Request",
        Status = "Triaged",
        WorkGroup = "FHIR-I",
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class Fixture
    {
        public required JiraTicketSyncWorker Worker { get; init; }
        public required JiraProcessingSourceTicketStore Store { get; init; }
        public required FakeDiscovery Discovery { get; init; }

        public static Fixture Create(bool startRunning, IReadOnlyList<JiraIssueSummaryEntry> tickets, string syncSchedule = "00:00:00.050")
        {
            FakeDiscovery discovery = new([_ => Task.FromResult(tickets)]);
            return CreateWithDiscovery(startRunning, discovery, syncSchedule);
        }

        public static Fixture CreateWithDiscovery(bool startRunning, FakeDiscovery discovery, string syncSchedule = "00:00:00.050")
        {
            string dbPath = Path.Combine(AppContext.BaseDirectory, $"jira-sync-worker-{Guid.NewGuid():N}.db");
            JiraProcessingSourceTicketStore store = new(dbPath);
            IOptions<JiraProcessingOptions> jiraOptions = Options.Create(new JiraProcessingOptions
            {
                AgentCliCommand = "agent {ticketKey}",
                JiraSourceAddress = "http://source",
                SourceTicketShape = "fhir",
            });
            IOptions<ProcessingServiceOptions> processingOptions = Options.Create(new ProcessingServiceOptions
            {
                DatabasePath = dbPath,
                SyncSchedule = syncSchedule,
                StartProcessingOnStartup = startRunning,
            });
            ProcessingLifecycleService lifecycle = new(processingOptions);
            JiraProcessingDatabase processingDatabase = new(
                dbPath,
                NullLogger<JiraProcessingDatabase>.Instance);
            processingDatabase.Initialize();
            AuthoringRunStore authoringStore = new(processingDatabase);
            JiraProcessingFilterResolver filterResolver = new();
            JiraAuthoringRunCoordinator coordinator = new(
                authoringStore,
                store,
                new JiraConfiguredTicketSelector(store, new TestJiraTicketLabelMatcher()),
                filterResolver,
                jiraOptions);
            JiraTicketSyncService syncService = new(
                discovery,
                store,
                authoringStore,
                coordinator,
                filterResolver,
                jiraOptions,
                NullLogger<JiraTicketSyncService>.Instance);
            JiraTicketSyncWorker worker = new(syncService, lifecycle, processingOptions, NullLogger<JiraTicketSyncWorker>.Instance);
            return new Fixture { Worker = worker, Store = store, Discovery = discovery };
        }

        public async Task RunForAsync(TimeSpan duration)
        {
            using CancellationTokenSource cts = new();
            await Worker.StartAsync(cts.Token);
            await Task.Delay(duration);
            await cts.CancelAsync();
            await Worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class SignalingLogger : ILogger<JiraTicketSyncWorker>
    {
        public TaskCompletionSource<Exception> Failure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Success { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Failure.TrySetResult(exception);
            }
            else if (logLevel == LogLevel.Debug)
            {
                Success.TrySetResult();
            }
        }
    }

    private sealed class FakeDiscovery(IReadOnlyList<Func<ResolvedJiraProcessingFilters, Task<IReadOnlyList<JiraIssueSummaryEntry>>>> responders) : IJiraTicketDiscoveryClient
    {
        private int _callIndex;
        private readonly object _lock = new();

        public int CallCount { get; private set; }
        public List<bool> RunBackedRequests { get; } = [];

        public Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsForModeAsync(
            ResolvedJiraProcessingFilters filters,
            bool runBacked,
            CancellationToken ct)
        {
            RunBackedRequests.Add(runBacked);
            return ListTicketsAsync(filters, ct);
        }

        public Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(ResolvedJiraProcessingFilters filters, CancellationToken ct)
        {
            Func<ResolvedJiraProcessingFilters, Task<IReadOnlyList<JiraIssueSummaryEntry>>> responder;
            lock (_lock)
            {
                CallCount++;
                int index = Math.Min(_callIndex, responders.Count - 1);
                _callIndex++;
                responder = responders[index];
            }
            return responder(filters);
        }

        public Task<JiraIssueSummaryEntry?> GetTicketAsync(string key, string sourceTicketShape, CancellationToken ct)
            => Task.FromResult<JiraIssueSummaryEntry?>(null);

        public Task MarkProcessedAsync(string key, string sourceTicketShape, CancellationToken ct) => Task.CompletedTask;
    }
}
