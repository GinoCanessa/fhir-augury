---
name: ticket-prep
description: "Prepares FHIR Jira tickets for workgroup review. USE FOR: ticket review prep, disposition proposals, ticket summary, workgroup ballot prep. Requires a Jira ticket key (e.g., FHIR-50738). Gathers ticket details, cross-references, Zulip conversations, and GitHub items via the `fhir-augury-cli` skill (CLI first, MCP/HTTP/appsettings as documented fallbacks), then produces a structured report with summary, keywords, related discussions, and three proposed dispositions with a recommendation."
---

# Ticket Prep Skill

Prepares a structured report for a FHIR Jira ticket to support workgroup
review and disposition. The report is built by querying for ticket details,
cross-references, related conversations, and linked artifacts.

## Data Access

All data access in this skill (Jira, Zulip, GitHub, cross-references,
search) goes through the **`fhir-augury-cli`** skill. That skill
documents the CLI invocation form, the canonical recipes
(`get`, `cross-referenced`, `search`, `query-zulip`, …), and the
fallback chain (CLI → MCP → direct HTTP → `appsettings.json`). Do not
duplicate command-line knowledge here.

When a CLI command is shown below, it is in the form documented by
`fhir-augury-cli`:

```bash
fhir-augury-cli --json '<json>' [--pretty]
```

If the CLI is unavailable in the current environment, fall back per the
order documented in `fhir-augury-cli` (MCP → direct HTTP → `appsettings.json`).

## Inputs

- **Ticket key** *(required)* — e.g., `FHIR-50738`.
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

## Workflow

When the user provides a Jira ticket key (e.g., `FHIR-50738`), execute the
following steps. Run independent calls in parallel where possible.

### Step 1: Gather the Ticket and Cross-References

Run these two calls in parallel:

**1a. Get the ticket with full content, comments, and snapshot:**

```bash
fhir-augury-cli --json '{"command":"get","source":"jira","id":"FHIR-50738","includeComments":true,"includeContent":true,"includeSnapshot":true}'
```

**1b. Get all cross-references:**

```bash
fhir-augury-cli --json '{"command":"cross-referenced","value":"FHIR-50738","limit":50}'
```

From the cross-references response, identify:
- **Jira references**: items where the source type is `jira` — these are
  related tickets.
- **Zulip references**: items where the source type is `zulip` — these are
  related chat threads.
- **GitHub references**: items where the source type is `github` — these are
  related PRs, issues, or commits.

### Step 2: Fetch Related Jira Tickets

For each Jira ticket found in the cross-references, fetch its details:

```bash
fhir-augury-cli --json '{"command":"get","source":"jira","id":"FHIR-XXXXX","includeContent":true,"includeSnapshot":true}'
```

These provide context for understanding the full scope of the request.

### Step 3: Fetch Zulip Conversations

For each Zulip cross-reference, retrieve the thread by item ID:

```bash
fhir-augury-cli --json '{"command":"get","source":"zulip","id":"<zulip-item-id>","includeContent":true}'
```

If the cross-references don't surface enough Zulip context, also search
for the ticket key in Zulip:

```bash
fhir-augury-cli --json '{"command":"search","query":"FHIR-50738","sources":["zulip"],"limit":10}'
```

### Step 4: Note GitHub Items

For each GitHub cross-reference, record the item type (PR, issue, commit),
repository, title, and URL. No deep fetch is required unless the user asks.

### Step 5: Load Repo Briefings (required when GitHub cross-refs exist)

For **every** distinct `owner/name` repository surfaced by the GitHub
cross-references in Step 1b, read the persisted briefing produced by the
`repo-analysis` skill:

- Briefing: `cache/github/repos/<owner>_<name>/repo-analysis/briefing.md`
- Metadata: `cache/github/repos/<owner>_<name>/repo-analysis/meta.json`

This skill is **data-only** with respect to repo-analysis: it reads the
cached artifacts but does **not** invoke the `repo-analysis` skill
itself. Check `meta.json` against the staleness rules documented in the
`repo-analysis` skill (clone HEAD + playbook SHA must both match).

If a briefing is **missing** or **stale** for any required repo, **stop
and ask the user** to run the `repo-analysis` skill before resuming —
e.g.:

> Briefing for `HL7/US-Core` is stale (clone HEAD changed since last
> analysis). Please run `repo-analysis HL7/US-Core if-stale` and let me
> know when it's ready.

Do not proceed with partial repo context, and do not fabricate repo
facts to fill the gap.

From each briefing, extract for use in later steps:

- **Category** (e.g., `FhirCore`, `Ig`, `Utg`).
- **Authoring root(s)** and **generated areas (do not edit)** — drives
  which paths a disposition would touch.
- **Recommended Change Recipes** that match the ticket's keywords /
  cross-referenced artifacts.
- **Warnings / Gotchas** relevant to the proposed change.
- **Cross-Repo Touch Points** that imply the change spans multiple
  repos.

If there are **no GitHub cross-references**, this step is a no-op —
record "No related GitHub repositories." in the report and skip the
briefing loads.

### Step 6: Build the Report

Compose a markdown report with the sections described below. Use the gathered
data to write substantive, specific content — not generic placeholders.

---

## Report Format

The report MUST follow this structure EXACTLY. Every section is required, though
sections may note "None found" if no data exists.

