# Category Playbook: JiraSpecArtifacts

## Purpose
Currently a single repo: `HL7/JIRA-Spec-Artifacts`. Hosts the canonical
artifact registry that backs the FHIR Jira tracker — every spec/IG known
to Jira has a manifest here that maps a Jira "Specification" value to the
artifacts that can be referenced on a ticket.

## Typical layout
Top-level directories are named after the spec/IG they describe (e.g.,
`FHIR-core`, `US-Core`, `IPS`, …). Within each spec directory the build
expects:
- A spec-level metadata file describing the specification (canonical,
  versions, ownership).
- One artifact descriptor per resource / artifact tracked by Jira
  (StructureDefinition, ValueSet, CodeSystem, page, …) — typically
  `*.xml`/`*.json` documents using the project's own schema.
- Index / manifest files that the FHIR Jira UI loads to populate the
  "Related Artifacts" / "Specification" / "Page" pickers on a ticket.

The exact filenames and folder conventions vary by spec; treat the live
clone as authoritative and confirm via `README.md` and any top-level
`schemas/` or `tools/` directory.

## Build / publish system
Custom — *not* sushi or IG-publisher. The repo is consumed directly by
the HL7 Jira instance to populate ticket fields and validate
spec/artifact references at submission time. No human-facing IG site is
generated.

## Artifact map
| Artifact | Lives at |
|----------|----------|
| Spec metadata | `<spec>/spec.xml` (or equivalent — varies per spec) |
| Tracked artifacts | `<spec>/artifacts/...` or per-artifact files in the spec dir |
| Page lists | `<spec>/pages/...` |
| Schemas | `schemas/` (for the spec/artifact descriptor formats) |
| Tooling | `tools/` |

Always inspect the clone to confirm the exact paths for a given spec —
this repo is older than the sushi convention and does not follow IG
norms.

## Cross-repo touch points
- **All other categories** — every FHIR-tracked spec that appears in the
  Jira "Specification" picker has a corresponding entry here. Adding a
  new spec to Jira typically requires a new directory in this repo.
- **FHIR Jira itself** — the Jira instance loads this content to drive
  ticket fields. Changes propagate through Jira's data refresh, not
  through an IG publish.
- **Ticket validation** — the cross-references this repo describes are
  what FhirAugury surfaces as "Related Artifacts" and "Related Pages"
  on a ticket.

## Common change recipes
- **Add a new spec / IG to Jira:** create a new top-level directory
  matching the existing convention for that family (FHIR-core IGs vs.
  HL7 product IGs vs. external IGs may use slightly different
  templates). Populate spec metadata and a starter artifact list.
- **Register a new artifact under an existing spec:** add the artifact
  descriptor in the spec's directory matching the existing pattern.
- **Update artifact metadata:** edit the descriptor in place; the change
  appears in Jira on the next refresh cycle.
- **Retire an artifact:** mark it deprecated in the descriptor rather
  than deleting — historical Jira tickets reference these by ID.

## Pitfalls / gotchas
- This is not a sushi / IG-publisher project; do not look for
  `input/`, `sushi-config.yaml`, `ig.ini`, `fsh-generated/`, or
  `output/`.
- File / directory conventions vary across specs. Pattern-match against
  a sibling spec in the same family (HL7 product IG vs. FHIR core IG vs.
  external) before adding new content.
- Renaming or removing an artifact descriptor breaks references on
  historical Jira tickets. Prefer deprecation over deletion.
- Changes here only show up in Jira after the next data refresh; the
  feedback loop is asynchronous compared to a normal IG build.
- Schema validation lives under `schemas/`; new descriptor types need a
  matching schema update.
