using Fhiraugury;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.McpShared;

/// <summary>
/// Shared gRPC client registration used by both McpStdio and McpHttp hosts.
/// </summary>
public static class McpServiceRegistration
{
    public static IServiceCollection AddMcpGrpcClients(
        this IServiceCollection services,
        string? orchestratorAddress = null,
        string? jiraAddress = null,
        string? zulipAddress = null,
        string? confluenceAddress = null,
        string? githubAddress = null)
    {
        string orchestratorAddr = orchestratorAddress
            ?? Environment.GetEnvironmentVariable("FHIR_AUGURY_ORCHESTRATOR")
            ?? "http://localhost:5151";
        string jiraAddr = jiraAddress
            ?? Environment.GetEnvironmentVariable("FHIR_AUGURY_JIRA_GRPC")
            ?? "http://localhost:5161";
        string zulipAddr = zulipAddress
            ?? Environment.GetEnvironmentVariable("FHIR_AUGURY_ZULIP_GRPC")
            ?? "http://localhost:5171";
        string confluenceAddr = confluenceAddress
            ?? Environment.GetEnvironmentVariable("FHIR_AUGURY_CONFLUENCE_GRPC")
            ?? "http://localhost:5181";
        string githubAddr = githubAddress
            ?? Environment.GetEnvironmentVariable("FHIR_AUGURY_GITHUB_GRPC")
            ?? "http://localhost:5191";

        GrpcChannel orchestratorChannel = GrpcChannel.ForAddress(orchestratorAddr);
        GrpcChannel jiraChannel = GrpcChannel.ForAddress(jiraAddr);
        GrpcChannel zulipChannel = GrpcChannel.ForAddress(zulipAddr);
        GrpcChannel confluenceChannel = GrpcChannel.ForAddress(confluenceAddr);
        GrpcChannel githubChannel = GrpcChannel.ForAddress(githubAddr);

        services.AddSingleton(orchestratorChannel);
        services.AddSingleton(jiraChannel);
        services.AddSingleton(zulipChannel);
        services.AddSingleton(confluenceChannel);
        services.AddSingleton(githubChannel);

        services.AddSingleton(new OrchestratorService.OrchestratorServiceClient(orchestratorChannel));
        services.AddSingleton(new SourceService.SourceServiceClient(orchestratorChannel));
        services.AddKeyedSingleton("jira", new SourceService.SourceServiceClient(jiraChannel));
        services.AddKeyedSingleton("zulip", new SourceService.SourceServiceClient(zulipChannel));
        services.AddKeyedSingleton("confluence", new SourceService.SourceServiceClient(confluenceChannel));
        services.AddKeyedSingleton("github", new SourceService.SourceServiceClient(githubChannel));
        services.AddSingleton(new JiraService.JiraServiceClient(jiraChannel));
        services.AddSingleton(new ZulipService.ZulipServiceClient(zulipChannel));
        services.AddSingleton(new ConfluenceService.ConfluenceServiceClient(confluenceChannel));
        services.AddSingleton(new GitHubService.GitHubServiceClient(githubChannel));

        return services;
    }
}
