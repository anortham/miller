#!/usr/bin/env python3
"""Replay bounded Miller performance workloads without touching the live store."""

from __future__ import annotations

import argparse
import ctypes
import dataclasses
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass, field
from typing import Any

try:
    import resource
except ImportError:  # pragma: no cover - Windows does not expose resource.
    resource = None


FIRST_ATTEMPT_TIMEOUT_MS = 60_000
MANIFEST_SCHEMA_VERSION = 1
KNOWN_WORKLOAD_IDS = (
    "startup.reader.warm",
    "startup.leader.no_change",
    "workspace.open.no_change",
    "producer.retry.identical",
    "producer.resolve.one_file",
    "producer.resolve.full",
    "tool.inspect.warm",
    "tool.context.references.depth0",
    "tool.context.references.depth1",
    "tool.impact.bounded",
    "tool.trace.warm",
)
KNOWN_VERBS = {
    "capabilities",
    "context",
    "content",
    "dashboard",
    "impact",
    "inspect",
    "metrics",
    "patterns",
    "refresh",
    "search",
    "symbols",
    "telemetry",
    "todos",
    "trace",
    "version",
    "workspace",
}
KNOWN_FLAGS = {
    "--base",
    "--changed-path",
    "--changed-paths",
    "--depth",
    "--entry-symbol",
    "--exclude-tests",
    "--full",
    "--json",
    "--kind",
    "--limit",
    "--max-depth",
    "--max-hops",
    "--mode",
    "--no-definition",
    "--path",
    "--path-kind",
    "--reference-depth",
    "--reference-kind",
    "--reference-mode",
    "--scope",
    "--staged",
    "--token-budget",
    "--to",
    "--wait",
    "--workspace",
    "--workspace-id",
}
PLACEHOLDER_NAMES = {"workspace", "store_copy", "target"}
SEMANTIC_WORKLOAD_LOCK = threading.Lock()


@dataclass(frozen=True)
class ReplayRequest:
    store_copy: Path
    live_store: Path
    workspace: Path = Path(".")
    miller: str | Path = "miller"
    miller_home: Path | None = None
    out: Path | None = None
    store_mode: str | None = None


@dataclass(frozen=True)
class ActiveStore:
    mode: str
    root: Path
    files: tuple[Path, ...] = ()
    pointer_path: Path | None = None
    generation: str | None = None
    view_id: str | None = None
    artifact_path: Path | None = None


