---
name: orchestrate-plan
description: "Orchestrates batch implementation planning of FHIR Jira tickets from a worklist. USE FOR: batch ticket planning, bulk implementation plans, worklist processing, orchestrated planning. Reads a markdown worklist of tickets grouped by work group, launches concurrent ticket-plan agents (up to a configurable limit), saves reports to a target directory organized by work group, and marks tickets complete in the worklist as they finish."
---

# Orchestrate Plan Skill

Processes a markdown worklist of FHIR Jira tickets by launching concurrent
`ticket-plan` agents, saving each implementation plan report to a structured
output directory, and tracking progress in the worklist file.

## Prerequisites

- The `ticket-plan` skill SKILL.md must exist at
  `.github/skills/ticket-plan/SKILL.md` (it defines the per-ticket workflow).
- The `fhir-augury` CLI must be available.
- The FHIR Augury services must be running and accessible.
- The GitHub source service cache must be populated
  (`cache/github/repos/`).

## Inputs

The user must provide (or you must confirm via ask_user):

| Input | Description | Example |
|-------|-------------|---------|
| **Worklist file** | Path to a markdown file containing tickets grouped by work group, using `- [ ]` / `- [x]` checkboxes | `scratch/_ticket-plan.md` |
| **Output directory** | Base directory where reports are saved, organized by work group subfolder | `C:/ai/git/fhir-augury-content/planned/` |
| **Concurrency** | Maximum number of concurrent planning agents (default: 2) | `2` |

## Worklist Format

The worklist markdown file must follow this structure:

```markdown
# {Title}

{Optional description}

## {Work Group Name} ({count})

- [ ] FHIR-XXXXX - {ticket title}
- [ ] FHIR-YYYYY - {ticket title}
- [x] FHIR-ZZZZZ - {already completed ticket title}

## {Another Work Group} ({count})

- [ ] FHIR-AAAAA - {ticket title}
```

Rules:
- Work group headings are `## {Name} ({count})`.
- Each ticket is a checkbox line: `- [ ] FHIR-NNNNN - {title}`.
- Completed tickets are marked `- [x] FHIR-NNNNN - {title}`.
- Only unchecked (`- [ ]`) tickets are processed.

## Work Group Folder Name Mapping

Work group names from the worklist headings are converted to PascalCase folder
names by removing special characters and joining words. The `&` character is
replaced with `And`. Examples:

| Work Group Name | Folder Name |
|----------------|-------------|
| Biomedical Research & Regulation | BiomedicalResearchAndRegulation |
| Orders & Observations | OrdersAndObservations |
| Clinical Decision Support | ClinicalDecisionSupport |
| FHIR Infrastructure | FHIRInfrastructure |
| Financial Mgmt | FinancialMgmt |
| Public Health | PublicHealth |
| Patient Administration | PatientAdministration |
| Clinical Quality Information | ClinicalQualityInformation |
| Cross-Group Projects | CrossGroupProjects |
| Patient Care | PatientCare |
| Electronic Health Record | ElectronicHealthRecord |
| Terminology Infrastructure | TerminologyInfrastructure |
| Community-Based Care and Privacy | CommunityBasedCareAndPrivacy |
| Structured Documents | StructuredDocuments |
| Patient Empowerment | PatientEmpowerment |
| Payer/Provider Information Exchange | PayerProviderInformationExchange |
| Implementable Technology Specifications | ImplementableTechnologySpecifications |
| Infrastructure & Messaging | InfrastructureAndMessaging |
| HL7 Australia FHIR | HL7AustraliaFHIR |
| Imaging Integration | ImagingIntegration |
| FHIR Mgmt Group | FHIRMgmtGroup |
| Clinical Genomics | ClinicalGenomics |
| US Realm Task Force [deprecated] | USRealmTaskForce |
| CDA Management Group | CDAManagementGroup |
| HL7 Europe | HL7Europe |
| Pharmacy | Pharmacy |
| Security | Security |
| Devices | Devices |
| Conformance | Conformance |
| Clinical Interoperability Council | ClinicalInteroperabilityCouncil |

For any work group not in the table above, apply this algorithm:
1. Replace `&` with `And`
2. Remove all characters that are not alphanumeric or spaces
3. Split on spaces and join in PascalCase (capitalize first letter of each word)

## Output Structure

Reports are saved as:
```
{output_directory}/{WgFolder}/{TICKET-KEY}.md
```

Example:
```
C:/ai/git/fhir-augury-content/planned/BiomedicalResearchAndRegulation/FHIR-20788.md
C:/ai/git/fhir-augury-content/planned/OrdersAndObservations/FHIR-42345.md
```

Work group subdirectories are created automatically as needed.

## Workflow

### Step 0: Confirm Inputs

