# Confluence API Notes

Durable record of what the HL7 Confluence instance actually does, as opposed to
what its documentation or our design assumptions say it does. Every claim below
was produced by `ConfluenceApiProbeTests` in
`tests/FhirAugury.Source.Confluence.Tests/`, which is a live, opt-in probe:

```powershell
$env:FHIR_AUGURY_CONFLUENCE_PROBE = '1'
dotnet test tests\FhirAugury.Source.Confluence.Tests --filter "FullyQualifiedName~ConfluenceApiProbeTests" --logger "console;verbosity=detailed"
```

Without `FHIR_AUGURY_CONFLUENCE_PROBE=1` every probe **skips**, so the
sanctioned `dotnet test fhir-augury.slnx` run never touches the network. A skip
is not a failure.

The probe requires **no credential**. HL7's Confluence answers the `/rest/api`
surface anonymously (see *Anonymous access* below). A cookie or API token is
used when `FHIR_AUGURY_CONFLUENCE_Confluence__Cookie` or
`…__ApiToken` is exported, and ignored otherwise.

---

## 2026-08-27 — initial probe

Instance: `https://confluence.hl7.org`. Credential: **anonymous**.
Probe wall clock: 36 seconds, 8 probes, ~120 requests.

### Summary

| # | Claim under test | Verdict |
|-|-|-|
| — | Requests need only a plausible User-Agent | **Refuted — a WAF blocks our current agent** |
| 2 | Instance-wide space discovery via `/rest/api/space` | **Confirmed** |
| 3 | Server / Data Center v1 dialect, not Cloud | **Confirmed** |
| 4 | Body-less page sweep honours `limit=200` and paginates to exhaustion | **Confirmed** |
| 5 | Archived pages are reachable via a `status` parameter | **Refuted — not visible at all anonymously** |
| 6 | CQL `type = comment` populates `container.id` | **Confirmed** |
| 7 | Space-wide attachment stream carries media type, size and download link | **Confirmed** |
| 8 | Corpus is ~150–200 spaces and ~10⁴ pages | **Confirmed for spaces and pages; refuted for attachments** |

---

### AWS WAF User-Agent gate — the finding that gates everything else

**Refuted:** that any reasonable User-Agent works.

The instance sits behind an AWS WAF (`Server: awselb/2.0`) that answers
**`405 Not Allowed`** with the header **`x-amzn-waf-action: captcha`** to any
client whose User-Agent is not browser-shaped. This is not rate limiting — the
very first request is rejected.

`GET /rest/api/space?type=global&status=current&limit=1`:

| Status | User-Agent | Note |
|-|-|-|
| `405` | `FhirAugury/2.0` | **what `Program.cs` sends today** |
| `405` | `Mozilla/5.0` | bare token, no comment |
| `405` | `python-requests/2.31.0` | |
| `405` | `curl/8.x` (curl default) | |
| `200` | `Mozilla/5.0 (Windows NT 10.0; Win64; x64)` | |
| `200` | `Mozilla/5.0 (compatible; FhirAugury/2.0; +https://github.com/GinoCanessa/fhir-augury)` | **adopted** |

The rule is the *shape*: `Mozilla/5.0` followed by a parenthesized comment. The
adopted agent satisfies it while still identifying the client honestly and
pointing at the project.

**Consequence:** the Confluence source as configured before this work would have
failed **100% of requests** against HL7 Confluence, and the failure surfaces as
a `405` — not a `401`, `403` or `429` — so no retry, backoff or auth path would
have recovered it. The user-agent is changed in
`src/FhirAugury.Source.Confluence/Program.cs`.

### Anonymous access

**Confirmed:** the whole `/rest/api` read surface used by this source — spaces,
content, CQL search, and attachment downloads under `/download/attachments/…` —
answers anonymously. Every number on this page was gathered without a
credential.

A credential still matters for *coverage*, not access: content restricted to
logged-in users is invisible anonymously and is therefore absent from an
anonymous sweep. Because absence from a manifest is what the reconciler treats
as `Vanished`, **mixing credentialed and anonymous runs against the same cache
will tombstone restricted content.** This is why tombstoning moves files to
`_vanished/` rather than deleting them.

### 2. Space discovery — confirmed

`GET /rest/api/space?type=global&status=current&limit=200`

