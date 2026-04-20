---
name: repo-analysis
description: "Generates and persists per-repo briefings for cached FHIR GitHub repositories. USE FOR: refreshing or producing the briefing.md + meta.json artifacts under cache/github/repos/<owner>_<name>/repo-analysis/ that ticket-prep and ticket-plan consume. Run on demand; supports refresh / if-stale / dry-run modes and ticket-scoped runs."
---

# Repo Analysis Skill

LLM-driven generator that analyzes one or more cached FHIR repositories and
writes a persistent briefing per repo. The persisted briefings are the
contract `ticket-prep` / `ticket-plan` consume; this skill is the only
producer of them.

This skill ships **no code**. Everything runs in the agent loop using the
`fhir-augury` CLI (per the `fhir-augury-cli` skill, with documented
fallbacks) plus filesystem reads/writes under `cache/`.

## When to use it

- You (the human) want to refresh briefings — run with explicit repo names
  or `--all`.
- A downstream skill (`ticket-prep`, `ticket-plan`) reports that a briefing
  is missing or stale and asks for a re-run.
- A new repo was added to `appsettings.json` under one of the
  `*Repositories` lists and the cache has been populated.

The skill is **on-demand**. There is no scheduler, no CI generator, and no
`fhir-augury skills regen` subcommand.

## Inputs

- **Repo selectors.** One of:
  - `owner/name` (display form).
  - `owner_name` (filesystem form — the directory name under
    `cache/github/repos/`).
  - `--all` — every repo configured under any `*Repositories` list in the
    GitHub source.
- **Optional ticket / scope context.** A Jira ticket key, summary, or
  list of key terms / linked artifacts. When supplied, the briefing
  gains a "Ticket-relevant paths" section in addition to the durable
  per-repo facts. When omitted, the briefing is purely structural and is
  still useful as a cache for later ticket runs.
- **Mode:**
  - `refresh` (default) — re-analyze and overwrite. Use for explicit
    re-runs.
  - `if-stale` — skip repos whose `meta.json.clone_head_sha` matches the
    current clone HEAD **and** whose `playbook_sha` matches the
    current source-controlled playbook; analyze the rest. This is what
    downstream skills request when they just need a current briefing.
  - `dry-run` — print the plan (which repos would be analyzed and why)
    without reading the clone or writing outputs.

## Procedure (per repo, parallel where practical)

### 1. Resolve the repo's category

Preferred: invoke the `fhir-augury` CLI per the `fhir-augury-cli` skill:

```bash
fhir-augury-cli --json '{"command":"call","source":"github","operation":"repos"}'
```

Each entry in the returned `repos` array carries `fullName`, `category`,
`url`, `description`, `issueCount`, `prCount`. The category enum values are
defined in `src/FhirAugury.Source.GitHub/Configuration/RepoCategory.cs`:
`FhirCore`, `Utg`, `FhirExtensionsPack`, `Incubator`, `Ig`,
`JiraSpecArtifacts`.

Fallback chain (per `fhir-augury-cli`):

1. FhirAugury MCP server: equivalent tool if exposed.
2. Direct HTTP: `GET http://localhost:5190/api/v1/repos` (the GitHub
   source's `ReposController`).
3. Static read: parse
   `src/FhirAugury.Source.GitHub/appsettings.json` and match the
   normalized `owner/name` against each `*Repositories` list. This is
   sufficient for category resolution because the mapping is a static
   config.

If the repo appears in **none** of the configured lists, **stop and
report**:

> `owner/name` is not configured under any `*Repositories` list. Add it
> to `src/FhirAugury.Source.GitHub/appsettings.json` (under the
> appropriate `*Repositories` list) before analyzing.

Do **not** try to second-guess the category.

### 2. Load the matching category playbook

Read `.github/skills/repo-analysis/categories/<kebab>.md` based on the
resolved category:

| Category | Playbook |
|----------|----------|
| `FhirCore` | `categories/fhir-core.md` |
| `Utg` | `categories/utg.md` |
| `FhirExtensionsPack` | `categories/fhir-extensions-pack.md` |
| `Incubator` | `categories/incubator.md` |
| `Ig` | `categories/ig.md` |
| `JiraSpecArtifacts` | `categories/jira-spec-artifacts.md` |

Capture the playbook's git SHA (or, if working tree differs from HEAD, a
content hash) for `meta.json`:

```powershell
git -C C:\ai\git\fhir-augury hash-object .github/skills/repo-analysis/categories/<kebab>.md
```

If a category is encountered without a matching playbook file, report a
hard error — every value of `RepoCategory` must have a playbook.

### 3. Probe the clone

Under `cache/github/repos/<owner>_<name>/clone/`, read:

- `README.md` (first ~8 KB).
- `sushi-config.yaml` if present (top-level).
- `ig.ini` if present.
- `source/fhir.ini` if present (FhirCore).
- Top-level directory listing (one level deep) for orientation.
- Ticket-relevant subtrees **only** when ticket context was supplied.
  Pick targets from the playbook's "Artifact map" plus the ticket
  keywords. Examples: `input/fsh/profiles/` for a ticket about a
  profile; `source/<resource>/` for a FhirCore ticket about a resource.

Capture the clone's current HEAD:

