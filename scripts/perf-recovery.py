#!/usr/bin/env python3
"""Replay bounded Miller performance workloads without touching the live store."""

from __future__ import annotations

import argparse
import ctypes
import dataclasses
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import queue
import re
import shutil
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
MAX_CAPTURE_BYTES = 1_048_576
MAX_MCP_LINE_BYTES = 64 * 1024
MAX_PHASE_RECORDS = 128
PROCESS_TEARDOWN_TIMEOUT_SECONDS = 1.0
STREAM_READ_CHUNK_BYTES = 64 * 1024
MANIFEST_SCHEMA_VERSION = 1
KNOWN_WORKLOAD_IDS = (
    "startup.leader.no_change",
    "startup.reader.warm",
    "workspace.open.no_change",
    "producer.retry.identical",
    "producer.resolve.one_file",
    "producer.resolve.full",
    "tool.inspect.warm",
    "tool.context.references.depth0",
    "tool.context.references.depth1",
    "tool.context.references.depth1.semantic",
    "tool.context.references.depth1.batch_off",
    "tool.context.references.depth1.batch_on",
    "tool.impact.bounded",
    "tool.trace.warm",
)
EXECUTION_KINDS = frozenset({"miller_cli", "mcp_bootstrap", "julie_store"})
PRODUCER_STORE_COMMANDS = frozenset({"import", "resolve"})
PRODUCER_IMPORT_FLAGS = frozenset(
    {
        "--store",
        "--family",
        "--root",
        "--view",
        "--level",
        "--request-id",
        "--idempotency-key",
        "--request-timeout-seconds",
        "--json",
    }
)
PRODUCER_RESOLVE_FLAGS = frozenset(
    {
        "--store",
        "--family",
        "--view",
        "--request-id",
        "--idempotency-key",
        "--request-timeout-seconds",
        "--json",
    }
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
PLACEHOLDER_NAMES = {"workspace", "store_copy", "target", "family", "view"}
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
    producer: str | Path = "julie-extract"


@dataclass(frozen=True)
class ActiveStore:
    mode: str
    root: Path
    files: tuple[Path, ...] = ()
    pointer_path: Path | None = None
    generation: str | None = None
    view_id: str | None = None
    family_id: str | None = None
    artifact_path: Path | None = None


@dataclass(frozen=True)
class Workload:
    workload_id: str
    command: tuple[str, ...]
    warmups: int
    runs: int
    hard_budget_ms: Mapping[str, int]
    execution_kind: str = "miller_cli"
    timeout_ms: int | None = None
    parity_with: str | None = None
    semantic: bool = False
    lexical_control: bool = True
    target_discovery: Mapping[str, Any] | None = None
    hard_budget_memory_bytes: int | None = None
    context_batch: str | None = None
    mutates_store: bool = False
    isolated_snapshot: bool = False
    metadata: Mapping[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class PairComparison:
    output_digest_match: bool
    delta_wall_ms: int
    exit_code_match: bool
    timeout_match: bool
    stable_pivot_match: bool = True
    symbol_neighbour_match: bool = True
    ordering_match: bool = True
    truncation_match: bool = True
    truncation_changed: bool = False
    truncation_shape_valid: bool = True
    extra_reference_rows: int = 0
    non_symbol_rows_delta: int = 0
    added_bytes: int = 0


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
    completed: bool = True


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
        "family_id": str(family_id),
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
            family_id=details["family_id"],
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
    if normalized.out is not None:
        output_aliases = (normalized.live_store, normalized.store_copy, active.root)
        if any(_is_alias(normalized.out, candidate) or _same_file(normalized.out, candidate) for candidate in output_aliases):
            raise ValueError("output must not alias the live, active, or copied store")
    return normalized


def _platform_name() -> str:
    return sys.platform


def _budget_key(platform_name: str) -> str:
    return "windows" if platform_name.startswith("win") else "development"


def first_attempt_timeout_ms(workload: Workload, platform_name: str | None = None) -> int:
    platform_name = platform_name or _platform_name()
    published_budget = int(workload.hard_budget_ms[_budget_key(platform_name)])
    timeout = max(FIRST_ATTEMPT_TIMEOUT_MS, published_budget, int(workload.timeout_ms or 0))
    if timeout <= published_budget:
        raise ValueError(f"{workload.workload_id} observation timeout must be strictly greater than its hard budget")
    return timeout


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
    if workload.hard_budget_memory_bytes is not None:
        if platform_name.startswith("win") and hard_memory_bytes is None:
            return False
        if hard_memory_bytes is not None:
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
        kernel32 = ctypes.windll.kernel32
        kernel32.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
        kernel32.OpenProcess.restype = ctypes.c_void_p
        kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
        kernel32.CloseHandle.restype = ctypes.c_int
        handle = kernel32.OpenProcess(process_query_information | process_vm_read, False, pid)
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
            kernel32.CloseHandle(handle)
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
    reaped = False

    def wait_bounded() -> bool:
        nonlocal reaped
        try:
            process.wait(timeout=PROCESS_TEARDOWN_TIMEOUT_SECONDS)
            reaped = True
            return True
        except (subprocess.TimeoutExpired, OSError, ValueError):
            return False

    def kill_windows_tree() -> bool:
        try:
            result = subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
                timeout=PROCESS_TEARDOWN_TIMEOUT_SECONDS,
            )
        except (subprocess.TimeoutExpired, OSError, ValueError):
            return False
        return getattr(result, "returncode", 1) == 0

    if process.poll() is not None:
        wait_bounded()
        return

    try:
        if os.name == "nt":
            kill_windows_tree()
            if not wait_bounded():
                kill_windows_tree()
                if not wait_bounded():
                    try:
                        process.kill()
                    except (ProcessLookupError, OSError):
                        pass
                    wait_bounded()
        else:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            if not wait_bounded():
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                if not wait_bounded():
                    try:
                        process.kill()
                    except (ProcessLookupError, OSError):
                        pass
                    wait_bounded()
    except (ProcessLookupError, PermissionError, OSError):
        try:
            process.kill()
        except (ProcessLookupError, OSError):
            pass
        wait_bounded()
    finally:
        if not reaped:
            wait_bounded()


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
    context_batch: str | None = None,
    resolution_delta: str | None = None,
    base: Mapping[str, str] | None = None,
) -> dict[str, str]:
    environment = dict(base or os.environ)
    environment.pop("MILLER_PERF_STORE_COPY", None)
    active = resolve_active_store(request)
    if base is not None and environment.get("MILLER_HOME"):
        home = _canonical(environment["MILLER_HOME"])
        if _is_alias(home, request.live_store):
            raise ValueError("miller-home must not alias the live store")
        home.mkdir(parents=True, exist_ok=True)
    else:
        home = _default_home(request)
    environment["MILLER_HOME"] = str(home)
    environment["MILLER_PERF_LEXICAL_CONTROL"] = "0" if semantic else "1"
    environment["MILLER_PERF_SEMANTIC_SERIALIZED"] = "1" if semantic else "0"
    environment["MILLER_SEMANTIC"] = "on" if semantic else "off"
    environment["MILLER_INDEX_STORE"] = "on" if active.mode == "family" else "off"
    environment["MILLER_CONTEXT_REFERENCE_BATCH"] = context_batch or "off"
    if resolution_delta is not None:
        environment["JULIE_STORE_RESOLUTION_DELTA"] = resolution_delta
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


