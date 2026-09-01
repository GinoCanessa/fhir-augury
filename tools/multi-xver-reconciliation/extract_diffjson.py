"""Extractor: published-guide *.diff.json cross-version element diffs.

Two globs -> two adjacent version pairs:
  C:\\ai\\support\\fhir-r4b\\*.diff.json      -> R4->R4B  (R4B diffed against R4)
  C:\\ai\\support\\fhir-r5\\*.r4b.diff.json   -> R4B->R5  (R5 diffed against R4B)

Each per-type diff file has the shape::

    { "types": ["Patient", ...],
      "Patient": { "elements": { "<path>": {<change keys>} }, "status": "changed" } }

For every name in ``types`` we read ``doc[name]["elements"]`` and translate each
element change dict into one or more atomic assertion records.  A single element
may emit several records (rename + cardinality + type + target + binding).

The aggregate rollup files (``fhir.diff.json`` / ``fhir.r4b.diff.json``) are an
exact superset of the per-type files (verified: their per-resource content is
byte-identical to the standalone ``<name>.diff.json``), so ingesting them would
duplicate every resource record.  They are skipped by name and reported.  Each
file is parsed inside try/except so one malformed file cannot abort the run.

Read-only. No network. Python 3.13 stdlib only (json, re, glob, os).
"""
from __future__ import annotations

import glob
import json
import os
import re
from collections import Counter

import contract as C

# --------------------------------------------------------------------------
# Source globs -> pair
# --------------------------------------------------------------------------
GLOBS = [
    (os.path.join(C.SUPPORT_R4B, "*.diff.json"), C.PAIR_R4_R4B),
    (os.path.join(C.SUPPORT_R5, "*.r4b.diff.json"), C.PAIR_R4B_R5),
]

# Aggregate rollup basenames: superset of the per-type files -> skip to avoid
# emitting exact-duplicate records for every resource.
AGGREGATE_BASENAMES = {"fhir.diff.json", "fhir.r4b.diff.json"}

# --------------------------------------------------------------------------
# Element change-dict key groups
# --------------------------------------------------------------------------
CARD_KEYS = ("old-min", "new-min", "old-max", "new-max")
BINDING_FLAG_KEYS = [
    "binding-status",
    "binding-strength-changed",
    "binding-valueset-changed",
    "binding-codes-changed",
    "max-valueset-changed",
]
BINDING_KEYS = set(BINDING_FLAG_KEYS) | {"old-binding", "new-binding"}
RECOGNIZED_KEYS = (
    set(CARD_KEYS)
    | {"added-types", "removed-types"}
    | BINDING_KEYS
    | {"status", "old-name", "subtype"}
)

# Outer name of a type literal: "Reference(A | B)" -> "Reference",
# "canonical(X)" -> "canonical", "string" -> "string".
_BASE_RE = re.compile(r"^([A-Za-z0-9_]+)\s*\(")


def type_base(token):
    t = token.strip()
    m = _BASE_RE.match(t)
    return m.group(1) if m else t


def _card_side(ch, min_key, max_key):
    """Format one cardinality side as ``min..max`` using '?' for absent keys.
    Key-presence (not truthiness) is checked so a legitimate 0 min is kept."""
    mn = ch[min_key] if min_key in ch else "?"
    mx = ch[max_key] if max_key in ch else "?"
    return f"{mn}..{mx}"


