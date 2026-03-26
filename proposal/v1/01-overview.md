# FHIR Augury — System Overview

## Vision

FHIR Augury is a consolidated knowledge platform for the HL7 FHIR specification
ecosystem. It pulls data from all major community knowledge sources — Zulip chat,
Jira issue tracking, Confluence wiki, and GitHub — into a unified, searchable
index backed by SQLite. The system exposes this data through both a CLI and an MCP
server, and runs a long-lived background service that can ingest new data and
update indexes on-the-fly.

## Problem Statement

FHIR specification work is spread across four disconnected platforms:

| Source | Content | Volume |
|--------|---------|--------|
| **Zulip** (chat.fhir.org) | Community discussions, Q&A, working-group chats | 1M+ messages across 100+ streams |
| **Jira** (jira.hl7.org) | Specification feedback, ballot issues, tracker items | 48k+ issues with comments |
| **Confluence** (confluence.hl7.org) | Working-group pages, meeting notes, governance docs | Thousands of pages across FHIR spaces |
| **GitHub** (github.com/HL7) | Spec source, IG repos, PRs, issues, commits | Dozens of repositories |

Finding relevant information requires manually searching each platform. There is
no cross-source search, no unified timeline, and no way to correlate a Zulip
discussion with its related Jira issue and Confluence page.

## Goals

1. **Unified ingestion** — download and normalize data from all four sources into
   a common SQLite database with full-text search indexes.
2. **Cross-source correlation** — link related items across sources (e.g., a Jira
   issue key mentioned in a Zulip message, a Confluence page referencing a GitHub PR).
3. **Live service** — a long-running background service with an API that accepts
   incremental data submissions and updates indexes in real-time.
4. **Dual interface** — a CLI for interactive use and batch operations, plus an
   MCP server for LLM agent integration.
5. **Extensibility** — clean abstractions so new data sources can be added with
   minimal changes.

## Constraints

- **Language:** C# 14, .NET 10.0
- **Database:** SQLite via `cslightdbgen.sqlitegen` (source-generated CRUD)
- **Zulip client:** `zulip-cs-lib` NuGet package
- **No external services** — the system is fully self-contained; no cloud
  databases, no Elasticsearch, no Docker required.

## Prior Art

Two existing reference implementations inform this design:

- **`temp/JiraFhirUtils`** — A C# solution with a custom Roslyn source generator
  for SQLite, Jira XML download/load pipeline, BM25 keyword scoring, FTS5
  indexing, CLI tools, and an MCP server. Demonstrates the full pattern for
  download → parse → store → index → search → serve.

- **`temp/josh-fhir-community-search`** — A TypeScript/Bun toolkit for
  downloading and searching both Jira issues and Zulip messages. Uses
  contentless FTS5 for Jira and content-synced FTS5 for Zulip, with snapshot
  rendering for deep-dive on individual items.

FHIR Augury takes the best ideas from both — the type-safe C# source generation
approach from JiraFhirUtils, and the dual-source search patterns from
josh-fhir-community-search — and extends them to cover all four data sources
with a unified architecture and a long-running service layer.
