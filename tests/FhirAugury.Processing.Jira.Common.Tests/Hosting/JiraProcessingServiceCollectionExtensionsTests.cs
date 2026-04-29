using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processing.Jira.Common.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Hosting;

public class JiraProcessingServiceCollectionExtensionsTests
{
    [Fact]
    public void AddJiraProcessing_RegistersOptionsStoreDiscoveryAndAgentServices()
    {
        ServiceProvider provider = CreateServices().BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IOptions<JiraProcessingOptions>>().Value);
        Assert.NotNull(provider.GetRequiredService<JiraProcessingSourceTicketStore>());
        Assert.Same(
            provider.GetRequiredService<JiraProcessingSourceTicketStore>(),
            provider.GetRequiredService<IProcessingWorkItemStore<JiraProcessingSourceTicketRecord>>());
        Assert.NotNull(provider.GetRequiredService<JiraProcessingFilterResolver>());
        Assert.NotNull(provider.GetRequiredService<JiraAgentCommandRenderer>());
        Assert.NotNull(provider.GetRequiredService<IJiraAgentCliRunner>());
        Assert.NotNull(provider.GetRequiredService<IJiraTicketDiscoveryClient>());
    }

    [Fact]
    public void AddJiraProcessing_ThrowsHelpfulErrorForInvalidOptions()
    {
        Dictionary<string, string?> values = BaseValues();
        values["Processing:Jira:AgentCliCommand"] = "agent";
        ServiceCollection services = new();
        services.AddJiraProcessing(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<JiraProcessingOptions>>().Value);
        Assert.Contains("Processing:Jira configuration is invalid", ex.Message);
    }

    private static ServiceCollection CreateServices()
    {
        ServiceCollection services = new();
        services.AddJiraProcessing(new ConfigurationBuilder().AddInMemoryCollection(BaseValues()).Build());
        return services;
    }

    private static Dictionary<string, string?> BaseValues() => new()
    {
        ["Processing:DatabasePath"] = Path.Combine(AppContext.BaseDirectory, $"jira-di-{Guid.NewGuid():N}.db"),
        ["Processing:SyncSchedule"] = "00:05:00",
        ["Processing:OrphanedInProgressThreshold"] = "00:10:00",
        ["Processing:MaxConcurrentProcessingThreads"] = "1",
        ["Processing:Jira:AgentCliCommand"] = "agent {ticketKey}",
        ["Processing:Jira:JiraSourceAddress"] = "http://source",
        ["Processing:Jira:SourceTicketShape"] = "fhir",
    };
}
