"""Ph0 proof 2: can sqlite-vec apply a view visibility filter BEFORE top-K?

Vectors come from the live `vectors.db` symbol lane, read-only: int8[384],
distance_metric=cosine, the same lane Miller writes, and each vector keeps its
real file path so versions are per-file exactly as the store models them.

Hidden versions are perturbations of those vectors. For every probe, 600 hidden
vectors sit closer than any visible vector - above the 500-candidate semantic
window (`SemanticSearchArm.MaxCandidates`), so a post-filter must starve.

Mechanisms tried:
  metadata_eq     - vec0 metadata column, single-version equality.
  metadata_in     - vec0 metadata column, IN list of the view's visible versions.
  rowid_in_list   - `rowid IN (...)` with visible rowids inlined.
  rowid_in_select - `rowid IN (SELECT ...)` from the per-view projection table.
  partition_key   - vec0 `partition key`; needs one copy of every vector per view.
  postfilter      - no constraint, filtered by the client after top-K.

A mechanism counts as a pre-filter only if it returns the SAME top-K unit set as
a dedicated per-view vector index holding only the visible rows.
"""

from __future__ import annotations

import json
import os
import random
import sqlite3
import statistics
import struct
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from corpus import SEED, SEMANTIC_WINDOW, VECTOR_LANE_DIMS, VEC_EXTENSION  # noqa: E402

LIVE_VECTORS_DB = os.environ.get(
    "MILLER_PH0_VECTORS_DB", "/Users/murphy/source/miller/.miller/vectors.db"
)
MULTIPLES = [1, 5, 20]
TOP_K = SEMANTIC_WINDOW
ADVERSARIAL_HIDDEN_NEARER = 600
PROBES = 12
VIEWS = 8
DIMS = VECTOR_LANE_DIMS

# sqlite-vec rejects a KNN query above this k, which bounds any over-fetch strategy.
VEC_MAX_K = 4096


def connect(path: str, mode: str = "rwc") -> sqlite3.Connection:
    con = sqlite3.connect(f"file:{path}?mode={mode}", uri=True)
    con.enable_load_extension(True)
    con.load_extension(VEC_EXTENSION)
    con.enable_load_extension(False)
    return con


def read_live_vectors() -> tuple[list[tuple[str, bytes]], str]:
    con = connect(LIVE_VECTORS_DB, mode="ro")
    try:
        rows = con.execute(
            "SELECT m.path, vec_to_json(v.embedding) FROM symbol_vectors v"
            " JOIN symbol_vector_map m ON m.rowid_ref = v.rowid ORDER BY v.rowid"
        ).fetchall()
        version = con.execute("SELECT vec_version()").fetchone()[0]
    finally:
        con.close()
    out = []
    for path, text in rows:
        values = [int(v) for v in json.loads(text)]
        out.append((path, struct.pack(f"{len(values)}b", *values)))
    return out, version


def perturb(blob: bytes, rng: random.Random, strength: int) -> bytes:
    values = list(struct.unpack(f"{len(blob)}b", blob))
    for _ in range(strength):
        i = rng.randrange(len(values))
        values[i] = max(-128, min(127, values[i] + rng.randint(-20, 20)))
    return struct.pack(f"{len(values)}b", *values)


SHARED_DDL = """
CREATE VIRTUAL TABLE store_vectors USING vec0(
    embedding  int8[{dims}] distance_metric=cosine,
    version_id integer
);
CREATE TABLE vector_map(
    rid        INTEGER PRIMARY KEY,
    version_id INTEGER NOT NULL,
    unit_key   TEXT NOT NULL
);
CREATE TABLE view_manifest(
    view_id    INTEGER NOT NULL,
    version_id INTEGER NOT NULL,
    PRIMARY KEY(view_id, version_id)
) WITHOUT ROWID;
CREATE TABLE view_projection(
    view_id INTEGER NOT NULL,
    rid     INTEGER NOT NULL,
    PRIMARY KEY(view_id, rid)
) WITHOUT ROWID;
"""