@dataclass(frozen=True)
class Workload:
    workload_id: str
    command: tuple[str, ...]
    warmups: int
    runs: int
    hard_budget_ms: Mapping[str, int]
    timeout_ms: int | None = None
    parity_with: str | None = None
    semantic: bool = False
    lexical_control: bool = True
    target_discovery: Mapping[str, Any] | None = None
    hard_budget_memory_bytes: int | None = None
    metadata: Mapping[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class PairComparison:
    output_digest_match: bool
    delta_wall_ms: int
    exit_code_match: bool
    timeout_match: bool


@dataclass(frozen=True)
class CommandResult:
    exit_code: int | None
    timed_out: bool
    wall_ms: int
    cpu_ms: int | None
    output_sha256: str
    stderr_sha256: str
    peak_rss_bytes: int | None
    peak_pss_bytes: int | None
    private_usage_bytes: int | None
    hard_memory_bytes: int | None
    hard_memory_metric: str | None
    io: Mapping[str, int | None]
    stdout: bytes = field(repr=False, compare=False)
    stderr: bytes = field(repr=False, compare=False)


@dataclass(frozen=True)
class ReplayRecord:
    workload_id: str
    platform: str
    commit: str | None
    producer_version: str | None
    wall_ms: int
    cpu_ms: int | None
    peak_rss_bytes: int | None
    peak_pss_bytes: int | None
    output_sha256: str
    exit_code: int | None
    timed_out: bool
    hard_gate_passed: bool
    attempt: int = 1
    warmup: bool = False
    timeout_ms: int | None = None
    workspace: str | None = None
    store_copy: str | None = None
    view: str | None = None
    generation: int | str | None = None
    io: Mapping[str, int | None] = field(default_factory=dict)
    environment: Mapping[str, str | None] = field(default_factory=dict)
    warm_state: str | None = None
    producer_timings: Mapping[str, Any] = field(default_factory=dict)
    phase_timings: Mapping[str, Any] = field(default_factory=dict)
    broker: Mapping[str, Any] | None = None
    parity: Mapping[str, Any] | None = None
    private_usage_bytes: int | None = None
    hard_memory_bytes: int | None = None
    hard_memory_metric: str | None = None
    stderr_sha256: str | None = None
    metadata: Mapping[str, Any] = field(default_factory=dict)

    @property
    def output_digest(self) -> str:
        return self.output_sha256

    def to_dict(self) -> dict[str, Any]:
        return _jsonable(dataclasses.asdict(self))


def _jsonable(value: Any) -> Any:
    if isinstance(value, Mapping):
        return {str(key): _jsonable(item) for key, item in value.items()}
    if isinstance(value, tuple):
        return [_jsonable(item) for item in value]
    if isinstance(value, list):
        return [_jsonable(item) for item in value]
    return value


def _canonical(path: Path | str) -> Path:
    return Path(path).expanduser().resolve(strict=False)


def _is_alias(left: Path, right: Path) -> bool:
    try:
        return left == right or left.is_relative_to(right) or right.is_relative_to(left)
    except AttributeError:  # pragma: no cover - Python 3.8 compatibility fallback.
        left_text = str(left)
        right_text = str(right)
        return left_text == right_text or left_text.startswith(right_text + os.sep) or right_text.startswith(left_text + os.sep)


def _is_contained(path: Path, parent: Path) -> bool:
    try:
        return path == parent or path.is_relative_to(parent)
    except AttributeError:  # pragma: no cover - Python 3.8 compatibility fallback.
        path_text = str(path)
        parent_text = str(parent)
        return path_text == parent_text or path_text.startswith(parent_text + os.sep)


def _same_file(left: Path, right: Path) -> bool:
    try:
        return left.exists() and right.exists() and os.path.samefile(left, right)
    except OSError:
        return False


def _family_store_paths(store_root: Path) -> tuple[Path, ...]:
    root = _canonical(store_root)
    current = _canonical(root / "CURRENT")
    if not _is_contained(current, root) or not current.is_file():
        return ()
    try:
        generation = current.read_text(encoding="utf-8").strip()
    except (OSError, UnicodeError):
        return ()
    if not re.fullmatch(r"gen-[0-9]{3,}", generation):
        return ()
    generation_root = _canonical(root / generation)
    database = _canonical(generation_root / "store.db")
    coordinator = _canonical(root / "coord.db")
    if (
        not _is_contained(generation_root, root)
        or not _is_contained(database, generation_root)
        or not _is_contained(coordinator, root)
        or not database.is_file()
        or not coordinator.is_file()
    ):
        return ()
    return current, database, coordinator


def _read_family_pointer(workspace: Path) -> tuple[Path, dict[str, Any]] | None:
    pointer_path = workspace / ".miller" / "store.json"
    if not pointer_path.exists():
        return None
    try:
        pointer = json.loads(pointer_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"active family-store pointer is malformed: {pointer_path}") from exc
    if not isinstance(pointer, Mapping):
        raise ValueError("active family-store pointer must be an object")
    if pointer.get("schema_version") != 1:
        raise ValueError("active family-store pointer schema_version must be 1")
    try:
        family_id = uuid.UUID(str(pointer.get("family_id", "")))
    except (ValueError, AttributeError):
        raise ValueError("active family-store pointer family_id is invalid") from None
    if family_id.int == 0:
        raise ValueError("active family-store pointer family_id is empty")
    store_root_value = pointer.get("store_root")
    workspace_root_value = pointer.get("workspace_root")
    view_id = pointer.get("view_id")
    if not isinstance(store_root_value, str) or not Path(store_root_value).is_absolute():
        raise ValueError("active family-store pointer store_root must be absolute")
    if not isinstance(workspace_root_value, str) or _canonical(workspace_root_value) != workspace:
        raise ValueError("active family-store pointer workspace_root does not match the workspace")
    if not isinstance(view_id, str) or not view_id.strip():
        raise ValueError("active family-store pointer view_id is empty")
    store_root = _canonical(store_root_value)
    if not store_root.is_dir():
        raise ValueError("active family-store pointer store_root does not exist")
    current_path = _canonical(store_root / "CURRENT")
    if not _is_contained(current_path, store_root) or not current_path.is_file():
        raise ValueError("active family-store pointer store is missing CURRENT")
    generation = current_path.read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"gen-[0-9]{3,}", generation):
        raise ValueError("active family-store pointer CURRENT is malformed")
    generation_root = _canonical(store_root / generation)
    if not _is_contained(generation_root, store_root) or not generation_root.is_dir():
        raise ValueError("active family-store pointer generation is outside the store root")
    database = _canonical(generation_root / "store.db")
    coordinator = _canonical(store_root / "coord.db")
    if (
        not _is_contained(database, generation_root)
        or not _is_contained(coordinator, store_root)
        or not database.is_file()
        or not coordinator.is_file()
    ):
        raise ValueError("active family-store pointer does not resolve to a complete copied store")
    return pointer_path, {
        "store_root": store_root,
        "generation": generation,
        "view_id": view_id,
        "artifact_path": database,
        "files": (current_path, database, coordinator),
    }


def _store_mode(request: ReplayRequest) -> str | None:
    value = request.store_mode
    if value is None:
        value = os.environ.get("MILLER_INDEX_STORE")
    if value is None or not value.strip():
        return None
    normalized = value.strip().lower()
    if normalized in {"0", "false", "off", "disabled"}:
        return "legacy"
    if normalized in {"1", "true", "on", "enabled"}:
        return "family"
    raise ValueError("MILLER_INDEX_STORE must be on/off, true/false, enabled/disabled, or 1/0")


def resolve_active_store(request: ReplayRequest) -> ActiveStore:
    workspace = _canonical(request.workspace)
    pointer = _read_family_pointer(workspace)
    mode = _store_mode(request)
    if pointer is not None:
        pointer_path, details = pointer
        if mode == "legacy":
            raise ValueError("legacy artifact mode cannot use a workspace with an active family-store pointer")
        return ActiveStore(
            mode="family",
            root=details["store_root"],
            files=details["files"],
            artifact_path=details["artifact_path"],
            pointer_path=pointer_path,
            generation=details["generation"],
            view_id=details["view_id"],
        )
    if mode != "legacy":
        raise ValueError("workspace has no active family-store pointer")
    artifact = _canonical(workspace / ".miller" / "symbols.db")
    if not artifact.is_file():
        raise ValueError("legacy artifact mode has no active symbols.db")
    return ActiveStore(mode="legacy", root=artifact, files=(artifact,), artifact_path=artifact)


