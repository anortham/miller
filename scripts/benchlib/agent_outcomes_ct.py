from __future__ import annotations

import hashlib
import json
import os
import re
import signal
import subprocess
import time
from collections.abc import Callable, Mapping, Sequence
from dataclasses import asdict, dataclass
from pathlib import Path, PurePosixPath
from typing import Any, Protocol

from .agent_outcomes_contract import (
    VerifiableTask,
    VerificationExecution,
    source_inventory,
)

_CONFIG_FIELDS = {
    "schema_version",
    "enabled_arm",
    "command_timeout_seconds",
    "readiness_timeout_seconds",
    "poll_interval_seconds",
}
_ENABLED_ARM = "native+miller-lexical"
_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_CONTAINER_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")


class CtCommandExecutor(Protocol):
    def execute(
        self, argv: Sequence[str], candidate_root: Path, timeout_seconds: float
    ) -> VerificationExecution: ...


class HostCommandExecutor(Protocol):
    def execute(
        self,
        argv: Sequence[str],
        timeout_seconds: int,
        *,
        stdin_path: Path | None = None,
        stdout_path: Path | None = None,
        stderr_path: Path | None = None,
    ) -> VerificationExecution: ...


@dataclass(frozen=True)
class CtLifecycleConfig:
    schema_version: int
    enabled_arm: str
    command_timeout_seconds: int
    readiness_timeout_seconds: int
    poll_interval_seconds: float


@dataclass(frozen=True)
class CtCommandEvidence:
    action: str
    argv: tuple[str, ...]
    ran: bool
    returncode: int | None
    wall_time_seconds: float
    stdout_sha256: str
    stderr_sha256: str


@dataclass(frozen=True)
class CtLifecycleEvidence:
    success: bool
    disposition: str
    enabled_arm: str
    arm_id: str
    setup_wall_time_seconds: float
    project_count: int
    case_count: int
    status_sha256: str | None
    index_identity: str | None
    revision: int | None
    commands: tuple[CtCommandEvidence, ...]
    discovered_project_paths: tuple[str, ...] = ()
    selected_project_paths: tuple[str, ...] = ()
    project_selection_rule: str = "not_applicable"
    warmup_verdict: str | None = None
    last_run: str | None = None
    baseline_failure_test_case_ids: tuple[str, ...] = ()
    baseline_failure_report_sha256: str | None = None
    qualification_evidence_sha256: str | None = None

    @property
    def evidence_sha256(self) -> str:
        return _digest(asdict(self))


@dataclass(frozen=True)
class CtLifecycleCleanupEvidence:
    success: bool
    disposition: str
    wall_time_seconds: float
    commands: tuple[CtCommandEvidence, ...]

    @property
    def evidence_sha256(self) -> str:
        return _digest(asdict(self))


@dataclass(frozen=True)
class CtKnownChange:
    path: Path
    sha256: str
    changed_paths: tuple[str, ...]
    baseline_snapshot_sha256: str
    changed_snapshot_sha256: str
    expected_ct_test_case_ids: tuple[str, ...]
    qualification_evidence_sha256: str
    expected_baseline_ct_verdict: str
    expected_baseline_ct_failure_ids: tuple[str, ...]

    def __post_init__(self) -> None:
        if not self.path.is_absolute() or not self.path.is_file():
            raise ValueError("CT known-change path must be an absolute file")
        if (
            not _SHA256.fullmatch(self.sha256)
            or not _SHA256.fullmatch(self.baseline_snapshot_sha256)
            or not _SHA256.fullmatch(self.changed_snapshot_sha256)
            or self.baseline_snapshot_sha256 == self.changed_snapshot_sha256
        ):
            raise ValueError("CT known-change digest is invalid")
        if not _SHA256.fullmatch(self.qualification_evidence_sha256):
            raise ValueError("CT known-change qualification evidence digest is invalid")
        if hashlib.sha256(self.path.read_bytes()).hexdigest() != self.sha256:
            raise ValueError("CT known-change bytes do not match the frozen digest")
        if not self.changed_paths or len(set(self.changed_paths)) != len(
            self.changed_paths
        ):
            raise ValueError("CT known-change paths must be unique and non-empty")
        if (
            not self.expected_ct_test_case_ids
            or len(set(self.expected_ct_test_case_ids))
            != len(self.expected_ct_test_case_ids)
            or any(
                not isinstance(value, str) or not value
                for value in self.expected_ct_test_case_ids
            )
        ):
            raise ValueError("CT known-change expected case IDs are invalid")
        if self.expected_baseline_ct_verdict not in {"green", "red", "partial"}:
            raise ValueError("CT known-change baseline verdict is invalid")
        if (
            len(set(self.expected_baseline_ct_failure_ids))
            != len(self.expected_baseline_ct_failure_ids)
            or any(
                not isinstance(value, str) or not value
                for value in self.expected_baseline_ct_failure_ids
            )
            or (
                self.expected_baseline_ct_verdict == "green"
                and self.expected_baseline_ct_failure_ids
            )
            or (
                self.expected_baseline_ct_verdict in {"red", "partial"}
                and not self.expected_baseline_ct_failure_ids
            )
        ):
            raise ValueError(
                f"CT known-change {self.expected_baseline_ct_verdict} baseline failures are invalid"
            )
        for value in self.changed_paths:
            path = PurePosixPath(value)
            if path.is_absolute() or ".." in path.parts or str(path) in {"", "."}:
                raise ValueError("CT known-change path is unsafe")
        if tuple(sorted(_patch_paths(self.path.read_text(encoding="utf-8")))) != tuple(
            sorted(self.changed_paths)
        ):
            raise ValueError("CT known-change declared paths do not match the patch")

    @classmethod
    def from_manifest(cls, value: Mapping[str, object]) -> CtKnownChange:
        required = {
            "path",
            "sha256",
            "changed_paths",
            "baseline_snapshot_sha256",
            "changed_snapshot_sha256",
            "expected_ct_test_case_ids",
            "qualification_evidence_sha256",
            "expected_baseline_ct_verdict",
            "expected_baseline_ct_failure_ids",
        }
        if not isinstance(value, Mapping) or set(value) != required:
            raise ValueError("CT known-change manifest fields are invalid")
        paths = value["changed_paths"]
        expected = value["expected_ct_test_case_ids"]
        baseline_expected = value["expected_baseline_ct_failure_ids"]
        if not isinstance(paths, list) or not all(
            isinstance(item, str) for item in paths
        ):
            raise ValueError("CT known-change changed_paths are invalid")
        if not isinstance(expected, list) or not all(
            isinstance(item, str) for item in expected
        ):
            raise ValueError("CT known-change expected case IDs are invalid")
        if not isinstance(baseline_expected, list) or not all(
            isinstance(item, str) for item in baseline_expected
        ):
            raise ValueError("CT known-change baseline failure IDs are invalid")
        return cls(
            Path(str(value["path"])),
            str(value["sha256"]),
            tuple(paths),
            str(value["baseline_snapshot_sha256"]),
            str(value["changed_snapshot_sha256"]),
            tuple(expected),
            str(value["qualification_evidence_sha256"]),
            str(value["expected_baseline_ct_verdict"]),
            tuple(baseline_expected),
        )


