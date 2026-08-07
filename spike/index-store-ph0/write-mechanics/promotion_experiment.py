#!/usr/bin/env python3
"""Promotion-capacity instrument.

Rebuilds a store generation alongside the live one and records the ACTUAL peak
bytes on disk against the plan's formula:

    peak = old generation + new generation + sidecars + WAL/temp
           + generations retained for pinned readers

Three arms, so every term is isolated by at least one comparison:

``no_reader``       one live generation, promoted. No retained term.
``pinned_reader``   an OLDER generation is still on disk because a reader holds
                    an open snapshot on it, plus the live generation, plus the
                    rebuild. Exercises the retained term and shows that a pinned
                    reader also blocks the live generation's WAL from resetting.
``retention_first`` the retention sweep (delete + staged incremental_vacuum)
                    runs BEFORE the rebuild, and the rebuild carries only the
                    survivors -- the plan's "retention cleanup runs before
                    capacity is judged".

Usage: promotion_experiment.py <workdir> <outdir> <file_versions> <generations_per_path>
"""

from __future__ import annotations

import json
import os
import shutil
import sqlite3
import sys
import threading
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "lib"))

import shapes  # noqa: E402
import store  # noqa: E402

CHUNK_VERSIONS = 40
EXTRACTOR_FP = "fp-v4-0001"
RETAINED_GENERATIONS_DROPPED = 2


def log(msg: str) -> None:
    print(f"[promotion] {msg}", flush=True)


class DirSampler(threading.Thread):
    """Peak bytes in the family directory, peak WAL bytes, minimum free disk."""

    def __init__(self, directory: str, interval: float = 0.05):
        super().__init__(daemon=True)
        self.directory = directory
        self.interval = interval
        self.peak_bytes = 0
        self.peak_wal_bytes = 0
        self.min_free_bytes = None
        self.samples = 0
        self.timeline: list[list] = []
        self.peak_breakdown: dict = {}
        self.peak_at_seconds = None
        self._stop = threading.Event()
        self._t0 = time.monotonic()

    def _walk(self) -> tuple[int, int, dict]:
        """Sum the family directory, keyed by inode.

        A promotion renames generation files between directories, and os.walk
        can list the same inode under both its old and its new path when a
        rename lands mid-walk. Keying on (st_dev, st_ino) makes the peak the
        real peak instead of a double count.
        """
        total = 0
        wal = 0
        files = {}
        seen = set()
        for root, _dirs, names in os.walk(self.directory):
            for name in names:
                full = os.path.join(root, name)
                try:
                    info = os.stat(full)
                except OSError:
                    continue
                key = (info.st_dev, info.st_ino)
                if key in seen:
                    continue
                seen.add(key)
                total += info.st_size
                files[os.path.relpath(full, self.directory)] = info.st_size
                if name.endswith("-wal"):
                    wal += info.st_size
        return total, wal, files

    def run(self) -> None:
        while not self._stop.is_set():
            try:
                total, wal, files = self._walk()
                statvfs = os.statvfs(self.directory)
                free = statvfs.f_bavail * statvfs.f_frsize
            except OSError:
                self._stop.wait(self.interval)
                continue
            if total > self.peak_bytes:
                self.peak_bytes = total
                self.peak_breakdown = dict(files)
                self.peak_at_seconds = round(time.monotonic() - self._t0, 2)
            self.peak_wal_bytes = max(self.peak_wal_bytes, wal)
            self.min_free_bytes = (
                free if self.min_free_bytes is None else min(self.min_free_bytes, free)
            )
            self.samples += 1
            if self.samples % 20 == 0:
                self.timeline.append(
                    [round(time.monotonic() - self._t0, 2), total, wal]
                )
            self._stop.wait(self.interval)

    def stop(self) -> None:
        self._stop.set()
        self.join(timeout=2.0)


