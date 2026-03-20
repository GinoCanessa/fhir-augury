# Phase 1: Foundation & Common Infrastructure

**Goal:** Establish the shared project structure, gRPC contracts, and common
abstractions that all services will use.

**Proposal references:** [02-architecture](../../proposal/v2/02-architecture.md),
[05-api-contracts](../../proposal/v2/05-api-contracts.md),
[06-caching-storage](../../proposal/v2/06-caching-storage.md)

---

## 1.1 — Solution Restructure

Reorganize the solution from the v1 monolith layout to the v2
service-oriented structure.

### 1.1.1 — Create `FhirAugury.Common` project

Create a new class library `src/FhirAugury.Common/` to replace
`FhirAugury.Models` as the shared types layer. This project holds:

- Shared model interfaces and base types
- gRPC proto definitions (referenced from `protos/`)
- Utility code (text sanitization, HTML stripping, FHIR-aware tokenization)
- `ResponseCache` and `IResponseCache` (moved from Models)
- `HttpRetryHelper` (moved from Models)
- `CacheConfiguration`, `CacheMode`, `CacheStats` (moved from Models)
- Common configuration types (`SourceServiceConfiguration`)
- Source-generated gRPC client/server stubs

**Migration from v1:** Move all reusable types from `FhirAugury.Models` into
`FhirAugury.Common`. `FhirAugury.Models` will be removed once all references
are updated. Types that were v1-specific (e.g., `IDataSource` with its
monolithic ingestion pattern) will be redesigned for the service-oriented
model.

**Dependencies:**
- `Google.Protobuf`
- `Grpc.Net.Client`
- `Grpc.Tools` (build-time)
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Logging.Abstractions`

### 1.1.2 — Create `protos/` directory

Create `protos/` at the solution root with all proto files. These define the
inter-service contracts before any implementation begins.

Files to create:
- `protos/common.proto` — shared messages (`Timestamp` imports, common enums)
- `protos/source_service.proto` — common `SourceService` contract
- `protos/orchestrator.proto` — `OrchestratorService` contract
- `protos/jira.proto` — `JiraService` extensions
- `protos/zulip.proto` — `ZulipService` extensions
- `protos/confluence.proto` — `ConfluenceService` extensions
- `protos/github.proto` — `GitHubService` extensions

Content is fully defined in
[05-api-contracts](../../proposal/v2/05-api-contracts.md), including all
message types, RPC signatures, and field numbers. The review document
(09-review) resolved all naming inconsistencies — use `{Source}Service`
naming (not `{Source}SourceService`), source-specific return types (e.g.,
`JiraIssueSummary` not `ItemSummary`), and include all RPCs listed in
03-source-services.

### 1.1.3 — Update solution file

Update `fhir-augury.slnx` to reference the new projects. The target
project structure is:

```
src/
├── FhirAugury.Common/           # Shared types, protos, utilities
├── FhirAugury.Source.Jira/      # Jira source service (Phase 2)
├── FhirAugury.Source.Zulip/     # Zulip source service (Phase 3)
├── FhirAugury.Source.Confluence/ # Confluence source service (Phase 5)
├── FhirAugury.Source.GitHub/    # GitHub source service (Phase 5)
├── FhirAugury.Orchestrator/    # Orchestrator service (Phase 4)
├── FhirAugury.Mcp/             # MCP server (Phase 4)
└── FhirAugury.Cli/             # CLI (Phase 4)

