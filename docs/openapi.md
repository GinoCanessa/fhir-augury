# OpenAPI Discovery

FHIR Augury exposes its HTTP surface through OpenAPI 3.1 documents on every
service. The orchestrator additionally publishes a merged document that
describes its own endpoints plus every enabled source service's endpoints
exposed through typed per-source proxy controllers under
`/api/v1/{name}/...` (one controller per source: `JiraProxyController`,
`ZulipProxyController`, `ConfluenceProxyController`,
`GitHubProxyController`).

> **Note.** The previous generic reverse proxy at `/api/v1/source/{name}/...`
> was removed in the 2026-04 sync. The orchestrator self-metadata routes
> (`/api/v1/source/orchestrator/openapi.json` and
> `/api/v1/source/orchestrator/list-sources`) are preserved by design.
> See [`docs/changelog/2026-04-sync.md`](changelog/2026-04-sync.md) for the
> migration notes.

The CLI uses these documents to enumerate available commands, fetch
parameter and response schemas, and invoke any operation generically — no
new code is required to call a newly added endpoint as long as it appears
in the OpenAPI document.

## Overview

Discovery flows top-down:

1. The CLI fetches the orchestrator's merged document at
   `/api/v1/openapi.json`.
2. Each operation has an `operationId` of the form `{source}.{command}`
   (e.g., `jira.query`).
3. The CLI resolves an operation, distributes parameters into path/query/
   header/body per the `parameters` and `requestBody` schema, and hits the
   orchestrator's typed per-source proxy at `/api/v1/{name}/{...}` (e.g.
   `POST /api/v1/jira/query`). The proxy forwards the call to the
   underlying source service.

The merged document is cached by the orchestrator (ETag-aware, invalidated
on health-state transitions of any source) and by the CLI on disk
(`%LOCALAPPDATA%\fhir-augury\openapi\merged.json`).

## Endpoints

### Per-source documents

Each source service (`source-jira`, `source-zulip`, `source-github`,
`source-confluence`) exposes its own document:

- `GET /api/v1/openapi.json` — JSON, OpenAPI 3.1.
- `GET /api/v1/openapi.yaml` — YAML, OpenAPI 3.1.

### Orchestrator merged document

The orchestrator publishes the union of its own document and every enabled
source's document:

- `GET /api/v1/openapi.json` — merged, JSON.
- `GET /api/v1/openapi.yaml` — merged, YAML.
- `GET /api/v1/openapi.json?include=internal` — include operations marked
  `x-augury-visibility: internal` (default omits them).

Source paths are remapped from `/api/v1/{...}` to `/api/v1/{name}/{...}` —
the same shape the typed proxy controllers serve. Source `operationId`s
are prefixed with `{name}.`. Source `components.schemas` keys are prefixed
with `{name}_` and all `$ref`s are rewritten accordingly. Source `tags`
are prefixed with `source:{name}/`.

When a typed-proxy stub and the rich source description point at the same
`{path, method}` (which they always do for proxied operations), the merger
performs a per-method merge instead of throwing — the source's richer
operation (full parameters, schemas, tags, descriptions) wins, while the
typed proxy contributes any operations the source did not describe. This
behavior changed in the 2026-04 sync (previously a duplicate path threw).

If a source is configured but unreachable when the merge runs, its paths
are omitted and the merged document carries a top-level
`x-augury-source-status` extension describing the failure (see below).

### Per-source proxy on the orchestrator

- `GET /api/v1/source/orchestrator/openapi.json` — orchestrator-only
  document (not merged with sources). The `api/v1/source/orchestrator/...`
  prefix is the **only** surviving usage of the historical
  `api/v1/source/{name}/...` namespace and is preserved by design.

### List sources

- `GET /api/v1/source/orchestrator/list-sources` — returns the set of
  enabled source names. Used by `augury sources`.

### Typed source proxies

Each enabled source is exposed under `/api/v1/{name}/...` by a dedicated
controller in `src/FhirAugury.Orchestrator/Controllers/Proxies/`:

