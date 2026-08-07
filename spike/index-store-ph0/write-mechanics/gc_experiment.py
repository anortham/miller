#!/usr/bin/env python3
"""GC / physical-reclamation instrument for the versioned index store.

Tests, on a synthetic version-keyed store whose row shapes are sized from the
live Miller artifact:

1. Do version-cohort deletes plus staged ``PRAGMA incremental_vacuum`` shrink
   the FILE (stat, not freelist counts)?
2. Negative control: do the identical deletes reclaim anything with
   ``auto_vacuum=NONE``?
3. Does the delete PATTERN matter -- a whole-epoch contiguous sweep versus the
   realistic retention sweep that drops old generations of every path?
4. FTS5 sidecar: does the page-limited ``merge`` command release space, how
   long does a bounded round take, and how does it compare with the unbounded
   ``optimize``?
5. Does FTS5 ``secure-delete`` (persistent) plus core ``secure_delete``
   (per-connection) actually erase deleted content from the file bytes?

Usage: gc_experiment.py <workdir> <outdir> <file_versions> <generations_per_path>
"""

from __future__ import annotations

import json
import os
import shutil
import sqlite3
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "lib"))

import shapes  # noqa: E402
import store  # noqa: E402

SENTINEL = "zqxjvwkzsentinelphzero" + "0123456789abcdef" * 2
CHUNK_VERSIONS = 40
VACUUM_PAGES_PER_STAGE = 2000
MERGE_PAGES = 64


def log(msg: str) -> None:
    print(f"[gc] {msg}", flush=True)


def incremental_vacuum(conn: sqlite3.Connection, pages: int) -> None:
    """Drive ``PRAGMA incremental_vacuum(N)`` to completion.

    A bare ``Connection.execute`` frees exactly ONE page: Python's sqlite3
    steps the statement once and stops. The rows must be drained for the full
    page budget to be applied.
    """
    conn.execute(f"PRAGMA incremental_vacuum({pages})").fetchall()


def build_store(path: str, auto_vacuum: str, versions: int, generations: int) -> dict:
    started = time.monotonic()
    conn = store.create(path, store.STORE_DDL, auto_vacuum=auto_vacuum)
    rows = 0
    conn.execute("BEGIN")
    for version_id in range(1, versions + 1):
        file_index = (version_id - 1) // generations
        generation = (version_id - 1) % generations
        path_ = shapes.version_path(file_index)
        factory = shapes.VersionRowFactory(path_, generation, "fp-v4-0001")
        conn.execute(
            "INSERT INTO file_versions VALUES (?,?,?,?,?,1)",
            (version_id, path_, factory.content_hash, "fp-v4-0001", generation),
        )
        conn.executemany(store.STORE_INSERTS["symbols"], factory.symbols(version_id))
        conn.executemany(
            store.STORE_INSERTS["reference_sites"], factory.reference_sites(version_id)
        )
        conn.executemany(
            store.STORE_INSERTS["identifiers"], factory.identifiers(version_id)
        )
        rows += shapes.ROWS_PER_FILE_VERSION
        if version_id % CHUNK_VERSIONS == 0:
            conn.execute("COMMIT")
            conn.execute("BEGIN")
    conn.execute("COMMIT")
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    stats = btree_bytes(conn)
    conn.close()
    elapsed = time.monotonic() - started
    size = store.main_file_bytes(path)
    log(
        f"built {os.path.basename(path)} auto_vacuum={auto_vacuum} versions={versions} "
        f"rows={rows} in {elapsed:.1f}s size={size / 1e9:.3f} GB"
    )
    return {
        "rows": rows,
        "build_seconds": round(elapsed, 2),
        "file_bytes": size,
        "bytes_per_row": round(size / rows, 1),
        "btree_bytes": stats,
    }


def btree_bytes(conn: sqlite3.Connection) -> dict:
    try:
        return {
            name: total
            for name, total in conn.execute(
                "SELECT name, SUM(pgsize) FROM dbstat GROUP BY name ORDER BY 2 DESC"
            )
        }
    except sqlite3.OperationalError as exc:
        return {"error": str(exc)}


def victim_versions(pattern: str, versions: int, generations: int) -> list[int]:
    """Version ids a retention sweep would drop.

    ``retention_scatter`` drops the two oldest generations of every path, which
    is what a "keep the newest K versions per file" policy actually does; the
    victims are spread across the whole version_id space.

    ``epoch_contiguous`` drops the oldest 40% of version ids as one block, the
    best case a whole-epoch sweep can hope for.
    """
    if pattern == "retention_scatter":
        return [
            v for v in range(1, versions + 1) if ((v - 1) % generations) < 2
        ]
    if pattern == "epoch_contiguous":
        cutoff = int(versions * 2 / generations)
        return list(range(1, cutoff + 1))
    raise ValueError(pattern)


