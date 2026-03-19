# Docker Deployment

FHIR Augury can be deployed as a Docker container running the background
service with HTTP API and scheduled sync.

## Quick Start

```bash
# Build and start
docker compose up -d

# Check health
curl http://localhost:5100/health
```

## Docker Compose

The included `docker-compose.yml` runs the service with persistent storage:

```yaml
services:
  fhir-augury:
    build: .
    ports:
      - "5100:5100"
    volumes:
      - ./local-cache:/data/cache     # Cache survives rebuilds
      - fhir-augury-db:/data/db       # Database persistence
    environment:
      - FHIR_AUGURY_Cache__RootPath=/data/cache
      - FHIR_AUGURY_Cache__DefaultMode=WriteThrough
      - FHIR_AUGURY_DatabasePath=/data/db/fhir-augury.db
      - FHIR_AUGURY_Api__Port=5100
      # Set your credentials:
      # - FHIR_AUGURY_Sources__jira__Cookie=JSESSIONID=...
      # - FHIR_AUGURY_Sources__zulip__Email=bot@example.com
      # - FHIR_AUGURY_Sources__zulip__ApiKey=...
      # - FHIR_AUGURY_Sources__confluence__Cookie=...
      # - FHIR_AUGURY_Sources__github__PersonalAccessToken=ghp_...

volumes:
  fhir-augury-db:
```

## Volumes

| Container Path | Purpose | Type |
|---------------|---------|------|
| `/data/cache` | Response cache (survives container rebuilds) | Bind mount |
| `/data/db` | SQLite database | Named volume |

The bind mount for cache (`./local-cache`) lets you populate the cache from
the host system (e.g., using the CLI's `download --cache-mode WriteOnly`) and
then use it inside the container.

## Setting Credentials

Uncomment and set the credential environment variables in `docker-compose.yml`:

```yaml
environment:
  # Jira (cookie or API token)
  - FHIR_AUGURY_Sources__jira__Cookie=JSESSIONID=ABC123...
  # OR
  - FHIR_AUGURY_Sources__jira__AuthMode=ApiToken
  - FHIR_AUGURY_Sources__jira__Email=you@example.com
  - FHIR_AUGURY_Sources__jira__ApiToken=your-token

  # Zulip
  - FHIR_AUGURY_Sources__zulip__Email=bot@example.com
  - FHIR_AUGURY_Sources__zulip__ApiKey=your-api-key

  # Confluence (cookie or Basic auth)
  - FHIR_AUGURY_Sources__confluence__Cookie=JSESSIONID=...
  # OR
  - FHIR_AUGURY_Sources__confluence__AuthMode=Basic
  - FHIR_AUGURY_Sources__confluence__Username=username
  - FHIR_AUGURY_Sources__confluence__ApiToken=your-token

  # GitHub
  - FHIR_AUGURY_Sources__github__PersonalAccessToken=ghp_...
```

> **Security note:** For production, use Docker secrets or a `.env` file
> (gitignored) instead of hardcoding credentials in `docker-compose.yml`.

## Dockerfile

The multi-stage Dockerfile:

1. **Build stage** — Uses `mcr.microsoft.com/dotnet/sdk:10.0` to restore and
   publish `FhirAugury.Service`
2. **Runtime stage** — Uses the smaller `mcr.microsoft.com/dotnet/aspnet:10.0`
   image

Default configuration:

| Setting | Value |
|---------|-------|
| Cache path | `/data/cache` |
| Database path | `/data/db/fhir-augury.db` |
| HTTP port | `5100` |

## Operations

### Trigger a Sync

```bash
# Sync all sources
curl -X POST http://localhost:5100/api/v1/ingest/sync

# Sync a specific source
curl -X POST http://localhost:5100/api/v1/ingest/jira?type=Incremental
```

### Check Status

```bash
curl http://localhost:5100/api/v1/ingest/status
```

### Update Sync Schedule

```bash
curl -X PUT http://localhost:5100/api/v1/ingest/jira/schedule \
  -H "Content-Type: application/json" \
  -d '{"SyncInterval": "00:30:00"}'
```

### View Logs

```bash
docker compose logs -f fhir-augury
```

### Backup the Database

The database is a single SQLite file. To back it up:

```bash
# Stop the service first for a clean copy
docker compose stop
cp local-cache/../fhir-augury-db/_data/fhir-augury.db backup.db
docker compose start

# Or use the SQLite backup API via the running service
sqlite3 /path/to/fhir-augury.db ".backup backup.db"
```

## Pre-Populating the Cache

To speed up initial setup, you can populate the cache on the host and share it
with the container:

```bash
# Download data to the local cache (host)
dotnet run --project src/FhirAugury.Cli -- \
  download --source jira --cache-mode WriteOnly \
  --cache-path ./local-cache --jira-cookie "..."

# Start the container (it will use the cache)
docker compose up -d
```