protos/                          # Shared gRPC proto definitions
tests/
├── FhirAugury.Source.Jira.Tests/
├── FhirAugury.Source.Zulip.Tests/
├── FhirAugury.Source.Confluence.Tests/
├── FhirAugury.Source.GitHub.Tests/
├── FhirAugury.Orchestrator.Tests/
└── FhirAugury.Integration.Tests/
```

Note: v1 projects (`FhirAugury.Models`, `FhirAugury.Database`,
`FhirAugury.Indexing`, `FhirAugury.Service`, `FhirAugury.Sources.*`) remain
in the solution during migration and are removed as their code is absorbed
into v2 projects.

---

## 1.2 — Shared Infrastructure

### 1.2.1 — `ResponseCache` base class

The `ResponseCache` class from v1 (`FileSystemResponseCache` in
`FhirAugury.Models/Caching/`) is already a good foundation. Move it to
`FhirAugury.Common` and extend with:

- `EnumerateFiles(string pattern)` — glob-based enumeration of cached files
- `GetTotalSizeBytes()` — total cache size reporting
- `Clear()` — full cache clearing
- Thread-safe concurrent access (multiple services may write simultaneously)

The interface is defined in
[06-caching-storage](../../proposal/v2/06-caching-storage.md):

```csharp
public class ResponseCache
{
    public async Task WriteAsync(string relativePath, ReadOnlyMemory<byte> data, CancellationToken ct);
    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken ct);
    public bool Exists(string relativePath, TimeSpan? maxAge = null);
    public IEnumerable<string> EnumerateFiles(string pattern);
    public long GetTotalSizeBytes();
    public void Clear();
}
```

Cache modes (`ReadWrite`, `WriteOnly`, `ReadOnly`, `Disabled`) carry over
from v1.

### 1.2.2 — `SourceDatabase` base class

Create a common SQLite database management class in `FhirAugury.Common` that
each source service will use. This replaces the monolithic `DatabaseService`
from `FhirAugury.Database`.

Responsibilities:
- Connection management (open, close, connection string)
- WAL mode + `PRAGMA synchronous=NORMAL` initialization
- Table creation orchestration (delegates to service-specific schema)
- FTS5 virtual table creation helpers
- Transaction management (`BeginTransaction`, batch operations)
- Database rebuild coordination (drop all → create all → reload from cache)
- In-memory mode for testing
- Database size reporting

Each source service defines its own record types and schema; the base class
provides the infrastructure.

### 1.2.3 — Service host template

Create a reusable pattern for hosting a source service as a standalone
ASP.NET process. This is not a shared library but a documented pattern that
each source service's `Program.cs` follows:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Source-specific configuration
builder.Configuration.AddJsonFile("appsettings.json", optional: true);
builder.Configuration.AddEnvironmentVariables("FHIR_AUGURY_{SOURCE}_");

// Core services
builder.Services.AddSingleton<SourceDatabase>();
builder.Services.AddSingleton<ResponseCache>();
builder.Services.AddSingleton<IngestionPipeline>();
builder.Services.AddSingleton<InternalIndexer>();

// Background workers
builder.Services.AddHostedService<ScheduledIngestionWorker>();

// gRPC + HTTP
builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<{Source}GrpcService>();
app.MapSourceHttpApi();
app.Run();
```

### 1.2.4 — Common text processing utilities

Move and consolidate text processing utilities from v1 into
`FhirAugury.Common`:

- `TextSanitizer` — HTML stripping, whitespace normalization (from Indexing)
- FHIR-aware tokenization (from Indexing/Bm25)
- Cross-reference pattern definitions (regex patterns for Jira keys, URLs,
  etc. — from Indexing/CrossRefLinker)

These are used by both source services (ingestion) and the orchestrator
(cross-reference scanning).

---

## 1.3 — gRPC Infrastructure

### 1.3.1 — Proto compilation setup

Configure `FhirAugury.Common.csproj` to compile proto files from `protos/`
into C# client and server stubs:

```xml
<ItemGroup>
  <Protobuf Include="..\..\protos\*.proto"
            GrpcServices="Both"
            ProtoRoot="..\..\protos" />
</ItemGroup>
```

Verify that proto compilation succeeds and generates the expected types.

### 1.3.2 — gRPC client factory helpers

Create extension methods for registering gRPC clients in DI:

```csharp
public static class GrpcClientExtensions
{
    public static IServiceCollection AddSourceServiceClient<TClient>(
        this IServiceCollection services,
        string sourceName,
        IConfiguration config)
        where TClient : ClientBase<TClient>
    {
        var address = config[$"Services:{sourceName}:GrpcAddress"];
        services.AddGrpcClient<TClient>(opts =>
            opts.Address = new Uri(address));
        return services;
    }
}
```

### 1.3.3 — gRPC service base class

Create a base class for source service gRPC implementations that handles
common concerns:

- Health check implementation
- Error mapping (exceptions → gRPC status codes)
- Logging
- Cancellation token propagation
- Common `SourceService` RPC implementations that delegate to service-specific
  logic

---

## 1.4 — Test Infrastructure

### 1.4.1 — Test project setup

Create test project shells for v2:

- `tests/FhirAugury.Source.Jira.Tests/`
- `tests/FhirAugury.Source.Zulip.Tests/`
- `tests/FhirAugury.Source.Confluence.Tests/`
- `tests/FhirAugury.Source.GitHub.Tests/`
- `tests/FhirAugury.Orchestrator.Tests/`
- `tests/FhirAugury.Integration.Tests/` (updated)

All use xUnit 2.9.3 + coverlet 6.0.4 + Microsoft.NET.Test.Sdk, matching the
existing v1 test conventions.

### 1.4.2 — gRPC test helpers

Create test utilities for gRPC service testing:

- In-memory gRPC server hosting for unit tests
- Mock gRPC client factories
- Test data builders for proto message types

---

## Phase 1 Verification

- [x] `FhirAugury.Common` project builds with proto compilation
- [x] All proto files compile without errors
- [x] Generated gRPC stubs are usable from test code
- [x] `ResponseCache` and `SourceDatabase` base classes compile with tests
- [x] Solution structure matches the target layout
- [x] All existing v1 tests still pass (no regressions during restructure)