PARTITIONED_DDL = """
CREATE VIRTUAL TABLE partitioned_vectors USING vec0(
    view_id   integer partition key,
    embedding int8[{dims}] distance_metric=cosine
);
"""

DEDICATED_DDL = """
CREATE VIRTUAL TABLE view_vectors USING vec0(
    embedding int8[{dims}] distance_metric=cosine
);
CREATE TABLE vector_map(
    rid      INTEGER PRIMARY KEY,
    unit_key TEXT NOT NULL
);
"""


def build_shared(work: str, vectors: list[tuple[str, bytes]], multiple: int, probes) -> dict:
    rng = random.Random(SEED + 3)
    path_ids = {p: i for i, p in enumerate(sorted({p for p, _ in vectors}))}
    shared_path = os.path.join(work, f"vec_store_x{multiple}.db")
    if os.path.exists(shared_path):
        os.remove(shared_path)

    visible, hidden = [], []
    rid = 0
    for unit, (path, blob) in enumerate(vectors):
        for k in range(multiple):
            rid += 1
            version_id = path_ids[path] * 64 + k
            vec = blob if k == 0 else perturb(blob, rng, strength=48)
            row = (rid, version_id, f"u{unit}:v{k}", vec)
            (visible if k == 0 else hidden).append(row)

    adversarial = 0
    if multiple > 1:
        hidden_version_offset = len(path_ids) * 64
        for probe_index, probe in enumerate(probes):
            for j in range(ADVERSARIAL_HIDDEN_NEARER):
                rid += 1
                hidden.append(
                    (
                        rid,
                        hidden_version_offset + probe_index * 64 + 1,
                        f"adv{probe_index}:{j}",
                        perturb(probe, rng, strength=1),
                    )
                )
                adversarial += 1

    shared = connect(shared_path)
    shared.executescript(SHARED_DDL.format(dims=DIMS))
    started = time.perf_counter()
    all_rows = visible + hidden
    shared.executemany(
        "INSERT INTO store_vectors(rowid, embedding, version_id) VALUES(?,vec_int8(?),?)",
        [(r[0], r[3], r[1]) for r in all_rows],
    )
    shared.executemany(
        "INSERT INTO vector_map(rid, version_id, unit_key) VALUES(?,?,?)",
        [(r[0], r[1], r[2]) for r in all_rows],
    )
    shared.executemany(
        "INSERT INTO view_manifest(view_id, version_id) VALUES(0,?)",
        sorted({(r[1],) for r in visible}),
    )
    shared.executemany(
        "INSERT INTO view_projection(view_id, rid) VALUES(0,?)", [(r[0],) for r in visible]
    )
    shared.commit()
    seconds = time.perf_counter() - started
    shared.close()

    return {
        "path": shared_path,
        "multiple": multiple,
        "store_vectors": len(all_rows),
        "visible_vectors": len(visible),
        "hidden_vectors": len(hidden),
        "adversarial_hidden_nearer_per_probe": ADVERSARIAL_HIDDEN_NEARER if multiple > 1 else 0,
        "visible_versions": len({r[1] for r in visible}),
        "insert_seconds": round(seconds, 2),
        "bytes": os.path.getsize(shared_path),
    }


def build_oracle(work: str, vectors: list[tuple[str, bytes]]) -> dict:
    oracle_path = os.path.join(work, "vec_dedicated.db")
    if os.path.exists(oracle_path):
        os.remove(oracle_path)
    con = connect(oracle_path)
    con.executescript(DEDICATED_DDL.format(dims=DIMS))
    started = time.perf_counter()
    con.executemany(
        "INSERT INTO view_vectors(rowid, embedding) VALUES(?,vec_int8(?))",
        [(i + 1, blob) for i, (_, blob) in enumerate(vectors)],
    )
    con.executemany(
        "INSERT INTO vector_map(rid, unit_key) VALUES(?,?)",
        [(i + 1, f"u{i}:v0") for i in range(len(vectors))],
    )
    con.commit()
    seconds = time.perf_counter() - started
    con.close()
    return {
        "path": oracle_path,
        "vectors": len(vectors),
        "insert_seconds": round(seconds, 2),
        "bytes": os.path.getsize(oracle_path),
    }