def _store_copy_matches_active(active: ActiveStore, store_copy: Path) -> bool:
    canonical_copy = _canonical(store_copy)
    if canonical_copy == active.root:
        return True
    return active.artifact_path is not None and _same_file(canonical_copy, active.artifact_path)


def validate_request(request: ReplayRequest) -> ReplayRequest:
    live_store = _canonical(request.live_store)
    store_copy = _canonical(request.store_copy)
    if _is_alias(live_store, store_copy) or _same_file(live_store, store_copy):
        raise ValueError(
            "store-copy must not be the live store (identical, parent/child, or canonical-path alias)"
        )
    if not live_store.exists():
        raise ValueError(f"live store does not exist: {live_store}")
    if not store_copy.exists():
        raise ValueError(f"store copy does not exist: {store_copy}")
    workspace = _canonical(request.workspace)
    if not workspace.exists() or not workspace.is_dir():
        raise ValueError(f"workspace does not exist or is not a directory: {workspace}")
    if request.miller_home is not None:
        home = _canonical(request.miller_home)
        if _is_alias(home, live_store):
            raise ValueError("miller-home must not alias the live store")
    normalized = dataclasses.replace(
        request,
        live_store=live_store,
        store_copy=store_copy,
        workspace=workspace,
        miller_home=_canonical(request.miller_home) if request.miller_home is not None else None,
        out=_canonical(request.out) if request.out is not None else None,
    )
    active = resolve_active_store(normalized)
    if _is_alias(active.root, normalized.live_store) or _same_file(active.root, normalized.live_store):
        raise ValueError("workspace active store resolves to the live store")
    live_paths = (normalized.live_store,)
    if normalized.live_store.is_dir():
        live_paths += _family_store_paths(normalized.live_store)
    if any(_same_file(active_path, live_path) for active_path in active.files for live_path in live_paths):
        raise ValueError("workspace active store resolves to the live store")
    if not _store_copy_matches_active(active, normalized.store_copy):
        raise ValueError("store-copy does not identify the active store root or generation artifact")
    return normalized


def _platform_name() -> str:
    return sys.platform


def _budget_key(platform_name: str) -> str:
    return "windows" if platform_name.startswith("win") else "development"


def first_attempt_timeout_ms(workload: Workload, platform_name: str | None = None) -> int:
    platform_name = platform_name or _platform_name()
    published_budget = int(workload.hard_budget_ms[_budget_key(platform_name)])
    return max(FIRST_ATTEMPT_TIMEOUT_MS, published_budget, int(workload.timeout_ms or 0))


def hard_gate_passed(
    workload: Workload,
    *,
    wall_ms: int,
    exit_code: int | None,
    timed_out: bool,
    hard_memory_bytes: int | None,
    platform_name: str,
) -> bool:
    budget = int(workload.hard_budget_ms[_budget_key(platform_name)])
    if timed_out or exit_code != 0 or wall_ms > budget:
        return False
    if workload.hard_budget_memory_bytes is not None and hard_memory_bytes is not None:
        return hard_memory_bytes <= workload.hard_budget_memory_bytes
    return True


def normalise_memory_metrics(platform_name: str, raw: Mapping[str, int | None]) -> dict[str, int | None]:
    if platform_name.startswith("linux"):
        return {
            "peak_rss_bytes": raw.get("peak_rss_bytes"),
            "peak_pss_bytes": raw.get("peak_pss_bytes"),
            "private_usage_bytes": None,
            "hard_memory_bytes": raw.get("peak_pss_bytes"),
            "hard_memory_metric": "linux_pss" if raw.get("peak_pss_bytes") is not None else None,
        }
    if platform_name.startswith("win"):
        return {
            "peak_rss_bytes": raw.get("peak_rss_bytes"),
            "peak_pss_bytes": None,
            "private_usage_bytes": raw.get("private_usage_bytes"),
            "hard_memory_bytes": raw.get("private_usage_bytes"),
            "hard_memory_metric": (
                "windows_private_usage" if raw.get("private_usage_bytes") is not None else None
            ),
        }
    return {
        "peak_rss_bytes": None,
        "peak_pss_bytes": None,
        "private_usage_bytes": None,
        "hard_memory_bytes": None,
        "hard_memory_metric": None,
    }


def _read_linux_memory(pid: int) -> dict[str, int | None]:
    values: dict[str, int | None] = {
        "peak_rss_bytes": None,
        "peak_pss_bytes": None,
        "read_bytes": None,
        "write_bytes": None,
        "read_syscalls": None,
        "write_syscalls": None,
    }
    try:
        status = Path(f"/proc/{pid}/status").read_text(encoding="utf-8", errors="replace")
        match = re.search(r"^VmRSS:\s+(\d+)\s+kB$", status, re.MULTILINE)
        if match:
            values["peak_rss_bytes"] = int(match.group(1)) * 1024
        rollup = Path(f"/proc/{pid}/smaps_rollup").read_text(encoding="utf-8", errors="replace")
        match = re.search(r"^Pss:\s+(\d+)\s+kB$", rollup, re.MULTILINE)
        if match:
            values["peak_pss_bytes"] = int(match.group(1)) * 1024
        io_text = Path(f"/proc/{pid}/io").read_text(encoding="utf-8", errors="replace")
        io_keys = {
            "read_bytes": "read_bytes",
            "write_bytes": "write_bytes",
            "read_syscalls": "syscr",
            "write_syscalls": "syscw",
        }
        for output_key, source_key in io_keys.items():
            match = re.search(rf"^{re.escape(source_key)}:\s+(\d+)$", io_text, re.MULTILINE)
            if match:
                values[output_key] = int(match.group(1))
    except (FileNotFoundError, PermissionError, OSError, ValueError):
        pass
    return values


