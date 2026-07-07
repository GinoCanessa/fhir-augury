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

## Generating outputs

End-to-end guides for the three output pipelines. Each makes the easy-to-skip
middle step explicit and leads its troubleshooting with the symptom you'd
actually see.

| Guide | What it produces |
|-------|------------------|
| [Generating Ballot Notes](generating-ballot-notes.md) | A `notes-site` static site of **proposed** ballot notes (start → hydrate → **author** → render). |
| [Generating Discussion Tickets](generating-discussion-tickets.md) | The `ticket-site` **Tickets for Discussion** sub-site (prepare → **topic groupings** → render). |
| [Generating Application Tickets](generating-application-tickets.md) | The `ticket-site` **Tickets for Applying** sub-site, plus the Applier apply-and-push flow. |

## Reference

| Guide | What it covers |
|-------|----------------|
| [CLI Reference](cli-reference.md) | All CLI commands and options. |
| [API Reference](api-reference.md) | The HTTP/REST APIs exposed by each service. |
| [MCP Tools](mcp-tools.md) | Using FHIR Augury as a Model Context Protocol server. |
| [OpenAPI and the Scalar UI](openapi.md) | The per-service and merged OpenAPI documents and the Scalar UI. |
| [Docker Deployment](docker.md) | The multi-container Docker Compose deployment. |
