"""Validate staging/*.jsonl against the assertion contract. Read-only; prints a
per-source report of counts, change-type distribution, path-semantics invariant
violations, duplicates, and structure coverage. Used to vet extractor output.
"""
from __future__ import annotations

from collections import Counter, defaultdict

import contract as C

# expected path presence per change type: (earlier_required, later_required)
# None => don't care
PATH_RULES = {
    C.CT_ADDED: (False, True),        # later only (earlier must be None)
    C.CT_REMOVED: (True, False),      # earlier only
    C.CT_NOMAP: (True, False),        # earlier only
    C.CT_RENAMED: (True, True),       # both, and earlier != later
    C.CT_CARDINALITY: (True, True),
    C.CT_TYPE: (True, True),
    C.CT_TARGET: (True, True),
    C.CT_BINDING: (True, True),
    C.CT_MAPPED: (True, True),
    C.CT_COMMENT: (None, None),
}


def check(source):
    recs = C.read_records(source)
    if not recs:
        print(f"[{source}] (no staging file)")
        return
    ct = Counter(r["change_type"] for r in recs)
    pair = Counter(r["pair"] for r in recs)
    structs = {r.get("structure") for r in recs}
    dupes = Counter((r["source"], r["pair"], r["earlier_path"], r["later_path"],
                     r["change_type"], r["raw_old"], r["raw_new"]) for r in recs)
    ndupe = sum(v - 1 for v in dupes.values() if v > 1)

    violations = Counter()
    bad_pair = 0
    bad_field = 0
    for r in recs:
        # field completeness
        if any(k not in r for k in C.ASSERTION_FIELDS):
            bad_field += 1
        # pair validity
        if r["pair"] not in C.PAIRS:
            bad_pair += 1
        # applicability
        if (source, r["pair"]) not in C.APPLICABILITY:
            violations["not-applicable-pair"] += 1
        # path semantics
        rule = PATH_RULES.get(r["change_type"])
        ep, lp = r["earlier_path"], r["later_path"]
        if rule is None:
            violations["unknown-change-type"] += 1
            continue
        er, lr = rule
        if er is True and not ep:
            violations[f"{r['change_type']}:missing-earlier"] += 1
        if er is False and ep:
            violations[f"{r['change_type']}:unexpected-earlier"] += 1
        if lr is True and not lp:
            violations[f"{r['change_type']}:missing-later"] += 1
        if lr is False and lp:
            violations[f"{r['change_type']}:unexpected-later"] += 1
        if r["change_type"] == C.CT_RENAMED and ep and lp and ep == lp:
            violations["Renamed:earlier==later"] += 1

    print(f"[{source}] {len(recs)} records | structures={len(structs)} | "
          f"dupes={ndupe} | bad_field={bad_field} | bad_pair={bad_pair}")
    print(f"   pair:   {dict(pair)}")
    print(f"   change: {dict(ct)}")
    if violations:
        print(f"   VIOLATIONS: {dict(violations)}")
    # show a few top structures by record volume (spot blow-ups)
    per_struct = Counter(r.get("structure") for r in recs)
    top = per_struct.most_common(5)
    print(f"   top structures: {top}")


def main():
    for source, _ in C.SOURCES:
        check(source)


if __name__ == "__main__":
    main()
