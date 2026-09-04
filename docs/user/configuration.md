# Configuration

FHIR Augury uses a microservices architecture where each service has its own
configuration file and environment variables. This guide covers how to configure
each service for your deployment.

> For complete configuration tables and all available options, see the
> [Configuration Reference](../configuration.md).

## Configuration Priority

Each service reads configuration from multiple sources. Later sources override
earlier ones:

1. **`appsettings.json`** — Default settings shipped with the service
2. **`appsettings.local.json`** — Local overrides (gitignored)
3. **Environment variables** — Per-service prefixed variables
4. **User secrets** — Development environment only

## Environment Variable Naming

Environment variables follow the standard
[ASP.NET Core configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/)
pattern. Each service has its own prefix:

```
FHIR_AUGURY_{SERVICE}__{Section}__{Key}
```

Double underscores (`__`) separate nested keys.

**Quick reference — env var prefixes:**

| Service | Prefix | Config Section |
|---------|--------|----------------|
| Jira Source | `FHIR_AUGURY_JIRA_` | `Jira` |
| Zulip Source | `FHIR_AUGURY_ZULIP_` | `Zulip` |
| Confluence Source | `FHIR_AUGURY_CONFLUENCE_` | `Confluence` |
| GitHub Source | `FHIR_AUGURY_GITHUB_` | `GitHub` |
| Orchestrator | `FHIR_AUGURY_ORCHESTRATOR_` | `Orchestrator` |
| Jira FHIR Preparer | `FHIR_AUGURY_PREPARER_` | `Processing` |
| Jira FHIR Planner | `FHIR_AUGURY_PROCESSOR_JIRA_FHIR_PLANNER_` | `Processing` |

---

## Source Services

Each source service runs independently with its own database, cache, and ports.

### Jira Source (`:5160`)

```json
{
  "Jira": {
    "BaseUrl": "https://jira.hl7.org",
    "AuthMode": "cookie",
    "Cookie": "",
    "ApiToken": "",
    "Email": "",
    "DefaultProject": "FHIR"
  }
}
```

**Authentication:** Choose one of three modes via `AuthMode`:

- **`cookie`** — Set `Cookie` to your Jira session cookie
  (`JSESSIONID=...`). Ingests via the XML export endpoint.
- **`pat`** (alias `bearer`) — Set `ApiToken` to a Jira Data Center
  personal access token (Profile → Personal Access Tokens). Sent as
  `Authorization: Bearer`. This is the durable option for a Data Center
  deployment such as `jira.hl7.org`; no `Email` is needed, because the
  token identifies the user on its own.
- **`apitoken`** (alias `basic`) — Set `Email` and `ApiToken`. Sent as
  `Authorization: Basic`, which is the Atlassian Cloud credential model.

All modes except `cookie` ingest via the REST API (`/rest/api/2/search`)
and cache under a separate `json/` subdirectory, so switching auth mode
means the next sync re-downloads rather than reusing the XML cache.

```bash
# Cookie auth
FHIR_AUGURY_JIRA__Jira__AuthMode=cookie
FHIR_AUGURY_JIRA__Jira__Cookie=JSESSIONID=ABC123...

# Personal access token (Jira Data Center)
FHIR_AUGURY_JIRA__Jira__AuthMode=pat
FHIR_AUGURY_JIRA__Jira__ApiToken=your-personal-access-token

# API token auth (Atlassian Cloud)
FHIR_AUGURY_JIRA__Jira__AuthMode=apitoken
FHIR_AUGURY_JIRA__Jira__Email=you@example.com
FHIR_AUGURY_JIRA__Jira__ApiToken=your-token
```

### Zulip Source (`:5170`)

```json
{
  "Zulip": {
    "BaseUrl": "https://chat.fhir.org",
    "Email": "",
    "ApiKey": "",
    "CredentialFile": "~/.zuliprc"
  }
}
```

**Authentication:** Provide either `Email` + `ApiKey`, or a path to a
`CredentialFile` (`.zuliprc` format):

```bash
FHIR_AUGURY_ZULIP__Zulip__Email=bot@example.com
FHIR_AUGURY_ZULIP__Zulip__ApiKey=your-api-key
```

### Confluence Source (`:5180` HTTP)

```json
{
  "Confluence": {
    "BaseUrl": "https://confluence.hl7.org",
    "AuthMode": "cookie",
    "Cookie": "",
    "Username": "",
    "ApiToken": "",
    "Spaces": ["FHIR", "FHIRI", "SOA"]
  }
}
```

**Authentication:** Choose one of two modes via `AuthMode`:

