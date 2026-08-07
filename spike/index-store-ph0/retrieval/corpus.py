"""Shared corpus builder for the Ph0 filtered-retrieval instrument.

Reads the live Miller search sidecar READ-ONLY and projects it into a versioned
"family store" shape: every symbol row is keyed by (version_id, symbol_id), the
FTS rowid is the store surrogate key, and a view is a manifest of
path -> version_id. View 0 sees version index 0 of every path; every other
version index is hidden from it.

Nothing here writes to the real artifact. All generated databases land under the
directory passed on the command line, which the entry script deletes.
"""

from __future__ import annotations

import os
import random
import sqlite3
import time
from dataclasses import dataclass

REAL_SEARCH_DB = os.environ.get(
    "MILLER_PH0_SEARCH_DB", "/Users/murphy/source/miller/.miller/search.db"
)
VEC_EXTENSION = os.environ.get(
    "MILLER_SQLITE_VEC_PATH", "/Users/murphy/source/miller/.tools/vec0.dylib"
)

SEED = 20260806
MUTATION_RATE = 0.35
ADVERSARIAL_HIDDEN_PER_QUERY = 260
TRIGRAM_WINDOW = 200
SEMANTIC_WINDOW = 500
VECTOR_LANE_DIMS = 384

# version_id = path_ordinal * VERSION_STRIDE + version_ordinal. The stride is fixed so a
# path's version 0 keeps the same id at every stored-version multiple, which lets one
# dedicated per-view oracle serve as the equivalence key space for all of them.
VERSION_STRIDE = 64


@dataclass(frozen=True)
class Row:
    symbol_id: str
    name: str
    path: str
    start_line: int
    doc_len: int
    body: str
    name_collapsed: str
    qual_collapsed: str


def read_real_corpus() -> list[Row]:
    uri = f"file:{REAL_SEARCH_DB}?mode=ro"
    con = sqlite3.connect(uri, uri=True)
    try:
        base = {}
        for sid, name, path, start_line, doc_len in con.execute(
            "SELECT symbol_id, name, path, start_line, doc_len FROM search_symbols"
        ):
            base[sid] = (name or "", path or "", start_line or 0, doc_len or 0)
        bodies = {}
        for sid, body in con.execute("SELECT symbol_id, body FROM symbols_fts"):
            bodies[sid] = body or ""
        collapsed = {}
        for sid, nc, qc in con.execute(
            "SELECT symbol_id, name_collapsed, qual_collapsed FROM symbols_trigram"
        ):
            collapsed[sid] = (nc or "", qc or "")
        rows = []
        for sid, (name, path, start_line, doc_len) in base.items():
            if sid not in bodies or sid not in collapsed:
                continue
            nc, qc = collapsed[sid]
            rows.append(
                Row(sid, name, path, start_line, doc_len, bodies[sid], nc, qc)
            )
        rows.sort(key=lambda r: (r.path, r.start_line, r.symbol_id))
        return rows
    finally:
        con.close()


def mutate(text: str, tag: str) -> str:
    if not text:
        return text
    return text + tag


def collapse(text: str) -> str:
    return "".join(ch for ch in text.lower() if ch.isalnum())


STORE_DDL = """
PRAGMA page_size=4096;
CREATE TABLE store_symbols(
    rid           INTEGER PRIMARY KEY,
    version_id    INTEGER NOT NULL,
    symbol_id     TEXT NOT NULL,
    name          TEXT NOT NULL,
    path          TEXT NOT NULL,
    start_line    INTEGER NOT NULL,
    doc_len       INTEGER NOT NULL,
    collapsed_len INTEGER NOT NULL
);
CREATE INDEX ix_store_symbols_version ON store_symbols(version_id);
CREATE TABLE view_manifest(
    view_id    INTEGER NOT NULL,
    path       TEXT NOT NULL,
    version_id INTEGER NOT NULL,
    PRIMARY KEY(view_id, version_id)
) WITHOUT ROWID;
CREATE TABLE view_projection(
    view_id INTEGER NOT NULL,
    rid     INTEGER NOT NULL,
    doc_id  INTEGER NOT NULL,
    PRIMARY KEY(view_id, rid)
) WITHOUT ROWID;
CREATE VIRTUAL TABLE symbols_fts USING fts5(
    body, tokenize='unicode61 remove_diacritics 0');
CREATE VIRTUAL TABLE symbols_trigram USING fts5(
    name_collapsed, qual_collapsed, tokenize='trigram');
"""


