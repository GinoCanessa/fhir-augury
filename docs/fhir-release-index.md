
## FHIR Release Indexes

Quick reference for bounding FHIR-version-specific work (ballot notes, ticket
attribution, spec diffing, "what changed in R5") to a single core release.
**Commit bounds** are in the [`HL7/fhir`](https://github.com/HL7/fhir) repository,
shown as `date · tag/ref · SHA` where the short SHA links to the full commit on
GitHub. **Ticket windows** are the lowest/highest `FHIR-` Jira ticket *applied*
during the release. Both are **best-effort / rough bounds**, not complete or
ordered lists — see the notes below the table. Resolved 2026-07-14 against
`HL7/fhir` `HEAD` = [`94dbe68`](https://github.com/HL7/fhir/commit/94dbe68f231ca265f33905f9b04433c6e0422f18)
(2026-07-10); a documented snapshot, not auto-regenerated.

| Release | Released | Interim builds | Lowest commit | Highest commit | FHIR- ticket window (rough) |
|---------|----------|----------------|---------------|----------------|------------------------------|
| R6 (6.0.0-ballot4) | in ballot | 2023-12-19, 2024-08-13, 2025-04-03, 2025-12-18, CI → next ballot (~2026-07-17) | 2023-03-26 · master · [`ee061c5`](https://github.com/HL7/fhir/commit/ee061c5a5d524fbbe58d95dfa4fb8472804a34e2) | 2026-07-10 · master@HEAD · [`94dbe68`](https://github.com/HL7/fhir/commit/94dbe68f231ca265f33905f9b04433c6e0422f18) — ongoing | FHIR-9197 → FHIR-57749 (ongoing as of 2026-07-10) |
| R5 (5.0.0) | 2023-03-26 | 2019-12-31, 2020-05-04, 2020-08-20, 2021-04-05, 2021-12-19, 2022-09-10, 2022-12-14, 2023-03-01 | 2019-11-02 · master · [`959acd1`](https://github.com/HL7/fhir/commit/959acd13e7964a6f7cfeb607d29fe458460254e3) | 2023-03-26 · v5.0.0 · [`eca054d`](https://github.com/HL7/fhir/commit/eca054db690594b98b3cf81ff52634f2bbc69822) | FHIR-3177 → FHIR-40664 |
| R4B (4.3.0) | 2022-05-28 | 2021-03-11, 2021-12-20 | 2021-01-21 · R4B · [`c69d71a`](https://github.com/HL7/fhir/commit/c69d71aa213de86d55e0261339c15e4bacd509e6) ⚠ | 2022-05-28 · R4B · [`d685d85`](https://github.com/HL7/fhir/commit/d685d8588a75141177ae8b93141986678017d7d8) ⚠ | FHIR-19955 → FHIR-36705 |
| R4 (4.0.1) | 2019-10-30 | 2018-04-02, 2018-08-20, 2018-11-09 | 2017-04-19 · master · [`cf39f66`](https://github.com/HL7/fhir/commit/cf39f6678ba74f1dd2177ce6149ba7fe78bed8ca) (approx.) | 2019-10-27 · master · [`b635715`](https://github.com/HL7/fhir/commit/b63571578b4560de7ad5507a1cc847a62f5d52ae) | FHIR-3160 → FHIR-25029 |

Notes:

- ⚠ **R4B** was split off the R4-era line while R5 was developed on `master`, so its
  commits are **not** a clean contiguous `master` range — its window is a best-effort
  span on branch `origin/R4B` (the post-GA branch tip `d63c3542`, 2022-08-15, is
  excluded). This non-sequential-commits caveat applies to **R4B only**.
- **Commit bounds** are **first-parent** mainline anchors on `master` (R4B is
  best-effort on its own branch), so they land on true integration commits rather
  than side-branch commits that merely sort by date. The R5 high bound is the
  annotated `v5.0.0` release tag. The **R4 low bound is approximate** (`approx.`) —
  the pre-R4 boundary is not precisely tracked and pre-R4 releases are out of scope.
- **Ticket windows are non-contiguous** — a rough indication of the work applied
  during the release, not every `FHIR-` number in the range and not in numeric order.
  They are derived from `FHIR-` references in commit messages within each window
  (a lighter-weight proxy than the BallotNotes attributor), then filtered to real
  `FHIR-` Jira keys, so bogus tokens are dropped.
- **R6** is still in development; its upper bounds are "as of" the last update
  (`HEAD` = `94dbe68`, 2026-07-10) and are marked **ongoing**, not final. Its ticket
  upper bound reflects the current Jira snapshot; newer `FHIR-` references may exist
  in `HL7/fhir` above it.
