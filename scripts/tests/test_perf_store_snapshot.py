from __future__ import annotations

import importlib.util
import os
import sqlite3
import sys
import tempfile
import time
import unittest
from contextlib import closing
from unittest import mock
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
        with closing(sqlite3.connect(path)) as connection:
            connection.execute(f"CREATE TABLE facts (name TEXT NOT NULL)")
            connection.execute("INSERT INTO facts(name) VALUES (?)", (table,))
            connection.commit()

    def test_snapshot_uses_read_only_backup_and_verifies_family(self) -> None:
        before = (self.source / "coord.db").read_bytes()
        result = snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertEqual(str(self.destination.resolve()), result["destination"])
        self.assertEqual("ok", result["quick_check"])
        self.assertEqual(before, (self.source / "coord.db").read_bytes())
        with closing(sqlite3.connect(self.destination / "gen-001" / "store.db")) as connection:
            self.assertEqual("store", connection.execute("SELECT name FROM facts").fetchone()[0])
        self.assertFalse(list(self.destination.rglob("*.db-wal")))
        self.assertFalse(list(self.destination.rglob("*.db-shm")))

    def test_snapshot_requires_live_root_for_api_and_cli(self) -> None:
        with self.assertRaisesRegex(ValueError, "live root"):
            snapshot.snapshot_family(self.source, self.destination)
        with self.assertRaises(SystemExit):
            snapshot.parse_args(
                ["--source", str(self.source), "--destination", str(self.destination)]
            )

    def test_snapshot_rejects_source_destination_alias_and_live_root(self) -> None:
        with self.assertRaisesRegex(ValueError, "alias"):
            snapshot.snapshot_family(self.source, self.source, live_root=self.root / "live")
        with self.assertRaisesRegex(ValueError, "live"):
            snapshot.snapshot_family(self.source, self.destination, live_root=self.source)

    def test_snapshot_rejects_live_parent_child_and_hardlink_aliases(self) -> None:
        live = self.root / "live"
        live.mkdir()
        with self.assertRaisesRegex(ValueError, "live"):
            snapshot.snapshot_family(self.source, live / "snapshot", live_root=live)

        live_store = live / "gen-001"
        live_store.mkdir()
        live_file = live_store / "store.db"
        os.link(self.source_generation / "store.db", live_file)
        with self.assertRaisesRegex(ValueError, "hardlink|alias"):
            snapshot.snapshot_family(self.source, self.destination, live_root=live)

    def test_snapshot_rejects_symlinked_family_entries(self) -> None:
        alias = self.source / "alias.db"
        alias.symlink_to(self.source_generation / "store.db")
        with self.assertRaisesRegex(ValueError, "alias|symlink|reparse"):
            snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")

    def test_snapshot_rejects_live_owner_but_allows_dead_stale_claim(self) -> None:
        with closing(sqlite3.connect(self.source / "coord.db")) as connection:
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
                ("store-writer", "live", "test", os.getpid(), int(time.time() * 1000), 0, 1),
            )
            connection.commit()
        with self.assertRaisesRegex(ValueError, "live owner"):
            snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")

        with closing(sqlite3.connect(self.source / "coord.db")) as connection:
            connection.execute("UPDATE writer_lease SET holder_pid=999999, expires_at=0")
            connection.commit()
        result = snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertEqual("ok", result["quick_check"])

    def test_snapshot_maps_request_owner_to_writer_lease(self) -> None:
        now = int(time.time() * 1000)
        with closing(sqlite3.connect(self.source / "coord.db")) as connection:
            connection.execute(
                "CREATE TABLE requests (request_id TEXT, state TEXT, claim_owner TEXT, claim_heartbeat_at INTEGER)"
            )
            connection.execute(
                "CREATE TABLE writer_lease (resource TEXT, holder_id TEXT, holder_pid INTEGER, heartbeat_at INTEGER, expires_at INTEGER)"
            )
            connection.execute(
                "INSERT INTO requests VALUES (?, ?, ?, ?)",
                ("request-1", "claimed", "writer-a", now),
            )
            connection.execute(
                "INSERT INTO writer_lease VALUES (?, ?, ?, ?, ?)",
                ("store-writer", "writer-a", 424242, now, 0),
            )
            connection.commit()
        with mock.patch.object(snapshot, "_pid_is_alive", return_value=False):
            result = snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertEqual("ok", result["quick_check"])

    def test_snapshot_does_not_parse_opaque_request_owner_as_pid(self) -> None:
        with closing(sqlite3.connect(self.source / "coord.db")) as connection:
            connection.execute(
                "CREATE TABLE requests (request_id TEXT, state TEXT, claim_owner TEXT, claim_heartbeat_at INTEGER)"
            )
            connection.execute(
                "INSERT INTO requests VALUES (?, ?, ?, ?)",
                ("request-1", "claimed", "worker-999999", int(time.time() * 1000)),
            )
            connection.commit()
        with self.assertRaisesRegex(ValueError, "unknown .*owner"):
            snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")

    def test_snapshot_ignores_non_coordinator_claim_tables(self) -> None:
        with closing(sqlite3.connect(self.source / "coord.db")) as connection:
            connection.execute("CREATE TABLE unrelated_claims (state TEXT, owner_pid INTEGER)")
            connection.execute("INSERT INTO unrelated_claims VALUES (?, ?)", ("claimed", os.getpid()))
            connection.commit()
        result = snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertEqual("ok", result["quick_check"])

    def test_snapshot_refuses_unknown_owner_probe_even_when_expired(self) -> None:
        with closing(sqlite3.connect(self.source / "coord.db")) as connection:
            connection.execute(
                "CREATE TABLE writer_lease (resource TEXT, holder_id TEXT, holder_pid INTEGER, heartbeat_at INTEGER, expires_at INTEGER)"
            )
            connection.execute(
                "INSERT INTO writer_lease VALUES (?, ?, ?, ?, ?)",
                ("store-writer", "writer-a", 424242, 0, 0),
            )
            connection.commit()
        with mock.patch.object(snapshot, "_pid_is_alive", return_value=None):
            with self.assertRaisesRegex(ValueError, "unknown owner|live owner"):
                snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")

    def test_snapshot_uses_encoded_sqlite_uri(self) -> None:
        path = self.root / "space # unicode-ü.db"
        self._database(path, "uri")
        uri = snapshot._sqlite_uri(path)
        self.assertIn("%20", uri)
        self.assertIn("%23", uri)
        self.assertIn("%C3%BC", uri)
        with closing(sqlite3.connect(uri, uri=True)) as connection:
            self.assertEqual("uri", connection.execute("SELECT name FROM facts").fetchone()[0])

    def test_snapshot_preserves_wal_and_source_bytes(self) -> None:
        path = self.source_generation / "store.db"
        writer = sqlite3.connect(path)
        try:
            writer.execute("PRAGMA journal_mode=WAL")
            writer.execute("PRAGMA wal_autocheckpoint=0")
            writer.execute("INSERT INTO facts(name) VALUES (?)", ("wal",))
            writer.commit()
            source_paths = [path, Path(f"{path}-wal"), Path(f"{path}-shm")]
            before = {
                item: (item.read_bytes(), item.stat().st_ino, item.stat().st_size, item.stat().st_mtime_ns)
                for item in source_paths
            }
            result = snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
            after = {
                item: (item.read_bytes(), item.stat().st_ino, item.stat().st_size, item.stat().st_mtime_ns)
                for item in source_paths
            }
            self.assertEqual(before, after)
            with closing(sqlite3.connect(self.destination / "gen-001" / "store.db")) as destination_connection:
                self.assertEqual(
                    "wal",
                    destination_connection.execute("SELECT name FROM facts WHERE name = 'wal'").fetchone()[0],
                )
            self.assertEqual("ok", result["quick_check"])
            self.assertFalse(Path(f"{self.destination / 'gen-001' / 'store.db'}-wal").exists())
            self.assertFalse(Path(f"{self.destination / 'gen-001' / 'store.db'}-shm").exists())
        finally:
            writer.close()

    def test_snapshot_streams_digest_in_bounded_chunks(self) -> None:
        path = self.source / "payload.bin"
        path.write_bytes(b"x" * 4097)
        with mock.patch.object(Path, "read_bytes", side_effect=AssertionError("unbounded read")):
            digest = snapshot._digest_files(self.source, [Path("payload.bin")])
        self.assertEqual(64, len(digest))

    def test_snapshot_cleans_temporary_destination_on_failure(self) -> None:
        with mock.patch.object(snapshot, "_copy_family_files", side_effect=RuntimeError("copy failed")):
            with self.assertRaisesRegex(RuntimeError, "copy failed"):
                snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertFalse(self.destination.exists())
        self.assertFalse(list(self.root.glob(".perf-store-snapshot-*")))

    def test_snapshot_rejects_source_tree_change_before_promotion(self) -> None:
        marker = self.source / "marker.txt"
        marker.write_text("before", encoding="utf-8")
        original_copy = snapshot._copy_family_files

        def copy_then_change(source: Path, destination: Path):
            result = original_copy(source, destination)
            marker.write_text("after", encoding="utf-8")
            return result

        with mock.patch.object(snapshot, "_copy_family_files", side_effect=copy_then_change):
            with self.assertRaisesRegex(ValueError, "source family changed"):
                snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertFalse(self.destination.exists())
        self.assertFalse(list(self.root.glob(".perf-store-snapshot-*")))

    def test_windows_pid_probe_is_pointer_safe_and_reports_alive(self) -> None:
        class Function:
            def __init__(self, callback):
                self.callback = callback
                self.argtypes = None
                self.restype = None

            def __call__(self, *args):
                return self.callback(*args)

        def get_exit_code(_handle, output):
            snapshot.ctypes.cast(output, snapshot.ctypes.POINTER(snapshot.ctypes.wintypes.DWORD)).contents.value = 259
            return 1

        kernel32 = type("Kernel32", (), {})()
        kernel32.OpenProcess = Function(lambda *_: object())
        kernel32.GetExitCodeProcess = Function(get_exit_code)
        kernel32.CloseHandle = Function(lambda *_: 1)
        windll = type("WinDll", (), {"kernel32": kernel32})()
        with mock.patch.object(snapshot.os, "name", "nt"), mock.patch.object(
            snapshot.ctypes, "windll", windll, create=True
        ):
            self.assertTrue(snapshot._pid_is_alive(424242))
        self.assertEqual(
            [snapshot.ctypes.wintypes.DWORD, snapshot.ctypes.wintypes.BOOL, snapshot.ctypes.wintypes.DWORD],
            kernel32.OpenProcess.argtypes,
        )
        self.assertIs(kernel32.OpenProcess.restype, snapshot.ctypes.wintypes.HANDLE)
        self.assertEqual(
            [snapshot.ctypes.wintypes.HANDLE, snapshot.ctypes.POINTER(snapshot.ctypes.wintypes.DWORD)],
            kernel32.GetExitCodeProcess.argtypes,
        )
        self.assertIs(kernel32.GetExitCodeProcess.restype, snapshot.ctypes.wintypes.BOOL)
        self.assertEqual([snapshot.ctypes.wintypes.HANDLE], kernel32.CloseHandle.argtypes)
        self.assertIs(kernel32.CloseHandle.restype, snapshot.ctypes.wintypes.BOOL)

    def test_windows_pid_probe_treats_access_denied_as_unknown(self) -> None:
        class Function:
            def __init__(self, result):
                self.result = result
                self.argtypes = None
                self.restype = None

            def __call__(self, *_):
                return self.result

        kernel32 = type("Kernel32", (), {})()
        kernel32.OpenProcess = Function(0)
        kernel32.GetExitCodeProcess = Function(0)
        kernel32.CloseHandle = Function(1)
        windll = type("WinDll", (), {"kernel32": kernel32})()
        with mock.patch.object(snapshot.os, "name", "nt"), mock.patch.object(
            snapshot.ctypes, "windll", windll, create=True
        ), mock.patch.object(snapshot.ctypes, "get_last_error", return_value=5, create=True):
            self.assertIsNone(snapshot._pid_is_alive(424242))


if __name__ == "__main__":
    unittest.main()
