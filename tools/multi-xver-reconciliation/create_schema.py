"""Create cache/multi-xver-analysis.db schema. Idempotent: drops & recreates
ONLY the output DB. Never touches any source/input database."""
from __future__ import annotations

import os
import sqlite3

import contract as C

# Source -> spine indicator column
IN_COL = {
    C.SRC_REPORT: "InReport",
    C.SRC_ELEMENT_MAP: "InElementMap",
    C.SRC_RESOURCE_MAP: "InResourceMap",
    C.SRC_TYPE_MAP: "InTypeMap",
    C.SRC_FML: "InFml",
    C.SRC_FHIR_INI: "InFhirIni",
    C.SRC_DIFF_JSON: "InDiffJson",
    C.SRC_COMPARISON_DB: "InComparisonDb",
}

SCHEMA = f"""
PRAGMA journal_mode=WAL;

CREATE TABLE Sources (
    SourceKey   INTEGER PRIMARY KEY,
    SourceName  TEXT NOT NULL UNIQUE,
    Description TEXT
);

CREATE TABLE VersionPairs (
    PairKey     INTEGER PRIMARY KEY,
    PairName    TEXT NOT NULL UNIQUE,
    EarlierSeq  TEXT NOT NULL,
    LaterSeq    TEXT NOT NULL,
    EarlierPkg  TEXT NOT NULL,
    LaterPkg    TEXT NOT NULL
);

-- Presence of a row == source is applicable to that pair. Absence == N/A.
CREATE TABLE SourceApplicability (
    SourceKey   INTEGER NOT NULL REFERENCES Sources(SourceKey),
    PairKey     INTEGER NOT NULL REFERENCES VersionPairs(PairKey),
    DetailFiles TEXT,
    PRIMARY KEY (SourceKey, PairKey)
);

-- Every atomic assertion from every source (the per-source detail store).
CREATE TABLE RawAssertions (
    AssertionKey INTEGER PRIMARY KEY,
    SourceKey    INTEGER NOT NULL REFERENCES Sources(SourceKey),
    PairKey      INTEGER NOT NULL REFERENCES VersionPairs(PairKey),
    Structure    TEXT,
    EarlierPath  TEXT,
    LaterPath    TEXT,
    ChangeType   TEXT NOT NULL,
    RawOld       TEXT,
    RawNew       TEXT,
    Relationship TEXT,
    DetailFile   TEXT,
    DetailRef    TEXT,
    Notes        TEXT,
    ChangeKey    INTEGER   -- back-filled to the spine row it maps to
);

-- Union-find components (element identity across a pair).
CREATE TABLE Correspondences (
    CorrespondenceKey INTEGER PRIMARY KEY,
    PairKey       INTEGER NOT NULL REFERENCES VersionPairs(PairKey),
    Kind          TEXT,          -- InPlace | Renamed | Added | Removed | FanOut | FanIn | Complex
    EarlierPaths  TEXT,          -- distinct earlier paths (| delimited)
    LaterPaths    TEXT,          -- distinct later paths (| delimited)
    RowCount      INTEGER NOT NULL DEFAULT 0
);

-- The spine: one row per (pair, earlierPath, laterPath) correspondence edge.
-- InX columns are TRI-STATE: NULL = source N/A for pair, 0 = applicable but
-- silent on this element, 1 = source asserts this element change.
CREATE TABLE ElementChanges (
    ChangeKey             INTEGER PRIMARY KEY,
    PairKey               INTEGER NOT NULL REFERENCES VersionPairs(PairKey),
    CorrespondenceKey     INTEGER REFERENCES Correspondences(CorrespondenceKey),
    Structure             TEXT,
    EarlierPath           TEXT,
    LaterPath             TEXT,
    IsAdded               INTEGER NOT NULL DEFAULT 0,
    IsRemoved             INTEGER NOT NULL DEFAULT 0,
    IsRenamed             INTEGER NOT NULL DEFAULT 0,
    IsCardinalityChanged  INTEGER NOT NULL DEFAULT 0,
    IsTypeChanged         INTEGER NOT NULL DEFAULT 0,
    IsTargetChanged       INTEGER NOT NULL DEFAULT 0,
    IsBindingChanged      INTEGER NOT NULL DEFAULT 0,
    InReport              INTEGER,
    InElementMap          INTEGER,
    InResourceMap         INTEGER,
    InTypeMap             INTEGER,
    InFml                 INTEGER,
    InFhirIni             INTEGER,
    InDiffJson            INTEGER,
    InComparisonDb        INTEGER,
    PresentSourceCount    INTEGER NOT NULL DEFAULT 0,
    ApplicableSourceCount INTEGER NOT NULL DEFAULT 0,
    DisagreementFlag      INTEGER NOT NULL DEFAULT 0
);

-- Normalized (spineRow x source x changeType) reconciliation signals.
CREATE TABLE ChangeSignals (
    SignalKey    INTEGER PRIMARY KEY,
    ChangeKey    INTEGER NOT NULL REFERENCES ElementChanges(ChangeKey),
    SourceKey    INTEGER NOT NULL REFERENCES Sources(SourceKey),
    ChangeType   TEXT NOT NULL,
    RawOld       TEXT,
    RawNew       TEXT,
    Relationship TEXT,
    AssertionKey INTEGER REFERENCES RawAssertions(AssertionKey),
    Notes        TEXT
);

CREATE INDEX IX_Raw_Pair_Paths ON RawAssertions (PairKey, EarlierPath, LaterPath);
CREATE INDEX IX_Raw_Source ON RawAssertions (SourceKey, PairKey);
CREATE INDEX IX_Raw_ChangeKey ON RawAssertions (ChangeKey);
CREATE INDEX IX_EC_Pair_Paths ON ElementChanges (PairKey, EarlierPath, LaterPath);
CREATE INDEX IX_EC_Corr ON ElementChanges (CorrespondenceKey);
CREATE INDEX IX_EC_Structure ON ElementChanges (PairKey, Structure);
CREATE INDEX IX_Sig_ChangeKey ON ChangeSignals (ChangeKey);
CREATE INDEX IX_Sig_Source ON ChangeSignals (SourceKey, ChangeType);
"""

