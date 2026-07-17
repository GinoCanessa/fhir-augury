# Category Playbook: Ig

## Purpose
The general "FHIR Implementation Guide" category. Covers the bulk of
configured repos — HL7 IGs (US Core, Da Vinci suite, CQF, mCODE,
SDC, …), non-HL7 IGs (`cds-hooks/docs`, `openehr-fhir/base-spec`,
`hl7-eu/laboratory`, the `WorldHealthOrganization/smart-*` family), and
FHIR-WG IGs (`FHIR/ig-guidance`, `FHIR/us-quality-core`). All are sushi /
IG-publisher projects; differences across IGs are stylistic, not
structural.

## Typical layout
- `input/` — IG authoring root.
  - `input/fsh/` — FSH sources (profiles, extensions, value sets, code
    systems, instances, invariants). Often subdivided by topic.
  - `input/resources/` — hand-authored XML/JSON resources (rare in
    pure-FSH IGs; common in older IGs).
  - `input/profiles/`, `input/extensions/`, `input/vocabulary/` — older
    folder convention for hand-authored XML/JSON.
  - `input/examples/` — example instances (XML/JSON or FSH).
  - `input/pagecontent/` — Markdown narrative pages keyed by `pages`
    in `sushi-config.yaml`.
  - `input/images/` — diagrams referenced from narrative.
  - `input/data/`, `input/includes/`, `input/intro-notes/` — IG-specific
    extras.
- `sushi-config.yaml` — IG identity (`canonical`, `id`, `version`),
  dependencies, parameters, `pages`, `menu`.
- `ig.ini` — IG-publisher entrypoint (name + spec version).
- `input-cache/` — IG-publisher download cache; not authoritative.
- `fsh-generated/` — sushi output. **Do not edit.**
- `output/` — IG-publisher render. **Do not edit.**
- `temp/`, `template/` — generated/derived. Treat as build artifacts.

## Build / publish system
Sushi compiles FSH → JSON under `fsh-generated/resources/`; the
IG-publisher merges those with hand-authored content in `input/` and
renders the site under `output/`. Most IGs document a `_genonce.sh` /
`_updatePublisher.sh` (and `.bat` equivalents) at the repo root.

## Artifact map
| Artifact | Lives at |
|----------|----------|
| Profiles (FSH) | `input/fsh/profiles/` or `input/fsh/<topic>/*.fsh` |
| Profiles (XML/JSON) | `input/resources/StructureDefinition-*.xml`/`json` or `input/profiles/` |
| Extensions (FSH) | `input/fsh/extensions/` or topic dirs |
| Extensions (XML/JSON) | `input/resources/StructureDefinition-*.xml` or `input/extensions/` |
| ValueSets / CodeSystems (FSH) | `input/fsh/vocabulary/` or `input/fsh/terminology/` |
| ValueSets / CodeSystems (XML/JSON) | `input/resources/` or `input/vocabulary/` |
| Examples | `input/examples/` or `input/fsh/examples/` |
| Narrative pages | `input/pagecontent/*.md` |
| Search parameters | `input/resources/SearchParameter-*.xml`/`json` (or FSH `Instance: ... SearchParameter`) |
| Capability statements | `input/resources/CapabilityStatement-*.xml`/`json` or FSH `Instance: ...` |
| Build configuration | `sushi-config.yaml`, `ig.ini` |

The exact folder layout under `input/` varies per IG — confirm via the
clone listing rather than assuming.

## Cross-repo touch points
- **FhirCore (`HL7/fhir`):** every IG profiles base FHIR resources;
  breaking changes in FhirCore can break IG validation.
- **UTG (`HL7/UTG`):** ValueSet bindings often reference UTG canonicals.
- **Other IGs:** `sushi-config.yaml` `dependencies` may pull in another
  IG (e.g., US Core depends on the Extensions Pack and core; Da Vinci
  IGs depend on US Core).
- **Non-HL7 IGs** (e.g., `cds-hooks/docs`, `openehr-fhir/base-spec`,
  `WorldHealthOrganization/smart-*`) follow the same sushi conventions
  but may have additional repo-local docs / governance files outside
  `input/`. Treat their `README.md` and any `docs/` directory as required
  context.

## Common change recipes
- **Add a profile (FSH):** add `Profile: <Id>` in
  `input/fsh/profiles/<Id>.fsh`. Define `Parent`, `Id`, `Title`,
  `Description`, then `* element` constraints. Add an example via
  `Instance: <Id>Example`.
- **Add an extension (FSH):** add `Extension: <Id>` in
  `input/fsh/extensions/<Id>.fsh` with `Context`, `Id`, `Title`,
  `Description`, value cardinality and type.
- **Add a ValueSet / CodeSystem (FSH):** add `ValueSet:` /
  `CodeSystem:` in `input/fsh/vocabulary/`. Reference from binding
  rules in profiles.
- **Add an example:** prefer FSH `Instance: ... InstanceOf: ...` under
  `input/fsh/examples/` if the IG uses FSH for examples; otherwise add
  the XML/JSON under `input/examples/`.
- **Add narrative:** create / edit `input/pagecontent/<slug>.md` and
  add `<slug>` under `pages` in `sushi-config.yaml`.
- **Update IG identity / dependencies:** edit `sushi-config.yaml`
  (`version`, `dependencies.<id>: <version>`).

## Pitfalls / gotchas
- **Never edit `fsh-generated/` or `output/`.** They are rebuilt and
  changes will be lost.
- Each IG chooses its own subdivision of `input/fsh/` (by topic, by
  artifact type, or flat). Inspect the actual directory tree before
  filing tasks.
- Some older IGs author hand-rolled XML/JSON under `input/resources/`
  *instead of* FSH. Mixing the two for the same artifact ID causes
  duplicate-canonical errors.
- `sushi-config.yaml` `dependencies` declarations are version-pinned;
  changing a dependency is non-trivial because downstream profiles may
  break.
- For non-HL7 IGs (CDS Hooks, openEHR-FHIR, WHO SMART, EU labs), repo
  governance and PR conventions may differ from HL7's; check the repo's
  `README.md` / `CONTRIBUTING.md` before assuming HL7 norms.
- ValueSet binding strength changes are high-impact and may need
  cross-IG coordination.