```powershell
git -C cache\github\repos\<owner>_<name>\clone rev-parse HEAD
```

If the clone directory is missing, report:

> `cache/github/repos/<owner>_<name>/clone` is not present. Run the
> GitHub source ingestion (e.g.,
> `fhir-augury --json '{"command":"ingest","action":"run","sources":["github"]}'`)
> before analyzing.

### 4. Synthesize the briefing

Combine playbook defaults with what the live clone actually shows. Format:
a single Markdown file per repo with these sections, in this order:

```markdown
# Repo Briefing: {owner/name}

- **Category:** {category}
- **Default branch:** {branch}
- **Clone HEAD:** {short-sha} ({full-sha})
- **Analyzed at:** {iso-8601 utc}
- **Playbook:** {playbook path} @ {playbook short-sha}
- **Ticket scope:** {ticket key or "none"}

## Facts

- **Build system:** ...
- **Authoring root(s):** ...
- **Generated areas (do not edit):** ...
- **Notable top-level files / folders observed in this clone:** ...

## Ticket-Relevant Paths

(Only present when ticket context was supplied.)

- `path/to/file` — {why this is relevant}

## Cross-Repo Touch Points

(From the playbook, filtered to what's actually present / actually
relevant for the supplied ticket scope.)

## Recommended Change Recipes

(From the playbook, parameterized with paths observed in this clone.)

## Warnings / Gotchas

(Playbook gotchas plus anything the live probe surfaced — e.g., presence
of both FSH and XML for the same artifact ID, missing `sushi-config.yaml`
where one was expected, draft/experimental status flags.)
```

Where the live clone contradicts the playbook (e.g., a folder the
playbook claims exists isn't there), **prefer the live clone** and note
the discrepancy under "Warnings / Gotchas".

### 5. Persist

Write two files under
`cache/github/repos/<owner>_<name>/repo-analysis/`:

- `briefing.md` — the Markdown above.
- `meta.json` — at minimum:
  ```json
  {
    "analyzed_at": "<ISO-8601 UTC>",
    "owner_name": "<owner/name>",
    "category": "<RepoCategory enum value>",
    "clone_head_sha": "<full SHA>",
    "playbook_path": ".github/skills/repo-analysis/categories/<kebab>.md",
    "playbook_sha": "<git SHA or content hash>",
    "source": "cli" | "mcp" | "http" | "appsettings",
    "ticket_key": "<FHIR-XXXXX>"   // only when ticket context supplied
  }
  ```

Create the `repo-analysis/` directory if it doesn't exist. The `cache/`
root is already gitignored (`/cache` in `.gitignore`); no further
exclusions are needed.

There is **no central index file**. The set of analyzed repos is
recoverable by globbing
`cache/github/repos/*/repo-analysis/meta.json`.

### 6. Report

Echo a concise per-repo summary:

```
✔ HL7/fhir @ 1a2b3c4 (FhirCore, source: cli)
   briefing: cache/github/repos/HL7_fhir/repo-analysis/briefing.md
   meta:     cache/github/repos/HL7_fhir/repo-analysis/meta.json
↻ HL7/UTG @ 5d6e7f8 (already current; skipped — if-stale mode)
✗ Foo/bar (not configured under any *Repositories list)
```

The user spot-checks from this summary; absolute paths make verification
trivial.

## Standalone invocation examples

> "Run `repo-analysis` for `HL7/US-Core` and `HL7/fhir`."
>
> "Re-run `repo-analysis --all if-stale`."
>
> "Run `repo-analysis HL7/UTG` for ticket FHIR-12345."
>
> "`repo-analysis --all dry-run` — show what you'd refresh."

## Staleness rules

A briefing is **fresh** when **both** of the following are true:

- `meta.json.clone_head_sha` equals the current
  `git -C <clone> rev-parse HEAD`.
- `meta.json.playbook_sha` equals the current source-controlled playbook
  hash.

Any mismatch → stale. Downstream skills check `meta.json` before reading
`briefing.md`. Stale or missing → either prompt the user to re-run or
invoke `repo-analysis <repo> if-stale` themselves (depending on how the
downstream skill is configured to behave when unattended).

A briefing whose recorded HEAD no longer exists in the clone (e.g., the
clone was rewound or re-cloned) is also stale.

The user can always force a refresh with `repo-analysis <repos> refresh`
regardless of staleness.

## Important rules

- **Use only the playbooks plus the live clone.** Do not invent paths
  or facts. If the playbook claims something the clone contradicts,
  prefer the clone and note the discrepancy.
- **Do not re-classify.** If a repo's category looks "wrong", say so in
  the report and suggest fixing `appsettings.json` — but write the
  briefing using the configured category. Misclassification is a config
  bug, not something this skill silently corrects.
- **Per-repo files only.** One `briefing.md` and one `meta.json` per
  repo. No combined / cross-repo summary file. This keeps downstream
  agents able to load only the repos they need.
- **Re-runs overwrite.** The artifacts are derived; they are not a
  history log. Re-running `repo-analysis` for a repo overwrites both
  files so the analysis stays current.
- **Record provenance.** Always populate `meta.json.source` so a
  downstream consumer can tell whether the analysis used CLI, MCP,
  HTTP, or appsettings to resolve category.
- **Write absolute paths in the report.** They make spot-checking
  fast.