def write_generation(
    store_path: str,
    sidecar_path: str,
    versions: int,
    generations: int,
    skip_oldest: int = 0,
) -> dict:
    started = time.monotonic()
    conn = store.create(store_path, store.STORE_DDL, auto_vacuum="INCREMENTAL")
    side = store.create(sidecar_path, store.SIDECAR_DDL, auto_vacuum="INCREMENTAL")
    store.enable_fts_secure_delete(side, ("symbols_fts", "symbols_trigram"))
    doc_id = 0
    rows = 0
    written = 0
    conn.execute("BEGIN")
    side.execute("BEGIN")
    for version_id in range(1, versions + 1):
        generation = (version_id - 1) % generations
        if generation < skip_oldest:
            continue
        file_index = (version_id - 1) // generations
        path_ = shapes.version_path(file_index)
        factory = shapes.VersionRowFactory(path_, generation, EXTRACTOR_FP)
        conn.execute(
            "INSERT INTO file_versions VALUES (?,?,?,?,?,1)",
            (version_id, path_, factory.content_hash, EXTRACTOR_FP, generation),
        )
        conn.executemany(store.STORE_INSERTS["symbols"], factory.symbols(version_id))
        conn.executemany(
            store.STORE_INSERTS["reference_sites"], factory.reference_sites(version_id)
        )
        conn.executemany(
            store.STORE_INSERTS["identifiers"], factory.identifiers(version_id)
        )
        rows += shapes.ROWS_PER_FILE_VERSION
        written += 1
        for _vid, symbol_id, body, name_collapsed in factory.fts_documents(version_id):
            doc_id += 1
            side.execute(
                "INSERT INTO symbols_fts(rowid, symbol_id, body) VALUES (?,?,?)",
                (doc_id, symbol_id, body),
            )
            side.execute(
                "INSERT INTO symbols_trigram(rowid, symbol_id, name_collapsed) VALUES (?,?,?)",
                (doc_id, symbol_id, name_collapsed),
            )
            side.execute(
                "INSERT INTO search_symbols VALUES (?,?,?)",
                (version_id, symbol_id, doc_id),
            )
        if written % CHUNK_VERSIONS == 0:
            conn.execute("COMMIT")
            side.execute("COMMIT")
            conn.execute("BEGIN")
            side.execute("BEGIN")
    conn.execute("COMMIT")
    side.execute("COMMIT")
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    side.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    conn.close()
    side.close()
    return {
        "versions_written": written,
        "rows": rows,
        "fts_docs": doc_id,
        "seconds": round(time.monotonic() - started, 2),
        "store_bytes": store.main_file_bytes(store_path),
        "sidecar_bytes": store.main_file_bytes(sidecar_path),
    }


def retention_sweep(store_path: str, vacuum: bool) -> dict:
    conn = store.connect(store_path)
    before = store.main_file_bytes(store_path)
    started = time.monotonic()
    conn.execute("BEGIN")
    victims = f"SELECT version_id FROM file_versions WHERE cohort < {RETAINED_GENERATIONS_DROPPED}"
    for table in ("identifiers", "reference_sites", "symbols"):
        conn.execute(f"DELETE FROM {table} WHERE version_id IN ({victims})")
    conn.execute(
        f"DELETE FROM file_versions WHERE cohort < {RETAINED_GENERATIONS_DROPPED}"
    )
    conn.execute("COMMIT")
    delete_seconds = time.monotonic() - started
    checkpoint = conn.execute("PRAGMA wal_checkpoint(TRUNCATE)").fetchone()
    vacuum_seconds = None
    if vacuum:
        vac_started = time.monotonic()
        while conn.execute("PRAGMA freelist_count").fetchone()[0] > 0:
            conn.execute("PRAGMA incremental_vacuum(2000)").fetchall()
        vacuum_seconds = round(time.monotonic() - vac_started, 2)
        conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    wal_bytes = (
        os.path.getsize(store_path + "-wal")
        if os.path.exists(store_path + "-wal")
        else 0
    )
    conn.close()
    return {
        "store_bytes_before": before,
        "store_bytes_after": store.main_file_bytes(store_path),
        "delete_seconds": round(delete_seconds, 2),
        "incremental_vacuum_seconds": vacuum_seconds,
        "checkpoint_result_busy_log_checkpointed": list(checkpoint),
        "wal_bytes_after": wal_bytes,
    }


