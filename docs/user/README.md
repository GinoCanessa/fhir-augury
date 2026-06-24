# User Guides

Task-oriented guides for using FHIR Augury. Start with **Getting Started**, then
use the topic guides below as you need them. These guides sit *above* the
[technical runbooks](../technical/) and link down to them for full detail.

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
