---
name: ticket-plan
description: "Plans the implementation of a resolved FHIR Jira ticket. USE FOR: implementation planning, feature proposals, impact analysis, change planning, ticket implementation. Requires a Jira ticket key (e.g., FHIR-55197). Gathers ticket details including resolution, identifies affected GitHub repositories from cached clones, and produces a structured report with a feature proposal, impact analysis, and detailed implementation plan."
---

# Ticket Plan Skill

Produces a structured implementation plan for a resolved FHIR Jira ticket.
Given a ticket key, the skill gathers the resolution details, determines which
repositories are affected, and builds a markdown report containing a feature
proposal, impact analysis, and step-by-step implementation plan.

## Data Access

All data access in this skill (Jira, Zulip, GitHub, cross-references,
search, keywords) goes through the **`fhir-augury-cli`** skill. That skill
documents the CLI invocation form, the canonical recipes
(`get`, `cross-referenced`, `search`, `keywords`, `related-by-keyword`,
…), and the fallback chain (CLI → MCP → direct HTTP →
`appsettings.json`). Do not duplicate command-line knowledge here.

When a CLI command is shown below, it is in the form documented by
`fhir-augury-cli`:

```bash
fhir-augury-cli --json '<json>' [--pretty]
```

If the CLI is unavailable in the current environment, fall back per the
order documented in `fhir-augury-cli` (MCP → direct HTTP → `appsettings.json`).

## Inputs

- **Ticket key** *(required)* — e.g., `FHIR-55197`.
- **Output file** *(optional)* — full path where the report should be
  saved. If omitted, the agent picks a sensible default and reports the
  path back to the caller.
- **Working directory** *(optional)* — directory the agent may use for
  any transient files produced while gathering data (intermediate JSON
  dumps, scratch notes, downloaded snapshots, etc.). When supplied,
  **all transient files must be written under this directory** rather
  than the repo root or the current working directory. Create it with a
  cross-platform mechanism (PowerShell `New-Item -ItemType Directory
  -Force`, bash `mkdir -p`, or your file-system tool) if it does not
  already exist. Do not write transient files outside this directory.

## Prerequisites

- The GitHub source service cache must be populated (cloned repositories
  live under `cache/github/repos/<owner>_<name>/clone/`).
- For each in-scope repository, a current per-repo briefing must exist
  under `cache/github/repos/<owner>_<name>/repo-analysis/`. Step 3 below
  detects missing or stale briefings and stops to ask the user to run
  `repo-analysis`; this skill does not invoke `repo-analysis` itself.

## Workflow

When the user provides a Jira ticket key (e.g., `FHIR-55197`), execute the
following steps. Run independent calls in parallel where possible.

### Step 1: Resolution-Type Guard

Run **Step 1a** first. If `metadata.resolution` is one of `Not
Persuasive`, `Duplicate`, or `Withdrawn`, write a short report:

```markdown
# Implementation Plan: {TICKET-KEY}

**Resolution:** {resolution}

No implementation required because the resolution is `{resolution}`.
```

…and exit. Do not run the remaining steps. This avoids burning sub-agent
time during orchestrated runs.

Otherwise, proceed with Steps 1a–1c (gather ticket data) followed by
Steps 2–5.

**1a. Get the ticket with full content, comments, and snapshot:**

```bash
fhir-augury-cli --json '{"command":"get","source":"jira","id":"FHIR-55197","includeComments":true,"includeContent":true,"includeSnapshot":true}'
```

Key fields to extract from the response:
- `metadata.resolution` — the resolution type (e.g., "Applied", "Persuasive",
  "Not Persuasive", "Duplicate")
- `metadata.resolution_description` — free-text description of the resolution
- `metadata.specification` — which spec is targeted (e.g., "FHIR Core (FHIR)")
- `metadata.work_group` — owning work group
- `content` — the full ticket description
- `comments` — all discussion comments

**1b. Get all cross-references:**

```bash
fhir-augury-cli --json '{"command":"cross-referenced","value":"FHIR-55197","limit":50}'
```