class _PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):  # pragma: no cover - Windows-only shape.
    _fields_ = [
        ("cb", ctypes.c_ulong),
        ("PageFaultCount", ctypes.c_ulong),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
        ("PrivateUsage", ctypes.c_size_t),
    ]


def _read_windows_memory(pid: int) -> dict[str, int | None]:  # pragma: no cover - Windows-only API.
    values: dict[str, int | None] = {"peak_rss_bytes": None, "private_usage_bytes": None}
    try:
        process_query_information = 0x0400
        process_vm_read = 0x0010
        handle = ctypes.windll.kernel32.OpenProcess(process_query_information | process_vm_read, False, pid)
        if not handle:
            return values
        try:
            counters = _PROCESS_MEMORY_COUNTERS_EX()
            counters.cb = ctypes.sizeof(counters)
            get_process_memory_info = ctypes.windll.psapi.GetProcessMemoryInfo
            get_process_memory_info.argtypes = [ctypes.c_void_p, ctypes.POINTER(_PROCESS_MEMORY_COUNTERS_EX), ctypes.c_ulong]
            get_process_memory_info.restype = ctypes.c_bool
            if get_process_memory_info(handle, ctypes.byref(counters), counters.cb):
                values["peak_rss_bytes"] = int(counters.PeakWorkingSetSize)
                values["private_usage_bytes"] = int(counters.PrivateUsage)
        finally:
            ctypes.windll.kernel32.CloseHandle(handle)
    except (AttributeError, OSError, TypeError, ValueError):
        pass
    return values


def _sample_memory(pid: int, platform_name: str) -> Mapping[str, int | None]:
    if platform_name.startswith("linux"):
        return _read_linux_memory(pid)
    if platform_name.startswith("win"):
        return _read_windows_memory(pid)
    return {}


def _resource_snapshot() -> Mapping[str, float | int] | None:
    if resource is None or not hasattr(resource, "RUSAGE_CHILDREN"):
        return None
    try:
        usage = resource.getrusage(resource.RUSAGE_CHILDREN)
    except (AttributeError, OSError, ValueError):
        return None
    return {
        "user": float(usage.ru_utime),
        "system": float(usage.ru_stime),
        "inblock": int(getattr(usage, "ru_inblock", 0)),
        "oublock": int(getattr(usage, "ru_oublock", 0)),
        "majflt": int(getattr(usage, "ru_majflt", 0)),
        "minflt": int(getattr(usage, "ru_minflt", 0)),
    }


def _resource_delta(before: Mapping[str, float | int] | None, after: Mapping[str, float | int] | None) -> tuple[int | None, dict[str, int | None]]:
    if before is None or after is None:
        return None, {
            "read_blocks": None,
            "write_blocks": None,
            "major_faults": None,
            "minor_faults": None,
            "read_bytes": None,
            "write_bytes": None,
            "read_syscalls": None,
            "write_syscalls": None,
        }
    cpu_ms = round((float(after["user"]) - float(before["user"]) + float(after["system"]) - float(before["system"])) * 1000)
    return max(0, cpu_ms), {
        "read_blocks": max(0, int(after["inblock"]) - int(before["inblock"])),
        "write_blocks": max(0, int(after["oublock"]) - int(before["oublock"])),
        "major_faults": max(0, int(after["majflt"]) - int(before["majflt"])),
        "minor_faults": max(0, int(after["minflt"]) - int(before["minflt"])),
        "read_bytes": None,
        "write_bytes": None,
        "read_syscalls": None,
        "write_syscalls": None,
    }


def _terminate_process(process: subprocess.Popen[bytes]) -> None:
    if process.poll() is not None:
        return
    try:
        if os.name == "nt":
            subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
            )
        else:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
    except (ProcessLookupError, PermissionError, OSError):
        try:
            process.kill()
        except (ProcessLookupError, OSError):
            pass


def _monitor_process(process: subprocess.Popen[bytes], platform_name: str, stop: threading.Event, peaks: dict[str, int]) -> None:
    while not stop.is_set():
        sample = _sample_memory(process.pid, platform_name)
        for key, value in sample.items():
            if value is not None:
                peaks[key] = max(peaks.get(key, 0), int(value))
        stop.wait(0.02)
    sample = _sample_memory(process.pid, platform_name)
    for key, value in sample.items():
        if value is not None:
            peaks[key] = max(peaks.get(key, 0), int(value))


def _default_home(request: ReplayRequest) -> Path:
    if request.miller_home is not None:
        request.miller_home.mkdir(parents=True, exist_ok=True)
        return request.miller_home
    return Path(tempfile.mkdtemp(prefix="miller-perf-recovery-"))


def build_environment(
    request: ReplayRequest,
    *,
    semantic: bool = False,
    base: Mapping[str, str] | None = None,
) -> dict[str, str]:
    environment = dict(base or os.environ)
    active = resolve_active_store(request)
    if base is not None and environment.get("MILLER_HOME"):
        home = _canonical(environment["MILLER_HOME"])
        if _is_alias(home, request.live_store):
            raise ValueError("miller-home must not alias the live store")
        home.mkdir(parents=True, exist_ok=True)
    else:
        home = _default_home(request)
    environment["MILLER_HOME"] = str(home)
    environment["MILLER_PERF_STORE_COPY"] = str(request.store_copy)
    environment["MILLER_PERF_LEXICAL_CONTROL"] = "0" if semantic else "1"
    environment["MILLER_PERF_SEMANTIC_SERIALIZED"] = "1" if semantic else "0"
    environment["MILLER_SEMANTIC"] = "on" if semantic else "off"
    environment["MILLER_INDEX_STORE"] = "on" if active.mode == "family" else "off"
    return environment