def run_arm(workdir: str, arm: str, versions: int, generations: int) -> dict:
    family = os.path.join(workdir, f"family_{arm}")
    retained_dir = os.path.join(family, "retained")
    if os.path.exists(family):
        shutil.rmtree(family)
    os.makedirs(family)

    live_store = os.path.join(family, "store.db")
    live_sidecar = os.path.join(family, "search.db")

    retained = None
    reader = None
    if arm == "pinned_reader":
        os.makedirs(retained_dir)
        log(f"{arm}: building the older generation a reader will pin")
        retained = write_generation(
            os.path.join(retained_dir, "store.db"),
            os.path.join(retained_dir, "search.db"),
            versions,
            generations,
        )
        reader = sqlite3.connect(
            f"file:{os.path.join(retained_dir, 'store.db')}?mode=ro", uri=True
        )
        reader.execute("BEGIN")
        reader.execute("SELECT COUNT(*) FROM file_versions").fetchone()

    log(f"{arm}: building the live generation")
    old_gen = write_generation(live_store, live_sidecar, versions, generations)
    baseline_bytes = store.dir_bytes(family)

    sampler = DirSampler(family)
    sampler.start()
    started = time.monotonic()

    sweep = None
    skip_oldest = 0
    if arm == "retention_first":
        log(f"{arm}: retention sweep before the rebuild")
        sweep = retention_sweep(live_store, vacuum=True)
        skip_oldest = RETAINED_GENERATIONS_DROPPED

    new_store = os.path.join(family, "store.db.rebuild")
    new_sidecar = os.path.join(family, "search.db.rebuild")
    log(f"{arm}: rebuilding a generation alongside the live one")
    new_gen = write_generation(
        new_store, new_sidecar, versions, generations, skip_oldest=skip_oldest
    )
    peak_during_rebuild = sampler.peak_bytes
    peak_wal = sampler.peak_wal_bytes

    superseded = os.path.join(family, "superseded")
    os.makedirs(superseded, exist_ok=True)
    for name in ("store.db", "search.db"):
        for suffix in ("", "-wal", "-shm"):
            src = os.path.join(family, name + suffix)
            if os.path.exists(src):
                shutil.move(src, os.path.join(superseded, name + suffix))
    os.replace(new_store, live_store)
    os.replace(new_sidecar, live_sidecar)
    promote_moment_bytes = store.dir_bytes(family)
    superseded_bytes = store.dir_bytes(superseded)
    shutil.rmtree(superseded)

    retained_bytes = store.dir_bytes(retained_dir) if retained else 0
    if reader is not None:
        reader.execute("COMMIT")
        reader.close()
        shutil.rmtree(retained_dir)

    after_release_bytes = store.dir_bytes(family)
    sampler.stop()
    total_seconds = time.monotonic() - started

    formula = {
        "old_generation_bytes": old_gen["store_bytes"]
        if arm != "retention_first"
        else sweep["store_bytes_after"],
        "new_generation_bytes": new_gen["store_bytes"],
        "sidecar_bytes": old_gen["sidecar_bytes"] + new_gen["sidecar_bytes"],
        "wal_temp_bytes": peak_wal,
        "reader_retained_bytes": retained_bytes,
    }
    predicted = sum(formula.values())
    measured = sampler.peak_bytes

    result = {
        "arm": arm,
        "versions": versions,
        "retained_generation": retained,
        "old_generation": old_gen,
        "new_generation": new_gen,
        "retention_sweep": sweep,
        "baseline_family_bytes": baseline_bytes,
        "peak_family_bytes_during_rebuild": peak_during_rebuild,
        "peak_family_bytes": measured,
        "peak_wal_bytes": peak_wal,
        "family_bytes_at_promote_moment": promote_moment_bytes,
        "superseded_generation_bytes": superseded_bytes,
        "retained_generation_bytes": retained_bytes,
        "family_bytes_after_release": after_release_bytes,
        "min_free_disk_bytes": sampler.min_free_bytes,
        "reader_pinned": reader is not None,
        "total_seconds": round(total_seconds, 2),
        "samples": sampler.samples,
        "timeline_time_total_wal": sampler.timeline,
        "peak_at_seconds": sampler.peak_at_seconds,
        "peak_file_breakdown": sampler.peak_breakdown,
        "formula_terms": formula,
        "formula_predicted_peak_bytes": predicted,
        "measured_over_predicted": round(measured / predicted, 4) if predicted else None,
        "delta_bytes": measured - predicted,
        "delta_percent": round((measured - predicted) / predicted * 100, 2)
        if predicted
        else None,
    }
    shutil.rmtree(family)
    return result


def main() -> int:
    workdir, outdir = sys.argv[1], sys.argv[2]
    versions = int(sys.argv[3])
    generations = int(sys.argv[4])
    os.makedirs(workdir, exist_ok=True)
    os.makedirs(outdir, exist_ok=True)

    result = {
        "sqlite_version": sqlite3.sqlite_version,
        "file_versions": versions,
        "generations_per_path": generations,
        "generations_dropped_by_retention": RETAINED_GENERATIONS_DROPPED,
        "rows_per_file_version": shapes.ROWS_PER_FILE_VERSION,
        "arms": [
            run_arm(workdir, "no_reader", versions, generations),
            run_arm(workdir, "pinned_reader", versions, generations),
            run_arm(workdir, "retention_first", versions, generations),
        ],
    }

    out = os.path.join(outdir, "promotion.json")
    with open(out, "w") as handle:
        json.dump(result, handle, indent=2, sort_keys=True)
    log(f"wrote {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
