# Source Endpoint Reference

This page mirrors the per-source HTTP surface as it stands after the
2026-04 sync. Every endpoint listed here is reachable in two ways:

1. **Directly** on the source service's port (e.g. Jira on `:5160`).
2. **Through the orchestrator's typed proxy** at `/api/v1/{name}/...`
   (e.g. `:5150/api/v1/jira/...`). The proxy preserves method, query
   string, body, response status, and ETag / `Last-Modified`. This is
   the canonical entry point for cross-source consumers (CLI, MCP,
   DevUI). The historical `/api/v1/source/{name}/...` generic proxy
   was removed in the 2026-04 sync — see
   [`docs/changelog/2026-04-sync.md`](../changelog/2026-04-sync.md).
   The orchestrator self-metadata routes
   (`/api/v1/source/orchestrator/...`) are preserved by design.

Route conventions:

- `{*id}` — single-segment catch-all (no `/` allowed in `id`).
- `{**id}` — multi-segment greedy catch-all (allows `/` inside the
  identifier; required for GitHub keys like `HL7/fhir:source/...` and
  for the cross-source `content/...` endpoints).

## Jira (`src/FhirAugury.Source.Jira/Controllers/`, port :5160)

### Cross-source content (also implemented by every source)

| Method | Route |
|--------|-------|
| GET  | `api/v1/content/refers-to` |
| GET  | `api/v1/content/referred-by` |
| GET  | `api/v1/content/cross-referenced` |
| GET  | `api/v1/content/search` |
| GET  | `api/v1/content/item/{source}/{**id}` |
| GET  | `api/v1/content/keywords/{source}/{**id}` |
| GET  | `api/v1/content/related-by-keyword/{source}/{**id}` |

### Items

| Method | Route |
|--------|-------|
| GET  | `api/v1/items` |
| GET  | `api/v1/items/{key}` |
| GET  | `api/v1/items/{key}/related` |
| GET  | `api/v1/items/{key}/snapshot` |
| GET  | `api/v1/items/{key}/content` |
| GET  | `api/v1/items/{key}/comments` |
| GET  | `api/v1/items/{key}/links` |

### Lifecycle / ingestion

