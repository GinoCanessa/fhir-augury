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
  and `--json @-` reads from stdin.
- **Output:** every command emits a JSON `OutputEnvelope` on stdout. Skills
  parse JSON; non-zero exit codes signal failure. Use `--pretty` only when
  inspecting output manually.
- **Discovery:** `fhir-augury --help` lists every command. Authoritative
  behavior lives under `src/FhirAugury.Cli/Dispatch/Handlers/`. The full
  request/response shapes can be exported with `save-schemas` (see below).

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
| `query-jira` | Structured Jira query (statuses, work groups, dates, …) |
| `query-zulip` | Structured Zulip query (streams, topics, senders, …) |
| `list-jira-dimension` | Distinct values for a Jira dimension (work-group, status, …) |
| `ingest` | Trigger / control source ingestion |
| `services` | Inspect service health |
| `version` | CLI/orchestrator version info |
| `sources` | List enabled source services and metadata |
| `commands` | List operations advertised by a source's OpenAPI doc |
| `schema` | Show a single source operation's request/response schema |
| `call` | Invoke an arbitrary source operation by `(source, operation)` |
| `save-schemas` | Dump every source's full schema set to a directory |

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
fhir-augury-cli --json '{"command":"ingest","action":"run","sources":["github"],"type":"incremental"}'
```

### Dump schemas for offline reference

```bash
fhir-augury-cli --json '{"command":"save-schemas","outputDirectory":"./tmp/schemas"}'
```

Use the dumped JSON files when you need exact field names or you are
building automation against the CLI; do not rely on undocumented envelope
internals.

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
