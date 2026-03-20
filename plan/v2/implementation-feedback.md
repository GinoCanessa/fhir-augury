# v2 Implementation Feedback & Notes

Tracking feedback, questions, and issues discovered during implementation.

---

## Phase 1: Foundation & Common Infrastructure

### Status: ✅ Complete (commit 294c582)

### Decisions Made
- Kept v1 Models/Database/Indexing namespaces intact — Common duplicates types with new namespaces
- CrossRefPatterns extracted as regex-only utility (no DB deps) — DB linking stays in orchestrator
- SourceDatabase is abstract base class — each service overrides InitializeSchema()
- GrpcClientExtensions uses GrpcChannel.ForAddress directly (no HttpClientFactory for gRPC)
- Text/ namespace contains all tokenization + classification (was Bm25/ in v1)

### Questions / Ambiguities
- (none)

### Issues Found
- (none)

---

## Phase 2: Jira Source Service

### Status: ✅ Complete

---

## Phase 3: Zulip Source Service

### Status: ✅ Complete

---

## Phase 4: Orchestrator, MCP & CLI

### Status: ✅ Complete

---

## Phase 5: Confluence & GitHub Sources

### Status: ✅ Complete

---

## Phase 6: Docker & Deployment

### Status: ✅ Complete

### Decisions Made
- v1 projects intentionally kept — v1 tests still validate shared code (Models, Database, Indexing); cleanup deferred
- In-process development host (6.4) deferred — individual `dotnet run` per service is sufficient for development
- Root Dockerfile builds the Orchestrator (the main entry point); per-service Dockerfiles in each service directory
- Docker Compose profiles: "full" (all 5), "jira-zulip" (3), "jira-only" (1 standalone)
- Orchestrator depends_on uses `required: false` for Confluence/GitHub so jira-zulip profile works
- Each service uses its own env var prefix (FHIR_AUGURY_JIRA_, FHIR_AUGURY_ZULIP_, etc.)
- MCP config examples updated for v2 gRPC-based architecture
- New docs at docs/deployment.md, docs/development.md, docs/configuration.md

### Items Not Implemented
- 6.4 In-process DevHost — deferred; each service can be run individually via `dotnet run`
- 6.7 v1 project removal — intentionally deferred; v1 tests validate shared library code