- **140** global, non-archived spaces, enumerated in **one** request.
- `status=current` **is** honoured: every returned space reported
  `status = current`, and `type=global` likewise.
- The unfiltered `/rest/api/space` returns **141** — the difference is a single
  personal space. `status=archived` returns **0**, so this instance currently
  has no archived spaces (which are out of scope regardless).
- `_links.next` is absent at `limit=200` for 140 results, so pagination is not
  exercised here; it is exercised on content (below).

The plan estimated 150–200 spaces. 140 is within the same order and the sweep
budget derived from it stands.

### 3. API dialect — confirmed

- `_links.self` on `/rest/api/space/FHIR` is
  `https://confluence.hl7.org/rest/api/space/FHIR` — the v1 Server/Data Center
  surface. No `/wiki/` prefix, so **not Cloud**.
- `_expandable` blocks are present throughout; version links point at
  `/rest/experimental/content/{id}/version/{n}`. Users carry `username` and
  `userKey` (Server/DC) rather than `accountId` (Cloud).
- Reported version, from the `ajs-version-number` meta tag on
  `/spaces/FHIR/overview`: **Confluence 10.2.13, build 9422**.

### 4. Body-less page sweep — confirmed

`GET /rest/api/content?spaceKey={key}&type=page&expand=version&limit=200`

- `limit=200` is **honoured verbatim**, not clamped: the envelope reports
  `limit: 200` and returns 200 results. `limit=500` is also honoured, so 200 is
  our politeness choice rather than a server ceiling.
- `_links.next` is present and **paginates to exhaustion**: FHIRI enumerated
  **1349** pages over **7** requests, exactly matching the CQL `totalSize` of
  1349 for the same space.
- The envelope's `size` field is the **count in this page**, not the corpus
  total. A corpus total must come from `/rest/api/search?cql=…`, whose envelope
  carries `totalSize`.
- Each entry carries `id`, `type`, `status`, `title`, and — with
  `expand=version` — `version.number` and `version.when`. That is everything a
  manifest entry needs, with no body transferred.

### 5. Archived page visibility — refuted

**No form of the `status` parameter surfaces archived pages to an anonymous
client, and CQL reports that none are visible at all.**

`GET /rest/api/content?spaceKey=FHIR&type=page&status=…&limit=25`:

| `status` form | Results | Statuses returned |
|-|-|-|
| `current` | 25 | `current` |
| `archived` | 0 | — |
| `current,archived` (comma) | **0** | — (comma form is not parsed as a list) |
| `status=current&status=archived` (repeated) | 25 | `current` only |
| `any` | 25 | `current`, **`trashed`** |

`CQL type=page and status=archived` across the whole instance returns
**`totalSize = 0`**.

Two things follow, both load-bearing:

1. **`status=any` must not be used as the archived-capture query.** It admits
   `trashed` content — pages in the recycle bin — which we do not want in the
   cache or the database.
2. **Archived visibility is permission-scoped.** Anonymously there is nothing to
   see. Whether a credentialed run sees archived pages is **not established by
   this probe** and must be re-probed with a cookie before the sweep is trusted
   to capture them.

Practical consequence for the sweep: keep the default page query at
`status=current`, treat archived capture as conditional on a credentialed
re-probe, and — critically — **do not infer "archived" from absence.** Absence
from an anonymous sweep means "not visible to this credential", which is exactly
the false positive that makes tombstone-by-move rather than delete the correct
design.

### 6. Comments via CQL — confirmed

`GET /rest/api/content/search?cql=space="{key}" and type=comment&expand=version,container&limit=200`

- Works. `container.id` was populated on **200 of 200** results.
- `_links.next` is present, so the stream paginates like any other.
- Entries carry `extensions.location` (`footer` / `inline`), useful later.
- Instance-wide: **7,118** comments.

**The per-page fallback is not needed.** The plan's contingency — one
`GET /rest/api/content/{id}/child/comment` per page, which would have added
~45,000 requests to every sweep — does not apply. The comment sweep is a third
space-wide stream costing roughly 140–180 requests instance-wide.

### 7. Attachments via CQL — confirmed

`GET /rest/api/content/search?cql=space="{key}" and type=attachment&expand=version,container,metadata&limit=200`

- The space-wide stream **exists**. On a 200-result page: `container.id` 200/200,
  `extensions.mediaType` 200/200, `extensions.fileSize` 200/200,
  `_links.download` 200/200.