From the cross-references response, categorize:
- **GitHub references**: PRs, issues, or commits that reference this ticket
- **Jira references**: related tickets that provide additional context
- **Zulip references**: chat discussions about this ticket

**1c. Get keywords for the ticket:**

```bash
fhir-augury-cli --json '{"command":"keywords","source":"jira","id":"FHIR-55197","limit":30}'
```

These keywords identify the FHIR resources, elements, and operations involved.

### Step 2: Determine In-Scope Repositories

From the resolution, linked artifacts, and cross-references, decide which
cached repositories the change touches (typically 1–2). Normalize each
to `owner/name`. Useful inputs:

- **Specification metadata** (e.g., "FHIR Core (FHIR)" → `HL7/fhir`).
- **GitHub cross-references** — IDs of the form `owner/repo#N` directly
  surface the repos already involved.
- **Keywords** — `fhir_path` entries (e.g., `Patient.identifier`) point
  at the resource the ticket is about.

If you still cannot infer any repo, default to the FhirCore repo
(`HL7/fhir`) and note the assumption in the report.

For the authoritative list of configured repos and their categories,
call (per `fhir-augury-cli`):

```bash
fhir-augury-cli --json '{"command":"call","source":"github","operation":"repos"}'
```

### Step 3: Load Saved Per-Repo Briefings

For **every** distinct `owner/name` repository surfaced by the GitHub
cross-references in Step 1b (and any additional in-scope repos identified
in Step 2), read the persisted briefing produced by the `repo-analysis`
skill:

- Briefing: `cache/github/repos/<owner>_<name>/repo-analysis/briefing.md`
- Metadata: `cache/github/repos/<owner>_<name>/repo-analysis/meta.json`

This skill is **data-only** with respect to repo-analysis: it reads the
cached artifacts but does **not** invoke the `repo-analysis` skill
itself. Check `meta.json` against the staleness rules documented in the
`repo-analysis` skill (clone HEAD + playbook SHA must both match).

If a briefing is **missing** or **stale** for any required repo, **stop
and ask the user** to run the `repo-analysis` skill before resuming —
e.g.:

> Briefing for `HL7/fhir` is stale (clone HEAD changed since last
> analysis). Please run `repo-analysis HL7/fhir if-stale` and let me
> know when it's ready.

Do not proceed with partial repo context, and do not fabricate repo
facts to fill the gap.

From each briefing, extract for use in later steps:

- **Category** (drives recipe / path expectations).
- **Authoring root(s)** and **generated areas (do not edit)**.
- **Ticket-Relevant Paths** / **Artifact Map**.
- **Recommended Change Recipes** that match the ticket.
- **Warnings / Gotchas** relevant to the proposed change.
- **Cross-Repo Touch Points**.

If there are **no GitHub cross-references** and Step 2 produced no
in-scope repos, this step is a no-op — record "No related GitHub
repositories." in the report and skip the briefing loads.

### Step 4: Analyze Impact

For each affected repository, assess the scope of change:

**4a. Examine existing definitions.**

Paths come from the briefing's Artifact Map / Ticket-Relevant Paths
loaded in Step 3. Use either the CLI or a direct read from the cache
clone to fetch the file content.

```bash
fhir-augury-cli --json '{"command":"get","source":"github","id":"HL7/fhir:source/patient/structuredefinition-Patient.xml","includeContent":true}'
```

Or read directly from the cache clone:

```powershell
Get-Content cache\github\repos\HL7_fhir\clone\source\<resource>\<file>.xml | Select-Object -First 50
```

**4b. Check for related PRs and commits.**

From the cross-references, identify any existing PRs or commits that have
already started implementing this change. Note whether they are open, merged,
or closed.

**4c. Look for related issues in the same area.**

Only when the resolution involves a coded element or cross-resource
concern. Cap `limit` at 10. Search for other tickets affecting the same
resources:

```bash
fhir-augury-cli --json '{"command":"search","query":"<resource-name>","sources":["jira"],"limit":10}'
```

**4d. Assess terminology impact.**

