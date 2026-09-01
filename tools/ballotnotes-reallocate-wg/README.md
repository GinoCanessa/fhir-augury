# ballotnotes-reallocate-wg

A one-off, **idempotent** maintenance command that re-stamps the owning **Work
Group** on existing ballot-note rows by re-running *only* the deterministic
`OwningWorkGroupResolver` (from `scratch/0624-03`) over the rows already in the
notes DB.

It does **not** re-walk the commit window, re-attribute tickets, re-run the
structural diff, or trigger any AI/prose authoring. It writes only four columns —
`WorkGroup`, `WorkGroupCode`, `WorkGroupNames`, `WorkGroupCodes` — and preserves
every other field, including all authoring timestamps and `NeedsNote`.

## When to use it

After landing a change to the owning-WG allocation rule, every note already in
`cache/ballot-notes.db` carries a WG computed under the old rule. This command
refreshes those rows cheaply and deterministically, so the DB matches what a
fresh hydration would now compute for the higher (deterministic) resolver tiers.

## Usage

```
ballotnotes-reallocate-wg reallocate --clone <repo-clone-path> [options]
```

### Options

| Flag | Default | Meaning |
|------|---------|---------|
| `--clone <path>` | *(required)* | Local repo clone for the repo-read step and `DataType` HEAD listing. |
| `--db <path>` | `./cache/ballot-notes.db` | Notes SQLite DB to re-stamp. |
| `--repo <owner/name>` | *(all rows)* | Restrict the run to one repository. Required if the DB spans multiple repos (one clone serves one repo). |
| `--dry-run` | off | Print intended per-note changes and write nothing. Opens the notes DB **read-only**. |
| `--github-db <path>` | `./cache/github.db` | Read-only GitHub source DB (registry + WG tables). |
| `--fhir-r6-db <path>` | `./cache/fhir-r6.db` | Read-only current-build FHIR R6 DB (`Structures.WorkGroup`). |
| `--fhir-spec-db <path>` | `./cache/fhir-spec.db` | Read-only published FHIR spec DB. |
| `--work-group-hint <wg>` | *(empty)* | Ticket-fallback hint; not persisted per note. |
| `--allow-stale-clone` | off | Skip the `clone HEAD == note HeadSha` guard. |
| `--allow-mixed-heads` | off | Allow selected notes to span multiple `HeadSha` values. |

## Recommended workflow

1. **Back up first.** Copy `cache/ballot-notes.db` somewhere safe (the data
   re-stamp is reversible by re-running hydration or restoring this copy).
2. **Preview.** Run with `--dry-run` against the copy to review the intended
   `from -> to` changes:

   ```
   dotnet run --project tools/ballotnotes-reallocate-wg -- reallocate \
       --db <copy>.db --clone cache/github/repos/HL7_fhir/clone --repo HL7/fhir --dry-run
   ```

3. **Apply.** Re-run without `--dry-run` to write the changes.
4. **Verify idempotency.** A second write run reports `changed: 0`.
5. **Regenerate the site.** Re-run `notes-site` / `index-notes` so the SPA
   groupings reflect the corrected owners (a separate, existing step).

## Safety / guards

- **Reference-DB preflight.** The runner opens `--github-db` (and a spec DB)
  read-only and confirms the resolver's tables are present before any restamp, so
  a missing/drifted DB cannot silently downgrade output to `(unknown)`/raw codes.
- **Multi-repo guard.** One `--clone` serves one repo; if the selected notes span
  multiple repositories the run fails and asks for `--repo`.
- **Clone-fidelity guard.** The resolver reads live clone files / `git ls-tree
  HEAD`, so the runner requires a single `HeadSha` across the selected notes and
  `git rev-parse HEAD == HeadSha` (override with `--allow-mixed-heads` /
  `--allow-stale-clone`).

## Known limitations

- **Ticket-fallback recency is approximate.** `note_tickets` stores no
  `AttributionDate`, so the artifact ticket-recency tier reconstructs all dates as
  `MinValue`; `SelectOwningWorkGroup` then picks the first non-empty-WG ticket in
  `TicketOrder`. This tier is the **last** resort (registry -> repo-read -> spec-DB
  -> base-resource run first), so a WG that *changes* does so because a higher
  deterministic tier now resolves — not because of ticket ordering.
- **`workGroupHint` is per-run, not per-note.** A fresh hydration may have passed
  a hint; this command defaults it to empty. Supply `--work-group-hint` for a
  single-repo DB if needed.