@dataclass(frozen=True)
class CtTransitionEvidence:
    success: bool
    disposition: str
    wall_time_seconds: float
    known_change_sha256: str
    changed_paths: tuple[str, ...]
    baseline_snapshot_sha256: str
    changed_snapshot_sha256: str | None
    baseline_index_identity: str | None
    baseline_revision: int | None
    changed_index_identity: str | None
    changed_revision: int | None
    commands: tuple[CtCommandEvidence, ...]
    expected_test_case_ids: tuple[str, ...] = ()
    observed_failure_test_case_ids: tuple[str, ...] = ()
    failure_report_sha256: str | None = None

    @property
    def evidence_sha256(self) -> str:
        return _digest(asdict(self))


class CtLifecycleSession:
    def __init__(
        self,
        evidence: CtLifecycleEvidence,
        cleanup: Callable[[], CtLifecycleCleanupEvidence],
        completed_cleanup: CtLifecycleCleanupEvidence | None = None,
    ) -> None:
        self.evidence = evidence
        self._cleanup = cleanup
        self._cleanup_evidence = completed_cleanup

    def cleanup(self) -> CtLifecycleCleanupEvidence:
        if self._cleanup_evidence is None:
            self._cleanup_evidence = self._cleanup()
        return self._cleanup_evidence


