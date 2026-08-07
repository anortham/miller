"""Ph0 proof 1: FTS recall-set equivalence with visibility joined inside retrieval.

Every recall set is compared to a dedicated per-view index built from the same
visible rows, by exact Python set comparison on (version_id, symbol_id) with the
symmetric difference reported.

Query shapes:
  postfilter  - the shipped shape with no visibility predicate, filtered in the
                client after ORDER BY rank LIMIT. Expected to FAIL.
  prefilter   - visibility joined through view_manifest INSIDE the query.
  temptable   - visibility materialised into a temp rowid table, joined inside.

Trigram window rules:
  rank        - today's rule, ORDER BY symbols_trigram.rank (FTS5 bm25).
  density     - a stored, corpus-independent key: collapsed_len then name length.

The rank rule reads corpus-wide statistics that a family-shared store does not
share with a per-view index, so `mechanism_probe` builds a synthetic history that
isolates whether that can move window membership.
"""

from __future__ import annotations

import json
import os
import random
import sqlite3
import statistics
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from corpus import (  # noqa: E402
    SEED,
    TRIGRAM_WINDOW,
    Row,
    build_dedicated,
    build_store,
    quote_fts,
    read_real_corpus,
)

MULTIPLES = [1, 5, 20]
WORD_QUERIES = 120
TRIGRAM_QUERIES = 120
ADVERSARIAL_QUERIES = 5
REPEATS = 3

VISIBLE_JOIN = "JOIN view_manifest m ON m.view_id = 0 AND m.version_id = s.version_id"
TEMP_JOIN = "JOIN visible_rid v ON v.rid = symbols_trigram.rowid"
PROJECTION_JOIN = "JOIN view_projection p ON p.view_id = 0 AND p.rid = symbols_trigram.rowid"

SHAPES = ("postfilter", "prefilter", "temptable", "projection")

WORD_SQL = {
    "postfilter": "SELECT s.rid FROM symbols_fts"
    " JOIN store_symbols s ON s.rid = symbols_fts.rowid WHERE body MATCH ?",
    "prefilter": "SELECT s.rid FROM symbols_fts"
    " JOIN store_symbols s ON s.rid = symbols_fts.rowid"
    " JOIN view_manifest m ON m.view_id = 0 AND m.version_id = s.version_id"
    " WHERE body MATCH ?",
    "temptable": "SELECT s.rid FROM symbols_fts"
    " JOIN visible_rid v ON v.rid = symbols_fts.rowid"
    " JOIN store_symbols s ON s.rid = symbols_fts.rowid WHERE body MATCH ?",
    "projection": "SELECT s.rid FROM symbols_fts"
    " JOIN view_projection p ON p.view_id = 0 AND p.rid = symbols_fts.rowid"
    " JOIN store_symbols s ON s.rid = symbols_fts.rowid WHERE body MATCH ?",
}

WINDOW_ORDER = {
    "rank": "symbols_trigram.rank, length(s.name), s.path, s.start_line, s.symbol_id",
    "density": "s.collapsed_len, length(s.name), s.path, s.start_line, s.symbol_id",
}


def trigram_sql(shape: str, window: str, limit: int = TRIGRAM_WINDOW) -> str:
    joins = "JOIN store_symbols s ON s.rid = symbols_trigram.rowid"
    if shape == "prefilter":
        joins += " " + VISIBLE_JOIN
    elif shape == "temptable":
        joins = TEMP_JOIN + " " + joins
    elif shape == "projection":
        joins = PROJECTION_JOIN + " " + joins
    return (
        f"SELECT s.rid FROM symbols_trigram {joins}"
        f" WHERE symbols_trigram MATCH ?"
        f" ORDER BY {WINDOW_ORDER[window]} LIMIT {limit}"
    )


TRIGRAM_UNCAPPED = (
    "SELECT COUNT(*) FROM symbols_trigram"
    " JOIN store_symbols s ON s.rid = symbols_trigram.rowid"
    " WHERE symbols_trigram MATCH ?"
)
TRIGRAM_UNCAPPED_VISIBLE = (
    "SELECT COUNT(*) FROM symbols_trigram"
    " JOIN store_symbols s ON s.rid = symbols_trigram.rowid"
    " JOIN view_manifest m ON m.view_id = 0 AND m.version_id = s.version_id"
    " WHERE symbols_trigram MATCH ?"
)


