---
name: orchestrate-prep
description: "Orchestrates bulk ticket preparation from a markdown worklist. USE FOR: batch ticket prep, bulk preparation, worklist processing, round-robin ticket prep across work groups. Reads a markdown file with checkbox-formatted ticket lists grouped by work group, dispatches up to N concurrent sub-agents (each running the ticket-prep workflow), saves reports to a structured output directory, and marks completed tickets in the worklist."
---

# Orchestrate Prep Skill

Bulk-processes a markdown worklist of FHIR Jira tickets, dispatching concurrent
sub-agents to prepare each ticket for workgroup review. Reports are saved to a
structured output directory and the worklist is updated as tickets complete.

## Prerequisites

- The `FhirAugury` MCP should be available (preferred). If not, the
  `fhir-augury-cli` CLI must be available as a fallback.

- The `ticket-prep` skill must be available (it defines the per-ticket workflow
  and report format).

## Inputs

The user must provide or you must determine:

1. **Worklist file** — a markdown file with tickets in checkbox format, grouped
   by work group headings. Example:
   ```markdown
   ## Orders & Observations (147)
   - [ ] FHIR-29776 - Some ticket title
   - [x] FHIR-30000 - Already completed ticket
   ```

2. **Output directory** — where prepared reports are saved, organized by
   cleaned work group name subfolder. Example:
   `C:\ai\git\fhir-augury-content\prepared\`

3. **Concurrency** — maximum number of concurrent sub-agents (default: 4).

4. **Ordering strategy** — how to select the next ticket to process. Default is
   **round-robin** across work groups (one ticket per group per round), ensuring
   every group gets coverage. Alternative is **sequential** (process all tickets
   in one group before moving to the next).

## Work Group Name Cleaning

Output subdirectories use "cleaned" work group names. The cleaning algorithm:

1. Remove all non-alphanumeric, non-space characters (e.g., `&`, `/`, `-`, `'`)
2. Split into words
3. Capitalize each word
4. Join without separators

Examples:
| Work Group Name | Cleaned Name |
|----------------|-------------|
| Orders & Observations | OrdersObservations |
| Biomedical Research & Regulation | BiomedicalResearchRegulation |
| Community-Based Care and Privacy | CommunitybasedCareAndPrivacy |
| Payer/Provider Information Exchange | PayerproviderInformationExchange |
| FHIR Infrastructure | FhirInfrastructure |
| HL7 Australia AU Core | Hl7AustraliaAuCore |

## Workflow

### Step 1: Parse the Worklist

Read the worklist markdown file and extract:
- Work group names from `## Group Name (count)` headings
- Unchecked tickets from `- [ ] FHIR-XXXXX - Title` lines
- Skip checked tickets `- [x] FHIR-XXXXX - Title` (already completed)

Build a data structure mapping each work group to its list of pending tickets.

### Step 2: Build the Processing Queue

Build the queue based on the ordering strategy:

**Round-robin (default):**
```
Round 0: first ticket from each group (up to 35 items for 35 groups)
Round 1: second ticket from each group (only groups with 2+ tickets)
Round 2: third ticket from each group (only groups with 3+ tickets)
...
```

**Sequential:**
```
All tickets from Group 1, then all from Group 2, etc.
```

### Step 3: Create Output Directories

For each work group in the worklist, create the output subdirectory using the
cleaned name:
```powershell
New-Item -ItemType Directory -Path "$OutputDir\$CleanedGroupName" -Force
```

### Step 4: Dispatch Sub-Agents

Process tickets from the queue, maintaining up to N concurrent agents at all
times. For each ticket, launch a **general-purpose background agent** with the
full ticket-prep workflow.

#### Agent Prompt Template

Each agent receives a prompt containing:

1. The ticket key and work group
2. The output file path
3. The CLI binary path
4. The complete ticket-prep workflow (Steps 1-5 from the ticket-prep skill)
5. The exact report format template

Use this template for each agent:

````
You are preparing a FHIR Jira ticket for workgroup review. Follow these steps.

## Ticket: {TICKET_KEY}
## Work Group: {WORK_GROUP}
## Output File: {OUTPUT_DIR}\{CLEAN_GROUP}\{TICKET_KEY}.md

## Data Access

Use whichever data access method is available, in this priority order:

1. **FhirAugury MCP** (preferred) — If tools prefixed with `FhirAugury-` are
   available (e.g., `FhirAugury-get_item`), use them directly for all data
   access. This is faster and avoids shell overhead.

2. **fhir-augury CLI** (fallback) — If MCP tools are not available, use the
   CLI at: {CLI_PATH}

### MCP Tool → CLI Command Mapping

| MCP Tool | CLI Command |
|----------|-------------|
| `FhirAugury-get_item` | `get` |
| `FhirAugury-cross_referenced` | `cross-referenced` |
| `FhirAugury-content_search` | `search` |
| `FhirAugury-get_zulip_thread` | `get` (source=zulip) |
| `FhirAugury-query_zulip_messages` | `query-zulip` |

## Step 1: Gather Ticket and Cross-References (run in parallel)

1a. Get ticket details:

Using MCP:
```
FhirAugury-get_item(source="jira", id="{TICKET_KEY}", includeComments=true, includeContent=true, includeSnapshot=true)
```
Using CLI (fallback):
```
& "{CLI_PATH}" --json '{{"command":"get","source":"jira","id":"{TICKET_KEY}","includeComments":true}}'
```

1b. Get all cross-references:

Using MCP:
```
FhirAugury-cross_referenced(value="{TICKET_KEY}", limit=50)
```
Using CLI (fallback):
```
& "{CLI_PATH}" --json '{{"command":"cross-referenced","value":"{TICKET_KEY}","limit":50}}'
```

