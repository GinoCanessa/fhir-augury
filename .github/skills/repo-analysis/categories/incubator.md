# Category Playbook: Incubator

## Purpose
HL7 work-group incubator IGs — currently `HL7/admin-incubator`,
`HL7/oo-incubator`, `HL7/cg-incubator`, `HL7/immunization-incubator`,
`HL7/ebm-incubator`, `HL7/api-incubator-ig`, `HL7/capstmt`, and
`HL7/fhir-testing-ig`. These are workspaces where work groups prototype
artifacts before promoting them into a published IG, into the FHIR core
spec, or into the Extensions Pack. Structurally they are IGs (sushi +
IG-publisher), so most guidance from the `Ig` playbook applies — this
file documents only the deltas.

## Typical layout
Same as `Ig` (`input/fsh/`, `input/resources/`, `input/pagecontent/`,
`sushi-config.yaml`, `ig.ini`, `fsh-generated/`, `output/`). Incubators
typically have:
- A noticeably smaller `input/fsh/` tree.
- Looser file organization — work-group conventions vary, and content
  may be added quickly without long-term structural planning.
- Frequent draft / experimental status markers in `sushi-config.yaml`
  (`status: draft`, `experimental: true`).
- Liberal use of placeholder narrative.

## Build / publish system
Identical to `Ig`. Sushi + IG-publisher; same generated artifact paths.

## Artifact map
See `ig.md`. Incubator-specific additions:
- `input/fsh/scratch/` or `input/fsh/proposals/` — common informal
  buckets for in-flight ideas.
- `input/notes/`, `input/discussion/` — work-group meeting notes that may
  not be referenced from `pages`.

## Cross-repo touch points
- **FhirCore (`HL7/fhir`)** — incubator artifacts often graduate into
  core; when a ticket says "promote", the change spans the incubator and
  FhirCore.
- **Extensions Pack (`HL7/fhir-extensions`)** — extensions prototyped in
  an incubator commonly migrate here.
- **UTG (`HL7/UTG`)** — terminology authored in an incubator that needs
  governance moves into UTG.
- **Other incubators** — work groups occasionally split or merge
  incubator workspaces; cross-incubator references are uncommon but
  possible.

## Common change recipes
- **Prototype a new profile / extension:** add to
  `input/fsh/<topic>/<Id>.fsh`. Mark `* ^status = #draft` and
  `* ^experimental = true` to signal incubator status.
- **Promote an artifact out of an incubator:** copy the FSH (or XML) to
  the destination repo (FhirCore, Extensions Pack, UTG, or a published
  IG), update canonical URL to match the destination, and remove the
  source from the incubator. Coordinate the cutover so the canonical URL
  is unique across the FHIR ecosystem.
- **Restructure work-group content:** common during major updates;
  prefer moving rather than duplicating artifacts.

## Pitfalls / gotchas
- **Rapid churn.** Incubator content moves between repos frequently; a
  briefing more than a few weeks old may misstate where a given
  artifact actually lives now. Always re-confirm via the live clone.
- **Draft status is the norm.** Treat artifact `status` and
  `experimental` flags as informational; many in-flight artifacts are
  intentionally not normative.
- **Sparse narrative.** Incubators frequently lack the polished
  `input/pagecontent/` content of a published IG; do not let that
  surprise the reader.
- **Promotion deletions.** When an artifact is promoted out, the
  incubator copy should be removed in the same change set to avoid
  duplicate canonicals — a frequent source of build failures.
- Otherwise, every gotcha from the `Ig` playbook still applies.
