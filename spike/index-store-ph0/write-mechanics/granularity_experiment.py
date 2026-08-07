#!/usr/bin/env python3
"""Commit-granularity harness: throughput, WAL peak, and SIGKILL crash reuse.

For each commit mode this runs one clean import (throughput + WAL peak), then
``--trials`` SIGKILL trials at randomized points inside the import. After each
kill it verifies the store, resumes the same import, and verifies again -- so
the reported reuse is what a real next run actually gets, not a guess.

Only this harness's own child process is ever signalled; the pid comes from
``subprocess.Popen``.

Usage: granularity_experiment.py <workdir> <outdir> <versions> <trials>
"""

from __future__ import annotations

import json
import os
import random
import signal
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "lib"))

import shapes  # noqa: E402
import store  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
IMPORTER = os.path.join(HERE, "importer.py")
CHUNK_VERSIONS = 100

MODE_SPECS = (
    {"label": "single", "mode": "single"},
    {"label": "per_chunk", "mode": "per_chunk"},
    {"label": "per_version", "mode": "per_version"},
    {"label": "per_version_nomarker", "mode": "per_version_nomarker"},
    {"label": "per_version_wal_headroom", "mode": "per_version", "autocheckpoint": 8000},
    {"label": "per_version_sync_full", "mode": "per_version", "synchronous": "FULL"},
)
KILL_SEED = 20260806


def log(msg: str) -> None:
    print(f"[granularity] {msg}", flush=True)


class WalSampler(threading.Thread):
    """Polls the -wal file while a child import runs."""

    def __init__(self, db: str, interval: float = 0.02):
        super().__init__(daemon=True)
        self.db = db
        self.interval = interval
        self.peak_wal = 0
        self.peak_total = 0
        self.samples = 0
        self._stop = threading.Event()

    def run(self) -> None:
        while not self._stop.is_set():
            wal = self.db + "-wal"
            try:
                wal_bytes = os.path.getsize(wal) if os.path.exists(wal) else 0
                total = store.db_bytes(self.db)
            except OSError:
                wal_bytes, total = 0, 0
            self.peak_wal = max(self.peak_wal, wal_bytes)
            self.peak_total = max(self.peak_total, total)
            self.samples += 1
            self._stop.wait(self.interval)

    def stop(self) -> None:
        self._stop.set()
        self.join(timeout=2.0)


def remove_db(path: str) -> None:
    for suffix in ("", "-wal", "-shm"):
        if os.path.exists(path + suffix):
            os.remove(path + suffix)


def importer_argv(db: str, spec: dict, versions: int) -> list[str]:
    return [
        sys.executable,
        IMPORTER,
        "--db",
        db,
        "--mode",
        spec["mode"],
        "--versions",
        str(versions),
        "--chunk",
        str(CHUNK_VERSIONS),
        "--autocheckpoint",
        str(spec.get("autocheckpoint", 1000)),
        "--synchronous",
        spec.get("synchronous", "NORMAL"),
    ]


def run_import(db: str, spec: dict, versions: int, kill_after: float | None) -> dict:
    sampler = WalSampler(db)
    sampler.start()
    started = time.monotonic()
    proc = subprocess.Popen(
        importer_argv(db, spec, versions),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )
    killed = False
    if kill_after is None:
        stdout, _ = proc.communicate()
    else:
        try:
            stdout, _ = proc.communicate(timeout=kill_after)
        except subprocess.TimeoutExpired:
            os.kill(proc.pid, signal.SIGKILL)
            killed = True
            stdout, _ = proc.communicate()
    elapsed = time.monotonic() - started
    sampler.stop()

    report = None
    if not killed:
        brace = stdout.find("{")
        if brace >= 0:
            try:
                report = json.loads(stdout[brace:])
            except json.JSONDecodeError:
                report = None
    return {
        "killed": killed,
        "kill_after_seconds": round(kill_after, 3) if kill_after else None,
        "returncode": proc.returncode,
        "wall_seconds": round(elapsed, 3),
        "peak_wal_bytes": sampler.peak_wal,
        "peak_db_plus_wal_bytes": sampler.peak_total,
        "wal_samples": sampler.samples,
        "report": report,
    }


def verify(db: str) -> dict:
    out = subprocess.run(
        [sys.executable, IMPORTER, "--db", db, "--verify"],
        capture_output=True,
        text=True,
        check=True,
    )
    brace = out.stdout.find("{")
    return json.loads(out.stdout[brace:])


