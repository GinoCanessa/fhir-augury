# Code Review: Infrastructure, Configuration & Documentation

**Reviewed:** 2026-03-19
**Scope:** Dockerfile, docker-compose, README, build props, .gitignore, MCP config, docs, plan/proposal

---

## Dockerfile

### [Critical] Container runs as root — ✅ **FIXED**
**Lines 28-43** — Added non-root `appuser` with `USER appuser` directive before `ENTRYPOINT`. Created data directories and set ownership.

No `USER` directive. Container security best practice violation.

**Fix:** Add `RUN adduser --disabled-password --gecos '' appuser && chown -R appuser /app /data` and `USER appuser`.

---

### [Critical] No HEALTHCHECK instruction — ✅ **FIXED**

**Resolution:** Added `HEALTHCHECK` using `wget` against the `/health` endpoint with 30s interval, 5s timeout, and 3 retries.

No `HEALTHCHECK` directive. Docker/orchestrators can't monitor container health.

**Fix:** Add `HEALTHCHECK CMD curl -f http://localhost:5100/health || exit 1` or dotnet-based check.

---

### [Medium] No .dockerignore file
`COPY src/ src/` sends unnecessary files to build context (bin/obj/docs/tests), inflating build times.

**Fix:** Create `.dockerignore` excluding `bin/`, `obj/`, `tests/`, `docs/`, `plan/`, `.git/`, `*.db`.

---

### ✅ Good: Layer caching strategy
Properly copies `.csproj` files first, runs `dotnet restore`, then copies source. Optimizes Docker layer caching.

### ✅ Good: Multi-stage build
SDK image for build, slim `aspnet` image for runtime.

### [Info] No pinned image digest
Uses `dotnet/sdk:10.0` without specific digest. For reproducible builds, consider pinning.

---

## docker-compose.yml

### [Medium] No resource limits
No `deploy.resources.limits`. A runaway indexing job could consume all host resources.

### [Medium] No restart policy
No `restart: unless-stopped`. If the service crashes, it stays down.

### [Medium] No read_only filesystem
Running with a writable root filesystem is less secure.

### [Medium] Credential comments show plaintext pattern
While commented out, examples show credentials in plaintext environment variables.

### [Info] Port 5200 inconsistency
Only port 5100 exposed, but MCP HTTP transport uses port 5200.

### ✅ Good: Named volume for database

---

## README.md

### ✅ Good: Comprehensive and well-structured
Architecture diagram, feature list, component table, prerequisites, tech stack, docs links, license.

### [Medium] No contributing guidelines
No CONTRIBUTING.md or contributing section.

### [Medium] No badges or CI status
No build status, license badge, or version badge.

### [Info] Quick start only shows CLI
A Docker quick start would help.

---

## Build Props (common.props / Directory.Build.props)

### [Medium] No TreatWarningsAsErrors
Nullable reference type violations and other warnings won't fail the build.

**Fix:** Add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` to `common.props`.

---

### [Medium] Non-deterministic build versions
```xml
<VersionPrefix>$([System.DateTime]::Now.ToString("yyyy.MMdd.HHmm"))</VersionPrefix>
```

Every build produces a unique version. Two builds of the same commit produce different version numbers.

---

### ✅ Good: Nullable and ImplicitUsings enabled
### ✅ Good: Directory.Build.props import pattern

---

## .gitignore

### [Medium] `.env` files not ignored — ✅ **FIXED**
Docker docs recommend `.env` for credentials, but `.env` is not in `.gitignore`. Accidental credential commits possible.

**Resolution:** Added `.env` and `.env.*` patterns to `.gitignore`.

---

### [Medium] SQLite database files not explicitly ignored — ✅ **FIXED**
`*.db` files not ignored. A `fhir-augury.db` (hundreds of MB) could be accidentally committed.

**Resolution:** Added `*.db`, `*.db-shm`, `*.db-wal` patterns to `.gitignore`.

---

### ✅ Good: Sensitive patterns covered
`/secrets`, `/local`, `*.local.json`, `/cache` all excluded.

---

## MCP Config Examples

### [Medium] Port 5200 not documented in service config
`http-client.json` uses `http://localhost:5200/mcp`, but service runs on port 5100. Relationship unclear.

### [Low] Placeholder paths in config
Uses `/path/to/fhir-augury/...` — acceptable as a template.

---

## Documentation

### ✅ Excellent: Technical documentation
6 detailed documents covering architecture, database schema, indexing, data sources, development, and project structure.

### ✅ Excellent: User documentation
6 well-written documents: getting-started, CLI reference, configuration, API reference, MCP tools, Docker.

### [Medium] Environment variable format inconsistency
`docs/user/configuration.md` shows `FHIR_AUGURY_FhirAugury__` prefix, but `docker-compose.yml` uses `FHIR_AUGURY_Sources__`. These can't both work unless prefix mapping differs.

---

## Plan & Proposal

### ✅ Well-structured phased approach
7-phase plan with clear goals, dependencies, acceptance criteria.

### [Low] Proposal references non-existent `temp/` directories
Historical context only, but readers may wonder.

### [Low] Proposal mentions unimplemented OpenAI/Azure dependencies
Could mislead readers into thinking AI features exist.

### [Low] Proposal mentions GraphQL for GitHub (only REST implemented)

### [Info] Local caching plan status stale
Marked "Pending" but feature appears implemented.

---

## Summary

| Severity | Count |
|----------|-------|
| **Critical** | 2 |
| **Medium** | 10 |
| **Low** | 4 |
| **Info** | 4 |
| **Total** | **20** |

### Top Priorities
1. **Add non-root USER to Dockerfile** — security critical
2. **Add HEALTHCHECK to Dockerfile** — operational critical
3. **Create `.dockerignore`** — build performance
4. **Add `.env` and `*.db` to `.gitignore`** — leak prevention
5. **Add `restart: unless-stopped` to docker-compose** — reliability
6. **Add `TreatWarningsAsErrors` to common.props** — code quality
7. **Clarify environment variable prefix convention** between docs and docker-compose
