using Fhiraugury;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

var orchestratorAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_ORCHESTRATOR") ?? "http://localhost:5151";
var jiraAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_JIRA_GRPC") ?? "http://localhost:5161";
var zulipAddr = Environment.GetEnvironmentVariable("FHIR_AUGURY_ZULIP_GRPC") ?? "http://localhost:5171";

var builder = Host.CreateApplicationBuilder(args);

// Logging must go to stderr (stdout is the MCP stdio transport)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Register gRPC channels as singletons so they are disposed on shutdown
var orchestratorChannel = GrpcChannel.ForAddress(orchestratorAddr);
var jiraChannel = GrpcChannel.ForAddress(jiraAddr);
var zulipChannel = GrpcChannel.ForAddress(zulipAddr);

builder.Services.AddSingleton(orchestratorChannel);
builder.Services.AddSingleton(jiraChannel);
builder.Services.AddSingleton(zulipChannel);

// Register gRPC clients
builder.Services.AddSingleton(new OrchestratorService.OrchestratorServiceClient(orchestratorChannel));
builder.Services.AddSingleton(new SourceService.SourceServiceClient(orchestratorChannel));
builder.Services.AddKeyedSingleton("jira", new SourceService.SourceServiceClient(jiraChannel));
builder.Services.AddKeyedSingleton("zulip", new SourceService.SourceServiceClient(zulipChannel));
builder.Services.AddSingleton(new JiraService.JiraServiceClient(jiraChannel));
builder.Services.AddSingleton(new ZulipService.ZulipServiceClient(zulipChannel));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
await app.RunAsync();
