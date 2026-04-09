---
name: ticket-plan
description: "Plans the implementation of a resolved FHIR Jira ticket. USE FOR: implementation planning, feature proposals, impact analysis, change planning, ticket implementation. Requires a Jira ticket key (e.g., FHIR-55197). Gathers ticket details including resolution, identifies affected GitHub repositories from cached clones, and produces a structured report with a feature proposal, impact analysis, and detailed implementation plan."
---

# Ticket Plan Skill

Produces a structured implementation plan for a resolved FHIR Jira ticket.
Given a ticket key, the skill gathers the resolution details, determines which
repositories are affected, and builds a markdown report containing a feature
proposal, impact analysis, and step-by-step implementation plan.

## Prerequisites

- The `fhir-augury` CLI must be available (installed as a dotnet tool or via
  local alias).
- The FHIR Augury services must be running and accessible (the CLI connects to
  the orchestrator, default `http://localhost:5150`).
- The GitHub source service cache must be populated (cloned repositories live
  under `cache/github/repos/`).

## CLI Reference

The `fhir-augury` CLI accepts JSON commands. All examples below use inline
JSON; for readability, use `--pretty` when inspecting output manually.

```bash
fhir-augury --json '<json>' --pretty
```

### Key Commands

| Command | Purpose |
|---------|---------|
| `get` | Fetch full details of an item from a source |
| `cross-referenced` | Get all cross-references (both directions) for a value |
| `refers-to` | Get outgoing cross-references from an item |
| `referred-by` | Get incoming cross-references to an item |
| `search` | Unified text search across all sources |
| `keywords` | Get extracted keywords for an item |
| `related-by-keyword` | Find items related by keyword similarity |

## Known GitHub Repository Cache

The following repositories are cloned under `cache/github/repos/`. The
directory names use underscores in place of slashes (e.g., `HL7_fhir` for
`HL7/fhir`). Each contains a `clone/` subdirectory with the actual git
checkout.

| Directory | Repository | Category |
|-----------|------------|----------|
| `HL7_fhir` | HL7/fhir | FhirCore |
| `HL7_UTG` | HL7/UTG | Utg (Unified Terminology Governance) |
| `HL7_fhir-extensions` | HL7/fhir-extensions | FhirExtensionsPack |
| `HL7_admin-incubator` | HL7/admin-incubator | Incubator |
| `HL7_api-incubator-ig` | HL7/api-incubator-ig | Incubator |
| `HL7_capstmt` | HL7/capstmt | Incubator |
| `HL7_cg-incubator` | HL7/cg-incubator | Incubator |
| `HL7_ebm-incubator` | HL7/ebm-incubator | Incubator |
| `HL7_fhir-testing-ig` | HL7/fhir-testing-ig | Ig |
| `HL7_immunization-incubator` | HL7/immunization-incubator | Incubator |
| `HL7_oo-incubator` | HL7/oo-incubator | Incubator |

### Repository Structure Conventions

**HL7/fhir (FhirCore):**
- Source files under `source/` directory
- Artifact directories: `source/<resource-name>/` (e.g., `source/patient/`)
- StructureDefinitions: `structuredefinition-*.xml`
- Canonical artifacts: `codesystem-*.xml`, `valueset-*.xml`, `searchparameter-*.xml`, etc.
- Configuration: `source/fhir.ini` maps artifact names to directories

**HL7/UTG (Terminology):**
- CodeSystems and ValueSets under `input/` directory
- Contains `sourceOfTruth/` with authoritative definitions

**HL7/fhir-extensions:**
- Extensions under `input/definitions/` and `input/` directories
- FSH files may be present for newer definitions

**Incubator repos:**
- Typically IG structure with `input/` directory
- May use FSH (`.fsh` files under `input/fsh/`)
- FHIR resources under `input/resources/` or `input/profiles/`

## Workflow

When the user provides a Jira ticket key (e.g., `FHIR-55197`), execute the
following steps. Run independent CLI calls in parallel where possible.

### Step 1: Gather Ticket Details and Resolution

Run these commands in parallel:

**1a. Get the ticket with full content, comments, and snapshot:**

```bash
fhir-augury --json '{"command":"get","source":"jira","id":"FHIR-55197","includeComments":true,"includeContent":true,"includeSnapshot":true}'
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
fhir-augury --json '{"command":"cross-referenced","value":"FHIR-55197","limit":50}'
```

From the cross-references response, categorize:
- **GitHub references**: PRs, issues, or commits that reference this ticket
- **Jira references**: related tickets that provide additional context
- **Zulip references**: chat discussions about this ticket

**1c. Get keywords for the ticket:**

```bash
fhir-augury --json '{"command":"keywords","source":"jira","id":"FHIR-55197","limit":30}'
```

These keywords identify the FHIR resources, elements, and operations involved.

### Step 2: Determine Affected Repositories

