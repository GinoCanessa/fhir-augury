using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.McpShared;

public static class McpHttpRegistration
{
    public static IServiceCollection AddMcpHttpClients(this IServiceCollection services)
    {
        string orchestratorAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_ORCHESTRATOR") ?? "http://localhost:5150";

        services.AddHttpClient("orchestrator", c => c.BaseAddress = new Uri(orchestratorAddr));

        return services;
    }
}