def run_command(
    request: ReplayRequest,
    command: Sequence[str],
    *,
    timeout_ms: int | None = None,
    env: Mapping[str, str] | None = None,
    semantic: bool = False,
) -> CommandResult:
    request = validate_request(request)
    argv = [str(token) for token in command]
    if not argv or any("\x00" in token for token in argv):
        raise ValueError("command must contain non-empty, NUL-free arguments")
    timeout_ms = int(timeout_ms if timeout_ms is not None else FIRST_ATTEMPT_TIMEOUT_MS)
    if timeout_ms <= 0:
        raise ValueError("timeout must be positive")
    environment = build_environment(request, semantic=semantic, base=env)
    lock = SEMANTIC_WORKLOAD_LOCK if semantic else _NullLock()
    with lock:
        return _run_process(request, argv, timeout_ms, environment)


class _NullLock:
    def __enter__(self) -> None:
        return None

    def __exit__(self, exc_type: Any, exc_value: Any, traceback: Any) -> None:
        return None


def _run_process(request: ReplayRequest, argv: list[str], timeout_ms: int, environment: Mapping[str, str]) -> CommandResult:
    platform_name = _platform_name()
    before_usage = _resource_snapshot()
    started = time.perf_counter()
    process = subprocess.Popen(
        argv,
        cwd=str(request.workspace),
        env=dict(environment),
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        shell=False,
        start_new_session=os.name != "nt",
    )
    stop = threading.Event()
    peaks: dict[str, int] = {}
    initial_sample = _sample_memory(process.pid, platform_name)
    for key, value in initial_sample.items():
        if value is not None:
            peaks[key] = int(value)
    monitor = threading.Thread(
        target=_monitor_process,
        args=(process, platform_name, stop, peaks),
        name="perf-recovery-memory",
        daemon=True,
    )
    monitor.start()
    timed_out = False
    try:
        stdout, stderr = process.communicate(timeout=timeout_ms / 1000)
    except subprocess.TimeoutExpired:
        timed_out = True
        _terminate_process(process)
        stdout, stderr = process.communicate()
    finally:
        stop.set()
        monitor.join(timeout=1)
    wall_ms = max(0, round((time.perf_counter() - started) * 1000))
    after_usage = _resource_snapshot()
    cpu_ms, io = _resource_delta(before_usage, after_usage)
    for key in ("read_bytes", "write_bytes", "read_syscalls", "write_syscalls"):
        io[key] = peaks.get(key)
    metrics = normalise_memory_metrics(platform_name, peaks)
    return CommandResult(
        exit_code=process.returncode,
        timed_out=timed_out,
        wall_ms=wall_ms,
        cpu_ms=cpu_ms,
        output_sha256=hashlib.sha256(stdout).hexdigest(),
        stderr_sha256=hashlib.sha256(stderr).hexdigest(),
        peak_rss_bytes=metrics["peak_rss_bytes"],
        peak_pss_bytes=metrics["peak_pss_bytes"],
        private_usage_bytes=metrics["private_usage_bytes"],
        hard_memory_bytes=metrics["hard_memory_bytes"],
        hard_memory_metric=metrics["hard_memory_metric"],
        io=io,
        stdout=stdout,
        stderr=stderr,
    )


def _parse_json_payload(output: bytes) -> Any:
    text = output.decode("utf-8", errors="replace").strip()
    if not text:
        return None
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        values: list[Any] = []
        for line in text.splitlines():
            try:
                values.append(json.loads(line))
            except json.JSONDecodeError:
                continue
        return values[-1] if values else None


def _find_value(value: Any, keys: set[str]) -> Any:
    if isinstance(value, Mapping):
        for key, nested in value.items():
            if str(key).casefold() in keys:
                return nested
        for nested in value.values():
            found = _find_value(nested, keys)
            if found is not None:
                return found
    elif isinstance(value, list):
        for nested in value:
            found = _find_value(nested, keys)
            if found is not None:
                return found
    return None


def _find_mapping(value: Any, keys: set[str]) -> Mapping[str, Any] | None:
    if isinstance(value, Mapping):
        for key, nested in value.items():
            if str(key).casefold() in keys and isinstance(nested, Mapping):
                return nested
        for nested in value.values():
            found = _find_mapping(nested, keys)
            if found is not None:
                return found
    elif isinstance(value, list):
        for nested in value:
            found = _find_mapping(nested, keys)
            if found is not None:
                return found
    return None


def _commit_for_workspace(workspace: Path) -> str | None:
    try:
        completed = subprocess.run(
            ["git", "-C", str(workspace), "rev-parse", "HEAD"],
            capture_output=True,
            text=True,
            check=True,
            shell=False,
        )
    except (OSError, subprocess.CalledProcessError):
        return None
    commit = completed.stdout.strip()
    return commit or None


def _producer_version(payload: Any) -> str | None:
    value = _find_value(
        payload,
        {"producer_version", "producerversion", "julie_extract_version", "extractor_version"},
    )
    return str(value) if value is not None else None


def _phase_mapping(payload: Any, keys: set[str]) -> Mapping[str, Any]:
    value = _find_mapping(payload, keys)
    return dict(value) if value is not None else {}