Only when the change involves coded elements. Cap `limit` at 10. Check
for ValueSet or CodeSystem changes needed in the UTG repository:

```bash
fhir-augury-cli --json '{"command":"search","query":"<valueset-name>","sources":["github"],"limit":10}'
```

### Step 5: Build the Report

Compose a markdown report with the sections described below, using the
Repo Context block (Step 3 briefings) to ground every concrete claim.
Use the gathered data to write substantive, specific content — not
generic placeholders.

---

## Report Format

The report MUST follow this structure. Every section is required, though
sections may note "None identified" if no data exists.

```markdown
# Implementation Plan: {TICKET-KEY}

| | |
|-|-|
| Ticket | ({TICKET-KEY}([{link to jira ticket}]) : {type} |
| Title | {ticket title} |
| Work Group | {work group} |
| Status | {priority} {status} |
| Labels | {comma-separated labels} |
| Specification | {specification} |
| Related Artifacts | {comma-separated list of related artifact names} |
| Related Pages | {comma-separated list of related page names} |
| Related URLs | {comma-separated list of related URLs} |
| Related Sections | {comma-separated list of related section values} |
| Reporter | {reporter} |
| Assignee | {assignee} |
| In-Person | {comma-separated list of in-person requesters} |
| Created | {created date} |
| Updated | {updated date} |
| Resolved | {resolved date} |

---

## Resolution Summary

{A clear, concise summary of what the resolution requires. Written based on
the resolution description and any applied-vote comments. Include the exact
wording of the resolution where available. If the resolution references
specific changes (e.g., "add element X to resource Y"), state them explicitly.}

## Feature Proposal

### Problem Statement

{What problem or gap does this ticket address? Describe the current state of
the specification and why it is insufficient. Reference the original ticket
description and any supporting Zulip discussions.}

### Proposed Change

{A detailed description of the change to be made. This should be concrete and
actionable:

- For new elements: specify the element name, path, type, cardinality,
  definition text, and any constraints.
- For modified elements: specify what changes (cardinality, type, binding,
  definition, constraints).
- For new extensions: specify the URL, context, type, and definition.
- For terminology changes: specify the CodeSystem/ValueSet changes.
- For behavioral changes: describe the new behavior precisely.
- For documentation changes: summarize the content to be added or modified.

Include example instances or snippets if helpful.}

### Design Rationale

{Why this approach was chosen. Reference FHIR design principles, consistency
with existing patterns, the resolution discussion, and any Zulip consensus.
Address potential alternatives that were considered and why they were not
chosen (if evident from comments or discussion).}

## Repo Context

{For each distinct repository loaded in Step 3, include a subsection
sourced from that repo's `repo-analysis/briefing.md`. If there are no
related GitHub repos, write "No related GitHub repositories." and omit
the subsections.}

### {owner/name} ({category})

- **Briefing:** `cache/github/repos/<owner>_<name>/repo-analysis/briefing.md` @ clone `{short-sha}`
- **Authoring root(s):** {from briefing}
- **Likely-touched paths for this ticket:** {paths from briefing's
  Ticket-Relevant Paths if present, else inferred from Authoring root(s)
  + the ticket's keywords / linked artifacts}
- **Applicable change recipes:** {names of recipes from the briefing's
  "Recommended Change Recipes" that match this ticket}
- **Gotchas to weigh in the plan:** {from briefing's "Warnings /
  Gotchas", filtered to what's relevant}
- **Cross-repo touch points:** {from briefing, only entries relevant to
  this ticket}

## Impact Analysis

{The Impact Analysis must reflect the Repo Context above. Sourced from
the briefings loaded in Step 3 (Authoring root(s), Artifact Map,
Ticket-Relevant Paths, Cross-Repo Touch Points).}

### Affected Repositories

{For each affected repository, list:}

#### {Repository Full Name} ({Category})

- **Role:** {What this repository contributes to the change}
- **Affected Files:**
  - `{file-path}` — {what changes in this file}
  - `{file-path}` — {what changes in this file}
- **Change Scope:** {Minor / Moderate / Major}

### Breaking Changes

{Identify any backward-incompatible changes. Consider:
- New required elements (was optional or missing before)
- Type changes to existing elements
- Removed elements or constraints
- Terminology binding changes (from example to required)
- Search parameter changes

If no breaking changes: "No breaking changes identified."}

### Related Specifications

{List other FHIR resources, operations, or profiles that reference or depend
on the changed artifacts. These may need conformance updates.}

### Related Tickets

{List other Jira tickets that affect the same resources/elements or are
otherwise related. For each, note:}
- **{TICKET-KEY}:** {title} — {how it relates, whether it conflicts or
  complements this change}

### Terminology Impact

{If the change involves coded elements, list affected ValueSets and
CodeSystems and describe what changes are needed. If none: "No terminology
impact."}

## Implementation Plan

### Prerequisites

{Any changes that must be completed before this work can begin, such as
terminology additions, extension definitions, or dependent ticket resolutions.}

### Step-by-Step Tasks

{Number each task. Group by repository. Each task should be specific enough
that a developer can execute it without ambiguity. Per-task `File:` paths
must come from the briefing's Artifact Map / Ticket-Relevant Paths and be
verified against the clone before being listed.}

#### {Repository Full Name}

1. **{Task title}**
   - File: `{path/to/file}`
   - Action: {Precise description of what to change}
   - Details: {Any additional context — element definitions, constraints,
     invariant expressions, binding strengths, etc.}

2. **{Task title}**
   - File: `{path/to/file}`
   - Action: {Precise description}
   - Details: {Additional context}

{Continue for each repository and task.}

### Validation Checklist

- [ ] StructureDefinition(s) validate with no errors
- [ ] Element definitions include short description and formal definition
- [ ] Cardinality is correct and consistent with the resolution
- [ ] Type constraints match the intended design
- [ ] Terminology bindings reference valid ValueSets
- [ ] Search parameters updated if the change adds searchable elements
- [ ] Examples updated to demonstrate the new/changed elements
- [ ] Resource scope/boundaries documentation updated if resource scope changed
- [ ] Cross-references to other resources are bidirectional
- [ ] No regressions in existing invariants or constraints

### Testing Considerations

{Describe what should be tested after the change is applied:
- Which resources need revalidation
- Example instances to create or update
- Edge cases to verify
- Interoperability considerations}

### Open Questions

{List any ambiguities in the resolution that need clarification before or
during implementation. Any Gotchas/Warnings surfaced by Repo Context that
affect implementation must be addressed here (or in the relevant task
above). If none: "No open questions."}
```

