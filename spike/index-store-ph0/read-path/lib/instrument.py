#!/usr/bin/env python3
"""Ph0 Task 3 instrument: versioned index store read-path + physical-byte measurement.

Throwaway prototype code (razorback:prototyping). Subcommands:

  build-single      today's single-key schema, one view of data (baseline / dedicated copy)
  build-keepfile    composite (version_id, local_id) keys but file_id retained
  build-v4          v4 composite schema, one view of data (no views/manifests)
  build-store       v4 composite schema, N view manifests at sampled divergence
  inflate           add retained historical versions to a store (invisible to every view)
  bytes             dbstat per-object byte report for a database
  measure           timed representative reads under three visibility shapes
  plans             EXPLAIN QUERY PLAN capture for every measured query

The real Miller artifact is only ever opened through a mode=ro URI.
"""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import statistics
import sys
import time

SRC_URI = os.environ.get(
    "MILLER_PH0_SOURCE",
    "file:/Users/murphy/source/miller/.miller/symbols.db?mode=ro",
)
FINGERPRINT = "julie-extract-2.22.0/rv6"

SYMBOL_COLS = [
    "symbol_id", "path", "language", "name", "kind", "signature", "doc_comment",
    "visibility", "parent_symbol_id", "start_line", "start_column", "end_line",
    "end_column", "start_byte", "end_byte", "body_start_line", "body_start_column",
    "body_end_line", "body_end_column", "body_start_byte", "body_end_byte",
    "body_hash", "semantic_group", "confidence", "content_type", "is_test",
    "test_container", "test_lifecycle", "metadata_json",
]
SYMBOL_DECL = """
  symbol_id TEXT NOT NULL,
  path TEXT NOT NULL,
  language TEXT NOT NULL,
  name TEXT NOT NULL,
  kind TEXT NOT NULL,
  signature TEXT,
  doc_comment TEXT,
  visibility TEXT,
  parent_symbol_id TEXT,
  start_line INTEGER NOT NULL,
  start_column INTEGER NOT NULL,
  end_line INTEGER NOT NULL,
  end_column INTEGER NOT NULL,
  start_byte INTEGER NOT NULL,
  end_byte INTEGER NOT NULL,
  body_start_line INTEGER,
  body_start_column INTEGER,
  body_end_line INTEGER,
  body_end_column INTEGER,
  body_start_byte INTEGER,
  body_end_byte INTEGER,
  body_hash TEXT,
  semantic_group TEXT,
  confidence REAL,
  content_type TEXT,
  is_test INTEGER NOT NULL DEFAULT 0,
  test_container INTEGER NOT NULL DEFAULT 0,
  test_lifecycle INTEGER NOT NULL DEFAULT 0,
  metadata_json TEXT
"""

REFSITE_COLS = [
    "reference_site_id", "path", "language", "containing_symbol_id", "start_line",
    "start_column", "end_line", "end_column", "start_byte", "end_byte", "is_exact",
    "provenance",
]
REFSITE_DECL = """
  reference_site_id TEXT NOT NULL,
  path TEXT NOT NULL,
  language TEXT NOT NULL,
  containing_symbol_id TEXT,
  start_line INTEGER,
  start_column INTEGER,
  end_line INTEGER,
  end_column INTEGER,
  start_byte INTEGER,
  end_byte INTEGER,
  is_exact INTEGER NOT NULL,
  provenance TEXT NOT NULL
"""

# target_symbol_id is intentionally absent: the v4 surgery strips the resolution
# denormalization out of shared rows. Both sides of the comparison drop it so the
# measurement isolates versioning, not the denorm change.
IDENT_COLS = [
    "identifier_id", "reference_site_id", "path", "language", "name", "kind",
    "containing_symbol_id", "start_line", "start_column", "end_line", "end_column",
    "start_byte", "end_byte", "confidence", "code_context", "metadata_json",
]
IDENT_DECL = """
  identifier_id TEXT NOT NULL,
  reference_site_id TEXT NOT NULL,
  path TEXT NOT NULL,
  language TEXT NOT NULL,
  name TEXT NOT NULL,
  kind TEXT NOT NULL,
  containing_symbol_id TEXT,
  start_line INTEGER NOT NULL,
  start_column INTEGER NOT NULL,
  end_line INTEGER NOT NULL,
  end_column INTEGER NOT NULL,
  start_byte INTEGER NOT NULL,
  end_byte INTEGER NOT NULL,
  confidence REAL NOT NULL,
  code_context TEXT,
  metadata_json TEXT
"""

RESOLUTION_COLS = ["tier", "confidence", "method", "outcome", "candidates"]
RESOLUTION_DECL = """
  tier INTEGER,
  confidence REAL,
  method TEXT,
  outcome TEXT NOT NULL,
  candidates INTEGER
"""


def connect(path: str) -> sqlite3.Connection:
    conn = sqlite3.connect(f"file:{path}", uri=True)
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA synchronous=NORMAL")
    return conn


def attach_source(conn: sqlite3.Connection) -> None:
    conn.execute(f"ATTACH DATABASE '{SRC_URI}' AS src")


def cols(prefix: str, names: list[str]) -> str:
    return ", ".join(f"{prefix}{n}" for n in names)


# ---------------------------------------------------------------- single-key


