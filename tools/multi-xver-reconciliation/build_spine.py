"""Integrator: load all staging/*.jsonl into cache/multi-xver-analysis.db,
resolve element correspondences (union-find), build the ElementChanges spine
with tri-state per-source indicators, and the normalized ChangeSignals matrix.

Run AFTER create_schema.py and after the extractors have written staging.
Idempotent w.r.t. data tables (clears RawAssertions/Correspondences/
ElementChanges/ChangeSignals before loading). Never touches source data.
"""
from __future__ import annotations

import sqlite3
from collections import Counter, defaultdict

import contract as C

# NoMap (a ConceptMap element with no forward target) is treated as a Removed
# signal for spine flag purposes, while its distinct NoMap provenance is kept
# in ChangeSignals. See notes / repository memory.
NOMAP_IMPLIES_REMOVED = True

# Only these assertion types constitute a "change" and thus create a spine row
# / set an InX=1 indicator. Mapped and Comment are recorded in ChangeSignals as
# counter-evidence but never create a row nor mark a source as having caught a
# change (a source that calls an element 'equivalent' did NOT capture a change).
REAL_CHANGE_TYPES = set(C.SPINE_FLAG_TYPES) | {C.CT_BINDING, C.CT_NOMAP}

DATA_TABLES = ["ChangeSignals", "ElementChanges", "Correspondences", "RawAssertions"]


class UF:
    def __init__(self):
        self.p = {}

    def find(self, x):
        self.p.setdefault(x, x)
        root = x
        while self.p[root] != root:
            root = self.p[root]
        while self.p[x] != root:
            self.p[x], x = root, self.p[x]
        return root

    def union(self, a, b):
        ra, rb = self.find(a), self.find(b)
        if ra != rb:
            self.p[rb] = ra


def _node(side, path):
    return f"{side}\u0001{path}"


def flags_for(change_types):
    """Return dict of the 7 Is* spine flags from a set of change types."""
    ct = set(change_types)
    removed = (C.CT_REMOVED in ct) or (NOMAP_IMPLIES_REMOVED and C.CT_NOMAP in ct)
    return {
        "IsAdded": int(C.CT_ADDED in ct),
        "IsRemoved": int(removed),
        "IsRenamed": int(C.CT_RENAMED in ct),
        "IsCardinalityChanged": int(C.CT_CARDINALITY in ct),
        "IsTypeChanged": int(C.CT_TYPE in ct),
        "IsTargetChanged": int(C.CT_TARGET in ct),
        "IsBindingChanged": int(C.CT_BINDING in ct),
    }


def corr_kind(earlier_paths, later_paths):
    ne, nl = len(earlier_paths), len(later_paths)
    if ne == 0:
        return "Added"
    if nl == 0:
        return "Removed"
    if ne == 1 and nl == 1:
        return "InPlace" if next(iter(earlier_paths)) == next(iter(later_paths)) else "Renamed"
    if ne == 1 and nl > 1:
        return "FanOut"
    if ne > 1 and nl == 1:
        return "FanIn"
    return "Complex"


