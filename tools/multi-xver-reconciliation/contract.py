"""Shared contract for the multi-xver reconciliation ETL.

Every source extractor imports this module, produces a list of assertion
records (plain dicts) following ASSERTION_FIELDS, and calls write_records().
The integrator loads all staging JSONL, resolves correspondences, and builds
the spine (ElementChanges) + ChangeSignals in cache/multi-xver-analysis.db.

Nothing here touches the source data or existing databases. Read-only inputs.
"""
from __future__ import annotations

import json
import os
import re

# --------------------------------------------------------------------------
# Paths.  This tool lives at <repo>/tools/multi-xver-reconciliation/.
#
# Writable outputs (git-ignored):
#   OUT_DB       the consolidated reconciliation database (cache/)
#   STAGING_DIR  per-source intermediate JSONL (tool-local staging/)
#
# Read-only source inputs default to their canonical local locations but can
# each be overridden with an environment variable, so the tool is portable to
# another checkout/machine without editing this file.
# --------------------------------------------------------------------------
TOOL_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(TOOL_DIR, os.pardir, os.pardir))


def _env(name, default):
    """Environment override (MXVER_*), falling back to the canonical default."""
    v = os.environ.get(name)
    return v if v else default


# Writable outputs (both git-ignored: cache/ at repo root, staging/ tool-local)
OUT_DB = _env("MXVER_OUT_DB", os.path.join(REPO_ROOT, "cache", "multi-xver-analysis.db"))
STAGING_DIR = _env("MXVER_STAGING_DIR", os.path.join(TOOL_DIR, "staging"))

# Read-only source inputs
XVER_REPORT_DIR = _env("MXVER_XVER_REPORT_DIR", os.path.join(REPO_ROOT, "scratch", "xver-report"))
XVER_INPUT = _env("MXVER_XVER_INPUT", r"C:\git\fhir-cross-version\input")
FHIR_INI = _env("MXVER_FHIR_INI", r"C:\specs\fhir\source\fhir.ini")
SUPPORT_R4 = _env("MXVER_SUPPORT_R4", r"C:\ai\support\fhir-r4")
SUPPORT_R4B = _env("MXVER_SUPPORT_R4B", r"C:\ai\support\fhir-r4b")
SUPPORT_R5 = _env("MXVER_SUPPORT_R5", r"C:\ai\support\fhir-r5")
COMPARISON_DB = _env("MXVER_COMPARISON_DB", r"C:\git\fhir-cross-version\temp\fhir-comparison.sqlite")

# --------------------------------------------------------------------------
# Version pairs (the 3 adjacent report pairs) - canonical names
# --------------------------------------------------------------------------
PAIR_R4_R4B = "R4->R4B"
PAIR_R4B_R5 = "R4B->R5"
PAIR_R5_R6 = "R5->R6"
PAIRS = [PAIR_R4_R4B, PAIR_R4B_R5, PAIR_R5_R6]

# earlier/later short sequence label per pair
PAIR_SEQ = {
    PAIR_R4_R4B: ("R4", "R4B"),
    PAIR_R4B_R5: ("R4B", "R5"),
    PAIR_R5_R6: ("R5", "R6"),
}
PAIR_PKG = {
    PAIR_R4_R4B: ("4.0.1", "4.3.0"),
    PAIR_R4B_R5: ("4.3.0", "5.0.0"),
    PAIR_R5_R6: ("5.0.0", "6.0.0-ballot4"),
}

# --------------------------------------------------------------------------
# Sources
# --------------------------------------------------------------------------
SRC_REPORT = "Report"          # scratch/xver-report/*.md
SRC_ELEMENT_MAP = "ElementMap"  # input/elements/ConceptMap-elements-*.json
SRC_RESOURCE_MAP = "ResourceMap"  # input/resources/ConceptMap-resources-*.json
SRC_TYPE_MAP = "TypeMap"        # input/types/ConceptMap-types-*.json
SRC_FML = "Fml"                # input/R4BtoR5/*.fml
SRC_FHIR_INI = "FhirIni"       # fhir.ini [r5-r6-changes]
SRC_DIFF_JSON = "DiffJson"     # support/fhir-*/*.diff.json
SRC_COMPARISON_DB = "ComparisonDb"  # fhir-comparison.sqlite

SOURCES = [
    (SRC_REPORT, "xver-report markdown change tables (the tool this session built)"),
    (SRC_ELEMENT_MAP, "FHIR ConceptMap element-name maps (input/elements)"),
    (SRC_RESOURCE_MAP, "FHIR ConceptMap resource-name maps (input/resources)"),
    (SRC_TYPE_MAP, "FHIR ConceptMap type-name maps (input/types)"),
    (SRC_FML, "FHIR Mapping Language StructureMaps (input/R4BtoR5)"),
    (SRC_FHIR_INI, "fhir.ini [r5-r6-changes] section"),
    (SRC_DIFF_JSON, "published-guide fhir.*.diff.json files"),
    (SRC_COMPARISON_DB, "local xver-analysis pipeline fhir-comparison.sqlite"),
]

