# AGENTS.md

Canonical, machine-readable conventions for automated agents working in
**fhir-augury**. This file is the single source of truth that the
`.github/skills/dev-*` skills read before naming any build, test, or lint
command.

**Precedence.** This file is authoritative for commands, conventions, and
invariants an agent must follow. [`README.md`](README.md) and
[`docs/`](docs/) are authoritative for rationale, configuration reference,
and operational detail, and are the place to look for the "why". If this
file contradicts the repository itself, the repository wins — fix this file.

---

## What this repository is

FHIR Augury is a unified knowledge platform for searching across HL7 FHIR
community data sources. It downloads, indexes, and cross-references content
from Jira, Zulip, Confluence, and GitHub, with full-text search powered by
SQLite FTS5 and BM25 relevance scoring.

The framing fact that settles most design arguments: **v2 is a
microservices architecture.** Each data source is an independent ASP.NET
Core HTTP service owning its own SQLite database and file-system cache; the
Orchestrator is the only component that aggregates across sources. "Should
this component talk to that database directly?" is almost always answered
**no — go through the owning service's HTTP API.**

---

## Repository layout

| Path | Contents |
|-|-|
| `src/` | All shipping projects (sources, orchestrator, processors, MCP servers, CLI, Dev UI, Aspire AppHost). |
| `src/common.props` | Solution-wide MSBuild properties: `TargetFramework`, `LangVersion`, nullability, versioning, packaging. Imported explicitly by each `src` project. |
| `src/sqlite.props` | SQLite package references and the provider module initializer, imported by projects that open a database. |
| `tests/` | One xUnit test project per shipping project, plus `FhirAugury.Testing.Sqlite` (shared test infrastructure). |
| `tests/Directory.Build.props` | Test-tree MSBuild properties and SQLite wiring; applies automatically to everything under `tests/`. |
| `tools/` | Standalone console tools (`notes-site`, `ticket-site`, `fhir-spec-review`, `dictionary-build`, `ballotnotes-reallocate-wg`). |
| `docs/` | User and technical documentation. `docs/technical/development-guide.md` is the deep dive behind this file. |
| `dictionary/` | cspell word lists and their licenses. |
| `mcp-config-examples/` | Sample MCP client configurations. |
| `.github/skills/` | Copilot skills, including the `dev-*` inner-loop skills. |
| `.github/agents/` | Named sub-agent roles used by the `dev-*` inner-loop skills. |

**Ignored paths** (per `.gitignore`) — never place committed assets here:
`/scratch`, `/temp`, `/cache`, `/local`, `/secrets`, `nupkg/`, `bin/`,
`obj/`, `*.local.json`, `.env*`, and `*.db` / `*.db-shm` / `*.db-wal`.

Because databases and caches are ignored, any fixture a test needs must be
**created by the test**, not checked in as a `.db` file.

---

## Toolchain pins

- **.NET 10 SDK.** `TargetFramework` is `net10.0`, set in `src/common.props`
  and `tests/Directory.Build.props`. There is **no `global.json`**, so the
  pin is a floor enforced by the target framework, not an exact SDK pin.
- **C# 14** (`LangVersion` `14.0`), set in the same two files.
- **Nullable reference types are enabled** and **implicit usings are
  enabled** repository-wide.
- **xUnit 2.9.x** with `Microsoft.NET.Test.Sdk` 18.x and
  `xunit.runner.visualstudio` 3.x. See "Test" below — the runner choice
  determines the filter syntax.
- Package versions are declared **per project**; there is no central package
  management (`Directory.Packages.props`) and no lock file. When adding a
  dependency, match the version already used by a sibling project rather
  than picking the newest.
- **Aspire workload** is optional and needed only to run the AppHost:
  `dotnet workload install aspire`.

**Warnings are not errors.** `TreatWarningsAsErrors` is not set. `CS0436` is
globally suppressed via `NoWarn` in `src/common.props` because the
`CsLightDbGen` source generator emits attribute types into both
`FhirAugury.Common` and each source project; the local type always wins. Do
not "fix" a CS0436 and do not add new blanket suppressions.

---

## Build