def main():
    con = sqlite3.connect(C.OUT_DB)
    con.execute("PRAGMA foreign_keys=ON")
    cur = con.cursor()

    for t in DATA_TABLES:
        cur.execute(f"DELETE FROM {t}")

    srckey = {n: k for (k, n) in cur.execute("SELECT SourceKey,SourceName FROM Sources")}
    pairkey = {n: k for (k, n) in cur.execute("SELECT PairKey,PairName FROM VersionPairs")}

    # ---- 1. Load every staging assertion into memory (assign AssertionKey) ----
    assertions = []  # list of dicts incl. 'akey'
    akey = 0
    src_counts = Counter()
    seen = set()
    dropped_dupes = 0
    for src, _desc in C.SOURCES:
        for r in C.read_records(src):
            sig = (r["source"], r["pair"], r.get("earlier_path"), r.get("later_path"),
                   r["change_type"], r.get("raw_old"), r.get("raw_new"),
                   r.get("detail_ref"))
            if sig in seen:
                dropped_dupes += 1
                continue
            seen.add(sig)
            akey += 1
            r["akey"] = akey
            assertions.append(r)
            src_counts[src] += 1

    if not assertions:
        print("No staging records found. Did the extractors run?")
        con.close()
        return

    # Insert RawAssertions (ChangeKey back-filled later).
    cur.executemany(
        """INSERT INTO RawAssertions
           (AssertionKey,SourceKey,PairKey,Structure,EarlierPath,LaterPath,
            ChangeType,RawOld,RawNew,Relationship,DetailFile,DetailRef,Notes,ChangeKey)
           VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,NULL)""",
        [(a["akey"], srckey[a["source"]], pairkey[a["pair"]], a.get("structure"),
          a.get("earlier_path"), a.get("later_path"), a["change_type"],
          a.get("raw_old"), a.get("raw_new"), a.get("relationship"),
          a.get("detail_file"), a.get("detail_ref"), a.get("notes"))
         for a in assertions])

    # ---- 2. Per pair: union-find correspondences + spine rows ----
    by_pair = defaultdict(list)
    for a in assertions:
        by_pair[a["pair"]].append(a)

    corr_rows = []          # (CorrespondenceKey,PairKey,Kind,EarlierPaths,LaterPaths,RowCount)
    ec_rows = []            # ElementChanges tuples
    sig_rows = []           # ChangeSignals tuples
    change_key = 0
    corr_key = 0
    sig_key = 0

    for pair, alist in by_pair.items():
        pk = pairkey[pair]
        applicable = set(C.applicable_sources(pair))
        applicable_count = len(applicable)

        # union-find over side-tagged path nodes
        uf = UF()
        for a in alist:
            ep, lp = a.get("earlier_path"), a.get("later_path")
            if ep is not None:
                uf.find(_node("E", ep))
            if lp is not None:
                uf.find(_node("L", lp))
            if ep is not None and lp is not None:
                uf.union(_node("E", ep), _node("L", lp))

        # A spine row exists ONLY for a (earlier,later) tuple that >=1 source
        # asserts is a REAL change. Mapped/Comment assertions never create a row;
        # they attach as counter-evidence when they land on a change tuple.
        rows = defaultdict(list)            # tuple -> ALL assertions on it
        change_tuples = set()
        for a in alist:
            t = (a.get("earlier_path"), a.get("later_path"))
            rows[t].append(a)
            if a["change_type"] in REAL_CHANGE_TYPES:
                change_tuples.add(t)

        # assign a CorrespondenceKey per union-find root (in this pair)
        root_to_corr = {}
        comp_earlier = defaultdict(set)
        comp_later = defaultdict(set)
        comp_rowcount = Counter()

        def row_root(ep, lp):
            if ep is not None:
                return uf.find(_node("E", ep))
            return uf.find(_node("L", lp))

        ordered = sorted(change_tuples, key=lambda t: (t[0] or "", t[1] or ""))

        # first pass: allocate corr keys + gather component path sets (change tuples only)
        for (ep, lp) in ordered:
            root = row_root(ep, lp)
            if root not in root_to_corr:
                corr_key += 1
                root_to_corr[root] = corr_key
            ck = root_to_corr[root]
            if ep is not None:
                comp_earlier[ck].add(ep)
            if lp is not None:
                comp_later[ck].add(lp)
            comp_rowcount[ck] += 1

        # second pass: emit one spine row per change tuple
        for (ep, lp) in ordered:
            change_key += 1
            agroup = rows[(ep, lp)]
            real = [a for a in agroup if a["change_type"] in REAL_CHANGE_TYPES]
            ck = root_to_corr[row_root(ep, lp)]
            fl = flags_for(a["change_type"] for a in real)

            # tri-state per-source indicators: 1 only if the source asserts a
            # REAL change on this tuple; a Mapped-only source stays 0.
            change_sources = {a["source"] for a in real}
            indicators = {}
            present_count = 0
            for src in C.IN_COL:
                col = C.IN_COL[src]
                if src not in applicable:
                    indicators[col] = None
                elif src in change_sources:
                    indicators[col] = 1
                    present_count += 1
                else:
                    indicators[col] = 0
            disagree = 1 if 0 < present_count < applicable_count else 0

            structure = (real[0].get("structure") if real else None) \
                or C.structure_of(ep) or C.structure_of(lp)
            ec_rows.append((
                change_key, pk, ck, structure, ep, lp,
                fl["IsAdded"], fl["IsRemoved"], fl["IsRenamed"],
                fl["IsCardinalityChanged"], fl["IsTypeChanged"],
                fl["IsTargetChanged"], fl["IsBindingChanged"],
                indicators["InReport"], indicators["InElementMap"],
                indicators["InResourceMap"], indicators["InTypeMap"],
                indicators["InFml"], indicators["InFhirIni"],
                indicators["InDiffJson"], indicators["InComparisonDb"],
                present_count, applicable_count, disagree,
            ))

            # backfill ChangeKey onto every assertion on this tuple (incl. Mapped)
            for a in agroup:
                a["change_key"] = change_key

            # ChangeSignals: one per (source, changeType) over ALL assertions on
            # the tuple, so Mapped/Comment counter-evidence is retained.
            byst = defaultdict(list)
            for a in agroup:
                byst[(a["source"], a["change_type"])].append(a)
            for (src, ct), group in byst.items():
                rep = group[0]
                olds = sorted({g["raw_old"] for g in group if g.get("raw_old")})
                news = sorted({g["raw_new"] for g in group if g.get("raw_new")})
                sig_key += 1
                sig_rows.append((
                    sig_key, change_key, srckey[src], ct,
                    " ; ".join(olds) or None, " ; ".join(news) or None,
                    rep.get("relationship"), rep["akey"], rep.get("notes"),
                ))

        # emit correspondence summary rows for this pair (change tuples only)
        for ck in sorted(set(root_to_corr.values())):
            eps = comp_earlier.get(ck, set())
            lps = comp_later.get(ck, set())
            corr_rows.append((
                ck, pk, corr_kind(eps, lps),
                "|".join(sorted(eps)) or None,
                "|".join(sorted(lps)) or None,
                comp_rowcount[ck],
            ))

    # ---- 3. Persist spine + correspondences + signals; backfill ChangeKey ----
    cur.executemany(
        """INSERT INTO Correspondences
           (CorrespondenceKey,PairKey,Kind,EarlierPaths,LaterPaths,RowCount)
           VALUES(?,?,?,?,?,?)""", corr_rows)
    cur.executemany(
        """INSERT INTO ElementChanges
           (ChangeKey,PairKey,CorrespondenceKey,Structure,EarlierPath,LaterPath,
            IsAdded,IsRemoved,IsRenamed,IsCardinalityChanged,IsTypeChanged,
            IsTargetChanged,IsBindingChanged,
            InReport,InElementMap,InResourceMap,InTypeMap,InFml,InFhirIni,
            InDiffJson,InComparisonDb,
            PresentSourceCount,ApplicableSourceCount,DisagreementFlag)
           VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""", ec_rows)
    cur.executemany(
        """INSERT INTO ChangeSignals
           (SignalKey,ChangeKey,SourceKey,ChangeType,RawOld,RawNew,
            Relationship,AssertionKey,Notes)
           VALUES(?,?,?,?,?,?,?,?,?)""", sig_rows)
    cur.executemany(
        "UPDATE RawAssertions SET ChangeKey=? WHERE AssertionKey=?",
        [(a["change_key"], a["akey"]) for a in assertions if "change_key" in a])

    con.commit()

    # ---- 4. Diagnostics ----
    print("Loaded assertions by source:")
    for src, _ in C.SOURCES:
        print(f"  {src:14} {src_counts.get(src,0):>7}")
    print(f"  {'TOTAL':14} {sum(src_counts.values()):>7}  (exact dupes dropped: {dropped_dupes})")
    print(f"\nSpine rows (ElementChanges): {len(ec_rows)}")
    print(f"Correspondences:             {len(corr_rows)}")
    print(f"ChangeSignals:               {len(sig_rows)}")

    print("\nSpine rows per pair x change flag:")
    q = """SELECT vp.PairName,
                  SUM(IsAdded),SUM(IsRemoved),SUM(IsRenamed),
                  SUM(IsCardinalityChanged),SUM(IsTypeChanged),
                  SUM(IsTargetChanged),SUM(IsBindingChanged),COUNT(*)
           FROM ElementChanges ec JOIN VersionPairs vp ON vp.PairKey=ec.PairKey
           GROUP BY vp.PairName ORDER BY vp.PairName"""
    print(f"  {'pair':10} {'add':>5}{'rem':>6}{'ren':>6}{'card':>6}{'type':>6}{'tgt':>6}{'bind':>6}{'rows':>7}")
    for row in cur.execute(q):
        print(f"  {row[0]:10} {row[1]:>5}{row[2]:>6}{row[3]:>6}{row[4]:>6}{row[5]:>6}{row[6]:>6}{row[7]:>6}{row[8]:>7}")

    print("\nDisagreement rows (partial coverage among applicable sources):")
    for row in cur.execute(
        """SELECT vp.PairName, COUNT(*) FROM ElementChanges ec
           JOIN VersionPairs vp ON vp.PairKey=ec.PairKey
           WHERE ec.DisagreementFlag=1 GROUP BY vp.PairName ORDER BY vp.PairName"""):
        print(f"  {row[0]:10} {row[1]:>7}")

    con.close()


if __name__ == "__main__":
    main()
