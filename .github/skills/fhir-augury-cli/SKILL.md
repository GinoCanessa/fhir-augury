---
name: fhir-augury-cli
description: "Reference for invoking the fhir-augury CLI. USE FOR: getting items from Jira/Zulip/Confluence/GitHub, cross-references, search, listing GitHub repos and their categories, ingestion control, service health. The CLI is the default integration surface for FHIR Augury data; MCP, direct HTTP, and appsettings.json are documented fallbacks."
---

# fhir-augury CLI Skill

The single, discoverable entry point any other skill should use to drive
FhirAugury. Other skills (e.g., `ticket-prep`, `ticket-plan`,
`repo-analysis`) consult this skill instead of duplicating CLI knowledge.

## When to use it

Any time a skill needs data that lives behind a FhirAugury source (Jira,
Zulip, Confluence, GitHub) or the orchestrator. Prefer the CLI over MCP and
over direct HTTP to the source services. MCP is supported as a fallback;
direct HTTP and `appsettings.json` reads are last-resort fallbacks.

## Invocation basics

- **Executable:** `fhir-augury-cli` (installed as a `dotnet tool`). During local
  development you can also run `dotnet run --project src/FhirAugury.Cli --`.
- **Connection:** the CLI talks to the orchestrator (default
  `http://localhost:5150`). Override with `--orchestrator <url>` or set
  `"orchestrator"` inside the JSON body.
- **Invocation form:** all commands are passed as a single JSON envelope:

  ```bash
  fhir-augury --json '{"command":"<name>", ...}' [--pretty]
  ```

  Alternatively, `--json @path/to/request.json` reads the body from a file,
  `--json @-` reads from stdin, and `--input <file>` reads the envelope from
  a file (equivalent to `--json @file`).
- **Bulky bodies:** for commands that take a `body` field (notably `call`,
  `jira-project`, `jira-local-processing`, `zulip-streams`), use the
  top-level `--body <value|@file|@->` flag to inject the body as a JSON
  value. This avoids string-escaping the body inside the envelope and the
  classic "double-encoded JSON string" mistake. The flag conflicts with an
  envelope-supplied `body`.

  ```bash
  fhir-augury --json '{"command":"call","source":"orchestrator","operation":"jira.query"}' --body @scratch/query.json
  ```
- **Output:** every command emits a JSON `OutputEnvelope`. Success
  envelopes go to stdout (or `--output <file>`); failure envelopes go to
  stderr (or `--output`) with exit code 1. Skills parse JSON; non-zero
  exit codes signal failure. Use `--pretty` only when inspecting output
  manually.
- **Discovery:** `fhir-augury --help` lists every command and every
  top-level flag (`--json`, `--input`, `--output`, `--body`, `--pretty`).
  Authoritative behavior lives under
  `src/FhirAugury.Cli/Dispatch/Handlers/`. The full request/response
  shapes can be exported with `save-schemas` (see below).

## Command map

Commands map 1:1 to handlers in `src/FhirAugury.Cli/Dispatch/Handlers/`.