def process_element(pair, basename, structure, path, ch, records, stats):
    """Translate one element change dict into atomic records. Returns count."""
    if not isinstance(ch, dict):
        stats["non_dict_change"] += 1
        return 0

    # Unrecognized keys are ignored per the contract; track them for the report.
    for k in ch:
        if k not in RECOGNIZED_KEYS:
            stats["unrecognized"][k] += 1

    status = ch.get("status")

    has_rename = "old-name" in ch
    has_card = any(k in ch for k in CARD_KEYS)
    removed_list = ch.get("removed-types") or []
    added_list = ch.get("added-types") or []
    has_types = bool(removed_list) or bool(added_list)
    binding_flags = [k for k in BINDING_FLAG_KEYS if k in ch]
    has_binding = bool(binding_flags) or ("old-binding" in ch) or ("new-binding" in ch)
    other_change = has_rename or has_card or has_types or has_binding

    def emit(change_type, ep, lp, raw_old=None, raw_new=None, notes=None):
        records.append(C.rec(
            C.SRC_DIFF_JSON, pair, change_type,
            earlier_path=ep, later_path=lp,
            raw_old=raw_old, raw_new=raw_new,
            detail_file=basename, detail_ref=path, notes=notes,
            structure=structure))
        stats["by_ct"][change_type] += 1

    # --- status: added / removed / (in-place baseline) --------------------
    if status == "new":
        emit(C.CT_ADDED, None, path)
        return 1
    if status == "deleted":
        emit(C.CT_REMOVED, path, None)
        return 1
    if status == "no-change" and not other_change:
        stats["skipped_no_change"] += 1
        return 0
    if not other_change:
        # in-place element with only unrecognized keys (e.g. modifier-only)
        stats["no_recognized_change"] += 1
        return 0

    # earlier/later baseline reused by every in-place record for this element
    if has_rename:
        old_name = ch["old-name"]
        earlier, later = old_name, path
    else:
        old_name = None
        earlier, later = path, path

    n = 0

    # --- rename -----------------------------------------------------------
    if has_rename:
        emit(C.CT_RENAMED, earlier, later, raw_old=old_name, raw_new=path)
        n += 1

    # --- cardinality ------------------------------------------------------
    if has_card:
        emit(C.CT_CARDINALITY, earlier, later,
             raw_old=_card_side(ch, "old-min", "old-max"),
             raw_new=_card_side(ch, "new-min", "new-max"))
        n += 1

    # --- types / target ---------------------------------------------------
    if has_types:
        removed_bases = set(type_base(t) for t in removed_list)
        added_bases = set(type_base(t) for t in added_list)
        # datatype SET changed (or one-sided add/remove)
        if removed_bases != added_bases:
            emit(C.CT_TYPE, earlier, later,
                 raw_old=(", ".join(removed_list) or None),
                 raw_new=(", ".join(added_list) or None))
            n += 1
        # a base present in BOTH whose parenthesized targets differ -> Target
        for b in sorted(removed_bases & added_bases):
            r_tokens = [t for t in removed_list if type_base(t) == b]
            a_tokens = [t for t in added_list if type_base(t) == b]
            if set(r_tokens) != set(a_tokens):
                emit(C.CT_TARGET, earlier, later,
                     raw_old=", ".join(r_tokens),
                     raw_new=", ".join(a_tokens))
                n += 1

    # --- binding ----------------------------------------------------------
    if has_binding:
        emit(C.CT_BINDING, earlier, later,
             raw_old=(json.dumps(ch["old-binding"], ensure_ascii=False)
                      if "old-binding" in ch else None),
             raw_new=(json.dumps(ch["new-binding"], ensure_ascii=False)
                      if "new-binding" in ch else None),
             notes=(", ".join(binding_flags) if binding_flags else None))
        n += 1

    return n


def parse_file(fpath, pair, records, stats):
    """Parse one per-type diff file (BOM-tolerant). Raises on shape failure."""
    with open(fpath, encoding="utf-8-sig") as f:
        doc = json.load(f)
    if not isinstance(doc, dict) or not isinstance(doc.get("types"), list):
        raise ValueError("missing 'types' list (not per-type shape)")
    basename = os.path.basename(fpath)
    for name in doc["types"]:
        obj = doc.get(name)
        if not isinstance(obj, dict):
            stats["missing_type"] += 1
            continue
        elements = obj.get("elements", {})
        if not isinstance(elements, dict):
            continue
        for epath, ch in elements.items():
            process_element(pair, basename, name, epath, ch, records, stats)