class _BoundedBytesCapture:
    def __init__(self, max_bytes: int) -> None:
        self._max_bytes = max_bytes
        self._data = bytearray()
        self._lock = threading.Lock()

    def append(self, value: bytes) -> None:
        if not value or self._max_bytes <= 0:
            return
        if len(value) >= self._max_bytes:
            value = value[-self._max_bytes :]
        with self._lock:
            self._data.extend(value)
            if len(self._data) > self._max_bytes:
                del self._data[: len(self._data) - self._max_bytes]

    def value(self) -> bytes:
        with self._lock:
            return bytes(self._data)


def _read_process_stream(stream: Any, capture: _BoundedBytesCapture) -> None:
    if stream is None:
        return
    try:
        while True:
            chunk = stream.read(STREAM_READ_CHUNK_BYTES)
            if not chunk:
                return
            if isinstance(chunk, str):
                chunk = chunk.encode("utf-8", errors="replace")
            capture.append(bytes(chunk))
    except (OSError, ValueError):
        return


def _close_stream(stream: Any) -> None:
    if stream is None:
        return
    try:
        stream.close()
    except (OSError, ValueError):
        return


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
    stdout_capture = _BoundedBytesCapture(MAX_CAPTURE_BYTES)
    stderr_capture = _BoundedBytesCapture(MAX_CAPTURE_BYTES)
    output_threads = [
        threading.Thread(
            target=_read_process_stream,
            args=(process.stdout, stdout_capture),
            name="perf-recovery-stdout",
            daemon=True,
        ),
        threading.Thread(
            target=_read_process_stream,
            args=(process.stderr, stderr_capture),
            name="perf-recovery-stderr",
            daemon=True,
        ),
    ]
    for thread in output_threads:
        thread.start()
    timed_out = False
    try:
        process.wait(timeout=timeout_ms / 1000)
    except subprocess.TimeoutExpired:
        timed_out = True
        _terminate_process(process)
    finally:
        for thread in output_threads:
            thread.join(timeout=PROCESS_TEARDOWN_TIMEOUT_SECONDS)
        _close_stream(process.stdout)
        _close_stream(process.stderr)
        for thread in output_threads:
            thread.join(timeout=PROCESS_TEARDOWN_TIMEOUT_SECONDS)
        stop.set()
        monitor.join(timeout=PROCESS_TEARDOWN_TIMEOUT_SECONDS)
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
        output_sha256=hashlib.sha256(stdout_capture.value()).hexdigest(),
        stderr_sha256=hashlib.sha256(stderr_capture.value()).hexdigest(),
        peak_rss_bytes=metrics["peak_rss_bytes"],
        peak_pss_bytes=metrics["peak_pss_bytes"],
        private_usage_bytes=metrics["private_usage_bytes"],
        hard_memory_bytes=metrics["hard_memory_bytes"],
        hard_memory_metric=metrics["hard_memory_metric"],
        io=io,
        stdout=stdout_capture.value(),
        stderr=stderr_capture.value(),
        completed=not timed_out,
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


def _resolution_gate(payload: Any, workload: Workload) -> Mapping[str, Any] | None:
    if workload.execution_kind != "julie_store" or workload.metadata.get("resolution_scope") not in {"one_file", "full"}:
        return None
    resolution = _find_mapping(payload, {"resolution"}) or {}
    mode = resolution.get("resolution_mode")
    scope_file_count = resolution.get("scope_file_count")
    expected_mode = "scoped" if workload.metadata.get("resolution_scope") == "one_file" else "full"
    passed = mode == expected_mode and (
        expected_mode != "scoped" or isinstance(scope_file_count, (int, float)) and scope_file_count > 0
    )
    return {
        "expected_mode": expected_mode,
        "actual_mode": mode,
        "scope_file_count": scope_file_count,
        "passed": passed,
    }