def delete_versions(path: str, victims: list[int]) -> dict:
    conn = store.connect(path)
    started = time.monotonic()
    conn.execute("CREATE TEMP TABLE victim(version_id INTEGER PRIMARY KEY)")
    conn.executemany("INSERT INTO victim VALUES (?)", ((v,) for v in victims))
    conn.execute("BEGIN")
    for table in ("identifiers", "reference_sites", "symbols", "file_versions"):
        conn.execute(
            f"DELETE FROM {table} WHERE version_id IN (SELECT version_id FROM victim)"
        )
    conn.execute("COMMIT")
    delete_seconds = time.monotonic() - started
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    result = {
        "victims": len(victims),
        "delete_seconds": round(delete_seconds, 2),
        "freelist_pages": conn.execute("PRAGMA freelist_count").fetchone()[0],
        "page_count": conn.execute("PRAGMA page_count").fetchone()[0],
        "file_bytes": store.main_file_bytes(path),
    }
    conn.close()
    return result


def staged_incremental_vacuum(path: str, pages_per_stage: int) -> dict:
    conn = store.connect(path)
    stages = []
    total_started = time.monotonic()
    while len(stages) < 100_000:
        before = conn.execute("PRAGMA freelist_count").fetchone()[0]
        if before == 0:
            break
        started = time.monotonic()
        incremental_vacuum(conn, pages_per_stage)
        elapsed = time.monotonic() - started
        after = conn.execute("PRAGMA freelist_count").fetchone()[0]
        stages.append(
            {
                "freelist_before": before,
                "freelist_after": after,
                "seconds": round(elapsed, 4),
                "file_bytes": store.main_file_bytes(path),
            }
        )
        if after >= before:
            break
    total = time.monotonic() - total_started
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    freelist = conn.execute("PRAGMA freelist_count").fetchone()[0]
    stats = btree_bytes(conn)
    conn.close()
    return {
        "stages": len(stages),
        "pages_per_stage": pages_per_stage,
        "total_seconds": round(total, 3),
        "freelist_remaining": freelist,
        "max_stage_seconds": round(max((s["seconds"] for s in stages), default=0), 4),
        "mean_stage_seconds": round(
            sum(s["seconds"] for s in stages) / len(stages), 4
        )
        if stages
        else None,
        "file_bytes": store.main_file_bytes(path),
        "btree_bytes": stats,
        "stage_trace": stages[:8],
    }


def incremental_vacuum_on_none(path: str) -> dict:
    conn = store.connect(path)
    before_file = store.main_file_bytes(path)
    before_free = conn.execute("PRAGMA freelist_count").fetchone()[0]
    error = None
    started = time.monotonic()
    try:
        incremental_vacuum(conn, 10_000_000)
    except sqlite3.Error as exc:
        error = f"{type(exc).__name__}: {exc}"
    elapsed = time.monotonic() - started
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    after_free = conn.execute("PRAGMA freelist_count").fetchone()[0]
    conn.close()
    return {
        "file_bytes_before": before_file,
        "file_bytes_after": store.main_file_bytes(path),
        "freelist_before": before_free,
        "freelist_after": after_free,
        "seconds": round(elapsed, 4),
        "raised": error,
    }


def full_vacuum(path: str) -> dict:
    conn = store.connect(path)
    conn.execute("PRAGMA temp_store=FILE")
    before = store.main_file_bytes(path)
    free_before = os.statvfs(os.path.dirname(path) or ".")
    started = time.monotonic()
    conn.execute("VACUUM")
    elapsed = time.monotonic() - started
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    conn.close()
    return {
        "file_bytes_before": before,
        "file_bytes_after": store.main_file_bytes(path),
        "seconds": round(elapsed, 2),
        "free_disk_bytes_at_start": free_before.f_bavail * free_before.f_frsize,
    }