## Important Rules

- **The plan must be grounded in the Repo Context.** Name the specific
  repo and authoring root when proposing a change. If the briefing flags
  a gotcha that affects the plan, the relevant section must address it.
- **Use only data from the `fhir-augury-cli` skill (CLI / MCP) and cached
  repositories.** Do not fabricate ticket details, file paths, or
  resolution content. If a call fails or returns no data, say so in the
  report.
- **Be specific in the proposal.** Generic statements like "modify the
  resource" are not useful. Name the exact element, path, type, cardinality,
  and binding.
- **Include actual file paths.** When referencing repository files, use
  paths from the saved per-repo briefing (loaded in Step 3) and verify
  they exist in the clone before listing them. Do not invent paths.
- **The implementation plan must be actionable.** Each task should describe a
  single, concrete file change. A developer should be able to follow the plan
  without referring back to the original ticket.
- **Assess breaking changes honestly.** Do not downplay impact. If a change
  adds a new required element, that is a breaking change — say so.
- **Cross-reference related tickets.** Look at the cross-references and
  keyword-related items to identify tickets that may conflict with or depend on
  this change.
- **Read the resolution description carefully.** The resolution (not the
  original ticket description) dictates what must be implemented. The ticket
  description states the problem; the resolution states the approved solution.
- **Trust the saved briefing.** Repo layout, build system, and recipes
  come from `cache/github/repos/<owner>_<name>/repo-analysis/briefing.md`.
  If the briefing is stale, re-run `repo-analysis` rather than
  re-discovering layout inline.
