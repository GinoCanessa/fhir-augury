"""Reconciliation views over the multi-xver spine. Re-runnable: drops & recreates
only these views. The views are the user-facing 'consolidated reconciliation'
surface — every source's coverage of every element change, plus conflict finders.
"""
from __future__ import annotations

import sqlite3

import contract as C

# tri-state InX -> readable label
def _tri(col):
    return f"CASE WHEN {col} IS NULL THEN 'n/a' WHEN {col}=1 THEN 'Y' ELSE '-' END"

VIEWS = {
    # ---- primary: one readable row per element change with the source matrix ----
    "ChangeMatrix": f"""
        SELECT ec.ChangeKey, vp.PairName AS Pair, ec.Structure,
               ec.EarlierPath, ec.LaterPath,
               CASE WHEN ec.IsAdded=1 THEN 'Y' ELSE '' END AS Added,
               CASE WHEN ec.IsRemoved=1 THEN 'Y' ELSE '' END AS Removed,
               CASE WHEN ec.IsRenamed=1 THEN 'Y' ELSE '' END AS Renamed,
               CASE WHEN ec.IsCardinalityChanged=1 THEN 'Y' ELSE '' END AS Cardinality,
               CASE WHEN ec.IsTypeChanged=1 THEN 'Y' ELSE '' END AS Type,
               CASE WHEN ec.IsTargetChanged=1 THEN 'Y' ELSE '' END AS Target,
               CASE WHEN ec.IsBindingChanged=1 THEN 'Y' ELSE '' END AS Binding,
               {_tri('ec.InReport')}       AS Report,
               {_tri('ec.InElementMap')}   AS ElementMap,
               {_tri('ec.InResourceMap')}  AS ResourceMap,
               {_tri('ec.InTypeMap')}      AS TypeMap,
               {_tri('ec.InFml')}          AS Fml,
               {_tri('ec.InFhirIni')}      AS FhirIni,
               {_tri('ec.InDiffJson')}     AS DiffJson,
               {_tri('ec.InComparisonDb')} AS ComparisonDb,
               ec.PresentSourceCount AS Present, ec.ApplicableSourceCount AS Applicable,
               ec.CorrespondenceKey, ec.DisagreementFlag
        FROM ElementChanges ec JOIN VersionPairs vp ON vp.PairKey=ec.PairKey
    """,

    # rows that assert at least one real change (exclude no-change Mapped anchors)
    "ChangeMatrixChanges": """
        SELECT * FROM ChangeMatrix
        WHERE Added||Removed||Renamed||Cardinality||Type||Target||Binding <> ''
    """,

    # ---- per (pair, source): how much of the pair's changes the source covers ----
    "SourceReliability": """
        SELECT vp.PairName AS Pair, s.SourceName AS Source,
               COUNT(*) AS ApplicableChangeRows,
               SUM(CASE WHEN
                   (s.SourceName='Report'       AND ec.InReport=1)       OR
                   (s.SourceName='ElementMap'   AND ec.InElementMap=1)   OR
                   (s.SourceName='ResourceMap'  AND ec.InResourceMap=1)  OR
                   (s.SourceName='TypeMap'      AND ec.InTypeMap=1)      OR
                   (s.SourceName='Fml'          AND ec.InFml=1)          OR
                   (s.SourceName='FhirIni'      AND ec.InFhirIni=1)      OR
                   (s.SourceName='DiffJson'     AND ec.InDiffJson=1)     OR
                   (s.SourceName='ComparisonDb' AND ec.InComparisonDb=1)
                   THEN 1 ELSE 0 END) AS PresentChangeRows
        FROM ElementChanges ec
        JOIN VersionPairs vp ON vp.PairKey=ec.PairKey
        JOIN SourceApplicability sa ON sa.PairKey=ec.PairKey
        JOIN Sources s ON s.SourceKey=sa.SourceKey
        WHERE ec.IsAdded+ec.IsRemoved+ec.IsRenamed+ec.IsCardinalityChanged
              +ec.IsTypeChanged+ec.IsTargetChanged+ec.IsBindingChanged > 0
        GROUP BY vp.PairName, s.SourceName
    """,

    # ---- structural conflicts: same element identity, sources disagree on fate ----
    "StructuralConflicts": """
        SELECT c.CorrespondenceKey, vp.PairName AS Pair, c.Kind,
               c.EarlierPaths, c.LaterPaths, c.RowCount,
               SUM(ec.IsAdded)   AS AddRows,
               SUM(ec.IsRemoved) AS RemoveRows,
               SUM(ec.IsRenamed) AS RenameRows
        FROM Correspondences c
        JOIN VersionPairs vp ON vp.PairKey=c.PairKey
        JOIN ElementChanges ec ON ec.CorrespondenceKey=c.CorrespondenceKey
        GROUP BY c.CorrespondenceKey
        HAVING (SUM(ec.IsAdded)>0 AND SUM(ec.IsRemoved)>0)
            OR (SUM(ec.IsRenamed)>0 AND (SUM(ec.IsAdded)>0 OR SUM(ec.IsRemoved)>0))
    """,

    # ---- value conflicts: same element+changeType, two sources, different values ----
    "ValueConflicts": """
        SELECT s1.ChangeKey, vp.PairName AS Pair, ec.Structure,
               ec.EarlierPath, ec.LaterPath, s1.ChangeType,
               a.SourceName AS SourceA, s1.RawOld AS OldA, s1.RawNew AS NewA,
               b.SourceName AS SourceB, s2.RawOld AS OldB, s2.RawNew AS NewB
        FROM ChangeSignals s1
        JOIN ChangeSignals s2
             ON s2.ChangeKey=s1.ChangeKey AND s2.ChangeType=s1.ChangeType
            AND s2.SourceKey>s1.SourceKey
        JOIN ElementChanges ec ON ec.ChangeKey=s1.ChangeKey
        JOIN VersionPairs vp ON vp.PairKey=ec.PairKey
        JOIN Sources a ON a.SourceKey=s1.SourceKey
        JOIN Sources b ON b.SourceKey=s2.SourceKey
        WHERE IFNULL(s1.RawOld,'')<>IFNULL(s2.RawOld,'')
           OR IFNULL(s1.RawNew,'')<>IFNULL(s2.RawNew,'')
    """,

    # ---- cardinality conflicts (high-signal: firm min..max on both sides) ----
    "CardinalityConflicts": """
        SELECT * FROM ValueConflicts
        WHERE ChangeType='Cardinality'
          AND NewA NOT LIKE '%?%' AND NewB NOT LIKE '%?%'
          AND IFNULL(OldA,'') NOT LIKE '%?%' AND IFNULL(OldB,'') NOT LIKE '%?%'
    """,

    # ---- unique catches: a change only ONE applicable source asserted ----
    "UniqueCatch": """
        SELECT cm.* FROM ChangeMatrixChanges cm
        WHERE cm.Present=1 AND cm.Applicable>1
    """,

    # ---- coverage gaps: most applicable sources caught it, at least one missed ----
    "CoverageGaps": """
        SELECT cm.* FROM ChangeMatrixChanges cm
        WHERE cm.Present>=2 AND cm.Present < cm.Applicable
    """,
}


def main():
    con = sqlite3.connect(C.OUT_DB)
    cur = con.cursor()
    # drop in reverse-dependency order (views referencing others dropped first)
    for name in ["CardinalityConflicts", "ValueConflicts", "UniqueCatch",
                 "CoverageGaps", "ChangeMatrixChanges", "StructuralConflicts",
                 "SourceReliability", "ChangeMatrix"]:
        cur.execute(f"DROP VIEW IF EXISTS {name}")
    # create in dependency order
    order = ["ChangeMatrix", "ChangeMatrixChanges", "SourceReliability",
             "StructuralConflicts", "ValueConflicts", "CardinalityConflicts",
             "UniqueCatch", "CoverageGaps"]
    for name in order:
        cur.execute(f"CREATE VIEW {name} AS {VIEWS[name]}")
    con.commit()
    con.close()
    print(f"Created {len(order)} reconciliation views: {', '.join(order)}")


if __name__ == "__main__":
    main()
