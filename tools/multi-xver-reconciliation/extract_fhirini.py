"""Extractor: fhir.ini [r5-r6-changes] section (R5->R6 only).

The [r5-r6-changes] section is a hand-maintained migration ledger keyed by FHIR
element path. It is NOT valid ini for configparser (duplicate keys, ':' inside
values, free-text prose, embedded '=' and URLs would all break it), so we read
the file as text, isolate the single [r5-r6-changes] section, and classify each
`key=value` line by the value's leading operator token:

    +      key names a NEW (R6) element              -> Added   (later_path=key)
    ->     key (R5) moved/renamed elsewhere          -> Renamed (earlier_path=key)
    @path  key (R5) maps to <path>                    -> Renamed / Mapped
    (empty)key (R5) listed with no descriptor         -> Mapped  (earlier_path=key)
    prose  free-text note attached to the key (R5)    -> Comment (earlier_path=key)

Read-only: reads C.FHIR_INI only; emits via C.write_records. The sibling
[r4-r6-changes] section (R4->R6) is deliberately ignored - out of scope here.
"""
from __future__ import annotations

import json
import re

import contract as C

SECTION_HEADER = "[r5-r6-changes]"
DETAIL_FILE = "fhir.ini#r5-r6-changes"

# Leading FHIR path token right after '@' (stops at comma / space / paren / url).
_AT_PATH = re.compile(r"[A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z0-9\[\]]+)*")
# First Resource-rooted path token anywhere after '->' (per spec regex).
_ARROW_PATH = re.compile(r"[A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9\[\]]+)+")

# The five value operator buckets (for validation reporting).
BUCKETS = ["+", "->", "@", "empty", "prose"]


def read_section_lines(path):
    """Return the raw content lines of the [r5-r6-changes] section only.

    Starts after the exact `[r5-r6-changes]` header line and stops at the next
    line that is a section header (starts with '[' and ends with ']').
    """
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        text = f.read()
    out = []
    in_section = False
    for line in text.splitlines():
        stripped = line.strip()
        if not in_section:
            if stripped == SECTION_HEADER:
                in_section = True
            continue
        if stripped.startswith("[") and stripped.endswith("]"):
            break  # next section header -> stop
        out.append(line)
    return out


def classify(key, value):
    """Map one key=value line to (bucket, change_type, rec_kwargs).

    rec_kwargs holds earlier_path / later_path / raw_new / notes; the caller
    supplies the shared fields (source, pair, detail_*, structure).
    """
    v = value.strip()

    if v.startswith("+"):
        # key names the NEW (R6) element -> Added (no earlier side).
        return "+", C.CT_ADDED, dict(
            earlier_path=None, later_path=key, raw_new=v, notes=v)

    if v.startswith("->"):
        desc = v[2:].strip()
        raw_first = desc.split()[0] if desc else ""
        first = raw_first.strip(",.;:")
        remainder = desc[len(raw_first):].strip()
        is_path = bool(re.match(r"^[A-Za-z][A-Za-z0-9\[\]]*(?:\.[A-Za-z0-9\[\]]+)+$", first))
        is_ident = bool(re.match(r"^[A-Za-z][A-Za-z0-9\[\]]*$", first))
        if is_path or (is_ident and not remainder):
            resource = C.structure_of(key)
            later = first if first.split(".")[0] == resource else f"{resource}.{first}"
            return "->", C.CT_RENAMED, dict(
                earlier_path=key, later_path=later, notes=v)
        # no clean single target (conditional/prose move) -> Comment
        return "->", C.CT_COMMENT, dict(earlier_path=key, notes=v)

    if v.startswith("@"):
        m = _AT_PATH.match(v[1:].strip())
        later = m.group(0) if m else None
        if later is None:
            return "@", C.CT_COMMENT, dict(earlier_path=key, notes=v)
        ct = C.CT_RENAMED if later != key else C.CT_MAPPED
        return "@", ct, dict(
            earlier_path=key, later_path=later, notes=v)

    if v == "":
        return "empty", C.CT_MAPPED, dict(
            earlier_path=key, later_path=key,
            notes="listed in r5-r6-changes (no descriptor)")

    # Anything else is a free-text note attached to the (R5) key.
    return "prose", C.CT_COMMENT, dict(earlier_path=key, notes=v)


