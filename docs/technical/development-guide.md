# Development Guide

This guide covers everything you need to set up a development environment,
build, test, and contribute to FHIR Augury. It complements the
[quickstart guide](../development.md) with deeper technical details.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- A text editor or IDE (Visual Studio, VS Code with C# Dev Kit, Rider)
- Git
- [.NET Aspire workload](https://learn.microsoft.com/en-us/dotnet/aspire/) (optional, for orchestrated development)
- Docker (optional, for running the full stack)

## Getting Started

```bash
git clone https://github.com/GinoCanessa/fhir-augury.git
cd fhir-augury
dotnet build fhir-augury.slnx
```

## Building

```bash
# Build the entire solution
dotnet build fhir-augury.slnx

# Build a specific project
dotnet build src/FhirAugury.Source.Jira

# Build in Release mode
dotnet build fhir-augury.slnx -c Release
```

### Build Configuration

The solution uses shared build properties:

- **`src/common.props`** — Shared by all source projects: targets `net10.0`,
  C# 14, nullable enabled, implicit usings, timestamp-based versioning
  (`yyyy.MMdd.HHmm`)
- **`src/Directory.Build.props`** — Imports `common.props` for all source
  projects
- **`tests/Directory.Build.props`** — Configures test projects: `net10.0`,
  C# 14, `IsPackable=false`

### Source Generation

Database record types across all v2 projects use `cslightdbgen.sqlitegen`, a
Roslyn source generator that produces CRUD code at compile time. The pattern is:

```csharp
[LdgSQLiteTable("my_items")]
public partial record class MyItemRecord
{
    [LdgSQLiteKey]
    public long Id { get; set; }

    [LdgSQLiteUnique]
    public string UniqueId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    // ... source generator emits Insert, Update, Delete, SelectAll, etc.
}
```

The `partial` keyword is required. Generated code updates automatically on the
next build — no manual code generation step is needed.

## Running Tests

```bash
# Run all tests
dotnet test fhir-augury.slnx

# Run a specific test project
dotnet test tests/FhirAugury.Source.Jira.Tests

# Run with verbose output
dotnet test fhir-augury.slnx --verbosity normal

# Run with code coverage
dotnet test fhir-augury.slnx --collect:"XPlat Code Coverage"
```

### Test Projects

| Project | What It Tests |
|---------|---------------|
| `FhirAugury.Common.Tests` | Shared library: caching, database helpers, text utilities |
| `FhirAugury.Source.Jira.Tests` | Jira source: ingestion, indexing, gRPC API |
| `FhirAugury.Source.Zulip.Tests` | Zulip source: ingestion, indexing, gRPC API |
| `FhirAugury.Source.Confluence.Tests` | Confluence source: ingestion, indexing, gRPC API |
| `FhirAugury.Source.GitHub.Tests` | GitHub source: ingestion, indexing, gRPC API |
| `FhirAugury.Orchestrator.Tests` | Orchestrator: unified search, cross-refs, related items |
| `FhirAugury.McpShared.Tests` | MCP server tool functions (xUnit + NSubstitute + Grpc.Core.Testing) |

### Test Infrastructure

- **Framework:** xUnit with `xunit.runner.visualstudio`
- **Coverage:** coverlet.collector
- **Database strategy:** Unit tests use in-memory SQLite
  (`Data Source=:memory:`) for speed

## Running Services

### Individual Source Services

Each source service runs independently with its own SQLite database:

```bash
# Run a single source service
dotnet run --project src/FhirAugury.Source.Jira
# Starts on HTTP :5160, gRPC :5161

dotnet run --project src/FhirAugury.Source.Zulip
# Starts on HTTP :5170, gRPC :5171

dotnet run --project src/FhirAugury.Source.Confluence
# Starts on HTTP :5180, gRPC :5181

dotnet run --project src/FhirAugury.Source.GitHub
# Starts on HTTP :5190, gRPC :5191
```

### Orchestrator

The orchestrator requires source services to be running. It connects to them
via gRPC:

```bash
dotnet run --project src/FhirAugury.Orchestrator
# Starts on HTTP :5150, gRPC :5151
```

### All Services with .NET Aspire

Instead of starting each service individually, use the Aspire AppHost to
launch everything at once:

```bash
# One-time: install the Aspire workload
dotnet workload install aspire

# Start all services with the Aspire dashboard
dotnet run --project src/FhirAugury.AppHost
```

The Aspire dashboard (URL shown in console output) provides real-time logs,
distributed traces, and metrics across all services. All services use the same
fixed ports as when running individually.

### MCP Server (Stdio)

The stdio MCP server connects to the orchestrator via gRPC. Configure the
orchestrator endpoint via environment variables:

```bash
dotnet run --project src/FhirAugury.McpStdio
```

The stdio server communicates with LLM clients via stdin/stdout using JSON-RPC.
All logging goes to stderr to avoid interfering with the transport. It is also
packaged as the `fhir-augury-mcp` dotnet tool. See `mcp-config-examples/` for
client configuration examples.

### MCP Server (HTTP)

The HTTP MCP server runs as an ASP.NET Core web application on port 5200,
exposing the `/mcp` endpoint for HTTP/SSE transport:

```bash
dotnet run --project src/FhirAugury.McpHttp
# Starts on HTTP :5200, endpoint /mcp
```

McpHttp includes Aspire ServiceDefaults and is registered in the AppHost.

### CLI

The CLI connects to the orchestrator via gRPC and uses a JSON-in/JSON-out
interface:

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"search","query":"patient"}' --pretty

# Get help for all commands
dotnet run --project src/FhirAugury.Cli -- --help --pretty
```

## Local Configuration

Each service reads `appsettings.local.json` (gitignored) for local overrides.
Environment variables are also supported with source-specific prefixes:

| Source | Env Var Prefix |
|--------|----------------|
| Jira | `FHIR_AUGURY_JIRA_` |
| Zulip | `FHIR_AUGURY_ZULIP_` |
| Confluence | `FHIR_AUGURY_CONFLUENCE_` |
| GitHub | `FHIR_AUGURY_GITHUB_` |

Example `appsettings.local.json` for a source service:

```json
{
  "Jira": {
    "BaseUrl": "https://jira.hl7.org",
    "Cookie": "JSESSIONID=..."
  }
}
```

## Docker

```bash
# Run all services (full stack)
docker compose --profile full up -d --build

# Run Jira + Zulip + Orchestrator only
docker compose --profile jira-zulip up -d --build

# Run Jira standalone (no orchestrator)
docker compose --profile jira-only up -d --build

# View logs
docker compose logs -f orchestrator
docker compose logs -f source-jira

# Rebuild after code changes
docker compose --profile full up -d --build
```

Docker Compose defines 9 volumes: 4 cache volumes (`jira-cache`, `zulip-cache`,
`confluence-cache`, `github-cache`) for raw API responses, and 5 data volumes
(`jira-data`, `zulip-data`, `confluence-data`, `github-data`,
`orchestrator-data`) for SQLite databases. Cache volumes should be preserved
across upgrades; data volumes can be deleted to force a rebuild from cache.

> **Tip:** For development, consider using [.NET Aspire](#all-services-with-net-aspire)
> instead of Docker — it provides faster iteration (no image rebuilds) and an
> integrated dashboard with logs, traces, and metrics.

## Code Conventions

### Language and Framework

- **C# 14** with nullable reference types enabled
- **.NET 10** target framework
- **Implicit usings** enabled
- **File-scoped namespaces** (single `namespace X;` per file)

### Naming

- PascalCase for types, methods, properties
- camelCase for local variables and parameters
- Prefix private fields with `_` (e.g., `_connection`)
- Use descriptive names; avoid abbreviations

### Record Types

Database records are `partial record class` types with source-generator
attributes. The `partial` keyword is required for the source generator.

### Dependency Injection

Each v2 service registers its own components via standard ASP.NET Core DI in
`Program.cs`. The typical registration pattern for a source service:

- Source-specific database (SQLite connection, schema init)
- Indexer (FTS5 setup and search)
- Ingestion pipeline (downloader, mapper, cache)
- gRPC services (`SourceService` + source-specific service)
- Hosted workers (`ScheduledIngestionWorker`)
- `IResponseCache` (file-system cache)
- `IHttpClientFactory` (for source API HTTP clients)

The orchestrator registers:

- Orchestrator database (cross-reference scan state)
- `SourceRouter` (gRPC channels to sources)
- `UnifiedSearchService`, `RelatedItemFinder`
- `ServiceHealthMonitor`, `HealthCheckWorker`
- gRPC service (`OrchestratorService`)

### Error Handling

- **gRPC:** `GrpcErrorMapper` in `FhirAugury.Common` maps exceptions to gRPC
  status codes (NotFound, Unavailable, Internal, etc.)
- **HTTP:** `HttpRetryHelper` retries transient failures (429/5xx) with
  exponential backoff + jitter. Respects `Retry-After` headers.
- **Logging:** Structured logging via `ILogger` throughout

### Configuration

- Use `IOptions<T>` pattern for configuration binding
- Environment variables with source-specific prefixes (see above)
- `appsettings.local.json` for local development (gitignored)

## Adding a New Source

To add a new data source (e.g., `Source.Slack`):

1. **Create proto** — Add `slack.proto` to `protos/` defining `SlackService`
   with source-specific RPCs
2. **Create project** — Add `src/FhirAugury.Source.Slack/` following the
   standard structure: `Api/`, `Cache/`, `Configuration/`, `Database/`,
   `Indexing/`, `Ingestion/`, `Workers/`, `Program.cs`
3. **Implement SourceService** — Implement the common `SourceService` gRPC
   contract (Search, GetItem, ListItems, etc.)
4. **Implement SlackService** — Implement source-specific RPCs
5. **Add Dockerfile** — Copy from an existing source and adjust
6. **Add to docker-compose.yml** — Define the service with HTTP/gRPC ports,
   cache and data volumes, health check
7. **Add to AppHost** — Register the project in `src/FhirAugury.AppHost/AppHost.cs`
   with `AddProject<>()`, HTTP/gRPC endpoints, and add it to the orchestrator's
   `WaitFor()` chain
8. **Register in orchestrator** — Add the source endpoint to the orchestrator's
   configuration so `SourceRouter` can create a gRPC channel to it
9. **Add tests** — Create `tests/FhirAugury.Source.Slack.Tests/`

## Versioning

Version numbers are generated automatically at build time using the timestamp
format `yyyy.MMdd.HHmm` (defined in `src/common.props`). Every build produces
a unique version number without manual version bumping.

## Solution Structure

See [Project Structure](project-structure.md) for a detailed breakdown of the
code organization.
