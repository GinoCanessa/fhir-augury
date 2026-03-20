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

### Status: Not Started

---

## Phase 5: Confluence & GitHub Sources

### Status: Not Started

---

## Phase 6: Docker & Deployment

### Status: Not Started
