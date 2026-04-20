# Category Playbook: FhirExtensionsPack

## Purpose
The HL7 FHIR Extensions Pack — currently `HL7/fhir-extensions`. Houses
extensions that no longer ship inside the FHIR core spec, plus ancillary
extensions. Published as an IG. Cross-version extensions are **not**
hosted here — they live in a separate repo.

## Typical layout
- `input/` — IG authoring root. **All work happens here; never edit
  generated output directories.**
  - `input/definitions/` — hand-authored StructureDefinitions for the
    extensions, in **both XML and JSON** form
    (`StructureDefinition-<id>.xml` or `StructureDefinition-<id>.json`).
  - `input/resources/` — supporting resources (search params, capability
    statements, examples).
  - `input/pagecontent/` — narrative pages (see below).
  - `input/images/` — diagrams.
- `ig.ini` — IG-publisher entrypoint.

**Ignore entirely:** `fsh-generated/`, `output/`, `temp/`, and any
other generated/build output directories. All analysis and changes
must be against the sources only.

## Build / publish system
IG-publisher. The Extensions Pack does **not** allow use of FSH —
all StructureDefinitions are hand-authored as XML or JSON under
`input/definitions/`.

## Artifact map
| Artifact | Lives at |
|----------|----------|
| Extension StructureDefinitions (XML) | `input/definitions/StructureDefinition-<id>.xml` |
| Extension StructureDefinitions (JSON) | `input/definitions/StructureDefinition-<id>.json` |
| Examples | `input/examples/` or topic-specific dirs under `input/` |
| Narrative pages (general) | `input/pagecontent/<page>.md` (or `.xml`) |
| Narrative pages (per-extension intro) | `input/pagecontent/StructureDefinition-<id>-intro.md` (or `.xml`) |
| Narrative pages (per-extension notes) | `input/pagecontent/StructureDefinition-<id>-notes.md` (or `.xml`) |
| Build configuration | `ig.ini` |

### Page content conventions
`input/pagecontent/` holds two distinct kinds of pages:
1. **Standalone pages** — e.g., `terminology-registry.md`, `index.xml`.
2. **Per-extension pages** — keyed by the StructureDefinition id, with
   `-intro` and/or `-notes` suffixes. Examples:
   - `StructureDefinition-device-alertDetection-intro.md`
   - `StructureDefinition-observation-gatewayDevice-intro.xml`
   - `StructureDefinition-targetConstraint-notes.md`
   These are auto-included by the IG-publisher in the rendered page for
   the matching extension.

## Cross-repo touch points
- Extensions migrated out of `HL7/fhir` (FhirCore) typically land here.
  A "remove from core, add to extensions" resolution spans both repos.
- IGs under `IgRepositories` reference these extensions by canonical URL;
  removing or renaming an extension breaks downstream consumers.
- Terminology bindings inside extension definitions may reference UTG
  ValueSets.

## Common change recipes
- **Update narrative for an extension:** edit the matching
  `input/pagecontent/StructureDefinition-<id>-intro.{md,xml}` and/or
  `-notes.{md,xml}`, or update the `description`/`comment`/`definition`
  text inside the StructureDefinition itself in `input/definitions/`.
- **Update the context of an extension:** edit the `context` element(s)
  on the StructureDefinition under `input/definitions/`. This is
  high-leverage — narrowing or expanding context silently changes who
  can validly use the extension.
- **Limit the FHIR versions an extension may be used in:** apply the
  `version-specific-use` extension (canonical
  `http://hl7.org/fhir/StructureDefinition/version-specific-use`) to the
  StructureDefinition to declare which FHIR versions it is valid for.
- **Add a new extension:** create
  `input/definitions/StructureDefinition-<id>.xml` (or `.json`) defining
  the extension with `id`, `url`, `title`, `description`, `context`,
  and the value/sub-extension structure. Cross-check that the extension
  URL is unique under the IG canonical. Optionally add
  `StructureDefinition-<id>-intro` and/or `-notes` pages under
  `input/pagecontent/`.
- **Modify an existing extension:** edit the XML or JSON source under
  `input/definitions/` (whichever form that extension uses).

## Pitfalls / gotchas
- **No FSH.** Do not propose FSH-based authoring for this repo.
- **Sources only.** Ignore `fsh-generated/`, `output/`, `temp/` —
  changes there are throwaway.
- **Cross-version extensions live elsewhere** — do not assume they are
  in this repo.
- Extension `context` is high-leverage — narrowing or expanding context
  silently changes who can validly use the extension.
- Extension URLs must remain stable; renaming an extension is a breaking
  change for every downstream IG.
- An extension may have an XML *or* JSON definition (not both) — check
  for both forms before editing to avoid creating a duplicate.