def _context_facts(payload: Any, output_bytes: int) -> Mapping[str, Any] | None:
    if not isinstance(payload, Mapping):
        return None

    bundle_value = _find_value(payload, {"bundle"})
    if not isinstance(bundle_value, list):
        return {"available": False, "bytes": output_bytes}

    order: list[str] = []
    pivot_ids: list[str] = []
    symbol_neighbour_ids: list[str] = []
    symbol_order: list[str] = []
    body_truncated: list[str] = []
    item_types: list[str] = []
    identifier_rows = 0
    for index, item in enumerate(bundle_value):
        if not isinstance(item, Mapping):
            continue
        item_type = str(_find_value(item, {"item_type", "itemtype"}) or "symbol").casefold()
        item_types.append(item_type)
        if item_type == "identifier":
            identifier_rows += 1
        identifier = _find_value(item, {"symbol_id", "symbolid", "source_id", "sourceid", "chunk_id", "chunkid"})
        if identifier is None:
            name = _find_value(item, {"name"})
            file_name = _find_value(item, {"file"})
            line = _find_value(item, {"line"})
            identifier = f"{name}|{file_name}|{line}|{index}"
        identifier = str(identifier)
        order.append(identifier)
        role = str(_find_value(item, {"role"}) or "").casefold()
        if role == "pivot":
            pivot_ids.append(identifier)
        if item_type == "symbol":
            symbol_order.append(identifier)
            if role in {"neighbour", "neighbor"}:
                symbol_neighbour_ids.append(identifier)
        if _find_value(item, {"body_truncated", "bodytruncated"}) is True:
            body_truncated.append(identifier)

    disposition = _find_mapping(payload, {"disposition"}) or {}
    truncation = {
        "status": disposition.get("status"),
        "reason": disposition.get("reason"),
        "body_truncated": body_truncated,
    }
    return {
        "available": True,
        "pivot_ids": pivot_ids,
        "symbol_pivot_ids": pivot_ids,
        "symbol_neighbour_ids": symbol_neighbour_ids,
        "symbol_order": symbol_order,
        "order": order,
        "truncation": _jsonable(truncation),
        "identifier_rows": identifier_rows,
        "non_symbol_rows": max(0, len(item_types) - sum(item_type == "symbol" for item_type in item_types)),
        "item_types": item_types,
        "bytes": output_bytes,
    }


def _status_probe_state(payload: Any) -> str:
    if not isinstance(payload, Mapping) or "error" in payload:
        return "failed"
    result = payload.get("result")
    if not isinstance(result, Mapping) or result.get("isError") is True:
        return "failed"
    bootstrap = _find_value(result, {"bootstrap"})
    if isinstance(bootstrap, Mapping):
        bootstrap = _find_value(bootstrap, {"state", "status", "phase"})
    if isinstance(bootstrap, str):
        state = bootstrap.casefold()
        if state in {"running", "idle"}:
            return "running"
        if state in {"failed", "error", "unavailable"}:
            return "failed"
    content = _find_value(result, {"content"})
    if isinstance(content, list):
        content_text = " ".join(
            item.get("text", "")
            for item in content
            if isinstance(item, Mapping) and isinstance(item.get("text"), str)
        )
    else:
        content_text = ""
    match = re.search(
        r"\bbootstrap\s*:\s*(running|idle|failed|unavailable)\b",
        content_text,
        flags=re.IGNORECASE,
    )
    if match:
        state = match.group(1).casefold()
        if state in {"running", "idle"}:
            return "running"
        return "failed"
    return "ready"


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
        "MILLER_INDEX_STORE",
        "MILLER_CONTEXT_REFERENCE_BATCH",
        "JULIE_STORE_RESOLUTION_DELTA",
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
    resolution_gate = _resolution_gate(payload, workload)
    metadata = dict(workload.metadata)
    if resolution_gate is not None:
        metadata["resolution_gate"] = dict(resolution_gate)
        metadata["resolution_report"] = dict(_find_mapping(payload, {"resolution"}) or {})
    context_facts = _context_facts(payload, len(result.stdout))
    if context_facts is not None:
        metadata["context_facts"] = dict(context_facts)
    gate_passed = hard_gate_passed(
        workload,
        wall_ms=result.wall_ms,
        exit_code=result.exit_code,
        timed_out=result.timed_out,
        hard_memory_bytes=result.hard_memory_bytes,
        platform_name=platform_name,
    ) and result.completed
    if resolution_gate is not None:
        gate_passed = gate_passed and bool(resolution_gate["passed"])
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
        hard_gate_passed=gate_passed,
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
        metadata=metadata,
    )


def compare_pair(depth0: ReplayRecord | Mapping[str, Any], depth1: ReplayRecord | Mapping[str, Any]) -> PairComparison:
    def value(record: ReplayRecord | Mapping[str, Any], key: str) -> Any:
        if isinstance(record, ReplayRecord):
            return getattr(record, key)
        if key in record:
            return record[key]
        return record.get("output_digest") if key == "output_sha256" else None

    def metadata(record: ReplayRecord | Mapping[str, Any]) -> Mapping[str, Any]:
        if isinstance(record, ReplayRecord):
            return record.metadata
        value = record.get("metadata")
        return value if isinstance(value, Mapping) else {}

    left_facts = metadata(depth0).get("context_facts")
    right_facts = metadata(depth1).get("context_facts")
    left_facts = left_facts if isinstance(left_facts, Mapping) else {}
    right_facts = right_facts if isinstance(right_facts, Mapping) else {}
    left_available = bool(left_facts) and left_facts.get("available", True) is not False
    right_available = bool(right_facts) and right_facts.get("available", True) is not False
    left_pivots = left_facts.get("symbol_pivot_ids", left_facts.get("pivot_ids", []))
    right_pivots = right_facts.get("symbol_pivot_ids", right_facts.get("pivot_ids", []))
    left_neighbours = left_facts.get("symbol_neighbour_ids", left_facts.get("order", []))
    right_neighbours = right_facts.get("symbol_neighbour_ids", right_facts.get("order", []))
    left_bytes = int(left_facts.get("bytes", 0) or 0)
    right_bytes = int(right_facts.get("bytes", 0) or 0)
    left_truncation = left_facts.get("truncation")
    right_truncation = right_facts.get("truncation")
    def valid_truncation_shape(facts: Mapping[str, Any]) -> bool:
        if not facts:
            return False
        truncation = facts.get("truncation")
        if isinstance(truncation, Mapping):
            return isinstance(truncation.get("body_truncated"), list)
        return isinstance(truncation, bool) or truncation is None

    truncation_shape_valid = valid_truncation_shape(left_facts) and valid_truncation_shape(right_facts)
    left_identifier_rows = int(left_facts.get("identifier_rows", 0) or 0)
    right_identifier_rows = int(right_facts.get("identifier_rows", 0) or 0)
    left_non_symbol_rows = int(left_facts.get("non_symbol_rows", 0) or 0)
    right_non_symbol_rows = int(right_facts.get("non_symbol_rows", 0) or 0)
    return PairComparison(
        output_digest_match=value(depth0, "output_sha256") == value(depth1, "output_sha256"),
        delta_wall_ms=int(value(depth1, "wall_ms")) - int(value(depth0, "wall_ms")),
        exit_code_match=value(depth0, "exit_code") == value(depth1, "exit_code"),
        timeout_match=value(depth0, "timed_out") == value(depth1, "timed_out"),
        stable_pivot_match=left_available and right_available and left_pivots == right_pivots,
        symbol_neighbour_match=left_available and right_available and left_neighbours == right_neighbours,
        ordering_match=left_available and right_available and left_pivots == right_pivots and left_neighbours == right_neighbours,
        truncation_match=left_available and right_available and left_truncation == right_truncation,
        truncation_changed=left_available and right_available and left_truncation != right_truncation,
        truncation_shape_valid=left_available and right_available and truncation_shape_valid,
        extra_reference_rows=right_identifier_rows - left_identifier_rows,
        non_symbol_rows_delta=right_non_symbol_rows - left_non_symbol_rows,
        added_bytes=right_bytes - left_bytes,
    )