def build_partitioned(work: str, vectors: list[tuple[str, bytes]]) -> dict:
    part_path = os.path.join(work, "vec_partitioned_8views.db")
    if os.path.exists(part_path):
        os.remove(part_path)
    con = connect(part_path)
    con.executescript(PARTITIONED_DDL.format(dims=DIMS))
    started = time.perf_counter()
    rows = []
    for view in range(VIEWS):
        rows.extend(
            (view * 100_000_000 + i + 1, view, blob) for i, (_, blob) in enumerate(vectors)
        )
    con.executemany(
        "INSERT INTO partitioned_vectors(rowid, view_id, embedding) VALUES(?,?,vec_int8(?))", rows
    )
    con.commit()
    seconds = time.perf_counter() - started
    con.close()
    return {
        "path": part_path,
        "views": VIEWS,
        "rows": len(rows),
        "insert_seconds": round(seconds, 2),
        "bytes": os.path.getsize(part_path),
    }


def attempt(con: sqlite3.Connection, sql: str, params: tuple):
    try:
        started = time.perf_counter()
        rows = con.execute(sql, params).fetchall()
        return rows, (time.perf_counter() - started) * 1000.0, None
    except sqlite3.Error as exc:
        return None, None, f"{type(exc).__name__}: {exc}"


