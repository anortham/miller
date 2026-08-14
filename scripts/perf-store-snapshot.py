#!/usr/bin/env python3
"""Make a read-only, whole-family store snapshot for performance replay."""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import errno
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import stat
import tempfile
import time
from collections.abc import Iterable, Mapping
from contextlib import contextmanager
from typing import Any


CLAIM_STALE_MS = 5_000
DIGEST_CHUNK_SIZE = 1024 * 1024
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
STILL_ACTIVE = 259
WINDOWS_DEAD_PROBE_ERRORS = frozenset({2, 6, 87, 1168})


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


def _path_has_reparse_point(path: Path) -> bool:
    candidate = path.expanduser()
    if not candidate.is_absolute():
        candidate = Path.cwd() / candidate
    for item in (candidate, *candidate.parents):
        try:
            item_stat = item.lstat()
        except OSError:
            continue
        if stat.S_ISLNK(item_stat.st_mode) or getattr(item_stat, "st_file_attributes", 0) & 0x400:
            return True
    return False


def _sqlite_uri(path: Path) -> str:
    absolute = path.expanduser()
    if not absolute.is_absolute():
        absolute = Path.cwd() / absolute
    return f"{absolute.resolve(strict=False).as_uri()}?mode=ro"


def _copy_file_stream(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with source.open("rb") as source_handle, destination.open("wb") as destination_handle:
        while chunk := source_handle.read(DIGEST_CHUNK_SIZE):
            destination_handle.write(chunk)


def _database_state(path: Path) -> tuple[tuple[str, tuple[Any, ...] | None, str | None], ...]:
    state: list[tuple[str, tuple[Any, ...] | None, str | None]] = []
    for suffix in ("", "-wal", "-shm"):
        member = Path(f"{path}{suffix}")
        facts = _file_facts(member)
        digest = _digest_files(member.parent, [Path(member.name)]) if facts is not None else None
        state.append((suffix, facts, digest))
    return tuple(state)


@contextmanager
def _database_input(path: Path, *, scratch_dir: Path | None = None) -> Iterable[Path]:
    before = _database_state(path)
    if before[0][1] is None:
        raise ValueError(f"source database is missing: {path}")
    temporary_directory = str(scratch_dir) if scratch_dir is not None else None
    with tempfile.TemporaryDirectory(prefix=".perf-store-input-", dir=temporary_directory) as directory:
        shadow = Path(directory) / path.name
        for suffix, facts, _digest in before:
            if facts is not None:
                _copy_file_stream(Path(f"{path}{suffix}"), Path(f"{shadow}{suffix}"))
        if _database_state(path) != before:
            raise ValueError(f"source database changed while creating read-only shadow: {path}")
        yield shadow
        if _database_state(path) != before:
            raise ValueError(f"source database changed while using read-only shadow: {path}")


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
    if isinstance(value, int) and not isinstance(value, bool) and value > 0:
        return value
    return None


def _windows_pid_is_alive(pid: int) -> bool | None:
    try:
        kernel32 = ctypes.windll.kernel32
        open_process = kernel32.OpenProcess
        get_exit_code = kernel32.GetExitCodeProcess
        close_handle = kernel32.CloseHandle
        get_last_error = getattr(kernel32, "GetLastError", None)
        open_process.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        open_process.restype = wintypes.HANDLE
        get_exit_code.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        get_exit_code.restype = wintypes.BOOL
        close_handle.argtypes = [wintypes.HANDLE]
        close_handle.restype = wintypes.BOOL
        if get_last_error is not None:
            get_last_error.argtypes = []
            get_last_error.restype = wintypes.DWORD
    except (AttributeError, OSError, TypeError, ValueError):
        return None
    try:
        handle = open_process(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    except (OSError, TypeError, ValueError):
        return None
    if not handle:
        if get_last_error is not None:
            try:
                error = get_last_error()
            except (OSError, TypeError, ValueError):
                error = None
        else:
            try:
                error = ctypes.get_last_error()
            except AttributeError:
                error = None
        if error in WINDOWS_DEAD_PROBE_ERRORS:
            return False
        return None
    try:
        exit_code = wintypes.DWORD()
        try:
            if not get_exit_code(handle, ctypes.byref(exit_code)):
                return None
        except (OSError, TypeError, ValueError):
            return None
        return exit_code.value == STILL_ACTIVE
    finally:
        try:
            close_handle(handle)
        except (OSError, TypeError, ValueError):
            pass


def _pid_is_alive(pid: int) -> bool | None:
    if pid <= 0:
        return False
    if os.name == "nt":
        return _windows_pid_is_alive(pid)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError as exc:
        if exc.errno == errno.ESRCH:
            return False
        if exc.errno == errno.EPERM:
            return True
        return None
    return True


def _table_rows(
    connection: sqlite3.Connection,
    table: str,
    columns: tuple[str, ...],
) -> Iterable[Mapping[str, Any]]:
    try:
        return [dict(zip(columns, row)) for row in connection.execute(f"SELECT {', '.join(columns)} FROM {table}")]
    except sqlite3.OperationalError as exc:
        if "no such table" in str(exc).casefold():
            return []
        raise


def _claim_is_stale(heartbeat: Any, now_ms: int) -> bool:
    return (
        isinstance(heartbeat, (int, float))
        and not isinstance(heartbeat, bool)
        and heartbeat <= now_ms - CLAIM_STALE_MS
    )


def _check_claims(coordinator: Path) -> None:
    now_ms = int(time.time() * 1000)
    try:
        with _database_input(coordinator) as readable_coordinator:
            connection = sqlite3.connect(_sqlite_uri(readable_coordinator), uri=True)
            try:
                writer_pids: dict[str, int] = {}
                owner_states: dict[int, bool | None] = {}
                for row in _table_rows(connection, "writer_lease", ("holder_id", "holder_pid")):
                    holder_id = row.get("holder_id")
                    pid = _pid_from_owner(row.get("holder_pid"))
                    if holder_id is None or pid is None:
                        raise ValueError("unknown owner in writer_lease")
                    writer_pids[str(holder_id)] = pid
                    owner_states[pid] = _pid_is_alive(pid)
                    if owner_states[pid] is not False:
                        raise ValueError("live owner or unknown owner in writer_lease")

                for row in _table_rows(connection, "maintenance_intent", ("owner_pid",)):
                    pid = _pid_from_owner(row.get("owner_pid"))
                    if pid is None:
                        raise ValueError("unknown owner in maintenance_intent")
                    owner_states[pid] = _pid_is_alive(pid)
                    if owner_states[pid] is not False:
                        raise ValueError("live owner or unknown owner in maintenance_intent")

                for row in _table_rows(
                    connection,
                    "requests",
                    ("state", "claim_owner", "claim_heartbeat_at"),
                ):
                    if str(row.get("state", "")).casefold() != "claimed":
                        continue
                    owner = row.get("claim_owner")
                    pid = writer_pids.get(str(owner)) if owner is not None else None
                    if pid is not None:
                        state = owner_states.setdefault(pid, _pid_is_alive(pid))
                        if state is not False:
                            raise ValueError("live owner or unknown owner in requests")
                    elif not _claim_is_stale(row.get("claim_heartbeat_at"), now_ms):
                        raise ValueError("unknown live owner in requests")
            finally:
                connection.close()
    except ValueError:
        raise
    except sqlite3.Error as exc:
        raise ValueError(f"cannot inspect coordinator claims read-only: {coordinator}") from exc


def _file_facts(path: Path) -> tuple[Any, ...] | None:
    try:
        item_stat = path.stat()
    except FileNotFoundError:
        return None
    return (
        item_stat.st_dev,
        item_stat.st_ino,
        item_stat.st_size,
        item_stat.st_mtime_ns,
        getattr(item_stat, "st_ctime_ns", None),
        item_stat.st_mode,
    )


def _sqlite_facts(
    path: Path,
    connection: sqlite3.Connection | None = None,
) -> tuple[Any, ...]:
    owns_connection = connection is None
    if owns_connection:
        connection = sqlite3.connect(_sqlite_uri(path), uri=True)
    assert connection is not None
    try:
        data_version = connection.execute("PRAGMA data_version").fetchone()[0]
        page_count = connection.execute("PRAGMA page_count").fetchone()[0]
        schema_version = connection.execute("PRAGMA schema_version").fetchone()[0]
        return (
            _file_facts(path),
            _file_facts(Path(f"{path}-wal")),
            _file_facts(Path(f"{path}-shm")),
            data_version,
            page_count,
            schema_version,
        )
    finally:
        if owns_connection:
            connection.close()


def _backup_database(source: Path, destination: Path) -> str:
    destination.parent.mkdir(parents=True, exist_ok=True)
    source_connection: sqlite3.Connection | None = None
    destination_connection: sqlite3.Connection | None = None
    with _database_input(source, scratch_dir=destination.parent) as readable_source:
        try:
            source_connection = sqlite3.connect(_sqlite_uri(readable_source), uri=True)
            destination_connection = sqlite3.connect(destination)
            before_sqlite = _sqlite_facts(readable_source, source_connection)
            before_source = _database_state(source)
            source_connection.backup(destination_connection)
            destination_connection.commit()
            destination_connection.execute("PRAGMA journal_mode=DELETE")
            destination_connection.commit()
            check = destination_connection.execute("PRAGMA quick_check").fetchone()[0]
            if str(check).casefold() != "ok":
                raise ValueError(f"destination quick_check failed: {destination}")
            after_sqlite = _sqlite_facts(readable_source, source_connection)
            after_source = _database_state(source)
            if before_sqlite[3:] != after_sqlite[3:] or before_source != after_source:
                raise ValueError(f"source changed during read-only backup: {source}")
        finally:
            if destination_connection is not None:
                destination_connection.close()
            if source_connection is not None:
                source_connection.close()
    return str(check)


def _is_sqlite_file(path: Path) -> bool:
    if path.suffix == ".db":
        return True
    try:
        with path.open("rb") as handle:
            return handle.read(16) == b"SQLite format 3\x00"
    except OSError:
        return False


def _source_files(source: Path) -> list[Path]:
    files: list[Path] = []
    for path in source.rglob("*"):
        if _path_has_reparse_point(path):
            raise ValueError(f"source family contains a symlink or reparse alias: {path}")
        if path.is_file():
            files.append(path.relative_to(source))
    return sorted(files, key=lambda path: path.as_posix())


def _source_tree_facts(
    source: Path,
    files: Iterable[Path] | None = None,
) -> dict[Path, tuple[Any, ...]]:
    facts: dict[Path, tuple[Any, ...]] = {}
    relatives = _source_files(source) if files is None else files
    for relative in relatives:
        item_facts = _file_facts(source / relative)
        if item_facts is None:
            raise ValueError(f"source file disappeared while reading: {source / relative}")
        facts[relative] = item_facts
    return facts


def _source_state(source: Path) -> tuple[list[Path], dict[Path, tuple[Any, ...]], str]:
    files = _source_files(source)
    facts = _source_tree_facts(source, files)
    digest = _digest_files(source, files)
    after_files = _source_files(source)
    after_facts = _source_tree_facts(source, after_files)
    after_digest = _digest_files(source, after_files)
    if files != after_files or facts != after_facts or digest != after_digest:
        raise ValueError("source family changed while reading")
    return files, facts, digest


def _database_digest(path: Path) -> str:
    files = [Path(path.name)]
    for suffix in ("-wal", "-shm"):
        sidecar = Path(f"{path.name}{suffix}")
        if (path.parent / sidecar).exists():
            files.append(sidecar)
    return _digest_files(path.parent, files)


def _copy_family_files(source: Path, destination: Path) -> tuple[list[Path], list[Path]]:
    generation = _read_current(source)
    databases: list[Path] = []
    copied: list[Path] = []
    for relative in _source_files(source):
        source_path = source / relative
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


def _validate_source_tree(source: Path, live_root: Path) -> None:
    if not live_root.is_dir():
        raise ValueError("live root must be an existing directory")
    live_identities: dict[tuple[int, int], Path] = {}
    for live_path in live_root.rglob("*"):
        if _path_has_reparse_point(live_path):
            raise ValueError(f"live root contains a symlink or reparse alias: {live_path}")
        if not live_path.is_file():
            continue
        item_stat = live_path.stat()
        live_identities[(item_stat.st_dev, item_stat.st_ino)] = live_path
    for relative in _source_files(source):
        path = source / relative
        item_stat = path.stat()
        live_alias = live_identities.get((item_stat.st_dev, item_stat.st_ino))
        if live_alias is not None:
            raise ValueError(f"source family contains a hardlink alias: {path} -> {live_alias}")


def _digest_files(root: Path, files: Iterable[Path]) -> str:
    digest = hashlib.sha256()
    for relative in sorted(files, key=lambda path: path.as_posix()):
        path = root / relative
        digest.update(relative.as_posix().encode())
        digest.update(b"\0")
        with path.open("rb") as handle:
            while chunk := handle.read(DIGEST_CHUNK_SIZE):
                digest.update(chunk)
    return digest.hexdigest()


def _destination_files(root: Path) -> list[Path]:
    files: list[Path] = []
    for path in root.rglob("*"):
        if _path_has_reparse_point(path):
            raise ValueError(f"snapshot contains a symlink or reparse alias: {path}")
        if path.is_file():
            files.append(path.relative_to(root))
    return sorted(files, key=lambda path: path.as_posix())


def _cleanup_failed_snapshot(
    temporary: Path,
    destination_input: Path,
    destination_path: Path,
) -> None:
    cleanup_error: Exception | None = None
    try:
        shutil.rmtree(temporary)
    except Exception as exc:
        cleanup_error = exc

    destination_error: Exception | None = None
    try:
        if os.path.lexists(destination_input) or os.path.lexists(destination_path):
            destination_error = ValueError("destination must remain absent after snapshot failure")
    except Exception as exc:
        destination_error = exc

    if cleanup_error is not None:
        if destination_error is not None:
            raise cleanup_error from destination_error
        raise cleanup_error
    if destination_error is not None:
        raise destination_error


def snapshot_family(
    source: Path | str,
    destination: Path | str,
    *,
    live_root: Path | str | None = None,
) -> dict[str, Any]:
    if live_root is None:
        raise ValueError("live root is required")
    source_input = Path(source).expanduser()
    destination_input = Path(destination).expanduser()
    live_input = Path(live_root).expanduser()
    for label, path in (("source", source_input), ("destination", destination_input), ("live root", live_input)):
        if _path_has_reparse_point(path):
            raise ValueError(f"{label} contains a symlink or reparse point")
    source_path = _canonical(source_input)
    destination_path = _canonical(destination_input)
    live_path = _canonical(live_input)
    if not source_path.is_dir():
        raise ValueError("source family must be a directory")
    if not live_path.is_dir():
        raise ValueError("live root must be an existing directory")
    if _is_alias(source_path, destination_path) or _same_file(source_path, destination_path):
        raise ValueError("source and destination are aliases")
    if _is_alias(source_path, live_path) or _same_file(source_path, live_path):
        raise ValueError("source family is the live store")
    if _is_alias(destination_path, live_path) or _same_file(destination_path, live_path):
        raise ValueError("destination aliases the live store")
    if os.path.lexists(destination_input):
        raise ValueError("destination already exists")
    generation = _read_current(source_path)
    _validate_source_tree(source_path, live_path)
    coordinator = source_path / "coord.db"
    source_state = _source_state(source_path)
    _check_claims(coordinator)
    if _source_state(source_path) != source_state:
        raise ValueError("source family changed during owner check")
    destination_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix=".perf-store-snapshot-", dir=destination_path.parent))
    try:
        databases, copied = _copy_family_files(source_path, temporary)
        _check_claims(coordinator)
        if _source_state(source_path) != source_state:
            raise ValueError("source family changed during snapshot")
        destination_files = _destination_files(temporary)
        if any(path.name.endswith(("-wal", "-shm")) for path in destination_files):
            raise ValueError("snapshot contains a WAL or SHM sidecar")
        quick_checks = {
            str(path.relative_to(temporary)): "ok"
            for path in databases
        }
        destination_digest = _digest_files(temporary, destination_files)
        if os.path.lexists(destination_input):
            raise ValueError("destination was created during snapshot")
        temporary.replace(destination_path)
    except Exception as error:
        try:
            _cleanup_failed_snapshot(temporary, destination_input, destination_path)
        except Exception as cleanup_error:
            raise cleanup_error from error
        raise
    return {
        "source": str(source_path),
        "destination": str(destination_path),
        "generation": generation,
        "files": [path.as_posix() for path in copied],
        "databases": quick_checks,
        "quick_check": "ok",
        "sha256": destination_digest,
        "wal_shm": False,
    }


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--destination", type=Path, required=True)
    parser.add_argument("--live-root", type=Path, required=True)
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
