"""Extractor: xver-report markdown (scratch/xver-report/{r4-r4b,r4b-r5,r5-r6}.md).

Each table row maps to one assertion per set change-flag column. Establishes the
report's view of the six reconciliation dimensions with the exact Summary text.
"""
from __future__ import annotations

import os
import re

import contract as C

FILE_PAIR = {
    "r4-r4b.md": C.PAIR_R4_R4B,
    "r4b-r5.md": C.PAIR_R4B_R5,
    "r5-r6.md": C.PAIR_R5_R6,
}

DASH = "\u2014"  # em dash used for absent element


def _cell(v):
    v = (v or "").strip()
    if v in ("", DASH, "-", "\u2013"):
        return None
    return v


def _flag(v):
    return (v or "").strip().upper() == "Y"


def _split_cells(s):
    """Split a markdown table row on unescaped pipes; unescape \\| -> |."""
    s = s.strip()
    if s.startswith("|"):
        s = s[1:]
    if s.endswith("|"):
        s = s[:-1]
    parts = re.split(r"(?<!\\)\|", s)
    return [p.replace("\\|", "|").strip() for p in parts]


def _split_summary(summary):
    """Return (old, new) if the summary is an 'X -> Y' clause, else (None, summary)."""
    if not summary:
        return (None, None)
    m = re.split(r"\s*(?:\u2192|->)\s*", summary, maxsplit=1)
    if len(m) == 2:
        return (m[0].strip() or None, m[1].strip() or None)
    return (None, summary)


_CARD_RE = re.compile(r"\d+\.\.[0-9*]+")


def _card_only(v):
    """Return just the 'min..max' cardinality token from a Report summary value.

    Report packs compound/annotated text into a Cardinality cell, e.g.
    '0..*; string -> CodeableConcept; +X target', 'renamed from a.b; 0..1', or
    '0..* (warn) suspected'. Extract the first 'min..max' token so cardinality
    reconciliation compares like-for-like; fall back to the raw value if none.
    """
    if not v:
        return v
    m = _CARD_RE.search(v)
    return m.group(0) if m else v


def parse_file(path, pair, records):
    struct = None
    header = None  # column header list (to detect table start)
    with open(path, "r", encoding="utf-8") as f:
        lines = f.readlines()

    for ln, line in enumerate(lines, 1):
        s = line.rstrip("\n")
        if s.startswith("#### "):
            name = s[5:].strip()
            # strip "(renamed from X)" etc.
            struct = re.split(r"\s*\(", name, maxsplit=1)[0].strip()
            continue
        if not s.startswith("|"):
            continue
        cells = _split_cells(s)
        # header / separator rows
        if cells and cells[0] in ("Source Element", ":---", "---") or (cells and set("".join(cells)) <= set("-: ")):
            continue
        if len(cells) < 10:
            continue

        src_el = _cell(cells[0])
        tgt_el = _cell(cells[1])
        added = _flag(cells[2])
        removed = _flag(cells[3])
        renamed = _flag(cells[4])
        card = _flag(cells[5])
        typ = _flag(cells[6])
        tgt = _flag(cells[7])
        summary = _cell(cells[8])
        change_rec = _cell(cells[9])
        old, new = _split_summary(summary)
        loc = f"{os.path.basename(path)}:{ln}"

        def add(ct, ep, lp, ro=None, rn=None):
            records.append(C.rec(
                C.SRC_REPORT, pair, ct, earlier_path=ep, later_path=lp,
                raw_old=ro, raw_new=rn, detail_file=os.path.basename(path),
                detail_ref=str(ln), notes=change_rec, structure=struct))

        emitted = 0
        if added:
            add(C.CT_ADDED, None, tgt_el, None, summary); emitted += 1
        if removed:
            add(C.CT_REMOVED, src_el, None, summary, None); emitted += 1
        if renamed:
            add(C.CT_RENAMED, src_el, tgt_el, src_el, tgt_el); emitted += 1
        if card:
            add(C.CT_CARDINALITY, src_el, tgt_el, _card_only(old), _card_only(new)); emitted += 1
        if typ:
            add(C.CT_TYPE, src_el, tgt_el, old or summary, new); emitted += 1
        if tgt:
            add(C.CT_TARGET, src_el, tgt_el, old, new or summary); emitted += 1

        # path change without an explicit Renamed flag => structure-rename cascade
        if src_el and tgt_el and src_el != tgt_el and not renamed:
            records.append(C.rec(
                C.SRC_REPORT, pair, C.CT_RENAMED, earlier_path=src_el, later_path=tgt_el,
                raw_old=src_el, raw_new=tgt_el, detail_file=os.path.basename(path),
                detail_ref=str(ln), notes="path differs (structure/element rename)",
                structure=struct))
        # pure correspondence anchor when nothing else emitted
        if emitted == 0 and src_el and tgt_el:
            add(C.CT_MAPPED, src_el, tgt_el)


def main():
    records = []
    for fn, pair in FILE_PAIR.items():
        p = os.path.join(C.XVER_REPORT_DIR, fn)
        if os.path.exists(p):
            parse_file(p, pair, records)
    C.write_records(C.SRC_REPORT, records)


if __name__ == "__main__":
    main()