def _adversarial_units(queries: list[str], version_ids_hidden: list[int], rng: random.Random):
    """Hidden-version rows engineered to dominate the trigram rank window.

    Their collapsed names are very short and contain the query substring, so
    FTS5's bm25 rank (shorter document, same matched phrase => better rank)
    puts them ahead of every real visible match.
    """
    out = []
    if not version_ids_hidden:
        return out
    for q in queries:
        for i in range(ADVERSARIAL_HIDDEN_PER_QUERY):
            vid = version_ids_hidden[i % len(version_ids_hidden)]
            nc = f"{q}{i:03d}"
            out.append(
                (
                    vid,
                    f"adv:{q}:{i}",
                    nc,
                    f"adv/{q}.hidden",
                    1 + i,
                    2,
                    f"{q} {i}",
                    nc,
                    nc,
                )
            )
    return out


def build_store(
    db_path: str,
    rows: list[Row],
    multiple: int,
    adversarial_queries: list[str],
) -> dict:
    """Build a family store holding `multiple` versions of every path.

    Version index 0 of each path is what view 0 sees; indices 1.. are hidden.
    Returns build stats.
    """
    if os.path.exists(db_path):
        os.remove(db_path)
    con = sqlite3.connect(db_path)
    con.executescript(STORE_DDL)

    paths = sorted({r.path for r in rows})
    path_index = {p: i for i, p in enumerate(paths)}

    def version_id(path: str, k: int) -> int:
        return path_index[path] * VERSION_STRIDE + k

    rng = random.Random(SEED)
    started = time.perf_counter()

    rid = 0
    sym_batch = []
    fts_batch = []
    tri_batch = []

    def flush():
        con.executemany(
            "INSERT INTO store_symbols(rid, version_id, symbol_id, name, path, start_line, doc_len,"
            " collapsed_len) VALUES(?,?,?,?,?,?,?,?)",
            sym_batch,
        )
        con.executemany(
            "INSERT INTO symbols_fts(rowid, body) VALUES(?,?)", fts_batch
        )
        con.executemany(
            "INSERT INTO symbols_trigram(rowid, name_collapsed, qual_collapsed) VALUES(?,?,?)",
            tri_batch,
        )
        sym_batch.clear()
        fts_batch.clear()
        tri_batch.clear()

    for k in range(multiple):
        for r in rows:
            rid += 1
            if k == 0:
                name, body, nc, qc = r.name, r.body, r.name_collapsed, r.qual_collapsed
                doc_len = r.doc_len
            elif rng.random() < MUTATION_RATE:
                tag = f"v{k}x{rng.randrange(97, 123):c}"
                name = mutate(r.name, tag)
                body = mutate(r.body, " " + tag)
                nc = r.name_collapsed + collapse(tag)
                qc = r.qual_collapsed + collapse(tag)
                doc_len = r.doc_len + 1
            else:
                name, body, nc, qc = r.name, r.body, r.name_collapsed, r.qual_collapsed
                doc_len = r.doc_len
            sym_batch.append(
                (rid, version_id(r.path, k), r.symbol_id, name, r.path, r.start_line, doc_len,
                 len(nc) + len(qc))
            )
            fts_batch.append((rid, body))
            tri_batch.append((rid, nc, qc))
            if len(sym_batch) >= 20000:
                flush()
    flush()

    hidden_versions = [
        version_id(p, k) for p in paths for k in range(1, multiple)
    ]
    adversarial_rows = _adversarial_units(adversarial_queries, hidden_versions, rng)
    for (
        vid,
        symbol_id,
        name,
        path,
        start_line,
        doc_len,
        body,
        nc,
        qc,
    ) in adversarial_rows:
        rid += 1
        sym_batch.append((rid, vid, symbol_id, name, path, start_line, doc_len, len(nc) + len(qc)))
        fts_batch.append((rid, body))
        tri_batch.append((rid, nc, qc))
    flush()

    con.executemany(
        "INSERT INTO view_manifest(view_id, path, version_id) VALUES(0,?,?)",
        [(p, version_id(p, 0)) for p in paths],
    )
    projection_started = time.perf_counter()
    con.execute(
        "INSERT INTO view_projection(view_id, rid, doc_id)"
        " SELECT 0, s.rid, ROW_NUMBER() OVER (ORDER BY s.path, s.start_line, s.symbol_id) - 1"
        " FROM store_symbols s"
        " JOIN view_manifest m ON m.view_id = 0 AND m.version_id = s.version_id"
    )
    projection_seconds = time.perf_counter() - projection_started
    con.commit()
    con.execute("ANALYZE")
    con.commit()
    elapsed = time.perf_counter() - started
    stats = {
        "db_path": db_path,
        "multiple": multiple,
        "store_rows": rid,
        "visible_rows": len(rows),
        "hidden_rows": rid - len(rows),
        "adversarial_hidden_rows": len(adversarial_rows),
        "build_seconds": round(elapsed, 2),
        "view_projection_build_seconds": round(projection_seconds, 3),
        "bytes": os.path.getsize(db_path),
    }
    con.close()
    return stats


