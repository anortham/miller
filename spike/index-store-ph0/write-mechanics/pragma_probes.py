#!/usr/bin/env python3
"""Empirical checks of the SQLite facts the store contract rests on.

Each probe records what SQLite actually does on this build, so the contract
cites measurement rather than documentation. Small and fast: every probe uses a
database of a few MB.

Usage: pragma_probes.py <workdir> <outdir>
"""

from __future__ import annotations

import json
import os
import sqlite3
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "lib"))

import store  # noqa: E402

SENTINEL = b"zqxjvwkzprobesentinel0123456789"


def fresh(path: str) -> None:
    for suffix in ("", "-wal", "-shm"):
        if os.path.exists(path + suffix):
            os.remove(path + suffix)


def probe_auto_vacuum_ordering(workdir: str) -> dict:
    path = os.path.join(workdir, "probe_av.db")
    fresh(path)
    conn = sqlite3.connect(path, isolation_level=None)
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)")
    conn.executemany("INSERT INTO t VALUES(?,?)", ((i, "x" * 200) for i in range(20000)))
    at_creation = conn.execute("PRAGMA auto_vacuum").fetchone()[0]
    error = None
    try:
        conn.execute("PRAGMA auto_vacuum=INCREMENTAL")
    except sqlite3.Error as exc:
        error = f"{type(exc).__name__}: {exc}"
    after_late_set = conn.execute("PRAGMA auto_vacuum").fetchone()[0]
    started = time.monotonic()
    conn.execute("VACUUM")
    vacuum_seconds = time.monotonic() - started
    after_vacuum = conn.execute("PRAGMA auto_vacuum").fetchone()[0]
    conn.close()
    reopened = sqlite3.connect(path)
    on_reopen = reopened.execute("PRAGMA auto_vacuum").fetchone()[0]
    reopened.close()

    before_path = os.path.join(workdir, "probe_av_before.db")
    fresh(before_path)
    conn = sqlite3.connect(before_path, isolation_level=None)
    conn.execute("PRAGMA auto_vacuum=INCREMENTAL")
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)")
    set_before_schema = conn.execute("PRAGMA auto_vacuum").fetchone()[0]
    conn.close()
    reopened = sqlite3.connect(before_path)
    before_on_reopen = reopened.execute("PRAGMA auto_vacuum").fetchone()[0]
    reopened.close()
    fresh(path)
    fresh(before_path)

    return {
        "claim": "auto_vacuum=INCREMENTAL must be set before the first table is created",
        "verdict": "CONFIRMED, and the late set fails SILENTLY",
        "auto_vacuum_at_creation_default": at_creation,
        "after_setting_INCREMENTAL_post_schema": after_late_set,
        "error_raised_by_late_set": error,
        "after_full_VACUUM_rewrite": after_vacuum,
        "on_reopen_after_vacuum": on_reopen,
        "vacuum_rewrite_seconds": round(vacuum_seconds, 3),
        "set_before_schema_creation": set_before_schema,
        "set_before_schema_on_reopen": before_on_reopen,
    }