def _broker_mapping(payload: Any) -> Mapping[str, Any] | None:
    value = _find_mapping(payload, {"broker", "semantic_broker", "semanticbroker"})
    if value is not None:
        return dict(value)
    identity = _find_value(payload, {"broker_identity", "brokeridentity", "endpoint_identity"})
    health = _find_value(payload, {"broker_health", "brokerhealth", "health"})
    if identity is None and health is None:
        return None
    return {"identity": identity, "health": health}


def _view_and_generation(payload: Any) -> tuple[str | None, int | str | None]:
    view = _find_value(payload, {"view", "view_id", "viewid"})
    generation = _find_value(payload, {"generation", "generation_id", "revision", "revision_id"})
    return (str(view) if view is not None else None, generation)


def _selected_environment(environment: Mapping[str, str]) -> dict[str, str | None]:
    keys = (
        "MILLER_HOME",
        "MILLER_SEMANTIC",
        "MILLER_PERF_LEXICAL_CONTROL",
        "MILLER_PERF_SEMANTIC_SERIALIZED",
        "MILLER_PERF_STORE_COPY",
        "MILLER_INDEX_STORE",
    )
    return {key: environment.get(key) for key in keys}


def _record_from_result(
    request: ReplayRequest,
    workload: Workload,
    result: CommandResult,
    *,
    attempt: int,
    warmup: bool,
    timeout_ms: int,
    environment: Mapping[str, str],
    commit: str | None,
) -> ReplayRecord:
    payload = _parse_json_payload(result.stdout)
    view, generation = _view_and_generation(payload)
    active = resolve_active_store(request)
    view = view or active.view_id
    generation = generation if generation is not None else active.generation
    platform_name = _platform_name()
    return ReplayRecord(
        workload_id=workload.workload_id,
        platform=platform_name,
        commit=commit,
        producer_version=_producer_version(payload),
        wall_ms=result.wall_ms,
        cpu_ms=result.cpu_ms,
        peak_rss_bytes=result.peak_rss_bytes,
        peak_pss_bytes=result.peak_pss_bytes,
        output_sha256=result.output_sha256,
        exit_code=result.exit_code,
        timed_out=result.timed_out,
        hard_gate_passed=hard_gate_passed(
            workload,
            wall_ms=result.wall_ms,
            exit_code=result.exit_code,
            timed_out=result.timed_out,
            hard_memory_bytes=result.hard_memory_bytes,
            platform_name=platform_name,
        ),
        attempt=attempt,
        warmup=warmup,
        timeout_ms=timeout_ms,
        workspace=str(request.workspace),
        store_copy=str(request.store_copy),
        view=view,
        generation=generation,
        io=result.io,
        environment=_selected_environment(environment),
        warm_state="warmup" if warmup else "warm",
        producer_timings=_phase_mapping(payload, {"producer_timings", "producertimings", "producer"}),
        phase_timings=_phase_mapping(payload, {"phase_timings", "phasetimings", "phases", "timings"}),
        broker=_broker_mapping(payload),
        private_usage_bytes=result.private_usage_bytes,
        hard_memory_bytes=result.hard_memory_bytes,
        hard_memory_metric=result.hard_memory_metric,
        stderr_sha256=result.stderr_sha256,
        metadata=dict(workload.metadata),
    )


def compare_pair(depth0: ReplayRecord | Mapping[str, Any], depth1: ReplayRecord | Mapping[str, Any]) -> PairComparison:
    def value(record: ReplayRecord | Mapping[str, Any], key: str) -> Any:
        if isinstance(record, ReplayRecord):
            return getattr(record, key)
        if key in record:
            return record[key]
        return record.get("output_digest") if key == "output_sha256" else None

    return PairComparison(
        output_digest_match=value(depth0, "output_sha256") == value(depth1, "output_sha256"),
        delta_wall_ms=int(value(depth1, "wall_ms")) - int(value(depth0, "wall_ms")),
        exit_code_match=value(depth0, "exit_code") == value(depth1, "exit_code"),
        timeout_match=value(depth0, "timed_out") == value(depth1, "timed_out"),
    )


def _replace_placeholders(token: str, request: ReplayRequest, target: str | None) -> str:
    values = {
        "workspace": str(request.workspace),
        "store_copy": str(request.store_copy),
        "target": target or "",
    }
    for name, value in values.items():
        token = token.replace("{" + name + "}", value)
    if "{" in token or "}" in token:
        raise ValueError(f"unresolved workload command placeholder: {token}")
    return token


def _miller_argv(request: ReplayRequest, command: Sequence[str], target: str | None = None) -> list[str]:
    miller = [str(request.miller)] if isinstance(request.miller, (str, Path)) else [str(item) for item in request.miller]
    return miller + [_replace_placeholders(str(token), request, target) for token in command]


def _target_from_payload(payload: Any) -> str | None:
    value = _find_value(payload, {"symbol_id", "symbolid", "target_symbol_id", "targetsymbolid"})
    return str(value) if value is not None else None


def _validate_command(command: Any, *, label: str) -> tuple[str, ...]:
    if not isinstance(command, list) or not command or not all(isinstance(item, str) and item for item in command):
        raise ValueError(f"{label} command must be a non-empty string array")
    for item in command:
        if item.startswith("--"):
            flag = item.split("=", 1)[0]
            if flag not in KNOWN_FLAGS:
                raise ValueError(f"{label} uses unknown CLI flag {flag}")
    if command[0] not in KNOWN_VERBS:
        raise ValueError(f"{label} uses unknown Miller CLI verb {command[0]}")
    return tuple(command)


