# OpenAPI and the Scalar UI

Every FHIR Augury service exposes its HTTP surface through an OpenAPI 3.1
document. The orchestrator additionally publishes a **merged** document that
combines its own routes with operations and schemas from every enabled source
document. Source paths are remapped under `/api/v1/{name}/...`, but inclusion
in the merged document is not by itself a routability guarantee: legacy
remapped lifecycle operations can remain even when clients should use an
Orchestrator-native replacement.

The orchestrator ships with a [Scalar](https://scalar.com/) documentation UI
that renders the merged document and lets you execute routable requests against
a live orchestrator from your browser.

> Looking for the merger internals, ETag semantics, or CLI discovery pipeline?
> See [`docs/openapi.md`](../openapi.md).

## Accessing the Scalar UI

When the orchestrator is running, open:

```
http://localhost:5150/scalar/v1
```

(Adjust the host/port if you have changed `Orchestrator:Ports:Http` or are
running behind a reverse proxy. The orchestrator address is also surfaced by
the Aspire dashboard when you launch the documented AppHost.)

The UI groups operations by tag. Each source service's operations appear under
`source:{name}/…` tags — for example `source:jira/Work Groups`. Use the
operation's **Test Request** panel to fill in parameters or the request body
and execute the call directly.

### What the UI shows you

- **Merged schema** — one page combines all available enabled-source documents
  with the orchestrator's own routes. The merger can retain legacy remapped
  source lifecycle paths that do not have a corresponding typed proxy; use
  `GET /api/v1/services` and `GET /api/v1/stats` instead.
- **Request body shapes** — for `POST` endpoints such as
  `/api/v1/jira/query`, the UI displays the exact JSON schema expected
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
- `GET /api/v1/source/orchestrator/openapi.json` — orchestrator-only document
  (not merged). This is the only `/api/v1/source/...` route; there is no
  generic source proxy.

Each source service (`source-jira`, `source-zulip`, `source-github`,
`source-confluence`, `source-fhir`) also exposes its own unmerged document at
`GET /api/v1/openapi.{json,yaml}` on its own port.

## Using Scalar with the Dev UI API tester

The **Dev UI → API Tests** page keeps familiar Orchestrator, Jira, Zulip,
Confluence, GitHub, and FHIR source tabs, but a tab is only a source-oriented
catalog view. Enabled source names come from `GET /api/v1/services`; every
request still uses the configured `DevUi:OrchestratorAddress`. There is no
source-direct client or per-source address in Dev UI configuration.

Each source operation in the Dev UI's explicit route matrix has one gateway
disposition:

- an Orchestrator-native aggregate route, with the selected source fixed where
  the contract supports it;
- a typed `/api/v1/{name}/...` source proxy; or
- a typed Orchestrator readiness, statistics, lifecycle, or metadata
  replacement.

The real-route guarantee is limited to this explicit Dev UI matrix. Do not
infer that every source operation copied into the raw merged document is
routable; in particular, legacy remapped lifecycle paths can still be
advertised by the merger.

When you select an operation (for example `query.flexible` on Jira), the page
shows its final Orchestrator target and a collapsible **OpenAPI / schema**
panel below the request preview. The schema and parameters are read from the
merged `GET /api/v1/openapi.json` document—the same document Scalar consumes,
not a source-local document. This preserves source-oriented exploration while
enforcing the Orchestrator-only client boundary.

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
  Read `GET /api/v1/services`, or request a fresh sweep with
  `POST /api/v1/services/refresh`, to inspect typed source and processor
  observations.
- **Requests from Scalar fail with CORS / auth errors** — ensure you're
  browsing Scalar from the same origin the orchestrator is listening on; the
  typed source proxies strip `Authorization` and `Cookie` headers by design.