def build_query_sets(rows: list[Row]) -> tuple[list[str], list[str], list[str]]:
    rng = random.Random(SEED + 1)

    word_queries: list[str] = []
    seen: set[str] = set()
    for r in rng.sample(rows, min(len(rows), WORD_QUERIES * 6)):
        tokens = [t for t in r.body.split() if len(t) >= 4]
        if not tokens:
            continue
        token = tokens[rng.randrange(len(tokens))]
        if token in seen:
            continue
        seen.add(token)
        word_queries.append(token)
        if len(word_queries) >= WORD_QUERIES:
            break

    substring_counts: dict[str, int] = {}
    for collapsed in (r.name_collapsed for r in rows):
        for length in (4, 5):
            for start in range(1, max(1, len(collapsed) - length)):
                sub = collapsed[start : start + length]
                if len(sub) == length:
                    substring_counts[sub] = substring_counts.get(sub, 0) + 1

    candidates = sorted(s for s, n in substring_counts.items() if 5 <= n <= 400)
    trigram_queries = rng.sample(candidates, min(TRIGRAM_QUERIES, len(candidates)))

    crowded = sorted(
        (s for s, n in substring_counts.items() if n >= 60 and s.isalpha()),
        key=lambda s: (-substring_counts[s], s),
    )
    return word_queries, trigram_queries, crowded[:ADVERSARIAL_QUERIES]


def keyset(con: sqlite3.Connection, rids: list[int]) -> set[tuple[int, str]]:
    out: set[tuple[int, str]] = set()
    for i in range(0, len(rids), 500):
        part = rids[i : i + 500]
        placeholders = ",".join("?" * len(part))
        out.update(
            con.execute(
                f"SELECT version_id, symbol_id FROM store_symbols WHERE rid IN ({placeholders})",
                part,
            ).fetchall()
        )
    return out


def install_temp_visibility(con: sqlite3.Connection) -> float:
    started = time.perf_counter()
    con.execute("DROP TABLE IF EXISTS temp.visible_rid")
    con.execute("CREATE TEMP TABLE visible_rid(rid INTEGER PRIMARY KEY)")
    con.execute(
        "INSERT INTO temp.visible_rid(rid) SELECT s.rid FROM store_symbols s"
        " JOIN view_manifest m ON m.view_id = 0 AND m.version_id = s.version_id"
    )
    con.commit()
    return time.perf_counter() - started


def timed(con: sqlite3.Connection, sql: str, param: str) -> tuple[list[int], float]:
    samples = []
    rids: list[int] = []
    for _ in range(REPEATS):
        started = time.perf_counter()
        rids = [row[0] for row in con.execute(sql, (param,))]
        samples.append(time.perf_counter() - started)
    return rids, statistics.median(samples) * 1000.0


def summarize(samples: list[float]) -> dict:
    ordered = sorted(samples)
    return {
        "median_ms": round(statistics.median(ordered), 3),
        "p95_ms": round(ordered[min(len(ordered) - 1, int(0.95 * len(ordered)))], 3),
        "total_ms": round(sum(ordered), 1),
    }


def run_word_arm(store, oracle, queries: list[str]) -> dict:
    timings = {k: [] for k in (*SHAPES, "dedicated")}
    mismatch = {"prefilter": [], "temptable": [], "projection": []}
    store_candidates, visible_candidates = [], []

    for q in queries:
        match = quote_fts(q)
        raw = {}
        for shape, sql in WORD_SQL.items():
            raw[shape], ms = timed(store, sql, match)
            timings[shape].append(ms)
        raw_oracle, ms = timed(oracle, WORD_SQL["postfilter"], match)
        timings["dedicated"].append(ms)

        expected = keyset(oracle, raw_oracle)
        store_candidates.append(len(raw["postfilter"]))
        visible_candidates.append(len(raw["prefilter"]))
        for shape in ("prefilter", "temptable", "projection"):
            got = keyset(store, raw[shape])
            if got != expected:
                mismatch[shape].append(
                    {
                        "query": q,
                        "expected_n": len(expected),
                        "got_n": len(got),
                        "missing": sorted(expected - got)[:3],
                        "extra": sorted(got - expected)[:3],
                    }
                )

    return {
        "queries": len(queries),
        "equivalent_prefilter": not mismatch["prefilter"],
        "equivalent_temptable": not mismatch["temptable"],
        "equivalent_projection": not mismatch["projection"],
        "mismatches": {k: v[:3] for k, v in mismatch.items()},
        "mismatch_counts": {k: len(v) for k, v in mismatch.items()},
        "timing": {k: summarize(v) for k, v in timings.items()},
        "candidate_rows_handed_to_ranker": {
            "no_visibility_median": statistics.median(store_candidates),
            "visible_median": statistics.median(visible_candidates),
            "no_visibility_total": sum(store_candidates),
            "visible_total": sum(visible_candidates),
            "amplification": round(
                sum(store_candidates) / max(1, sum(visible_candidates)), 2
            ),
        },
    }


