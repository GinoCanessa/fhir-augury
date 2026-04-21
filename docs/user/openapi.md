# OpenAPI and the Scalar UI

Every FHIR Augury service exposes its HTTP surface through an OpenAPI 3.1
document. The orchestrator additionally publishes a **merged** document that
describes its own endpoints plus every enabled source service, remapped under
`/api/v1/source/{name}/...`.

The orchestrator ships with a [Scalar](https://scalar.com/) documentation UI
that renders the merged document and lets you execute requests against a live
orchestrator from your browser.

> Looking for the merger internals, ETag semantics, or CLI discovery pipeline?
> See [`docs/openapi.md`](../openapi.md).

## Accessing the Scalar UI

When the orchestrator is running, open:

```
http://localhost:5150/scalar/v1
```

(Adjust the host/port if you have changed `Orchestrator:Ports:Http` or are
running behind a reverse proxy. The orchestrator address is also surfaced by
the Aspire dashboard when you launch via `aspire run`.)

The UI groups operations by tag. Each source service's operations appear under
`source:{name}/…` tags — for example `source:jira/Work Groups`. Use the
operation's **Test Request** panel to fill in parameters or the request body
and execute the call directly.

### What the UI shows you

- **Merged schema** — the orchestrator routes every source's `openapi.json`
  under `/api/v1/source/{name}/…`, so a single page documents all enabled
  sources plus the orchestrator's own routes.
- **Request body shapes** — for `POST` endpoints such as
  `/api/v1/source/jira/query`, the UI displays the exact JSON schema expected
  by the `body` parameter, including nested filter objects and enumerations.
- **Try it** — the UI issues requests from your browser to the orchestrator
  at the same origin, so no CORS configuration or API keys are required for
  local development.

### Alternative: raw OpenAPI documents

If you prefer to consume the OpenAPI document directly (for example to drive
a client generator or a different UI), the orchestrator exposes:

- `GET /api/v1/openapi.json` — merged JSON
- `GET /api/v1/openapi.yaml` — merged YAML
- `GET /api/v1/openapi.json?include=internal` — include `internal` operations
  (default hides them)
- `GET /api/v1/source/{name}/openapi.json` — passthrough to a single source's
  document
- `GET /api/v1/source/orchestrator/openapi.json` — orchestrator-only document
  (not merged)

Each source service (`source-jira`, `source-zulip`, `source-github`,
`source-confluence`) also exposes its own unmerged document at
`GET /api/v1/openapi.{json,yaml}` on its own port.

## Using Scalar with the DevUI API tester

The **DevUI → API Tests** page provides a curated catalog of endpoints. When
you select a source-specific API (for example `query.flexible` on Jira), the
page shows a collapsible **OpenAPI / schema** panel below the request
preview. Expand it to see the operation's `requestBody` schema and parameter
definitions pulled from the same OpenAPI document Scalar consumes. This is the
fastest way to discover what to put in the generic `body` field for
`POST` endpoints.

If Scalar is what you prefer, use the orchestrator's Scalar UI at
`/scalar/v1` side-by-side with the DevUI — the DevUI renders results and
parses common response shapes (search hits, cross-references) while Scalar
provides the richer schema view and auto-generated client snippets.

## Troubleshooting

- **`404 Not Found` at `/scalar/v1`** — the orchestrator is up but Scalar
  hasn't been mapped. Verify you're on a build that includes the
  `Scalar.AspNetCore` package reference on `FhirAugury.Orchestrator` and that
  `MapScalarApiReference` is called in `Program.cs`.
- **Scalar loads but operations are missing** — check
  `/api/v1/openapi.json?include=internal`. If a source is configured but
  unreachable, its operations are omitted from the merged document and the
  root carries an `x-augury-source-status` extension describing the failure.
  Run `GET /api/v1/services` (or the `augury sources` CLI command) to confirm
  health.
- **Requests from Scalar fail with CORS / auth errors** — ensure you're
  browsing Scalar from the same origin the orchestrator is listening on; the
  generic source proxy strips `Authorization` and `Cookie` headers by design.
