---
name: ticket-prep
description: "Prepares FHIR Jira tickets for workgroup review. USE FOR: ticket review prep, disposition proposals, ticket summary, workgroup ballot prep. Requires a Jira ticket key (e.g., FHIR-50738). Gathers ticket details, cross-references, Zulip conversations, and GitHub items via the fhir-augury CLI, then produces a structured report with summary, keywords, related discussions, and three proposed dispositions with a recommendation."
---

# Ticket Prep Skill

Prepares a structured report for a FHIR Jira ticket to support workgroup
review and disposition. The report is built by querying the `fhir-augury` CLI
for ticket details, cross-references, related conversations, and linked
artifacts.

## Prerequisites

- The `fhir-augury` CLI must be available (installed as a dotnet tool or via
  local alias).
- The FHIR Augury services must be running and accessible (the CLI connects to
  the orchestrator, default `http://localhost:5150`).

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
| `query-zulip` | Structured Zulip message search |

## Workflow

When the user provides a Jira ticket key (e.g., `FHIR-50738`), execute the
following steps. Run independent CLI calls in parallel where possible.

### Step 1: Gather the Ticket and Cross-References

Run these two commands in parallel:

**1a. Get the ticket with full content, comments, and snapshot:**

```bash
fhir-augury --json '{"command":"get","source":"jira","id":"FHIR-50738","includeComments":true,"includeContent":true,"includeSnapshot":true}'
```

**1b. Get all cross-references:**

```bash
fhir-augury --json '{"command":"cross-referenced","value":"FHIR-50738","limit":50}'
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
fhir-augury --json '{"command":"get","source":"jira","id":"FHIR-XXXXX","includeContent":true,"includeSnapshot":true}'
```

These provide context for understanding the full scope of the request.

### Step 3: Fetch Zulip Conversations

For each Zulip cross-reference, retrieve the thread. The cross-reference will
contain stream and topic information. Fetch the full thread:

```bash
fhir-augury --json '{"command":"get","source":"zulip","id":"<zulip-item-id>","includeContent":true}'
```

If the cross-references don't surface enough Zulip context, also search for
the ticket key in Zulip:

```bash
fhir-augury --json '{"command":"search","query":"FHIR-50738","sources":["zulip"],"limit":10}'
```

### Step 4: Note GitHub Items

For each GitHub cross-reference, record the item type (PR, issue, commit),
repository, title, and URL. No deep fetch is required unless the user asks.

### Step 5: Build the Report

Compose a markdown report with the sections described below. Use the gathered
data to write substantive, specific content — not generic placeholders.

---

## Report Format

The report MUST follow this structure. Every section is required, though
sections may note "None found" if no data exists.

```markdown
# Ticket Review: {TICKET-KEY}

**Title:** {ticket title}
**Status:** {status}
**Priority:** {priority}
**Type:** {type}
**Work Group:** {work group}
**Specification:** {specification}
**Reporter:** {reporter}
**Assignee:** {assignee}
**Created:** {date}
**Updated:** {date}
**Labels:** {comma-separated labels}

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

## Related Zulip Discussions

{For each related Zulip thread, provide:}

### {Stream} > {Topic}

**Link:** {URL to the Zulip thread}

{A 2-4 sentence summary of the conversation: what was discussed, what
positions were taken, whether consensus was reached, and any relevant
conclusions or open questions.}

{If no Zulip discussions found: "No related Zulip discussions were found."}

## Related GitHub Items

{A bullet list of related GitHub items:}

- [{type}: {title}]({url}) — {one-line description of relevance}

{If no GitHub items found: "No related GitHub items were found."}

## Proposed Dispositions

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

- **Use only data from the CLI.** Do not fabricate ticket details, Zulip
  conversation content, or GitHub links. If a CLI call fails or returns no
  data, say so in the report.
- **Be specific in dispositions.** Generic statements like "modify the spec"
  are not useful. Name the resource, element, constraint, or mechanism.
- **Summarize Zulip threads honestly.** Capture the range of opinions, not
  just the majority view. Note disagreements.
- **The recommendation must pick one.** Don't hedge with "it depends" — the
  workgroup can override the recommendation, but they need a starting point.
- **Include ticket keys as links.** When referencing Jira tickets, format them
  as identifiers the reader can look up.
- **Keep the summary self-contained.** A reviewer should be able to understand
  the request from the Summary section alone, without reading the original
  ticket.