| `command` | Purpose |
|-----------|---------|
| `get` | Fetch a full item by `(source, id)` |
| `list` | Paged list of items in a single source with sort/filter |
| `search` | Unified text search across one or more sources |
| `keywords` | Extracted keywords for an item |
| `related-by-keyword` | Items related to a given item by keyword similarity |
| `refers-to` | Outgoing cross-references from an item |
| `referred-by` | Incoming cross-references to an item |
| `cross-referenced` | Both directions of cross-reference for a value |
| `query-jira` | Structured Jira query. Supported filter fields: `query`, `statuses`, `resolutions`, `projects`, `excludeProjects`, `workGroups`, `specifications`, `types`, `priorities`, `labels`, `assignees`, `reporters`, `inPersonRequesters`, `createdAfter`, `createdBefore`, `updatedAfter`, `updatedBefore`, `sortBy`, `sortOrder`, `limit`, `offset`. |
| `query-zulip` | Structured Zulip query (streams, topics, senders, …) |
| `list-jira-workgroups`, `list-jira-specifications`, `list-jira-labels`, `list-jira-statuses` | Distinct values for a Jira dimension. The `workgroups` variant joins the canonical HL7 catalog and surfaces `name`, `code`, `nameClean` (PascalCase slug from `Hl7WorkGroupNameCleaner`, safe for URLs and folder names), `definition`, `retired`, and `issueCount`. The response is an envelope: `{ "dimension": "workgroups", "catalogJoinDegraded": <bool>, "items": [...] }` for the workgroups dimension (the flag is omitted for other dimensions). When `catalogJoinDegraded` is `true`, the HL7 catalog join was empty or partial; every row still carries a `nameClean` (the server falls back to `Hl7WorkGroupNameCleaner.Clean(name)`). Selectors may be passed in any of `code` / `name` / `nameClean` form via the shared `WorkGroupResolver`. |
| `ingest` | Trigger / control source ingestion. Actions: `trigger`, `status`, `reingest`, `reindex` (the latter two were renamed from `rebuild`/`index` in the 2026-04 sync; no aliases). Accepts `jiraProject` to scope a Jira run to one project. |
| `services` | Inspect service health |
| `version` | CLI/orchestrator version info |
| `sources` | List enabled source services and metadata |
| `commands` | List operations advertised by a source's OpenAPI doc |
| `schema` | Show a single source operation's request/response schema |
| `call` | Invoke an arbitrary source operation by `(source, operation)` |
| `save-schemas` | Dump every source's full schema set to a directory |
| `jira-items`, `jira-dimension`, `jira-workgroup`, `jira-project`, `jira-local-processing`, `jira-specs` | Jira source-scoped families (added in the 2026-04 sync). Mirror the typed orchestrator proxies under `/api/v1/jira/...` and the GitHub-side `jira-specs` resolver. |
| `prepared-ticket-authoring` | Preparer authoring-run start, status, bounded immediate retry, explicit supersession, worker submit, and verified snapshot download |
| `planned-ticket-authoring` | Planner authoring-run start, status, bounded immediate retry, explicit supersession, worker submit, and verified snapshot download |
| `ballot-note-authoring` | BallotNotes authoring-run start, status, bounded immediate retry, explicit supersession, worker submit, and verified snapshot download |
| `zulip-items`, `zulip-messages`, `zulip-streams`, `zulip-threads` | Zulip source-scoped families. |
| `confluence-items`, `confluence-pages` | Confluence source-scoped families. |
| `github-items`, `github-repos` | GitHub source-scoped families (action-first item layout — `items/{action}/{**key}` — preserved by design). |
| `fhir-releases`, `fhir-resources`, `fhir-structure`, `fhir-datatypes`, `fhir-profiles`, `fhir-codesystems`, `fhir-codesystem-lookup`, `fhir-valuesets`, `fhir-valueset-expand`, `fhir-operations`, `fhir-searchparameters`, `fhir-resolve`, `fhir-search` | FHIR specification source family (read-only query surface over `cache/fhir-spec.db`). All commands take an optional `release` token (`R5`, `5.0`, `DSTU2`, `hl7.fhir.r6.core`, …); when omitted it resolves to the latest stable release. Canonical URLs are passed as values (`system`, `url`), never path segments. See the FHIR recipes below. |

## Common recipes

Each recipe shows the canonical CLI form. Skills should always read the
returned envelope's documented top-level fields (e.g., `items`, `repos`)
rather than internal envelope plumbing.

### Get a Jira ticket

```bash
fhir-augury-cli --json '{"command":"get","source":"jira","id":"FHIR-12345","includeContent":true,"includeComments":true,"includeSnapshot":true}'
```

Returns the ticket envelope with `metadata`, `content`, `comments`, and
`snapshot` fields populated.

### Cross-references for any value

```bash
fhir-augury-cli --json '{"command":"cross-referenced","value":"FHIR-12345","limit":50}'
```

### Search across sources

```bash
fhir-augury-cli --json '{"command":"search","query":"Patient identifier","sources":["jira","zulip","github"],"limit":20}'
```

### List GitHub repositories and their categories

The GitHub source exposes `GET /api/v1/repos` (see
`src/FhirAugury.Source.GitHub/Controllers/ReposController.cs`). Reach it
via the `call` command:

```bash
fhir-augury-cli --json '{"command":"call","source":"github","operation":"repos"}'
```

The returned envelope carries a `repos` array; each entry includes
`fullName`, `description`, `category` (one of the values in
`src/FhirAugury.Source.GitHub/Configuration/RepoCategory.cs`:
`FhirCore`, `Utg`, `FhirExtensionsPack`, `Incubator`, `Ig`,
`JiraSpecArtifacts`), `issueCount`, `prCount`, and `url`.

This is the canonical replacement for any older "look at the table in
ticket-plan" or hand-maintained repo list.

### Health check

