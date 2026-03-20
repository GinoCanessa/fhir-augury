# Deployment Guide

FHIR Augury v2 uses a microservices architecture with five independent services
communicating via gRPC. This guide covers Docker Compose deployment.

## Architecture Overview

```
┌─────────────┐
│ MCP / CLI   │ ← Clients connect via gRPC
└──────┬──────┘
       │
┌──────▼──────┐
│ Orchestrator│ :5150 (HTTP) / :5151 (gRPC)
└──┬───┬───┬──┘
   │   │   │
   │   │   └──────────────────────────────────┐
   │   └─────────────────┐                    │
   │                     │                    │
┌──▼──────┐  ┌───▼──────┐  ┌──▼──────────┐  ┌──▼──────┐
│ Jira    │  │ Zulip    │  │ Confluence  │  │ GitHub  │
│:5160/61 │  │:5170/71  │  │ :5180/81    │  │:5190/91 │
└─────────┘  └──────────┘  └─────────────┘  └─────────┘
```

## Quick Start

```bash
# Full stack — all services
docker compose --profile full up -d

# Check health
curl http://localhost:5150/health   # Orchestrator
curl http://localhost:5160/health   # Jira
curl http://localhost:5170/health   # Zulip
curl http://localhost:5180/health   # Confluence
curl http://localhost:5190/health   # GitHub
```

## Docker Compose Profiles

The `docker-compose.yml` supports subset deployments via profiles:

| Profile | Services Started | Use Case |
|---------|-----------------|----------|
| `full` | All 5 services | Production deployment |
| `jira-zulip` | Jira + Zulip + Orchestrator | Common subset |
| `jira-only` | Jira only | Single source, no orchestrator |

```bash
# Full stack
docker compose --profile full up -d

# Jira + Zulip only (with orchestrator)
docker compose --profile jira-zulip up -d

# Single source standalone (no orchestrator)
docker compose --profile jira-only up -d
```

## Service Ports

| Service | HTTP Port | gRPC Port | Health Check |
|---------|-----------|-----------|-------------|
| Orchestrator | 5150 | 5151 | `GET /health` |
| Jira | 5160 | 5161 | `GET /health` |
| Zulip | 5170 | 5171 | `GET /health` |
| Confluence | 5180 | 5181 | `GET /health` |
| GitHub | 5190 | 5191 | `GET /health` |

## Volume Management

Each service uses named Docker volumes for persistent storage:

### Volume Types

| Volume Pattern | Persistence | Action on Upgrade |
|---------------|-------------|-------------------|
| `*-cache` | **Critical** — raw API responses | Preserve always |
| `*-data` | Rebuildable — SQLite databases | Safe to delete (regenerates from cache) |
| `orchestrator-data` | Rebuildable — cross-ref index | Safe to delete (rebuilds from sources) |

### Named Volumes

| Volume | Service | Contents |
|--------|---------|----------|
| `jira-cache` | Jira | Cached API responses |
| `jira-data` | Jira | SQLite database + FTS5 index |
| `zulip-cache` | Zulip | Cached API responses |
| `zulip-data` | Zulip | SQLite database + FTS5 index |
| `confluence-cache` | Confluence | Cached API responses |
| `confluence-data` | Confluence | SQLite database + FTS5 index |
| `github-cache` | GitHub | Cached API responses + git clones |
| `github-data` | GitHub | SQLite database + FTS5 index |
| `orchestrator-data` | Orchestrator | Cross-reference database |

### Operational Procedures

```bash
# Normal shutdown (preserves all volumes)
docker compose --profile full down

# Force database rebuild for one service (re-index from cache)
docker volume rm fhir-augury_jira-data
docker compose --profile full up source-jira

# Full reset (re-download everything — slow)
docker compose --profile full down -v

# Export cache from a container
docker cp $(docker compose ps -q source-jira):/app/cache ./exported-jira-cache

# Import cache into a volume
docker run --rm -v fhir-augury_jira-cache:/data -v ./exported-jira-cache:/import \
    alpine cp -r /import/. /data/
```

## Environment Variables

### Jira

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_JIRA__Jira__BaseUrl` | `https://jira.hl7.org` | Jira server URL |
| `FHIR_AUGURY_JIRA__Jira__AuthMode` | `cookie` | `cookie` or `apitoken` |
| `FHIR_AUGURY_JIRA__Jira__Cookie` | | Session cookie value |
| `FHIR_AUGURY_JIRA__Jira__CachePath` | `./cache/jira` | Cache directory |
| `FHIR_AUGURY_JIRA__Jira__DatabasePath` | `./data/jira.db` | Database path |

### Zulip

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_ZULIP__Zulip__BaseUrl` | `https://chat.fhir.org` | Zulip server URL |
| `FHIR_AUGURY_ZULIP__Zulip__Email` | | Bot email |
| `FHIR_AUGURY_ZULIP__Zulip__ApiKey` | | API key |
| `FHIR_AUGURY_ZULIP__Zulip__CachePath` | `./cache/zulip` | Cache directory |
| `FHIR_AUGURY_ZULIP__Zulip__DatabasePath` | `./data/zulip.db` | Database path |

### Confluence

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_CONFLUENCE__Confluence__BaseUrl` | `https://confluence.hl7.org` | Server URL |
| `FHIR_AUGURY_CONFLUENCE__Confluence__AuthMode` | `cookie` | `cookie` or `basic` |
| `FHIR_AUGURY_CONFLUENCE__Confluence__Cookie` | | Session cookie |
| `FHIR_AUGURY_CONFLUENCE__Confluence__CachePath` | `./cache/confluence` | Cache directory |
| `FHIR_AUGURY_CONFLUENCE__Confluence__DatabasePath` | `./data/confluence.db` | Database path |

### GitHub

| Variable | Default | Description |
|----------|---------|-------------|
| `GITHUB_TOKEN` | | GitHub personal access token |
| `FHIR_AUGURY_GITHUB__GitHub__CachePath` | `./cache/github` | Cache directory |
| `FHIR_AUGURY_GITHUB__GitHub__DatabasePath` | `./data/github.db` | Database path |

### Orchestrator

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR__Orchestrator__DatabasePath` | `./data/orchestrator.db` | Database path |
| `FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Jira__GrpcAddress` | `http://localhost:5161` | Jira gRPC |
| `FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Zulip__GrpcAddress` | `http://localhost:5171` | Zulip gRPC |
| `FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Confluence__GrpcAddress` | `http://localhost:5181` | Confluence gRPC |
| `FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__GitHub__GrpcAddress` | `http://localhost:5191` | GitHub gRPC |

> **Security note:** For production, use a `.env` file (gitignored) or Docker
> secrets instead of hardcoding credentials in `docker-compose.yml`.

## Health Checks

All services expose `GET /health` on their HTTP port. Docker Compose is
configured with:
- **Interval:** 30 seconds
- **Timeout:** 10 seconds
- **Retries:** 3
- **Start period:** 15 seconds

The orchestrator's `depends_on` uses `condition: service_healthy` so it waits
for source services to be ready before starting.

## Rebuilding Images

```bash
# Rebuild all images
docker compose --profile full build

# Rebuild a specific service
docker compose build source-jira

# Rebuild and restart
docker compose --profile full up -d --build
```

## Logs

```bash
# All services
docker compose --profile full logs -f

# Specific service
docker compose logs -f source-jira

# Last 100 lines
docker compose logs --tail 100 orchestrator
```