```markdown
# Ticket Review: {TICKET-KEY}

| | |
|-|-|
| Ticket | ({TICKET-KEY}([{link to jira ticket}]) : {type} |
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

---

## Summary

{A clear, concise summary of what the ticket is requesting. Written in
the third person. If there are cross-referenced Jira tickets, incorporate
their context here to give a complete picture of the request. Mention each
cross-referenced ticket by key and explain how it relates.}

## Details

**Description:**
{The full description content of the ticket, including any formatting.}

**Comments:**
{For each comment, include:}

## Keywords

{A comma-separated list of keywords and key phrases that capture the
essential concepts, resources, and FHIR elements involved in this ticket.
Include FHIR resource names, element paths, operation names, terminology,
and domain terms as appropriate.}

## Linked Jira Tickets

{For each explicitly-linked Jira ticket, include:}

### ({TICKET-KEY}: {ticket title})[{URL to the ticket}]

{A 2-4 sentence summary of the ticket's content and how it relates to the main ticket.}

{A comma-separated list of users involved in the ticket - reporter, assignee, watchers, commenters, etc..}

## Related Jira Tickets

{For each cross-referenced Jira ticket, include:}

### ({TICKET-KEY}: {ticket title})[{URL to the ticket}]

{A 2-4 sentence summary of the ticket's content and how it relates to the main ticket.}

{A comma-separated list of users involved in the ticket - reporter, assignee, watchers, commenters, etc..}

## Related Zulip Discussions

{For each related Zulip thread, provide:}

### ({Stream} > {Topic})[{URL to the Zulip thread}]

{A 2-4 sentence summary of the conversation: what was discussed, what
positions were taken, whether consensus was reached, and any relevant
conclusions or open questions.}

{A comma-separated list of the participants in the discussion, if available.}

{If no Zulip discussions found: "No related Zulip discussions were found."}

## Related GitHub Items

{A bullet list of related GitHub items:}

- [{type}: {title}]({url}) — {one-line description of relevance}

{If no GitHub items found: "No related GitHub items were found."}

## Repo Context

{For each distinct repository surfaced in "Related GitHub Items", include
a subsection sourced from that repo's `repo-analysis/briefing.md`. If
there are no related GitHub repos, write
"No related GitHub repositories." and omit the subsections.}

### {owner/name} ({category})

- **Briefing:** `cache/github/repos/<owner>_<name>/repo-analysis/briefing.md` @ clone `{short-sha}`
- **Authoring root(s):** {from briefing}
- **Likely-touched paths for this ticket:** {paths from briefing's
  Ticket-Relevant Paths if present, else inferred from Authoring root(s)
  + the ticket's keywords / linked artifacts}
- **Applicable change recipes:** {names of recipes from the briefing's
  "Recommended Change Recipes" that match this ticket}
- **Gotchas to weigh in dispositions:** {from briefing's "Warnings /
  Gotchas", filtered to what's relevant}
- **Cross-repo touch points:** {from briefing, only entries relevant to
  this ticket}

## Proposed Dispositions

{The dispositions below MUST reflect the Repo Context above. When a
disposition implies a code/spec change, name the specific repo,
authoring root, and (when known) file path drawn from the briefing(s).
When a briefing surfaces a gotcha that affects feasibility, address it
explicitly in the relevant disposition's Justification.}

### Disposition A: Accept As Requested

#### Proposal

{The specific action to take that fulfills exactly what the ticket asks for.
Write this as a concrete change proposal: what would change in the spec, what
resource/element/constraint would be added/modified/removed. This should be
detailed enough to act on.}

#### Justification

{Why this is a reasonable approach. Reference specific FHIR design principles,
consistency with existing patterns, community feedback from Zulip, or
standards requirements.}

---

### Disposition B: Alternative Approach

#### Proposal

{An alternative way to address the underlying need of the ticket that differs
from what was literally requested. This might use a different mechanism (e.g.,
extension vs. core element, different resource, different cardinality, profile
instead of base spec change). Be specific about what the alternative is.}

#### Justification

{Why this alternative might be preferable. Address trade-offs vs. Disposition
A. Reference design principles, backward compatibility, implementation
burden, or scope.}

---

### Disposition C: Decline

#### Proposal

{A clear statement that the request should not be adopted, with a specific
rationale category (e.g., out of scope, insufficient use cases, already
addressed by existing mechanism, breaking change not justified).}

#### Justification

{Why declining is defensible. Reference the specific reason the request
should not be adopted. If there is an existing mechanism that already
addresses the need, cite it. If the change would cause harm (breaking
changes, complexity), explain how.}

---

### Recommendation

**Recommended disposition:** {A, B, or C}

{A paragraph explaining why this disposition is recommended over the others.
Weigh the trade-offs, reference the community discussion if relevant, and
consider the impact on implementers. Be direct and opinionated — the
workgroup wants a clear recommendation to start the discussion.}
```

## Important Rules

- **Use only data from the `fhir-augury-cli` skill (CLI / MCP).** Do not
  fabricate ticket details, Zulip conversation content, or GitHub
  links. If a call fails or returns no data, say so in the report.
- **Use only persisted repo-analysis briefings for repo facts.** This
  skill does not invoke `repo-analysis` and does not probe clones
  directly. If a required briefing is missing or stale, stop and ask
  the user to run `repo-analysis`. Do not infer repo structure from
  memory.
- **Dispositions must be grounded in the Repo Context.** Name the
  specific repo and authoring root(s) when proposing a change. If the
  briefing flags a gotcha that affects the proposal, the Justification
  must address it.
- **Be specific in dispositions.** Generic statements like "modify the spec"
  are not useful. Name the resource, element, constraint, or mechanism.
- **Summarize Zulip threads honestly.** Capture the range of opinions, not
  just the majority view. Note disagreements.
- **The recommendation must pick one.** Don't hedge with "it depends" — the
  workgroup can override the recommendation, but they need a starting point.
- **Include ticket keys as links.** When referencing Jira tickets, format them
  as links the reader can use to get to Jira directly.
- **Keep the summary self-contained.** A reviewer should be able to understand
  the request from the Summary section alone, without reading the original
  ticket.
