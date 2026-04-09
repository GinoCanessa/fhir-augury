using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.McpShared;

public static class McpHttpRegistration
{
    public static IServiceCollection AddMcpHttpClients(this IServiceCollection services)
    {
        string orchestratorAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_ORCHESTRATOR") ?? "http://localhost:5150";
        string jiraAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_JIRA") ?? "http://localhost:5160";
        string zulipAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_ZULIP") ?? "http://localhost:5170";
        string confluenceAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_CONFLUENCE") ?? "http://localhost:5180";
        string githubAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_GITHUB") ?? "http://localhost:5190";

        services.AddHttpClient("orchestrator", c => c.BaseAddress = new Uri(orchestratorAddr));
        services.AddHttpClient("jira", c => c.BaseAddress = new Uri(jiraAddr));
        services.AddHttpClient("zulip", c => c.BaseAddress = new Uri(zulipAddr));
        services.AddHttpClient("confluence", c => c.BaseAddress = new Uri(confluenceAddr));
        services.AddHttpClient("github", c => c.BaseAddress = new Uri(githubAddr));

        return services;
    }
}