def main():
    records = []
    stats = {
        "by_ct": Counter(),
        "unrecognized": Counter(),
        "skipped_no_change": 0,
        "no_recognized_change": 0,
        "non_dict_change": 0,
        "missing_type": 0,
    }
    files_parsed = Counter()      # folder -> parsed file count
    files_skipped = []            # (basename, reason)

    for pattern, pair in GLOBS:
        folder = os.path.basename(os.path.dirname(pattern))
        for fpath in sorted(glob.glob(pattern)):
            basename = os.path.basename(fpath)
            low = basename.lower()

            if low in AGGREGATE_BASENAMES:
                files_skipped.append(
                    (basename, "aggregate rollup (exact superset of per-type files)"))
                continue
            # R4->R4B glob must use only plain <name>.diff.json (no version infix)
            if pair == C.PAIR_R4_R4B and (".r4." in low or ".r4b." in low):
                files_skipped.append(
                    (basename, "unexpected version infix for R4->R4B glob"))
                continue

            checkpoint = len(records)
            try:
                parse_file(fpath, pair, records, stats)
            except Exception as e:  # noqa: BLE001 - report and continue
                del records[checkpoint:]  # roll back partial records from this file
                files_skipped.append(
                    (basename, f"parse/shape error: {type(e).__name__}: {e}"))
                continue
            files_parsed[folder] += 1

    C.write_records(C.SRC_DIFF_JSON, records)

    # ----------------------------------------------------------------------
    # Validation report
    # ----------------------------------------------------------------------
    by_pair = Counter(r["pair"] for r in records)
    by_ct = Counter(r["change_type"] for r in records)

    print("\n================= DiffJson extractor validation =================")
    print(f"total records: {len(records)}")

    print("\n-- records by pair --")
    for pair in C.PAIRS:
        if by_pair.get(pair):
            print(f"  {pair:10s}: {by_pair[pair]}")

    print("\n-- records by change_type --")
    for ct in [C.CT_ADDED, C.CT_REMOVED, C.CT_RENAMED, C.CT_CARDINALITY,
               C.CT_TYPE, C.CT_TARGET, C.CT_BINDING]:
        print(f"  {ct:12s}: {by_ct.get(ct, 0)}")

    print("\n-- files parsed per folder --")
    for folder, cnt in files_parsed.items():
        print(f"  {folder}: {cnt}")

    print(f"\n-- files skipped: {len(files_skipped)} --")
    for basename, reason in files_skipped:
        print(f"  {basename}: {reason}")

    print("\n-- element bookkeeping --")
    print(f"  no-change elements skipped        : {stats['skipped_no_change']}")
    print(f"  in-place, no recognized change    : {stats['no_recognized_change']}")
    print(f"  non-dict change values            : {stats['non_dict_change']}")
    print(f"  types entries missing type object : {stats['missing_type']}")
    if stats["unrecognized"]:
        seen = ", ".join(f"{k}={v}" for k, v in stats["unrecognized"].most_common())
        print(f"  unrecognized keys (ignored)       : {seen}")

    # ----------------------------------------------------------------------
    # One sample per change type, preferring the R4B->R5 (*.r4b.diff.json) set.
    # ----------------------------------------------------------------------
    def find_sample(change_type):
        for r in records:  # prefer the r4b diffs (R4B->R5)
            if r["change_type"] == change_type and r["detail_file"].endswith(".r4b.diff.json"):
                return r
        for r in records:
            if r["change_type"] == change_type:
                return r
        return None

    print("\n-- one sample per change type (from the r4b diffs where present) --")
    wanted = [
        ("Cardinality", C.CT_CARDINALITY),
        ("Type", C.CT_TYPE),
        ("Target", C.CT_TARGET),
        ("Renamed (old-name)", C.CT_RENAMED),
        ("Added", C.CT_ADDED),
        ("Removed", C.CT_REMOVED),
        ("Binding", C.CT_BINDING),
    ]
    for label, ct in wanted:
        sample = find_sample(ct)
        print(f"\n### {label}")
        if sample is None:
            print("  (none found)")
        else:
            print(json.dumps(sample, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
