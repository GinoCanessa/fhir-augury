# Development Guide

This guide covers everything you need to set up a development environment,
build, test, and contribute to FHIR Augury.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- A text editor or IDE (Visual Studio, VS Code with C# Dev Kit, Rider)
- Git

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
dotnet build src/FhirAugury.Cli

# Build in Release mode
dotnet build fhir-augury.slnx -c Release
```

### Build Configuration

The solution uses shared build properties:

- **`src/common.props`** — Shared by all source projects: targets `net10.0`,
  C# 14, nullable enabled, timestamp-based versioning (`yyyy.MMdd.HHmm`)
- **`src/Directory.Build.props`** — Imports `common.props` for all source
  projects
- **`tests/Directory.Build.props`** — Configures test projects: `net10.0`,
  C# 14, `IsPackable=false`

### Source Generation

The `FhirAugury.Database` project uses `cslightdbgen.sqlitegen`, a Roslyn
source generator that produces CRUD code at compile time from decorated record
classes. When you add or modify a database record class, the generated code
updates automatically on the next build. No manual code generation step is needed.

## Running Tests

```bash
# Run all tests
dotnet test fhir-augury.slnx

# Run a specific test project
dotnet test tests/FhirAugury.Database.Tests

# Run with verbose output
dotnet test fhir-augury.slnx --verbosity normal

# Run with code coverage
dotnet test fhir-augury.slnx --collect:"XPlat Code Coverage"
```

### Test Projects

| Project | Tests | What It Tests |
|---------|-------|---------------|
| `FhirAugury.Database.Tests` | ~56 | SQLite CRUD, table creation, FTS5 triggers |
| `FhirAugury.Indexing.Tests` | ~45 | BM25 scoring, tokenization, cross-references, unified search |
| `FhirAugury.Sources.Tests` | ~85 | Source parsers/mappers, text sanitization, caching |
| `FhirAugury.Integration.Tests` | ~22 | HTTP API endpoints via `WebApplicationFactory` |
| `FhirAugury.Mcp.Tests` | ~44 | MCP tool functions (search, retrieval, listing, snapshots) |

**Total: ~252 tests**

### Test Infrastructure

- **Framework:** xUnit 2.9.3 with `xunit.runner.visualstudio`
- **Coverage:** coverlet.collector 6.0.4
- **Database strategy:**
  - **Unit tests** use in-memory SQLite (`Data Source=:memory:`) for speed
  - **Integration/MCP tests** use temporary file-backed SQLite with cleanup
- **Test fixtures:** 7 sample files in `tests/TestData/` (Jira XML/JSON, GitHub
  issue/PR JSON, Confluence page/storage XML, Zulip messages JSON)
- **Helpers:**
  - `TestHelper` — Creates in-memory DB with full schema; factory methods for
    sample records
  - `McpTestHelper` — Creates temp file-backed `DatabaseService` with cleanup

### Writing Tests

Follow the existing patterns:

```csharp
public class MyFeatureTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public MyFeatureTests()
    {
        _conn = TestHelper.CreateInMemoryDatabase();
    }

    [Fact]
    public void Should_do_something()
    {
        // Arrange
        var record = TestHelper.CreateSampleJiraIssue("TEST-1");
        record.Insert(_conn);

        // Act
        var result = MyFeature.Process(_conn, "TEST-1");

        // Assert
        Assert.NotNull(result);
    }

    public void Dispose() => _conn.Dispose();
}
```

## Running the CLI

```bash
# Run via dotnet run
dotnet run --project src/FhirAugury.Cli -- [command] [options]

# Example: search
dotnet run --project src/FhirAugury.Cli -- search -q "patient" --db fhir-augury.db
```

## Running the Service

```bash
# Run the HTTP service
dotnet run --project src/FhirAugury.Service

# Service starts on http://localhost:5100 by default
curl http://localhost:5100/health
```

Configure via `src/FhirAugury.Service/appsettings.local.json` (gitignored):

```json
{
  "FhirAugury": {
    "Sources": {
      "jira": {
        "Cookie": "JSESSIONID=..."
      }
    }
  }
}
```

## Running the MCP Server

```bash
# Run the MCP server (stdio transport)
dotnet run --project src/FhirAugury.Mcp -- --db fhir-augury.db
```

The MCP server communicates via stdin/stdout using JSON-RPC. All logging goes
to stderr to avoid interfering with the transport.

## Docker

```bash
# Build and run
docker compose up -d

# View logs
docker compose logs -f fhir-augury

# Rebuild after code changes
docker compose up -d --build
```

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

The service project uses standard ASP.NET Core DI:
- `DatabaseService` — singleton
- `IngestionQueue` — singleton (bounded channel)
- `IngestionWorker`, `ScheduledIngestionService` — hosted services
- `IResponseCache` — singleton
- `IHttpClientFactory` — for source connector HTTP clients

### Error Handling

- Use `HttpRetryHelper` for HTTP calls (handles transient errors)
- Return `IngestionResult` from source operations (includes success/failure
  status, item counts, error messages)
- API endpoints return `ProblemResponse` on errors
- Log errors with structured logging (`ILogger`)

### Configuration

- Use `IOptions<T>` pattern for configuration binding
- Environment variables prefixed with `FHIR_AUGURY_`
- `appsettings.local.json` for local development (gitignored)

## Versioning

Version numbers are generated automatically at build time using the timestamp
format `yyyy.MMdd.HHmm`. This means every build produces a unique version
number without manual version bumping.

## Solution Structure

See [Project Structure](project-structure.md) for a detailed breakdown of the
code organization.
