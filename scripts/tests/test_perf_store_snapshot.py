from __future__ import annotations

import importlib.util
import os
import sqlite3
import sys
import tempfile
import time
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "perf-store-snapshot.py"
SPEC = importlib.util.spec_from_file_location("perf_store_snapshot", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
snapshot = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = snapshot
SPEC.loader.exec_module(snapshot)


class PerfStoreSnapshotTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        self.destination = self.root / "destination"
        self.source_generation = self.source / "gen-001"
        self.source_generation.mkdir(parents=True)
        (self.source / "CURRENT").write_text("gen-001\n", encoding="utf-8")
        self._database(self.source_generation / "store.db", "store")
        self._database(self.source / "coord.db", "coord")

    def tearDown(self) -> None:
        self.temp.cleanup()

    @staticmethod
    def _database(path: Path, table: str) -> None:
        with sqlite3.connect(path) as connection:
            connection.execute(f"CREATE TABLE facts (name TEXT NOT NULL)")
            connection.execute("INSERT INTO facts(name) VALUES (?)", (table,))
            connection.commit()

    def test_snapshot_uses_read_only_backup_and_verifies_family(self) -> None:
        before = (self.source / "coord.db").read_bytes()
        result = snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertEqual(str(self.destination.resolve()), result["destination"])
        self.assertEqual("ok", result["quick_check"])
        self.assertEqual(before, (self.source / "coord.db").read_bytes())
        self.assertEqual("store", sqlite3.connect(self.destination / "gen-001" / "store.db").execute("SELECT name FROM facts").fetchone()[0])
        self.assertFalse(list(self.destination.rglob("*.db-wal")))
        self.assertFalse(list(self.destination.rglob("*.db-shm")))

    def test_snapshot_rejects_source_destination_alias_and_live_root(self) -> None:
        with self.assertRaisesRegex(ValueError, "alias"):
            snapshot.snapshot_family(self.source, self.source, live_root=self.root / "live")
        with self.assertRaisesRegex(ValueError, "live"):
            snapshot.snapshot_family(self.source, self.destination, live_root=self.source)

    def test_snapshot_rejects_live_owner_but_allows_dead_stale_claim(self) -> None:
        with sqlite3.connect(self.source / "coord.db") as connection:
            connection.execute(
                "CREATE TABLE requests (request_id TEXT, kind TEXT, state TEXT, claim_owner TEXT, claim_heartbeat_at INTEGER)"
            )
            connection.execute(
                "CREATE TABLE maintenance_intent (resource TEXT, run_id TEXT, owner_id TEXT, owner_pid INTEGER, heartbeat_at INTEGER, expires_at INTEGER)"
            )
            connection.execute(
                "CREATE TABLE writer_lease (resource TEXT, holder_id TEXT, holder_version TEXT, holder_pid INTEGER, heartbeat_at INTEGER, expires_at INTEGER, fencing_token INTEGER)"
            )
            connection.execute(
                "INSERT INTO writer_lease(resource, holder_id, holder_version, holder_pid, heartbeat_at, expires_at, fencing_token) VALUES (?, ?, ?, ?, ?, ?, ?)",
                ("store-writer", "live", "test", os.getpid(), int(time.time() * 1000), int(time.time() * 1000) + 10_000_000, 1),
            )
            connection.commit()
        with self.assertRaisesRegex(ValueError, "live owner"):
            snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")

        with sqlite3.connect(self.source / "coord.db") as connection:
            connection.execute("UPDATE writer_lease SET holder_pid=999999, expires_at=0")
            connection.commit()
        result = snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertEqual("ok", result["quick_check"])


if __name__ == "__main__":
    unittest.main()