def _attach_depth_pair(records: list[ReplayRecord]) -> list[ReplayRecord]:
    by_attempt = {
        record.attempt: record
        for record in records
        if record.workload_id == "tool.context.references.depth0"
    }
    updated = list(records)
    for index, record in enumerate(records):
        if record.workload_id != "tool.context.references.depth1":
            continue
        left = by_attempt.get(record.attempt)
        if left is None:
            continue
        comparison = compare_pair(left, record)
        metadata = dict(record.metadata)
        left_context = left.metadata.get("context_facts", {})
        right_context = record.metadata.get("context_facts", {})
        left_context = left_context if isinstance(left_context, Mapping) else {}
        right_context = right_context if isinstance(right_context, Mapping) else {}
        metadata["depth_pair"] = {
            "stable_pivot_match": comparison.stable_pivot_match,
            "symbol_neighbour_match": comparison.symbol_neighbour_match,
            "ordering_match": comparison.ordering_match,
            "truncation_match": comparison.truncation_match,
            "truncation_changed": comparison.truncation_changed,
            "truncation_shape_valid": comparison.truncation_shape_valid,
            "extra_reference_rows": comparison.extra_reference_rows,
            "non_symbol_rows_delta": comparison.non_symbol_rows_delta,
            "truncation_before": left_context.get("truncation"),
            "truncation_after": right_context.get("truncation"),
            "added_bytes": comparison.added_bytes,
        }
        updated[index] = dataclasses.replace(
            record,
            metadata=metadata,
            hard_gate_passed=record.hard_gate_passed
            and comparison.stable_pivot_match
            and comparison.symbol_neighbour_match
            and comparison.truncation_shape_valid,
        )
    return updated


def _replace_placeholders(token: str, request: ReplayRequest, target: str | None) -> str:
    values = {
        "workspace": str(request.workspace),
        "store_copy": str(request.store_copy),
        "target": target or "",
    }
    if any("{" + name + "}" in token for name in ("family", "view")):
        active = resolve_active_store(request)
        values["family"] = active.family_id or ""
        values["view"] = active.view_id or ""
    for name, value in values.items():
        token = token.replace("{" + name + "}", value)
    if "{" in token or "}" in token:
        raise ValueError(f"unresolved workload command placeholder: {token}")
    return token


def _miller_argv(request: ReplayRequest, command: Sequence[str], target: str | None = None) -> list[str]:
    miller = [str(request.miller)] if isinstance(request.miller, (str, Path)) else [str(item) for item in request.miller]
    return miller + [_replace_placeholders(str(token), request, target) for token in command]


def _mcp_argv(request: ReplayRequest) -> list[str]:
    miller = [str(request.miller)] if isinstance(request.miller, (str, Path)) else [str(item) for item in request.miller]
    return miller + ["serve"]


def _producer_argv(request: ReplayRequest, command: Sequence[str], target: str | None = None) -> list[str]:
    validated = _validate_command(list(command), label="producer command", execution_kind="julie_store")
    producer = [str(request.producer)] if isinstance(request.producer, (str, Path)) else [str(item) for item in request.producer]
    return producer + [_replace_placeholders(str(token), request, target) for token in validated]


def _target_from_payload(payload: Any) -> str | None:
    value = _find_value(payload, {"symbol_id", "symbolid", "target_symbol_id", "targetsymbolid"})
    return str(value) if value is not None else None