class CtLifecycle:
    def __init__(
        self,
        config: CtLifecycleConfig,
        miller_path: str,
        executor: CtCommandExecutor,
        clock: Callable[[], float],
        sleeper: Callable[[float], None],
    ) -> None:
        self.config = config
        self.miller_path = miller_path
        self.executor = executor
        self.clock = clock
        self.sleeper = sleeper

    @classmethod
    def from_manifest(
        cls,
        value: Mapping[str, object],
        *,
        miller_path: str,
        executor: CtCommandExecutor,
        clock: Callable[[], float] = time.monotonic,
        sleeper: Callable[[float], None] = time.sleep,
    ) -> CtLifecycle:
        if not isinstance(value, Mapping) or set(value) != _CONFIG_FIELDS:
            raise ValueError("CT lifecycle manifest fields are invalid")
        if value["schema_version"] != 1:
            raise ValueError("CT lifecycle schema_version is unsupported")
        if value["enabled_arm"] != _ENABLED_ARM:
            raise ValueError("CT lifecycle enabled_arm is invalid")
        command_timeout = _integer(
            value["command_timeout_seconds"], "command timeout", 1, 300
        )
        readiness_timeout = _integer(
            value["readiness_timeout_seconds"], "readiness timeout", 1, 600
        )
        poll_interval = value["poll_interval_seconds"]
        if (
            isinstance(poll_interval, bool)
            or not isinstance(poll_interval, (int, float))
            or not 0 < poll_interval <= 10
        ):
            raise ValueError("CT lifecycle poll interval is invalid")
        if not isinstance(miller_path, str) or not miller_path:
            raise ValueError("CT lifecycle miller_path is invalid")
        return cls(
            CtLifecycleConfig(
                1,
                _ENABLED_ARM,
                command_timeout,
                readiness_timeout,
                float(poll_interval),
            ),
            miller_path,
            executor,
            clock,
            sleeper,
        )

    def prepare(
        self,
        attempt_root: Path,
        task: VerifiableTask,
        arm_id: str,
        *,
        comparison_mode: str = "ct",
        known_change: CtKnownChange | None = None,
    ) -> CtLifecycleSession:
        root = Path(attempt_root)
        if comparison_mode not in {"primary", "secondary", "ct"}:
            raise ValueError("CT lifecycle comparison_mode is invalid")
        if comparison_mode != "ct" or arm_id == "native":
            evidence = CtLifecycleEvidence(
                True,
                "not_applicable",
                self.config.enabled_arm,
                arm_id,
                0.0,
                0,
                0,
                None,
                None,
                None,
                (),
            )
            return CtLifecycleSession(evidence, _empty_cleanup)
        if arm_id != self.config.enabled_arm:
            raise ValueError("CT lifecycle arm is invalid")
        if task.task.workflow != "test_selection":
            raise ValueError("CT lifecycle requires a test_selection task")

        started = self.clock()
        deadline = started + self.config.readiness_timeout_seconds
        commands: list[CtCommandEvidence] = []
        discovery_result = self._command("status", root, commands, deadline=deadline)
        discovery = (
            _json_document(discovery_result.stdout)
            if _succeeded(discovery_result)
            else None
        )
        discovered_projects = _discovered_projects(discovery)
        if discovered_projects is None:
            return self._failed_session(
                root,
                arm_id,
                started,
                commands,
                "project_discovery_failed",
            )
        discovered_paths = tuple(
            sorted(str(project["project_path"]) for project in discovered_projects)
        )
        selected_paths = _select_governing_projects(
            discovered_projects,
            None if known_change is None else known_change.changed_paths,
        )
        if not selected_paths:
            return self._failed_session(
                root, arm_id, started, commands, "changed_paths_have_no_project"
            )
        enabled_paths: tuple[str, ...] = ()
        for project_path in selected_paths:
            enable_argv = (
                self.miller_path,
                "tests",
                "enable",
                "--project",
                project_path,
                "--json",
            )
            enable_result = self._execute_command(
                "enable", enable_argv, root, commands, deadline=deadline
            )
            enable = (
                _json_document(enable_result.stdout)
                if _succeeded(enable_result)
                else None
            )
            if not _succeeded(enable_result):
                return self._failed_session(
                    root,
                    arm_id,
                    started,
                    commands,
                    "enable_refused"
                    if enable_result.returncode == 3
                    else "enable_failed",
                )
            projects = enable.get("projects") if enable is not None else None
            unsupported_count = (
                _nonnegative_int(enable.get("unsupported_count"))
                if enable is not None
                else None
            )
            if unsupported_count is not None and unsupported_count > 0:
                return self._failed_session(
                    root, arm_id, started, commands, "unsupported_provider"
                )
            if (
                enable is None
                or enable.get("operation") != "enable"
                or unsupported_count != 0
                or not _supported_projects(projects)
                or enable.get("enabled_count") != len(projects)
            ):
                return self._failed_session(
                    root, arm_id, started, commands, "invalid_enable_report"
                )
            enabled_paths = tuple(
                sorted(str(project["project_path"]) for project in projects)
            )
        if enabled_paths != selected_paths:
            return self._failed_session(
                root, arm_id, started, commands, "unexpected_enabled_projects"
            )

        serve_result = self._command("serve", root, commands, deadline=deadline)
        serve = (
            _json_document(serve_result.stdout) if _succeeded(serve_result) else None
        )
        if (
            not _succeeded(serve_result)
            or serve is None
            or serve.get("status") not in {"started", "alreadyrunning", "replaced"}
        ):
            return self._failed_session(
                root, arm_id, started, commands, "daemon_start_failed"
            )

        warmup_argv = (
            self.miller_path,
            "tests",
            "run",
            "--wait",
            "--json",
        )
        warmup_result = self._execute_command(
            "inventory_warmup", warmup_argv, root, commands, deadline=deadline
        )
        warmup = (
            _json_document(warmup_result.stdout) if _succeeded(warmup_result) else None
        )
        wait = warmup.get("wait") if isinstance(warmup, Mapping) else None
        if (
            warmup is None
            or warmup.get("execution") != "daemon"
            or warmup.get("verdict") not in {"green", "red", "partial"}
            or warmup.get("waited") is not True
            or warmup.get("paused") is not False
            or not isinstance(wait, Mapping)
            or wait.get("wait_complete") is not True
            or wait.get("state") != "completed"
        ):
            return self._failed_session(
                root, arm_id, started, commands, "inventory_warmup_failed"
            )

        if known_change is None:
            return self._failed_session(
                root, arm_id, started, commands, "baseline_qualification_missing"
            )
        selected = warmup.get("selected")
        baseline_identity = (
            selected.get("index_identity") if isinstance(selected, Mapping) else None
        )
        baseline_revision = (
            selected.get("revision") if isinstance(selected, Mapping) else None
        )
        if (
            not isinstance(baseline_identity, str)
            or not baseline_identity
            or not _positive_int(baseline_revision)
        ):
            return self._failed_session(
                root, arm_id, started, commands, "baseline_identity_missing"
            )
        baseline_failures = self._failure_inventory(
            root,
            baseline_identity,
            baseline_revision,
            commands,
            deadline,
            "baseline_failures",
        )
        if baseline_failures is None:
            return self._failed_session(
                root, arm_id, started, commands, "baseline_failure_report_invalid"
            )
        baseline_failure_ids, baseline_failure_sha = baseline_failures
        if any(
            case_id.startswith("ct-discovery-failure:")
            for case_id in baseline_failure_ids
        ):
            return self._failed_session(
                root,
                arm_id,
                started,
                commands,
                "baseline_provider_failure",
                warmup_verdict=str(warmup["verdict"]),
                baseline_failure_ids=baseline_failure_ids,
                baseline_failure_sha=baseline_failure_sha,
                qualification_sha=known_change.qualification_evidence_sha256,
            )
        if warmup["verdict"] != known_change.expected_baseline_ct_verdict:
            return self._failed_session(
                root,
                arm_id,
                started,
                commands,
                "baseline_verdict_mismatch",
                warmup_verdict=str(warmup["verdict"]),
                baseline_failure_ids=baseline_failure_ids,
                baseline_failure_sha=baseline_failure_sha,
                qualification_sha=known_change.qualification_evidence_sha256,
            )
        if baseline_failure_ids != tuple(
            sorted(known_change.expected_baseline_ct_failure_ids)
        ):
            return self._failed_session(
                root,
                arm_id,
                started,
                commands,
                "baseline_failures_mismatch",
                warmup_verdict=str(warmup["verdict"]),
                baseline_failure_ids=baseline_failure_ids,
                baseline_failure_sha=baseline_failure_sha,
                qualification_sha=known_change.qualification_evidence_sha256,
            )

        last_status_sha: str | None = None
        while True:
            if self.clock() >= deadline:
                return self._failed_session(
                    root,
                    arm_id,
                    started,
                    commands,
                    "inventory_timeout",
                    last_status_sha,
                )
            status_result = self._command("status", root, commands, deadline=deadline)
            last_status_sha = _text_sha256(status_result.stdout)
            if not _succeeded(status_result):
                return self._failed_session(
                    root, arm_id, started, commands, "status_failed", last_status_sha
                )
            status = _json_document(status_result.stdout)
            readiness = _readiness(status)
            if readiness[0]:
                evidence = CtLifecycleEvidence(
                    True,
                    "ready",
                    self.config.enabled_arm,
                    arm_id,
                    max(0.0, self.clock() - started),
                    readiness[2],
                    readiness[3],
                    last_status_sha,
                    readiness[4],
                    readiness[5],
                    tuple(commands),
                    discovered_paths,
                    selected_paths,
                    "nearest_governing_discovered_project",
                    str(warmup["verdict"]),
                    status.get("last_run")
                    if isinstance(status.get("last_run"), str)
                    else None,
                    baseline_failure_ids,
                    baseline_failure_sha,
                    known_change.qualification_evidence_sha256,
                )
                return CtLifecycleSession(evidence, lambda: self._cleanup(root))
            if readiness[1] is not None:
                return self._failed_session(
                    root, arm_id, started, commands, readiness[1], last_status_sha
                )
            remaining = deadline - self.clock()
            if remaining <= 0:
                return self._failed_session(
                    root,
                    arm_id,
                    started,
                    commands,
                    "inventory_timeout",
                    last_status_sha,
                )
            self.sleeper(min(self.config.poll_interval_seconds, remaining))

    def wait_for_transition(
        self,
        root: Path,
        baseline: CtLifecycleEvidence,
        known_change: CtKnownChange,
        expected_test_case_ids: Sequence[str] = (),
    ) -> CtTransitionEvidence:
        started = self.clock()
        deadline = started + self.config.readiness_timeout_seconds
        commands: list[CtCommandEvidence] = []
        refresh_argv = (
            self.miller_path,
            "workspace",
            "refresh",
            "--path",
            "/workspace",
            "--json",
        )
        refresh = self._execute_command(
            "workspace_refresh", refresh_argv, root, commands, deadline=deadline
        )
        if not _succeeded(refresh) or _json_document(refresh.stdout) is None:
            return _transition_failure(
                "index_refresh_failed",
                started,
                self.clock(),
                baseline,
                known_change,
                _ct_source_snapshot_sha256(root),
                commands,
            )
        while True:
            if self.clock() >= deadline:
                return _transition_failure(
                    "freshness_timeout",
                    started,
                    self.clock(),
                    baseline,
                    known_change,
                    _ct_source_snapshot_sha256(root),
                    commands,
                )
            status = self._command("status", root, commands, deadline=deadline)
            value = _json_document(status.stdout) if _succeeded(status) else None
            readiness = _readiness(value)
            changed_identity = (
                readiness[4] != baseline.index_identity
                or readiness[5] != baseline.revision
            )
            changed_last_run = (
                value.get("last_run") if isinstance(value, Mapping) else None
            )
            run_advanced = not expected_test_case_ids or (
                isinstance(changed_last_run, str)
                and changed_last_run
                and changed_last_run != baseline.last_run
            )
            if readiness[0] and changed_identity and run_advanced:
                observed: tuple[str, ...] = ()
                failure_sha: str | None = None
                if expected_test_case_ids:
                    failures = self._expected_failures(
                        root,
                        tuple(expected_test_case_ids),
                        str(readiness[4]),
                        int(readiness[5]),
                        commands,
                        deadline,
                    )
                    if failures is None:
                        return _transition_failure(
                            "expected_cases_missing",
                            started,
                            self.clock(),
                            baseline,
                            known_change,
                            _ct_source_snapshot_sha256(root),
                            commands,
                        )
                    observed, failure_sha = failures
                return CtTransitionEvidence(
                    True,
                    "ready",
                    max(0.0, self.clock() - started),
                    known_change.sha256,
                    known_change.changed_paths,
                    known_change.baseline_snapshot_sha256,
                    _ct_source_snapshot_sha256(root),
                    baseline.index_identity,
                    baseline.revision,
                    readiness[4],
                    readiness[5],
                    tuple(commands),
                    tuple(sorted(expected_test_case_ids)),
                    observed,
                    failure_sha,
                )
            if readiness[1] is not None:
                return _transition_failure(
                    readiness[1],
                    started,
                    self.clock(),
                    baseline,
                    known_change,
                    _ct_source_snapshot_sha256(root),
                    commands,
                )
            remaining = deadline - self.clock()
            if remaining <= 0:
                return _transition_failure(
                    "freshness_timeout",
                    started,
                    self.clock(),
                    baseline,
                    known_change,
                    _ct_source_snapshot_sha256(root),
                    commands,
                )
            self.sleeper(min(self.config.poll_interval_seconds, remaining))

    def _expected_failures(
        self,
        root: Path,
        expected: tuple[str, ...],
        index_identity: str,
        revision: int,
        commands: list[CtCommandEvidence],
        deadline: float,
    ) -> tuple[tuple[str, ...], str] | None:
        inventory = self._failure_inventory(
            root, index_identity, revision, commands, deadline, "changed_failures"
        )
        if inventory is None or not set(expected).issubset(inventory[0]):
            return None
        return inventory

    def _failure_inventory(
        self,
        root: Path,
        index_identity: str,
        revision: int,
        commands: list[CtCommandEvidence],
        deadline: float,
        action: str,
    ) -> tuple[tuple[str, ...], str] | None:
        observed: set[str] = set()
        report_hashes: list[str] = []
        offset = 0
        total: int | None = None
        while offset <= 1000:
            argv = (
                self.miller_path,
                "tests",
                "failures",
                "--limit",
                "200",
                "--offset",
                str(offset),
                "--json",
            )
            result = self._execute_command(
                action, argv, root, commands, deadline=deadline
            )
            report_hashes.append(_text_sha256(result.stdout))
            value = _json_document(result.stdout) if _succeeded(result) else None
            failures = value.get("failures") if value is not None else None
            reported_total = (
                _nonnegative_int(value.get("total")) if value is not None else None
            )
            reported_offset = (
                _nonnegative_int(value.get("offset")) if value is not None else None
            )
            truncated = (
                _nonnegative_int(value.get("truncated")) if value is not None else None
            )
            if (
                not isinstance(failures, list)
                or reported_total is None
                or reported_offset != offset
                or truncated is None
                or (total is not None and reported_total != total)
            ):
                return None
            total = reported_total
            for failure in failures:
                if (
                    not isinstance(failure, Mapping)
                    or failure.get("state") != "red"
                    or failure.get("index_identity") != index_identity
                    or failure.get("revision") != revision
                    or not isinstance(failure.get("test_case_id"), str)
                ):
                    return None
                observed.add(str(failure["test_case_id"]))
            if truncated == 0:
                return (
                    (tuple(sorted(observed)), _digest(report_hashes))
                    if len(observed) == total
                    else None
                )
            offset += len(failures)
            if not failures:
                return None
        return None

    def _failed_session(
        self,
        root: Path,
        arm_id: str,
        started: float,
        commands: list[CtCommandEvidence],
        disposition: str,
        status_sha256: str | None = None,
        *,
        warmup_verdict: str | None = None,
        baseline_failure_ids: tuple[str, ...] = (),
        baseline_failure_sha: str | None = None,
        qualification_sha: str | None = None,
    ) -> CtLifecycleSession:
        evidence = CtLifecycleEvidence(
            False,
            disposition,
            self.config.enabled_arm,
            arm_id,
            max(0.0, self.clock() - started),
            0,
            0,
            status_sha256,
            None,
            None,
            tuple(commands),
            warmup_verdict=warmup_verdict,
            baseline_failure_test_case_ids=baseline_failure_ids,
            baseline_failure_report_sha256=baseline_failure_sha,
            qualification_evidence_sha256=qualification_sha,
        )
        cleanup = self._cleanup(root)
        return CtLifecycleSession(evidence, lambda: cleanup, cleanup)

    def _command(
        self,
        action: str,
        root: Path,
        evidence: list[CtCommandEvidence],
        *,
        deadline: float | None = None,
    ) -> VerificationExecution:
        argv = (self.miller_path, "tests", action, "--json")
        return self._execute_command(action, argv, root, evidence, deadline=deadline)

    def _execute_command(
        self,
        action: str,
        argv: tuple[str, ...],
        root: Path,
        evidence: list[CtCommandEvidence],
        *,
        deadline: float | None = None,
    ) -> VerificationExecution:
        started = self.clock()
        timeout = (
            float(self.config.command_timeout_seconds)
            if deadline is None
            else min(
                float(self.config.command_timeout_seconds),
                max(0.0, deadline - started),
            )
        )
        if timeout <= 0:
            result = VerificationExecution(False, None, "", "setup deadline expired")
        else:
            try:
                result = self.executor.execute(argv, root, timeout)
            except Exception as exc:  # noqa: BLE001
                result = VerificationExecution(False, None, "", type(exc).__name__)
        if deadline is not None and self.clock() > deadline and _succeeded(result):
            result = VerificationExecution(
                True, 124, result.stdout, result.stderr + "setup deadline expired"
            )
        evidence.append(
            CtCommandEvidence(
                action,
                argv,
                result.ran,
                result.returncode,
                max(0.0, self.clock() - started),
                _text_sha256(result.stdout),
                _text_sha256(result.stderr),
            )
        )
        return result

    def _cleanup(self, root: Path) -> CtLifecycleCleanupEvidence:
        started = self.clock()
        commands: list[CtCommandEvidence] = []
        stop = self._command("stop", root, commands)
        stop_json = _json_document(stop.stdout) if _succeeded(stop) else None
        stop_ok = (
            _succeeded(stop)
            and stop_json is not None
            and stop_json.get("status")
            in {"stopped", "already_stopped", "detached", "not_adopted"}
        )
        if not stop_ok:
            self.sleeper(self.config.poll_interval_seconds)
            stop = self._command("stop", root, commands)
            stop_json = _json_document(stop.stdout) if _succeeded(stop) else None
            stop_ok = (
                _succeeded(stop)
                and stop_json is not None
                and stop_json.get("status")
                in {"stopped", "already_stopped", "detached", "not_adopted"}
            )
        disable = self._command("disable", root, commands)
        disable_json = _json_document(disable.stdout) if _succeeded(disable) else None
        disable_ok = (
            _succeeded(disable)
            and disable_json is not None
            and disable_json.get("operation") == "disable"
        )
        success = stop_ok and disable_ok
        return CtLifecycleCleanupEvidence(
            success,
            "cleaned" if success else "cleanup_failed",
            max(0.0, self.clock() - started),
            tuple(commands),
        )


