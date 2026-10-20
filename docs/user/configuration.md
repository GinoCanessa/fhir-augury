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

Each service registers a literal prefix ending in `_`. Append the top-level
configuration section directly to that prefix, then use the standard
[ASP.NET Core configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/)
double underscore between nested key segments:

```
FHIR_AUGURY_{SERVICE}_{Section}__{Key}
```

The underscore ending the service prefix is not a hierarchy separator, so do
not add another underscore before the section name. For example, a Dev UI key
starts with `FHIR_AUGURY_DEVUI_DevUi__`, not
`FHIR_AUGURY_DEVUI__DevUi__`.

**Quick reference — env var prefixes:**

| Service | Prefix | Config Section |
|---------|--------|----------------|
| Jira Source | `FHIR_AUGURY_JIRA_` | `Jira` |
| Zulip Source | `FHIR_AUGURY_ZULIP_` | `Zulip` |
| Confluence Source | `FHIR_AUGURY_CONFLUENCE_` | `Confluence` |
| GitHub Source | `FHIR_AUGURY_GITHUB_` | `GitHub` |
| Orchestrator | `FHIR_AUGURY_ORCHESTRATOR_` | `Orchestrator` |
| Dev UI | `FHIR_AUGURY_DEVUI_` | `DevUi` |
| Jira FHIR Preparer | `FHIR_AUGURY_PREPARER_` | `Processing` |
| Jira FHIR Planner | `FHIR_AUGURY_PROCESSOR_JIRA_FHIR_PLANNER_` | `Processing` |
| Jira FHIR Applier | `FHIR_AUGURY_PROCESSOR_JIRA_FHIR_APPLIER_` | `Processing` |
| BallotNotes Processor | `FHIR_AUGURY_BALLOTNOTES_` | `BallotNotes` |

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

**Authentication:** Choose one of two modes via `AuthMode`:

- **`cookie`** — Set `Cookie` to your Jira session cookie
  (`JSESSIONID=...`)
- **`apitoken`** — Set `Email` and `ApiToken`

```bash
# Cookie auth
FHIR_AUGURY_JIRA_Jira__AuthMode=cookie
FHIR_AUGURY_JIRA_Jira__Cookie=JSESSIONID=ABC123...

# API token auth
FHIR_AUGURY_JIRA_Jira__AuthMode=apitoken
FHIR_AUGURY_JIRA_Jira__Email=you@example.com
FHIR_AUGURY_JIRA_Jira__ApiToken=your-token
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
FHIR_AUGURY_ZULIP_Zulip__Email=bot@example.com
FHIR_AUGURY_ZULIP_Zulip__ApiKey=your-api-key
```

### Confluence Source (`:5180` HTTP)

```json
{
  "Confluence": {
    "BaseUrl": "https://confluence.hl7.org",
    "AuthMode": "cookie",
    "Cookie": "",
    "Username": "",
    "ApiToken": ""
  }
}
```

`Spaces` is omitted above on purpose: leaving it unset (or `null`) indexes
**every non-archived global space on the instance**. Set it to an explicit list
to narrow that, or to `[]` to index nothing.

**Authentication:** Choose one of two modes via `AuthMode`:

- **`cookie`** — Set `Cookie` to your Confluence session cookie
- **`basic`** — Set `Username` and `ApiToken`

```bash
# Cookie auth
FHIR_AUGURY_CONFLUENCE_Confluence__AuthMode=cookie
FHIR_AUGURY_CONFLUENCE_Confluence__Cookie=JSESSIONID=...

# Basic auth
FHIR_AUGURY_CONFLUENCE_Confluence__AuthMode=basic
FHIR_AUGURY_CONFLUENCE_Confluence__Username=username
FHIR_AUGURY_CONFLUENCE_Confluence__ApiToken=your-token
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
FHIR_AUGURY_GITHUB_GitHub__Auth__Token=ghp_...
```

**Data provider:** The `Provider` setting selects the data fetch implementation
(`gh-cli`, the default in `appsettings.json` and recommended, or `rest` for the
GitHub REST API directly).

