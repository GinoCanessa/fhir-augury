# FHIR Augury — NuGet Dependencies

## Production Dependencies

### Core (all projects)

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.Data.Sqlite` | 10.0.x | SQLite ADO.NET provider |
| `cslightdbgen.sqlitegen` | latest | Source generator for SQLite CRUD (analyzer only, no runtime dep) |

### FhirAugury.Sources.Zulip

| Package | Version | Purpose |
|---------|---------|---------|
| `zulip-cs-lib` | 0.0.1-alpha.6+ | Zulip REST API client |

### FhirAugury.Service

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.Extensions.Hosting` | 10.0.x | Generic host + DI + config |
| `Microsoft.Extensions.Configuration.Json` | 10.0.x | JSON config files |
| `Microsoft.Extensions.Configuration.EnvironmentVariables` | 10.0.x | Env vars |
| `Microsoft.Extensions.Configuration.UserSecrets` | 10.0.x | Dev-time secrets |
| `Microsoft.Extensions.Logging.Console` | 10.0.x | Console logging |
| `Microsoft.Extensions.Http` | 10.0.x | `IHttpClientFactory` for Jira/Confluence/GitHub |

### FhirAugury.Cli

| Package | Version | Purpose |
|---------|---------|---------|
| `System.CommandLine` | 2.0.x | CLI command parsing |
| `Microsoft.Extensions.Configuration.Json` | 10.0.x | Config file support |

### FhirAugury.Mcp

| Package | Version | Purpose |
|---------|---------|---------|
| `ModelContextProtocol` | 1.0.x | MCP SDK for .NET |
| `Microsoft.Extensions.Hosting` | 10.0.x | Host builder |

### FhirAugury.Indexing (optional, for AI features)

| Package | Version | Purpose |
|---------|---------|---------|
| `OpenAI` | 2.x | LLM-based summarization (if AI summary feature enabled) |
| `Azure.Identity` | 1.x | Azure OpenAI authentication |

## Test Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `xunit` | 2.x | Test framework |
| `xunit.runner.visualstudio` | 2.x | VS test runner |
| `Microsoft.NET.Test.Sdk` | 17.x | Test host |

## Dependency Philosophy

- **Minimize runtime dependencies.** The source generator (`cslightdbgen.sqlitegen`)
  produces code at compile time and has zero runtime footprint.
- **Use `HttpClient` directly** for Jira, Confluence, and GitHub APIs rather than
  bringing in large SDK packages (Atlassian SDKs, Octokit). This keeps the
  dependency tree shallow and avoids version conflicts.
- **`zulip-cs-lib`** is used because it's authored by the project maintainer and
  provides tested, typed access to the Zulip API.
- **No ORM.** Source-generated CRUD replaces EF Core entirely.

## Package References in `.csproj`

The source generator requires special reference attributes:

```xml
<ItemGroup>
  <!-- Source generator — analyzer only, no runtime reference -->
  <PackageReference Include="cslightdbgen.sqlitegen"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />

  <!-- Runtime SQLite provider -->
  <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.*" />
</ItemGroup>
```

## Solution-Wide Properties

Inherited from `src/common.props`:

```xml
<PropertyGroup>
    <LangVersion>14.0</LangVersion>
    <TargetFrameworks>net10.0</TargetFrameworks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
```