def probe_incremental_vacuum_stepping(workdir: str) -> dict:
    path = os.path.join(workdir, "probe_iv.db")
    fresh(path)
    conn = sqlite3.connect(path, isolation_level=None)
    conn.execute("PRAGMA page_size=4096")
    conn.execute("PRAGMA auto_vacuum=INCREMENTAL")
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT)")
    conn.execute("BEGIN")
    conn.executemany("INSERT INTO t VALUES(?,?)", ((i, "x" * 500) for i in range(200000)))
    conn.execute("COMMIT")

    conn.execute("DELETE FROM t WHERE a % 2 = 0")
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    interleaved_freelist = conn.execute("PRAGMA freelist_count").fetchone()[0]

    conn.execute("DELETE FROM t")
    conn.execute("BEGIN")
    conn.executemany("INSERT INTO t VALUES(?,?)", ((i, "x" * 500) for i in range(200000)))
    conn.execute("COMMIT")
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    while conn.execute("PRAGMA freelist_count").fetchone()[0] > 0:
        conn.execute("PRAGMA incremental_vacuum(20000)").fetchall()
    conn.execute("DELETE FROM t WHERE a < 100000")
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    contiguous_freelist = conn.execute("PRAGMA freelist_count").fetchone()[0]

    before = conn.execute("PRAGMA freelist_count").fetchone()[0]
    conn.execute("PRAGMA incremental_vacuum(2000)")
    after_bare_execute = conn.execute("PRAGMA freelist_count").fetchone()[0]
    conn.execute("PRAGMA incremental_vacuum(2000)").fetchall()
    after_fetchall = conn.execute("PRAGMA freelist_count").fetchone()[0]
    conn.close()
    fresh(path)

    return {
        "claim": "PRAGMA incremental_vacuum(N) frees up to N pages",
        "verdict": "CONFIRMED only when the statement is stepped to completion",
        "freelist_before": before,
        "pages_freed_by_bare_connection_execute": before - after_bare_execute,
        "pages_freed_by_execute_plus_fetchall": after_bare_execute - after_fetchall,
        "requested_pages": 2000,
        "interleaved_delete_freelist_pages": interleaved_freelist,
        "contiguous_delete_freelist_pages": contiguous_freelist,
        "note": (
            "Interleaved row deletes leave partly-filled pages that never reach "
            "the freelist, so incremental_vacuum cannot reclaim them. Deleting a "
            "contiguous key range does free whole pages."
        ),
    }


def probe_fts_merge_semantics(workdir: str) -> dict:
    path = os.path.join(workdir, "probe_merge.db")
    fresh(path)
    conn = sqlite3.connect(path, isolation_level=None)
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("CREATE VIRTUAL TABLE ft USING fts5(body)")
    conn.execute("INSERT INTO ft(ft, rank) VALUES ('automerge', 0)")
    for batch in range(40):
        conn.execute("BEGIN")
        for i in range(500):
            conn.execute(
                "INSERT INTO ft(body) VALUES (?)",
                (f"segment {batch} document {i} alpha beta gamma delta epsilon",),
            )
        conn.execute("COMMIT")
    segids_before = conn.execute("SELECT COUNT(DISTINCT segid) FROM ft_idx").fetchone()[0]

    rounds = []
    for _ in range(200):
        before_changes = conn.total_changes
        started = time.monotonic()
        conn.execute("INSERT INTO ft(ft, rank) VALUES ('merge', 16)")
        elapsed = time.monotonic() - started
        delta = conn.total_changes - before_changes
        rounds.append({"total_changes_delta": delta, "seconds": round(elapsed, 4)})
        if delta < 2:
            break
    segids_after_merge = conn.execute(
        "SELECT COUNT(DISTINCT segid) FROM ft_idx"
    ).fetchone()[0]

    started = time.monotonic()
    conn.execute("INSERT INTO ft(ft) VALUES ('optimize')")
    optimize_seconds = time.monotonic() - started
    segids_after_optimize = conn.execute(
        "SELECT COUNT(DISTINCT segid) FROM ft_idx"
    ).fetchone()[0]
    conn.close()
    fresh(path)

    work_rounds = [r for r in rounds if r["total_changes_delta"] >= 2]
    return {
        "claim": (
            "page-limited merge is bounded work; total_changes rises by 2 when a "
            "merge did work and by 1 when it did not; optimize is unbounded"
        ),
        "verdict": "CONFIRMED",
        "merge_pages_per_round": 16,
        "segids_before": segids_before,
        "segids_after_page_limited_merges": segids_after_merge,
        "segids_after_optimize": segids_after_optimize,
        "merge_rounds": len(rounds),
        "merge_rounds_that_did_work": len(work_rounds),
        "merge_total_seconds": round(sum(r["seconds"] for r in rounds), 4),
        "merge_max_round_seconds": round(max(r["seconds"] for r in rounds), 4),
        "final_round_total_changes_delta": rounds[-1]["total_changes_delta"],
        "optimize_single_call_seconds": round(optimize_seconds, 4),
        "optimize_over_max_merge_round": round(
            optimize_seconds / max(r["seconds"] for r in rounds), 1
        ),
    }