def run(work: str) -> dict:
    vectors, vec_version = read_live_vectors()
    print(f"# live vectors: {len(vectors)} int8[{DIMS}] rows, sqlite-vec {vec_version}")
    step = max(1, len(vectors) // PROBES)
    probes = [blob for _, blob in vectors[::step]][:PROBES]

    oracle_stats = build_oracle(work, vectors)
    partition_stats = build_partitioned(work, vectors)
    print(f"# dedicated per-view oracle: {json.dumps(oracle_stats)}")
    print(f"# 8-view partition-key table: {json.dumps(partition_stats)}")

    report = {
        "vec_version": vec_version,
        "lane": f"int8[{DIMS}] distance_metric=cosine",
        "live_symbol_vectors": len(vectors),
        "top_k": TOP_K,
        "probes": len(probes),
        "dedicated_oracle": oracle_stats,
        "partition_key_8_views": partition_stats,
        "multiples": {},
    }

    oracle = connect(oracle_stats["path"], mode="ro")
    partitioned = connect(partition_stats["path"], mode="ro")

    for multiple in MULTIPLES:
        stats = build_shared(work, vectors, multiple, probes)
        print(f"\n## vector store x{multiple}: {json.dumps(stats)}")
        shared = connect(stats["path"], mode="ro")

        visible_versions = [
            r[0] for r in shared.execute("SELECT version_id FROM view_manifest WHERE view_id = 0")
        ]
        visible_rids = [
            r[0] for r in shared.execute("SELECT rid FROM view_projection WHERE view_id = 0")
        ]
        version_in = ",".join(str(v) for v in visible_versions)
        rid_in = ",".join(str(v) for v in visible_rids)

        acc: dict[str, dict] = {}

        def record(name, ms, got, expected, error=None, note=None, comparable=True):
            entry = acc.setdefault(
                name, {"supported": True, "error": None, "ms": [], "equal": [], "note": note,
                       "returned": [], "comparable": comparable}
            )
            if error is not None:
                entry["supported"] = False
                entry["error"] = error
                return
            entry["ms"].append(ms)
            entry["returned"].append(len(got) if got is not None else 0)
            if comparable:
                entry["equal"].append(got == expected)

        for probe in probes:
            expected = {
                r[0]
                for r in oracle.execute(
                    "SELECT m.unit_key FROM view_vectors v JOIN vector_map m ON m.rid = v.rowid"
                    " WHERE v.embedding MATCH vec_int8(?) AND k = ?",
                    (probe, TOP_K),
                )
            }

            base = (
                "SELECT m.unit_key FROM store_vectors v JOIN vector_map m ON m.rid = v.rowid"
                " WHERE v.embedding MATCH vec_int8(?) AND k = ?"
            )

            rows, ms, err = attempt(shared, base, (probe, TOP_K))
            record(
                "postfilter", ms, ({r[0] for r in rows} & expected) if rows is not None else None,
                expected, error=err,
                note="no constraint; client filters the top-K afterwards",
            )

            rows, ms, err = attempt(shared, base, (probe, VEC_MAX_K))
            record(
                "postfilter_overfetch_max_k", ms,
                ({r[0] for r in rows} & expected) if rows is not None else None, expected,
                error=err,
                note=f"k = {VEC_MAX_K}, sqlite-vec's hard ceiling ({VEC_MAX_K / TOP_K:.1f}x the"
                     " semantic window), then client-filtered",
            )

            rows, ms, err = attempt(
                shared, base + " AND v.version_id = ?", (probe, TOP_K, visible_versions[0])
            )
            record(
                "metadata_eq", ms, None, None, error=err, comparable=False,
                note="single-version equality; a view spans many versions so this alone"
                     " cannot express visibility",
            )

            rows, ms, err = attempt(
                shared, base + f" AND v.version_id IN ({version_in})", (probe, TOP_K)
            )
            record(
                "metadata_in", ms, {r[0] for r in rows} if rows is not None else None, expected,
                error=err, note=f"IN list of {len(visible_versions)} visible version ids",
            )

            rows, ms, err = attempt(shared, base + f" AND v.rowid IN ({rid_in})", (probe, TOP_K))
            record(
                "rowid_in_list", ms, {r[0] for r in rows} if rows is not None else None, expected,
                error=err, note=f"{len(visible_rids)} visible rowids inlined in the SQL",
            )

            rows, ms, err = attempt(
                shared,
                base + " AND v.rowid IN (SELECT rid FROM view_projection WHERE view_id = 0)",
                (probe, TOP_K),
            )
            record(
                "rowid_in_select", ms, {r[0] for r in rows} if rows is not None else None,
                expected, error=err,
                note="visible rowids read from the per-view projection table",
            )

            rows, ms, err = attempt(
                partitioned,
                "SELECT v.rowid FROM partitioned_vectors v"
                " WHERE v.embedding MATCH vec_int8(?) AND k = ? AND v.view_id = 0",
                (probe, TOP_K),
            )
            record(
                "partition_key", ms,
                {f"u{r[0] - 1}:v0" for r in rows} if rows is not None else None, expected,
                error=err,
                note="one copy of every vector per view; correct by construction,"
                     " cost is VIEWS x storage",
            )

            rows, ms, err = attempt(
                oracle,
                "SELECT m.unit_key FROM view_vectors v JOIN vector_map m ON m.rid = v.rowid"
                " WHERE v.embedding MATCH vec_int8(?) AND k = ?",
                (probe, TOP_K),
            )
            record(
                "dedicated_per_view", ms,
                {r[0] for r in rows} if rows is not None else None, expected, error=err,
            )

        summary = {}
        for name, entry in acc.items():
            if not entry["supported"]:
                summary[name] = {"supported": False, "error": entry["error"], "note": entry["note"]}
                continue
            ordered = sorted(entry["ms"])
            summary[name] = {
                "supported": True,
                "note": entry["note"],
                "median_ms": round(statistics.median(ordered), 3),
                "p95_ms": round(ordered[min(len(ordered) - 1, int(0.95 * len(ordered)))], 3),
                "rows_returned_median": statistics.median(entry["returned"]),
                "matches_dedicated_top_k": (
                    all(entry["equal"]) if entry["comparable"] else "not-applicable"
                ),
                "probes_checked": len(entry["equal"]),
            }

        report["multiples"][str(multiple)] = {"build": stats, "mechanisms": summary}
        print(json.dumps(report["multiples"][str(multiple)], indent=2, default=str))
        shared.close()
        os.remove(stats["path"])

    oracle.close()
    partitioned.close()

    print("\n## k-ceiling probe: more hidden-nearer vectors than the engine's maximum k")
    report["ceiling_probe"] = ceiling_probe(work, vectors)
    print(json.dumps(report["ceiling_probe"], indent=2))
    return report


CEILING_HIDDEN_NEARER = 6000


def ceiling_probe(work: str, vectors: list[tuple[str, bytes]]) -> dict:
    """Does over-fetching to sqlite-vec's maximum k survive a denser hidden set?

    One probe gets more hidden-nearer vectors than the engine's k ceiling, which
    is the case an over-fetch strategy cannot answer at any k.
    """
    import random as _random

    rng = _random.Random(SEED + 9)
    path = os.path.join(work, "vec_ceiling.db")
    if os.path.exists(path):
        os.remove(path)
    con = connect(path)
    con.executescript(SHARED_DDL.format(dims=DIMS))

    probe = vectors[0][1]
    visible = [(i + 1, 0, f"u{i}:v0", blob) for i, (_, blob) in enumerate(vectors)]
    hidden = [
        (len(vectors) + j + 1, 1, f"adv:{j}", perturb(probe, rng, strength=1))
        for j in range(CEILING_HIDDEN_NEARER)
    ]
    con.executemany(
        "INSERT INTO store_vectors(rowid, embedding, version_id) VALUES(?,vec_int8(?),?)",
        [(r[0], r[3], r[1]) for r in visible + hidden],
    )
    con.executemany(
        "INSERT INTO vector_map(rid, version_id, unit_key) VALUES(?,?,?)",
        [(r[0], r[1], r[2]) for r in visible + hidden],
    )
    con.executemany(
        "INSERT INTO view_projection(view_id, rid) VALUES(0,?)", [(r[0],) for r in visible]
    )
    con.commit()
    con.close()

    con = connect(path, mode="ro")
    oracle_keys = {f"u{i}:v0" for i in range(len(vectors))}
    base = (
        "SELECT m.unit_key FROM store_vectors v JOIN vector_map m ON m.rid = v.rowid"
        " WHERE v.embedding MATCH vec_int8(?) AND k = ?"
    )
    out = {}
    for label, k in (("postfilter_k500", TOP_K), ("postfilter_k_max", VEC_MAX_K)):
        rows, ms, err = attempt(con, base, (probe, k))
        visible_hits = len({r[0] for r in rows} & oracle_keys) if rows else 0
        out[label] = {
            "error": err,
            "ms": round(ms, 3) if ms else None,
            "visible_rows_recovered": visible_hits,
            "visible_rows_wanted": TOP_K,
        }
    rows, ms, err = attempt(
        con,
        base + " AND v.rowid IN (SELECT rid FROM view_projection WHERE view_id = 0)",
        (probe, TOP_K),
    )
    out["rowid_in_select"] = {
        "error": err,
        "ms": round(ms, 3) if ms else None,
        "visible_rows_recovered": len({r[0] for r in rows} & oracle_keys) if rows else 0,
        "visible_rows_wanted": TOP_K,
    }
    con.close()
    os.remove(path)
    return {
        "hidden_nearer_vectors": CEILING_HIDDEN_NEARER,
        "engine_k_ceiling": VEC_MAX_K,
        "visible_vectors": len(vectors),
        "results": out,
    }


def main() -> int:
    work = sys.argv[1]
    os.makedirs(work, exist_ok=True)
    if not os.path.exists(VEC_EXTENSION):
        print(f"# PARTIAL: sqlite-vec extension not found at {VEC_EXTENSION}; nothing proven")
        return 2
    if not os.path.exists(LIVE_VECTORS_DB):
        print(f"# PARTIAL: live vectors.db not found at {LIVE_VECTORS_DB}; nothing proven")
        return 2

    report = run(work)
    out = os.path.join(work, "vec_prefilter.json")
    with open(out, "w") as fh:
        json.dump(report, fh, indent=2, default=str)
    print(f"\n# wrote {out}")

    worst = report["multiples"][str(max(MULTIPLES))]["mechanisms"]
    winners = [
        name
        for name, entry in worst.items()
        if entry.get("supported")
        and entry.get("matches_dedicated_top_k") is True
        and name != "dedicated_per_view"
    ]
    print(f"# PRE-FILTER MECHANISMS reproducing the dedicated top-K at x{max(MULTIPLES)}: {winners}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
