# User Guides

Task-oriented guides for using FHIR Augury. Start with **Getting Started**, then
use the topic guides below as you need them.

The documentation is organized in three tiers:

- **Reference** ([`docs/`](../)) — canonical, cross-cutting references for a topic.
- **User guides** (this folder) — task-oriented "how do I…" guides that link
  *down* to the reference and technical docs for full detail.
- **Technical docs** ([`docs/technical/`](../technical/README.md)) — deep
  implementation and reference material for contributors.

## Getting started

| Guide | What it covers |
|-------|----------------|
| [Getting Started](getting-started.md) | Set up FHIR Augury v2, configure source credentials, start the services, and run your first search. |
| [Configuration](configuration.md) | How to configure each service via files and environment variables. |

## Ticket operations workspace

When running through Aspire, start the explicit `devui` resource and open
`http://localhost:5210`. The root page guides Preparer and Planner runs,
reconnects to processor-owned active/recent history, and publishes run-scoped
local review sites. Aspire remains responsible for starting/stopping resources
and for logs and traces.

The discussion and application guides below document both this UI path and the
supported CLI/skill headless equivalent.

## Generating outputs

Each output pipeline freezes a processor-owned authoring run. Accepted
receipts remain durable if later finalization fails; after processor-owned
finalization completes, download the canonical snapshot and descriptor, then
render or publish only from that verified pair. The guides cover polling,
retries, recovery, and publication in detail.

| Guide | What it produces |
|-------|------------------|
| [Generating Ballot Notes](generating-ballot-notes.md) | Run hydration, author **proposed** ballot notes in a BallotNotes run, download its verified snapshot pair, then render the `notes-site` site. |
| [Generating Discussion Tickets](generating-discussion-tickets.md) | Use the Dev UI or CLI/skill equivalent to control a Preparer run and publish its verified snapshot as a run-scoped discussion site. |
| [Generating Application Tickets](generating-application-tickets.md) | Use the Dev UI or CLI/skill equivalent to control a Planner run and publish its verified snapshot as a run-scoped applying site. The separate Applier flow remains outside the UI. |

## Reference

| Guide | What it covers |
|-------|----------------|
| [CLI Reference](cli-reference.md) | All CLI commands and options. |
| [API Reference](api-reference.md) | The Orchestrator aggregate, typed source-proxy, readiness, and processor-control APIs, plus direct service references. |
| [MCP Tools](mcp-tools.md) | Using FHIR Augury as a Model Context Protocol server. |
| [OpenAPI and the Scalar UI](openapi.md) | The per-service and merged OpenAPI documents and the Scalar UI. |
| [Docker Deployment](docker.md) | The multi-container Docker Compose deployment. |
