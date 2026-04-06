# Development Guide

Guide for developing FHIR Augury v2 — prerequisites, building, running services,
and testing.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- Git
- A text editor or IDE (Visual Studio, VS Code with C# Dev Kit, Rider)
- [.NET Aspire workload](https://learn.microsoft.com/en-us/dotnet/aspire/) (optional, for orchestrated development)
- Docker (optional, for containerized deployment)

## Getting Started

```bash
git clone https://github.com/GinoCanessa/fhir-augury.git
cd fhir-augury
dotnet build fhir-augury.slnx
```

## Solution Structure

The v2 architecture uses 13 independent projects:

| Project | Type | Description |
|---------|------|-------------|
| `FhirAugury.Common` | Library | Shared types, API contracts, utilities |
| `FhirAugury.Source.Jira` | Service | Jira source (:5160) |
| `FhirAugury.Source.Zulip` | Service | Zulip source (:5170) |
| `FhirAugury.Source.Confluence` | Service | Confluence source (:5180) |
| `FhirAugury.Source.GitHub` | Service | GitHub source (:5190) |
| `FhirAugury.Orchestrator` | Service | Aggregator + cross-ref (:5150) |
| `FhirAugury.McpStdio` | CLI Tool | MCP server for LLM agents (stdio transport) |
| `FhirAugury.McpHttp` | Service | MCP server for LLM agents (HTTP/SSE transport, :5200) |
| `FhirAugury.McpShared` | Library | Shared MCP tool implementations and HTTP client registration |
| `FhirAugury.Cli` | CLI Tool | Command-line interface |
| `FhirAugury.DevUi` | Blazor Server | Operational dashboard (:5210) |
| `FhirAugury.ServiceDefaults` | Library | Shared Aspire defaults (OpenTelemetry, health, resilience) |
| `FhirAugury.AppHost` | Aspire Host | Orchestrates all services for local development |

Each service has its own SQLite database, file-system cache, and HTTP API.

## Building

```bash
# Build the entire solution
dotnet build fhir-augury.slnx

# Build a specific service
dotnet build src/FhirAugury.Source.Jira

# Build in Release mode
dotnet build fhir-augury.slnx -c Release
```

### Build Configuration

- **`src/common.props`** — Shared properties: .NET 10, C# 14, nullable,
  timestamp-based versioning
- **`src/Directory.Build.props`** — Imports `common.props`
- **`tests/Directory.Build.props`** — Test project configuration


## Running Individual Services

Each source service can run independently:

```bash
# Start Jira source service
dotnet run --project src/FhirAugury.Source.Jira
# → HTTP on :5160

# Start Zulip source service
dotnet run --project src/FhirAugury.Source.Zulip
# → HTTP on :5170

# Start Confluence source service
dotnet run --project src/FhirAugury.Source.Confluence
# → HTTP on :5180

# Start GitHub source service
dotnet run --project src/FhirAugury.Source.GitHub
# → HTTP on :5190

# Start the orchestrator (needs source services running)
dotnet run --project src/FhirAugury.Orchestrator
# → HTTP on :5150
```

## Running All Services with .NET Aspire

Instead of starting each service individually, you can use the Aspire AppHost
to launch all services at once with an integrated dashboard:

```bash
# One-time: install the Aspire workload
dotnet workload install aspire

# Start all services
dotnet run --project src/FhirAugury.AppHost
```

The AppHost registers all source services, the orchestrator, the MCP HTTP
server, the Dev UI, and the CLI tool with their standard ports. The orchestrator waits for
Jira, Zulip, and GitHub to be healthy before starting. Confluence, Dev UI, MCP HTTP,
and the CLI use explicit start and must be started manually from the Aspire
dashboard. The Aspire dashboard (URL shown in the console) provides real-time
logs, distributed traces, and metrics.

### Local Configuration

Each service reads `appsettings.json` and supports `appsettings.local.json`
(gitignored) for local overrides:

```json
// src/FhirAugury.Source.Jira/appsettings.local.json
{
  "Jira": {
    "Cookie": "JSESSIONID=your-cookie-here"
  }
}
```

### Environment Variable Prefixes

Each service uses its own prefix for environment variables:

| Service | Prefix |
|---------|--------|
| Jira | `FHIR_AUGURY_JIRA_` |
| Zulip | `FHIR_AUGURY_ZULIP_` |
| Confluence | `FHIR_AUGURY_CONFLUENCE_` |
| GitHub | `FHIR_AUGURY_GITHUB_` |
| Orchestrator | `FHIR_AUGURY_ORCHESTRATOR_` |

## Running the MCP Server

### Stdio Transport

```bash
# Connect to orchestrator (default)
dotnet run --project src/FhirAugury.McpStdio

# Override service addresses
FHIR_AUGURY_ORCHESTRATOR=http://localhost:5150 \
FHIR_AUGURY_JIRA=http://localhost:5160 \
FHIR_AUGURY_ZULIP=http://localhost:5170 \
dotnet run --project src/FhirAugury.McpStdio
```

The stdio MCP server uses stdio transport — all logging goes to stderr.

### HTTP/SSE Transport

```bash
dotnet run --project src/FhirAugury.McpHttp
# → MCP endpoint at http://localhost:5200/mcp
```

The HTTP MCP server runs as a long-lived ASP.NET service and is also included
in the Aspire AppHost.

## Running the CLI

The CLI uses a JSON-in/JSON-out interface via HTTP:

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"search","query":"patient"}' --pretty
dotnet run --project src/FhirAugury.Cli -- --help --pretty
```

## Running Tests

```bash
# Run all tests
dotnet test fhir-augury.slnx

# Run a specific test project
dotnet test tests/FhirAugury.Source.Jira.Tests
dotnet test tests/FhirAugury.Orchestrator.Tests

# Run with verbose output
dotnet test fhir-augury.slnx --verbosity normal
```

### V2 Test Projects

| Project | Tests |
|---------|-------|
| `FhirAugury.Common.Tests` | Common utilities and types |
| `FhirAugury.Source.Jira.Tests` | Jira service logic |
| `FhirAugury.Source.Zulip.Tests` | Zulip service logic |
| `FhirAugury.Source.Confluence.Tests` | Confluence service logic |
| `FhirAugury.Source.GitHub.Tests` | GitHub service logic |
| `FhirAugury.Orchestrator.Tests` | Orchestrator, cross-ref, search |
| `FhirAugury.McpShared.Tests` | MCP tool functions |

### Test Infrastructure

- **Framework:** xUnit
- **Database:** In-memory SQLite for unit tests, temp files for integration
- **Source generation:** `cslightdbgen.sqlitegen` generates CRUD at compile time

## Docker Development

```bash
# Build and run full stack
docker compose --profile full up -d --build

# View logs
docker compose --profile full logs -f

# Rebuild after code changes
docker compose --profile full up -d --build

# Tear down (preserves volumes)
docker compose --profile full down
```

See [deployment.md](deployment.md) for full Docker Compose and Aspire
documentation.

## Code Conventions

- **C# 14** with nullable reference types
- **.NET 10** target framework
- **File-scoped namespaces** (`namespace X;`)
- **PascalCase** for types, methods, properties
- **camelCase** for locals and parameters
- **`_` prefix** for private fields
- **`partial record class`** for database records (required for source generator)

## Adding a New Source Service

1. Create `src/FhirAugury.Source.{Name}/` with `Microsoft.NET.Sdk.Web`
2. Add HTTP API controllers implementing the standard API endpoints
3. Add database schema, ingestion pipeline, and indexer
4. Configure Kestrel with an HTTP port
5. Create `Dockerfile` following the existing pattern
6. Add service to `docker-compose.yml` with volumes and health check
7. Add the service to `src/FhirAugury.AppHost/AppHost.cs` with HTTP endpoint
8. Register the source in the Orchestrator's `appsettings.json`
9. Add test project in `tests/FhirAugury.Source.{Name}.Tests/`
10. Update documentation
