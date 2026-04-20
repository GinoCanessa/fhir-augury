# OpenAPI Discovery

FHIR Augury exposes its HTTP surface through OpenAPI 3.1 documents on every
service. The orchestrator additionally publishes a merged document that
describes its own endpoints plus every enabled source service's endpoints
remapped under a single `/api/v1/source/{name}/...` namespace.

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
   orchestrator's generic proxy at
   `/api/v1/source/{name}/{**rest}`. The orchestrator forwards the call to
   the underlying source service.

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

Source paths are remapped from `/api/v1/{...}` to
`/api/v1/source/{name}/{...}`. Source `operationId`s are prefixed with
`{name}.`. Source `components.schemas` keys are prefixed with `{name}_`
and all `$ref`s are rewritten accordingly. Source `tags` are prefixed
with `source:{name}/`.

If a source is configured but unreachable when the merge runs, its paths
are omitted and the merged document carries a top-level
`x-augury-source-status` extension describing the failure (see below).

### Per-source proxy on the orchestrator

- `GET /api/v1/source/{name}/openapi.json` — passthrough to that source's
  own document. Returns 404 if the source is disabled.
- `GET /api/v1/source/orchestrator/openapi.json` — orchestrator-only
  document (not merged with sources).

### List sources

- `GET /api/v1/source/orchestrator/list-sources` — returns the set of
  enabled source names. Used by `augury sources`.

### Generic passthrough

Any request to `/api/v1/source/{name}/{**rest}` (for `name != orchestrator`)
is forwarded by the orchestrator to `source-{name}` at
`/api/v1/{rest}`, preserving method, query string, and body. Allow-listed
request headers (`Accept`, `Accept-Encoding`, `Accept-Language`,
`If-None-Match`, `If-Modified-Since`, `Range`, `User-Agent`,
`X-Augury-*`) are forwarded; everything else (including `Authorization`
and `Cookie`) is stripped. Response bodies stream through without
buffering.

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
- The merged orchestrator document passes the same checks and every
  non-orchestrator path starts with `/api/v1/source/{name}/`.

Companion tests under
`tests/FhirAugury.Common.OpenApi.Tests/` (in particular
`Merge_MergedPaths_StartWithSourcePrefix`) cover the merger primitive
itself, and
`tests/FhirAugury.Orchestrator.Tests/OpenApiMergeServiceTests.cs`
exercises the runtime merge service against mocked source HTTP clients,
including ETag stability and the `x-augury-source-status` extension on
source failures.