```powershell
dotnet build fhir-augury.slnx
```

Scoped to a single project:

```powershell
dotnet build src\FhirAugury.Cli\FhirAugury.Cli.csproj
```

The expected baseline is **0 warnings, 0 errors**. If you see anything else,
investigate before attributing it — it may be environmental or pre-existing.
Confirm against a clean checkout or `HEAD` before calling it a regression.

### Running services lock the build output

This is the single most common false failure in this repository. If services
are running — under the Aspire AppHost, `dotnet run`, or Docker Compose —
they hold open handles on `bin\Debug\net10.0\*.dll`, and a solution build
fails with a burst of:

```
error MSB3021 / MSB3027 : Unable to copy file ... because it is being used by another process
warning MSB3026 : Could not copy ... Exceeded retry count of 10
```

**These are not code errors.** The message names the locking process
(e.g. `FhirAugury.Source.Jira (17172)`). Stop the running services and
rebuild, or scope the build to a project that is not running. Never
"fix" code in response to an MSB302x diagnostic, and never report one as a
build regression.

---

## Test

Tests are **xUnit v2** run through **VSTest**
(`Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`). This repository
does **not** use Microsoft.Testing.Platform, so `dotnet test --filter` is
valid and is the correct way to scope a run.

### Full suite

```powershell
dotnet test fhir-augury.slnx
```

### Scoped — one test project

```powershell
dotnet test tests\FhirAugury.Source.Jira.Tests
```

### Focused — one class or one test

```powershell
dotnet test tests\FhirAugury.Source.Jira.Tests --filter "FullyQualifiedName~JiraIngestionTests"
dotnet test tests\FhirAugury.Source.Jira.Tests --filter "FullyQualifiedName~JiraIngestionTests.MapsIssueFields"
```

**Prefer the smallest command that covers the change.** Escalate to the full
suite only when the focused run indicates you need to.

Setup notes that make a verification step actually runnable:

- SQLite tests depend on the native provider being registered by
  `SqliteProviderModuleInitializer.cs`, wired in through
  `src/sqlite.props` and `tests/Directory.Build.props`. A new test project
  under `tests/` picks this up automatically; one placed elsewhere will not.
- Some GitHub-source tests shell out to real `git`. They are guarded and
  skip when the prerequisite is absent — a skip is not a failure.
- Tests requiring live source credentials are not part of the default run.
  Do not add a verification step that needs a Jira cookie or a `gh` token.

---

## Lint / format

There is **no separate lint step and no `.editorconfig`** in this
repository. The build is the only enforcement, and `dotnet format` is not
part of the workflow. Do not introduce a linter or formatter without being
asked, and do not reformat files you are not otherwise changing.

Spelling is backed by the cspell word lists in `dictionary/`. When
introducing a genuinely new domain term, add it to
`dictionary/additional.words.txt` rather than working around the checker.

---

## Run

```powershell
# Everything at once, with the Aspire dashboard (recommended for development)
dotnet workload install aspire        # one-time
dotnet run --project src\FhirAugury.AppHost

# A single source service (Jira on :5160)
dotnet run --project src\FhirAugury.Source.Jira

# The orchestrator (needs source services running; :5150)
dotnet run --project src\FhirAugury.Orchestrator

# The full topology in containers
docker compose --profile full up -d
curl http://localhost:5150/health
```

Ports: Orchestrator `:5150`, Jira `:5160`, Zulip `:5170`, Confluence
`:5180`, GitHub `:5190`, FHIR spec `:5195`, processors `:5171`–`:5174`.

Configuration comes from each service's `appsettings.json`, overridden by a
gitignored `appsettings.local.json` and by environment variables with a
per-service prefix (e.g. `FHIR_AUGURY_JIRA_`). See
[`docs/configuration.md`](docs/configuration.md). **Never commit a
credential**; `appsettings.local.json` exists precisely so you do not have
to.

Under Aspire, Confluence, Dev UI, MCP HTTP, and the CLI use explicit start
and must be started by hand from the dashboard.

---

## Code style