- **`cookie`** — Set `Cookie` to your Confluence session cookie
- **`basic`** — Set `Username` and `ApiToken`

```bash
# Cookie auth
FHIR_AUGURY_CONFLUENCE__Confluence__AuthMode=cookie
FHIR_AUGURY_CONFLUENCE__Confluence__Cookie=JSESSIONID=...

# Basic auth
FHIR_AUGURY_CONFLUENCE__Confluence__AuthMode=basic
FHIR_AUGURY_CONFLUENCE__Confluence__Username=username
FHIR_AUGURY_CONFLUENCE__Confluence__ApiToken=your-token
```

### GitHub Source (`:5190`)

```json
{
  "GitHub": {
    "FhirCoreRepositories": ["HL7/fhir"],
    "UtgRepositories": ["HL7/UTG"],
    "FhirExtensionsPackRepositories": ["HL7/fhir-extensions"],
    "Auth": { "Token": null, "TokenEnvVar": "GITHUB_TOKEN" },
    "Provider": "gh-cli"
  }
}
```

**Authentication:** The GitHub source reads your token from the `GITHUB_TOKEN`
environment variable by default (via the `Auth.TokenEnvVar` setting). You can
also set the token directly:

```bash
# Use the standard GITHUB_TOKEN env var (recommended)
GITHUB_TOKEN=ghp_...

# Or set the token directly in config
FHIR_AUGURY_GITHUB__GitHub__Auth__Token=ghp_...
```

**Data provider:** The `Provider` setting selects the data fetch implementation
(`gh-cli`, the default in `appsettings.json` and recommended, or `rest` for the
GitHub REST API directly).