SINGLE_SCHEMA = f"""
CREATE TABLE files (
  file_id TEXT PRIMARY KEY,
  path TEXT NOT NULL UNIQUE,
  language TEXT NOT NULL,
  content_hash TEXT NOT NULL,
  content_bytes INTEGER NOT NULL,
  line_count INTEGER
);
CREATE INDEX idx_files_path ON files(path);

CREATE TABLE symbols (
  file_id TEXT NOT NULL,
  {SYMBOL_DECL},
  PRIMARY KEY (symbol_id)
);
CREATE INDEX idx_symbols_path ON symbols(path);
CREATE INDEX idx_symbols_file ON symbols(file_id);
CREATE INDEX idx_symbols_name_kind ON symbols(name, kind);
CREATE INDEX idx_symbols_parent ON symbols(parent_symbol_id);
CREATE INDEX idx_symbols_is_test ON symbols(is_test);

CREATE TABLE reference_sites (
  file_id TEXT NOT NULL,
  {REFSITE_DECL},
  PRIMARY KEY (reference_site_id)
);
CREATE INDEX idx_reference_sites_file ON reference_sites(file_id);
CREATE INDEX idx_reference_sites_containing_symbol ON reference_sites(containing_symbol_id);

CREATE TABLE identifiers (
  file_id TEXT NOT NULL,
  {IDENT_DECL},
  PRIMARY KEY (identifier_id)
);
CREATE INDEX idx_identifiers_file ON identifiers(file_id);
CREATE INDEX idx_identifiers_name_kind ON identifiers(name, kind);
CREATE INDEX idx_identifiers_containing ON identifiers(containing_symbol_id);
CREATE INDEX idx_identifiers_reference_site ON identifiers(reference_site_id);

CREATE TABLE resolutions (
  identifier_id TEXT NOT NULL,
  target_symbol_id TEXT,
  {RESOLUTION_DECL},
  PRIMARY KEY (identifier_id)
);
CREATE INDEX idx_resolutions_target ON resolutions(target_symbol_id);
"""


def build_single(dest: str) -> None:
    conn = connect(dest)
    conn.executescript(SINGLE_SCHEMA)
    attach_source(conn)
    conn.execute(
        "INSERT INTO files (file_id, path, language, content_hash, content_bytes, line_count) "
        "SELECT file_id, path, language, content_hash, content_bytes, line_count FROM src.files"
    )
    conn.execute(
        f"INSERT INTO symbols (file_id, {cols('', SYMBOL_COLS)}) "
        f"SELECT s.file_id, {cols('s.', SYMBOL_COLS)} FROM src.symbols s"
    )
    conn.execute(
        f"INSERT INTO reference_sites (file_id, {cols('', REFSITE_COLS)}) "
        f"SELECT r.file_id, {cols('r.', REFSITE_COLS)} FROM src.reference_sites r"
    )
    conn.execute(
        f"INSERT INTO identifiers (file_id, {cols('', IDENT_COLS)}) "
        f"SELECT i.file_id, {cols('i.', IDENT_COLS)} FROM src.identifiers i"
    )
    conn.execute(
        f"INSERT INTO resolutions (identifier_id, target_symbol_id, {cols('', RESOLUTION_COLS)}) "
        f"SELECT ir.identifier_id, ir.target_symbol_id, {cols('ir.', RESOLUTION_COLS)} "
        "FROM src.identifier_resolutions ir"
    )
    conn.commit()
    finish(conn, dest)


# ------------------------------------------------- composite keys, file_id kept


KEEPFILE_SCHEMA = f"""
CREATE TABLE file_versions (
  version_id INTEGER PRIMARY KEY,
  file_id TEXT NOT NULL,
  path TEXT NOT NULL,
  language TEXT NOT NULL,
  content_hash TEXT NOT NULL,
  content_bytes INTEGER NOT NULL,
  line_count INTEGER,
  extractor_fingerprint TEXT NOT NULL,
  complete_level INTEGER NOT NULL,
  UNIQUE (path, content_hash, extractor_fingerprint)
);
CREATE INDEX idx_file_versions_path ON file_versions(path);

CREATE TABLE symbols (
  version_id INTEGER NOT NULL,
  file_id TEXT NOT NULL,
  {SYMBOL_DECL},
  PRIMARY KEY (version_id, symbol_id)
);
CREATE INDEX idx_symbols_path ON symbols(path, version_id);
CREATE INDEX idx_symbols_file ON symbols(file_id, version_id);
CREATE INDEX idx_symbols_name_kind ON symbols(name, kind, version_id);
CREATE INDEX idx_symbols_parent ON symbols(parent_symbol_id, version_id);
CREATE INDEX idx_symbols_is_test ON symbols(is_test, version_id);

CREATE TABLE reference_sites (
  version_id INTEGER NOT NULL,
  file_id TEXT NOT NULL,
  {REFSITE_DECL},
  PRIMARY KEY (version_id, reference_site_id)
);
CREATE INDEX idx_reference_sites_file ON reference_sites(file_id, version_id);
CREATE INDEX idx_reference_sites_containing_symbol ON reference_sites(containing_symbol_id, version_id);

CREATE TABLE identifiers (
  version_id INTEGER NOT NULL,
  file_id TEXT NOT NULL,
  {IDENT_DECL},
  PRIMARY KEY (version_id, identifier_id)
);
CREATE INDEX idx_identifiers_file ON identifiers(file_id, version_id);
CREATE INDEX idx_identifiers_name_kind ON identifiers(name, kind, version_id);
CREATE INDEX idx_identifiers_containing ON identifiers(containing_symbol_id, version_id);
CREATE INDEX idx_identifiers_reference_site ON identifiers(version_id, reference_site_id);

CREATE TABLE resolution_base_entries (
  base_id INTEGER NOT NULL,
  version_id INTEGER NOT NULL,
  identifier_id TEXT NOT NULL,
  target_version_id INTEGER,
  target_symbol_id TEXT,
  {RESOLUTION_DECL},
  PRIMARY KEY (base_id, version_id, identifier_id)
);
CREATE INDEX idx_rbe_target ON resolution_base_entries(base_id, target_symbol_id, target_version_id);
"""

