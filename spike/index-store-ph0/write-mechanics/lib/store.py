"""Version-keyed store + FTS5 sidecar schema for the Ph0 write-mechanics instrument.

The schema mirrors the shape the program plan specifies for `store.db`: a
`file_versions` table carrying a completion marker, and per-file tables keyed by
a composite `(version_id, <native id>)`.
"""

from __future__ import annotations

import os
import sqlite3

STORE_DDL = """
CREATE TABLE file_versions (
  version_id     INTEGER PRIMARY KEY,
  path           TEXT NOT NULL,
  content_hash   TEXT NOT NULL,
  extractor_fp   TEXT NOT NULL,
  cohort         INTEGER NOT NULL,
  complete       INTEGER NOT NULL DEFAULT 0,
  UNIQUE (path, content_hash, extractor_fp)
);
CREATE INDEX idx_file_versions_cohort ON file_versions(cohort);
CREATE INDEX idx_file_versions_complete ON file_versions(complete, path);

CREATE TABLE symbols (
  version_id        INTEGER NOT NULL,
  symbol_id         TEXT NOT NULL,
  file_id           TEXT NOT NULL,
  path              TEXT NOT NULL,
  language          TEXT NOT NULL,
  name              TEXT NOT NULL,
  kind              TEXT NOT NULL,
  signature         TEXT,
  doc_comment       TEXT,
  visibility        TEXT,
  parent_symbol_id  TEXT,
  start_line        INTEGER NOT NULL,
  start_column      INTEGER NOT NULL,
  end_line          INTEGER NOT NULL,
  end_column        INTEGER NOT NULL,
  start_byte        INTEGER NOT NULL,
  end_byte          INTEGER NOT NULL,
  body_hash         TEXT,
  is_test           INTEGER NOT NULL DEFAULT 0,
  metadata_json     TEXT,
  PRIMARY KEY (version_id, symbol_id)
);
CREATE INDEX idx_symbols_path ON symbols(path);
CREATE INDEX idx_symbols_name_kind ON symbols(name, kind);

CREATE TABLE reference_sites (
  version_id            INTEGER NOT NULL,
  reference_site_id     TEXT NOT NULL,
  file_id               TEXT NOT NULL,
  path                  TEXT NOT NULL,
  language              TEXT NOT NULL,
  containing_symbol_id  TEXT,
  start_line            INTEGER,
  start_column          INTEGER,
  end_line              INTEGER,
  end_column            INTEGER,
  start_byte            INTEGER,
  end_byte              INTEGER,
  is_exact              INTEGER NOT NULL,
  provenance            TEXT NOT NULL,
  PRIMARY KEY (version_id, reference_site_id)
);
CREATE INDEX idx_reference_sites_file ON reference_sites(version_id, file_id);
CREATE INDEX idx_reference_sites_containing ON reference_sites(containing_symbol_id);

CREATE TABLE identifiers (
  version_id            INTEGER NOT NULL,
  identifier_id         TEXT NOT NULL,
  reference_site_id     TEXT NOT NULL,
  file_id               TEXT NOT NULL,
  path                  TEXT NOT NULL,
  language              TEXT NOT NULL,
  name                  TEXT NOT NULL,
  kind                  TEXT NOT NULL,
  containing_symbol_id  TEXT,
  target_symbol_id      TEXT,
  start_line            INTEGER NOT NULL,
  start_column          INTEGER NOT NULL,
  end_line              INTEGER NOT NULL,
  end_column            INTEGER NOT NULL,
  start_byte            INTEGER NOT NULL,
  end_byte              INTEGER NOT NULL,
  confidence            REAL NOT NULL,
  metadata_json         TEXT,
  PRIMARY KEY (version_id, identifier_id)
);
CREATE INDEX idx_identifiers_file ON identifiers(version_id, file_id);
CREATE INDEX idx_identifiers_name_kind ON identifiers(name, kind);
CREATE INDEX idx_identifiers_containing ON identifiers(containing_symbol_id);
CREATE INDEX idx_identifiers_target ON identifiers(target_symbol_id);
CREATE INDEX idx_identifiers_reference_site ON identifiers(reference_site_id);
"""

SIDECAR_DDL = """
CREATE VIRTUAL TABLE symbols_fts USING fts5(
  symbol_id UNINDEXED, body, tokenize='unicode61 remove_diacritics 0');
CREATE VIRTUAL TABLE symbols_trigram USING fts5(
  symbol_id UNINDEXED, name_collapsed, tokenize='trigram');
CREATE TABLE search_symbols (
  version_id INTEGER NOT NULL,
  symbol_id  TEXT NOT NULL,
  doc_id     INTEGER NOT NULL,
  PRIMARY KEY (version_id, symbol_id)
);
CREATE INDEX idx_search_symbols_doc ON search_symbols(doc_id);
"""

STORE_INSERTS = {
    "symbols": "INSERT INTO symbols VALUES (" + ",".join("?" * 20) + ")",
    "reference_sites": "INSERT INTO reference_sites VALUES ("
    + ",".join("?" * 14)
    + ")",
    "identifiers": "INSERT INTO identifiers VALUES (" + ",".join("?" * 18) + ")",
}


def connect(path: str, *, secure_delete: bool = True) -> sqlite3.Connection:
    """Open an existing store/sidecar with the per-connection settings the
    contract requires. ``secure_delete`` is NOT persisted in the file, so every
    writer connection must re-assert it."""
    conn = sqlite3.connect(path, isolation_level=None, timeout=60.0)
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA synchronous=NORMAL")
    conn.execute(f"PRAGMA secure_delete={'ON' if secure_delete else 'OFF'}")
    conn.execute("PRAGMA cache_size=-65536")
    conn.execute("PRAGMA temp_store=MEMORY")
    return conn


def create(
    path: str,
    ddl: str,
    *,
    auto_vacuum: str,
    secure_delete: bool = True,
    page_size: int = 4096,
) -> sqlite3.Connection:
    """Create a fresh database. ``auto_vacuum`` and ``page_size`` must be set
    before the first table is created; neither can be changed later without a
    full VACUUM rewrite."""
    if os.path.exists(path):
        raise FileExistsError(path)
    conn = sqlite3.connect(path, isolation_level=None, timeout=60.0)
    conn.execute(f"PRAGMA page_size={page_size}")
    conn.execute(f"PRAGMA auto_vacuum={auto_vacuum}")
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA synchronous=NORMAL")
    conn.execute(f"PRAGMA secure_delete={'ON' if secure_delete else 'OFF'}")
    conn.execute("PRAGMA cache_size=-65536")
    conn.execute("PRAGMA temp_store=MEMORY")
    conn.executescript(ddl)
    return conn


def enable_fts_secure_delete(conn: sqlite3.Connection, tables) -> None:
    for table in tables:
        conn.execute(
            f"INSERT INTO {table}({table}, rank) VALUES ('secure-delete', 1)"
        )


def db_bytes(path: str) -> int:
    total = 0
    for suffix in ("", "-wal", "-shm"):
        candidate = path + suffix
        if os.path.exists(candidate):
            total += os.path.getsize(candidate)
    return total


def main_file_bytes(path: str) -> int:
    return os.path.getsize(path) if os.path.exists(path) else 0


def dir_bytes(directory: str) -> int:
    total = 0
    for root, _dirs, names in os.walk(directory):
        for name in names:
            try:
                total += os.path.getsize(os.path.join(root, name))
            except OSError:
                pass
    return total
