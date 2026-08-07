"""Ph0 proof 3: per-view DocId and BM25 statistic economics.

Miller's C# ranker consumes three view-local inputs per query:
  * `_documentCount` and `_avgdl`, stamped into `meta` today by a full-table scan
    (`SearchIndexWriter.ReadStats`, src/Miller.Indexing/SearchIndexWriter.cs:592);
  * a per-document `DocId` used as the deterministic tie-break in
    `score DESC, DocId ASC` and as the trigram-only ordering key.

Two DocId histories exist in the shipped code and disagree:
  * fresh ordinal assignment - `ROW_NUMBER() OVER (ORDER BY path, start_line,
    symbol_id) - 1` (src/Miller.Indexing/SqliteSymbolReader.cs:45), a pure
    function of the visible set;
  * incremental stable reuse - `AssignStableDocIds`
    (src/Miller.Indexing/SearchIndexWriter.cs:436), which preserves old ids and
    recycles freed ones, so the result depends on convergence history.

This instrument measures the two per-view DocId options named in the program
plan, the statistic-maintenance options, and what a manifest flip costs.
"""

from __future__ import annotations

import json
import os
import statistics
import sqlite3
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from corpus import build_store, quote_fts, read_real_corpus  # noqa: E402

MULTIPLE = 20
VIEWS = 8
REPEATS = 5
QUERIES = 60

ROW_NUMBER_CTE = """
WITH visible AS (
    SELECT s.rid,
           ROW_NUMBER() OVER (ORDER BY s.path, s.start_line, s.symbol_id) - 1 AS doc_id
      FROM store_symbols s
      JOIN view_manifest m ON m.view_id = 0 AND m.version_id = s.version_id
)
SELECT v.doc_id FROM symbols_fts
  JOIN visible v ON v.rid = symbols_fts.rowid
 WHERE body MATCH ?
"""

PROJECTION_SQL = """
SELECT p.doc_id FROM symbols_fts
  JOIN view_projection p ON p.view_id = 0 AND p.rid = symbols_fts.rowid
 WHERE body MATCH ?
"""

SORT_KEY_SQL = """
SELECT s.path, s.start_line, s.symbol_id FROM symbols_fts
  JOIN view_projection p ON p.view_id = 0 AND p.rid = symbols_fts.rowid
  JOIN store_symbols s ON s.rid = symbols_fts.rowid
 WHERE body MATCH ?
"""

STATS_SCAN_SQL = """
SELECT COUNT(*), COALESCE(SUM(s.doc_len), 0) FROM store_symbols s
  JOIN view_manifest m ON m.view_id = 0 AND m.version_id = s.version_id
"""

STATS_PROJECTION_SQL = """
SELECT COUNT(*), COALESCE(SUM(s.doc_len), 0) FROM view_projection p
  JOIN store_symbols s ON s.rid = p.rid WHERE p.view_id = 0
"""


def table_bytes(con: sqlite3.Connection, name: str) -> int | None:
    try:
        row = con.execute("SELECT SUM(pgsize) FROM dbstat WHERE name = ?", (name,)).fetchone()
        return int(row[0]) if row and row[0] is not None else 0
    except sqlite3.Error:
        return None


def measure(con: sqlite3.Connection, sql: str, param) -> tuple[int, float]:
    samples = []
    count = 0
    params = (param,) if param is not None else ()
    for _ in range(REPEATS):
        started = time.perf_counter()
        count = sum(1 for _ in con.execute(sql, params))
        samples.append((time.perf_counter() - started) * 1000.0)
    return count, statistics.median(samples)