Use `ask_user` to confirm or gather:
1. The worklist file path (if not already provided)
2. The output directory (if not already provided)
3. The concurrency limit (default 2)

### Step 1: Parse the Worklist

Read the worklist file and extract all pending (unchecked) tickets with their
work group names. Count totals for progress reporting.

```powershell
# Example: parse pending tickets
$lines = Get-Content $worklistPath
$currentWg = ""
$pending = @()
foreach ($line in $lines) {
    if ($line -match '^## (.+?) \(\d+\)') {
        $currentWg = $Matches[1]
    }
    if ($line -match '- \[ \] (FHIR-\d+) - (.+)$') {
        $pending += [PSCustomObject]@{
            Key = $Matches[1]
            Title = $Matches[2]
            WgName = $currentWg
            WgFolder = ConvertTo-WgFolder $currentWg
        }
    }
}
```

Report the total pending count to the user before starting.

### Step 2: Process Tickets in Batches

Process tickets in batches of `{concurrency}` agents at a time. For each
batch:

#### 2a. Ensure output directories exist

For each ticket in the batch, create the work group subdirectory if it doesn't
exist:

```powershell
New-Item -ItemType Directory -Path "{output_directory}\{WgFolder}" -Force
```

#### 2b. Launch concurrent agents

Launch up to `{concurrency}` `general-purpose` task agents simultaneously.
Each agent receives the full `ticket-plan` skill instructions and is told to
save its report to the correct output path.

**Agent prompt template:**

```
You are planning the implementation of FHIR Jira ticket {TICKET-KEY}
("{ticket title}") for the "{work group name}" work group.

Your job is to produce a structured implementation plan report and save it to:
{output_directory}/{WgFolder}/{TICKET-KEY}.md

[... full ticket-plan skill workflow from .github/skills/ticket-plan/SKILL.md ...]
```

**IMPORTANT:** Read the full content of `.github/skills/ticket-plan/SKILL.md`
and include its complete workflow instructions (Steps 1–4, report format, and
important rules) in each agent prompt. Do not summarize or abbreviate — the
agent needs the full context since it cannot access skills.

Launch agents using:
```
task(
    agent_type="general-purpose",
    name="plan-{TICKET-KEY}",
    description="Plan ticket {TICKET-KEY}",
    mode="background",
    prompt="..."
)
```

Use the same model as the orchestrating agent (check your own model config).

#### 2c. Wait for completions

Wait for all agents in the batch to complete. As each completes:

1. **Read the result** with `read_agent` to confirm success.
2. **Verify the output file** exists:
   ```powershell
   Test-Path "{output_directory}\{WgFolder}\{TICKET-KEY}.md"
   ```
3. **Mark the ticket complete** in the worklist file by replacing `- [ ]` with
   `- [x]` for that ticket's line:
   ```
   edit(
       path="{worklist_path}",
       old_str="- [ ] {TICKET-KEY} - {title}",
       new_str="- [x] {TICKET-KEY} - {title}"
   )
   ```
4. **Report progress** to the user: "{TICKET-KEY} done — {completed}/{total}"

If an agent fails:
- Log the failure and note the ticket key
- Do NOT mark it as completed in the worklist
- Continue processing remaining tickets
- Report failures at the end

#### 2d. Launch next batch

After all agents in the current batch complete, extract the next batch of
pending tickets from the worklist (re-read the file to pick up any marks) and
repeat from Step 2a.

### Step 3: Report Summary

After all tickets are processed (or if interrupted), provide a summary:

```
## Planning Complete

- **Total processed:** {n}
- **Succeeded:** {n}
- **Failed:** {n} (list keys)
- **Remaining:** {n}

Reports saved to: {output_directory}
```

## Error Handling

- **Agent failure:** Log the error, skip the ticket, continue with remaining.
  The ticket stays unchecked in the worklist for retry in a future run.
- **CLI unavailable:** If the first agent fails due to CLI connectivity, stop
  all processing and alert the user to check services.
- **Worklist parse error:** If the worklist doesn't match the expected format,
  alert the user and stop.
- **Output directory issues:** Create directories as needed. If creation fails,
  alert the user.

## Resumability

Because completion is tracked via checkboxes in the worklist file, the skill
is **fully resumable**. If interrupted:
- Already-completed tickets remain marked `[x]`
- The next invocation picks up where the previous one left off
- No duplicate work is performed

## Example Invocation

User: "Process the ticket plan worklist in scratch/_ticket-plan.md, saving
reports to C:/ai/git/fhir-augury-content/planned/ with 2 concurrent agents."

The agent should:
1. Confirm inputs (worklist, output dir, concurrency=2)
2. Parse the worklist → find 1808 pending tickets
3. Launch 2 agents for the first 2 pending tickets
4. As each completes, verify output, mark done, report progress
5. Continue until all tickets are processed or interrupted