def build_records():
    """Parse the section into (records, buckets) parallel lists."""
    records = []
    buckets = []
    for line in read_section_lines(C.FHIR_INI):
        stripped = line.strip()
        if not stripped or stripped[0] in "#;":
            continue  # blank / comment line
        if "=" not in line:
            continue  # not a key=value assertion
        raw_key, value = line.split("=", 1)
        key = raw_key.strip()
        if not key:
            continue  # defensive: no element path -> skip
        bucket, change_type, kwargs = classify(key, value)
        records.append(C.rec(
            C.SRC_FHIR_INI, C.PAIR_R5_R6, change_type,
            detail_file=DETAIL_FILE, detail_ref=key,
            structure=C.structure_of(key), **kwargs))
        buckets.append(bucket)
    return records, buckets


def report(records, buckets):
    """Emit the validation summary required for this extractor."""
    from collections import Counter

    op_counter = Counter(buckets)
    ct_counter = Counter(r["change_type"] for r in records)

    print()
    print("=" * 70)
    print(f"{C.SRC_FHIR_INI} extractor - fhir.ini {SECTION_HEADER} ({C.PAIR_R5_R6})")
    print("=" * 70)
    print(f"total records            : {len(records)}")
    print(f"operator buckets         : "
          f"{ {b: op_counter.get(b, 0) for b in BUCKETS} }")
    print(f"by change_type           : {dict(ct_counter)}")

    def find(key):
        for r in records:
            if r["detail_ref"] == key:
                return r
        return None

    def check(label, cond):
        print(f"  [{'PASS' if cond else 'FAIL'}] {label}")

    print("\nconfirmations:")
    a = find("Account.parent")
    check("Account.parent -> Added, later_path=Account.parent",
          bool(a) and a["change_type"] == C.CT_ADDED
          and a["later_path"] == "Account.parent"
          and a["earlier_path"] is None)

    b = find("Account.relatedAccount")
    check("Account.relatedAccount -> Comment (prose 'moved to guarantor or parent...')",
          bool(b) and b["change_type"] == C.CT_COMMENT
          and b["earlier_path"] == "Account.relatedAccount")

    c = find("ActivityDefinition.dosage")
    check("ActivityDefinition.dosage -> Renamed, "
          "earlier=ActivityDefinition.dosage, later=ActivityDefinition.dosageInstruction",
          bool(c) and c["change_type"] == C.CT_RENAMED
          and c["earlier_path"] == "ActivityDefinition.dosage"
          and c["later_path"] == "ActivityDefinition.dosageInstruction")

    d = find("AdverseEvent.occurrence[x]")
    check("AdverseEvent.occurrence[x] -> Renamed, later=AdverseEvent.effect[x]",
          bool(d) and d["change_type"] == C.CT_RENAMED
          and d["later_path"] == "AdverseEvent.effect[x]")

    # Six sample records: guarantee at least one of every operator bucket.
    print("\nsample records (>=1 per operator bucket):")
    chosen = []
    for b in BUCKETS:
        for i, bk in enumerate(buckets):
            if bk == b:
                chosen.append(i)
                break
    for i in range(len(records)):  # top up to 6 with any not-yet-shown record
        if len(chosen) >= 6:
            break
        if i not in chosen:
            chosen.append(i)
    for i in chosen[:6]:
        print(f"  ({buckets[i]:>5}) {json.dumps(records[i], ensure_ascii=False)}")


def main():
    records, buckets = build_records()
    C.write_records(C.SRC_FHIR_INI, records)
    report(records, buckets)


if __name__ == "__main__":
    main()
