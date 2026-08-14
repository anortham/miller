from __future__ import annotations

import importlib.util
import os
import sqlite3
import subprocess
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
        self.live = self.root / "live"
        self.source_generation = self.source / "gen-001"
        self.source_generation.mkdir(parents=True)
        self.live.mkdir()
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

    def _insert_maintenance_intent(self, owner_pid: int) -> None:
        with closing(sqlite3.connect(self.source / "coord.db")) as connection:
            connection.execute(
                "CREATE TABLE maintenance_intent ("
                "resource TEXT, run_id TEXT, action TEXT, source_generation_name TEXT, "
                "owner_id TEXT, owner_pid INTEGER, fencing_token INTEGER, heartbeat_at INTEGER, "
                "expires_at INTEGER, started_at INTEGER, plan_fingerprint TEXT, source_min_writer_version TEXT"
                ")"
            )
            connection.execute(
                "INSERT INTO maintenance_intent VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                (
                    "store-maintenance",
                    "run-1",
                    "gc",
                    "gen-001",
                    "owner-1",
                    owner_pid,
                    1,
                    0,
                    1,
                    0,
                    "plan-1",
                    "julie-1",
                ),
            )
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

    def test_snapshot_requires_existing_directory_live_root(self) -> None:
        with self.assertRaisesRegex(ValueError, "live root"):
            snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "missing-live")

        live_file = self.root / "live-file"
        live_file.write_text("not a family", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "live root"):
            snapshot.snapshot_family(self.source, self.destination, live_root=live_file)

    def test_snapshot_rejects_source_destination_alias_and_live_root(self) -> None:
        with self.assertRaisesRegex(ValueError, "alias"):
            snapshot.snapshot_family(self.source, self.source, live_root=self.root / "live")
        with self.assertRaisesRegex(ValueError, "live"):
            snapshot.snapshot_family(self.source, self.destination, live_root=self.source)

    def test_snapshot_rejects_live_parent_child_and_hardlink_aliases(self) -> None:
        live = self.root / "live"
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

    def test_snapshot_rejects_live_root_symlink_entries(self) -> None:
        live = self.root / "live"
        (live / "source-alias.db").symlink_to(self.source_generation / "store.db")
        with self.assertRaisesRegex(ValueError, "live|alias|symlink|reparse"):
            snapshot.snapshot_family(self.source, self.destination, live_root=live)

    def test_snapshot_uses_private_read_only_shadow_without_mutating_source(self) -> None:
        source = self.source_generation / "store.db"
        destination = self.root / "copy.db"
        producer = subprocess.run(
            [
                sys.executable,
                "-c",
                (
                    "import os, sqlite3, sys; "
                    "connection = sqlite3.connect(sys.argv[1]); "
                    "connection.execute('PRAGMA journal_mode=WAL'); "
                    "connection.execute('PRAGMA wal_autocheckpoint=0'); "
                    "connection.execute(\"INSERT INTO facts(name) VALUES ('shadow-wal')\"); "
                    "connection.commit(); os._exit(0)"
                ),
                str(source),
            ],
            check=False,
        )
        self.assertEqual(0, producer.returncode)
        connect_inputs = []
        copy_inputs = []
        original_connect = snapshot.sqlite3.connect
        original_copy = snapshot._copy_file_stream

        before = snapshot._database_state(source)

        def connect(database, *args, **kwargs):
            connect_inputs.append(database)
            return original_connect(database, *args, **kwargs)

        def copy_file(source_path, destination_path):
            copy_inputs.append((source_path, destination_path))
            original_copy(source_path, destination_path)

        with mock.patch.object(snapshot.sqlite3, "connect", side_effect=connect), mock.patch.object(
            snapshot, "_copy_file_stream", side_effect=copy_file
        ):
            result = snapshot._backup_database(source, destination)
        self.assertEqual("ok", result)
        self.assertEqual(before, snapshot._database_state(source))
        self.assertTrue(copy_inputs)
        self.assertTrue(all(path == source or path.name.startswith("store.db-") for path, _ in copy_inputs))
        self.assertTrue(all(destination_path != source for _, destination_path in copy_inputs))
        shadow_roots = {destination_path.parent for _, destination_path in copy_inputs}
        self.assertTrue(all(root.name.startswith(".perf-store-input-") for root in shadow_roots))
        self.assertTrue(all(not root.exists() for root in shadow_roots))
        read_only_inputs = [value for value in connect_inputs if isinstance(value, str) and "mode=ro" in value]
        self.assertTrue(read_only_inputs)
        self.assertTrue(all(str(source) not in value for value in read_only_inputs))
        with closing(sqlite3.connect(destination)) as connection:
            self.assertEqual(
                "shadow-wal",
                connection.execute("SELECT name FROM facts WHERE name = 'shadow-wal'").fetchone()[0],
            )

    def test_snapshot_allows_shm_churn_during_wal_backup(self) -> None:
        source = self.source_generation / "store.db"
        destination = self.root / "copy.db"
        producer = subprocess.run(
            [
                sys.executable,
                "-c",
                (
                    "import os, sqlite3, sys; "
                    "connection = sqlite3.connect(sys.argv[1]); "
                    "connection.execute('PRAGMA journal_mode=WAL'); "
                    "connection.execute('PRAGMA wal_autocheckpoint=0'); "
                    "connection.execute(\"INSERT INTO facts(name) VALUES ('shm-churn')\"); "
                    "connection.commit(); os._exit(0)"
                ),
                str(source),
            ],
            check=False,
        )
        self.assertEqual(0, producer.returncode)
        shm = Path(f"{source}-shm")
        self.assertTrue(shm.exists())
        durable_members = (source, Path(f"{source}-wal"))
        before = {
            member: (
                member.read_bytes(),
                member.stat().st_dev,
                member.stat().st_ino,
                member.stat().st_mtime_ns,
                member.stat().st_ctime_ns,
                member.stat().st_mode,
            )
            for member in durable_members
        }
        original_copy = snapshot._copy_file_stream
        mutated = False
        copy_inputs = []

        def copy_with_shm_churn(source_path: Path, destination_path: Path) -> None:
            nonlocal mutated
            copy_inputs.append((source_path, destination_path))
            original_copy(source_path, destination_path)
            if source_path == source and not mutated:
                shm.write_bytes(shm.read_bytes() + b"churn")
                item_stat = shm.stat()
                os.utime(shm, ns=(item_stat.st_atime_ns, item_stat.st_mtime_ns + 1))
                mutated = True

        with mock.patch.object(snapshot, "_copy_file_stream", side_effect=copy_with_shm_churn):
            result = snapshot.snapshot_family(self.source, destination, live_root=self.root / "live")

        after = {
            member: (
                member.read_bytes(),
                member.stat().st_dev,
                member.stat().st_ino,
                member.stat().st_mtime_ns,
                member.stat().st_ctime_ns,
                member.stat().st_mode,
            )
            for member in durable_members
        }
        self.assertTrue(mutated)
        self.assertEqual(before, after)
        self.assertNotIn(shm, [source_path for source_path, _ in copy_inputs])
        self.assertEqual("ok", result["quick_check"])
        with closing(sqlite3.connect(destination / "gen-001" / "store.db")) as connection:
            self.assertEqual(
                "shm-churn",
                connection.execute("SELECT name FROM facts WHERE name = 'shm-churn'").fetchone()[0],
            )
        self.assertFalse(list(destination.rglob("*.db-shm")))

    def test_snapshot_copies_all_generations_bases_and_sidecars(self) -> None:
        second_generation = self.source / "gen-002"
        second_generation.mkdir()
        self._database(second_generation / "store.db", "second")
        base = self.source / "resolution" / "bases" / "base-001.db"
        base.parent.mkdir(parents=True)
        self._database(base, "base")
        sidecar = self.source / "gen-001" / "sidecars" / "search.db"
        sidecar.parent.mkdir()
        self._database(sidecar, "sidecar")
        (self.source / "manifest.json").write_text("{\"generation\":4}\n", encoding="utf-8")

        result = snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")

        for relative in (
            Path("gen-002/store.db"),
            Path("resolution/bases/base-001.db"),
            Path("gen-001/sidecars/search.db"),
            Path("manifest.json"),
        ):
            self.assertTrue((self.destination / relative).is_file())
        self.assertTrue({
            "gen-002/store.db",
            "resolution/bases/base-001.db",
            "gen-001/sidecars/search.db",
        }.issubset(result["databases"]))

    def test_snapshot_rejects_live_maintenance_owner_even_when_expired(self) -> None:
        self._insert_maintenance_intent(os.getpid())
        with self.assertRaisesRegex(ValueError, "live owner"):
            snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")

    def test_snapshot_rejects_unknown_maintenance_owner_even_when_expired(self) -> None:
        self._insert_maintenance_intent(424242)
        with mock.patch.object(snapshot, "_pid_is_alive", return_value=None):
            with self.assertRaisesRegex(ValueError, "unknown owner"):
                snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")

    def test_snapshot_allows_dead_maintenance_owner(self) -> None:
        self._insert_maintenance_intent(424242)
        with mock.patch.object(snapshot, "_pid_is_alive", return_value=False):
            result = snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertEqual("ok", result["quick_check"])

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
        producer = subprocess.run(
            [
                sys.executable,
                "-c",
                (
                    "import os, sqlite3, sys; "
                    "connection = sqlite3.connect(sys.argv[1]); "
                    "connection.execute('PRAGMA journal_mode=WAL'); "
                    "connection.execute('PRAGMA wal_autocheckpoint=0'); "
                    "connection.execute(\"INSERT INTO facts(name) VALUES ('wal')\"); "
                    "connection.commit(); os._exit(0)"
                ),
                str(path),
            ],
            check=False,
        )
        self.assertEqual(0, producer.returncode)
        source_paths = [path, Path(f"{path}-wal"), Path(f"{path}-shm")]
        self.assertTrue(all(item.exists() for item in source_paths))
        before = {
            item: (
                item.read_bytes(),
                item.stat().st_ino,
                item.stat().st_size,
                item.stat().st_mtime_ns,
                item.stat().st_ctime_ns,
                item.stat().st_mode,
            )
            for item in source_paths
        }
        result = snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        after = {
            item: (
                item.read_bytes(),
                item.stat().st_ino,
                item.stat().st_size,
                item.stat().st_mtime_ns,
                item.stat().st_ctime_ns,
                item.stat().st_mode,
            )
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

    def test_snapshot_rejects_same_size_restored_mtime_source_change(self) -> None:
        marker = self.source / "marker.txt"
        marker.write_text("before", encoding="utf-8")
        original_copy = snapshot._copy_family_files
        original_stat = marker.stat()

        def copy_then_mutate(source: Path, destination: Path):
            result = original_copy(source, destination)
            marker.write_text("after!", encoding="utf-8")
            os.utime(marker, ns=(original_stat.st_atime_ns, original_stat.st_mtime_ns))
            return result

        with mock.patch.object(snapshot, "_copy_family_files", side_effect=copy_then_mutate):
            with self.assertRaisesRegex(ValueError, "source family changed"):
                snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertFalse(self.destination.exists())

    def test_snapshot_rejects_same_size_restored_mtime_database_change(self) -> None:
        database = self.source_generation / "store.db"
        original_copy = snapshot._copy_family_files
        original_stat = database.stat()

        def copy_then_mutate(source: Path, destination: Path):
            result = original_copy(source, destination)
            with closing(sqlite3.connect(database)) as connection:
                connection.execute("UPDATE facts SET name = 'change'")
                connection.commit()
            os.utime(database, ns=(original_stat.st_atime_ns, original_stat.st_mtime_ns))
            return result

        with mock.patch.object(snapshot, "_copy_family_files", side_effect=copy_then_mutate):
            with self.assertRaisesRegex(ValueError, "source family changed"):
                snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertFalse(self.destination.exists())

    def test_snapshot_rejects_same_size_restored_mtime_wal_change(self) -> None:
        database = self.source_generation / "store.db"
        producer = subprocess.run(
            [
                sys.executable,
                "-c",
                (
                    "import os, sqlite3, sys; "
                    "connection = sqlite3.connect(sys.argv[1]); "
                    "connection.execute('PRAGMA journal_mode=WAL'); "
                    "connection.execute('PRAGMA wal_autocheckpoint=0'); "
                    "connection.execute(\"INSERT INTO facts(name) VALUES ('wal-change')\"); "
                    "connection.commit(); os._exit(0)"
                ),
                str(database),
            ],
            check=False,
        )
        self.assertEqual(0, producer.returncode)
        wal = Path(f"{database}-wal")
        self.assertTrue(wal.exists())
        original_copy = snapshot._copy_family_files
        original_stat = wal.stat()

        def copy_then_mutate(source: Path, destination: Path):
            result = original_copy(source, destination)
            payload = wal.read_bytes()
            wal.write_bytes(payload[::-1])
            os.utime(wal, ns=(original_stat.st_atime_ns, original_stat.st_mtime_ns))
            return result

        with mock.patch.object(snapshot, "_copy_family_files", side_effect=copy_then_mutate):
            with self.assertRaisesRegex(ValueError, "source family changed"):
                snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertFalse(self.destination.exists())

    def test_backup_closes_source_when_destination_connect_fails(self) -> None:
        source_connection = mock.Mock()
        connect = mock.Mock(side_effect=[source_connection, sqlite3.OperationalError("destination connect")])
        with mock.patch.object(snapshot.sqlite3, "connect", side_effect=connect):
            with self.assertRaisesRegex(sqlite3.OperationalError, "destination connect"):
                snapshot._backup_database(self.source_generation / "store.db", self.root / "copy.db")
        source_connection.close.assert_called_once_with()

    def test_backup_closes_both_connections_when_backup_fails(self) -> None:
        source_connection = mock.Mock()
        destination_connection = mock.Mock()
        source_connection.backup.side_effect = RuntimeError("backup failed")
        with mock.patch.object(
            snapshot.sqlite3,
            "connect",
            side_effect=[source_connection, destination_connection],
        ), mock.patch.object(snapshot, "_sqlite_facts", return_value=("stable",)):
            with self.assertRaisesRegex(RuntimeError, "backup failed"):
                snapshot._backup_database(self.source_generation / "store.db", self.root / "copy.db")
        source_connection.close.assert_called_once_with()
        destination_connection.close.assert_called_once_with()

    def test_snapshot_digest_failure_cleans_before_promotion(self) -> None:
        original_digest = snapshot._digest_files

        def fail_temporary_digest(root: Path, files):
            if root.name.startswith(".perf-store-snapshot-"):
                raise RuntimeError("digest failed")
            return original_digest(root, files)

        with mock.patch.object(snapshot, "_digest_files", side_effect=fail_temporary_digest):
            with self.assertRaisesRegex(RuntimeError, "digest failed"):
                snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertFalse(self.destination.exists())
        self.assertFalse(list(self.root.glob(".perf-store-snapshot-*")))

    def test_snapshot_surfaces_cleanup_failure_and_keeps_destination_absent(self) -> None:
        with mock.patch.object(snapshot, "_copy_family_files", side_effect=RuntimeError("copy failed")), mock.patch.object(
            snapshot.shutil, "rmtree", side_effect=OSError("cleanup failed")
        ):
            with self.assertRaisesRegex(OSError, "cleanup failed"):
                snapshot.snapshot_family(self.source, self.destination, live_root=self.root / "live")
        self.assertFalse(self.destination.exists())

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