```bash
fhir-augury-cli --json '{"command":"services","action":"health"}'
```

Use this to decide whether the CLI path is viable before dispatching
data-fetching commands. Non-zero exit or `"healthy": false` in the
envelope signals that fallbacks should be considered.

### Trigger / inspect ingestion

```bash
# Inspect ingestion status across sources
fhir-augury-cli --json '{"command":"ingest","action":"status"}'

# Run an incremental ingest for one or more sources
fhir-augury-cli --json '{"command":"ingest","action":"trigger","sources":["github"],"type":"incremental"}'

# Restrict an ingest to a single Jira project (consumer-facing parameter
# is jira-project, not project, to disambiguate from "GitHub project")
fhir-augury-cli --json '{"command":"ingest","action":"trigger","sources":["jira"],"jiraProject":"FHIR"}'

# Rebuild a source's database from cache (was: action=rebuild — renamed
# in the 2026-04 sync; no backwards-compatible alias)
fhir-augury-cli --json '{"command":"ingest","action":"reingest","sources":["jira"]}'

# Rebuild a specific index family (was: action=index — renamed in the
# 2026-04 sync; no alias)
fhir-augury-cli --json '{"command":"ingest","action":"reindex","sources":["jira"],"indexType":"bm25"}'
```

### Dump schemas for offline reference

```bash
fhir-augury-cli --json '{"command":"save-schemas","outputDirectory":"./tmp/schemas"}'
```

Use the dumped JSON files when you need exact field names or you are
building automation against the CLI; do not rely on undocumented envelope
internals.

### Run-backed authoring

Outer automation may start a frozen run, inspect its status, request an
immediate bounded retry, explicitly supersede a known non-actionable error, and
download its verified snapshot pair. Automatic retry is processor-owned, so
polling—not manual retry—is the default. The `submit` action is reserved for
processor-launched callback workers with the complete
`FHIR_AUGURY_AUTHORING_*` environment; never construct callback tokens or
context in an outer skill.

| Command | Start selector | Guide |
|---------|----------------|-------|
| `prepared-ticket-authoring` | Optional `ticketKeys`; omitted or `[]` uses Preparer discovery | [Discussion tickets](../../../docs/user/generating-discussion-tickets.md) |
| `planned-ticket-authoring` | Optional `ticketKeys`; omitted or `[]` uses Planner discovery | [Application tickets](../../../docs/user/generating-application-tickets.md) |
| `ballot-note-authoring` | Required `hydrationExecutionId`; optional `noteIds` stays within that execution | [Ballot notes](../../../docs/user/generating-ballot-notes.md) |

All three commands accept actions `start`, `status`, `retry`, `supersede`,
`submit`, and `snapshot`. `databaseOnly: true` on `start` is the explicit
no-snapshot mode. Use `runId` for status, retry, supersede, and snapshot; add
`itemId` for retry or supersede, and a non-blank `reason` for supersede.
`operationId` is worker/receipt correlation, `acceptedReceiptId` is durable
item evidence returned by status, and descriptor `snapshotId` is the immutable
output identity rather than a CLI selector.

```bash
# Start and retain data.runId from the output envelope
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"start","ticketKeys":[],"databaseOnly":false}'

# Inspect items and retain acceptedReceiptId evidence
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"status","runId":"<runId>"}'

# Request an immediate retry only when explicitly directed
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"retry","runId":"<runId>","itemId":"<itemId>"}'

# Explicitly close a known non-actionable, non-receipt-backed error
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"supersede","runId":"<runId>","itemId":"<itemId>","reason":"duplicate request"}'

# Download the verified pair; trust the returned paths and descriptor filename
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\preparer\\<runId>\\"}'
```

Status keeps `failedItems` as the aggregate:
`retryableErrorItems` identifies errors still governed by automatic retry and
`supersededItems` identifies terminal non-authored outcomes. Item errors may
include `attemptsRemaining` and `nextAutomaticRetryAt`. Exact run status
`completed` or `completed-database-only` is lifecycle success even with
superseded items; treat that as partial authoring, continue publication from
accepted results, and surface every superseded item and reason.

Never infer a supersede reason, never supersede a receipt-backed item, and
never replay `retry` or `supersede` after an ambiguous transport failure.

