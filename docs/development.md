# Development Guide

A quickstart for building, running, and testing FHIR Augury v2 locally.

> For the deep dive — source generation, code conventions, the full test
> matrix, dependency-injection patterns, and how to add a new source — see the
> [technical Development Guide](technical/development-guide.md). For the code
> layout, see [Project Structure](technical/project-structure.md).

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

## Running Services

Run a single source service, or start the whole topology at once with the
Aspire AppHost:

```bash
# One source service (Jira on :5160)
dotnet run --project src/FhirAugury.Source.Jira

# The orchestrator (needs source services running; :5150)
dotnet run --project src/FhirAugury.Orchestrator

# Everything at once, with the Aspire dashboard
dotnet workload install aspire        # one-time
dotnet run --project src/FhirAugury.AppHost
```

The AppHost registers all source services, the orchestrator, the MCP HTTP
server, the Dev UI, and the CLI. See the
[technical Development Guide](technical/development-guide.md#running-services)
for MCP, CLI, and per-service run details, and the
[Deployment Guide](deployment.md) for Docker Compose and Aspire.

## Running Tests

```bash
# Run all tests
dotnet test fhir-augury.slnx

# Run a specific test project
dotnet test tests/FhirAugury.Source.Jira.Tests

# Run with verbose output
dotnet test fhir-augury.slnx --verbosity normal
```

See the [technical Development Guide](technical/development-guide.md#test-projects)
for the full test matrix and infrastructure notes.

## Local Configuration

Each service reads `appsettings.json` and supports `appsettings.local.json`
(gitignored) for local overrides, plus environment variables with a per-service
prefix (e.g. `FHIR_AUGURY_JIRA_`):

```json
// src/FhirAugury.Source.Jira/appsettings.local.json
{
  "Jira": {
    "Cookie": "JSESSIONID=your-cookie-here"
  }
}
```

See the [Configuration Reference](configuration.md) for the complete key list
and the [Configuration guide](user/configuration.md) for credential setup.

## Where to Go Next

- [Architecture](technical/architecture.md) — system design and components.
- [Project Structure](technical/project-structure.md) — code organization.
- [technical Development Guide](technical/development-guide.md) — conventions,
  source generation, and adding a new source.
- [Deployment Guide](deployment.md) — Docker Compose and .NET Aspire.
