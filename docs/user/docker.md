# Docker Deployment

FHIR Augury uses a multi-container Docker Compose deployment. Each source
(Jira, Zulip, Confluence, GitHub) runs as an independent service, with an
orchestrator aggregating results across them.

> For the complete deployment reference including all environment variables,
> architecture details, and .NET Aspire as an alternative, see the
> [Deployment Guide](../deployment.md).

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

## More: profiles, ports, volumes, and operations

For the full list of Compose profiles, the service-port table, named-volume
management, health-check tuning, and day-to-day operations (viewing logs,
rebuilding images, rebuilding databases from cache, exporting/importing cache,
and full resets), see the [Deployment Guide](../deployment.md). It also covers
running the same topology under .NET Aspire.