- `_links.download` is a **site-relative** path, e.g.
  `/download/attachments/{pageId}/{fileName}?version=1&modificationDate=…&api=v2`.
  It must be resolved against the base URL, and it is **not** under `/rest/api/`.
- A `HEAD` on that link returns `200` with an accurate `Content-Length` and the
  real `Content-Type`. `Content-Length` is therefore a usable pre-transfer gate
  for the attachment size cap.
- The per-page fallback `GET /rest/api/content/{id}/child/attachment?expand=version,metadata`
  also works, if it is ever needed.

### 8. Corpus size and the derived budget

Instance-wide totals, from `/rest/api/search?cql=…&limit=1` → `totalSize`:

| Content type | Count |
|-|-|
| Spaces (global, current) | **140** |
| Pages | **45,222** |
| Comments | **7,118** |
| Attachments | **195,570** |
| Blog posts | 99 (not in scope) |

Representative spaces:

| Space | Pages | Comments | Attachments |
|-|-|-|-|
| FHIR | 1,621 | 494 | 4,907 |
| FHIRI | 1,349 | 140 | 225 |
| SOA | 292 | 131 | 608 |

#### Sweep cost — cheap, as the design assumed

A sweep costs at least one request per stream per space, plus one per extra page
of results. At `SweepPageSize = 200`:

```
>= 420 (3 streams x 140 spaces) + ~1,239 paging  =  ~1,660 requests
at 5 req/s                                        =  ~5.5 minutes per full sweep
```

**This confirms the sweep-is-cheap argument.** Sweeping every space on every run
costs about five and a half minutes of body-less traffic. `SpaceSweepMaxAge`
stays at `"00:00:00"` (sweep everything, every run); the measurement gives no
reason to rotate.

#### Fill cost — substantially larger than assumed

```
~52,340 body fetches (45,222 pages + 7,118 comments)  ~2.9 hours at 5 req/s
195,570 attachment blob fetches                       ~10.9 hours at 5 req/s
                                                total ~13.8 hours
```

#### Attachment bytes — the material refutation

Sampled by full enumeration of four spaces (SOA, FHIRI, CIMI, CDS):

| Measure | Value |
|-|-|
| Attachments sampled | 1,375 |
| Total bytes | 2,053.3 MB |
| Average | **1,529 KB** |
| Largest single | 98.7 MB |
| `extensions.fileSize` **absent** | **0** |
| `extensions.fileSize` **zero** | **0** |
| Over 25 MB | 15 (1.1%) |
| Over 100 MB (`AttachmentMaxBytes` default) | **0** |

Extrapolating the sampled average across 195,570 attachments gives
**~285 GB** for a complete attachment byte pull.

Three consequences, stated plainly because later phases depend on them:

1. **`AttachmentMaxBytes = 104857600` (100 MB) excludes essentially nothing.**
   Not one attachment in a 1,375-item sample exceeded it, and the largest seen
   anywhere was 98.7 MB. The cap does its stated job — bounding a single
   pathological item — but it does **not** bound the aggregate pull. A ~285 GB,
   ~14-hour initial acquisition is the real shape of "download everything", and
   the design's convergence and resumability are what make that tractable
   rather than the cap.
2. **`extensions.fileSize` is reliable on this instance** — absent in 0 of 1,375
   and zero in 0 of 1,375. The manifest can be trusted for the common case.
   The `Content-Length` check and the counting-stream guard remain worth having
   as defence in depth, but they are not load-bearing here, and the nullable
   `FileSize` model is a correctness nicety rather than a frequent path.
3. Attachments outnumber pages roughly **4.3 : 1**. They dominate the cache, the
   fill budget, and the sweep's paging cost.

---

### Open items for a credentialed re-probe

The following are **unresolved anonymously** and should be re-run with
`FHIR_AUGURY_CONFLUENCE_Confluence__Cookie` exported before the corresponding
behaviour is trusted:

- Whether archived pages are visible at all, and which `status` form returns
  them (probe 5 above).
- Whether the corpus totals rise once restricted spaces and pages become
  visible — every count on this page is an anonymous-visibility lower bound.
- Whether the WAF treats an authenticated session differently from an anonymous
  one. The User-Agent gate was only measured anonymously.