## Step 2: Fetch Related Jira Tickets
For each Jira ticket found in cross-references, fetch its details using
`FhirAugury-get_item` (MCP) or the `get` CLI command.

## Step 3: Fetch Zulip Conversations
For each Zulip cross-reference, get the thread.

Using MCP:
```
FhirAugury-get_zulip_thread(stream="<stream>", topic="<topic>")
```
Also search Zulip for the ticket key:
```
FhirAugury-content_search(values="{TICKET_KEY}", sources="zulip", limit=10)
```

Using CLI (fallback):
```
& "{CLI_PATH}" --json '{{"command":"search","query":"{TICKET_KEY}","sources":["zulip"],"limit":10}}'
```

## Step 4: Note GitHub Items
Record any GitHub cross-references (type, repo, title, URL).

## Step 5: Build the Report
Compose a markdown report following this EXACT structure:

```markdown
# Ticket Review: {TICKET_KEY}

**Title:** {{ticket title}}
**Status:** {{status}}
**Priority:** {{priority}}
**Type:** {{type}}
**Work Group:** {{work group}}
**Specification:** {{specification}}
**Reporter:** {{reporter}}
**Assignee:** {{assignee}}
**Created:** {{date}}
**Updated:** {{date}}
**Labels:** {{comma-separated labels}}

---

## Summary
{{A clear, concise summary of what the ticket is requesting. Written in third
person. If there are cross-referenced Jira tickets, incorporate their context.}}

## Details

**Description:**
{{The full description content of the ticket}}

**Comments:**
{{Each comment with author/date}}

## Keywords
{{Comma-separated keywords capturing essential concepts, resources, FHIR
elements}}

## Related Zulip Discussions
{{For each thread: ### Stream > Topic, Link, 2-4 sentence summary}}
{{If none: "No related Zulip discussions were found."}}

## Related GitHub Items
{{Bullet list of items, or "No related GitHub items were found."}}

## Proposed Dispositions

### Disposition A: Accept As Requested
#### Proposal
{{Specific action to fulfill the request}}
#### Justification
{{Why this is reasonable}}

---

### Disposition B: Alternative Approach
#### Proposal
{{Alternative way to address the need}}
#### Justification
{{Why this might be preferable}}

---

### Disposition C: Decline
#### Proposal
{{Clear statement with rationale category}}
#### Justification
{{Why declining is defensible}}

---

### Recommendation
**Recommended disposition:** {{A, B, or C}}
{{Paragraph explaining why}}
```

## Important Rules
- Use ONLY data from the MCP or CLI. Do not fabricate details.
- Be specific in dispositions — name resources, elements, constraints.
- Summarize Zulip threads honestly.
- The recommendation must pick one.
- Keep the summary self-contained.

## Final Step
Save the completed report to: {OUTPUT_DIR}\{CLEAN_GROUP}\{TICKET_KEY}.md
````

### Step 6: Handle Completions

When a sub-agent completes:

1. **Read the agent result** to confirm success.
2. **Mark the ticket as completed** in the worklist file by replacing
   `- [ ] FHIR-XXXXX` with `- [x] FHIR-XXXXX` on the matching line.
   - Use `grep` to find the exact line text first (titles in the file may
     differ from what you expect).
   - Use `edit` with the exact matched line for the replacement.
3. **Launch the next ticket** from the queue to maintain concurrency at N.

### Step 7: Report Progress

After each batch of completions, report progress to the user:
- Total completed / total pending
- Number of agents currently running
- Which work groups have been covered
- Next tickets in the queue

## Error Handling

- If a sub-agent fails, log the ticket key and error, skip it, and continue
  with the next ticket in the queue. Do not retry automatically.
- If neither MCP tools nor the CLI binary are available, attempt to build the
  CLI before failing.
- If the FHIR Augury services are not responding, stop and inform the user.
- If a ticket's line cannot be found in the worklist for marking, log a
  warning but continue processing.

## Resumability

The worklist file serves as the persistent state. If the process is interrupted:
- Already-completed tickets are marked `[x]` and will be skipped on restart.
- Already-saved report files in the output directory are not re-processed.
- Simply re-invoke the skill with the same worklist to continue from where it
  left off.

To check for orphaned work (reports saved but not marked in worklist), compare
the output directory contents against the worklist:
```powershell
# Find reports that exist but aren't marked complete
Get-ChildItem -Path "$OutputDir" -Recurse -Filter "FHIR-*.md" |
  ForEach-Object { $_.BaseName } |
  ForEach-Object { Select-String -Path "$WorklistFile" -Pattern "- \[ \] $_" }
```

## Example Invocation

User says: "Prepare all tickets in scratch/_ticket-prep.md, saving reports to
C:\ai\git\fhir-augury-content\prepared\, round-robin order, 4 concurrent agents"

The orchestrator should:
1. Parse `scratch/_ticket-prep.md` → find 2729 unchecked tickets across 35 groups
2. Build round-robin queue → 2729 items
3. Create 35 output subdirectories
4. Build CLI binary
5. Launch first 4 agents (tickets 0-3 from round 0)
6. As each completes: mark done, launch next, report progress
7. Continue until queue is exhausted or user stops

## Performance Notes

- Each ticket takes approximately 60-200 seconds depending on cross-reference
  density and Zulip search results.
- With 4 concurrent agents, expect ~1-2 tickets per minute throughput.
- The orchestrator and source services handle concurrent CLI requests well.
- Sub-agents should use the same model as the orchestrating agent for
  consistency.
