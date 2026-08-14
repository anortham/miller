#!/usr/bin/env python3
"""Make a read-only, whole-family store snapshot for performance replay."""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import tempfile
import time
from collections.abc import Iterable, Mapping
from typing import Any


CLAIM_STALE_MS = 5_000


def _canonical(path: Path | str) -> Path:
    return Path(path).expanduser().resolve(strict=False)


def _is_alias(left: Path, right: Path) -> bool:
    try:
        return left == right or left.is_relative_to(right) or right.is_relative_to(left)
    except AttributeError:  # pragma: no cover - Python 3.8 compatibility fallback.
        left_text = str(left)
        right_text = str(right)
        return left_text == right_text or left_text.startswith(right_text + os.sep) or right_text.startswith(left_text + os.sep)


def _same_file(left: Path, right: Path) -> bool:
    try:
        return left.exists() and right.exists() and os.path.samefile(left, right)
    except OSError:
        return False


def _sqlite_uri(path: Path) -> str:
    return f"file:{path.as_posix()}?mode=ro"


def _read_current(source: Path) -> str:
    current = source / "CURRENT"
    if not current.is_file():
        raise ValueError("source family is missing CURRENT")
    generation = current.read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"gen-[0-9]{3,}", generation):
        raise ValueError("source family CURRENT is malformed")
    generation_root = source / generation
    if not generation_root.is_dir() or not (generation_root / "store.db").is_file():
        raise ValueError("source family CURRENT does not identify a complete generation")
    return generation


def _pid_from_owner(value: Any) -> int | None:
    if isinstance(value, int) and value > 0:
        return value
    if isinstance(value, str):
        match = re.search(r"(?:^|[-_:])(?P<pid>[0-9]+)$", value)
        if match:
            return int(match.group("pid"))
    return None


def _pid_is_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    if os.name == "nt":  # The snapshot helper cannot import psutil or add a dependency.
        try:
            handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)
            if not handle:
                return False
            exit_code = ctypes.c_ulong()
            alive = bool(ctypes.windll.kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code))) and exit_code.value == 259
            ctypes.windll.kernel32.CloseHandle(handle)
            return alive
        except (AttributeError, OSError, TypeError, ValueError):
            return pid == os.getpid()
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return False
    return True


def _table_rows(connection: sqlite3.Connection, table: str) -> Iterable[Mapping[str, Any]]:
    columns = [row[1] for row in connection.execute(f"PRAGMA table_info({table})")]
    interesting = {
        column
        for column in columns
        if column.casefold() in {
            "state",
            "owner_pid",
            "claim_owner",
            "holder_pid",
            "heartbeat_at",
            "claim_heartbeat_at",
            "expires_at",
        }
    }
    if not interesting:
        return []
    query = f"SELECT {', '.join(interesting)} FROM {table}"
    return [dict(zip(interesting, row)) for row in connection.execute(query)]


def _check_claims(coordinator: Path) -> None:
    now_ms = int(time.time() * 1000)
    try:
        connection = sqlite3.connect(_sqlite_uri(coordinator), uri=True)
    except sqlite3.Error as exc:
        raise ValueError(f"cannot inspect coordinator read-only: {coordinator}") from exc
    try:
        tables = [
            row[0]
            for row in connection.execute(
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"
            )
        ]
        for table in tables:
            for row in _table_rows(connection, table):
                state = str(row.get("state", "")).casefold()
                claimed = state == "claimed" or table.casefold() in {"writer_lease", "maintenance_intent"}
                if not claimed:
                    continue
                owner = row.get("owner_pid")
                if owner is None:
                    owner = row.get("holder_pid")
                if owner is None:
                    owner = row.get("claim_owner")
                pid = _pid_from_owner(owner)
                heartbeat = row.get("claim_heartbeat_at")
                if heartbeat is None:
                    heartbeat = row.get("heartbeat_at")
                expires = row.get("expires_at")
                stale = (
                    isinstance(heartbeat, (int, float)) and heartbeat <= now_ms - CLAIM_STALE_MS
                ) or (isinstance(expires, (int, float)) and expires <= now_ms)
                if pid is None and not stale:
                    raise ValueError(f"unknown live owner in {table}")
                if pid is not None and _pid_is_alive(pid) and not stale:
                    raise ValueError(f"live owner in {table}")
    except sqlite3.Error as exc:
        raise ValueError(f"cannot inspect coordinator claims read-only: {coordinator}") from exc
    finally:
        connection.close()


def _sqlite_facts(path: Path) -> tuple[Any, ...]:
    stat = path.stat()
    try:
        connection = sqlite3.connect(_sqlite_uri(path), uri=True)
        facts = (
            stat.st_ino,
            stat.st_size,
            stat.st_mtime_ns,
            connection.execute("PRAGMA page_count").fetchone()[0],
            connection.execute("PRAGMA schema_version").fetchone()[0],
        )
    finally:
        connection.close()
    return facts


