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
        path: "/health",
        displayName: "Health Check",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Checks the health of the source service.",
            Method = HttpMethod.Get,
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild indexes",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of all index types.",
            Method = HttpMethod.Get,
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild bm25",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of bm25 index.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=bm25");

                return Task.CompletedTask;
            }
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild cross-refs",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of cross-references.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=cross-refs");

                return Task.CompletedTask;
            }
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild fts",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of FTS.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=fts");

                return Task.CompletedTask;
            }
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
        path: "/rebuild-index",
        displayName: "Rebuild indexes",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of all index types.",
            Method = HttpMethod.Get,
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild bm25",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of bm25 index.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=bm25");

                return Task.CompletedTask;
            }
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild cross-refs",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of cross-references.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=cross-refs");

                return Task.CompletedTask;
            }
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild fts",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of FTS.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=fts");

                return Task.CompletedTask;
            }
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
        path: "/health",
        displayName: "Health Check",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Checks the health of the source service.",
            Method = HttpMethod.Get,
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild indexes",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of all index types.",
            Method = HttpMethod.Get,
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild bm25",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of bm25 index.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=bm25");

                return Task.CompletedTask;
            }
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild cross-refs",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of cross-references.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=cross-refs");

                return Task.CompletedTask;
            }
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild fts",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of FTS.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=fts");

                return Task.CompletedTask;
            }
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
        path: "/health",
        displayName: "Health Check",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Checks the health of the source service.",
            Method = HttpMethod.Get,
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild indexes",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of all index types.",
            Method = HttpMethod.Get,
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild bm25",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of bm25 index.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=bm25");

                return Task.CompletedTask;
            }
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild cross-refs",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of cross-references.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=cross-refs");

                return Task.CompletedTask;
            }
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild fts",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of FTS.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=fts");

                return Task.CompletedTask;
            }
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
        path: "/rebuild-index",
        displayName: "Rebuild indexes",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuilds for all active services.",
            Method = HttpMethod.Get,
        }
    )
    .WithHttpCommand(
        path: "/rebuild-index",
        displayName: "Rebuild cross-refs",
        commandOptions: new HttpCommandOptions()
        {
            Description = "Starts index rebuild of cross-references.",
            Method = HttpMethod.Get,
            PrepareRequest = (ctx) =>
            {
                if (ctx.Request.RequestUri is null)
                {
                    throw new ArgumentException(nameof(ctx.Request.RequestUri));
                }

                string prefix = ctx.Request.RequestUri.Query.Length > 0 ? "&" : "?";

                ctx.Request.RequestUri = new Uri(ctx.Request.RequestUri.ToString() + prefix + "type=cross-refs");

                return Task.CompletedTask;
            }
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

builder.Build().Run();
