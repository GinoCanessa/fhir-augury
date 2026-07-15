"""Extractor: FHIR ConceptMap name maps (elements / resources / types).

Reads the six adjacent-pair ConceptMap JSON files (R4->R4B, R4B->R5) for the
element, resource, and type name maps and emits one atomic assertion per
target -- Renamed when the code changes, Mapped when the correspondence is
asserted unchanged -- plus one NoMap assertion per element flagged noMap.

Read-only: only the six named inputs are read; output goes through the shared
contract's staging writer. Stdlib only (json). Throwaway reconciliation ETL.
"""
from __future__ import annotations

import json
import os
from collections import Counter

import contract as C

# (source_const, pair, (subdir, filename)) -- exactly the six adjacent-pair
# maps for the two covered pairs. Other-direction and *.unused files are never
# referenced here.
SPECS = [
    (C.SRC_ELEMENT_MAP,  C.PAIR_R4_R4B, ("elements",  "ConceptMap-elements-4to4B.json")),
    (C.SRC_ELEMENT_MAP,  C.PAIR_R4B_R5, ("elements",  "ConceptMap-elements-4Bto5.json")),
    (C.SRC_RESOURCE_MAP, C.PAIR_R4_R4B, ("resources", "ConceptMap-resources-4to4B.json")),
    (C.SRC_RESOURCE_MAP, C.PAIR_R4B_R5, ("resources", "ConceptMap-resources-4Bto5.json")),
    (C.SRC_TYPE_MAP,     C.PAIR_R4_R4B, ("types",     "ConceptMap-types-4to4B.json")),
    (C.SRC_TYPE_MAP,     C.PAIR_R4B_R5, ("types",     "ConceptMap-types-4Bto5.json")),
]

# Sources whose element codes are bare structure names (no dotted path); for
# these there is no leading token to derive `structure` from, so we pass it.
BARE_NAME_SOURCES = {C.SRC_RESOURCE_MAP, C.SRC_TYPE_MAP}


def _real_basename(path):
    """Case-corrected on-disk file name (Windows paths are case-insensitive)."""
    d, b = os.path.split(path)
    try:
        for name in os.listdir(d):
            if name.lower() == b.lower():
                return name
    except OSError:
        pass
    return b


def parse_file(path, source, pair, records):
    """Append assertion records for one ConceptMap file; return count added."""
    with open(path, "r", encoding="utf-8") as f:
        cm = json.load(f)
    detail_file = _real_basename(path)
    bare = source in BARE_NAME_SOURCES
    start = len(records)

    for gi, group in enumerate(cm.get("group") or []):
        for ei, el in enumerate(group.get("element") or []):
            code = el.get("code")
            structure = code if bare else None
            raw_targets = el.get("target") or []
            usable = [t for t in raw_targets if t.get("code")]

            # Fan-out: one atomic record per (usable) target, original index.
            for ti, t in enumerate(raw_targets):
                tcode = t.get("code")
                if not tcode:
                    continue
                ct = C.CT_RENAMED if tcode != code else C.CT_MAPPED
                records.append(C.rec(
                    source, pair, ct,
                    earlier_path=code, later_path=tcode,
                    relationship=t.get("relationship"),
                    detail_file=detail_file, detail_ref=f"group{gi}/el{ei}/t{ti}",
                    structure=structure))

            # Asserted absence of any mapping (only when no usable target).
            if el.get("noMap") is True and not usable:
                records.append(C.rec(
                    source, pair, C.CT_NOMAP,
                    earlier_path=code, later_path=None,
                    detail_file=detail_file, detail_ref=f"group{gi}/el{ei}/noMap",
                    structure=structure))

    return len(records) - start


# --------------------------------------------------------------------------
# Driver + self-validation report
# --------------------------------------------------------------------------

def _find(records, **crit):
    for r in records:
        if all(r.get(k) == v for k, v in crit.items()):
            return r
    return None