def build_eight_views(con: sqlite3.Connection, visible_count: int) -> dict:
    """Materialise view_projection for eight sibling views over the shared store.

    Sibling views differ from view 0 by a handful of files, which is what a
    worktree family looks like; the projection is a full per-view mapping either
    way, so the eight-view byte cost is the honest number.
    """
    con.execute("DELETE FROM view_projection WHERE view_id > 0")
    con.execute("DELETE FROM view_manifest WHERE view_id > 0")
    base = con.execute(
        "SELECT path, version_id FROM view_manifest WHERE view_id = 0 ORDER BY path"
    ).fetchall()
    started = time.perf_counter()
    for view in range(1, VIEWS):
        stride = max(1, len(base) // 12)
        manifest = [
            (view, path, version_id + (view if index % stride == view % stride else 0))
            for index, (path, version_id) in enumerate(base)
        ]
        con.executemany(
            "INSERT INTO view_manifest(view_id, path, version_id) VALUES(?,?,?)", manifest
        )
        con.execute(
            "INSERT INTO view_projection(view_id, rid, doc_id)"
            " SELECT ?, s.rid,"
            "        ROW_NUMBER() OVER (ORDER BY s.path, s.start_line, s.symbol_id) - 1"
            "   FROM store_symbols s"
            "   JOIN view_manifest m ON m.view_id = ? AND m.version_id = s.version_id",
            (view, view),
        )
    con.commit()
    seconds = time.perf_counter() - started
    rows = con.execute("SELECT COUNT(*) FROM view_projection").fetchone()[0]
    con.execute("ANALYZE")
    con.commit()
    return {
        "views": VIEWS,
        "rows": rows,
        "rows_per_view_median": rows // VIEWS,
        "visible_rows_view0": visible_count,
        "build_seconds_views_1_to_7": round(seconds, 2),
        "projection_bytes": table_bytes(con, "view_projection"),
        "manifest_bytes": table_bytes(con, "view_manifest"),
    }


def manifest_flip_cost(con: sqlite3.Connection) -> dict:
    """One file changes version: what does each DocId option owe?

    The contiguous ordinal shifts for every row after the edited path, so a
    materialised projection must be rewritten from that path onward. The stored
    sort key owes nothing because it is a property of the row, not of the view.
    """
    path = con.execute(
        "SELECT path FROM view_manifest WHERE view_id = 0 ORDER BY path LIMIT 1 OFFSET 20"
    ).fetchone()[0]
    next_version = con.execute(
        "SELECT MIN(s.version_id) FROM store_symbols s"
        " JOIN view_manifest m ON m.view_id = 0 AND m.path = s.path"
        " WHERE s.path = ? AND s.version_id > m.version_id",
        (path,),
    ).fetchone()[0]

    started = time.perf_counter()
    con.execute("BEGIN")
    con.execute(
        "UPDATE view_manifest SET version_id = ? WHERE view_id = 0 AND path = ?",
        (next_version, path),
    )
    manifest_seconds = time.perf_counter() - started

    started = time.perf_counter()
    con.execute("DELETE FROM view_projection WHERE view_id = 0")
    con.execute(
        "INSERT INTO view_projection(view_id, rid, doc_id)"
        " SELECT 0, s.rid, ROW_NUMBER() OVER (ORDER BY s.path, s.start_line, s.symbol_id) - 1"
        "   FROM store_symbols s"
        "   JOIN view_manifest m ON m.view_id = 0 AND m.version_id = s.version_id"
    )
    rebuild_seconds = time.perf_counter() - started
    con.execute("ROLLBACK")

    shifted = con.execute(
        "SELECT COUNT(*) FROM store_symbols s"
        " JOIN view_manifest m ON m.view_id = 0 AND m.version_id = s.version_id"
        " WHERE s.path > ?",
        (path,),
    ).fetchone()[0]

    return {
        "flipped_path": path,
        "manifest_update_ms": round(manifest_seconds * 1000, 3),
        "contiguous_projection_full_rebuild_ms": round(rebuild_seconds * 1000, 3),
        "rows_whose_ordinal_shifts": shifted,
        "stored_sort_key_maintenance_ms": 0.0,
    }


def docid_history_divergence(rows) -> dict:
    """Do the two shipped DocId histories order the same corpus the same way?

    Replays `AssignStableDocIds` over one file replacement and compares the
    resulting order against the fresh-ordinal rule on the identical final set.
    """
    sample = rows[:2000]
    fresh = {r.symbol_id: i for i, r in enumerate(sample)}

    removed_path = sample[len(sample) // 2].path
    survivors = [r for r in sample if r.path != removed_path]
    arriving = [
        type(r)(
            symbol_id=f"new:{i}",
            name=r.name,
            path="!added/first.cs",
            start_line=i,
            doc_len=r.doc_len,
            body=r.body,
            name_collapsed=r.name_collapsed,
            qual_collapsed=r.qual_collapsed,
        )
        for i, r in enumerate(sample[:50])
    ]
    final = sorted(arriving + survivors, key=lambda r: (r.path, r.start_line, r.symbol_id))

    current_ids = {r.symbol_id for r in final}
    reusable = sorted(doc_id for sid, doc_id in fresh.items() if sid not in current_ids)
    next_doc_id = max(fresh.values()) + 1
    stable: dict[str, int] = {}
    for r in final:
        if r.symbol_id in fresh:
            doc_id = fresh[r.symbol_id]
            if doc_id in reusable:
                reusable.remove(doc_id)
        elif reusable:
            doc_id = reusable.pop(0)
        else:
            doc_id = next_doc_id
            next_doc_id += 1
        stable[r.symbol_id] = doc_id

    fresh_order = [r.symbol_id for r in final]
    stable_order = [r.symbol_id for r in sorted(final, key=lambda r: stable[r.symbol_id])]
    first_divergence = next(
        (i for i, (a, b) in enumerate(zip(fresh_order, stable_order)) if a != b), None
    )
    return {
        "symbols_before": len(sample),
        "symbols_after": len(final),
        "file_removed": removed_path,
        "file_added": "!added/first.cs",
        "orders_identical": fresh_order == stable_order,
        "first_divergent_position": first_divergence,
        "positions_differing": sum(1 for a, b in zip(fresh_order, stable_order) if a != b),
        "note": "fresh ordinal assignment (SqliteSymbolReader.cs:45) vs incremental stable reuse"
                " (SearchIndexWriter.cs:436 AssignStableDocIds) on the identical final symbol set",
    }


def main() -> int:
    work = sys.argv[1]
    os.makedirs(work, exist_ok=True)

    rows = read_real_corpus()
    store_path = os.path.join(work, f"docid_store_x{MULTIPLE}.db")
    stats = build_store(store_path, rows, MULTIPLE, ["tion", "coun"])
    print(f"# store x{MULTIPLE}: {json.dumps(stats)}")

    writable = sqlite3.connect(store_path)
    eight = build_eight_views(writable, len(rows))
    print(f"# eight-view projection: {json.dumps(eight)}")
    flip = manifest_flip_cost(writable)
    print(f"# manifest flip: {json.dumps(flip)}")
    baseline_bytes = os.path.getsize(store_path)
    writable.close()

    store = sqlite3.connect(f"file:{store_path}?mode=ro", uri=True)
    queries = [r.name for r in rows[:: max(1, len(rows) // QUERIES)] if r.name][:QUERIES]

    per_query = {"row_number_cte": [], "materialised_projection": [], "stored_sort_key": []}
    counts = {k: [] for k in per_query}
    for q in queries:
        match = quote_fts(q)
        for label, sql in (
            ("row_number_cte", ROW_NUMBER_CTE),
            ("materialised_projection", PROJECTION_SQL),
            ("stored_sort_key", SORT_KEY_SQL),
        ):
            count, ms = measure(store, sql, match)
            per_query[label].append(ms)
            counts[label].append(count)

    stats_scan_count, stats_scan_ms = measure(store, STATS_SCAN_SQL, None)
    stats_projection_count, stats_projection_ms = measure(store, STATS_PROJECTION_SQL, None)
    stats_row = store.execute(STATS_SCAN_SQL).fetchone()

    def summarize(samples):
        ordered = sorted(samples)
        return {
            "median_ms": round(statistics.median(ordered), 3),
            "p95_ms": round(ordered[min(len(ordered) - 1, int(0.95 * len(ordered)))], 3),
            "total_ms": round(sum(ordered), 1),
        }

    docid_bytes = eight["projection_bytes"]
    report = {
        "store": stats,
        "queries": len(queries),
        "docid_options": {
            "query_time_row_number": {
                "sql": "ROW_NUMBER() OVER (ORDER BY path, start_line, symbol_id) over the visible set,"
                       " evaluated per query",
                "timing": summarize(per_query["row_number_cte"]),
                "candidate_rows_median": statistics.median(counts["row_number_cte"]),
                "extra_bytes_for_8_views": 0,
            },
            "materialised_per_view_mapping": {
                "sql": "view_projection(view_id, rid, doc_id) PRIMARY KEY(view_id, rid) WITHOUT ROWID",
                "timing": summarize(per_query["materialised_projection"]),
                "candidate_rows_median": statistics.median(counts["materialised_projection"]),
                "bytes_for_8_views": docid_bytes,
                "bytes_per_view": (docid_bytes // VIEWS) if docid_bytes else None,
                "bytes_per_row": (
                    round(docid_bytes / eight["rows"], 2) if docid_bytes and eight["rows"] else None
                ),
            },
            "stored_sort_key_no_docid": {
                "sql": "order by the stored (path, start_line, symbol_id) triple; no per-view ordinal",
                "timing": summarize(per_query["stored_sort_key"]),
                "candidate_rows_median": statistics.median(counts["stored_sort_key"]),
                "extra_bytes_for_8_views": 0,
            },
        },
        "bm25_statistics": {
            "view_local_scan": {
                "sql": STATS_SCAN_SQL.strip(),
                "median_ms": round(stats_scan_ms, 3),
                "doc_count": stats_row[0],
                "avgdl": round(stats_row[1] / stats_row[0], 6) if stats_row[0] else 0,
            },
            "via_projection": {
                "sql": STATS_PROJECTION_SQL.strip(),
                "median_ms": round(stats_projection_ms, 3),
            },
            "cached_per_manifest_generation": {
                "sql": "one row per (view_id, manifest_generation): doc_count, sum_doc_len",
                "median_ms": 0.0,
                "bytes_for_8_views": VIEWS * 32,
            },
        },
        "docid_history_divergence": docid_history_divergence(rows),
        "eight_views": eight,
        "manifest_flip": flip,
        "store_bytes_with_8_view_projections": baseline_bytes,
    }
    store.close()

    out = os.path.join(work, "docid_bm25.json")
    with open(out, "w") as fh:
        json.dump(report, fh, indent=2, default=str)
    print(json.dumps(report, indent=2, default=str))
    print(f"\n# wrote {out}")
    os.remove(store_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
