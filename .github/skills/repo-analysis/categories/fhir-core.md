# Category Playbook: FhirCore

## Purpose
The base FHIR specification itself. Currently a single repo: `HL7/fhir`.
Owns the normative resource definitions, base StructureDefinitions, and the
generated specification site.

> **Scope:** for ingestion / impact analysis we care **only** about the
> `source/` directory. Ignore `tools/`, the publisher harness
> (`publish.sh`, `build.sh`, `publish/`), and other top-level support files.

- `source/<resource-name>/` — one directory per resource (e.g.,
  `source/patient/`, `source/observation/`). Authoring root.
  - `structuredefinition-<resource-name>.xml` — the resource's
    StructureDefinition. **The file's `<resource-name>` segment must match
    the directory name.** Other XML files in the directory may also begin
    with `structuredefinition-` (e.g., for profiles or supporting
    definitions); only the one whose name matches the folder is the
    canonical resource StructureDefinition. Mismatched names cause
    collisions during ingestion.
  - `bundle-<resource-name>-search-params.xml` — a FHIR Bundle containing
    the SearchParameter resources for this resource.
  - `list-<resource-name>-operations.xml` — a FHIR Bundle containing the
    OperationDefinition resources for this resource.
  - `valueset-<value-set-name>.xml`, `codesystem-<code-system-name>.xml` —
    ValueSets and CodeSystems scoped to this resource. Any
    resource/datatype directory may contain these.
  - `<resource>-spreadsheet.xml` — legacy spreadsheet authoring source for
    many resources; still authoritative for some content.
  - `<resource>-notes.xml`, `<resource>-introduction.xml`,
    `<resource>-examples.xml` — narrative, examples, scope.
  - Examples: `<resource>-example*.xml`.
- `source/fhir.ini` — index that enumerates the resource directories the
  build processes.
- `source/<datatype>/` — non-primitive datatypes follow the same per-folder
  pattern as resources (with `structuredefinition-<datatype>.xml`).
- `source/datatypes/` — primitive types and some simple complex / metadata
  types. Definitions here live in `<type-name>.xml` (e.g., `age.xml`,
  `annotation.xml`) and are **Excel spreadsheet** sources, **not** FHIR
  StructureDefinition XML. Do not parse them as FHIR resources.
- `source/valueset/` and `source/codesystem/` — these are the structural
  definitions of the ValueSet and CodeSystem **resource types** (i.e., the
  StructureDefinitions of those types), **not** instances of value sets or
  code systems.
- `source/*.html` — top-level specification pages. These can be referenced
  by tickets and must be exposed alongside the resource artifacts.

## Build / publish system
Bespoke — *not* sushi/IG-publisher. The HL7 publisher reads `source/fhir.ini`
and per-resource `*.xml` authoring files to assemble the spec site. Builds
are typically run on dedicated infrastructure; locally a developer may
inspect inputs but rarely re-runs the full publish.

## Artifact map
| Artifact | Lives at |
|----------|----------|
| Resource StructureDefinition | `source/<resource>/structuredefinition-<resource>.xml` (filename stem must match folder name) |
| Resource narrative & scope | `source/<resource>/<resource>-introduction.xml`, `<resource>-notes.xml` |
| Examples | `source/<resource>/<resource>-example*.xml` |
| Resource search parameters | `source/<resource>/bundle-<resource>-search-params.xml` (FHIR Bundle of SearchParameter) |
| Resource operations | `source/<resource>/list-<resource>-operations.xml` (FHIR Bundle of OperationDefinition) |
| Resource-scoped ValueSets | `source/<resource>/valueset-<name>.xml` |
| Resource-scoped CodeSystems | `source/<resource>/codesystem-<name>.xml` |
| Non-primitive datatypes | `source/<datatype>/structuredefinition-<datatype>.xml` |
| Primitive / simple datatypes | `source/datatypes/<type-name>.xml` (Excel spreadsheet, not FHIR XML) |
| ValueSet resource type definition | `source/valueset/` (StructureDefinition of the type, not instances) |
| CodeSystem resource type definition | `source/codesystem/` (StructureDefinition of the type, not instances) |
| Specification pages | `source/*.html` (referenced by tickets; must be exposed) |
| Build configuration | `source/fhir.ini` |