# v4 drops file_id from every child table: version_id replaces it.
V4_SCHEMA = KEEPFILE_SCHEMA.replace("  file_id TEXT NOT NULL,\n", "", 100)
V4_SCHEMA = V4_SCHEMA.replace(
    "CREATE TABLE file_versions (\n  version_id INTEGER PRIMARY KEY,\n",
    "CREATE TABLE file_versions (\n  version_id INTEGER PRIMARY KEY,\n  file_id TEXT NOT NULL,\n",
)
V4_SCHEMA = V4_SCHEMA.replace(
    "CREATE INDEX idx_symbols_file ON symbols(file_id, version_id);",
    "CREATE INDEX idx_symbols_version ON symbols(version_id);",
)
V4_SCHEMA = V4_SCHEMA.replace(
    "CREATE INDEX idx_reference_sites_file ON reference_sites(file_id, version_id);",
    "CREATE INDEX idx_reference_sites_version ON reference_sites(version_id);",
)
V4_SCHEMA = V4_SCHEMA.replace(
    "CREATE INDEX idx_identifiers_file ON identifiers(file_id, version_id);",
    "CREATE INDEX idx_identifiers_version ON identifiers(version_id);",
)

VIEW_SCHEMA = """
CREATE TABLE views (
  view_id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  root TEXT NOT NULL,
  base_id INTEGER NOT NULL,
  manifest_generation INTEGER NOT NULL,
  divergence_pct REAL NOT NULL
);

CREATE TABLE view_manifest (
  view_id INTEGER NOT NULL,
  path TEXT NOT NULL,
  version_id INTEGER NOT NULL,
  extractor_fingerprint TEXT NOT NULL,
  PRIMARY KEY (view_id, path)
);
CREATE INDEX idx_view_manifest_version ON view_manifest(view_id, version_id);

CREATE TABLE resolution_deltas (
  view_id INTEGER NOT NULL,
  version_id INTEGER NOT NULL,
  identifier_id TEXT NOT NULL,
  target_version_id INTEGER,
  target_symbol_id TEXT,
  tombstone INTEGER NOT NULL DEFAULT 0,
  tier INTEGER,
  confidence REAL,
  method TEXT,
  outcome TEXT NOT NULL,
  candidates INTEGER,
  PRIMARY KEY (view_id, version_id, identifier_id)
);
CREATE INDEX idx_rd_target ON resolution_deltas(view_id, target_symbol_id, target_version_id);
"""


def build_versioned(dest: str, schema: str, keep_file_id: bool) -> sqlite3.Connection:
    """Loads one view's worth of data into the composite-key schema."""
    conn = connect(dest)
    conn.executescript(schema)
    attach_source(conn)
    conn.execute(
        "INSERT INTO file_versions (file_id, path, language, content_hash, content_bytes, "
        "line_count, extractor_fingerprint, complete_level) "
        f"SELECT file_id, path, language, content_hash, content_bytes, line_count, "
        f"'{FINGERPRINT}', 3 FROM src.files ORDER BY path"
    )
    conn.executescript(
        "CREATE TEMP TABLE map_file AS "
        "SELECT fv.file_id AS file_id, fv.version_id AS version_id FROM file_versions fv;"
        "CREATE UNIQUE INDEX temp.idx_map_file ON map_file(file_id);"
    )
    fid_col = "file_id, " if keep_file_id else ""
    fid_sel = "{p}file_id, " if keep_file_id else ""

    conn.execute(
        f"INSERT INTO symbols (version_id, {fid_col}{cols('', SYMBOL_COLS)}) "
        f"SELECT m.version_id, {fid_sel.format(p='s.')}{cols('s.', SYMBOL_COLS)} "
        "FROM src.symbols s JOIN map_file m ON m.file_id = s.file_id"
    )
    conn.execute(
        f"INSERT INTO reference_sites (version_id, {fid_col}{cols('', REFSITE_COLS)}) "
        f"SELECT m.version_id, {fid_sel.format(p='r.')}{cols('r.', REFSITE_COLS)} "
        "FROM src.reference_sites r JOIN map_file m ON m.file_id = r.file_id"
    )
    conn.execute(
        f"INSERT INTO identifiers (version_id, {fid_col}{cols('', IDENT_COLS)}) "
        f"SELECT m.version_id, {fid_sel.format(p='i.')}{cols('i.', IDENT_COLS)} "
        "FROM src.identifiers i JOIN map_file m ON m.file_id = i.file_id"
    )
    conn.execute(
        "INSERT INTO resolution_base_entries (base_id, version_id, identifier_id, "
        f"target_version_id, target_symbol_id, {cols('', RESOLUTION_COLS)}) "
        "SELECT 1, mi.version_id, ir.identifier_id, mt.version_id, ir.target_symbol_id, "
        f"{cols('ir.', RESOLUTION_COLS)} "
        "FROM src.identifier_resolutions ir "
        "JOIN src.identifiers i ON i.identifier_id = ir.identifier_id "
        "JOIN map_file mi ON mi.file_id = i.file_id "
        "LEFT JOIN src.symbols ts ON ts.symbol_id = ir.target_symbol_id "
        "LEFT JOIN map_file mt ON mt.file_id = ts.file_id"
    )
    conn.commit()
    return conn