def main():
    by_source = {C.SRC_ELEMENT_MAP: [], C.SRC_RESOURCE_MAP: [], C.SRC_TYPE_MAP: []}
    per_file = []   # (source, pair, basename, exists, count)
    missing = []

    for source, pair, (sub, fn) in SPECS:
        path = os.path.join(C.XVER_INPUT, sub, fn)
        if not os.path.exists(path):
            per_file.append((source, pair, fn, False, 0))
            missing.append(fn)
            continue
        n = parse_file(path, source, pair, by_source[source])
        per_file.append((source, pair, _real_basename(path), True, n))

    element_records = by_source[C.SRC_ELEMENT_MAP]
    resource_records = by_source[C.SRC_RESOURCE_MAP]
    type_records = by_source[C.SRC_TYPE_MAP]

    C.write_records(C.SRC_ELEMENT_MAP, element_records)
    C.write_records(C.SRC_RESOURCE_MAP, resource_records)
    C.write_records(C.SRC_TYPE_MAP, type_records)

    allrecs = element_records + resource_records + type_records

    # ---- report -------------------------------------------------------
    print("\n================ ConceptMap extractor report ================")
    print("\nPer source file (source | pair | file | records):")
    for source, pair, fn, exists, n in per_file:
        tag = "" if exists else "  <-- MISSING (skipped)"
        print(f"  {source:<12} {pair:<9} {fn:<40} {n:>4}{tag}")

    print("\nPer emitted source constant:")
    print(f"  {C.SRC_ELEMENT_MAP:<12} (ElementMap.jsonl) : {len(element_records)}")
    print(f"  {C.SRC_RESOURCE_MAP:<12} (ResourceMap.jsonl): {len(resource_records)}")
    print(f"  {C.SRC_TYPE_MAP:<12} (TypeMap.jsonl)    : {len(type_records)}")
    print(f"  TOTAL: {len(allrecs)}")

    by_pair = Counter(r["pair"] for r in allrecs)
    by_ct = Counter(r["change_type"] for r in allrecs)
    by_pair_ct = Counter((r["pair"], r["change_type"]) for r in allrecs)
    print("\nCounter by pair:", dict(by_pair))
    print("Counter by change_type:", dict(by_ct))
    print("Counter by (pair, change_type):")
    for key in sorted(by_pair_ct):
        print(f"    {key}: {by_pair_ct[key]}")

    print("\nFiles missing:", missing if missing else "(none)")

    # ---- validation ---------------------------------------------------
    print("\n---------------- validation ----------------")
    ap = _find(allrecs, source=C.SRC_ELEMENT_MAP, pair=C.PAIR_R4B_R5,
               earlier_path="Account.partOf",
               later_path="Account.relatedAccount.account")
    ok_ap = bool(ap) and ap["change_type"] == C.CT_RENAMED and \
        ap["relationship"] == "source-is-narrower-than-target"
    print(f"[{'PASS' if ok_ap else 'FAIL'}] Account.partOf -> Account.relatedAccount.account "
          f"is Renamed w/ 'source-is-narrower-than-target'")
    if ap:
        print("        " + json.dumps(ap, ensure_ascii=False))

    # Task example element "AdverseEvent.contributor" -- verify presence.
    contrib = _find(allrecs, source=C.SRC_ELEMENT_MAP, pair=C.PAIR_R4B_R5,
                    earlier_path="AdverseEvent.contributor")
    if contrib is None:
        print("[NOTE] 'AdverseEvent.contributor' is NOT present in ConceptMap-elements-4Bto5.json; "
              "confirming NoMap via the file's actual noMap elements instead.")

    nomap_elems = [r for r in element_records
                   if r["pair"] == C.PAIR_R4B_R5 and r["change_type"] == C.CT_NOMAP]
    org = _find(element_records, change_type=C.CT_NOMAP, earlier_path="Consent.organization")
    ok_nomap = bool(org) and org["later_path"] is None
    print(f"[{'PASS' if ok_nomap else 'FAIL'}] a noMap element appears as NoMap "
          f"(earlier set, later None). 4Bto5 noMap elements: "
          f"{[r['earlier_path'] for r in nomap_elems]}")
    if org:
        print("        " + json.dumps(org, ensure_ascii=False))

    # ---- 5 representative samples (span all 3 sources & 3 change types) ----
    print("\n---------------- 5 sample records ----------------")
    samples = [
        _find(allrecs, source=C.SRC_ELEMENT_MAP, earlier_path="Account.partOf"),                       # Renamed (element)
        _find(allrecs, source=C.SRC_ELEMENT_MAP, change_type=C.CT_NOMAP, earlier_path="Consent.organization"),  # NoMap (element)
        _find(allrecs, source=C.SRC_ELEMENT_MAP, earlier_path="Consent.performer", later_path="Consent.grantee"),  # fan-out target t1
        _find(allrecs, source=C.SRC_RESOURCE_MAP, change_type=C.CT_MAPPED, earlier_path="Account"),     # Mapped (resource, bare structure)
        _find(allrecs, source=C.SRC_TYPE_MAP, change_type=C.CT_MAPPED, earlier_path="string"),          # Mapped (type, bare structure)
    ]
    for s in samples:
        print("  " + json.dumps(s, ensure_ascii=False))


if __name__ == "__main__":
    main()
