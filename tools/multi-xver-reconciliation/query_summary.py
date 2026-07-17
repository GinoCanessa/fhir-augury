"""Final validation + reconciliation summary over cache/multi-xver-analysis.db.
Read-only. Prints table counts, tri-state indicator sanity, per-source coverage,
conflict tallies, and targeted spot-checks that prove the reconciliation works.
"""
from __future__ import annotations

import sqlite3

import contract as C


def q(cur, sql, args=()):
    return cur.execute(sql, args).fetchall()


def main():
    con = sqlite3.connect(f"file:{C.OUT_DB}?mode=ro", uri=True)
    cur = con.cursor()

    print("=" * 74)
    print("TABLE COUNTS")
    for t in ["Sources", "VersionPairs", "SourceApplicability", "RawAssertions",
              "Correspondences", "ElementChanges", "ChangeSignals"]:
        n = q(cur, f"SELECT COUNT(*) FROM {t}")[0][0]
        print(f"  {t:22} {n:>8}")

    print("\n" + "=" * 74)
    print("TRI-STATE INDICATOR SANITY (per pair: NULL=n/a, 0=silent, 1=asserts)")
    for col in ["InReport", "InElementMap", "InResourceMap", "InTypeMap",
                "InFml", "InFhirIni", "InDiffJson", "InComparisonDb"]:
        row = q(cur, f"""SELECT vp.PairName,
                    SUM(CASE WHEN {col} IS NULL THEN 1 ELSE 0 END),
                    SUM(CASE WHEN {col}=0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN {col}=1 THEN 1 ELSE 0 END)
                 FROM ElementChanges ec JOIN VersionPairs vp ON vp.PairKey=ec.PairKey
                 GROUP BY vp.PairName ORDER BY vp.PairName""")
        disp = "  ".join(f"{p}:n/a={na},0={z},1={o}" for (p, na, z, o) in row)
        print(f"  {col:15} {disp}")

    print("\n" + "=" * 74)
    print("SOURCE RELIABILITY (change rows a source caught / rows applicable to pair)")
    print(f"  {'pair':10} {'source':14} {'present':>8} {'applic':>8}  cover%")
    for (pair, src, present, applic) in q(cur, """
            SELECT Pair, Source, PresentChangeRows, ApplicableChangeRows
            FROM SourceReliability ORDER BY Pair, PresentChangeRows DESC"""):
        pct = (100.0 * present / applic) if applic else 0
        print(f"  {pair:10} {src:14} {present:>8} {applic:>8}  {pct:5.1f}%")

    print("\n" + "=" * 74)
    print("CONFLICT FINDERS")
    print(f"  StructuralConflicts (same element, sources disagree on fate): "
          f"{q(cur, 'SELECT COUNT(*) FROM StructuralConflicts')[0][0]}")
    print(f"  ValueConflicts (same element+type, sources give diff values): "
          f"{q(cur, 'SELECT COUNT(*) FROM ValueConflicts')[0][0]}")
    print(f"  CardinalityConflicts: "
          f"{q(cur, 'SELECT COUNT(*) FROM CardinalityConflicts')[0][0]}")
    print(f"  UniqueCatch (only 1 of >1 applicable sources caught it): "
          f"{q(cur, 'SELECT COUNT(*) FROM UniqueCatch')[0][0]}")
    print(f"  CoverageGaps (>=2 caught, >=1 applicable source missed): "
          f"{q(cur, 'SELECT COUNT(*) FROM CoverageGaps')[0][0]}")

    print("\n  -- sample StructuralConflicts --")
    for r in q(cur, """SELECT Pair,Kind,EarlierPaths,LaterPaths,AddRows,RemoveRows,RenameRows
                       FROM StructuralConflicts LIMIT 5"""):
        print(f"     {r[0]} {r[1]:8} E={r[2]} L={r[3]} (add={r[4]} rem={r[5]} ren={r[6]})")

    print("\n  -- sample CardinalityConflicts --")
    for r in q(cur, """SELECT Pair,EarlierPath,SourceA,NewA,SourceB,NewB
                       FROM CardinalityConflicts LIMIT 5"""):
        print(f"     {r[0]} {r[1]}: {r[2]}={r[3]} vs {r[4]}={r[5]}")

    print("\n" + "=" * 74)
    print("TARGETED SPOT-CHECKS")

    def show(label, sql, args=()):
        rows = q(cur, sql, args)
        print(f"  {label}: {len(rows)} row(s)")
        for r in rows[:4]:
            print(f"     {r}")

    # MarketingStatus.country cardinality 1..1 -> 0..1 (R4->R4B); which sources?
    show("MarketingStatus.country (R4->R4B)",
         """SELECT EarlierPath,Cardinality,Report,DiffJson,ComparisonDb,Present,Applicable
            FROM ChangeMatrix WHERE Pair='R4->R4B' AND Structure='MarketingStatus'
              AND EarlierPath LIKE '%country%'""")

    # AdverseEvent.occurrence[x] -> effect[x] rename in R5->R6 (fhir.ini + report?)
    show("AdverseEvent.occurrence[x]->effect[x] (R5->R6)",
         """SELECT EarlierPath,LaterPath,Renamed,Report,FhirIni,Present
            FROM ChangeMatrix WHERE Pair='R5->R6' AND EarlierPath LIKE 'AdverseEvent.occurrence%'""")

    # AdverseEvent.contributor -> participant (R4B->R5): report + fml + comparison?
    show("AdverseEvent.contributor->participant (R4B->R5)",
         """SELECT EarlierPath,LaterPath,Renamed,Report,Fml,ElementMap,DiffJson,ComparisonDb,Present
            FROM ChangeMatrix WHERE Pair='R4B->R5' AND EarlierPath='AdverseEvent.contributor'""")

    # a change ALL applicable sources agree on (max present) for R4B->R5
    show("Top R4B->R5 changes by source agreement",
         """SELECT Structure,EarlierPath,LaterPath,Present,Applicable,
                   Report,ElementMap,Fml,DiffJson,ComparisonDb
            FROM ChangeMatrixChanges WHERE Pair='R4B->R5'
            ORDER BY Present DESC LIMIT 4""")

    con.close()


if __name__ == "__main__":
    main()