def _validate_command(
    command: Any,
    *,
    label: str,
    execution_kind: str = "miller_cli",
) -> tuple[str, ...]:
    if not isinstance(command, list) or not command or not all(isinstance(item, str) and item for item in command):
        raise ValueError(f"{label} command must be a non-empty string array")
    if execution_kind == "mcp_bootstrap":
        if tuple(command) != ("serve",):
            raise ValueError(f"{label} mcp_bootstrap command must be exactly ['serve']")
        return tuple(command)
    if execution_kind == "julie_store":
        if len(command) < 2 or command[0] != "store" or command[1] not in PRODUCER_STORE_COMMANDS:
            raise ValueError(f"{label} julie_store command must be store import or store resolve")
        flags = {item.split("=", 1)[0] for item in command if item.startswith("--")}
        allowed_flags = PRODUCER_IMPORT_FLAGS if command[1] == "import" else PRODUCER_RESOLVE_FLAGS
        unknown_flags = flags - allowed_flags
        if unknown_flags:
            raise ValueError(f"{label} uses unknown producer flag(s): {', '.join(sorted(unknown_flags))}")
        required = {"--store", "--view", "--json", "--request-id", "--idempotency-key", "--request-timeout-seconds"}
        if not required.issubset(flags):
            raise ValueError(f"{label} julie_store command must include store/view/request identity/timeout/json")
        def flag_values(flag: str) -> list[str]:
            values: list[str] = []
            index = 0
            while index < len(command):
                token = command[index]
                if token == flag:
                    if index + 1 >= len(command) or command[index + 1].startswith("--"):
                        raise ValueError(f"{label} {flag} requires a value")
                    values.append(command[index + 1])
                    index += 2
                    continue
                prefix = flag + "="
                if token.startswith(prefix):
                    values.append(token[len(prefix):])
                index += 1
            return values

        def required_placeholder(flag: str, placeholder: str) -> None:
            values = flag_values(flag)
            if values != [f"{{{placeholder}}}"]:
                raise ValueError(f"{label} {flag} must be bound to {{{placeholder}}} runtime placeholder")

        required_placeholder("--store", "store_copy")
        required_placeholder("--view", "view")
        if command[1] == "import":
            if not {"--root", "--family", "--level"}.issubset(flags):
                raise ValueError(f"{label} store import must include root, family, and level")
            required_placeholder("--family", "family")
            required_placeholder("--root", "workspace")
        else:
            if "--family" in flags:
                required_placeholder("--family", "family")
            if "--root" in flags:
                raise ValueError(f"{label} store resolve does not accept --root")
        return tuple(command)
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
    execution_kind = item.get("execution_kind", "miller_cli")
    if not isinstance(execution_kind, str) or execution_kind not in EXECUTION_KINDS:
        raise ValueError(f"{workload_id} execution_kind must be one of {sorted(EXECUTION_KINDS)}")
    if workload_id.startswith("startup.") and execution_kind != "mcp_bootstrap":
        raise ValueError(f"{workload_id} must use execution_kind=mcp_bootstrap")
    if workload_id.startswith("producer.") and execution_kind != "julie_store":
        raise ValueError(f"{workload_id} must use execution_kind=julie_store")
    command = _validate_command(item.get("command"), label=workload_id, execution_kind=execution_kind)
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
    max_budget_ms = max(int(value) for value in budgets.values())
    if timeout_ms is not None and timeout_ms <= max_budget_ms:
        raise ValueError(f"{workload_id} timeout_ms must be strictly greater than its hard budget")
    if timeout_ms is None and max(FIRST_ATTEMPT_TIMEOUT_MS, max_budget_ms) <= max_budget_ms:
        raise ValueError(f"{workload_id} observation timeout must be strictly greater than its hard budget")
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
    context_batch = item.get("context_batch")
    if context_batch is not None and context_batch not in {"off", "on"}:
        raise ValueError(f"{workload_id} context_batch must be off or on")
    mutates_store = item.get("mutates_store", False)
    isolated_snapshot = item.get("isolated_snapshot", False)
    if not isinstance(mutates_store, bool) or not isinstance(isolated_snapshot, bool):
        raise ValueError(f"{workload_id} mutates_store and isolated_snapshot must be boolean")
    if mutates_store and not isolated_snapshot:
        raise ValueError(f"{workload_id} mutates_store requires isolated_snapshot=true")
    return Workload(
        workload_id=workload_id,
        command=command,
        warmups=warmups,
        runs=runs,
        hard_budget_ms={str(key): int(value) for key, value in budgets.items()},
        execution_kind=execution_kind,
        timeout_ms=timeout_ms,
        parity_with=parity_with,
        semantic=bool(item.get("semantic", False)),
        lexical_control=bool(item.get("lexical_control", not bool(item.get("semantic", False)))),
        target_discovery=target_discovery,
        hard_budget_memory_bytes=memory_budget,
        context_batch=context_batch,
        mutates_store=mutates_store,
        isolated_snapshot=isolated_snapshot,
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


def _phase_value(record: Mapping[str, Any], name: str) -> Any:
    wanted = name.casefold()
    for key, value in record.items():
        if str(key).casefold() == wanted:
            return value
    return None


def _phase_records(
    workspace: Path,
    phase: str,
    offsets: dict[Path, int] | None = None,
    *,
    pid: int | None = None,
) -> list[dict[str, Any]]:
    logs = workspace / ".miller" / "logs"
    if not logs.is_dir():
        return []
    matches: list[dict[str, Any]] = []
    for path in sorted(logs.glob("miller-*.jsonl")):
        try:
            with path.open("r", encoding="utf-8", errors="replace") as handle:
                if offsets is not None:
                    handle.seek(offsets.get(path, 0))
                lines = handle
                for line in lines:
                    try:
                        value = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if not isinstance(value, Mapping) or _phase_value(value, "Phase") != phase:
                        continue
                    if pid is not None:
                        try:
                            record_pid = int(_phase_value(value, "pid"))
                        except (TypeError, ValueError):
                            continue
                        if record_pid != pid:
                            continue
                    matches.append(dict(value))
                    if len(matches) > MAX_PHASE_RECORDS:
                        del matches[: len(matches) - MAX_PHASE_RECORDS]
                if offsets is not None:
                    offsets[path] = handle.tell()
        except OSError:
            continue
    return matches


def _bounded_text(line: str, *, max_bytes: int = MAX_CAPTURE_BYTES) -> str:
    if max_bytes <= 0:
        return ""
    encoded = line.encode("utf-8", errors="replace")
    if len(encoded) > max_bytes:
        return encoded[-max_bytes:].decode("utf-8", errors="replace")
    return line


def _append_bounded(captured: list[str], line: str, *, max_bytes: int = MAX_CAPTURE_BYTES) -> None:
    captured.append(_bounded_text(line, max_bytes=max_bytes))
    total = sum(len(item.encode("utf-8", errors="replace")) for item in captured)
    while captured and total > max_bytes:
        removed = captured.pop(0)
        total -= len(removed.encode("utf-8", errors="replace"))


class _McpSession:
    def __init__(self, request: ReplayRequest, environment: Mapping[str, str]) -> None:
        self.request = request
        self.environment = dict(environment)
        logs = request.workspace / ".miller" / "logs"
        self._log_offsets: dict[Path, int] = {
            path: path.stat().st_size
            for path in logs.glob("miller-*.jsonl")
            if path.is_file()
        } if logs.is_dir() else {}
        self.process = subprocess.Popen(
            _mcp_argv(request),
            cwd=str(request.workspace),
            env=self.environment,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=False,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
            start_new_session=os.name != "nt",
        )
        self._stdout: queue.Queue[str] = queue.Queue(maxsize=256)
        self._stdout_capture: list[str] = []
        self._stderr: list[str] = []
        self._next_id = 1
        self._closed = False
        self._stop = threading.Event()
        self._peaks: dict[str, int] = {}
        self._stdout_thread = threading.Thread(
            target=self._read_lines,
            args=(self.process.stdout, self._stdout, self._stdout_capture),
            name="perf-recovery-mcp-stdout",
            daemon=True,
        )
        self._stderr_thread = threading.Thread(
            target=self._read_lines,
            args=(self.process.stderr, None, self._stderr),
            name="perf-recovery-mcp-stderr",
            daemon=True,
        )
        self._stdout_thread.start()
        self._stderr_thread.start()
        self._monitor = threading.Thread(
            target=_monitor_process,
            args=(self.process, _platform_name(), self._stop, self._peaks),
            name="perf-recovery-mcp-memory",
            daemon=True,
        )
        self._monitor.start()

    @staticmethod
    def _read_lines(
        stream: Any,
        output: queue.Queue[str] | None,
        captured: list[str] | None,
    ) -> None:
        if stream is None:
            return
        for line in iter(lambda: stream.readline(MAX_MCP_LINE_BYTES), ""):
            bounded_line = _bounded_text(line)
            if output is not None:
                while True:
                    try:
                        output.put_nowait(bounded_line)
                        break
                    except queue.Full:
                        try:
                            output.get_nowait()
                        except queue.Empty:
                            break
            if captured is not None:
                _append_bounded(captured, bounded_line)

    def request_json(self, method: str, params: Mapping[str, Any], deadline: float) -> Mapping[str, Any]:
        if self.process.stdin is None:
            raise RuntimeError("MCP stdin is unavailable")
        if time.monotonic() >= deadline:
            raise TimeoutError(f"timed out before MCP request {method}")
        request_id = self._next_id
        self._next_id += 1
        message = {"jsonrpc": "2.0", "id": request_id, "method": method, "params": dict(params)}
        self.process.stdin.write(json.dumps(message, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        while time.monotonic() < deadline:
            try:
                line = self._stdout.get(timeout=min(0.1, max(0.01, deadline - time.monotonic())))
            except queue.Empty:
                if self.process.poll() is not None:
                    raise RuntimeError("MCP host exited before its response")
                continue
            try:
                value = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(value, Mapping) and value.get("id") == request_id:
                return value
        raise TimeoutError(f"timed out waiting for MCP response {method}")

    def notify(self, method: str, params: Mapping[str, Any], deadline: float) -> None:
        if self.process.stdin is None:
            raise RuntimeError("MCP stdin is unavailable")
        if time.monotonic() >= deadline:
            raise TimeoutError(f"timed out before MCP notification {method}")
        self.process.stdin.write(
            json.dumps({"jsonrpc": "2.0", "method": method, "params": dict(params)}, separators=(",", ":")) + "\n"
        )
        self.process.stdin.flush()

    def workspace_status(self, deadline: float) -> Mapping[str, Any]:
        return self.request_json(
            "tools/call",
            {
                "name": "workspace",
                "arguments": {"operation": "status", "path": str(self.request.workspace)},
            },
            deadline,
        )

    def wait_for_phase(self, phase: str, deadline: float) -> Mapping[str, Any] | None:
        while time.monotonic() < deadline:
            records = _phase_records(self.request.workspace, phase, self._log_offsets, pid=self.process.pid)
            if records:
                completed = [
                    record
                    for record in records
                    if str(_phase_value(record, "Outcome")).casefold() == "completed"
                ]
                if completed:
                    return completed[-1]
                if self.process.poll() is not None:
                    return records[-1]
            if self.process.poll() is not None:
                return None
            time.sleep(0.05)
        return None

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        self._stop.set()
        if self.process.stdin is not None:
            try:
                self.process.stdin.close()
            except (OSError, ValueError):
                pass
        try:
            self.process.wait(timeout=PROCESS_TEARDOWN_TIMEOUT_SECONDS)
        except subprocess.TimeoutExpired:
            _terminate_process(self.process)
        except (OSError, ValueError):
            _terminate_process(self.process)
        _close_stream(getattr(self.process, "stdout", None))
        _close_stream(getattr(self.process, "stderr", None))
        self._monitor.join(timeout=PROCESS_TEARDOWN_TIMEOUT_SECONDS)
        self._stdout_thread.join(timeout=PROCESS_TEARDOWN_TIMEOUT_SECONDS)
        self._stderr_thread.join(timeout=PROCESS_TEARDOWN_TIMEOUT_SECONDS)

    def result(
        self,
        started: float,
        evidence: Mapping[str, Any],
        timed_out: bool,
        *,
        close: bool = True,
    ) -> CommandResult:
        if close:
            self.close()
        metrics = normalise_memory_metrics(_platform_name(), self._peaks)
        phase = evidence.get("phase")
        status = evidence.get("workspace_status")
        stdout = json.dumps(
            {"phases": {"startup_total": dict(phase or {})}, "workspace_status": status},
            sort_keys=True,
        ).encode()
        stderr = "".join(self._stderr).encode()
        exit_code = self.process.returncode
        completed = (
            bool(evidence.get("completed"))
            and not timed_out
            and (exit_code is None or exit_code == 0)
        )
        ended = float(evidence.get("ready_at") or evidence.get("observed_at") or time.perf_counter())
        return CommandResult(
            exit_code=0 if completed and exit_code is None else exit_code,
            timed_out=timed_out,
            wall_ms=max(0, round((ended - started) * 1000)),
            cpu_ms=None,
            output_sha256=hashlib.sha256(stdout).hexdigest(),
            stderr_sha256=hashlib.sha256(stderr).hexdigest(),
            peak_rss_bytes=metrics["peak_rss_bytes"],
            peak_pss_bytes=metrics["peak_pss_bytes"],
            private_usage_bytes=metrics["private_usage_bytes"],
            hard_memory_bytes=metrics["hard_memory_bytes"],
            hard_memory_metric=metrics["hard_memory_metric"],
            io={"read_bytes": None, "write_bytes": None, "read_syscalls": None, "write_syscalls": None},
            stdout=stdout,
            stderr=stderr,
            completed=completed,
        )


def _bootstrap_session(
    request: ReplayRequest,
    *,
    timeout_ms: int,
    environment: Mapping[str, str],
    require_phase: bool = False,
) -> tuple[_McpSession, float, Mapping[str, Any], bool]:
    started = time.perf_counter()
    deadline = time.monotonic() + timeout_ms / 1000
    session = _McpSession(request, environment)
    phase: Mapping[str, Any] | None = None
    status: Mapping[str, Any] | None = None
    ready_at: float | None = None
    timed_out = False
    completed = False
    try:
        session.request_json(
            "initialize",
            {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "miller-perf-recovery", "version": "1"},
            },
            deadline,
        )
        session.notify("notifications/initialized", {}, deadline)
        while True:
            if session.process.poll() is not None:
                raise RuntimeError("MCP host exited before workspace readiness")
            status = session.workspace_status(deadline)
            status_state = _status_probe_state(status)
            if status_state == "ready":
                break
            if status_state == "failed":
                raise RuntimeError("MCP workspace status probe failed")
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("timed out waiting for MCP workspace readiness")
            time.sleep(min(0.05, remaining))
        if require_phase:
            phase = session.wait_for_phase("startup_total", deadline)
            completed = (
                phase is not None
                and str(_phase_value(phase, "Outcome")).casefold() == "completed"
            )
        else:
            phase_records = _phase_records(request.workspace, "startup_total", pid=session.process.pid)
            phase = phase_records[-1] if phase_records else None
            completed = phase is None or str(_phase_value(phase, "Outcome")).casefold() == "completed"
        ready_at = time.perf_counter()
    except (OSError, RuntimeError, TimeoutError):
        timed_out = time.monotonic() >= deadline
    observed_at = time.perf_counter()
    evidence: dict[str, Any] = {
        "phase": phase,
        "workspace_status": status,
        "ready_at": ready_at,
        "observed_at": observed_at,
        "completed": completed,
    }
    return session, started, evidence, timed_out


def _run_mcp_bootstrap(
    request: ReplayRequest,
    workload: Workload,
    *,
    timeout_ms: int,
    environment: Mapping[str, str],
) -> CommandResult:
    session, started, evidence, timed_out = _bootstrap_session(
        request,
        timeout_ms=timeout_ms,
        environment=environment,
        require_phase=workload.workload_id == "startup.leader.no_change",
    )
    return session.result(started, evidence, timed_out)


def _run_mcp_workload(
    request: ReplayRequest,
    workload: Workload,
    *,
    keep_alive: bool,
) -> tuple[list[ReplayRecord], _McpSession | None]:
    effective_timeout = first_attempt_timeout_ms(workload)
    environment = build_environment(
        request,
        semantic=workload.semantic and not workload.lexical_control,
        context_batch=workload.context_batch,
    )
    commit = _commit_for_workspace(request.workspace)
    records: list[ReplayRecord] = []
    retained: _McpSession | None = None
    total_attempts = workload.warmups + workload.runs
    for index in range(total_attempts):
        session, started, evidence, timed_out = _bootstrap_session(
            request,
            timeout_ms=effective_timeout,
            environment=environment,
            require_phase=workload.workload_id == "startup.leader.no_change",
        )
        retain = (
            keep_alive
            and index == total_attempts - 1
            and not timed_out
            and bool(evidence.get("completed"))
            and session.process.poll() is None
        )
        result = session.result(started, evidence, timed_out, close=not retain)
        if retain:
            retained = session
        records.append(
            _record_from_result(
                request,
                workload,
                result,
                attempt=index + 1,
                warmup=index < workload.warmups,
                timeout_ms=effective_timeout,
                environment=environment,
                commit=commit,
            )
        )
    return records, retained


def _snapshot_helper_module() -> Any:
    script = Path(__file__).with_name("perf-store-snapshot.py")
    spec = importlib.util.spec_from_file_location("perf_store_snapshot_runtime", script)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load snapshot helper: {script}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _isolated_request(request: ReplayRequest) -> tuple[ReplayRequest, tempfile.TemporaryDirectory[str]]:
    active = resolve_active_store(request)
    temporary = tempfile.TemporaryDirectory(prefix="miller-perf-workload-")
    root = Path(temporary.name)
    isolated_workspace = root / "workspace"
    shutil.copytree(
        request.workspace,
        isolated_workspace,
        ignore=shutil.ignore_patterns(".miller", ".git"),
    )
    isolated_miller = isolated_workspace / ".miller"
    isolated_miller.mkdir(parents=True, exist_ok=True)
    isolated_store = root / "store-family"
    if active.mode == "family":
        _snapshot_helper_module().snapshot_family(active.root, isolated_store, live_root=request.live_store)
        pointer_path = request.workspace / ".miller" / "store.json"
        pointer = json.loads(pointer_path.read_text(encoding="utf-8"))
        pointer["store_root"] = str(isolated_store)
        pointer["workspace_root"] = str(isolated_workspace)
        (isolated_miller / "store.json").write_text(json.dumps(pointer), encoding="utf-8")
        isolated_copy = isolated_store
    else:
        isolated_copy = isolated_miller / "symbols.db"
        shutil.copy2(active.artifact_path or active.root, isolated_copy)
    isolated_request = dataclasses.replace(
        request,
        workspace=isolated_workspace,
        store_copy=isolated_copy,
        miller_home=root / "miller-home",
    )
    return validate_request(isolated_request), temporary


def _one_file_path(request: ReplayRequest, workload: Workload) -> Path:
    requested = workload.metadata.get("changed_path")
    if isinstance(requested, str) and requested.strip():
        candidate = (request.workspace / requested).resolve(strict=False)
        if candidate.is_file() and _is_contained(candidate, request.workspace):
            return candidate
    candidates = sorted(
        path
        for path in request.workspace.rglob("*")
        if path.is_file() and ".miller" not in path.parts and ".git" not in path.parts
    )
    if not candidates:
        raise RuntimeError(f"{workload.workload_id} has no staged file to change")
    return candidates[0]


def _prepare_resolve_resolution(
    request: ReplayRequest,
    workload: Workload,
    environment: Mapping[str, str],
    timeout_ms: int,
) -> None:
    changed = _one_file_path(request, workload)
    changed.write_bytes(changed.read_bytes() + b"\n")
    active = resolve_active_store(request)
    import_command = (
        "store",
        "import",
        "--store",
        "{store_copy}",
        "--family",
        "{family}",
        "--root",
        "{workspace}",
        "--view",
        "{view}",
        "--level",
        "full",
        "--request-id",
        f"perf-recovery-{workload.metadata.get('resolution_scope', 'resolve')}-setup",
        "--idempotency-key",
        f"perf-recovery-{workload.metadata.get('resolution_scope', 'resolve')}-setup-key",
        "--request-timeout-seconds",
        "30",
        "--json",
    )
    if active.mode != "family":
        raise RuntimeError("producer resolution requires a family store")
    setup_environment = dict(environment)
    setup_environment.pop("JULIE_STORE_RESOLUTION_DELTA", None)
    result = _run_process(request, _producer_argv(request, import_command), timeout_ms, setup_environment)
    if result.timed_out or result.exit_code != 0 or not result.completed:
        raise RuntimeError(f"{workload.workload_id} staged full import setup failed")


def run_workload(
    request: ReplayRequest,
    workload: Workload,
    *,
    command: Sequence[str] | None = None,
    target: str | None = None,
) -> list[ReplayRecord]:
    request = validate_request(request)
    if workload.mutates_store:
        if not workload.isolated_snapshot:
            raise ValueError(f"{workload.workload_id} requires isolated_snapshot=true")
        isolated_request, temporary = _isolated_request(request)
        try:
            return run_workload(
                isolated_request,
                dataclasses.replace(workload, mutates_store=False, isolated_snapshot=False),
                command=command,
                target=target,
            )
        finally:
            temporary.cleanup()
    target = target or _discovery_target(request, workload)
    effective_timeout = first_attempt_timeout_ms(workload)
    actual_command = tuple(command) if command is not None else workload.command
    semantic = workload.semantic and not workload.lexical_control
    resolution_delta = "off" if workload.metadata.get("resolution_scope") == "full" else None
    environment = build_environment(
        request,
        semantic=semantic,
        context_batch=workload.context_batch,
        resolution_delta=resolution_delta,
    )
    if workload.execution_kind == "julie_store":
        _validate_command(list(actual_command), label=f"{workload.workload_id} command", execution_kind="julie_store")
    if workload.execution_kind == "julie_store" and workload.metadata.get("resolution_scope") in {"one_file", "full"}:
        _prepare_resolve_resolution(request, workload, environment, effective_timeout)
    process_command = actual_command if command is not None else _miller_argv(request, actual_command, target)
    commit = _commit_for_workspace(request.workspace)
    if workload.workload_id == "workspace.open.no_change":
        setup = run_command(
            request,
            process_command,
            timeout_ms=effective_timeout,
            env=environment,
            semantic=semantic,
        )
        if setup.timed_out or setup.exit_code != 0 or not setup.completed:
            raise RuntimeError("workspace.open.no_change setup failed")
    records: list[ReplayRecord] = []
    for index in range(workload.warmups + workload.runs):
        warmup = index < workload.warmups
        if workload.execution_kind == "mcp_bootstrap":
            result = _run_mcp_bootstrap(
                request,
                workload,
                timeout_ms=effective_timeout,
                environment=environment,
            )
        elif workload.execution_kind == "julie_store":
            producer_command = _producer_argv(request, actual_command, target)
            lock = SEMANTIC_WORKLOAD_LOCK if semantic else _NullLock()
            with lock:
                result = _run_process(request, list(producer_command), effective_timeout, environment)
        else:
            result = run_command(
                request,
                process_command,
                timeout_ms=effective_timeout,
                env=environment,
                semantic=semantic,
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
    leader_session: _McpSession | None = None
    try:
        for workload_id, workload in workloads.items():
            if workload_id != workload.workload_id:
                raise ValueError(f"manifest key does not match workload id: {workload_id}")
            if (
                workload_id == "startup.leader.no_change"
                and workload.execution_kind == "mcp_bootstrap"
                and "startup.reader.warm" in workloads
            ):
                leader_records, leader_session = _run_mcp_workload(request, workload, keep_alive=True)
                records.extend(leader_records)
            elif workload_id == "startup.reader.warm" and leader_session is not None:
                reader_records, _ = _run_mcp_workload(request, workload, keep_alive=False)
                records.extend(reader_records)
                leader_session.close()
                leader_session = None
            else:
                records.extend(run_workload(request, workload))
    finally:
        if leader_session is not None:
            leader_session.close()
    return _attach_parity(_attach_depth_pair(records), workloads)


def write_jsonl(path: Path | str, records: Iterable[ReplayRecord]) -> None:
    output_path = Path(path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            newline="\n",
            dir=output_path.parent,
            prefix=f".{output_path.name}.",
            suffix=".tmp",
            delete=False,
        ) as handle:
            temporary_path = Path(handle.name)
            for record in records:
                handle.write(json.dumps(record.to_dict(), sort_keys=True, separators=(",", ":")))
                handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary_path, output_path)
        temporary_path = None
    finally:
        if temporary_path is not None:
            try:
                temporary_path.unlink()
            except FileNotFoundError:
                pass


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
