using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processing.Jira.Common.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Hosting;

public static class JiraProcessingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Jira processing common layer for a concrete processor.
    /// Concrete services typically call this during startup, map the common
    /// Processing endpoints, then replace <see cref="IJiraAgentExtensionTokenProvider"/>
    /// or <see cref="IJiraAgentCliRunner"/> when they need processor-specific
    /// tokens or tests:
    /// <code>
    /// builder.Services.AddJiraProcessing(builder.Configuration, defaults: new JiraProcessingFilterDefaults
    /// {
    ///     TicketStatusesToProcess = ["Triaged"]
    /// });
    /// app.MapProcessingEndpoints&lt;JiraProcessingSourceTicketRecord&gt;();
    /// app.MapJiraProcessingTicketEndpoints();
    /// </code>
    /// Concrete processors remain responsible for defining output tables, deleting/upserting prior output for the ticket,
    /// and adding extension tokens such as {repoFilters}.
    /// </summary>
    public static IServiceCollection AddJiraProcessing(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<JiraProcessingOptions>? configure = null,
        JiraProcessingFilterDefaults? defaults = null)
    {
        services.AddOptions<ProcessingServiceOptions>()
            .Bind(configuration.GetSection(ProcessingServiceOptions.SectionName))
            .Validate(options => !options.Validate().Any(), "Processing configuration is invalid.")
            .ValidateOnStart();

        services.AddOptions<JiraProcessingOptions>()
            .Bind(configuration.GetSection(JiraProcessingOptions.SectionName))
            .Configure(options => configure?.Invoke(options))
            .Validate(options => !options.Validate().Any(), "Processing:Jira configuration is invalid.")
            .ValidateOnStart();

        services.AddSingleton<ProcessingLifecycleService>();
        services.AddSingleton(defaults ?? JiraProcessingFilterDefaults.None);
        services.AddSingleton<JiraProcessingFilterResolver>(sp => new JiraProcessingFilterResolver(sp.GetRequiredService<JiraProcessingFilterDefaults>()));
        services.AddSingleton<JiraLocalProcessingRequestFactory>();
        services.AddSingleton<JiraProcessingSourceTicketStore>();
        services.AddSingleton<IProcessingWorkItemStore<JiraProcessingSourceTicketRecord>>(sp => sp.GetRequiredService<JiraProcessingSourceTicketStore>());
        services.AddSingleton<JiraAgentCommandRenderer>();
        services.AddSingleton<IJiraAgentCliRunner, JiraAgentCliRunner>();
        services.AddSingleton<IJiraAgentExtensionTokenProvider, EmptyJiraAgentExtensionTokenProvider>();
        services.AddSingleton<IProcessingWorkItemHandler<JiraProcessingSourceTicketRecord>, JiraTicketProcessingHandler>();
        services.AddSingleton<ProcessingQueueRunner<JiraProcessingSourceTicketRecord>>();
        services.AddHostedService<ProcessingHostedService<JiraProcessingSourceTicketRecord>>();
        services.AddHttpClient<DirectJiraTicketDiscoveryClient>((sp, client) =>
        {
            JiraProcessingOptions options = sp.GetRequiredService<IOptions<JiraProcessingOptions>>().Value;
            client.BaseAddress = new Uri(EnsureTrailingSlash(options.JiraSourceAddress));
        });
        services.AddHttpClient<OrchestratorJiraTicketDiscoveryClient>((sp, client) =>
        {
            JiraProcessingOptions options = sp.GetRequiredService<IOptions<JiraProcessingOptions>>().Value;
            string address = options.OrchestratorAddress ?? options.JiraSourceAddress;
            client.BaseAddress = new Uri(EnsureTrailingSlash(address));
        });
        services.AddSingleton<IJiraTicketDiscoveryClient>(sp =>
        {
            JiraProcessingOptions options = sp.GetRequiredService<IOptions<JiraProcessingOptions>>().Value;
            return options.DiscoverySource == JiraTicketDiscoverySource.Orchestrator
                ? sp.GetRequiredService<OrchestratorJiraTicketDiscoveryClient>()
                : sp.GetRequiredService<DirectJiraTicketDiscoveryClient>();
        });
        services.AddSingleton<JiraTicketSyncService>();
        return services;
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
}