DEDICATED_DDL = STORE_DDL


def build_dedicated(db_path: str, rows: list[Row]) -> dict:
    """A dedicated per-view index: only view 0's rows, contiguous rowids.

    This is the equivalence oracle. Its FTS corpus statistics are view-local by
    construction, which is exactly what the shared store must reproduce.
    """
    if os.path.exists(db_path):
        os.remove(db_path)
    con = sqlite3.connect(db_path)
    con.executescript(DEDICATED_DDL)
    paths = sorted({r.path for r in rows})
    path_index = {p: i for i, p in enumerate(paths)}
    started = time.perf_counter()
    sym_batch, fts_batch, tri_batch = [], [], []
    for doc_id, r in enumerate(rows):
        rid = doc_id + 1
        sym_batch.append(
            (rid, path_index[r.path] * VERSION_STRIDE, r.symbol_id, r.name, r.path, r.start_line,
             r.doc_len, len(r.name_collapsed) + len(r.qual_collapsed))
        )
        fts_batch.append((rid, r.body))
        tri_batch.append((rid, r.name_collapsed, r.qual_collapsed))
    con.executemany(
        "INSERT INTO store_symbols(rid, version_id, symbol_id, name, path, start_line, doc_len,"
        " collapsed_len) VALUES(?,?,?,?,?,?,?,?)",
        sym_batch,
    )
    con.executemany("INSERT INTO symbols_fts(rowid, body) VALUES(?,?)", fts_batch)
    con.executemany(
        "INSERT INTO symbols_trigram(rowid, name_collapsed, qual_collapsed) VALUES(?,?,?)",
        tri_batch,
    )
    con.executemany(
        "INSERT INTO view_manifest(view_id, path, version_id) VALUES(0,?,?)",
        [(p, path_index[p] * VERSION_STRIDE) for p in paths],
    )
    con.executemany(
        "INSERT INTO view_projection(view_id, rid, doc_id) VALUES(0,?,?)",
        [(doc_id + 1, doc_id) for doc_id in range(len(rows))],
    )
    con.commit()
    con.execute("ANALYZE")
    con.commit()
    elapsed = time.perf_counter() - started
    stats = {
        "db_path": db_path,
        "rows": len(rows),
        "build_seconds": round(elapsed, 2),
        "bytes": os.path.getsize(db_path),
    }
    con.close()
    return stats


def quote_fts(term: str) -> str:
    return '"' + term.replace('"', '""') + '"'