There is no `.editorconfig`, so the authoritative source is
[`docs/technical/development-guide.md`](docs/technical/development-guide.md#code-conventions)
plus the surrounding code.

- **File-scoped namespaces** — one `namespace X;` per file.
- PascalCase for types, methods, and properties; camelCase for locals and
  parameters; private fields prefixed with `_` (e.g. `_connection`).
- Descriptive names; avoid abbreviations.
- Nullable reference types are on. Honour the annotations rather than
  silencing them with `!`.
- Database record types are `partial record class` with source-generator
  attributes. **The `partial` keyword is required** — removing it silently
  breaks code generation.
- Structured logging through `ILogger` throughout; no `Console.WriteLine`
  in services.
- Configuration binding uses the `IOptions<T>` pattern.
- HTTP error handling uses standard status codes (404, 503, 500, …);
  transient failures (429/5xx) go through `HttpRetryHelper`, which applies
  exponential backoff with jitter and respects `Retry-After`.
- Match the surrounding file. Consistency with neighbouring code beats any
  general preference.

### Architectural invariants

These are decisions, not preferences. Violating one is a review Blocker.

- **Services own their data.** A source service is the only component that
  opens its own SQLite database. Cross-source access goes through the
  Orchestrator's HTTP API, never through a second connection to someone
  else's file.
- **The Orchestrator is the only aggregator.** MCP tools, the CLI, and the
  Dev UI route through the Orchestrator rather than calling source services
  directly. Re-introducing a source-direct client is a regression that has
  been deliberately reverted before.
- **MCP tool names must be unique** across the registered servers; there is
  a guardrail test asserting this. Adding a duplicate breaks the whole tool
  catalog, not just the new tool.
- **Generated code is not hand-edited.** Source-generator output belongs to
  the generator; change the input or the generator.
- **Versions are generated, never bumped by hand.** `src/common.props`
  derives `Version`, `AssemblyVersion`, and `FileVersion` from the build
  timestamp (`yyyy.MMdd.HHmm`). Do not add a hard-coded version.
- **Adding a source is a nine-step checklist**, not just a new project:
  contracts, project, controllers, source-specific endpoints, Dockerfile,
  `docker-compose.yml`, AppHost registration and `WaitFor()` chain,
  orchestrator registration, and tests. See
  [Adding a New Source](docs/technical/development-guide.md#adding-a-new-source).

---

## Commit conventions

- **Conventional commits**: `<type>(<scope>): <subject>`. Types in active
  use here are `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`,
  `chore`, and `style`. Subject in the imperative, target ≤ 72 characters.
  Scope is **encouraged** and usually names the component
  (`feat(github-source):`, `fix(mcp-tools):`, `docs(readme):`).
- Required trailer, verbatim:

  ```
  Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
  ```

- One logical change per commit.
- When the GitHub integration below is on **and** the slot carries an
  `Issue` binding, `dev-do` adds an `Issue: #N` trailer to each phase
  commit, in addition to every trailer required above. When the
  integration is off or the slot is unbound, nothing is added.
- Agents **do not push** and **do not open pull requests** unless the user
  explicitly asks. `dev-pr-open` is the only skill permitted to do either.
- The default branch is `main`; day-to-day work lands on `dev`.

---

## GitHub Integration

**Off by default, in two independent ways.** A repository whose
`AGENTS.md` has **no** `## GitHub Integration` section is off. A section
whose `Enabled` row says **`no`** is equally off. In either case no skill
prompts about GitHub, and the `dev-*` loop behaves exactly as it did
before this feature existed.

The block below is **machine-managed**. This section is the **normative
definition** of both sentinel strings: every skill that reads or writes
the block reproduces the opener and the closer byte-for-byte from here,
and no skill re-derives, paraphrases, or reformats them.

<!-- >>> dev-* github integration (managed by dev-* skills) >>> -->
| Setting | Value |
|-|-|
| Enabled | yes |
| Repository | GinoCanessa/fhir-augury |
| Label — feature request | `enhancement` |
| Label — bug report | `bug` |
| Label — docs-only (additive) | `documentation` |
| Changelog file | none |
| Changelog entry format | n/a — repository keeps no changelog |
| PR opens as draft | no |
<!-- <<< dev-* github integration (managed by dev-* skills) <<< -->

**These sentinels are not `dev-setup`'s ignore-file sentinels.** The
ignore-file block that `dev-setup` maintains in `.gitignore` or
`.git/info/exclude` is delimited by
`# >>> dev-* skills (managed by dev-setup) >>>` and
`# <<< dev-* skills (managed by dev-setup) <<<`. That is a **different
block in a different file**, with a `#` comment prefix rather than an
HTML comment. Do not conflate the two, and never substitute one pair for
the other.

Rules for the block:

- Only `dev-setup`, `dev-issue`, and `dev-pr-open` may rewrite it, and
  only **in place** — never a second copy, never appended to the end of
  the file.
- Hand-written text outside the sentinels is never touched. Everything a
  human writes in this section survives every rewrite.
- A recorded value of `no`, `none`, or `n/a` is a **resolved answer**, not
  a missing one. It must never re-trigger a prompt on a later run.
- When `Enabled` is `no`, every other row is `n/a`.

---

## Scratch / slot convention

Local inner-loop work is organized into **slots** under `scratch/`:

```
scratch/<MMDD>-<##>/
  featurerequest.md    # authored by the dev-request skill
  bugreport.md         # authored by the dev-report skill
  approach-a.md        # authored by dev-approach (minimum change)
  approach-b.md        # authored by dev-approach (cleanest architecture)
  approach-c.md        # authored by dev-approach (unconstrained)
  approach.md          # authored by dev-approach (the judge's selection)
  plan.md              # authored by dev-plan, updated by dev-do
  analysis.md          # authored by dev-review
```

- `<MMDD>` is the local date (zero-padded month + day); `<##>` is a
  zero-padded two-digit slot number.
- `scratch/` is **ignored** (`/scratch` in `.gitignore`). Nothing in it is
  ever committed.
- Because the slot is ignored, **no plan phase may declare a `scratch/` path
  as an owned path.** `plan.md` is a control file that `dev-do` edits
  continuously and never stages or commits.

---

## Agent guardrails

- Read this file before proposing any build, test, or lint command. **Never
  invent a command.** If something you need is not documented here, say so
  rather than guessing.
- Subagents follow the **subagent model policy** recorded below.
- Do not add new linting, building, or testing tooling without being asked.
- Prefer the smallest targeted verification that covers the change; escalate
  to the full suite only when the targeted run indicates it is needed.
- **Treat an MSB3021 / MSB3026 / MSB3027 file-lock diagnostic as
  environmental.** Stop the running services and rebuild; do not change
  code and do not report a regression.
- **Never commit a credential.** Local secrets belong in the gitignored
  `appsettings.local.json` or in environment variables.
- **Do not commit database or cache artifacts.** `data/`, `cache/`,
  `temp/`, and every `*.db` are ignored on purpose.
- Documentation is expected to stay current with the code: this repository
  routinely lands `docs(...)` commits alongside feature work, and
  `docs/technical/` is treated as part of the deliverable, not an
  afterthought.
- Other Copilot skills in `.github/skills/` (the `fhir-augury-cli`,
  `orchestrate-*`, `ticket-*`, and `notes-*` families) document this
  project's operational workflows. Consult them before hand-rolling a
  workflow they already cover.

### Subagent model policy

Every `dev-*` skill that fans out reads this table before it spawns
anything, and each skill classifies its own roles as **reasoning** or
**mechanical**. **An absent or unreadable table means `uniform`** — the
conservative default, and the behavior this repository used before this
table existed.

| Setting | Value |
|-|-|
| Policy | uniform |
| Mechanical-tier model | n/a |

- **`uniform`** — every sub-agent runs the spawning agent's model
  configuration, whatever its role.
- **`tiered`** — a sub-agent in a **reasoning** role runs the spawning
  agent's configuration; a sub-agent in a **mechanical** role runs the
  recorded mechanical-tier model.

The role classification lives in the skills, not here: it is a property
of the loop and does not vary between repositories. Only the policy and
the model id do, which is why they are the two rows recorded.

A recorded value here is a **resolved answer**. `dev-setup` asks once and
never re-prompts, exactly as it treats the GitHub integration block.