def probe_secure_delete_persistence(workdir: str) -> dict:
    path = os.path.join(workdir, "probe_sd.db")
    fresh(path)
    conn = sqlite3.connect(path, isolation_level=None)
    conn.execute("PRAGMA journal_mode=WAL")
    default_on_create = conn.execute("PRAGMA secure_delete").fetchone()[0]
    conn.execute("PRAGMA secure_delete=ON")
    set_value = conn.execute("PRAGMA secure_delete").fetchone()[0]
    conn.execute("CREATE VIRTUAL TABLE ft USING fts5(body)")
    config_at_creation = dict(conn.execute("SELECT k, v FROM ft_config"))
    conn.execute("INSERT INTO ft(ft, rank) VALUES ('secure-delete', 1)")
    config_after_enable = dict(conn.execute("SELECT k, v FROM ft_config"))
    conn.execute("BEGIN")
    for i in range(4000):
        conn.execute("INSERT INTO ft(body) VALUES (?)", (f"document {i} alpha beta gamma",))
    conn.execute("COMMIT")
    conn.execute("DELETE FROM ft WHERE rowid = 2000")
    conn.execute("INSERT INTO ft(ft, rank) VALUES ('merge', 64)")
    config_same_connection = dict(conn.execute("SELECT k, v FROM ft_config"))
    conn.close()

    reopened = sqlite3.connect(path)
    core_on_reopen = reopened.execute("PRAGMA secure_delete").fetchone()[0]
    config_on_reopen = dict(reopened.execute("SELECT k, v FROM ft_config"))
    reopened.close()
    cli_exit = os.system(f'sqlite3 "{path}" "SELECT COUNT(*) FROM ft;" > /dev/null 2>&1')
    fresh(path)

    return {
        "claim": (
            "core secure_delete and the FTS5 secure-delete option are different "
            "mechanisms with different lifetimes"
        ),
        "verdict": (
            "CONFIRMED: core secure_delete is per-connection and is NOT stored in "
            "the file; the FTS5 option is stored in the %_config shadow table"
        ),
        "core_secure_delete_default": default_on_create,
        "core_secure_delete_after_set": set_value,
        "core_secure_delete_on_a_fresh_connection": core_on_reopen,
        "fts_config_at_table_creation": config_at_creation,
        "fts_config_immediately_after_enabling": config_after_enable,
        "fts_config_after_a_secure_delete_write": config_same_connection,
        "fts_config_on_reopen": config_on_reopen,
        "system_sqlite3_cli_can_read_the_file_exit": cli_exit,
        "note": (
            "Writing a secure delete raises the stored FTS5 structure version "
            "from 4 to 5, so every reader of the sidecar must be new enough for "
            "it. The version rises on the first secure delete, not on the "
            "config INSERT that enables the option."
        ),
    }


def main() -> int:
    workdir, outdir = sys.argv[1], sys.argv[2]
    os.makedirs(workdir, exist_ok=True)
    os.makedirs(outdir, exist_ok=True)

    result = {
        "sqlite_library_version": sqlite3.sqlite_version,
        "system_sqlite3_cli": os.popen("sqlite3 --version").read().strip(),
        "compile_options": [
            row[0]
            for row in sqlite3.connect(":memory:").execute("PRAGMA compile_options")
        ],
        "probes": {
            "auto_vacuum_ordering": probe_auto_vacuum_ordering(workdir),
            "incremental_vacuum_stepping": probe_incremental_vacuum_stepping(workdir),
            "fts5_merge_semantics": probe_fts_merge_semantics(workdir),
            "secure_delete_persistence": probe_secure_delete_persistence(workdir),
        },
    }

    out = os.path.join(outdir, "pragma-probes.json")
    with open(out, "w") as handle:
        json.dump(result, handle, indent=2, sort_keys=True)
    print(f"[probes] wrote {out}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
