#!/usr/bin/env python3
"""Killable store importer, one commit-granularity mode per run.

Modes:

``single``
    One transaction for the whole import -- julie's current snapshot-writer
    shape. Nothing is durable until the final COMMIT.
``per_chunk``
    COMMIT every ``--chunk`` file versions.
``per_version``
    COMMIT per file version. The version row, all its child rows, and the
    ``complete`` marker land in the SAME transaction.
``per_version_nomarker``
    Negative control for doubt-pass finding 7: the version row commits first,
    its child rows commit second. Dedup that trusts the version row alone will
    "find" a version whose children never arrived.

``--verify`` reports what a following run could reuse instead of importing.
"""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "lib"))

import shapes  # noqa: E402
import store  # noqa: E402

EXTRACTOR_FP = "fp-v4-0001"
MODES = ("single", "per_chunk", "per_version", "per_version_nomarker")


def open_or_create(path: str, autocheckpoint: int, synchronous: str) -> sqlite3.Connection:
    if os.path.exists(path):
        conn = store.connect(path)
    else:
        conn = store.create(path, store.STORE_DDL, auto_vacuum="INCREMENTAL")
    conn.execute(f"PRAGMA wal_autocheckpoint={autocheckpoint}")
    conn.execute(f"PRAGMA synchronous={synchronous}")
    return conn


def complete_versions(conn: sqlite3.Connection, trust_marker: bool) -> dict:
    """Dedup index: (path, content_hash) of versions this importer may skip."""
    predicate = "WHERE complete = 1" if trust_marker else ""
    return {
        (path, content_hash): version_id
        for version_id, path, content_hash in conn.execute(
            f"SELECT version_id, path, content_hash FROM file_versions {predicate}"
        )
    }


def insert_version(conn, version_id, factory, complete):
    conn.execute(
        "INSERT INTO file_versions VALUES (?,?,?,?,?,?)",
        (
            version_id,
            factory.path,
            factory.content_hash,
            EXTRACTOR_FP,
            factory.generation,
            complete,
        ),
    )


def insert_children(conn, version_id, factory) -> int:
    conn.executemany(store.STORE_INSERTS["symbols"], factory.symbols(version_id))
    conn.executemany(
        store.STORE_INSERTS["reference_sites"], factory.reference_sites(version_id)
    )
    conn.executemany(
        store.STORE_INSERTS["identifiers"], factory.identifiers(version_id)
    )
    return shapes.ROWS_PER_FILE_VERSION


def run_import(args) -> dict:
    conn = open_or_create(args.db, args.autocheckpoint, args.synchronous)
    trust_marker = args.mode != "per_version_nomarker"
    known = complete_versions(conn, trust_marker)
    next_id = (
        conn.execute("SELECT COALESCE(MAX(version_id), 0) FROM file_versions").fetchone()[0]
        + 1
    )

    targets = []
    for file_index in range(args.versions):
        path = shapes.version_path(file_index)
        factory = shapes.VersionRowFactory(path, args.generation, EXTRACTOR_FP)
        targets.append(factory)

    skipped = sum(1 for f in targets if (f.path, f.content_hash) in known)
    todo = [f for f in targets if (f.path, f.content_hash) not in known]

    print(
        f"importer mode={args.mode} targets={len(targets)} skipped={skipped} "
        f"todo={len(todo)}",
        flush=True,
    )

    rows = 0
    started = time.monotonic()
    commits = 0

    if args.mode == "single":
        conn.execute("BEGIN")
        for factory in todo:
            insert_version(conn, next_id, factory, 1)
            rows += insert_children(conn, next_id, factory)
            next_id += 1
        conn.execute("COMMIT")
        commits = 1
    elif args.mode == "per_chunk":
        conn.execute("BEGIN")
        for i, factory in enumerate(todo, 1):
            insert_version(conn, next_id, factory, 1)
            rows += insert_children(conn, next_id, factory)
            next_id += 1
            if i % args.chunk == 0:
                conn.execute("COMMIT")
                commits += 1
                conn.execute("BEGIN")
        conn.execute("COMMIT")
        commits += 1
    elif args.mode == "per_version":
        for factory in todo:
            conn.execute("BEGIN")
            insert_version(conn, next_id, factory, 1)
            rows += insert_children(conn, next_id, factory)
            conn.execute("COMMIT")
            commits += 1
            next_id += 1
    elif args.mode == "per_version_nomarker":
        for factory in todo:
            conn.execute("BEGIN")
            insert_version(conn, next_id, factory, 1)
            conn.execute("COMMIT")
            commits += 1
            conn.execute("BEGIN")
            rows += insert_children(conn, next_id, factory)
            conn.execute("COMMIT")
            commits += 1
            next_id += 1
    else:
        raise ValueError(args.mode)

    elapsed = time.monotonic() - started
    wal_bytes = (
        os.path.getsize(args.db + "-wal") if os.path.exists(args.db + "-wal") else 0
    )
    conn.close()
    return {
        "mode": args.mode,
        "autocheckpoint_pages": args.autocheckpoint,
        "synchronous": args.synchronous,
        "targets": len(targets),
        "skipped_by_dedup": skipped,
        "imported": len(todo),
        "rows": rows,
        "commits": commits,
        "seconds": round(elapsed, 3),
        "rows_per_second": round(rows / elapsed, 1) if elapsed > 0 else None,
        "versions_per_second": round(len(todo) / elapsed, 2) if elapsed > 0 else None,
        "wal_bytes_at_exit": wal_bytes,
        "db_bytes": store.main_file_bytes(args.db),
    }