def _backup_database(source: Path, destination: Path) -> str:
    before = _sqlite_facts(source)
    destination.parent.mkdir(parents=True, exist_ok=True)
    source_connection = sqlite3.connect(_sqlite_uri(source), uri=True)
    destination_connection = sqlite3.connect(destination)
    try:
        source_connection.backup(destination_connection)
        destination_connection.commit()
        check = destination_connection.execute("PRAGMA quick_check").fetchone()[0]
        if str(check).casefold() != "ok":
            raise ValueError(f"destination quick_check failed: {destination}")
    finally:
        destination_connection.close()
        source_connection.close()
    if _sqlite_facts(source) != before:
        raise ValueError(f"source changed during read-only backup: {source}")
    return str(check)


def _is_sqlite_file(path: Path) -> bool:
    if path.suffix == ".db":
        return True
    try:
        with path.open("rb") as handle:
            return handle.read(16) == b"SQLite format 3\x00"
    except OSError:
        return False


def _copy_family_files(source: Path, destination: Path) -> tuple[list[Path], list[Path]]:
    generation = _read_current(source)
    source_files = [path for path in source.rglob("*") if path.is_file()]
    databases: list[Path] = []
    copied: list[Path] = []
    for source_path in source_files:
        relative = source_path.relative_to(source)
        if source_path.name.endswith(("-wal", "-shm")):
            continue
        destination_path = destination / relative
        if _is_sqlite_file(source_path):
            _backup_database(source_path, destination_path)
            databases.append(destination_path)
        else:
            destination_path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source_path, destination_path)
        copied.append(relative)
    required = {Path("CURRENT"), Path("coord.db"), Path(generation) / "store.db"}
    if not required.issubset(set(copied)):
        raise ValueError("source family is missing required store-owned files")
    return databases, copied


def _validate_source_tree(source: Path, live_root: Path | None) -> None:
    for path in source.rglob("*"):
        if not path.is_symlink():
            continue
        target = path.resolve(strict=False)
        if not target.is_relative_to(source):
            raise ValueError(f"source family contains an alias outside the family: {path}")
        if live_root is not None and _is_alias(target, live_root):
            raise ValueError(f"source family contains a live-store alias: {path}")


def _digest_files(root: Path, files: Iterable[Path]) -> str:
    digest = hashlib.sha256()
    for relative in sorted(files, key=lambda path: path.as_posix()):
        path = root / relative
        digest.update(relative.as_posix().encode())
        digest.update(path.read_bytes())
    return digest.hexdigest()


def snapshot_family(
    source: Path | str,
    destination: Path | str,
    *,
    live_root: Path | str | None = None,
) -> dict[str, Any]:
    source_path = _canonical(source)
    destination_path = _canonical(destination)
    if not source_path.is_dir():
        raise ValueError("source family must be a directory")
    if _is_alias(source_path, destination_path) or _same_file(source_path, destination_path):
        raise ValueError("source and destination are aliases")
    if live_root is not None:
        live_path = _canonical(live_root)
        if _is_alias(source_path, live_path) or _same_file(source_path, live_path):
            raise ValueError("source family is the live store")
        if _is_alias(destination_path, live_path):
            raise ValueError("destination aliases the live store")
    if destination_path.exists():
        raise ValueError("destination already exists")
    generation = _read_current(source_path)
    _validate_source_tree(source_path, _canonical(live_root) if live_root is not None else None)
    source_facts = {
        path.relative_to(source_path): (path.stat().st_ino, path.stat().st_size, path.stat().st_mtime_ns)
        for path in source_path.rglob("*")
        if path.is_file() and not path.name.endswith(("-wal", "-shm"))
    }
    coordinator = source_path / "coord.db"
    _check_claims(coordinator)
    destination_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix=".perf-store-snapshot-", dir=destination_path.parent))
    try:
        databases, copied = _copy_family_files(source_path, temporary)
        after_facts = {
            path.relative_to(source_path): (path.stat().st_ino, path.stat().st_size, path.stat().st_mtime_ns)
            for path in source_path.rglob("*")
            if path.is_file() and not path.name.endswith(("-wal", "-shm"))
        }
        if after_facts != source_facts:
            raise ValueError("source family changed during snapshot")
        for path in temporary.rglob("*"):
            if path.name.endswith(("-wal", "-shm")):
                raise ValueError("snapshot contains a WAL or SHM sidecar")
        quick_checks = {
            str(path.relative_to(temporary)): "ok"
            for path in databases
        }
        temporary.replace(destination_path)
    except Exception:
        shutil.rmtree(temporary, ignore_errors=True)
        raise
    return {
        "source": str(source_path),
        "destination": str(destination_path),
        "generation": generation,
        "files": [path.as_posix() for path in copied],
        "databases": quick_checks,
        "quick_check": "ok",
        "sha256": _digest_files(destination_path, copied),
        "wal_shm": False,
    }


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--destination", type=Path, required=True)
    parser.add_argument("--live-root", type=Path)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        print(json.dumps(snapshot_family(args.source, args.destination, live_root=args.live_root), sort_keys=True))
    except (OSError, ValueError, sqlite3.Error) as exc:
        print(f"perf-store-snapshot: {exc}", file=os.sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