Render or publish only from the `snapshot` action's verified
`snapshotPath` / `descriptorPath` pair. For human-facing action and identifier
details, see the [CLI reference](../../../docs/user/cli-reference.md#run-backed-authoring).
For exhaustive contracts use `fhir-augury --help <command>` and
[`save-schemas`](../../../docs/user/cli-reference.md#save-schemas--save-schemas-to-disk);
for finalization and recovery semantics see the
[processor runbook](../../../docs/technical/processors.md).

### Query the FHIR specification

The `fhir-*` family is a read-only query surface over the parsed FHIR spec
(`cache/fhir-spec.db`, releases DSTU2 → R6). Every response echoes the resolved
`release`. The `release` field is optional and defaults to the latest stable
release.

```bash
# List available releases
fhir-augury-cli --json '{"command":"fhir-releases"}'

# List R5 resources (optionally filter by workGroup / maturity / status)
fhir-augury-cli --json '{"command":"fhir-resources","release":"R5","workGroup":"oo"}'

# A resource's metadata + full element tree
fhir-augury-cli --json '{"command":"fhir-structure","release":"R5","name":"Observation"}'

# Data types, profiles (release defaults to latest stable when omitted)
fhir-augury-cli --json '{"command":"fhir-datatypes"}'
fhir-augury-cli --json '{"command":"fhir-profiles","release":"R6"}'

# Code system concept lookup (canonical URL passed as a value, not a path)
fhir-augury-cli --json '{"command":"fhir-codesystem-lookup","release":"R5","system":"http://hl7.org/fhir/observation-status","code":"final"}'

# Value set expansion and reverse bindings
fhir-augury-cli --json '{"command":"fhir-valueset-expand","release":"R5","url":"http://hl7.org/fhir/ValueSet/observation-status"}'

# Operations and search parameters
fhir-augury-cli --json '{"command":"fhir-operations","release":"R5","idOrCode":"expand"}'
fhir-augury-cli --json '{"command":"fhir-searchparameters","release":"R5","base":"Observation"}'

# Resolve a canonical URL to an artifact, or full-text search
fhir-augury-cli --json '{"command":"fhir-resolve","release":"R5","url":"http://hl7.org/fhir/StructureDefinition/Observation"}'
fhir-augury-cli --json '{"command":"fhir-search","release":"R5","query":"observation","types":"structure,valueset"}'
```

## Fallback order

If a CLI invocation fails (missing executable, transport error, or a
documented command is unavailable in the running build), fall back through
these alternatives **in order**:

1. **FhirAugury MCP server.** Tools prefixed with `FhirAugury-` (e.g.,
   `FhirAugury-get_item`, `FhirAugury-cross_referenced`,
   `FhirAugury-content_search`). Same semantics as the matching CLI
   command.
2. **Direct HTTP** to the orchestrator (`http://localhost:5150` by
   default) or to an individual source service (Jira `:5160`,
   Zulip `:5170`, Confluence `:5180`, GitHub `:5190`). Endpoints are
   discoverable via each service's OpenAPI document.
3. **Static config reads.** For purely-static information that doesn't
   change at runtime — most importantly, the repo → category mapping —
   read `src/FhirAugury.Source.GitHub/appsettings.json` and match repo
   names against the `*Repositories` lists (`FhirCoreRepositories`,
   `UtgRepositories`, `FhirExtensionsPackRepositories`,
   `IncubatorRepositories`, `IgRepositories`,
   `JiraSpecArtifactsRepositories`).

Other skills that need to declare their fallback chain may simply state
"per the `fhir-augury-cli` skill's fallback order" rather than restating
this list.

## Discovery hints

- Start with `fhir-augury --help`, then drill into a specific command by
  passing `--help` after `--json` is omitted (e.g., the CLI's own
  command-line help text describes flags).
- For exhaustive shape information, read
  `src/FhirAugury.Cli/Models/CliRequests.cs` (request DTOs) and the
  matching handlers under `src/FhirAugury.Cli/Dispatch/Handlers/`.
- `fhir-augury --json '{"command":"sources"}'` lists the currently
  enabled source services.
- `fhir-augury --json '{"command":"commands","source":"github"}'` lists
  the operations a given source advertises (used by `call`).

## Important rules

- Always invoke the CLI via the `--json` envelope. Do not script around
  the older positional-argument forms — they are not the documented
  surface.
- Treat the JSON envelope as the contract. Read documented fields; do
  not depend on field ordering or undocumented keys.
- When a CLI command is unavailable in the running build, fall back per
  the order above and record which path you used (useful when
  downstream skills persist provenance).
- This skill ships no code; everything happens by invoking the CLI in a
  shell.
