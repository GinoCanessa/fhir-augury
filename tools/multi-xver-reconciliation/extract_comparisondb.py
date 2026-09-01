"""Extractor: local xver-analysis pipeline fhir-comparison.sqlite (ComparisonDb).

Read-only. Emits atomic reconciliation assertions from ElementComparisons for the
two in-scope adjacent pairs (R4->R4B, R4B->R5). Each source-driven comparison row
may yield several atomic records (Removed | Renamed / Cardinality / Type / Target /
Mapped), joined to Elements on both sides for cardinality + type literals.

The source DB is source-driven, so target-only "Added in later" elements are
largely ABSENT. That under-coverage of Added is itself a meaningful reconciliation
finding and is deliberately NOT synthesized here.

Never writes the source DB: opened via file: URI with mode=ro & immutable=1.
"""
from __future__ import annotations

import json
import sqlite3
from collections import Counter

import contract as C

# Read-only, immutable open of the ~1GB source DB (never mutated, no WAL side files).
DB_URI = "file:C:/git/fhir-cross-version/temp/fhir-comparison.sqlite?mode=ro&immutable=1"

DETAIL_FILE = "fhir-comparison.sqlite"

# pair constant -> (SourceFhirSequence, TargetFhirSequence) filter values
PAIR_SEQ = {
    C.PAIR_R4_R4B: ("R4", "R4B"),
    C.PAIR_R4B_R5: ("R4B", "R5"),
}

# Relationship tokens that assert *no* real change on a dimension.
EQUIVISH = ("", "Equivalent")

# One streamed query per pair. LEFT JOIN Elements twice (source + target) to pull
# cardinality + type literals in a single pass. Elements.Key is INTEGER PRIMARY KEY,
# so each join matches at most one row (no fan-out). Parameterized on the sequences.
SQL = """
SELECT
    ec.Key                        AS Key,
    ec.SourceElementId            AS SourceElementId,
    ec.TargetElementId            AS TargetElementId,
    ec.Relationship               AS Relationship,
    ec.NotMapped                  AS NotMapped,
    ec.RelativePathsAreIdentical  AS RelativePathsAreIdentical,
    ec.TypeRelationship           AS TypeRelationship,
    ec.TypeMessage                AS TypeMessage,
    ec.TargetProfileRelationship  AS TargetProfileRelationship,
    ec.TargetProfileMessage       AS TargetProfileMessage,
    ec.UserMessage                AS UserMessage,
    ec.TechnicalMessage           AS TechnicalMessage,
    se.MinCardinality             AS sMin,
    se.MaxCardinalityString       AS sMax,
    se.FullCollatedTypeLiteral    AS sFull,
    se.DistinctTypeLiterals       AS sDist,
    te.MinCardinality             AS tMin,
    te.MaxCardinalityString       AS tMax,
    te.FullCollatedTypeLiteral    AS tFull,
    te.DistinctTypeLiterals       AS tDist
FROM ElementComparisons ec
LEFT JOIN Elements se ON se.Key = ec.SourceElementKey
LEFT JOIN Elements te ON te.Key = ec.TargetElementKey
WHERE ec.SourceFhirSequence = ? AND ec.TargetFhirSequence = ?
ORDER BY ec.Key
"""


def _clean(v):
    """Trim strings; collapse empty / whitespace-only to None."""
    if v is None:
        return None
    s = str(v).strip()
    return s or None


def _card(cmin, cmax):
    """'min..maxStr' cardinality literal, or None when either side is absent."""
    if cmin is None or cmax is None:
        return None
    return f"{cmin}..{cmax}"


def _changed(rel):
    """True when a *-Relationship column asserts a real (non-equivalent) change."""
    return rel is not None and rel not in EQUIVISH