| Method | Route |
|--------|-------|
| GET  | `api/v1/health` (liveness — always 200) |
| GET  | `api/v1/status` (readiness — 200/503) |
| GET  | `api/v1/stats` |
| POST | `api/v1/ingest` (accepts `?project=KEY`; surfaced as `?jira-project=` on the typed orchestrator proxy) |
| POST | `api/v1/ingest/trigger` (accepts `?project=KEY`; same renaming) |
| POST | `api/v1/rebuild` (CLI verb: `reingest`) |
| POST | `api/v1/rebuild-index` (CLI verb: `reindex`) |
| POST | `api/v1/notify-peer` (peer of orchestrator's `notify-ingestion`; tagged `ingestion-notifications`) |

### Local processing

| Method | Route |
|--------|-------|
| POST | `api/v1/local-processing/tickets` |
| POST | `api/v1/local-processing/random-ticket` |
| POST | `api/v1/local-processing/set-processed` |
| POST | `api/v1/local-processing/clear-all-processed` |

### Projects, query, dimensions, work groups, specifications

| Method | Route |
|--------|-------|
| GET  | `api/v1/projects` |
| GET  | `api/v1/projects/{key}` |
| PUT  | `api/v1/projects/{key}` |
| POST | `api/v1/query` |
| GET  | `api/v1/labels` |
| GET  | `api/v1/statuses` |
| GET  | `api/v1/users` |
| GET  | `api/v1/inpersons` |
| GET  | `api/v1/specifications` |
| GET  | `api/v1/specifications/{spec}` |
| GET  | `api/v1/issue-numbers` |
| GET  | `api/v1/work-groups` |
| GET  | `api/v1/work-groups/{groupCode}/issues` |
| GET  | `api/v1/work-groups/issues` |

## Zulip (`src/FhirAugury.Source.Zulip/Controllers/`, port :5170)

The cross-source `content/...` family and the lifecycle / ingestion
family are identical to Jira (same shapes, same routes).

### Items

| Method | Route |
|--------|-------|
| GET  | `api/v1/items` |
| GET  | `api/v1/items/{id}` |
| GET  | `api/v1/items/{id}/related` |
| GET  | `api/v1/items/{id}/snapshot` |
| GET  | `api/v1/items/{id}/content` |
| GET  | `api/v1/items/{id}/comments` (shape-parity stub — always returns `[]`) |
| GET  | `api/v1/items/{id}/links` (shape-parity stub — always returns `[]`) |

### Messages, streams, threads, query

| Method | Route |
|--------|-------|
| GET  | `api/v1/messages` |
| GET  | `api/v1/messages/{id:int}` |
| GET  | `api/v1/messages/by-user/{user}` |
| GET  | `api/v1/streams` |
| GET  | `api/v1/streams/{zulipStreamId:int}` |
| PUT  | `api/v1/streams/{zulipStreamId:int}` |
| GET  | `api/v1/streams/{streamName}/topics` |
| GET  | `api/v1/threads/{streamName}/{topic}` |
| GET  | `api/v1/threads/{streamName}/{topic}/snapshot` |
| POST | `api/v1/query` |

## Confluence (`src/FhirAugury.Source.Confluence/Controllers/`, port :5180)

The cross-source `content/...` family and the lifecycle / ingestion
family are identical to Jira.

### Items

| Method | Route |
|--------|-------|
| GET  | `api/v1/items` |
| GET  | `api/v1/items/{id}` |
| GET  | `api/v1/items/{id}/related` |
| GET  | `api/v1/items/{id}/snapshot` |
| GET  | `api/v1/items/{id}/content` |

### Pages, spaces

| Method | Route |
|--------|-------|
| GET  | `api/v1/pages` |
| GET  | `api/v1/pages/by-label/{label}` |
| GET  | `api/v1/pages/{pageId}` |
| GET  | `api/v1/pages/{pageId}/related` |
| GET  | `api/v1/pages/{pageId}/snapshot` |
| GET  | `api/v1/pages/{pageId}/content` |
| GET  | `api/v1/pages/{pageId}/comments` |
| GET  | `api/v1/pages/{pageId}/children` |
| GET  | `api/v1/pages/{pageId}/ancestors` |
| GET  | `api/v1/pages/{pageId}/linked` |
| GET  | `api/v1/spaces` |

## GitHub (`src/FhirAugury.Source.GitHub/Controllers/`, port :5190)

The cross-source `content/...` family and the lifecycle / ingestion
family are identical to Jira.

### Items — action-first layout (kept by design — analysis §5.1.4)

GitHub is the **only** source with action-first item routes. The
`{**key}` catch-all carries the full `owner/name#123` (or
`owner/name@sha`) identifier through the route template without URL-
escaping the `/`. This is intentional and is **not** scheduled for
unification with the other sources' `items/{id}/{action}` shape — it is
the right shape for keys that include slashes.

| Method | Route |
|--------|-------|
| GET  | `api/v1/items` |
| GET  | `api/v1/items/{**key}` |
| GET  | `api/v1/items/related/{**key}` |
| GET  | `api/v1/items/snapshot/{**key}` |
| GET  | `api/v1/items/content/{**key}` |
| GET  | `api/v1/items/comments/{**key}` |
| GET  | `api/v1/items/commits/{**key}` |
| GET  | `api/v1/items/pr/{**key}` |

### Repos, tags

| Method | Route |
|--------|-------|
| GET  | `api/v1/repos` |
| GET  | `api/v1/repos/{owner}/{name}/tags` |
| GET  | `api/v1/repos/{owner}/{name}/tags/files` |
| GET  | `api/v1/repos/{owner}/{name}/tags/search` |

### Jira-spec resolution (Jira-spec ↔ GitHub artifact mapping)

| Method | Route |
|--------|-------|
| GET  | `api/v1/jira-specs` |
| GET  | `api/v1/jira-specs/workgroups` |
| GET  | `api/v1/jira-specs/families` |
| GET  | `api/v1/jira-specs/by-git-url` |
| GET  | `api/v1/jira-specs/by-canonical` |
| GET  | `api/v1/jira-specs/resolve-artifact/{artifactKey}` |
| GET  | `api/v1/jira-specs/resolve-page/{pageKey}` |
| GET  | `api/v1/jira-specs/{specKey}` |
| GET  | `api/v1/jira-specs/{specKey}/artifacts` |
| GET  | `api/v1/jira-specs/{specKey}/pages` |
| GET  | `api/v1/jira-specs/{specKey}/versions` |

## Orchestrator-only endpoints

Documented in [`docs/user/api-reference.md`](../user/api-reference.md).
Highlights:

- `GET /api/v1/health` — liveness (always 200).
- `GET /api/v1/status` — readiness (200/503 from in-process registry).
- `GET /api/v1/services` — aggregate dashboard fan-out (per-source
  health, last-sync, item counts, index status).
- `POST /api/v1/ingest/trigger` — fan-out, accepts `?jira-project=KEY`
  (forwarded only to the Jira leg).
- `POST /api/v1/rebuild-index` — fan-out reindex.
- `POST /api/v1/notify-ingestion` — receives the source-side
  `notify-peer` call and propagates xref re-scan to the other sources.
- `GET /api/v1/source/orchestrator/openapi.json` — orchestrator-only
  OpenAPI document (the only surviving `api/v1/source/...` route).
- `GET /api/v1/source/orchestrator/list-sources` — enumerate enabled
  sources.
