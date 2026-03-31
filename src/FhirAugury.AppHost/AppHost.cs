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
    })
    .WithHttpCommand(
        endpointName: "http",
        path: "/health",
        displayName: "Health Check",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Checks the health of the source service.",
            Method = HttpMethod.Get,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index",
        displayName: "Rebuild indexes",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of all index types.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=bm25",
        displayName: "Rebuild bm25",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of bm25 index.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=cross-refs",
        displayName: "Rebuild cross-refs",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of cross-references.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=fts",
        displayName: "Rebuild fts",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of FTS.",
            Method = HttpMethod.Post,
        }
    );

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
    .WithHttpCommand(
        path: "/health",
        displayName: "Health Check",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Checks the health of the source service.",
            Method = HttpMethod.Get,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index",
        displayName: "Rebuild indexes",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of all index types.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=bm25",
        displayName: "Rebuild bm25",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of bm25 index.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=cross-refs",
        displayName: "Rebuild cross-refs",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of cross-references.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=fts",
        displayName: "Rebuild fts",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of FTS.",
            Method = HttpMethod.Post,
        }
    )
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
    .WithHttpCommand(
        endpointName: "http",
        path: "/health",
        displayName: "Health Check",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Checks the health of the source service.",
            Method = HttpMethod.Get,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index",
        displayName: "Rebuild indexes",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of all index types.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=bm25",
        displayName: "Rebuild bm25",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of bm25 index.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=cross-refs",
        displayName: "Rebuild cross-refs",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of cross-references.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=fts",
        displayName: "Rebuild fts",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of FTS.",
            Method = HttpMethod.Post,
        }
    )
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
    .WithHttpCommand(
        endpointName: "http",
        path: "/health",
        displayName: "Health Check",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Checks the health of the source service.",
            Method = HttpMethod.Get,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index",
        displayName: "Rebuild indexes",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of all index types.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=bm25",
        displayName: "Rebuild bm25",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of bm25 index.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=cross-refs",
        displayName: "Rebuild cross-refs",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of cross-references.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=fts",
        displayName: "Rebuild fts",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of FTS.",
            Method = HttpMethod.Post,
        }
    )
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
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index",
        displayName: "Rebuild indexes",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuilds for all active services.",
            Method = HttpMethod.Post,
        }
    )
    .WithHttpCommand(
        endpointName: "http",
        path: "/rebuild-index?type=cross-ref",
        displayName: "Rebuild cross-refs",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of cross-references.",
            Method = HttpMethod.Post,
        }
    )
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
    .WaitFor(orchestrator)
    .WithExplicitStart();

// ── CLI tool ─────────────────────────────────────────────────────
IResourceBuilder<ProjectResource> cli = builder.AddProject<Projects.FhirAugury_Cli>("cli")
    .WithExplicitStart();

(string? Type, string DisplayName, string Description)[] indexCommands =
[
    (null, "Rebuild all indexes", "Rebuilds all indexes on all source services via the orchestrator."),
    ("bm25", "Rebuild BM25", "Rebuilds BM25 full-text search indexes."),
    ("fts", "Rebuild FTS", "Rebuilds FTS indexes."),
    ("cross-refs", "Rebuild cross-refs", "Rebuilds cross-reference indexes."),
    ("lookup-tables", "Rebuild lookup tables", "Rebuilds lookup table indexes."),
    ("commits", "Rebuild commits", "Rebuilds commit indexes."),
    ("artifact-map", "Rebuild artifact map", "Rebuilds artifact mapping indexes."),
    ("page-links", "Rebuild page links", "Rebuilds page link indexes."),
];

foreach ((string? type, string displayName, string description) in indexCommands)
{
    string commandName = type is null ? "index-all" : $"index-{type}";
    string url = type is null
        ? "http://localhost:5150/api/v1/rebuild-index"
        : $"http://localhost:5150/api/v1/rebuild-index?type={type}";

    cli.WithCommand(
        name: commandName,
        displayName: displayName,
        executeCommand: async context =>
        {
            using HttpClient http = new();
            try
            {
                HttpResponseMessage response = await http.PostAsync(url, null, context.CancellationToken);
                if (response.IsSuccessStatusCode)
                    return CommandResults.Success();

                string body = await response.Content.ReadAsStringAsync(context.CancellationToken);
                return CommandResults.Failure($"HTTP {(int)response.StatusCode}: {body}");
            }
            catch (HttpRequestException ex)
            {
                return CommandResults.Failure($"Cannot reach orchestrator: {ex.Message}");
            }
        },
        commandOptions: new CommandOptions { Description = description }
    );
}

builder.Build().Run();
