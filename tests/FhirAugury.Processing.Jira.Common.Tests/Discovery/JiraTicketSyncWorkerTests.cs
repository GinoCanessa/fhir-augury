using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
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
            JiraTicketSyncService syncService = new(
                discovery,
                store,
                new JiraProcessingFilterResolver(),
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

    private sealed class FakeDiscovery(IReadOnlyList<Func<ResolvedJiraProcessingFilters, Task<IReadOnlyList<JiraIssueSummaryEntry>>>> responders) : IJiraTicketDiscoveryClient
    {
        private int _callIndex;
        private readonly object _lock = new();

        public int CallCount { get; private set; }

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