- `JiraProxyController` → `/api/v1/jira/...`
- `ZulipProxyController` → `/api/v1/zulip/...`
- `ConfluenceProxyController` → `/api/v1/confluence/...`
- `GitHubProxyController` → `/api/v1/github/...`

These typed proxies cover **every** source endpoint (≈98 actions across
the four controllers). They forward requests via `SourceHttpClient`,
preserving method, query string, body, response status, and ETag /
`Last-Modified` headers. Allow-listed request headers (`Accept`,
`Accept-Encoding`, `Accept-Language`, `If-None-Match`,
`If-Modified-Since`, `Range`, `User-Agent`, `X-Augury-*`) are forwarded;
everything else (including `Authorization` and `Cookie`) is stripped.
Response bodies stream through without buffering.

The Jira proxy additionally accepts a consumer-facing `?jira-project=KEY`
query parameter on `POST /api/v1/jira/ingest` and
`POST /api/v1/jira/ingest/trigger`, which the proxy translates back to
Jira's internal `?project=` parameter (renamed at the seam to disambiguate
from "GitHub project"). The orchestrator fan-out
`POST /api/v1/ingest/trigger?jira-project=KEY` forwards the parameter to
the Jira leg only.

## Vendor extensions

| Extension                  | Scope         | Purpose                                                                                                  |
| -------------------------- | ------------- | -------------------------------------------------------------------------------------------------------- |
| `x-augury-command`         | operation     | CLI command name for the operation. Set via `[AuguryCommand("name")]`.                                   |
| `x-augury-streaming`       | operation     | Marks the operation as a streaming response (SSE/NDJSON). Set via `[AuguryStreaming]`.                   |
| `x-augury-visibility`      | operation     | `public` (default) or `internal`. `internal` operations are filtered from the merged doc by default.     |
| `x-augury-since`           | operation     | First version where the operation appeared. Set via `[AugurySince("x.y.z")]`.                            |
| `x-augury-until`           | operation     | Version after which the operation is removed. Set via `[AuguryUntil("x.y.z")]`.                          |
| `x-augury-source-status`   | document root | Map of `{source-name → status string}` set by the merge service for sources that failed to be retrieved. |

## CLI usage

```bash
# List enabled sources.
augury sources

# Enumerate every operation across all sources.
augury commands

# Filter by source or tag.
augury commands --source jira
augury commands --tag source:jira/items

# Print the request/response schema for one operation.
augury schema source=jira operation=query

# Invoke any operation generically.
augury call source=jira operation=query body=@query.json
augury call source=jira operation=get-item param:id=PROJ-123
augury call source=zulip operation=query body=- < query.json
```

The CLI caches the merged document at
`%LOCALAPPDATA%\fhir-augury\openapi\merged.json` (with a sibling `.etag`
file) and revalidates with `If-None-Match`. Force a refresh with:

```bash
augury commands refresh=true
augury call source=jira operation=query body=@q.json refresh=true
```

## CI quality gate

The orchestrator test project enforces basic OpenAPI hygiene in
`tests/FhirAugury.Orchestrator.Tests/OpenApiDocumentValidationTests.cs`:

- Each per-service-style document parses with zero diagnostic errors via
  `OpenApiDocument.Parse(json, "json", new OpenApiReaderSettings { Readers = { ["json"] = new OpenApiJsonReader() } })`.
- All operations within a document have a unique `operationId`.
- The merged orchestrator document passes the same checks. After the
  2026-04 sync, every non-orchestrator path starts with `/api/v1/{name}/`
  (typed proxy shape); no path may match `^/api/v1/source/[a-z]` other
  than the preserved `api/v1/source/orchestrator/...` self-metadata routes.

Companion tests under
`tests/FhirAugury.Common.OpenApi.Tests/` (in particular
`Merge_MergedPaths_StartWithSourcePrefix`) cover the merger primitive
itself, and
`tests/FhirAugury.Orchestrator.Tests/OpenApiMergeServiceTests.cs`
exercises the runtime merge service against mocked source HTTP clients,
including ETag stability and the `x-augury-source-status` extension on
source failures.