def run_trigram_arm(store, oracle, queries: list[str], window: str) -> dict:
    sql = {shape: trigram_sql(shape, window) for shape in SHAPES}
    timings = {k: [] for k in (*SHAPES, "dedicated")}
    mismatch = {"prefilter": [], "temptable": [], "projection": []}
    starved = []
    hidden_matches = []
    window_saturated = 0

    for q in queries:
        match = quote_fts(q)
        raw = {}
        for shape, statement in sql.items():
            raw[shape], ms = timed(store, statement, match)
            timings[shape].append(ms)
        raw_oracle, ms = timed(oracle, sql["postfilter"], match)
        timings["dedicated"].append(ms)

        total = store.execute(TRIGRAM_UNCAPPED, (match,)).fetchone()[0]
        visible_total = store.execute(TRIGRAM_UNCAPPED_VISIBLE, (match,)).fetchone()[0]
        hidden_matches.append(total - visible_total)
        if visible_total > TRIGRAM_WINDOW:
            window_saturated += 1

        expected = keyset(oracle, raw_oracle)
        for shape in ("prefilter", "temptable", "projection"):
            got = keyset(store, raw[shape])
            if got != expected:
                mismatch[shape].append(
                    {
                        "query": q,
                        "expected_n": len(expected),
                        "got_n": len(got),
                        "symmetric_difference": len(expected ^ got),
                        "missing": sorted(expected - got)[:3],
                        "extra": sorted(got - expected)[:3],
                    }
                )
        post = keyset(store, raw["postfilter"]) & expected
        if post != expected:
            starved.append(
                {
                    "query": q,
                    "expected_n": len(expected),
                    "post_filter_survivors": len(post),
                    "lost": len(expected - post),
                    "hidden_matches_in_store": total - visible_total,
                }
            )

    return {
        "window_rule": window,
        "queries": len(queries),
        "queries_whose_visible_matches_exceed_window": window_saturated,
        "hidden_matches_median": statistics.median(hidden_matches),
        "hidden_matches_max": max(hidden_matches),
        "equivalent_prefilter": not mismatch["prefilter"],
        "equivalent_temptable": not mismatch["temptable"],
        "equivalent_projection": not mismatch["projection"],
        "mismatches": {k: v[:3] for k, v in mismatch.items()},
        "mismatch_counts": {k: len(v) for k, v in mismatch.items()},
        "post_filter_starved_queries": len(starved),
        "post_filter_examples": starved[:5],
        "timing": {k: summarize(v) for k, v in timings.items()},
    }


MECHANISM_QUERY = "abcd"
MECHANISM_VISIBLE = 300
MECHANISM_HIDDEN = 40000


def mechanism_probe(work: str) -> dict:
    """Isolate whether FTS5 rank makes window membership corpus-dependent.

    Visible rows vary in both phrase frequency and collapsed length; hidden rows
    are long and non-matching, so they move only the corpus statistics that
    FTS5's bm25 length normalisation reads. If the rank-ordered window is
    corpus-independent the two indexes agree; if it is not, they diverge.
    """
    rng = random.Random(SEED + 2)
    visible = []
    for i in range(MECHANISM_VISIBLE):
        tf = 1 + (i % 3)
        pad = "z" * (2 + (i * 7) % 60)
        text = pad.join([MECHANISM_QUERY] * tf) + "y" * ((i * 13) % 40)
        visible.append((i + 1, text, text))

    def build(path: str, hidden: int):
        if os.path.exists(path):
            os.remove(path)
        con = sqlite3.connect(path)
        con.execute(
            "CREATE VIRTUAL TABLE t USING fts5(name_collapsed, qual_collapsed, tokenize='trigram')"
        )
        con.execute("CREATE TABLE meta_rows(rid INTEGER PRIMARY KEY, collapsed_len INTEGER)")
        con.executemany("INSERT INTO t(rowid, name_collapsed, qual_collapsed) VALUES(?,?,?)", visible)
        con.executemany(
            "INSERT INTO meta_rows(rid, collapsed_len) VALUES(?,?)",
            [(r, len(a) + len(b)) for r, a, b in visible],
        )
        if hidden:
            rows = []
            metas = []
            for j in range(hidden):
                text = "".join(rng.choice("mnopqrstuvw") for _ in range(400))
                rows.append((100000 + j, text, text))
                metas.append((100000 + j, len(text) * 2))
            con.executemany("INSERT INTO t(rowid, name_collapsed, qual_collapsed) VALUES(?,?,?)", rows)
            con.executemany("INSERT INTO meta_rows(rid, collapsed_len) VALUES(?,?)", metas)
        con.commit()
        return con

    dedicated = build(os.path.join(work, "mechanism_dedicated.db"), 0)
    shared = build(os.path.join(work, "mechanism_shared.db"), MECHANISM_HIDDEN)
    match = quote_fts(MECHANISM_QUERY)

    order = {
        "rank": "rank, t.rowid",
        "density": "m.collapsed_len, t.rowid",
    }
    results = {}
    for rule, clause in order.items():
        base = (
            "SELECT t.rowid FROM t JOIN meta_rows m ON m.rid = t.rowid"
            " WHERE t MATCH ?{visible} ORDER BY " + clause + " LIMIT ?"
        )
        per_k = {}
        for k in (50, 100, 200):
            expected = [
                r[0] for r in dedicated.execute(base.format(visible=""), (match, k))
            ]
            got = [
                r[0]
                for r in shared.execute(
                    base.format(visible=" AND t.rowid < 100000"), (match, k)
                )
            ]
            per_k[str(k)] = {
                "set_equal": set(expected) == set(got),
                "order_equal": expected == got,
                "symmetric_difference": len(set(expected) ^ set(got)),
            }
        results[rule] = per_k

    dedicated.close()
    shared.close()
    return {
        "visible_rows": MECHANISM_VISIBLE,
        "hidden_non_matching_rows": MECHANISM_HIDDEN,
        "phrase_frequency_range": "1..3",
        "results": results,
    }


