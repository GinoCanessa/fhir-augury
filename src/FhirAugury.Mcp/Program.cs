using Fhiraugury;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

string orchestratorAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_ORCHESTRATOR") ?? "http://localhost:5151";
string jiraAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_JIRA_GRPC") ?? "http://localhost:5161";
string zulipAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_ZULIP_GRPC") ?? "http://localhost:5171";
string confluenceAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_CONFLUENCE_GRPC") ?? "http://localhost:5181";
string githubAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_GITHUB_GRPC") ?? "http://localhost:5191";

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Logging must go to stderr (stdout is the MCP stdio transport)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Register gRPC channels as singletons so they are disposed on shutdown
GrpcChannel orchestratorChannel = GrpcChannel.ForAddress(orchestratorAddr);
GrpcChannel jiraChannel = GrpcChannel.ForAddress(jiraAddr);
GrpcChannel zulipChannel = GrpcChannel.ForAddress(zulipAddr);
GrpcChannel confluenceChannel = GrpcChannel.ForAddress(confluenceAddr);
GrpcChannel githubChannel = GrpcChannel.ForAddress(githubAddr);

builder.Services.AddSingleton(orchestratorChannel);
builder.Services.AddSingleton(jiraChannel);
builder.Services.AddSingleton(zulipChannel);
builder.Services.AddSingleton(confluenceChannel);
builder.Services.AddSingleton(githubChannel);

// Register gRPC clients
builder.Services.AddSingleton(new OrchestratorService.OrchestratorServiceClient(orchestratorChannel));
builder.Services.AddSingleton(new SourceService.SourceServiceClient(orchestratorChannel));
builder.Services.AddKeyedSingleton("jira", new SourceService.SourceServiceClient(jiraChannel));
builder.Services.AddKeyedSingleton("zulip", new SourceService.SourceServiceClient(zulipChannel));
builder.Services.AddKeyedSingleton("confluence", new SourceService.SourceServiceClient(confluenceChannel));
builder.Services.AddKeyedSingleton("github", new SourceService.SourceServiceClient(githubChannel));
builder.Services.AddSingleton(new JiraService.JiraServiceClient(jiraChannel));
builder.Services.AddSingleton(new ZulipService.ZulipServiceClient(zulipChannel));
builder.Services.AddSingleton(new ConfluenceService.ConfluenceServiceClient(confluenceChannel));
builder.Services.AddSingleton(new GitHubService.GitHubServiceClient(githubChannel));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

IHost app = builder.Build();
await app.RunAsync();