@dataclass(frozen=True)
class CtContainerSpec:
    podman_path: str
    image_reference: str
    container_create_argv: tuple[str, ...]
    codex_exec_argv: tuple[str, ...]
    prompt_path: Path
    raw_events_path: Path
    stderr_path: Path
    cidfile: Path
    timeout_seconds: int
    candidate_root: Path
    arm_id: str
    known_change: CtKnownChange

    def __post_init__(self) -> None:
        digest = self.image_reference.rsplit("@sha256:", 1)
        if len(digest) != 2 or not _SHA256.fullmatch(digest[1]):
            raise ValueError("CT container image must use a sha256 digest")
        create = self.container_create_argv
        if (
            len(create) < 7
            or create[:2] != (self.podman_path, "create")
            or create.count("--init") != 1
            or create.count("--cidfile") != 1
            or create[create.index("--cidfile") + 1] != str(self.cidfile)
            or create[-3:] != (self.image_reference, "sleep", "infinity")
            or "--rm" in create
        ):
            raise ValueError(
                "CT container create command lacks runner-frozen init or identity"
            )
        workspace_mount = (
            f"type=bind,src={self.candidate_root.resolve()},dst=/workspace,rw,Z"
        )
        if workspace_mount not in create:
            raise ValueError(
                "CT container must mount only the disposable candidate writable"
            )
        if self.codex_exec_argv[:2] != ("/usr/local/bin/codex", "exec"):
            raise ValueError("CT Codex command is not runner-frozen")
        if self.arm_id not in {"native", _ENABLED_ARM}:
            raise ValueError("CT container arm is invalid")
        miller_settings = " ".join(self.codex_exec_argv)
        if self.arm_id == "native" and "mcp_servers.miller" in miller_settings:
            raise ValueError("CT native control cannot configure Miller")
        if self.arm_id == _ENABLED_ARM and (
            'mcp_servers.miller.env.MILLER_SEMANTIC="off"' not in miller_settings
            or 'mcp_servers.miller.env.MILLER_CT="off"' in miller_settings
        ):
            raise ValueError("CT treatment command has invalid Miller settings")
        paths = (
            self.prompt_path,
            self.raw_events_path,
            self.stderr_path,
            self.cidfile,
            self.candidate_root,
        )
        if any(not Path(path).is_absolute() for path in paths):
            raise ValueError("CT paths must be absolute")
        private_paths = paths[:4]
        if len(set(private_paths)) != len(private_paths):
            raise ValueError("CT private paths must be distinct")
        if not self.prompt_path.is_file() or self.prompt_path.stat().st_mode & 0o077:
            raise ValueError("CT prompt must be an existing private file")
        if not self.candidate_root.is_dir():
            raise ValueError("CT candidate root must be an existing directory")
        if (
            _ct_source_snapshot_sha256(self.candidate_root)
            != self.known_change.baseline_snapshot_sha256
        ):
            raise ValueError("CT candidate does not match the frozen baseline snapshot")
        if not 1 <= self.timeout_seconds <= 3600:
            raise ValueError("CT attempt timeout is invalid")


