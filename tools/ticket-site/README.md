# ticket-site

`ticket-site` is the offline, network-independent command-line adapter over
`FhirAugury.Publishing.Tickets`. It renders a static review site from one
supplied, verified Preparer or Planner snapshot pair. It never opens a live
processor database, downloads a snapshot, starts a publication refresh,
contacts the Preparer, Planner, Orchestrator, or Jira, or owns processor
completion state.

## Usage

Choose exactly one processor snapshot and provide its descriptor:

```powershell
dotnet run --project tools\ticket-site -- `
  --preparer-snapshot <snapshot.db> `
  --snapshot-descriptor <snapshot.descriptor.json> `
  --out cache\jira-ticket-site
```

```powershell
dotnet run --project tools\ticket-site -- `
  --planner-snapshot <snapshot.db> `
  --snapshot-descriptor <snapshot.descriptor.json> `
  --out cache\jira-ticket-site
```

Options:

| Option | Description |
|-|-|
| `--preparer-snapshot <path>` | Preparer review snapshot for the discussion site. |
| `--planner-snapshot <path>` | Planner review snapshot for the applying site. |
| `--snapshot-descriptor <path>` | Trusted descriptor paired with the selected snapshot. |
| `--out <dir>` | Root output directory. Defaults to `cache\jira-ticket-site`. |
| `--title <text>` | Base site title. When omitted, Preparer uses `Tickets for Discussion` and Planner uses the existing `Ticket Site`. |
| `--spec <value>` | Filter by specification using snapshot-resident data. |
| `--project <value>` | Filter by project using snapshot-resident data. |
| `--wg <value>` | Filter by workgroup using the snapshot workgroup catalog. |
| `--force` | Permit replacement of an owned compatible output. Not a fallback for an enrichment refusal or permission to replace the retained original site. |

Exactly one snapshot option is required. The descriptor filename, checksum,
processor kind, service/workflow binding, schema, counts, provenance, and
monotonic sequence are validated before publication.

Preparer snapshot schema and Discussion renderer schema are independent
contracts. The current safe public processor output is **Preparer snapshot
schema v3**. The adapter validates that immutable input, then creates a
publication-owned **Discussion renderer schema v3** browser database. A
renderer version never changes the supplied snapshot in place.

The Discussion display title appends the maximum exported **self-ticket Jira
`UpdatedAt`**, for example `Tickets for Discussion - Sept 15, 2026`.
This is `prepared_jira_hydration.UpdatedAt` where `TicketKey = JiraKey`,
selected by generation filters and normalized to UTC. The suffix requires a
nonempty export with complete valid dates. It uses fixed English labels
`Jan, Feb, Mar, Apr, May, Jun, Jul, Aug, Sept, Oct, Nov, Dec`, an unpadded day,
and no `Built`. Related-ticket dates, upstream-refresh watermarks, authoring
completion, snapshot creation, and publication time cannot supply the date.
Malformed non-null timestamps abort validation rather than disappearing into
missing coverage. Every retained ticket in schema v2/v3 must still resolve
to exactly one accepted authoring coordinate; zero or multiple matches remain
structural errors.

Legacy Preparer schema-v1 and schema-v2 snapshots remain readable
compatibility inputs, but every generated publication reports degraded
readiness and exposes no trusted people from them. Complete self-ticket dates
can qualify the suffix independently of degraded readiness/provenance,
including on legacy input. Empty, missing, or partial date coverage leaves
the title unsuffixed; a partial maximum remains a corpus fact, not a title
date. The legacy pair is never upgraded or edited. An explicit `--title`
remains the stable base; the date suffix and then any generation-filter
suffix are added only to the display title. Browser filters do not change
that frozen title. Applying remains unchanged: Planner snapshots stay on
schema v1, receive no Discussion date/coverage contract, and retain
`Ticket Site` as the omitted-title CLI default.

When the two paths name files in a durable pair directory, the adapter verifies
the directory's `verified-pair.json`. For legacy loose-file input, it captures
an immutable private copy and constructs a temporary service-bound pair before
calling the publisher. Loose files are compatibility input, not durable
publisher state. A ready pair contains exactly:

- the descriptor named by `descriptorFileName`;
- the SQLite database named by `databaseFileName`; and
- `verified-pair.json`, written last and binding the service, run, snapshot,
  format version, filenames, size, and descriptor/database SHA-256 digests.

The shared publisher re-verifies that pair, validates the canonical public
snapshot schema, renders from an immutable private copy, and publishes through
the staged directory publisher. A failed or older build cannot replace a valid
newer site. The Dev UI invokes this same publisher in-process; it does not
spawn `ticket-site`.

Discussion publication accepts immutable Preparer snapshot schemas v1, v2,
and v3 and projects each into a newly created **renderer schema v3** SQLite
database. Its `ReadinessJson` and the manifest's structured
`discussionReadiness` carry the same `isReady`, `evidence`, optional source
revision and people-policy coordinates, and machine-readable reasons. Legacy
input includes
`legacy-snapshot-schema` and `missing-people-policy-proof`; missing ordinary
provenance or an invalid refresh proof adds its corresponding reason. Reporter
and Assignee are rendered with that unavailability reason when policy proof is
missing. `No public display name available` describes a policy-qualified v3
value with no publishable name; it does not establish that the role is absent
or a ticket is unassigned. Policy proof and actual public-name coverage are independent.
The browser never queries processor persistence tables.
Applying continues to consume Planner snapshot schema v1 through its existing
path.

Renderer v3 also requires a matching `discussionCorpus` manifest summary and
`site_metadata.CorpusSummaryJson`: `ticketCount`, `exportedProjectCount`,
`validJiraUpdatedAtCount`, `maxJiraUpdatedAt`, `dateCoverage`
(`empty`, `none`, `partial`, `complete`), and ticket counts
`ticketsWithPublicReporter`, `ticketsWithPublicAssignee`, and
`ticketsWithPublicRequester`. `linksByKind` entries contain `kind`,
`totalRows`, `resolvedSafeLinks`, `unresolvedWithRetainedSafeLinks`, and
`withoutUsableUrl`. Link coverage counts related rows, not tickets; a safe URL
does not prove source backing. The summary is optional only for legacy
manifests and Applying. Missing legacy coverage is unknown, not measured zero.
Row-derived coverage and its title are validated independently of readiness.

Discussion facets preserve overview/crosscut/list routes and accumulate
without discarding other filters. Chip removal preserves the remaining
selection and list search/sort/history; active/fixed dimensions are hidden,
and Project is hidden for a single-project export, not merely a narrowed
browser result. Guided proposal labels preserve authored bodies and
copy-for-AI text. Safe HTTP(S) source links open externally, while in-corpus
Jira hash navigation preserves filters.

Related-item and summary-source links retain explicit lookup diagnostics.
Previously source-backed safe context remains last-known after a failed
lookup; an old zero-message thread URL remains unverified rather than
resolved. Numeric Zulip references can carry indexed `/near/` links, and
repository references retain their snapshot URLs. Optional-detail diagnostics
are visible even on resolved links. The publisher never repairs or discovers
references from a source. Repair of missing GitHub-item URLs or lookup failures
is outside the enrichment operation; repository links are a separate kind.

The output contains the selected sub-site, an embedded SQLite database, the
exact manifest name `site-manifest.json`, ownership metadata, and a chooser
page when either sub-site exists. For Discussion, manifest `title` remains the
stable base used by workflow validation, while `displayTitle`,
`jiraSourceLastSuccessfulRefreshAt`, `rendererSchemaVersion`,
`discussionReadiness`, and `discussionCorpus` carry the presentation contract.
`jiraSourceLastSuccessfulRefreshAt` remains upstream provenance, not the
display-title date. `tableCounts` describes the embedded renderer database,
not the canonical Preparer snapshot.
The remaining fields record source snapshot identity and digest, included
counts, filters, renderer assets version, build identity, and output path.
`TicketSiteManifest.ComputeBuildIdentity()` is a public read-only recomputation
used to validate committed artifacts, including Dev UI reconstruction; legacy
manifests are not rewritten into renderer v3.

