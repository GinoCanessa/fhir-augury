# Phase 6: Docker Compose & Deployment

**Goal:** Productionize the deployment with containerization, multi-service
orchestration, health checks, and documentation.

**Proposal references:**
[02-architecture](../../proposal/v2/02-architecture.md) (Deployment Models),
[06-caching-storage](../../proposal/v2/06-caching-storage.md) (Volume Management)

**Depends on:** Phase 5

---

## 6.1 — Individual Dockerfiles

### 6.1.1 — Create per-service Dockerfiles

Create a Dockerfile for each service using multi-stage builds for small
images. All follow the same pattern:

```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/FhirAugury.Source.{Name}/ -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

RUN adduser --disabled-password --no-create-home appuser
USER appuser

EXPOSE {http-port} {grpc-port}
ENTRYPOINT ["dotnet", "FhirAugury.Source.{Name}.dll"]
```

**Dockerfiles to create:**
- `src/FhirAugury.Source.Jira/Dockerfile`
- `src/FhirAugury.Source.Zulip/Dockerfile`
- `src/FhirAugury.Source.Confluence/Dockerfile`
- `src/FhirAugury.Source.GitHub/Dockerfile` (needs `git` installed for
  repo cloning)
- `src/FhirAugury.Orchestrator/Dockerfile`

### 6.1.2 — GitHub service special requirements

The GitHub source service's Dockerfile needs additional tooling:
- `git` — for repository cloning and `git log --name-status`
- Adequate disk space for local clones (the `HL7/fhir` repo is ~2 GB)

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*
# ...rest of Dockerfile
```

---

## 6.2 — Docker Compose

### 6.2.1 — Create `docker-compose.yml`

Full-stack composition with all five services:

```yaml
services:
  source-jira:
    build: { context: ., dockerfile: src/FhirAugury.Source.Jira/Dockerfile }
    volumes:
      - jira-cache:/app/cache
      - jira-data:/app/data
    ports: ["5160:5160", "5161:5161"]
    environment:
      - FHIR_AUGURY_JIRA__BaseUrl=https://jira.hl7.org
      - FHIR_AUGURY_JIRA__AuthMode=cookie
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:5160/health"]
      interval: 30s
      timeout: 10s
      retries: 3

  source-zulip:
    build: { context: ., dockerfile: src/FhirAugury.Source.Zulip/Dockerfile }
    volumes:
      - zulip-cache:/app/cache
      - zulip-data:/app/data
    ports: ["5170:5170", "5171:5171"]
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:5170/health"]
      interval: 30s
      timeout: 10s
      retries: 3

  source-confluence:
    build: { context: ., dockerfile: src/FhirAugury.Source.Confluence/Dockerfile }
    volumes:
      - confluence-cache:/app/cache
      - confluence-data:/app/data
    ports: ["5180:5180", "5181:5181"]
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:5180/health"]
      interval: 30s
      timeout: 10s
      retries: 3

  source-github:
    build: { context: ., dockerfile: src/FhirAugury.Source.GitHub/Dockerfile }
    volumes:
      - github-cache:/app/cache
      - github-data:/app/data
    ports: ["5190:5190", "5191:5191"]
    environment:
      - GITHUB_TOKEN=${GITHUB_TOKEN:-}
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:5190/health"]
      interval: 30s
      timeout: 10s
      retries: 3

  orchestrator:
    build: { context: ., dockerfile: src/FhirAugury.Orchestrator/Dockerfile }
    depends_on:
      source-jira: { condition: service_healthy }
      source-zulip: { condition: service_healthy }
      source-confluence: { condition: service_healthy }
      source-github: { condition: service_healthy }
    volumes:
      - orchestrator-data:/app/data
    ports: ["5150:5150", "5151:5151"]
    environment:
      - FHIR_AUGURY_ORCHESTRATOR__Services__Jira__GrpcAddress=http://source-jira:5161
      - FHIR_AUGURY_ORCHESTRATOR__Services__Zulip__GrpcAddress=http://source-zulip:5171
      - FHIR_AUGURY_ORCHESTRATOR__Services__Confluence__GrpcAddress=http://source-confluence:5181
      - FHIR_AUGURY_ORCHESTRATOR__Services__GitHub__GrpcAddress=http://source-github:5191
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:5150/health"]
      interval: 30s
      timeout: 10s
      retries: 3

volumes:
  jira-cache:
  jira-data:
  zulip-cache:
  zulip-data:
  confluence-cache:
  confluence-data:
  github-cache:
  github-data:
  orchestrator-data:
```

### 6.2.2 — Create subset compose profiles

Support subset deployments using Docker Compose profiles:

```yaml
services:
  source-jira:
    profiles: ["full", "jira-zulip", "jira-only"]
    # ...

  source-zulip:
    profiles: ["full", "jira-zulip"]
    # ...

  source-confluence:
    profiles: ["full"]
    # ...

  source-github:
    profiles: ["full"]
    # ...

  orchestrator:
    profiles: ["full", "jira-zulip"]
    # ...
```

Usage:
```bash
# Full stack
docker compose --profile full up

# Jira + Zulip only
docker compose --profile jira-zulip up

