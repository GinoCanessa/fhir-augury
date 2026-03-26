var builder = DistributedApplication.CreateBuilder(args);

// ── Source services (pinned ports matching existing convention) ───
// Use WithEndpoint callback to configure the auto-discovered "http" endpoint
// (AddProject auto-creates "http" from launchSettings.json in Aspire 13.2+).
var jira = builder.AddProject<Projects.FhirAugury_Source_Jira>("source-jira")
    .WithEndpoint("http", e =>
    {
        e.Port = 5160;
        e.TargetPort = 5160;
        e.IsProxied = false;
    })
    .WithEndpoint("grpc", e =>
    {
        e.Port = 5161;
        e.TargetPort = 5161;
        e.Transport = "http2";
        e.IsProxied = false;
    });

var zulip = builder.AddProject<Projects.FhirAugury_Source_Zulip>("source-zulip")
    .WithEndpoint("http", e =>
    {
        e.Port = 5170;
        e.TargetPort = 5170;
        e.IsProxied = false;
    })
    .WithEndpoint("grpc", e =>
    {
        e.Port = 5171;
        e.TargetPort = 5171;
        e.Transport = "http2";
        e.IsProxied = false;
    })
    .WaitFor(jira);

var confluence = builder.AddProject<Projects.FhirAugury_Source_Confluence>("source-confluence")
    .WithEndpoint("http", e =>
    {
        e.Port = 5180;
        e.TargetPort = 5180;
        e.IsProxied = false;
    })
    .WithEndpoint("grpc", e =>
    {
        e.Port = 5181;
        e.TargetPort = 5181;
        e.Transport = "http2";
        e.IsProxied = false;
    })
    .WithExplicitStart();

var github = builder.AddProject<Projects.FhirAugury_Source_GitHub>("source-github")
    .WithEndpoint("http", e =>
    {
        e.Port = 5190;
        e.TargetPort = 5190;
        e.IsProxied = false;
    })
    .WithEndpoint("grpc", e =>
    {
        e.Port = 5191;
        e.TargetPort = 5191;
        e.Transport = "http2";
        e.IsProxied = false;
    })
    .WaitFor(jira);

// ── Orchestrator ─────────────────────────────────────────────────
var orchestrator = builder.AddProject<Projects.FhirAugury_Orchestrator>("orchestrator")
    .WithEndpoint("http", e =>
    {
        e.Port = 5150;
        e.TargetPort = 5150;
        e.IsProxied = false;
    })
    .WithEndpoint("grpc", e =>
    {
        e.Port = 5151;
        e.TargetPort = 5151;
        e.Transport = "http2";
        e.IsProxied = false;
    })
    .WaitFor(jira)
    .WaitFor(zulip)
    .WaitFor(github);

// ── MCP HTTP server ─────────────────────────────────────────────
builder.AddProject<Projects.FhirAugury_McpHttp>("mcp")
    .WithEndpoint("http", e =>
    {
        e.Port = 5200;
        e.TargetPort = 5200;
        e.IsProxied = false;
    })
    .WaitFor(orchestrator);

builder.Build().Run();