## Cross-repo touch points
- **UTG (`HL7/UTG`)** hosts terminology that the core spec can consume via
  canonical URLs. UTG and FhirCore both contain ValueSets / CodeSystems —
  the rules for which terminology lives where are nuanced and involve
  human judgement (governance, scope, normative status). Do **not** assume
  that terminology automatically belongs in UTG; specific ValueSets and
  CodeSystems may legitimately live in either repo.
- **`HL7/fhir-extensions`** absorbs extensions removed from the core spec.
  When the resolution moves an extension out of core, the work spans both
  repos.
- IGs (under `IgRepositories`) consume FhirCore's canonicals; breaking
  changes here ripple downstream.

## Common change recipes
- **Update narrative / prose:** edit `source/<resource>/<resource>-introduction.xml`
  or `<resource>-notes.xml`. Many tickets are narrative-only and touch
  nothing else.
- **Tweak an element definition (e.g., `short`, `definition`, `comment`):**
  edit the resource's StructureDefinition directly. For resources and
  non-primitive datatypes that have their own `source/<name>/` folder,
  `structuredefinition-<name>.xml` is **authoritative** — edit it there.
  XML files are only authoritative inside `source/datatypes/` (the
  primitive / simple-type spreadsheet folder); everywhere else, prefer
  the StructureDefinition over any sibling spreadsheet/legacy XML.
- **Add an element to a resource:** edit the StructureDefinition's
  `<differential>` (and ensure the `<snapshot>` is regenerated by the
  publisher). Update narrative in `<resource>-introduction.xml` or
  `<resource>-notes.xml` if behavior changes. Add or update example
  instances under the same directory.
- **Change cardinality / type:** edit the differential element. Verify the
  snapshot is regenerated. Cross-check examples to confirm they remain
  valid; update them if not.
- **Add a search parameter:** add or edit a SearchParameter entry inside
  `source/<resource>/bundle-<resource>-search-params.xml` (a FHIR Bundle).
  Reference it from any affected operation/profile docs.
- **Add an operation:** add or edit an OperationDefinition entry inside
  `source/<resource>/list-<resource>-operations.xml` (a FHIR Bundle), and
  add any associated example invocations.
- **Add a constraint / invariant:** edit the StructureDefinition's
  element with a new `<constraint>` block; add example data exercising it.

## Pitfalls / gotchas
- The build is bespoke; sushi/IG-publisher conventions do **not** apply.
  Do not look for `sushi-config.yaml`, `ig.ini`, `input/`, `fsh-generated/`,
  or `output/` in this repo.
- `<snapshot>` content in StructureDefinitions is regenerated by the
  publisher. Edits should focus on `<differential>` content; treat
  snapshot edits as derived data that may be overwritten.
- Some resources have a legacy `<resource>-spreadsheet.xml`. For resources
  and non-primitive datatypes with their own `source/<name>/` folder, the
  **StructureDefinition is authoritative**; treat the spreadsheet as
  legacy/derived. The XML file is only authoritative inside
  `source/datatypes/` (primitive / simple types).
- Terminology placement between FhirCore and UTG involves human judgement
  (governance, scope, normative status). Do not assume new ValueSets /
  CodeSystems must move to UTG — both repos legitimately host
  terminology. When in doubt, flag for workgroup review rather than
  auto-relocating.
- Examples are validated during the build; new or changed elements that
  break existing examples will fail publish.
- Within a `source/<resource-name>/` directory, multiple files can begin
  with `structuredefinition-`. Only the one whose name matches the
  directory (`structuredefinition-<resource-name>.xml`) is the canonical
  resource StructureDefinition; the others are extra artifacts. Naming a
  non-canonical file with the directory's name will collide with the
  resource's StructureDefinition during ingestion.
- `source/datatypes/<type-name>.xml` files are **Excel spreadsheets**, not
  FHIR XML — do not feed them into a FHIR XML parser.
- `source/valueset/` and `source/codesystem/` define the resource
  **types**; do not mistake them for instances of value sets or code
  systems.
- Ignore `tools/` and the publisher harness — only changes under `source/`
  are in scope for impact analysis.