def explain(con: sqlite3.Connection, sql: str, param: str) -> list[str]:
    return [row[3] for row in con.execute("EXPLAIN QUERY PLAN " + sql, (param,))]


def main() -> int:
    work = sys.argv[1]
    os.makedirs(work, exist_ok=True)

    started = time.perf_counter()
    rows = read_real_corpus()
    print(
        f"# real corpus: {len(rows)} symbols, {len({r.path for r in rows})} paths"
        f" ({time.perf_counter() - started:.1f}s)"
    )

    word_q, tri_q, adv_q = build_query_sets(rows)
    print(f"# query sets: word={len(word_q)} trigram={len(tri_q)} adversarial={adv_q}")

    oracle_path = os.path.join(work, "dedicated_view0.db")
    oracle_stats = build_dedicated(oracle_path, rows)
    print("# dedicated per-view oracle:", json.dumps(oracle_stats))
    oracle = sqlite3.connect(f"file:{oracle_path}?mode=ro", uri=True)

    report = {
        "sqlite_version": sqlite3.sqlite_version,
        "oracle": oracle_stats,
        "adversarial_queries": adv_q,
        "multiples": {},
    }

    for multiple in MULTIPLES:
        store_path = os.path.join(work, f"store_x{multiple}.db")
        stats = build_store(store_path, rows, multiple, adv_q)
        print(f"\n## stored-version multiple x{multiple}: {json.dumps(stats)}")
        store = sqlite3.connect(f"file:{store_path}?mode=ro", uri=True)
        temp_seconds = install_temp_visibility(store)

        entry = {
            "build": stats,
            "temp_visibility_build_seconds": round(temp_seconds, 3),
            "word": run_word_arm(store, oracle, word_q),
            "trigram_rank_window": run_trigram_arm(store, oracle, tri_q, "rank"),
            "trigram_density_window": run_trigram_arm(store, oracle, tri_q, "density"),
            "adversarial_rank_window": run_trigram_arm(store, oracle, adv_q, "rank"),
            "adversarial_density_window": run_trigram_arm(store, oracle, adv_q, "density"),
            "query_plans": {
                f"trigram_{shape}": explain(
                    store, trigram_sql(shape, "rank"), quote_fts(adv_q[0])
                )
                for shape in SHAPES
            }
            | {
                f"word_{shape}": explain(store, WORD_SQL[shape], quote_fts(word_q[0]))
                for shape in SHAPES
            },
        }
        report["multiples"][str(multiple)] = entry
        print(json.dumps(entry, indent=2, default=str))
        store.close()
        os.remove(store_path)

    oracle.close()

    print("\n## mechanism probe: is the FTS5 rank window corpus-dependent?")
    probe = mechanism_probe(work)
    report["mechanism_probe"] = probe
    print(json.dumps(probe, indent=2))

    out = os.path.join(work, "fts_equivalence.json")
    with open(out, "w") as fh:
        json.dump(report, fh, indent=2, default=str)
    print(f"\n# wrote {out}")

    for label, keys in (
        ("rank window (today's rule)", ("word", "trigram_rank_window", "adversarial_rank_window")),
        (
            "density window (corpus-independent)",
            ("word", "trigram_density_window", "adversarial_density_window"),
        ),
    ):
        ok = all(
            report["multiples"][str(m)][arm][f"equivalent_{shape}"]
            for m in MULTIPLES
            for arm in keys
            for shape in ("prefilter", "temptable", "projection")
        )
        print(f"# EQUIVALENCE vs dedicated per-view index, {label}: {'PASS' if ok else 'FAIL'}")
    density_safe = all(
        v["set_equal"] for v in probe["results"]["density"].values()
    )
    rank_safe = all(v["set_equal"] for v in probe["results"]["rank"].values())
    print(f"# MECHANISM PROBE: rank window corpus-independent={rank_safe},"
          f" density window corpus-independent={density_safe}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