# Which (source, pair) combinations are APPLICABLE (source can cover the pair).
# 1 => applicable, absence => not applicable (N/A). Drives tri-state indicators.
# Detail = the concrete files/section that back the applicability.
APPLICABILITY = {
    (SRC_REPORT, PAIR_R4_R4B): "r4-r4b.md",
    (SRC_REPORT, PAIR_R4B_R5): "r4b-r5.md",
    (SRC_REPORT, PAIR_R5_R6): "r5-r6.md",

    (SRC_ELEMENT_MAP, PAIR_R4_R4B): "ConceptMap-elements-4to4B.json",
    (SRC_ELEMENT_MAP, PAIR_R4B_R5): "ConceptMap-elements-4Bto5.json",

    (SRC_RESOURCE_MAP, PAIR_R4_R4B): "ConceptMap-resources-4to4B.json",
    (SRC_RESOURCE_MAP, PAIR_R4B_R5): "ConceptMap-resources-4Bto5.json",

    (SRC_TYPE_MAP, PAIR_R4_R4B): "ConceptMap-types-4to4B.json",
    (SRC_TYPE_MAP, PAIR_R4B_R5): "ConceptMap-types-4Bto5.json",

    (SRC_FML, PAIR_R4B_R5): "input/R4BtoR5/*.fml",

    (SRC_FHIR_INI, PAIR_R5_R6): "fhir.ini#r5-r6-changes",

    (SRC_DIFF_JSON, PAIR_R4_R4B): "support/fhir-r4b/*.diff.json",
    (SRC_DIFF_JSON, PAIR_R4B_R5): "support/fhir-r5/*.r4b.diff.json",

    (SRC_COMPARISON_DB, PAIR_R4_R4B): "StructureComparisons R4->R4B",
    (SRC_COMPARISON_DB, PAIR_R4B_R5): "StructureComparisons R4B->R5",
}

# --------------------------------------------------------------------------
# Change types
# --------------------------------------------------------------------------
CT_ADDED = "Added"            # exists only in later version
CT_REMOVED = "Removed"        # exists only in earlier version
CT_RENAMED = "Renamed"        # path differs earlier -> later
CT_CARDINALITY = "Cardinality"  # min/max changed
CT_TYPE = "Type"             # datatype set changed (non-Reference/canonical)
CT_TARGET = "Target"         # Reference()/canonical() target types changed
CT_BINDING = "Binding"       # value-set binding changed (auxiliary; diff.json)
CT_MAPPED = "Mapped"         # asserted correspondence, no (further) change
CT_NOMAP = "NoMap"           # asserted to have no mapping
CT_COMMENT = "Comment"       # free-text note attached to a path

# The 6 flags that form the spine reconciliation dimensions.
SPINE_FLAG_TYPES = [CT_ADDED, CT_REMOVED, CT_RENAMED, CT_CARDINALITY, CT_TYPE, CT_TARGET]

ASSERTION_FIELDS = [
    "source", "pair", "structure",
    "earlier_path", "later_path",
    "change_type", "raw_old", "raw_new",
    "relationship", "detail_file", "detail_ref", "notes",
]

_WS = re.compile(r"\s+")


def canon(path):
    """Trim + collapse internal whitespace. Case preserved (paths are case
    sensitive). Returns None for falsy input."""
    if path is None:
        return None
    p = _WS.sub(" ", str(path).strip())
    return p or None


def structure_of(path):
    """Owning structure/resource name = the token before the first dot.
    For bare structure names (resource/type maps) returns the name itself."""
    p = canon(path)
    if not p:
        return None
    return p.split(".", 1)[0].split("[", 1)[0].strip() or None


def choice_base(path):
    """Loose key for polymorphic elements: strip a trailing [x] and lowercase.
    Used only for fuzzy correspondence linking, never for row identity."""
    p = canon(path)
    if not p:
        return None
    p = re.sub(r"\[x\]$", "", p)
    return p.lower()


def rec(source, pair, change_type, earlier_path=None, later_path=None,
        raw_old=None, raw_new=None, relationship=None,
        detail_file=None, detail_ref=None, notes=None, structure=None):
    """Build one assertion record (dict) with canonicalized paths."""
    ep = canon(earlier_path)
    lp = canon(later_path)
    st = canon(structure) or structure_of(ep) or structure_of(lp)
    return {
        "source": source,
        "pair": pair,
        "structure": st,
        "earlier_path": ep,
        "later_path": lp,
        "change_type": change_type,
        "raw_old": (None if raw_old is None else str(raw_old)),
        "raw_new": (None if raw_new is None else str(raw_new)),
        "relationship": relationship,
        "detail_file": detail_file,
        "detail_ref": (None if detail_ref is None else str(detail_ref)),
        "notes": notes,
    }


def write_records(source, records):
    """Write records to <tool>/staging/<source>.jsonl."""
    os.makedirs(STAGING_DIR, exist_ok=True)
    out = os.path.join(STAGING_DIR, f"{source}.jsonl")
    n = 0
    with open(out, "w", encoding="utf-8") as f:
        for r in records:
            # guard: only known fields, all present
            row = {k: r.get(k) for k in ASSERTION_FIELDS}
            f.write(json.dumps(row, ensure_ascii=False) + "\n")
            n += 1
    print(f"[{source}] wrote {n} records -> {out}")
    return n


def read_records(source):
    path = os.path.join(STAGING_DIR, f"{source}.jsonl")
    if not os.path.exists(path):
        return []
    with open(path, "r", encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


# Source -> spine tri-state indicator column (NULL=N/A, 0=absent, 1=present)
IN_COL = {
    SRC_REPORT: "InReport",
    SRC_ELEMENT_MAP: "InElementMap",
    SRC_RESOURCE_MAP: "InResourceMap",
    SRC_TYPE_MAP: "InTypeMap",
    SRC_FML: "InFml",
    SRC_FHIR_INI: "InFhirIni",
    SRC_DIFF_JSON: "InDiffJson",
    SRC_COMPARISON_DB: "InComparisonDb",
}


def applicable_sources(pair):
    """Sources applicable to a pair (per APPLICABILITY)."""
    return [s for (s, p) in APPLICABILITY if p == pair]