def run_store_arm(
    workdir: str, label: str, auto_vacuum: str, pattern: str, versions: int, generations: int
) -> dict:
    path = os.path.join(workdir, f"gc_{label}.db")
    arm = {"label": label, "auto_vacuum": auto_vacuum, "delete_pattern": pattern}
    arm["build"] = build_store(path, auto_vacuum, versions, generations)
    conn = store.connect(path)
    arm["pragmas_on_reopen"] = {
        "auto_vacuum": conn.execute("PRAGMA auto_vacuum").fetchone()[0],
        "page_size": conn.execute("PRAGMA page_size").fetchone()[0],
    }
    conn.close()
    plain = sqlite3.connect(path)
    arm["secure_delete_on_plain_reopen"] = plain.execute(
        "PRAGMA secure_delete"
    ).fetchone()[0]
    plain.close()

    victims = victim_versions(pattern, versions, generations)
    log(f"{label}: deleting {len(victims)} versions ({pattern})")
    arm["delete"] = delete_versions(path, victims)

    if auto_vacuum == "INCREMENTAL":
        log(f"{label}: staged incremental_vacuum")
        arm["incremental_vacuum"] = staged_incremental_vacuum(
            path, VACUUM_PAGES_PER_STAGE
        )
    else:
        log(f"{label}: incremental_vacuum attempt on a NONE database")
        arm["incremental_vacuum_attempt"] = incremental_vacuum_on_none(path)

    log(f"{label}: full VACUUM for comparison")
    arm["full_vacuum"] = full_vacuum(path)

    for suffix in ("", "-wal", "-shm"):
        if os.path.exists(path + suffix):
            os.remove(path + suffix)
    return arm


def build_sidecar(path: str, versions: int, generations: int) -> dict:
    conn = store.create(path, store.SIDECAR_DDL, auto_vacuum="INCREMENTAL")
    config_before = dict(conn.execute("SELECT k, v FROM symbols_fts_config").fetchall())
    store.enable_fts_secure_delete(conn, ("symbols_fts", "symbols_trigram"))
    conn.execute("INSERT INTO symbols_fts(symbols_fts, rank) VALUES ('automerge', 0)")
    conn.execute(
        "INSERT INTO symbols_trigram(symbols_trigram, rank) VALUES ('automerge', 0)"
    )
    config_after = dict(conn.execute("SELECT k, v FROM symbols_fts_config").fetchall())
    doc_id = 0
    started = time.monotonic()
    for version_id in range(1, versions + 1):
        file_index = (version_id - 1) // generations
        generation = (version_id - 1) % generations
        path_ = shapes.version_path(file_index)
        factory = shapes.VersionRowFactory(path_, generation, "fp-v4-0001")
        conn.execute("BEGIN")
        for _vid, symbol_id, body, name_collapsed in factory.fts_documents(version_id):
            doc_id += 1
            conn.execute(
                "INSERT INTO symbols_fts(rowid, symbol_id, body) VALUES (?,?,?)",
                (doc_id, symbol_id, body),
            )
            conn.execute(
                "INSERT INTO symbols_trigram(rowid, symbol_id, name_collapsed) VALUES (?,?,?)",
                (doc_id, symbol_id, name_collapsed),
            )
            conn.execute(
                "INSERT INTO search_symbols VALUES (?,?,?)",
                (version_id, symbol_id, doc_id),
            )
        conn.execute("COMMIT")
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    elapsed = time.monotonic() - started
    segments = fts_segments(conn)
    conn.close()
    log(
        f"built sidecar versions={versions} docs={doc_id} in {elapsed:.1f}s "
        f"size={store.main_file_bytes(path) / 1e6:.1f} MB segments={segments}"
    )
    return {
        "versions": versions,
        "docs": doc_id,
        "build_seconds": round(elapsed, 2),
        "file_bytes": store.main_file_bytes(path),
        "fts_config_before": config_before,
        "fts_config_after": config_after,
        "segments": segments,
    }


def fts_segments(conn: sqlite3.Connection) -> dict:
    out = {}
    for table in ("symbols_fts", "symbols_trigram"):
        out[table] = {
            "segids": conn.execute(
                f"SELECT COUNT(DISTINCT segid) FROM {table}_idx"
            ).fetchone()[0],
            "data_rows": conn.execute(
                f"SELECT COUNT(*) FROM {table}_data"
            ).fetchone()[0],
        }
    return out