Using the data gathered in Step 1, identify which repositories are affected:

**2a. Map the specification to repositories.**

Use the `specification` metadata field from the ticket:

| Specification Pattern | Primary Repository | Secondary |
|-----------------------|-------------------|-----------|
| "FHIR Core (FHIR)" | HL7/fhir | HL7/fhir-extensions |
| Contains "UTG" or "Terminology" | HL7/UTG | — |
| Contains "Extensions" | HL7/fhir-extensions | — |
| Specific IG name | Look for matching incubator/IG repo | — |

**2b. Identify repositories from cross-references.**

GitHub cross-references will contain repository names in their IDs (format
`owner/repo#N` for issues/PRs). Extract the unique `owner/repo` values.

**2c. Identify resources from keywords.**

The keywords response contains `fhir_path` entries (e.g., `Patient.identifier`,
`Observation.value`) and `word` entries. Use the resource name (first segment
of a FHIR path) to find which repository directory contains that resource.

**2d. Search the repository clones directly.**

For each identified resource or artifact, search the cloned repositories to
find the actual source files. Use shell commands against the cache:

```bash
# Find files related to a specific resource in FhirCore
find cache/github/repos/HL7_fhir/clone/source -type d -iname "<resource-name>"

# Find StructureDefinition files for a resource
find cache/github/repos/HL7_fhir/clone/source -name "structuredefinition-<resource>.xml"

# Find references in UTG
grep -rl "<artifact-name>" cache/github/repos/HL7_UTG/clone/input/ --include="*.xml"

# Find extension definitions
find cache/github/repos/HL7_fhir-extensions/clone/input -name "*<extension-name>*"

# Search across all repos for a term
grep -rl "<search-term>" cache/github/repos/*/clone/ --include="*.xml" --include="*.fsh" | head -20
```

On Windows, use PowerShell equivalents:
```powershell
# Find directories matching a resource name
Get-ChildItem -Path cache\github\repos\HL7_fhir\clone\source -Directory -Recurse -Filter "<resource-name>"

# Search for a term across repos
Get-ChildItem -Path cache\github\repos\*\clone -Recurse -Include "*.xml","*.fsh" | Select-String -Pattern "<term>" -List | Select-Object Path
```

### Step 3: Analyze Impact

For each affected repository, assess the scope of change:

**3a. Examine existing definitions.**

For each affected FHIR resource or artifact, read the current source file to
understand the existing state:

```bash
# Get file content from the GitHub source service
fhir-augury --json '{"command":"get","source":"github","id":"HL7/fhir:source/patient/structuredefinition-Patient.xml","includeContent":true}'
```

Or read directly from the cache clone:
```powershell
Get-Content cache\github\repos\HL7_fhir\clone\source\<resource>\<file>.xml | Select-Object -First 50
```

**3b. Check for related PRs and commits.**

From the cross-references, identify any existing PRs or commits that have
already started implementing this change. Note whether they are open, merged,
or closed.

**3c. Look for related issues in the same area.**

Search for other tickets affecting the same resources:

```bash
fhir-augury --json '{"command":"search","query":"<resource-name>","sources":["jira"],"limit":10}'
```

**3d. Assess terminology impact.**

If the change involves coded elements, check for ValueSet or CodeSystem
changes needed in the UTG repository:

```bash
fhir-augury --json '{"command":"search","query":"<valueset-name>","sources":["github"],"limit":10}'
```

### Step 4: Build the Report

Compose a markdown report with the sections described below. Use the gathered
data to write substantive, specific content — not generic placeholders.

---

## Report Format

The report MUST follow this structure. Every section is required, though
sections may note "None identified" if no data exists.

```markdown
# Implementation Plan: {TICKET-KEY}

**Title:** {ticket title}
**Status:** {status}
**Resolution:** {resolution}
**Work Group:** {work group}
**Specification:** {specification}
**Resolved:** {resolved date}

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

## Impact Analysis

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
that a developer can execute it without ambiguity.}

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
during implementation. If none: "No open questions."}
```

## Important Rules

- **Use only data from the CLI and cached repositories.** Do not fabricate
  ticket details, file paths, or resolution content. If a CLI call fails or
  returns no data, say so in the report.
- **Be specific in the proposal.** Generic statements like "modify the
  resource" are not useful. Name the exact element, path, type, cardinality,
  and binding.
- **Include actual file paths.** When referencing repository files, use the
  real paths found in the cache clones. Verify files exist before listing them.
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
- **Distinguish between resolution types.** Only "Applied", "Persuasive", and
  "Persuasive with Modification" resolutions require implementation. If the
  resolution is "Not Persuasive", "Duplicate", or "Withdrawn", note this in
  the report and explain that no implementation is needed.
- **Search the repo clones to find real files.** Don't guess at file paths.
  Use PowerShell or bash to search the cache directory and confirm which files
  exist and contain relevant content.
