using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Api;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processing.Jira.Common.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.Processing.Jira.Common.Tests.Hosting;

public class FakeJiraProcessorHostTests
{
    [Fact]
    public async Task FakeHost_EnqueuesClaimsRendersAndMarksComplete()
    {
        FakeDiscovery discovery = new(CreateTicket("FHIR-1"));
        FakeRunner runner = new();
        ServiceCollection services = new();
        services.AddJiraProcessing(CreateConfiguration(), defaults: new JiraProcessingFilterDefaults { TicketStatusesToProcess = ["Triaged"] });
        services.AddSingleton<IJiraTicketDiscoveryClient>(discovery);
        services.AddSingleton<IJiraAgentCliRunner>(runner);
        services.AddSingleton<IJiraAgentExtensionTokenProvider>(new FakeTokenProvider());
        await using ServiceProvider provider = services.BuildServiceProvider();
        JiraProcessingSourceTicketStore store = provider.GetRequiredService<JiraProcessingSourceTicketStore>();

        Microsoft.AspNetCore.Http.IResult enqueueResult = await JiraProcessingTicketEndpointHandler.EnqueueTicketAsync(
            "FHIR-1",
            null,
            provider.GetRequiredService<IJiraTicketDiscoveryClient>(),
            store,
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FhirAugury.Processing.Jira.Common.Configuration.JiraProcessingOptions>>(),
            CancellationToken.None);
        IReadOnlyList<JiraProcessingSourceTicketRecord> pending = await store.GetPendingAsync(1, CancellationToken.None);
        bool claimed = await store.ClaimItemAsync(pending[0], DateTimeOffset.UtcNow, CancellationToken.None);
        await provider.GetRequiredService<IProcessingWorkItemHandler<JiraProcessingSourceTicketRecord>>().ProcessAsync(pending[0], CancellationToken.None);

        Assert.NotNull(enqueueResult);
        Assert.True(claimed);
        Assert.Equal(ProcessingStatusValues.Complete, pending[0].ProcessingStatus);
        Assert.Contains("--repo", runner.LastCommand!.Arguments);
        Assert.Contains("HL7/fhir", runner.LastCommand.Arguments);
        Assert.Equal(["FHIR-1:fhir"], discovery.Marked);
    }

    private static IConfiguration CreateConfiguration()
    {
        Dictionary<string, string?> values = new()
        {
            ["Processing:DatabasePath"] = Path.Combine(AppContext.BaseDirectory, $"fake-host-{Guid.NewGuid():N}.db"),
            ["Processing:SyncSchedule"] = "00:05:00",
            ["Processing:OrphanedInProgressThreshold"] = "00:10:00",
            ["Processing:MaxConcurrentProcessingThreads"] = "1",
            ["Processing:Jira:AgentCliCommand"] = "agent {repoFilters} {ticketKey} --db {dbPath}",
            ["Processing:Jira:JiraSourceAddress"] = "http://source",
            ["Processing:Jira:SourceTicketShape"] = "fhir",
            ["Processing:Jira:MarkUpstreamProcessedOnSuccess"] = "true",
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static JiraIssueSummaryEntry CreateTicket(string key) => new()
    {
        Key = key,
        ProjectKey = "FHIR",
        Title = "Title",
        Type = "Change Request",
        Status = "Triaged",
        WorkGroup = "FHIR-I",
    };

    private sealed class FakeTokenProvider : IJiraAgentExtensionTokenProvider
    {
        public Task<IReadOnlyDictionary<string, string>> GetTokensAsync(JiraProcessingSourceTicketRecord ticket, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["repoFilters"] = "--repo HL7/fhir" });
    }

    private sealed class FakeRunner : IJiraAgentCliRunner
    {
        public JiraAgentCommand? LastCommand { get; private set; }
        public Task<JiraAgentResult> RunAsync(JiraAgentCommand command, JiraAgentCommandContext context, CancellationToken ct)
        {
            LastCommand = command;
            return Task.FromResult(new JiraAgentResult(0, "ok", string.Empty, TimeSpan.Zero, false));
        }
    }

    private sealed class FakeDiscovery(JiraIssueSummaryEntry ticket) : IJiraTicketDiscoveryClient
    {
        public List<string> Marked { get; } = [];
        public Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(ResolvedJiraProcessingFilters filters, CancellationToken ct) => Task.FromResult<IReadOnlyList<JiraIssueSummaryEntry>>([ticket]);
        public Task<JiraIssueSummaryEntry?> GetTicketAsync(string key, string sourceTicketShape, CancellationToken ct) => Task.FromResult<JiraIssueSummaryEntry?>(ticket.Key == key ? ticket : null);
        public Task MarkProcessedAsync(string key, string sourceTicketShape, CancellationToken ct)
        {
            Marked.Add($"{key}:{sourceTicketShape}");
            return Task.CompletedTask;
        }
    }
}