def sidecar_gc(path: str, versions: int, generations: int, pattern: str) -> dict:
    result = {"file_bytes_start": store.main_file_bytes(path)}
    conn = store.connect(path)
    result["segments_start"] = fts_segments(conn)

    victims = victim_versions(pattern, versions, generations)
    placeholders = ",".join("?" * 900)
    doc_ids: list[int] = []
    for i in range(0, len(victims), 900):
        batch = victims[i : i + 900]
        marks = ",".join("?" * len(batch))
        doc_ids.extend(
            row[0]
            for row in conn.execute(
                f"SELECT doc_id FROM search_symbols WHERE version_id IN ({marks})",
                batch,
            )
        )
    del placeholders

    started = time.monotonic()
    conn.execute("BEGIN")
    conn.executemany(
        "DELETE FROM symbols_fts WHERE rowid = ?", ((d,) for d in doc_ids)
    )
    conn.executemany(
        "DELETE FROM symbols_trigram WHERE rowid = ?", ((d,) for d in doc_ids)
    )
    conn.executemany(
        "DELETE FROM search_symbols WHERE doc_id = ?", ((d,) for d in doc_ids)
    )
    conn.execute("COMMIT")
    result["deleted_docs"] = len(doc_ids)
    result["delete_seconds"] = round(time.monotonic() - started, 2)
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    result["file_bytes_after_delete"] = store.main_file_bytes(path)
    result["segments_after_delete"] = fts_segments(conn)
    result["freelist_after_delete"] = conn.execute(
        "PRAGMA freelist_count"
    ).fetchone()[0]
    conn.close()

    optimize_clone = path + ".optimize-clone"
    shutil.copyfile(path, optimize_clone)

    conn = store.connect(path)
    merges = []
    for table in ("symbols_fts", "symbols_trigram"):
        rounds = 0
        while rounds < 2000:
            before_changes = conn.total_changes
            round_started = time.monotonic()
            conn.execute(
                f"INSERT INTO {table}({table}, rank) VALUES ('merge', ?)", (MERGE_PAGES,)
            )
            round_elapsed = time.monotonic() - round_started
            delta = conn.total_changes - before_changes
            rounds += 1
            merges.append(
                {
                    "table": table,
                    "round": rounds,
                    "total_changes_delta": delta,
                    "seconds": round(round_elapsed, 4),
                }
            )
            if delta < 2:
                break
    result["merge_pages_per_round"] = MERGE_PAGES
    result["merge_rounds_total"] = len(merges)
    result["merge_rounds_with_work"] = sum(
        1 for m in merges if m["total_changes_delta"] >= 2
    )
    result["merge_seconds_total"] = round(sum(m["seconds"] for m in merges), 3)
    result["merge_max_round_seconds"] = round(
        max((m["seconds"] for m in merges), default=0), 4
    )
    result["merge_mean_round_seconds"] = (
        round(sum(m["seconds"] for m in merges) / len(merges), 4) if merges else None
    )
    result["merge_round_trace"] = merges[:6] + merges[-4:]
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    result["file_bytes_after_merge"] = store.main_file_bytes(path)
    result["segments_after_merge"] = fts_segments(conn)
    result["freelist_after_merge"] = conn.execute(
        "PRAGMA freelist_count"
    ).fetchone()[0]
    conn.close()

    result["incremental_vacuum"] = staged_incremental_vacuum(
        path, VACUUM_PAGES_PER_STAGE
    )
    result["file_bytes_after_vacuum"] = store.main_file_bytes(path)

    conn = store.connect(optimize_clone)
    started = time.monotonic()
    for table in ("symbols_fts", "symbols_trigram"):
        conn.execute(f"INSERT INTO {table}({table}) VALUES ('optimize')")
    optimize_seconds = time.monotonic() - started
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    optimize_segments = fts_segments(conn)
    conn.close()
    optimize_vacuum = staged_incremental_vacuum(optimize_clone, VACUUM_PAGES_PER_STAGE)
    result["optimize_control"] = {
        "single_call_seconds": round(optimize_seconds, 3),
        "segments_after": optimize_segments,
        "file_bytes_after_optimize_and_vacuum": store.main_file_bytes(optimize_clone),
        "incremental_vacuum_seconds": optimize_vacuum["total_seconds"],
    }
    for suffix in ("", "-wal", "-shm"):
        if os.path.exists(optimize_clone + suffix):
            os.remove(optimize_clone + suffix)
    for suffix in ("", "-wal", "-shm"):
        if os.path.exists(path + suffix):
            os.remove(path + suffix)
    return result


def scan_for_sentinel(path: str) -> dict:
    needle = SENTINEL.encode()
    hits = {}
    for suffix in ("", "-wal"):
        candidate = path + suffix
        if not os.path.exists(candidate):
            hits[suffix.lstrip("-") or "main"] = 0
            continue
        count = 0
        with open(candidate, "rb") as handle:
            tail = b""
            while True:
                block = handle.read(1 << 20)
                if not block:
                    break
                buffer = tail + block
                count += buffer.count(needle)
                tail = buffer[-len(needle) :]
        hits[suffix.lstrip("-") or "main"] = count
    return hits