def build_store(dest: str, divergences: list[float], keep_file_id: bool = False) -> None:
    """Builds the family store: shared base versions + one manifest per view."""
    conn = build_versioned(dest, (KEEPFILE_SCHEMA if keep_file_id else V4_SCHEMA) + VIEW_SCHEMA,
                           keep_file_id)
    base_versions = conn.execute("SELECT COUNT(*) FROM file_versions").fetchone()[0]

    conn.execute(
        "INSERT INTO views (view_id, name, root, base_id, manifest_generation, divergence_pct) "
        "VALUES (1, 'main', '/repo/main', 1, 1, 0.0)"
    )
    conn.execute(
        "INSERT INTO view_manifest (view_id, path, version_id, extractor_fingerprint) "
        "SELECT 1, path, version_id, extractor_fingerprint FROM file_versions"
    )

    fid_col = "file_id, " if keep_file_id else ""
    stats = []
    for idx, pct in enumerate(divergences, start=2):
        changed = max(1, round(base_versions * pct / 100.0))
        conn.execute(
            "INSERT INTO views (view_id, name, root, base_id, manifest_generation, divergence_pct) "
            "VALUES (?, ?, ?, 1, 1, ?)",
            (idx, f"wt{idx}", f"/repo/wt{idx}", pct),
        )
        # Deterministic changed-file draw: order by content hash, stride through the set.
        conn.execute("DROP TABLE IF EXISTS temp.changed")
        conn.execute(
            "CREATE TEMP TABLE changed AS SELECT version_id, path FROM ("
            "  SELECT version_id, path, ROW_NUMBER() OVER (ORDER BY content_hash) AS rn"
            "  FROM file_versions WHERE version_id <= ?) "
            "WHERE (rn + ?) % ? = 0",
            (base_versions, idx, max(1, base_versions // changed)),
        )
        conn.execute("CREATE UNIQUE INDEX temp.idx_changed ON changed(version_id)")
        actual_changed = conn.execute("SELECT COUNT(*) FROM temp.changed").fetchone()[0]

        # A changed file becomes a NEW version: same row shapes, new version_id.
        conn.execute(
            "INSERT INTO file_versions (file_id, path, language, content_hash, content_bytes, "
            "line_count, extractor_fingerprint, complete_level) "
            "SELECT fv.file_id, fv.path, fv.language, 'v' || ? || '-' || fv.content_hash, "
            "fv.content_bytes, fv.line_count, fv.extractor_fingerprint, 3 "
            "FROM file_versions fv JOIN temp.changed c ON c.version_id = fv.version_id",
            (idx,),
        )
        conn.execute("DROP TABLE IF EXISTS temp.newver")
        conn.execute(
            "CREATE TEMP TABLE newver AS "
            "SELECT c.version_id AS old_version_id, fv.version_id AS new_version_id "
            "FROM file_versions fv JOIN temp.changed c ON c.path = fv.path "
            "WHERE fv.content_hash = 'v' || ? || '-' || (SELECT content_hash FROM file_versions o "
            "                                            WHERE o.version_id = c.version_id)",
            (idx,),
        )
        conn.execute("CREATE UNIQUE INDEX temp.idx_newver ON newver(old_version_id)")

        for table, columns in (
            ("symbols", SYMBOL_COLS),
            ("reference_sites", REFSITE_COLS),
            ("identifiers", IDENT_COLS),
        ):
            conn.execute(
                f"INSERT INTO {table} (version_id, {fid_col}{cols('', columns)}) "
                f"SELECT nv.new_version_id, {('t.file_id, ' if keep_file_id else '')}"
                f"{cols('t.', columns)} "
                f"FROM {table} t JOIN temp.newver nv ON nv.old_version_id = t.version_id"
            )

        conn.execute(
            "INSERT INTO view_manifest (view_id, path, version_id, extractor_fingerprint) "
            "SELECT ?, fv.path, COALESCE(nv.new_version_id, fv.version_id), fv.extractor_fingerprint "
            "FROM file_versions fv LEFT JOIN temp.newver nv ON nv.old_version_id = fv.version_id "
            "WHERE fv.version_id <= ?",
            (idx, base_versions),
        )

        # Resolution delta, part 1: the changed files' own identifiers re-resolve.
        conn.execute(
            "INSERT INTO resolution_deltas (view_id, version_id, identifier_id, target_version_id, "
            f"target_symbol_id, tombstone, {cols('', RESOLUTION_COLS)}) "
            "SELECT ?, nv.new_version_id, b.identifier_id, "
            "COALESCE(nt.new_version_id, b.target_version_id), b.target_symbol_id, 0, "
            f"{cols('b.', RESOLUTION_COLS)} "
            "FROM resolution_base_entries b "
            "JOIN temp.newver nv ON nv.old_version_id = b.version_id "
            "LEFT JOIN temp.newver nt ON nt.old_version_id = b.target_version_id "
            "WHERE b.base_id = 1",
            (idx,),
        )
        # Part 2: references from UNCHANGED files INTO a changed file must repoint.
        conn.execute(
            "INSERT OR IGNORE INTO resolution_deltas (view_id, version_id, identifier_id, "
            f"target_version_id, target_symbol_id, tombstone, {cols('', RESOLUTION_COLS)}) "
            "SELECT ?, b.version_id, b.identifier_id, nt.new_version_id, b.target_symbol_id, 0, "
            f"{cols('b.', RESOLUTION_COLS)} "
            "FROM resolution_base_entries b "
            "JOIN temp.newver nt ON nt.old_version_id = b.target_version_id "
            "WHERE b.base_id = 1 "
            "  AND b.version_id NOT IN (SELECT old_version_id FROM temp.newver)",
            (idx,),
        )
        delta_rows = conn.execute(
            "SELECT COUNT(*) FROM resolution_deltas WHERE view_id = ?", (idx,)
        ).fetchone()[0]
        stats.append(
            {
                "view_id": idx,
                "target_divergence_pct": pct,
                "changed_files": actual_changed,
                "actual_divergence_pct": round(100.0 * actual_changed / base_versions, 3),
                "resolution_delta_rows": delta_rows,
            }
        )
        conn.commit()

    print(json.dumps({"base_versions": base_versions, "views": stats}, indent=2))
    finish(conn, dest)


def build_dedicated_view(store: str, dest: str, view_id: int) -> None:
    """Materializes one view back into today's single-key schema: a dedicated per-worktree copy."""
    conn = connect(dest)
    conn.executescript(SINGLE_SCHEMA)
    conn.execute(f"ATTACH DATABASE 'file:{store}?mode=ro' AS st")
    conn.executescript(
        "CREATE TEMP TABLE vis AS "
        f"SELECT m.version_id AS version_id, fv.file_id AS file_id FROM st.view_manifest m "
        f"JOIN st.file_versions fv ON fv.version_id = m.version_id WHERE m.view_id = {view_id};"
        "CREATE UNIQUE INDEX temp.idx_vis ON vis(version_id);"
    )
    conn.execute(
        "INSERT INTO files (file_id, path, language, content_hash, content_bytes, line_count) "
        "SELECT fv.file_id, fv.path, fv.language, fv.content_hash, fv.content_bytes, fv.line_count "
        "FROM st.file_versions fv JOIN vis v ON v.version_id = fv.version_id"
    )
    for table, columns in (
        ("symbols", SYMBOL_COLS),
        ("reference_sites", REFSITE_COLS),
        ("identifiers", IDENT_COLS),
    ):
        conn.execute(
            f"INSERT INTO {table} (file_id, {cols('', columns)}) "
            f"SELECT v.file_id, {cols('t.', columns)} FROM st.{table} t "
            "JOIN vis v ON v.version_id = t.version_id"
        )
    conn.execute(
        f"INSERT INTO resolutions (identifier_id, target_symbol_id, {cols('', RESOLUTION_COLS)}) "
        f"SELECT b.identifier_id, b.target_symbol_id, {cols('b.', RESOLUTION_COLS)} "
        "FROM st.resolution_base_entries b JOIN vis v ON v.version_id = b.version_id "
        "WHERE b.base_id = 1 AND NOT EXISTS (SELECT 1 FROM st.resolution_deltas d "
        f"  WHERE d.view_id = {view_id} AND d.version_id = b.version_id "
        "    AND d.identifier_id = b.identifier_id)"
    )
    conn.execute(
        f"INSERT INTO resolutions (identifier_id, target_symbol_id, {cols('', RESOLUTION_COLS)}) "
        f"SELECT d.identifier_id, d.target_symbol_id, {cols('d.', RESOLUTION_COLS)} "
        "FROM st.resolution_deltas d JOIN vis v ON v.version_id = d.version_id "
        f"WHERE d.view_id = {view_id} AND d.tombstone = 0"
    )
    conn.commit()
    print(json.dumps({
        "view_id": view_id,
        "files": conn.execute("SELECT COUNT(*) FROM files").fetchone()[0],
        "symbols": conn.execute("SELECT COUNT(*) FROM symbols").fetchone()[0],
        "identifiers": conn.execute("SELECT COUNT(*) FROM identifiers").fetchone()[0],
        "reference_sites": conn.execute("SELECT COUNT(*) FROM reference_sites").fetchone()[0],
        "resolutions": conn.execute("SELECT COUNT(*) FROM resolutions").fetchone()[0],
    }))
    finish(conn, dest)


def inflate(dest: str, extra_generations: int) -> None:
    """Adds retained historical versions that no view manifest references."""
    conn = connect(dest)
    base_versions = conn.execute(
        "SELECT COUNT(*) FROM file_versions WHERE version_id <= "
        "(SELECT MAX(version_id) FROM view_manifest WHERE view_id = 1)"
    ).fetchone()[0]
    for gen in range(extra_generations):
        conn.execute(
            "INSERT INTO file_versions (file_id, path, language, content_hash, content_bytes, "
            "line_count, extractor_fingerprint, complete_level) "
            "SELECT file_id, path, language, 'hist' || ? || '-' || content_hash, content_bytes, "
            "line_count, extractor_fingerprint, 3 FROM file_versions WHERE version_id <= ?",
            (gen, base_versions),
        )
        conn.execute("DROP TABLE IF EXISTS temp.hist")
        conn.execute(
            "CREATE TEMP TABLE hist AS SELECT o.version_id AS old_version_id, "
            "n.version_id AS new_version_id FROM file_versions n JOIN file_versions o "
            "ON o.path = n.path AND n.content_hash = 'hist' || ? || '-' || o.content_hash "
            "WHERE o.version_id <= ?",
            (gen, base_versions),
        )
        conn.execute("CREATE UNIQUE INDEX temp.idx_hist ON hist(old_version_id)")
        for table, columns in (
            ("symbols", SYMBOL_COLS),
            ("reference_sites", REFSITE_COLS),
            ("identifiers", IDENT_COLS),
        ):
            conn.execute(
                f"INSERT INTO {table} (version_id, {cols('', columns)}) "
                f"SELECT h.new_version_id, {cols('t.', columns)} "
                f"FROM {table} t JOIN temp.hist h ON h.old_version_id = t.version_id"
            )
        conn.commit()
    print(json.dumps({"extra_generations": extra_generations,
                      "file_versions": conn.execute(
                          "SELECT COUNT(*) FROM file_versions").fetchone()[0]}))
    finish(conn, dest)


def finish(conn: sqlite3.Connection, dest: str) -> None:
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    conn.commit()
    conn.close()


# ------------------------------------------------------------------- reporting


def bytes_report(path: str, label: str) -> None:
    conn = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    page_size, page_count = conn.execute("PRAGMA page_size").fetchone()[0], \
        conn.execute("PRAGMA page_count").fetchone()[0]
    rows = conn.execute(
        "SELECT name, SUM(pgsize) FROM dbstat GROUP BY name ORDER BY 2 DESC"
    ).fetchall()
    total = page_size * page_count
    payload = {
        "label": label,
        "path": path,
        "file_bytes": os.path.getsize(path),
        "page_size": page_size,
        "page_count": page_count,
        "physical_bytes": total,
        "objects": [{"name": n, "bytes": b} for n, b in rows],
    }
    counts = {}
    for table in ("symbols", "identifiers", "reference_sites", "files", "file_versions",
                  "resolutions", "resolution_base_entries", "resolution_deltas",
                  "view_manifest"):
        try:
            counts[table] = conn.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
        except sqlite3.OperationalError:
            pass
    payload["row_counts"] = counts
    conn.close()
    print(json.dumps(payload, indent=2))


# ------------------------------------------------------------------ read paths


def sample_keys(store_path: str, view_id: int, n: int) -> dict:
    conn = sqlite3.connect(f"file:{store_path}?mode=ro", uri=True)
    visible = f"(SELECT version_id FROM view_manifest WHERE view_id = {view_id})"
    names = [r[0] for r in conn.execute(
        "SELECT DISTINCT name FROM (SELECT name FROM symbols "
        f"WHERE version_id IN {visible} ORDER BY symbol_id LIMIT ?)", (n * 3,)).fetchall()][:n]
    paths = [r[0] for r in conn.execute(
        f"SELECT path FROM view_manifest WHERE view_id = {view_id} ORDER BY path LIMIT ?",
        (n,)).fetchall()]
    hot = conn.execute(
        "SELECT target_symbol_id, target_version_id, COUNT(*) c FROM resolution_base_entries "
        f"WHERE base_id = 1 AND target_symbol_id IS NOT NULL AND target_version_id IN {visible} "
        "GROUP BY 1, 2 ORDER BY c DESC LIMIT ?", (n // 4,)).fetchall()
    spread = conn.execute(
        "SELECT target_symbol_id, target_version_id FROM resolution_base_entries "
        f"WHERE base_id = 1 AND target_symbol_id IS NOT NULL AND target_version_id IN {visible} "
        "GROUP BY 1, 2 ORDER BY target_symbol_id LIMIT ?", (n - n // 4,)).fetchall()
    conn.close()
    return {
        "name": names,
        "path": paths,
        "refs": [(s, v) for s, v, _ in hot] + [(s, v) for s, v in spread],
    }


BASELINE_SQL = {
    "name_lookup": (
        "SELECT symbol_id, path, kind, start_line FROM symbols WHERE name = ?"),
    "file_symbols": (
        "SELECT symbol_id, name, kind, start_line FROM symbols WHERE path = ? "
        "ORDER BY start_line"),
    "refs_by_symbol": (
        "SELECT i.containing_symbol_id, i.path, i.start_line "
        "FROM resolutions r "
        "JOIN identifiers i ON i.identifier_id = r.identifier_id "
        "JOIN symbols s ON s.symbol_id = i.containing_symbol_id "
        "WHERE r.target_symbol_id = ?"),
}

JOIN_SQL = {
    "name_lookup": (
        "SELECT s.symbol_id, s.path, s.kind, s.start_line FROM symbols s "
        "JOIN view_manifest m ON m.view_id = :view AND m.version_id = s.version_id "
        "WHERE s.name = :key"),
    "file_symbols": (
        "SELECT s.symbol_id, s.name, s.kind, s.start_line "
        "FROM view_manifest m JOIN symbols s ON s.version_id = m.version_id "
        "WHERE m.view_id = :view AND m.path = :key ORDER BY s.start_line"),
    "refs_by_symbol": (
        "SELECT i.containing_symbol_id, i.path, i.start_line FROM ("
        "  SELECT b.version_id, b.identifier_id FROM resolution_base_entries b"
        "   WHERE b.base_id = :base AND b.target_symbol_id = :key"
        "     AND b.target_version_id = :keyver"
        "     AND NOT EXISTS (SELECT 1 FROM resolution_deltas d WHERE d.view_id = :view"
        "                       AND d.version_id = b.version_id"
        "                       AND d.identifier_id = b.identifier_id)"
        "  UNION ALL"
        "  SELECT d.version_id, d.identifier_id FROM resolution_deltas d"
        "   WHERE d.view_id = :view AND d.tombstone = 0 AND d.target_symbol_id = :key"
        "     AND d.target_version_id = :keyver"
        ") r "
        "JOIN identifiers i ON i.version_id = r.version_id AND i.identifier_id = r.identifier_id "
        "JOIN view_manifest m ON m.view_id = :view AND m.version_id = i.version_id "
        "JOIN symbols s ON s.version_id = i.version_id AND s.symbol_id = i.containing_symbol_id"),
}

TEMP_SQL = {
    "name_lookup": (
        "SELECT s.symbol_id, s.path, s.kind, s.start_line FROM symbols s "
        "JOIN vis v ON v.version_id = s.version_id WHERE s.name = :key"),
    "file_symbols": (
        "SELECT s.symbol_id, s.name, s.kind, s.start_line FROM symbols s "
        "JOIN vis v ON v.version_id = s.version_id WHERE s.path = :key ORDER BY s.start_line"),
    "refs_by_symbol": (
        "SELECT i.containing_symbol_id, i.path, i.start_line FROM ("
        "  SELECT b.version_id, b.identifier_id FROM resolution_base_entries b"
        "   WHERE b.base_id = :base AND b.target_symbol_id = :key"
        "     AND b.target_version_id = :keyver"
        "     AND NOT EXISTS (SELECT 1 FROM resolution_deltas d WHERE d.view_id = :view"
        "                       AND d.version_id = b.version_id"
        "                       AND d.identifier_id = b.identifier_id)"
        "  UNION ALL"
        "  SELECT d.version_id, d.identifier_id FROM resolution_deltas d"
        "   WHERE d.view_id = :view AND d.tombstone = 0 AND d.target_symbol_id = :key"
        "     AND d.target_version_id = :keyver"
        ") r "
        "JOIN identifiers i ON i.version_id = r.version_id AND i.identifier_id = r.identifier_id "
        "JOIN vis v ON v.version_id = i.version_id "
        "JOIN symbols s ON s.version_id = i.version_id AND s.symbol_id = i.containing_symbol_id"),
}


# Composite-key tables, single view, no visibility join at all. Isolates the cost of
# visibility from the cost of the v4 row shape.
NOVIS_SQL = {
    "name_lookup": (
        "SELECT symbol_id, path, kind, start_line FROM symbols WHERE name = :key"),
    "file_symbols": (
        "SELECT symbol_id, name, kind, start_line FROM symbols WHERE path = :key "
        "ORDER BY start_line"),
    "refs_by_symbol": (
        "SELECT i.containing_symbol_id, i.path, i.start_line "
        "FROM resolution_base_entries b "
        "JOIN identifiers i ON i.version_id = b.version_id AND i.identifier_id = b.identifier_id "
        "JOIN symbols s ON s.version_id = i.version_id AND s.symbol_id = i.containing_symbol_id "
        "WHERE b.base_id = :base AND b.target_symbol_id = :key AND b.target_version_id = :keyver"),
}


# Every measured connection gets the same generous private page cache, so the comparison
# reflects engine work on cached pages instead of OS-page-cache luck between three files.
MEASURE_CACHE_PAGES = -200000  # ~200 MB


def open_measured(path: str) -> sqlite3.Connection:
    conn = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    conn.execute(f"PRAGMA cache_size={MEASURE_CACHE_PAGES}")
    return conn


def open_shape(shape: str, store: str, baseline: str, view_id: int, v4single: str | None = None):
    if shape == "dedicated":
        return open_measured(baseline), BASELINE_SQL, 0.0
    if shape == "v4_novis":
        return open_measured(v4single), NOVIS_SQL, 0.0
    conn = open_measured(store)
    build_ms = 0.0
    if shape == "temp_vis":
        start = time.perf_counter()
        conn.execute("CREATE TEMP TABLE vis (version_id INTEGER PRIMARY KEY)")
        conn.execute(
            "INSERT INTO vis (version_id) SELECT version_id FROM view_manifest WHERE view_id = ?",
            (view_id,))
        build_ms = (time.perf_counter() - start) * 1000.0
    return conn, (JOIN_SQL if shape == "manifest_join" else TEMP_SQL), build_ms


def build_params(sql: str, shape: str, klass: str, key, view_id: int):
    if shape == "dedicated":
        return (key[0] if klass == "refs_by_symbol" else key,)
    full = {"view": view_id, "base": 1}
    if klass == "refs_by_symbol":
        full["key"], full["keyver"] = key[0], key[1]
    else:
        full["key"] = key
    return {k: v for k, v in full.items() if f":{k}" in sql}


def run_class(conn, sql: str, shape: str, klass: str, keys, view_id: int) -> tuple[float, int]:
    cur = conn.cursor()
    rows = 0
    start = time.perf_counter()
    for key in keys:
        rows += len(cur.execute(sql, build_params(sql, shape, klass, key, view_id)).fetchall())
    return (time.perf_counter() - start) * 1000.0, rows


def count_steps(conn, sql: str, shape: str, klass: str, keys, view_id: int,
                granularity: int = 100) -> int:
    """Deterministic engine-work metric: VDBE instructions executed for one key sweep.

    Wall-clock on a loaded machine is noisy; this counts the work itself.
    """
    ticks = 0

    def tick():
        nonlocal ticks
        ticks += 1
        return 0

    cur = conn.cursor()
    conn.set_progress_handler(tick, granularity)
    for key in keys:
        cur.execute(sql, build_params(sql, shape, klass, key, view_id)).fetchall()
    conn.set_progress_handler(None, 0)
    return ticks * granularity


def measure(store: str, baseline: str, v4single: str, view_id: int, keys_n: int, passes: int,
            label: str) -> None:
    keys = sample_keys(store, view_id, keys_n)
    key_map = {"name_lookup": keys["name"], "file_symbols": keys["path"],
               "refs_by_symbol": keys["refs"]}
    result = {"label": label, "view_id": view_id, "passes": passes,
              "keys_per_class": {k: len(v) for k, v in key_map.items()}, "classes": {}}

    floor_conn = sqlite3.connect(f"file:{store}?mode=ro", uri=True)
    floor = []
    for _ in range(passes):
        cur = floor_conn.cursor()
        start = time.perf_counter()
        for _ in range(keys_n):
            cur.execute("SELECT 1").fetchall()
        floor.append((time.perf_counter() - start) * 1000.0 / keys_n * 1000.0)
    floor_conn.close()
    result["harness_floor_us_per_query"] = round(statistics.median(floor), 2)

    shapes = ("dedicated", "v4_novis", "manifest_join", "temp_vis")
    # Passes are interleaved across shapes so machine noise hits every shape equally.
    opened = {s: open_shape(s, store, baseline, view_id, v4single) for s in shapes}
    for klass, klass_keys in key_map.items():
        timings = {s: [] for s in shapes}
        rowcount = {}
        warm = {}
        for shape in shapes:
            conn, sqlmap, _ = opened[shape]
            warm[shape] = run_class(conn, sqlmap[klass], shape, klass, klass_keys, view_id)[0]
        for p in range(passes):
            order = shapes[p % len(shapes):] + shapes[:p % len(shapes)]
            for shape in order:
                conn, sqlmap, _ = opened[shape]
                ms, rows = run_class(conn, sqlmap[klass], shape, klass, klass_keys, view_id)
                timings[shape].append(ms)
                rowcount[shape] = rows
        entry = {}
        for shape in shapes:
            conn, sqlmap, _ = opened[shape]
            entry[shape] = {
                "vdbe_steps_per_sweep": count_steps(
                    conn, sqlmap[klass], shape, klass, klass_keys, view_id),
                "median_ms_per_sweep": round(statistics.median(timings[shape]), 3),
                "min_ms_per_sweep": round(min(timings[shape]), 3),
                "us_per_query": round(
                    statistics.median(timings[shape]) * 1000.0 / len(klass_keys), 2),
                "max_ms": round(max(timings[shape]), 3),
                "stdev_ms": round(statistics.stdev(timings[shape]), 3),
                "rows_returned": rowcount[shape],
                "warm_up_ms": round(warm[shape], 3),
                "vis_build_ms": round(opened[shape][2], 3),
            }
        base = entry["dedicated"]["median_ms_per_sweep"]
        base_min = entry["dedicated"]["min_ms_per_sweep"]
        novis = entry["v4_novis"]["median_ms_per_sweep"]
        for shape in ("v4_novis", "manifest_join", "temp_vis"):
            entry[shape]["overhead_pct_vs_dedicated"] = round(
                100.0 * (entry[shape]["median_ms_per_sweep"] - base) / base, 1)
            entry[shape]["overhead_pct_vs_dedicated_min"] = round(
                100.0 * (entry[shape]["min_ms_per_sweep"] - base_min) / base_min, 1)
        base_steps = entry["dedicated"]["vdbe_steps_per_sweep"]
        novis_steps = entry["v4_novis"]["vdbe_steps_per_sweep"]
        for shape in ("v4_novis", "manifest_join", "temp_vis"):
            entry[shape]["work_pct_vs_dedicated"] = round(
                100.0 * (entry[shape]["vdbe_steps_per_sweep"] - base_steps) / base_steps, 1)
        for shape in ("manifest_join", "temp_vis"):
            entry[shape]["overhead_pct_vs_v4_novis"] = round(
                100.0 * (entry[shape]["median_ms_per_sweep"] - novis) / novis, 1)
            entry[shape]["work_pct_vs_v4_novis"] = round(
                100.0 * (entry[shape]["vdbe_steps_per_sweep"] - novis_steps) / novis_steps, 1)
        result["classes"][klass] = entry
    for conn, _, _ in opened.values():
        conn.close()
    print(json.dumps(result, indent=2))


def verify(store: str, baseline: str, v4single: str, view_id: int, keys_n: int) -> None:
    """Both visibility shapes must return the dedicated copy's result set exactly."""
    keys = sample_keys(store, view_id, keys_n)
    key_map = {"name_lookup": keys["name"], "file_symbols": keys["path"],
               "refs_by_symbol": keys["refs"]}
    opened = {s: open_shape(s, store, baseline, view_id, v4single)
              for s in ("dedicated", "manifest_join", "temp_vis")}
    report = {"view_id": view_id, "classes": {}}
    for klass, klass_keys in key_map.items():
        mismatch = []
        rows_checked = 0
        for key in klass_keys:
            expected = None
            for shape in ("dedicated", "manifest_join", "temp_vis"):
                conn, sqlmap, _ = opened[shape]
                sql = sqlmap[klass]
                got = sorted(conn.execute(
                    sql, build_params(sql, shape, klass, key, view_id)).fetchall())
                if shape == "dedicated":
                    expected = got
                    rows_checked += len(got)
                elif got != expected:
                    mismatch.append({"key": str(key), "shape": shape,
                                     "expected_rows": len(expected), "got_rows": len(got)})
        report["classes"][klass] = {
            "keys": len(klass_keys),
            "rows_compared": rows_checked,
            "mismatches": len(mismatch),
            "examples": mismatch[:5],
        }
    for conn, _, _ in opened.values():
        conn.close()
    report["verdict"] = "IDENTICAL" if all(
        c["mismatches"] == 0 for c in report["classes"].values()) else "MISMATCH"
    print(json.dumps(report, indent=2))


def plans(store: str, baseline: str, v4single: str, view_id: int) -> None:
    out = {}
    for shape in ("dedicated", "v4_novis", "manifest_join", "temp_vis"):
        conn, sqlmap, _ = open_shape(shape, store, baseline, view_id, v4single)
        out[shape] = {}
        for klass, sql in sqlmap.items():
            key = ("x", 1) if klass == "refs_by_symbol" else "x"
            rows = conn.execute(
                "EXPLAIN QUERY PLAN " + sql,
                build_params(sql, shape, klass, key, view_id)).fetchall()
            out[shape][klass] = [r[3] for r in rows]
        conn.close()
    print(json.dumps(out, indent=2))


def main() -> int:
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("build-single"); p.add_argument("dest")
    p = sub.add_parser("build-keepfile"); p.add_argument("dest")
    p = sub.add_parser("build-v4"); p.add_argument("dest")
    p = sub.add_parser("build-store")
    p.add_argument("dest"); p.add_argument("--divergences", required=True)
    p = sub.add_parser("build-dedicated-view")
    p.add_argument("dest"); p.add_argument("--store", required=True)
    p.add_argument("--view", type=int, required=True)
    p = sub.add_parser("inflate"); p.add_argument("dest"); p.add_argument("--generations", type=int, default=2)
    p = sub.add_parser("bytes"); p.add_argument("path"); p.add_argument("--label", default="")
    p = sub.add_parser("measure")
    p.add_argument("--store", required=True); p.add_argument("--baseline", required=True)
    p.add_argument("--v4single", required=True)
    p.add_argument("--view", type=int, default=1); p.add_argument("--keys", type=int, default=200)
    p.add_argument("--passes", type=int, default=7); p.add_argument("--label", default="")
    p = sub.add_parser("plans")
    p.add_argument("--store", required=True); p.add_argument("--baseline", required=True)
    p.add_argument("--v4single", required=True)
    p.add_argument("--view", type=int, default=1)
    p = sub.add_parser("verify")
    p.add_argument("--store", required=True); p.add_argument("--baseline", required=True)
    p.add_argument("--v4single", required=True)
    p.add_argument("--view", type=int, default=1); p.add_argument("--keys", type=int, default=300)

    a = ap.parse_args()
    if a.cmd == "build-single":
        build_single(a.dest)
    elif a.cmd == "build-keepfile":
        finish(build_versioned(a.dest, KEEPFILE_SCHEMA, True), a.dest)
    elif a.cmd == "build-v4":
        finish(build_versioned(a.dest, V4_SCHEMA, False), a.dest)
    elif a.cmd == "build-store":
        build_store(a.dest, [float(x) for x in a.divergences.split(",")])
    elif a.cmd == "build-dedicated-view":
        build_dedicated_view(a.store, a.dest, a.view)
    elif a.cmd == "inflate":
        inflate(a.dest, a.generations)
    elif a.cmd == "bytes":
        bytes_report(a.path, a.label or os.path.basename(a.path))
    elif a.cmd == "measure":
        measure(a.store, a.baseline, a.v4single, a.view, a.keys, a.passes, a.label)
    elif a.cmd == "plans":
        plans(a.store, a.baseline, a.v4single, a.view)
    elif a.cmd == "verify":
        verify(a.store, a.baseline, a.v4single, a.view, a.keys)
    return 0


if __name__ == "__main__":
    sys.exit(main())