The GitHub source also supports additional settings covered in full by the
canonical reference: the `GhCli` provider options (`ExecutablePath`, `Limit`,
`Hostname`, `ProcessTimeout`, `MaxConcurrentProcesses`, and the history-backfill
controls `BackfillLimit`, `BackfillCheckpointInterval` and
`BackfillMaxRepairPasses`), the complete set of repository category lists
(`FhirCoreRepositories`, `UtgRepositories`, `FhirExtensionsPackRepositories`,
`IncubatorRepositories`, `IgRepositories`, `ManualLinks`), and the
`FileContentIndexing` controls. See the
[Configuration Reference](../configuration.md#github-source-service) for the
complete tables and defaults, and
[Data Sources](../technical/data-sources.md#history-backfill) for how the
backfill resumes after an interrupted run.

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
FHIR_AUGURY_ORCHESTRATOR_Orchestrator__Services__Jira__HttpAddress=http://localhost:5160
FHIR_AUGURY_ORCHESTRATOR_Orchestrator__Services__Jira__Enabled=true
FHIR_AUGURY_ORCHESTRATOR_Orchestrator__Services__Zulip__HttpAddress=http://localhost:5170
FHIR_AUGURY_ORCHESTRATOR_Orchestrator__Services__Zulip__Enabled=true
```

The Orchestrator also carries operator-readiness metadata for processing
services. The shipped mappings are fixed: Preparer requires Jira, while
Planner requires Jira and GitHub:

```json
{
  "Orchestrator": {
    "ProcessingServices": {
      "Preparer": {
        "HttpAddress": "http://localhost:5171",
        "Enabled": true,
        "RequiredServices": [ "Jira" ]
      },
      "Planner": {
        "HttpAddress": "http://localhost:5172",
        "Enabled": true,
        "RequiredServices": [ "Jira", "GitHub" ]
      }
    }
  }
}
```

These names drive readiness messages; they do not start or stop resources.
Aspire applies the same dependency catalog to its runtime topology. A disabled
required source is shown as a blocker in the Dev UI rather than treated as an
Orchestrator configuration failure.

---

## Processing Services

Preparer and Planner use processor-owned authoring runs. Their live SQLite
databases are private service state; outer clients use the typed CLI or
Orchestrator proxy, then build sites from a downloaded snapshot plus descriptor.
BallotNotes uses the equivalent options under its `BallotNotes` section.

The shipped authoring processors enable `ActivateRunBackedAuthoring`. On first
startup the service acquires exclusive ownership, creates and verifies
`PreCutoverBackupPath`, classifies existing rows as `legacy-unverified`, and
creates an initial revalidation run when that legacy corpus is non-empty. A
fresh empty database activates directly without a revalidation run. Do not
delete or replace the live database to perform this migration.

Key authoring options:

| Key | Purpose |
|-----|---------|
| `MaxActiveAuthoringRuns` | Bound live run admission; the shipped Preparer sets this to `1` |
| `AuthoringRetryDelay` / `AuthoringMaxAttempts` | Bound retries before a receipt is accepted |
| `SnapshotDirectory` / `SnapshotSchemaVersion` | Processor-owned immutable review snapshots |
| `ReconcileSnapshotsOnStartup` | Recover interrupted snapshot creation and recording |
| `ActivateRunBackedAuthoring` | Enable the one-way cutover and initial revalidation |
| `PreCutoverBackupPath` | Required verified rollback backup for activation |

The Preparer returns `409 active-run-capacity-reached` if a start is requested
while its existing run is `queued`, `running`, `finalizing`, or in recoverable
`error`. Completed and superseded runs release the slot. Each accepted Jira
authoring run stores its normalized request with the frozen items so the same
run can resume after restart.

### Jira FHIR Planner (`:5172`)

The Planner queues resolved change-required tickets and runs `ticket-plan`. Its
only Planner-specific knob is `Processing:Planner:RepoFilters` — an optional
exact `owner/repo` allow-list (`null` or `[]` = no restriction). Non-empty lists
are matched case-insensitively, do not support globs/wildcards/block-lists, and
are passed to the processor-launched `ticket-plan` worker through the canonical
`--repos` JSON-array argument.

Jira worker commands use `Processing:Jira:AuthoringAgentCliCommand`. The
processor supplies callback, operation token, run/item, and source-revision
values through `FHIR_AUGURY_AUTHORING_*` environment variables. Do not put a
database path or token placeholder in the command.

For the complete Preparer, Planner, Applier, and BallotNotes tables, see
[Configuration Reference → Processing Services](../configuration.md#processing-services).

---

## Dev UI (`:5210`)

Under Aspire, the AppHost supplies the Orchestrator address and repository-local
artifact roots automatically. For a standalone Dev UI, local overrides belong
in the gitignored `src\FhirAugury.DevUi\appsettings.local.json`:

```json
{
  "DevUi": {
    "OrchestratorAddress": "http://localhost:5150",
    "RecentRunLimit": 20,
    "RunPollInterval": "00:00:03",
    "CacheRoot": "..\\..\\cache",
    "SnapshotCacheRoot": "devui-authoring-snapshots",
    "ReviewSitesRoot": "devui-review-sites"
  }
}
```

The default/AppHost layout is:

| Artifact | Local path | Browser exposure |
|-|-|-|
| Verified snapshot pair | `cache\devui-authoring-snapshots\{prepare|plan}\{runId}\` | Private; never web-served |
| Generated review site | `cache\devui-review-sites\{prepare|plan}\{runId}\{discussion|applying}\` | `/review-sites/{prepare|plan}/{runId}/{discussion|applying}/` |

Pair directories contain the database, descriptor, and `verified-pair.json`.
Published sub-sites contain `site-manifest.json`. Both roots are under ignored
`cache\`; no credential or operation token belongs in either. Sites and pairs
are retained until an operator removes an old workflow/run directory while the
Dev UI is stopped.

The roots must be separate strict descendants of `DevUi:CacheRoot`. Startup
rejects repository/content/web/filesystem roots, ancestor/descendant overlap,
unsafe Windows names or alternate-data-stream syntax, and existing reparse
points. Only `DevUi:ReviewSitesRoot` is served. See the
[complete Dev UI option table](../configuration.md#dev-ui).

For standalone environment configuration, use the exact prefix and option
keys below:

```text
FHIR_AUGURY_DEVUI_DevUi__OrchestratorAddress=http://localhost:5150
FHIR_AUGURY_DEVUI_DevUi__RecentRunLimit=20
FHIR_AUGURY_DEVUI_DevUi__RunPollInterval=00:00:03
FHIR_AUGURY_DEVUI_DevUi__CacheRoot=..\..\cache
FHIR_AUGURY_DEVUI_DevUi__SnapshotCacheRoot=devui-authoring-snapshots
FHIR_AUGURY_DEVUI_DevUi__ReviewSitesRoot=devui-review-sites
```

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
FHIR_AUGURY_ZULIP_Zulip__Bm25__K1=1.5
FHIR_AUGURY_ZULIP_Zulip__Bm25__B=0.5
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
FHIR_AUGURY_JIRA_Jira__AuxiliaryDatabase__AuxiliaryDatabasePath=/data/auxiliary.db
FHIR_AUGURY_JIRA_Jira__AuxiliaryDatabase__FhirSpecDatabasePath=/data/fhir-spec.db
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