The command-line default remains `cache\jira-ticket-site`. The Dev UI instead
uses one output root per workflow/run:

```text
cache\devui-review-sites\prepare\{runId}\discussion\
cache\devui-review-sites\plan\{runId}\applying\
```

Those sites are served at
`/review-sites/{prepare|plan}/{runId}/{discussion|applying}/` on the trusted
local Dev UI origin. Their corresponding verified pairs stay under the private
`cache\devui-authoring-snapshots\` root. A publication failure does not alter
processor state or accepted receipts; retain the pair and rerun only
publication.

Publication enrichment happens before this adapter is invoked. Retain the
original descriptor/database pair and complete site with their digests. The
Dev UI's generation/regeneration action is independent of its refresh action,
even when a site is proof-ready. Use the Dev UI, documented
`prepared-ticket-authoring` / `refresh-publication` CLI action, or processor
HTTP API to request a **new linked** Preparer `publication-refresh` run.
Admission verifies that the original snapshot's protected content/receipts
and grouping still exist unchanged, then freezes the entire current accepted
corpus. Disjoint additional current output is disclosed in public run status
`corpusComparison`, not inferred through a client database read.

The persisted `publication-enrichment` v1 recipe executes
`publication-enrichment-v1` under the maintenance fence, enriches Jira and
accepted Zulip references, and certifies retained grouping without authoring
or grouping dispatch. Changed original content/grouping, accepted Jira
revision, or source generation causes refusal, not deletion, re-authoring,
regrouping, or forced original-site replacement. After successful completion,
download that new run's verified pair and publish into a different output
root. Run, snapshot, sequence, and site identity change; original artifacts
remain untouched. Preparer-v3/public proof-v1 are unchanged contracts.

If source people need population, that is a separate authorized
`POST /api/v1/jira/public-people/preview` followed by `/apply` through the
gateway, with exact identity/revision evidence and full shared-impact
acknowledgement. Cache-only is the default; legacy raw caches without source
acquisition receipts are untrusted and upstream reads need explicit
authenticated opt-in. Not every name can be recovered. See the
[operator procedure](../../docs/user/generating-discussion-tickets.md#populate-source-people-only-after-an-explicit-preview)
for bounds, failure codes, expiry, atomic apply, and lost-response handling.
Typed indexed-only Zulip diagnostics use gateway
`GET /api/v1/zulip/references/resolve?reference=...`; they do not establish
recovery of every historical JSON failure or any particular live corpus.

Persisted null legacy maintenance metadata recovers the old Jira-only recipe;
unknown non-null recipes are refused. Quiesce/reconcile new runs before a
binary downgrade. Committed source/processor metadata persists after a code
revert: do not reset it, lower generations, or replay accepted work.
Operational live repair remains separately authorized.

The adapter renders exactly the supplied pair and publishes files locally;
it does not call sources or processors to validate, refresh, or supplement
its contents. Legacy loose-file arguments remain a compatibility entry point
only because the adapter first captures them into an immutable, service-bound
verified pair. There is no in-place renderer migration.

## Static hosting

Upload the complete generated output directory, including every `assets`
folder. Discussion's classic `state.js` is mandatory and loads before its
presentation/application scripts; publish it with the matching database and
all other assets. Generated HTML adds the renderer asset-version token to URLs
so a CDN or browser cannot combine a newly generated embedded database with stale
JavaScript or WebAssembly. The completed site is self-contained: its browser
reads only the embedded renderer database and static assets and does not call
Jira, the Orchestrator, the Preparer, or any other live service. It can be
opened directly from disk or served from a static host. Discussion HTML embeds
the trusted SQL.js WebAssembly bytes for direct `file://` initialization while
also retaining the standalone WebAssembly asset for hosted compatibility.

For a deployment created before asset versioning was added, purge the CDN
cache once after replacing the complete output. A stale discussion `app.js`
does not inflate the current gzipped database and reports
`Failed to load database: file is not a database`.
