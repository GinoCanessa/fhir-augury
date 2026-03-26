var builder = DistributedApplication.CreateBuilder(args);

// ── Source services (pinned ports matching existing convention) ───
var jira = builder.AddProject<Projects.FhirAugury_Source_Jira>("source-jira")
    .WithHttpEndpoint(port: 5160, targetPort: 5160, name: "http", isProxied: false)
    .WithEndpoint("grpc", e =>
    {
        e.Port = 5161;
        e.TargetPort = 5161;
        e.Transport = "http2";
        e.IsProxied = false;
    });

var zulip = builder.AddProject<Projects.FhirAugury_Source_Zulip>("source-zulip")
    .WithHttpEndpoint(port: 5170, targetPort: 5170, name: "http", isProxied: false)
    .WithEndpoint("grpc", e =>
    {
        e.Port = 5171;
        e.TargetPort = 5171;
        e.Transport = "http2";
        e.IsProxied = false;
    });

var confluence = builder.AddProject<Projects.FhirAugury_Source_Confluence>("source-confluence")
    .WithHttpEndpoint(port: 5180, targetPort: 5180, name: "http", isProxied: false)
    .WithEndpoint("grpc", e =>
    {
        e.Port = 5181;
        e.TargetPort = 5181;
        e.Transport = "http2";
        e.IsProxied = false;
    });

var github = builder.AddProject<Projects.FhirAugury_Source_GitHub>("source-github")
    .WithHttpEndpoint(port: 5190, targetPort: 5190, name: "http", isProxied: false)
    .WithEndpoint("grpc", e =>
    {
        e.Port = 5191;
        e.TargetPort = 5191;
        e.Transport = "http2";
        e.IsProxied = false;
    });

// ── Orchestrator ─────────────────────────────────────────────────
builder.AddProject<Projects.FhirAugury_Orchestrator>("orchestrator")
    .WithHttpEndpoint(port: 5150, targetPort: 5150, name: "http", isProxied: false)
    .WithEndpoint("grpc", e =>
    {
        e.Port = 5151;
        e.TargetPort = 5151;
        e.Transport = "http2";
        e.IsProxied = false;
    })
    .WaitFor(jira)
    .WaitFor(zulip)
    .WaitFor(confluence)
    .WaitFor(github);

builder.Build().Run();