def _workload_from_mapping(item: Mapping[str, Any]) -> Workload:
    workload_id = item.get("id")
    if not isinstance(workload_id, str) or not re.fullmatch(r"[a-z0-9][a-z0-9._-]+", workload_id):
        raise ValueError(f"invalid workload id: {workload_id!r}")
    command = _validate_command(item.get("command"), label=workload_id)
    for token in command:
        for placeholder in re.findall(r"\{([^{}]+)\}", token):
            if placeholder not in PLACEHOLDER_NAMES:
                raise ValueError(f"{workload_id} uses unknown runtime placeholder {{{placeholder}}}")
    warmups = item.get("warmups", 0)
    runs = item.get("runs", 1)
    if not isinstance(warmups, int) or warmups < 0 or not isinstance(runs, int) or runs <= 0:
        raise ValueError(f"{workload_id} warmups/runs must be non-negative/positive integers")
    budgets = item.get("hard_budget_ms")
    if not isinstance(budgets, Mapping) or set(budgets) != {"development", "windows"}:
        raise ValueError(f"{workload_id} hard_budget_ms must provide development and windows")
    if any(not isinstance(value, int) or value <= 0 for value in budgets.values()):
        raise ValueError(f"{workload_id} hard_budget_ms values must be positive integers")
    timeout_ms = item.get("timeout_ms")
    if timeout_ms is not None and (not isinstance(timeout_ms, int) or timeout_ms < FIRST_ATTEMPT_TIMEOUT_MS):
        raise ValueError(f"{workload_id} timeout_ms cannot be below the 60 s first-attempt timeout")
    target_discovery = item.get("target_discovery")
    if target_discovery is not None:
        if not isinstance(target_discovery, Mapping):
            raise ValueError(f"{workload_id} target_discovery must be an object")
        discovery_command = _validate_command(target_discovery.get("command"), label=f"{workload_id}.target_discovery")
        if discovery_command[0] != "search":
            raise ValueError(f"{workload_id} target_discovery must use the Miller search contract")
        target_discovery = dict(target_discovery)
        target_discovery["command"] = discovery_command
        if target_discovery.get("field", "symbol_id") != "symbol_id":
            raise ValueError(f"{workload_id} target_discovery field must be symbol_id")
    if any("{target}" in token for token in command) and target_discovery is None:
        raise ValueError(f"{workload_id} uses {{target}} without target_discovery")
    memory_budget = item.get("hard_budget_memory_bytes")
    if memory_budget is not None and (not isinstance(memory_budget, int) or memory_budget <= 0):
        raise ValueError(f"{workload_id} hard_budget_memory_bytes must be positive")
    metadata = item.get("metadata", {})
    if not isinstance(metadata, Mapping):
        raise ValueError(f"{workload_id} metadata must be an object")
    if "shell" in item or "shell" in metadata:
        raise ValueError(f"{workload_id} cannot request shell execution")
    parity_with = item.get("parity_with")
    if parity_with is not None and not isinstance(parity_with, str):
        raise ValueError(f"{workload_id} parity_with must be a workload id")
    for name in ("semantic", "lexical_control"):
        if name in item and not isinstance(item[name], bool):
            raise ValueError(f"{workload_id} {name} must be boolean")
    return Workload(
        workload_id=workload_id,
        command=command,
        warmups=warmups,
        runs=runs,
        hard_budget_ms={str(key): int(value) for key, value in budgets.items()},
        timeout_ms=timeout_ms,
        parity_with=parity_with,
        semantic=bool(item.get("semantic", False)),
        lexical_control=bool(item.get("lexical_control", not bool(item.get("semantic", False)))),
        target_discovery=target_discovery,
        hard_budget_memory_bytes=memory_budget,
        metadata=dict(metadata),
    )


def load_manifest(path: Path | str, *, require_fixed_ids: bool = True) -> dict[str, Workload]:
    manifest_path = Path(path)
    try:
        value = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"cannot read workload manifest: {manifest_path}: {exc}") from exc
    if not isinstance(value, Mapping) or value.get("schema_version") != MANIFEST_SCHEMA_VERSION:
        raise ValueError(f"workload manifest schema_version must be {MANIFEST_SCHEMA_VERSION}")
    items = value.get("workloads")
    if not isinstance(items, list) or not items:
        raise ValueError("workload manifest workloads must be a non-empty array")
    workloads: dict[str, Workload] = {}
    for item in items:
        if not isinstance(item, Mapping):
            raise ValueError("workload manifest entries must be objects")
        workload = _workload_from_mapping(item)
        if workload.workload_id in workloads:
            raise ValueError(f"duplicate workload id: {workload.workload_id}")
        workloads[workload.workload_id] = workload
    if require_fixed_ids and set(workloads) != set(KNOWN_WORKLOAD_IDS):
        missing = sorted(set(KNOWN_WORKLOAD_IDS) - set(workloads))
        extra = sorted(set(workloads) - set(KNOWN_WORKLOAD_IDS))
        raise ValueError(f"workload ids do not match contract; missing={missing}, extra={extra}")
    for workload in workloads.values():
        if workload.parity_with is not None and workload.parity_with not in workloads:
            raise ValueError(f"{workload.workload_id} parity_with references unknown workload {workload.parity_with}")
    return workloads