def run_verify(args) -> dict:
    """Report what a following run could reuse, and whether the marker held."""
    conn = store.connect(args.db)
    expected = {
        "symbols": shapes.SYMBOLS_PER_FILE,
        "reference_sites": shapes.REFERENCE_SITES_PER_FILE,
        "identifiers": shapes.IDENTIFIERS_PER_FILE,
    }
    totals = {
        "file_versions": conn.execute("SELECT COUNT(*) FROM file_versions").fetchone()[0],
        "marked_complete": conn.execute(
            "SELECT COUNT(*) FROM file_versions WHERE complete = 1"
        ).fetchone()[0],
        "marked_incomplete": conn.execute(
            "SELECT COUNT(*) FROM file_versions WHERE complete = 0"
        ).fetchone()[0],
    }

    counts = {}
    for table, per_version in expected.items():
        counts[table] = dict(
            conn.execute(f"SELECT version_id, COUNT(*) FROM {table} GROUP BY version_id")
        )

    intact = 0
    truncated = 0
    truncated_examples = []
    for (version_id,) in conn.execute(
        "SELECT version_id FROM file_versions WHERE complete = 1"
    ):
        actual = {t: counts[t].get(version_id, 0) for t in expected}
        if actual == expected:
            intact += 1
        else:
            truncated += 1
            if len(truncated_examples) < 5:
                truncated_examples.append({"version_id": version_id, "counts": actual})

    orphan_rows = 0
    for table in expected:
        orphan_rows += conn.execute(
            f"SELECT COUNT(*) FROM {table} WHERE version_id NOT IN "
            "(SELECT version_id FROM file_versions)"
        ).fetchone()[0]

    integrity = conn.execute("PRAGMA quick_check").fetchone()[0]
    conn.close()
    return {
        **totals,
        "reusable_complete_versions": intact,
        "visible_but_truncated_versions": truncated,
        "truncated_examples": truncated_examples,
        "orphan_child_rows": orphan_rows,
        "quick_check": integrity,
        "db_bytes": store.main_file_bytes(args.db),
        "wal_bytes": os.path.getsize(args.db + "-wal")
        if os.path.exists(args.db + "-wal")
        else 0,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--db", required=True)
    parser.add_argument("--mode", choices=MODES, default="per_version")
    parser.add_argument("--versions", type=int, default=1000)
    parser.add_argument("--generation", type=int, default=0)
    parser.add_argument("--chunk", type=int, default=100)
    parser.add_argument("--autocheckpoint", type=int, default=1000)
    parser.add_argument("--synchronous", default="NORMAL")
    parser.add_argument("--report")
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()

    result = run_verify(args) if args.verify else run_import(args)
    payload = json.dumps(result, indent=2, sort_keys=True)
    if args.report:
        with open(args.report, "w") as handle:
            handle.write(payload)
    print(payload, flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