def run_mode(workdir: str, spec: dict, versions: int, trials: int, rng: random.Random) -> dict:
    label = spec["label"]
    db = os.path.join(workdir, f"gran_{label}.db")
    remove_db(db)

    log(f"{label}: clean import of {versions} versions")
    clean = run_import(db, spec, versions, None)
    clean["verify"] = verify(db)
    clean["final_db_bytes"] = store.main_file_bytes(db)
    baseline_seconds = clean["wall_seconds"]
    remove_db(db)

    trial_results = []
    for trial in range(1, trials + 1):
        remove_db(db)
        fraction = rng.uniform(0.20, 0.80)
        kill_after = max(0.35, baseline_seconds * fraction)
        log(
            f"{label}: SIGKILL trial {trial}/{trials} at {fraction:.0%} "
            f"({kill_after:.2f}s of a {baseline_seconds:.2f}s import)"
        )
        crashed = run_import(db, spec, versions, kill_after)
        after_crash = verify(db)
        resumed = run_import(db, spec, versions, None)
        after_resume = verify(db)
        trial_results.append(
            {
                "trial": trial,
                "kill_fraction": round(fraction, 3),
                "kill_after_seconds": round(kill_after, 3),
                "crashed_run": {
                    k: v for k, v in crashed.items() if k not in ("report",)
                },
                "after_crash": after_crash,
                "resume_run": {
                    k: v for k, v in resumed.items() if k not in ("report",)
                },
                "resume_report": resumed["report"],
                "after_resume": after_resume,
                "reusable_versions_after_crash": after_crash[
                    "reusable_complete_versions"
                ],
                "truncated_versions_after_crash": after_crash[
                    "visible_but_truncated_versions"
                ],
                "resume_skipped": (resumed["report"] or {}).get("skipped_by_dedup"),
                "resume_imported": (resumed["report"] or {}).get("imported"),
                "final_intact_versions": after_resume["reusable_complete_versions"],
                "final_truncated_versions": after_resume[
                    "visible_but_truncated_versions"
                ],
                "final_quick_check": after_resume["quick_check"],
            }
        )
        remove_db(db)

    reusable = [t["reusable_versions_after_crash"] for t in trial_results]
    truncated_final = [t["final_truncated_versions"] for t in trial_results]
    return {
        "mode": label,
        "importer_mode": spec["mode"],
        "autocheckpoint_pages": spec.get("autocheckpoint", 1000),
        "synchronous": spec.get("synchronous", "NORMAL"),
        "versions": versions,
        "chunk_versions": CHUNK_VERSIONS if spec["mode"] == "per_chunk" else None,
        "clean": clean,
        "trials": trial_results,
        "summary": {
            "rows_per_second": (clean["report"] or {}).get("rows_per_second"),
            "versions_per_second": (clean["report"] or {}).get("versions_per_second"),
            "commits": (clean["report"] or {}).get("commits"),
            "peak_wal_bytes": clean["peak_wal_bytes"],
            "final_db_bytes": clean["final_db_bytes"],
            "reusable_after_crash_min": min(reusable) if reusable else None,
            "reusable_after_crash_max": max(reusable) if reusable else None,
            "reusable_after_crash_mean": round(sum(reusable) / len(reusable), 1)
            if reusable
            else None,
            "truncated_after_resume_max": max(truncated_final)
            if truncated_final
            else None,
            "all_quick_checks_ok": all(
                t["final_quick_check"] == "ok" for t in trial_results
            ),
        },
    }


def main() -> int:
    workdir, outdir = sys.argv[1], sys.argv[2]
    versions = int(sys.argv[3])
    trials = int(sys.argv[4])
    os.makedirs(workdir, exist_ok=True)
    os.makedirs(outdir, exist_ok=True)
    rng = random.Random(KILL_SEED)

    result = {
        "sqlite_version": __import__("sqlite3").sqlite_version,
        "versions_per_import": versions,
        "rows_per_file_version": shapes.ROWS_PER_FILE_VERSION,
        "rows_per_import": versions * shapes.ROWS_PER_FILE_VERSION,
        "trials_per_mode": trials,
        "kill_seed": KILL_SEED,
        "chunk_versions": CHUNK_VERSIONS,
        "journal_mode": "WAL",
        "synchronous": "NORMAL",
        "modes": [run_mode(workdir, spec, versions, trials, rng) for spec in MODE_SPECS],
    }

    out = os.path.join(outdir, "granularity.json")
    with open(out, "w") as handle:
        json.dump(result, handle, indent=2, sort_keys=True)
    log(f"wrote {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