def select_workloads(
    workloads: Mapping[str, Workload],
    *,
    only: str | None = None,
    runs: int | None = None,
) -> dict[str, Workload]:
    if runs is not None and (isinstance(runs, bool) or not isinstance(runs, int) or runs <= 0):
        raise ValueError("--runs must be a positive integer")
    selected_ids: set[str] | None = None
    if only is not None:
        if not only.strip():
            raise ValueError("--only must be a non-empty comma-separated workload list")
        selected_ids = set()
        for raw_id in only.split(","):
            workload_id = raw_id.strip()
            if not workload_id:
                raise ValueError("--only cannot contain empty workload IDs")
            if workload_id in selected_ids:
                raise ValueError(f"--only contains duplicate workload ID: {workload_id}")
            if workload_id not in KNOWN_WORKLOAD_IDS or workload_id not in workloads:
                raise ValueError(f"--only contains unknown workload ID: {workload_id}")
            selected_ids.add(workload_id)
    selected: dict[str, Workload] = {}
    for workload_id, workload in workloads.items():
        if selected_ids is not None and workload_id not in selected_ids:
            continue
        selected[workload_id] = dataclasses.replace(workload, runs=runs) if runs is not None else workload
    return selected


def _discovery_target(request: ReplayRequest, workload: Workload) -> str | None:
    if workload.target_discovery is None:
        return None
    command = workload.target_discovery["command"]
    result = run_command(request, _miller_argv(request, command), timeout_ms=first_attempt_timeout_ms(workload))
    if result.timed_out or result.exit_code != 0:
        raise RuntimeError(f"target discovery failed for {workload.workload_id}")
    target = _target_from_payload(_parse_json_payload(result.stdout))
    if not target:
        raise RuntimeError(f"target discovery returned no symbol_id for {workload.workload_id}")
    return target


def run_workload(
    request: ReplayRequest,
    workload: Workload,
    *,
    command: Sequence[str] | None = None,
    target: str | None = None,
) -> list[ReplayRecord]:
    request = validate_request(request)
    target = target or _discovery_target(request, workload)
    effective_timeout = first_attempt_timeout_ms(workload)
    actual_command = tuple(command) if command is not None else workload.command
    process_command = actual_command if command is not None else _miller_argv(request, actual_command, target)
    environment = build_environment(request, semantic=workload.semantic and not workload.lexical_control)
    commit = _commit_for_workspace(request.workspace)
    records: list[ReplayRecord] = []
    for index in range(workload.warmups + workload.runs):
        warmup = index < workload.warmups
        result = run_command(
            request,
            process_command,
            timeout_ms=effective_timeout,
            env=environment,
            semantic=workload.semantic and not workload.lexical_control,
        )
        records.append(
            _record_from_result(
                request,
                workload,
                result,
                attempt=index + 1,
                warmup=warmup,
                timeout_ms=effective_timeout,
                environment=environment,
                commit=commit,
            )
        )
    return records


def _attach_parity(records: list[ReplayRecord], workloads: Mapping[str, Workload]) -> list[ReplayRecord]:
    by_workload = {workload_id: [record for record in records if record.workload_id == workload_id] for workload_id in workloads}
    updated = list(records)
    positions = {(record.workload_id, record.attempt): index for index, record in enumerate(records)}
    for workload in workloads.values():
        if workload.parity_with is None:
            continue
        for left in by_workload.get(workload.parity_with, []):
            right = next((record for record in by_workload.get(workload.workload_id, []) if record.attempt == left.attempt), None)
            if right is None:
                continue
            comparison = compare_pair(left, right)
            parity = {
                "paired_workload_id": left.workload_id,
                "output_digest_match": comparison.output_digest_match,
                "delta_wall_ms": comparison.delta_wall_ms,
                "exit_code_match": comparison.exit_code_match,
                "timeout_match": comparison.timeout_match,
            }
            right_index = positions[(right.workload_id, right.attempt)]
            updated[right_index] = dataclasses.replace(
                right,
                parity=parity,
                hard_gate_passed=right.hard_gate_passed and comparison.output_digest_match and comparison.exit_code_match,
            )
    return updated


def run_replay(request: ReplayRequest, workloads: Mapping[str, Workload]) -> list[ReplayRecord]:
    request = validate_request(request)
    records: list[ReplayRecord] = []
    for workload_id, workload in workloads.items():
        if workload_id != workload.workload_id:
            raise ValueError(f"manifest key does not match workload id: {workload_id}")
        records.extend(run_workload(request, workload))
    return _attach_parity(records, workloads)


def write_jsonl(path: Path | str, records: Iterable[ReplayRecord]) -> None:
    output_path = Path(path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8", newline="\n") as handle:
        for record in records:
            handle.write(json.dumps(record.to_dict(), sort_keys=True, separators=(",", ":")))
            handle.write("\n")


def _default_manifest() -> Path:
    return Path(__file__).resolve().parent / "benchmarks" / "perf-recovery-workloads.json"


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workloads", type=Path, default=_default_manifest())
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--miller", default="miller")
    parser.add_argument("--workspace", type=Path, default=Path.cwd())
    parser.add_argument("--store-copy", type=Path, required=True)
    parser.add_argument("--live-store", type=Path, required=True)
    parser.add_argument("--only", help="comma-separated workload IDs to run")
    parser.add_argument("--runs", type=int, help="override measured attempts per selected workload")
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        workspace = _canonical(args.workspace)
        request = validate_request(
            ReplayRequest(
                store_copy=args.store_copy,
                live_store=args.live_store,
                workspace=workspace,
                miller=args.miller,
                out=args.out,
            )
        )
        workloads = select_workloads(load_manifest(args.workloads), only=args.only, runs=args.runs)
        records = run_replay(request, workloads)
        write_jsonl(args.out, records)
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as exc:
        print(f"perf-recovery: {exc}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