@dataclass(frozen=True)
class CtAttemptOutcome:
    success: bool
    disposition: str
    returncode: int | None
    wall_time_seconds: float
    setup_wall_time_seconds: float
    agent_wall_time_seconds: float | None
    lifecycle: CtLifecycleEvidence
    transition: CtTransitionEvidence
    cleanup: CtLifecycleCleanupEvidence
    container_removed: bool
    measured_snapshot_sha256: str | None

    @property
    def evidence_sha256(self) -> str:
        return _digest(asdict(self))


class PersistentCtAttemptSupervisor(CtCommandExecutor):
    def __init__(
        self,
        host: HostCommandExecutor | None = None,
        *,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self.host = host or SubprocessHostCommandExecutor()
        self.clock = clock
        self._podman_path: str | None = None
        self._container_id: str | None = None

    def execute(
        self, argv: Sequence[str], candidate_root: Path, timeout_seconds: int
    ) -> VerificationExecution:
        del candidate_root
        if self._podman_path is None or self._container_id is None:
            raise RuntimeError("CT lifecycle command has no active container")
        return self.host.execute(
            (
                self._podman_path,
                "exec",
                "--workdir",
                "/workspace",
                "--env",
                "HOME=/runtime/home",
                "--env",
                "MILLER_SEMANTIC=off",
                self._container_id,
                *argv,
            ),
            timeout_seconds,
        )

    def run(
        self,
        spec: CtContainerSpec,
        lifecycle: CtLifecycle,
        task: VerifiableTask,
        arm_id: str,
    ) -> CtAttemptOutcome:
        if lifecycle.executor is not self:
            raise ValueError("CT lifecycle must use this persistent supervisor")
        if arm_id != spec.arm_id:
            raise ValueError("CT supervisor arm differs from its frozen container spec")
        if task.task.snapshot_sha256 != spec.known_change.baseline_snapshot_sha256:
            raise ValueError("CT task snapshot does not match the frozen known change")
        if self._container_id is not None:
            raise RuntimeError("CT supervisor is already active")
        started = self.clock()
        session: CtLifecycleSession | None = None
        lifecycle_evidence = _failed_lifecycle(arm_id)
        transition = _failed_transition(spec.known_change)
        cleanup = _empty_cleanup()
        returncode: int | None = None
        disposition = "container_create_failed"
        operation_success = False
        setup_wall_time = 0.0
        agent_wall_time: float | None = None
        container_removed = False
        measured_snapshot: str | None = None
        self._podman_path = spec.podman_path
        try:
            created = self.host.execute(
                spec.container_create_argv, spec.timeout_seconds
            )
            if not _succeeded(created):
                returncode = created.returncode
            else:
                container_id = _read_container_id(spec.cidfile)
                self._container_id = container_id
                started_container = self.host.execute(
                    (spec.podman_path, "start", container_id), spec.timeout_seconds
                )
                if not _succeeded(started_container):
                    disposition = "container_start_failed"
                    returncode = started_container.returncode
                else:
                    ready_for_change = False
                    if arm_id == "native":
                        session = lifecycle.prepare(spec.candidate_root, task, arm_id)
                        lifecycle_evidence = session.evidence
                        ready_for_change = lifecycle_evidence.success
                    else:
                        opened = self.execute(
                            (
                                lifecycle.miller_path,
                                "workspace",
                                "open",
                                "--path",
                                "/workspace",
                                "--full",
                                "--json",
                            ),
                            spec.candidate_root,
                            spec.timeout_seconds,
                        )
                        if (
                            not _succeeded(opened)
                            or _json_document(opened.stdout) is None
                        ):
                            disposition = "baseline_index_failed"
                            returncode = opened.returncode
                        else:
                            session = lifecycle.prepare(
                                spec.candidate_root,
                                task,
                                arm_id,
                                known_change=spec.known_change,
                            )
                            lifecycle_evidence = session.evidence
                            if lifecycle_evidence.success:
                                ready_for_change = True
                            else:
                                disposition = "ct_" + lifecycle_evidence.disposition
                    if ready_for_change:
                        if (
                            _ct_source_snapshot_sha256(spec.candidate_root)
                            != spec.known_change.baseline_snapshot_sha256
                        ):
                            disposition = "baseline_snapshot_mismatch"
                        else:
                            patch_check = self._patch_command(
                                spec, container_id, "--check"
                            )
                            patch_apply = (
                                self._patch_command(spec, container_id)
                                if _succeeded(patch_check)
                                else VerificationExecution(False, None)
                            )
                            measured_snapshot = _ct_source_snapshot_sha256(
                                spec.candidate_root
                            )
                            if not _succeeded(patch_check) or not _succeeded(
                                patch_apply
                            ):
                                disposition = "known_change_apply_failed"
                            elif (
                                measured_snapshot
                                != spec.known_change.changed_snapshot_sha256
                            ):
                                disposition = "changed_snapshot_mismatch"
                            elif arm_id == "native":
                                transition = _native_transition(spec.known_change)
                            else:
                                transition = lifecycle.wait_for_transition(
                                    spec.candidate_root,
                                    lifecycle_evidence,
                                    spec.known_change,
                                    spec.known_change.expected_ct_test_case_ids,
                                )
                            if transition.success:
                                setup_wall_time = max(0.0, self.clock() - started)
                                agent_started = self.clock()
                                agent_result = self.host.execute(
                                    (
                                        spec.podman_path,
                                        "exec",
                                        "--workdir",
                                        "/workspace",
                                        "--env",
                                        "HOME=/runtime/home",
                                        "--env",
                                        "MILLER_SEMANTIC=off",
                                        "-i",
                                        container_id,
                                        *spec.codex_exec_argv,
                                    ),
                                    spec.timeout_seconds,
                                    stdin_path=spec.prompt_path,
                                    stdout_path=spec.raw_events_path,
                                    stderr_path=spec.stderr_path,
                                )
                                agent_wall_time = max(0.0, self.clock() - agent_started)
                                returncode = agent_result.returncode
                                operation_success = _succeeded(agent_result)
                                disposition = (
                                    "completed" if operation_success else "agent_failed"
                                )
                            elif (
                                measured_snapshot
                                == spec.known_change.changed_snapshot_sha256
                            ):
                                disposition = "ct_" + transition.disposition
        except Exception:  # noqa: BLE001
            disposition = "supervisor_failed"
            operation_success = False
            returncode = None
        finally:
            if session is not None:
                cleanup = session.cleanup()
            if self._container_id is not None:
                removed = self.host.execute(
                    (spec.podman_path, "rm", "--force", self._container_id),
                    spec.timeout_seconds,
                )
                container_removed = _succeeded(removed)
            self._container_id = None
            self._podman_path = None
        success = operation_success and cleanup.success and container_removed
        if operation_success and not cleanup.success:
            disposition = "cleanup_failed"
        elif operation_success and not container_removed:
            disposition = "container_cleanup_failed"
        return CtAttemptOutcome(
            success,
            disposition,
            returncode,
            max(0.0, self.clock() - started),
            setup_wall_time,
            agent_wall_time,
            lifecycle_evidence,
            transition,
            cleanup,
            container_removed,
            measured_snapshot,
        )

    def _patch_command(
        self,
        spec: CtContainerSpec,
        container_id: str,
        mode: str | None = None,
    ) -> VerificationExecution:
        command = [
            spec.podman_path,
            "exec",
            "--workdir",
            "/workspace",
            "--env",
            "HOME=/runtime/home",
            "--env",
            "MILLER_SEMANTIC=off",
            "-i",
            container_id,
            "git",
            "apply",
            "--unidiff-zero",
        ]
        if mode is not None:
            command.append(mode)
        command.append("-")
        return self.host.execute(
            tuple(command),
            spec.timeout_seconds,
            stdin_path=spec.known_change.path,
        )


class SubprocessHostCommandExecutor:
    def execute(
        self,
        argv: Sequence[str],
        timeout_seconds: int,
        *,
        stdin_path: Path | None = None,
        stdout_path: Path | None = None,
        stderr_path: Path | None = None,
    ) -> VerificationExecution:
        stdin_file = None
        stdout_file = None
        stderr_file = None
        try:
            stdin_file = stdin_path.open("rb") if stdin_path is not None else None
            stdout_file = (
                stdout_path.open("xb") if stdout_path is not None else subprocess.PIPE
            )
            stderr_file = (
                stderr_path.open("xb") if stderr_path is not None else subprocess.PIPE
            )
            process = subprocess.Popen(
                list(argv),
                stdin=stdin_file,
                stdout=stdout_file,
                stderr=stderr_file,
                start_new_session=True,
                env={"PATH": os.defpath},
            )
            try:
                stdout, stderr = process.communicate(timeout=timeout_seconds)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    stdout, stderr = process.communicate(timeout=2)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    stdout, stderr = process.communicate()
                return VerificationExecution(
                    True,
                    124,
                    _decoded(stdout),
                    _decoded(stderr),
                )
            return VerificationExecution(
                True,
                process.returncode,
                _decoded(stdout),
                _decoded(stderr),
            )
        except OSError as exc:
            return VerificationExecution(False, None, "", str(exc))
        finally:
            for stream in (stdin_file, stdout_file, stderr_file):
                if stream is not None and stream not in {subprocess.PIPE}:
                    stream.close()


def _readiness(
    value: Mapping[str, Any] | None,
) -> tuple[bool, str | None, int, int, str | None, int | None]:
    if value is None or value.get("schema_version") != 1:
        return False, "invalid_status_report", 0, 0, None, None
    if value.get("kill_switch") is True:
        return False, "ct_kill_switch", 0, 0, None, None
    if (
        value.get("enabled") is not True
        or value.get("projects_discovered") is not False
    ):
        return False, "ct_not_enabled", 0, 0, None, None
    projects = value.get("projects")
    if not _supported_projects(projects):
        return False, "unsupported_provider", 0, 0, None, None
    daemon = value.get("daemon")
    if not isinstance(daemon, Mapping):
        return False, "invalid_status_report", 0, 0, None, None
    if daemon.get("version_mismatch") is True or daemon.get("loop_stalled") is True:
        return False, "unsafe_daemon_state", 0, 0, None, None
    selected = value.get("selected")
    index_identity = (
        selected.get("index_identity") if isinstance(selected, Mapping) else None
    )
    revision = selected.get("revision") if isinstance(selected, Mapping) else None
    if (
        not isinstance(index_identity, str)
        or not index_identity
        or not _positive_int(revision)
    ):
        index_identity = None
        revision = None
    cases = [project.get("case_count") for project in projects]
    if any(not _positive_int(count) for count in cases):
        return False, None, len(projects), 0, index_identity, revision
    ready = (
        daemon.get("state") == "running"
        and daemon.get("running") is True
        and daemon.get("paused") is False
        and daemon.get("auto_runs_paused") is False
        and daemon.get("activity") == "idle"
        and index_identity is not None
        and value.get("budget_holder") is None
    )
    return ready, None, len(projects), sum(cases), index_identity, revision


def _supported_projects(value: Any) -> bool:
    return (
        isinstance(value, list)
        and bool(value)
        and all(
            isinstance(project, Mapping)
            and isinstance(project.get("project_path"), str)
            and bool(project.get("project_path"))
            and isinstance(project.get("framework"), str)
            and bool(project.get("framework"))
            and project.get("unsupported_reason") in {None, ""}
            for project in value
        )
    )


def _discovered_projects(
    value: Mapping[str, Any] | None,
) -> tuple[Mapping[str, Any], ...] | None:
    if (
        value is None
        or value.get("schema_version") != 1
        or value.get("enabled") is not False
        or value.get("kill_switch") is not False
        or value.get("projects_discovered") is not True
        or not _supported_projects(value.get("projects"))
    ):
        return None
    return tuple(value["projects"])


def _select_governing_projects(
    projects: Sequence[Mapping[str, Any]],
    changed_paths: Sequence[str] | None,
) -> tuple[str, ...]:
    validated: list[tuple[str, PurePosixPath]] = []
    for project in projects:
        value = str(project["project_path"])
        path = PurePosixPath(value)
        if not path.is_absolute() or path.parts[:2] != ("/", "workspace"):
            return ()
        validated.append((value, path.parent))
    if changed_paths is None:
        return (validated[0][0],) if len(validated) == 1 else ()
    selected: set[str] = set()
    for changed_path in changed_paths:
        source = PurePosixPath("/workspace") / PurePosixPath(changed_path)
        governing = [
            item
            for item in validated
            if item[1] == source.parent or item[1] in source.parents
        ]
        if not governing:
            return ()
        depth = max(len(item[1].parts) for item in governing)
        nearest = [item for item in governing if len(item[1].parts) == depth]
        if len(nearest) != 1:
            return ()
        selected.add(nearest[0][0])
    return tuple(sorted(selected))


def _json_document(value: str) -> Mapping[str, Any] | None:
    try:
        parsed = json.loads(value, object_pairs_hook=_unique_object)
    except (json.JSONDecodeError, ValueError, TypeError):
        return None
    return parsed if isinstance(parsed, Mapping) else None


def _unique_object(pairs: Sequence[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def _integer(value: object, name: str, minimum: int, maximum: int) -> int:
    if (
        isinstance(value, bool)
        or not isinstance(value, int)
        or not minimum <= value <= maximum
    ):
        raise ValueError(f"CT lifecycle {name} is invalid")
    return value


def _nonnegative_int(value: object) -> int | None:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        return None
    return value


def _positive_int(value: object) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


def _succeeded(result: VerificationExecution) -> bool:
    return result.ran and result.returncode == 0


def _digest(value: object) -> str:
    encoded = json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _ct_source_snapshot_sha256(root: Path) -> str:
    inventory = [
        dict(entry)
        for entry in source_inventory(root)
        if PurePosixPath(str(entry["path"])).parts[:1] != (".miller",)
    ]
    return _digest(inventory)


def _text_sha256(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def _empty_cleanup() -> CtLifecycleCleanupEvidence:
    return CtLifecycleCleanupEvidence(True, "not_applicable", 0.0, ())


def _failed_lifecycle(arm_id: str) -> CtLifecycleEvidence:
    return CtLifecycleEvidence(
        False, "not_started", _ENABLED_ARM, arm_id, 0.0, 0, 0, None, None, None, ()
    )


def _failed_transition(known_change: CtKnownChange) -> CtTransitionEvidence:
    return CtTransitionEvidence(
        False,
        "not_started",
        0.0,
        known_change.sha256,
        known_change.changed_paths,
        known_change.baseline_snapshot_sha256,
        None,
        None,
        None,
        None,
        None,
        (),
    )


def _native_transition(known_change: CtKnownChange) -> CtTransitionEvidence:
    return CtTransitionEvidence(
        True,
        "native_control_changed_source",
        0.0,
        known_change.sha256,
        known_change.changed_paths,
        known_change.baseline_snapshot_sha256,
        known_change.changed_snapshot_sha256,
        None,
        None,
        None,
        None,
        (),
    )


def _transition_failure(
    disposition: str,
    started: float,
    finished: float,
    baseline: CtLifecycleEvidence,
    known_change: CtKnownChange,
    changed_snapshot_sha256: str,
    commands: Sequence[CtCommandEvidence],
) -> CtTransitionEvidence:
    return CtTransitionEvidence(
        False,
        disposition,
        max(0.0, finished - started),
        known_change.sha256,
        known_change.changed_paths,
        known_change.baseline_snapshot_sha256,
        changed_snapshot_sha256,
        baseline.index_identity,
        baseline.revision,
        None,
        None,
        tuple(commands),
    )


def _patch_paths(value: str) -> tuple[str, ...]:
    old_path: str | None = None
    result: set[str] = set()
    for line in value.splitlines():
        if line.startswith("--- "):
            old_path = line[4:].split("\t", 1)[0]
        elif line.startswith("+++ "):
            new_path = line[4:].split("\t", 1)[0]
            selected = old_path if new_path == "/dev/null" else new_path
            if selected is None or selected == "/dev/null":
                continue
            if selected.startswith(("a/", "b/")):
                selected = selected[2:]
            result.add(selected)
    if not result:
        raise ValueError("CT known-change patch has no changed paths")
    return tuple(sorted(result))


def _read_container_id(path: Path) -> str:
    try:
        value = path.read_text(encoding="utf-8").strip()
    except OSError as exc:
        raise RuntimeError("CT container id was not published") from exc
    if not _CONTAINER_ID.fullmatch(value):
        raise RuntimeError("CT container id is invalid")
    return value


def _decoded(value: bytes | None) -> str:
    return (
        "" if value is None else value[: 1024 * 1024].decode("utf-8", errors="replace")
    )