The GitHub source also supports additional settings covered in full by the
canonical reference: the `GhCli` provider options (`ExecutablePath`, `Limit`,
`Hostname`, `ProcessTimeout`), the complete set of repository category lists
(`FhirCoreRepositories`, `UtgRepositories`, `FhirExtensionsPackRepositories`,
`IncubatorRepositories`, `IgRepositories`, `ManualLinks`), and the
`FileContentIndexing` controls. See the
[Configuration Reference](../configuration.md#github-source-service) for the
complete tables and defaults.

---

## Orchestrator (`:5150`)

The orchestrator aggregates results from source services and provides unified
search, cross-references, and related-item discovery.

```json
{
  "Orchestrator": {
    "DatabasePath": "./data/orchestrator.db",
    "Ports": { "Http": 5150 },
    "Services": {
      "Jira": { "HttpAddress": "http://localhost:5160", "Enabled": true },
      "Zulip": { "HttpAddress": "http://localhost:5170", "Enabled": true },
      "Confluence": { "HttpAddress": "http://localhost:5180", "Enabled": false },
      "GitHub": { "HttpAddress": "http://localhost:5190", "Enabled": true }
    }
  }
}
```

The `Search`, `Related`, and `DictionaryDatabase` tuning sections are documented
in the [Configuration Reference](../configuration.md#orchestrator-service).
Configure which source services the orchestrator connects to:

```bash
FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Jira__HttpAddress=http://localhost:5160
FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Jira__Enabled=true
FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Zulip__HttpAddress=http://localhost:5170
FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Zulip__Enabled=true
```

---

## Processing Services

Processing services share the `Processing` configuration shape and expose
`/health`, `/status`, `/processing/start`, `/processing/stop`,
`/processing/queue`, and `POST /processing/tickets/{key}`.

> **Schema migration note (April 2026):** The Processing layer's SQLite schemas
> are now derived from the `cslightdbgen.sqlitegen` annotations on the record
> classes via generator-emitted `CreateTable` calls, and every Processing table
> now has a `RowId INTEGER PRIMARY KEY` column in addition to its `Id` GUID.
> Existing `CREATE TABLE IF NOT EXISTS` calls will not retro-fit `RowId` onto
> tables built by an older revision, so after pulling this change you must
> delete any pre-existing local Processing databases (for example
> `./data/processor.jira.fhir.preparer.db` and
> `./data/processor.jira.fhir.planner.db`). The services will recreate them on
> startup. Production data is not affected — these databases are dev-only work
> queues.

### Jira FHIR Planner (`:5172`)

The Planner queues resolved change-required tickets and runs `ticket-plan`. Its
only Planner-specific knob is `Processing:Planner:RepoFilters` — an optional
exact `owner/repo` allow-list (`null` or `[]` = no restriction). Non-empty lists
are matched case-insensitively, do not support globs/wildcards/block-lists, and
are passed to `ticket-plan` through the canonical `--repos` JSON-array argument.

For the full `Processing` shape (Preparer, Planner, and Applier), every key, and
the agent-command tokens, see
[Configuration Reference → Processing Services](../configuration.md#processing-services).

---

## MCP Server Configuration

The MCP tools are provided by two server projects (`FhirAugury.McpStdio` and
`FhirAugury.McpHttp`) that share a common library (`FhirAugury.McpShared`).
Both connect to the orchestrator and source services via HTTP using the same
environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR` | `http://localhost:5150` | Orchestrator HTTP address |

### McpStdio

The stdio-based server (`FhirAugury.McpStdio`) is configured entirely through
environment variables (listed above):

```bash
dotnet run --project src/FhirAugury.McpStdio
```

### McpHttp

The HTTP-based server (`FhirAugury.McpHttp`) is an ASP.NET Core application
that exposes the MCP endpoint via HTTP/SSE. It uses the same HTTP environment
variables as `McpStdio`, plus standard ASP.NET Core configuration:

- **Port:** 5200 (configurable via `ASPNETCORE_URLS` or `--urls`)
- **MCP endpoint:** `/mcp`
- **Aspire integration:** Includes Aspire ServiceDefaults for health checks,
  telemetry, and service discovery

```bash
dotnet run --project src/FhirAugury.McpHttp
```

See [MCP Tools](mcp-tools.md) for client configuration and tool documentation.

## CLI Configuration

The CLI connects to the orchestrator for queries. Configure the endpoint with:

- **Flag:** `--orchestrator http://localhost:5150`
- **Environment variable:** `FHIR_AUGURY_ORCHESTRATOR=http://localhost:5150`

The flag takes precedence over the environment variable.

## Sync Schedule Defaults

Each source service manages its own sync schedule independently:

| Source | Default Interval | Rationale |
|--------|-----------------|-----------|
| Jira | 1 hour | Changes frequently during ballots and WGMs |
| Zulip | 4 hours | High volume but append-only |
| Confluence | 24 hours | Pages change infrequently |
| GitHub | 2 hours | Moderate update frequency |

All source services also have a `MinSyncAge` setting (default `04:00:00`) that
prevents over-syncing by skipping the startup incremental sync if the last sync
occurred less than `MinSyncAge` ago.

All source services also support a `RunIngestionOnStartupOnly` flag (default
`false`). When `true`, the scheduled ingestion worker runs exactly one pass at
startup (still honoring `MinSyncAge` and `IngestionPaused`) and then exits its
loop cleanly. The service itself keeps running, so HTTP endpoints and manual
ingestion via the `IngestionController` remain available. This is primarily
useful for local/dev runs that should not continue syncing in the background.

---

## BM25 Tuning

Each source service uses the
[BM25 algorithm](https://en.wikipedia.org/wiki/Okapi_BM25) for keyword relevance
scoring. The `Bm25` parameters (`K1`, `B`, `UseLemmatization`, `FtsTokenizer`)
can be tuned per service — for example, a lower `B` for short Zulip messages and
a higher `B` for long Confluence pages:

```bash
FHIR_AUGURY_ZULIP__Zulip__Bm25__K1=1.5
FHIR_AUGURY_ZULIP__Zulip__Bm25__B=0.5
```

See the [Configuration Reference](../configuration.md) for the full `Bm25`
parameter table and defaults.

---

## Auxiliary Database (Optional)

Each source service can optionally load extended stop words, lemmatization data,
and FHIR vocabulary from read-only SQLite databases to improve search quality.
Configure the paths in each source's `AuxiliaryDatabase` section
(`AuxiliaryDatabasePath` and `FhirSpecDatabasePath`); both are optional and fall
back to built-in defaults when unset, with no loss of functionality:

```bash
FHIR_AUGURY_JIRA__Jira__AuxiliaryDatabase__AuxiliaryDatabasePath=/data/auxiliary.db
FHIR_AUGURY_JIRA__Jira__AuxiliaryDatabase__FhirSpecDatabasePath=/data/fhir-spec.db
```

See the [Configuration Reference](../configuration.md) for details.

---

## Dictionary Database

All services include a `DictionaryDatabase` section that compiles a dictionary
database on startup from `*.words.txt` and `*.typo.txt` files in the source path.
In Docker Compose, dictionary source files are shared across services via a
read-only bind mount (`./cache/dictionary:/app/cache/dictionary:ro`); the
compiled database is stored in each service's data volume. See the
[Configuration Reference](../configuration.md) for the `DictionaryDatabase` key
table.