def secure_delete_probe(workdir: str, fts_secure_delete: bool, core_secure_delete: bool) -> dict:
    label = f"fts{int(fts_secure_delete)}_core{int(core_secure_delete)}"
    path = os.path.join(workdir, f"probe_{label}.db")
    conn = store.create(
        path,
        store.SIDECAR_DDL,
        auto_vacuum="INCREMENTAL",
        secure_delete=core_secure_delete,
    )
    if fts_secure_delete:
        store.enable_fts_secure_delete(conn, ("symbols_fts", "symbols_trigram"))
    conn.execute("BEGIN")
    for doc_id in range(1, 8001):
        body = " ".join(
            shapes.WORDS[(doc_id * 7 + i) % len(shapes.WORDS)] for i in range(14)
        )
        if doc_id == 4000:
            body += " " + SENTINEL
        conn.execute(
            "INSERT INTO symbols_fts(rowid, symbol_id, body) VALUES (?,?,?)",
            (doc_id, f"sym{doc_id}", body),
        )
        if doc_id % 500 == 0:
            conn.execute("COMMIT")
            conn.execute("BEGIN")
    conn.execute("COMMIT")
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    before = scan_for_sentinel(path)

    conn.execute("DELETE FROM symbols_fts WHERE rowid = 4000")
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    after_delete = scan_for_sentinel(path)

    rounds = 0
    while rounds < 2000:
        before_changes = conn.total_changes
        conn.execute("INSERT INTO symbols_fts(symbols_fts, rank) VALUES ('merge', 64)")
        rounds += 1
        if conn.total_changes - before_changes < 2:
            break
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    after_merge = scan_for_sentinel(path)
    conn.close()

    staged_incremental_vacuum(path, VACUUM_PAGES_PER_STAGE)
    after_vacuum = scan_for_sentinel(path)

    reopened = store.connect(path)
    config = dict(reopened.execute("SELECT k, v FROM symbols_fts_config").fetchall())
    reopened.close()
    cli_readable = os.system(
        f'sqlite3 "{path}" "SELECT COUNT(*) FROM symbols_fts;" > /dev/null 2>&1'
    )

    for suffix in ("", "-wal", "-shm"):
        if os.path.exists(path + suffix):
            os.remove(path + suffix)

    return {
        "label": label,
        "fts_secure_delete": fts_secure_delete,
        "core_secure_delete": core_secure_delete,
        "sentinel_hits_before_delete": before,
        "sentinel_hits_after_delete": after_delete,
        "sentinel_hits_after_merge": after_merge,
        "sentinel_hits_after_vacuum": after_vacuum,
        "merge_rounds": rounds,
        "fts_config_on_reopen": config,
        "system_sqlite3_cli_exit": cli_readable,
    }


def main() -> int:
    workdir, outdir = sys.argv[1], sys.argv[2]
    versions = int(sys.argv[3])
    generations = int(sys.argv[4])
    os.makedirs(workdir, exist_ok=True)
    os.makedirs(outdir, exist_ok=True)

    result = {
        "sqlite_version": sqlite3.sqlite_version,
        "python_version": sys.version.split()[0],
        "file_versions": versions,
        "generations_per_path": generations,
        "distinct_paths": versions // generations,
        "rows_per_file_version": shapes.ROWS_PER_FILE_VERSION,
        "vacuum_pages_per_stage": VACUUM_PAGES_PER_STAGE,
    }

    result["arms"] = [
        run_store_arm(
            workdir, "inc_retention", "INCREMENTAL", "retention_scatter", versions, generations
        ),
        run_store_arm(
            workdir, "inc_epoch", "INCREMENTAL", "epoch_contiguous", versions, generations
        ),
        run_store_arm(
            workdir, "none_retention", "NONE", "retention_scatter", versions, generations
        ),
    ]

    sidecar_versions = max(generations, versions // 3)
    sidecar_path = os.path.join(workdir, "gc_sidecar.db")
    result["sidecar"] = {
        "build": build_sidecar(sidecar_path, sidecar_versions, generations),
    }
    log("sidecar GC: delete + page-limited merge + incremental_vacuum")
    result["sidecar"]["gc"] = sidecar_gc(
        sidecar_path, sidecar_versions, generations, "retention_scatter"
    )

    log("secure-delete probes")
    result["secure_delete_probes"] = [
        secure_delete_probe(workdir, False, False),
        secure_delete_probe(workdir, True, False),
        secure_delete_probe(workdir, False, True),
        secure_delete_probe(workdir, True, True),
    ]

    out = os.path.join(outdir, "gc.json")
    with open(out, "w") as handle:
        json.dump(result, handle, indent=2, sort_keys=True)
    log(f"wrote {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