# Per-source convenience views over RawAssertions (user-facing "ElementMaps" etc.)
VIEW_NAMES = {
    C.SRC_REPORT: "ReportChanges",
    C.SRC_ELEMENT_MAP: "ElementMaps",
    C.SRC_RESOURCE_MAP: "ResourceMaps",
    C.SRC_TYPE_MAP: "TypeMaps",
    C.SRC_FML: "FmlMappings",
    C.SRC_FHIR_INI: "FhirIniChanges",
    C.SRC_DIFF_JSON: "DiffJsonChanges",
    C.SRC_COMPARISON_DB: "ComparisonElements",
}


def _try_remove_db_files():
    """Delete the DB + WAL sidecars for a pristine rebuild.

    Returns True if the main DB is gone (or never existed). Returns False when the
    main DB is held open by another process (e.g. a SQLite viewer) — on Windows
    ``os.remove`` cannot delete a file with a live handle. In that case the caller
    resets the schema in-place via SQL instead (WAL permits DDL alongside readers).
    """
    main_db = C.OUT_DB
    if os.path.exists(main_db):
        try:
            os.remove(main_db)
        except PermissionError:
            return False
    for suffix in ("-wal", "-shm"):
        p = C.OUT_DB + suffix
        if os.path.exists(p):
            try:
                os.remove(p)
            except OSError:
                pass
    return True


def _drop_all_objects(cur):
    """Drop every user view then table so the CREATE statements can re-run clean."""
    for (v,) in cur.execute("SELECT name FROM sqlite_master WHERE type='view'").fetchall():
        cur.execute(f'DROP VIEW IF EXISTS "{v}"')
    for (t,) in cur.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").fetchall():
        cur.execute(f'DROP TABLE IF EXISTS "{t}"')


def main():
    os.makedirs(os.path.dirname(C.OUT_DB), exist_ok=True)
    fresh = _try_remove_db_files()

    con = sqlite3.connect(C.OUT_DB)
    cur = con.cursor()
    if not fresh:
        # DB is open elsewhere; reset objects in-place rather than deleting the file.
        _drop_all_objects(cur)
    cur.executescript(SCHEMA)

    # Seed Sources
    for name, desc in C.SOURCES:
        cur.execute("INSERT INTO Sources(SourceName, Description) VALUES(?,?)", (name, desc))
    # Seed VersionPairs
    for pair in C.PAIRS:
        es, ls = C.PAIR_SEQ[pair]
        ep, lp = C.PAIR_PKG[pair]
        cur.execute(
            "INSERT INTO VersionPairs(PairName,EarlierSeq,LaterSeq,EarlierPkg,LaterPkg) VALUES(?,?,?,?,?)",
            (pair, es, ls, ep, lp))

    srckey = {n: k for (k, n) in cur.execute("SELECT SourceKey,SourceName FROM Sources")}
    pairkey = {n: k for (k, n) in cur.execute("SELECT PairKey,PairName FROM VersionPairs")}

    for (src, pair), detail in C.APPLICABILITY.items():
        cur.execute(
            "INSERT INTO SourceApplicability(SourceKey,PairKey,DetailFiles) VALUES(?,?,?)",
            (srckey[src], pairkey[pair], detail))

    # Per-source detail views
    for src, view in VIEW_NAMES.items():
        cur.execute(
            f"""CREATE VIEW {view} AS
                SELECT r.*, vp.PairName, s.SourceName
                FROM RawAssertions r
                JOIN Sources s ON s.SourceKey=r.SourceKey
                JOIN VersionPairs vp ON vp.PairKey=r.PairKey
                WHERE s.SourceName='{src}'""")

    con.commit()
    con.close()
    print(f"Created {C.OUT_DB}")
    print(f"  sources={len(C.SOURCES)} pairs={len(C.PAIRS)} applicability={len(C.APPLICABILITY)}")


if __name__ == "__main__":
    main()
