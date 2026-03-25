# Docker Deployment

FHIR Augury uses a multi-container Docker Compose deployment. Each source
(Jira, Zulip, Confluence, GitHub) runs as an independent service, with an
orchestrator aggregating results across them.

> For the complete deployment reference including all environment variables
> and architecture details, see the [Deployment Guide](../deployment.md).

## Quick Start

```bash
# Start all services
docker compose --profile full up -d

# Check health
curl http://localhost:5150/health

# Start only Jira + Zulip
docker compose --profile jira-zulip up -d

# Start Jira standalone
docker compose --profile jira-only up -d
```

## Profiles

Docker Compose profiles let you run only the services you need:

| Profile | Services | Use Case |
|---------|----------|----------|
| `full` | Orchestrator + Jira + Zulip + Confluence + GitHub | Full deployment with all sources |
| `jira-zulip` | Orchestrator + Jira + Zulip | Most common — core FHIR community sources |
| `jira-only` | Jira (standalone) | Minimal setup for Jira-only workflows |

## Service Ports

| Service | HTTP Port | gRPC Port |
|---------|-----------|-----------|
| Orchestrator | 5150 | 5151 |
| Jira Source | 5160 | 5161 |
| Zulip Source | 5170 | 5171 |
| Confluence Source | 5180 | 5181 |
| GitHub Source | 5190 | 5191 |

## Volume Management

The deployment uses 9 named volumes — two per source service (cache + data)
plus one for the orchestrator:

| Volume | Purpose | Critical? |
|--------|---------|-----------|
| `jira-cache` | Raw Jira API responses | **Yes** — cannot be regenerated |
| `jira-data` | Jira SQLite database + FTS index | No — rebuildable from cache |
| `zulip-cache` | Raw Zulip API responses | **Yes** |
| `zulip-data` | Zulip SQLite database + FTS index | No — rebuildable from cache |
| `confluence-cache` | Raw Confluence API responses | **Yes** |
| `confluence-data` | Confluence SQLite database + FTS index | No — rebuildable from cache |
| `github-cache` | Raw GitHub API responses | **Yes** |
| `github-data` | GitHub SQLite database + FTS index | No — rebuildable from cache |
| `orchestrator-data` | Orchestrator database (cross-refs, search) | No — rebuildable |

> **Important:** Cache volumes contain raw API responses that take significant
> time and API quota to re-download. Always back up cache volumes before
> destructive operations.

## Configuring Credentials

Set credentials via environment variables in `docker-compose.yml` or a `.env`
file (recommended, gitignored):

```bash
# .env file
# Jira (cookie or API token)
JIRA_COOKIE=JSESSIONID=ABC123...
# JIRA_AUTH_MODE=apitoken
# JIRA_EMAIL=you@example.com
# JIRA_API_TOKEN=your-token

# Zulip
ZULIP_EMAIL=bot@example.com
ZULIP_API_KEY=your-api-key

# Confluence (cookie or basic auth)
CONFLUENCE_COOKIE=JSESSIONID=...
# CONFLUENCE_AUTH_MODE=basic
# CONFLUENCE_USERNAME=username
# CONFLUENCE_API_TOKEN=your-token

# GitHub
GITHUB_TOKEN=ghp_...
```

> **Security note:** For production, use a `.env` file (gitignored) or Docker
> secrets instead of hardcoding credentials in `docker-compose.yml`.

## Auxiliary Databases (Optional)

To provide auxiliary databases (extended stop words, lemmatization, FHIR
vocabulary) in Docker, bind-mount the database files into each source container
and set the paths via environment variables:

```yaml
# docker-compose.override.yml
services:
  source-jira:
    volumes:
      - ./data/auxiliary.db:/app/data/auxiliary.db:ro
      - ./data/fhir-spec.db:/app/data/fhir-spec.db:ro
    environment:
      - FHIR_AUGURY_JIRA__Jira__AuxiliaryDatabase__AuxiliaryDatabasePath=/app/data/auxiliary.db
      - FHIR_AUGURY_JIRA__Jira__AuxiliaryDatabase__FhirSpecDatabasePath=/app/data/fhir-spec.db
```

Apply the same pattern for each source service (`source-zulip`,
`source-confluence`, `source-github`), adjusting the environment variable
prefix accordingly. The databases are opened read-only, so the `:ro` mount
flag is recommended.

When not configured, the system uses built-in defaults.

## Health Checks

All services expose a `GET /health` endpoint. Health check configuration:

- **Interval:** 30 seconds
- **Timeout:** 10 seconds
- **Retries:** 3
- **Start period:** 15 seconds

The orchestrator's health depends on its configured source services being
healthy (with non-required sources degrading gracefully).

```bash
# Check individual services
curl http://localhost:5150/health   # Orchestrator
curl http://localhost:5160/health   # Jira
curl http://localhost:5170/health   # Zulip
curl http://localhost:5180/health   # Confluence
curl http://localhost:5190/health   # GitHub
```

## Operations

### View Logs

```bash
# All services
docker compose --profile full logs -f

# Specific service
docker compose logs -f source-jira
docker compose logs -f orchestrator
```

### Rebuild Images

```bash
# Rebuild all images
docker compose --profile full build

# Rebuild and restart
docker compose --profile full up -d --build
```

### Rebuild Databases from Cache

Data volumes (SQLite + FTS indexes) can be rebuilt from cache if corrupted:

```bash
# Stop services
docker compose --profile full down

# Remove only the data volumes (keep cache!)
docker volume rm fhir-augury_jira-data fhir-augury_zulip-data

# Restart — services will rebuild from cache
docker compose --profile full up -d
```

### Export / Import Cache

```bash
# Export cache for backup
docker run --rm -v fhir-augury_jira-cache:/data -v $(pwd):/backup \
  alpine tar czf /backup/jira-cache.tar.gz -C /data .

# Import cache to a new deployment
docker volume create fhir-augury_jira-cache
docker run --rm -v fhir-augury_jira-cache:/data -v $(pwd):/backup \
  alpine tar xzf /backup/jira-cache.tar.gz -C /data
```

### Full Reset

```bash
# Stop and remove everything (including volumes)
docker compose --profile full down -v

# Start fresh
docker compose --profile full up -d
```

> **Warning:** This deletes all cache and data volumes. Re-syncing from
> upstream APIs will take significant time and API quota.