# Single source (standalone, no orchestrator)
docker compose --profile jira-only up
```

---

## 6.3 — Volume Management

### 6.3.1 — Document volume strategy

Cache volumes are the critical data — they survive rebuilds and contain
raw API responses. Database volumes are derived and rebuildable from cache.

| Volume Type | Persistence | Action |
|-------------|-------------|--------|
| `*-cache` | Critical — never auto-delete | Preserve across upgrades |
| `*-data` | Rebuildable — can regenerate | Delete to force rebuild |
| `orchestrator-data` | Rebuildable | Delete to rebuild xref index |

### 6.3.2 — Document operational procedures

```bash
# Normal shutdown (preserves volumes)
docker compose down

# Force database rebuild for one service
docker volume rm fhir-augury_jira-data
docker compose up source-jira

# Full reset (re-download everything — slow)
docker compose down -v

# Share cache between machines
docker cp source-jira:/app/cache ./exported-jira-cache
```

---

## 6.4 — In-Process Development Host

### 6.4.1 — Create development all-in-one host

For development convenience, create a host that runs all services in a
single process using multiple Kestrel endpoints:

```csharp
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenLocalhost(5150); // Orchestrator HTTP
    kestrel.ListenLocalhost(5151, o => o.Protocols = HttpProtocols.Http2);
    kestrel.ListenLocalhost(5160); // Jira HTTP
    kestrel.ListenLocalhost(5161, o => o.Protocols = HttpProtocols.Http2);
    kestrel.ListenLocalhost(5170); // Zulip HTTP
    kestrel.ListenLocalhost(5171, o => o.Protocols = HttpProtocols.Http2);
    kestrel.ListenLocalhost(5180); // Confluence HTTP
    kestrel.ListenLocalhost(5181, o => o.Protocols = HttpProtocols.Http2);
    kestrel.ListenLocalhost(5190); // GitHub HTTP
    kestrel.ListenLocalhost(5191, o => o.Protocols = HttpProtocols.Http2);
});
```

This could be a launch profile or a separate `FhirAugury.DevHost` project
that references all service projects and registers all services in one
process. The port layout matches production so clients don't need different
configuration.

---

## 6.5 — MCP Configuration Templates

### 6.5.1 — Create MCP config examples

Update `mcp-config-examples/` with v2 configurations:

**Full stack (orchestrator mode):**
```json
{
  "mcpServers": {
    "fhir-augury": {
      "command": "dotnet",
      "args": ["run", "--project", "src/FhirAugury.Mcp"],
      "env": {
        "FHIR_AUGURY_ORCHESTRATOR": "http://localhost:5151"
      }
    }
  }
}
```

**Single source (direct mode):**
```json
{
  "mcpServers": {
    "fhir-augury-jira": {
      "command": "dotnet",
      "args": ["run", "--project", "src/FhirAugury.Mcp", "--",
               "--mode", "direct", "--source", "jira"],
      "env": {
        "FHIR_AUGURY_JIRA_GRPC": "http://localhost:5161"
      }
    }
  }
}
```

---

## 6.6 — Documentation

### 6.6.1 — Update README.md

Update the root README with:
- v2 architecture overview
- Quick start (Docker Compose)
- Service URLs and ports
- Configuration reference
- MCP setup instructions

### 6.6.2 — Create deployment guide

`docs/deployment.md`:
- Docker Compose deployment
- Subset deployment (profiles)
- Single-source standalone deployment
- Volume management and backup
- Environment variable reference for all services
- Health check endpoints

### 6.6.3 — Create development guide

`docs/development.md`:
- Development prerequisites
- In-process development host usage
- Running individual services
- Test suite execution
- Adding a new source service (template/checklist)

### 6.6.4 — Create configuration reference

`docs/configuration.md`:
- All configuration options per service
- Environment variable naming conventions
- appsettings.json examples
- Docker environment variable mappings

---

## 6.7 — v1 Cleanup

### 6.7.1 — Remove v1 projects

Once all v2 services are operational, remove the v1 monolith projects:

- `src/FhirAugury.Models/` (replaced by `FhirAugury.Common`)
- `src/FhirAugury.Database/` (absorbed into per-service databases)
- `src/FhirAugury.Indexing/` (absorbed into per-service indexing +
  orchestrator)
- `src/FhirAugury.Service/` (replaced by per-service hosts +
  orchestrator)
- `src/FhirAugury.Sources.Jira/` (replaced by `FhirAugury.Source.Jira`)
- `src/FhirAugury.Sources.Zulip/` (replaced by `FhirAugury.Source.Zulip`)
- `src/FhirAugury.Sources.Confluence/` (replaced by
  `FhirAugury.Source.Confluence`)
- `src/FhirAugury.Sources.GitHub/` (replaced by
  `FhirAugury.Source.GitHub`)

Also remove v1 test projects:
- `tests/FhirAugury.Database.Tests/`
- `tests/FhirAugury.Sources.Tests/`
- `tests/FhirAugury.Indexing.Tests/`
- `tests/FhirAugury.Integration.Tests/` (replaced by v2 integration tests)

### 6.7.2 — Update solution file

Remove all v1 project references from `fhir-augury.slnx`.

---

## Phase 6 Verification

- [x] All five services build as Docker images
- [x] `docker compose up` starts the full stack
- [x] Health checks pass for all services
- [x] Services communicate via container names (Docker networking)
- [x] Volumes persist data across container restarts
- [x] Subset deployment profiles work (e.g., Jira + Zulip only)
- [ ] In-process development host runs all services on correct ports
- [x] MCP configuration templates work with the running stack
- [x] Documentation is accurate and complete
- [ ] v1 projects are fully removed
- [x] All v2 tests pass
- [ ] End-to-end: download → cache → index → search → xref works