def emit_pair(cur, pair, records, counts):
    """Stream one pair's ElementComparisons rows into atomic assertion records."""
    src_seq, tgt_seq = PAIR_SEQ[pair]
    cur.execute(SQL, (src_seq, tgt_seq))

    for r in cur:
        ref = f"ElementComparison:{r['Key']}"
        src_id = _clean(r["SourceElementId"])
        tgt_id = _clean(r["TargetElementId"])
        relationship = _clean(r["Relationship"])

        def add(ct, **kw):
            records.append(C.rec(
                C.SRC_COMPARISON_DB, pair, ct,
                detail_file=DETAIL_FILE, detail_ref=ref, **kw))
            counts[(pair, ct)] += 1

        # --- Removed: source-only element, or explicitly asserted no-map ------
        # (single record for the row; no other dimensions apply)
        if tgt_id is None or r["NotMapped"] == 1:
            notes = _clean(r["UserMessage"]) or _clean(r["TechnicalMessage"])
            add(C.CT_REMOVED, earlier_path=src_id, later_path=None,
                relationship=relationship, notes=notes)
            continue

        # --- Mapped correspondence: may emit several atomic dimension records --
        emitted = 0

        # Renamed: full path differs, or relative path flagged non-identical.
        if src_id != tgt_id or r["RelativePathsAreIdentical"] == 0:
            add(C.CT_RENAMED, earlier_path=src_id, later_path=tgt_id,
                relationship=relationship)
            emitted += 1

        # Cardinality: both sides known and the 'min..max' literal differs.
        src_card = _card(r["sMin"], r["sMax"])
        tgt_card = _card(r["tMin"], r["tMax"])
        if src_card and tgt_card and src_card != tgt_card:
            add(C.CT_CARDINALITY, earlier_path=src_id, later_path=tgt_id,
                raw_old=src_card, raw_new=tgt_card)
            emitted += 1

        # Type: datatype-set relationship is non-equivalent. Use the distinct
        # type sets (Reference/canonical *profiles* are captured by Target below).
        if _changed(_clean(r["TypeRelationship"])):
            add(C.CT_TYPE, earlier_path=src_id, later_path=tgt_id,
                raw_old=_clean(r["sDist"]) or _clean(r["sFull"]),
                raw_new=_clean(r["tDist"]) or _clean(r["tFull"]),
                relationship=_clean(r["TypeRelationship"]),
                notes=_clean(r["TypeMessage"]))
            emitted += 1

        # Target: Reference()/canonical() target-profile relationship non-equivalent.
        # Target profiles are "easily available" in the FullCollatedTypeLiteral
        # (e.g. 'Reference(url1,url2,...)'), so surface them as raw_old/raw_new.
        if _changed(_clean(r["TargetProfileRelationship"])):
            add(C.CT_TARGET, earlier_path=src_id, later_path=tgt_id,
                raw_old=_clean(r["sFull"]), raw_new=_clean(r["tFull"]),
                relationship=_clean(r["TargetProfileRelationship"]),
                notes=_clean(r["TargetProfileMessage"]) or _clean(r["TypeMessage"]))
            emitted += 1

        # Mapped anchor: correspondence asserted but no further change on the row.
        if emitted == 0:
            add(C.CT_MAPPED, earlier_path=src_id, later_path=tgt_id,
                relationship=relationship)


def _report(records, counts):
    print("\n" + "=" * 72)
    print("VALIDATION REPORT - ComparisonDb extractor")
    print("=" * 72)
    print(f"total records: {len(records)}")

    by_pair = Counter(r["pair"] for r in records)
    print("\nby pair:")
    for pair in (C.PAIR_R4_R4B, C.PAIR_R4B_R5):
        print(f"  {pair:<10} {by_pair.get(pair, 0)}")

    by_ct = Counter(r["change_type"] for r in records)
    print("\nby change_type:")
    for ct, n in by_ct.most_common():
        print(f"  {ct:<12} {n}")

    print("\nby (pair, change_type):")
    for (pair, ct), n in sorted(counts.items()):
        print(f"  {pair:<10} {ct:<12} {n}")

    print("\none sample per change_type:")
    for ct in (C.CT_RENAMED, C.CT_CARDINALITY, C.CT_TYPE,
               C.CT_TARGET, C.CT_REMOVED, C.CT_MAPPED):
        sample = next((r for r in records if r["change_type"] == ct), None)
        print(f"\n-- {ct} --")
        print(json.dumps(sample, ensure_ascii=False, indent=2) if sample
              else "  (none emitted)")

    # Sanity check: MarketingStatus.country R4->R4B cardinality 1..1 -> 0..1
    print("\n" + "-" * 72)
    print("SANITY: MarketingStatus.country (R4->R4B) cardinality 1..1 -> 0..1 ?")
    hits = [r for r in records
            if r["pair"] == C.PAIR_R4_R4B
            and r["earlier_path"] == "MarketingStatus.country"
            and r["change_type"] == C.CT_CARDINALITY]
    ok = any(r["raw_old"] == "1..1" and r["raw_new"] == "0..1" for r in hits)
    print(f"  => {'YES' if ok else 'NO'}")
    for r in hits:
        print(json.dumps(r, ensure_ascii=False, indent=2))
    # also show every ComparisonDb record for that element, for context
    all_ms = [r for r in records
              if r["pair"] == C.PAIR_R4_R4B
              and r["earlier_path"] == "MarketingStatus.country"]
    print(f"  (all {len(all_ms)} MarketingStatus.country R4->R4B records:"
          f" {[r['change_type'] for r in all_ms]})")

    print("\n" + "-" * 72)
    print("~6 sample records (JSON):")
    step = max(1, len(records) // 6)
    for r in records[::step][:6]:
        print(json.dumps(r, ensure_ascii=False))


def main():
    records = []
    counts = Counter()
    con = sqlite3.connect(DB_URI, uri=True)
    con.row_factory = sqlite3.Row
    try:
        cur = con.cursor()
        for pair in (C.PAIR_R4_R4B, C.PAIR_R4B_R5):
            emit_pair(cur, pair, records, counts)
    finally:
        con.close()

    C.write_records(C.SRC_COMPARISON_DB, records)
    _report(records, counts)


if __name__ == "__main__":
    main()
