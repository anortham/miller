from __future__ import annotations

import json
import hashlib
import os
import re
import secrets
import shlex
import signal
import shutil
import subprocess
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping, Protocol, Sequence
from urllib.parse import urlsplit

from .agent_outcomes_contract import (
    Campaign,
    OutcomeTask,
    VerifiableTask,
    VerificationExecutor,
    VerificationExecution,
    public_response_schema,
    source_snapshot_sha256,
    validate_run_record,
    verify_result,
)


_SUPPORTED_HOST = ("codex-cli", "0.153.4")
_ANSWER_WORKFLOWS = {"location", "concept", "references", "test_selection"}
_EVENT_TYPES = {"thread.started", "turn.started", "turn.completed", "turn.failed", "item.started", "item.updated", "item.completed", "error"}
_ID = re.compile(r"^[a-z][a-z0-9-]{0,127}$")
_SHA256 = re.compile(r"^[0-9a-f]{64}$")


@dataclass(frozen=True)
class ParsedAgentEvents:
    native_tool_counts: Mapping[str, int]
    miller_calls: int
    model_tokens: tuple[int | None, int | None, int | None]
    answer: str | None
    unsupported_reason: str | None
    raw_lines: tuple[str, ...]
    reasoning_output_tokens: int | None = None
    failed: bool = False


@dataclass(frozen=True)
class RunnerQualification:
    host_name: str
    host_version: str
    host_binary_sha256: str
    image_reference: str
    network_policy: str
    environment_allowlist: tuple[str, ...]
    auth_mounted: bool
    configuration_sha256: str
    experiment_root_sha256: str | None
    workspace_mode: str | None
    arm_id: str | None
    passed: bool
    kind: str

    def __post_init__(self) -> None:
        if not _SHA256.fullmatch(self.host_binary_sha256) or not _SHA256.fullmatch(self.configuration_sha256):
            raise ValueError("runner qualification digest is invalid")
        if self.experiment_root_sha256 is not None and not _SHA256.fullmatch(self.experiment_root_sha256):
            raise ValueError("runner qualification experiment root digest is invalid")
        if self.workspace_mode not in {None, "ro", "rw"}:
            raise ValueError("runner qualification workspace mode is invalid")
        if self.kind not in {"fake", "os"}:
            raise ValueError("runner qualification kind is invalid")
        if self.network_policy not in {"denied", "unrestricted"} or self.auth_mounted:
            raise ValueError("runner qualification cannot mount auth or substitute network policy")

    @classmethod
    def fake(
        cls,
        runner: "NativeAgentRunner",
        experiment_root: Path | None = None,
        *,
        mutation: bool | None = None,
        arm_id: str | None = None,
    ) -> "RunnerQualification":
        workspace_mode = None if mutation is None else ("rw" if mutation else "ro")
        value = cls(
            runner.host_name,
            runner.host_version,
            runner.host_binary_sha256,
            runner.image_reference,
            "denied",
            (),
            False,
            runner.qualification_configuration_sha256(experiment_root, workspace_mode, arm_id),
            None if experiment_root is None else _path_sha256(experiment_root.resolve()),
            workspace_mode,
            arm_id,
            True,
            "fake",
        )
        return value


@dataclass(frozen=True)
class IsolationProbeResult:
    passed: bool
    argv: tuple[str, ...]
    returncode: int
    stdout: str
    stderr: str
    qualification_sha256: str | None = None
    inspect_json: str = ""
    qualification: RunnerQualification | None = None
    evidence_path: str | None = None


@dataclass(frozen=True)
class ZeroWorkObservation:
    process_names: tuple[str, ...]
    accessed_paths: tuple[str, ...]


class RuntimeObserver(Protocol):
    def snapshot(self) -> ZeroWorkObservation: ...


class UnsafeLiveExecution(RuntimeError):
    pass


@dataclass(frozen=True)
class ProviderTransport:
    provider_id: str
    base_url: str
    qualification_sha256: str
    network_policy: str

    def __post_init__(self) -> None:
        if not _ID.fullmatch(self.provider_id):
            raise ValueError("provider transport id is invalid")
        if not _SHA256.fullmatch(self.qualification_sha256):
            raise ValueError("provider transport qualification digest is invalid")
        parsed = urlsplit(self.base_url)
        loopback = parsed.hostname in {"127.0.0.1", "::1", "localhost"}
        if parsed.scheme != "https" and not (parsed.scheme == "http" and loopback):
            raise ValueError("provider transport URL must use HTTPS or loopback HTTP")
        if (
            not parsed.hostname
            or parsed.username
            or parsed.password
            or parsed.query
            or parsed.fragment
            or any(ord(character) <= 32 or character in {'"', "'", "\\"} for character in self.base_url)
        ):
            raise ValueError("provider transport URL cannot contain credentials, query, or fragment")
        if self.network_policy not in {"denied", "unrestricted"}:
            raise ValueError("provider transport network policy is not enforceable")


@dataclass(frozen=True)
class ExecutionResultEnvelope:
    run_record: Mapping[str, object]
    execution: Mapping[str, object]


class PodmanVerificationExecutor:
    def __init__(self, image_reference: str, *, podman_path: str = "podman") -> None:
        self.image_reference = image_reference
        self.podman_path = podman_path

    def execute(self, argv: Sequence[str], candidate_root: Path, timeout_seconds: int) -> VerificationExecution:
        with tempfile.TemporaryDirectory(prefix="agent-outcomes-container-") as control_directory:
            cidfile = Path(control_directory) / "container.cid"
            command = [
                self.podman_path, "run", "--rm", "--cidfile", str(cidfile),
                "--network=none", "--userns=keep-id",
                "--security-opt=no-new-privileges", "--cap-drop=all",
                "--mount", f"type=bind,src={candidate_root.resolve()},dst=/workspace,rw",
                "--workdir", "/workspace", self.image_reference, *argv,
            ]
            return self._execute_owned(command, cidfile, timeout_seconds)

    def _execute_owned(
        self,
        command: Sequence[str],
        cidfile: Path,
        timeout_seconds: int,
    ) -> VerificationExecution:
        with tempfile.TemporaryFile() as stdout_file, tempfile.TemporaryFile() as stderr_file:
            try:
                process = subprocess.Popen(
                    command,
                    stdout=stdout_file,
                    stderr=stderr_file,
                    start_new_session=True,
                    env={"PATH": os.defpath},
                )
            except OSError as exc:
                return VerificationExecution(False, None, "", str(exc))
            supervision_error = None
            try:
                try:
                    process.wait(timeout=timeout_seconds)
                    returncode = process.returncode
                    _terminate_process_group(process.pid, None)
                except subprocess.TimeoutExpired:
                    returncode = 124
                    _terminate_process_group(process.pid, process)
            except (OSError, subprocess.SubprocessError) as exc:
                supervision_error = str(exc)
                returncode = 124
            finally:
                cleanup_confirmed, cleanup_error = _cleanup_container(self.podman_path, cidfile)
            stdout_file.seek(0)
            stderr_file.seek(0)
            stdout = stdout_file.read(1024 * 1024).decode("utf-8", errors="replace")
            stderr = stderr_file.read(1024 * 1024).decode("utf-8", errors="replace")
            if supervision_error is not None or not cleanup_confirmed:
                detail = "; ".join(value for value in (supervision_error, cleanup_error) if value)
                return VerificationExecution(False, None, stdout, stderr + detail)
            return VerificationExecution(True, returncode, stdout, stderr)


class NativeAgentRunner:
    def __init__(
        self,
        campaign: Campaign,
        *,
        image_reference: str,
        codex_path: str,
        miller_path: str,
        podman_path: str = "podman",
        environment_allowlist: Sequence[str] = (),
        provider_transport: ProviderTransport | None = None,
        qualification: RunnerQualification | None = None,
        verification_executor: VerificationExecutor | None = None,
        runtime_observer: RuntimeObserver | None = None,
        probe_timeout_seconds: int = 30,
    ) -> None:
        host = campaign.value["host"]
        model = campaign.value["model"]
        self.campaign = campaign
        self.host_name = host["name"]
        self.host_version = host["version"]
        self.host_binary_sha256 = host["binary_sha256"]
        self.model_id = model["model_id"]
        self.reasoning = model["reasoning"]
        self.image_reference = image_reference
        self.codex_path = codex_path
        self.miller_path = miller_path
        self.podman_path = podman_path
        self.environment_allowlist = tuple(environment_allowlist)
        self.provider_transport = provider_transport
        self.qualification = qualification
        self.verification_executor = verification_executor or PodmanVerificationExecutor(
            image_reference,
            podman_path=podman_path,
        )
        self.runtime_observer = runtime_observer
        self.probe_timeout_seconds = probe_timeout_seconds
        self._os_qualifications: set[str] = set()
        expected_digest = campaign.value["platform_toolchain_image_sha256"]
        if not image_reference.endswith("@sha256:" + expected_digest):
            raise ValueError("image reference must use the frozen campaign digest")
        if (self.host_name, self.host_version) != _SUPPORTED_HOST:
            raise ValueError(f"unsupported native adapter host: {self.host_name} {self.host_version}")
        forbidden = {"HOME", "CODEX_HOME", "OPENAI_API_KEY", "ANTHROPIC_API_KEY"}
        if forbidden.intersection(self.environment_allowlist):
            raise ValueError("environment allowlist contains auth or host-config variables")
        if provider_transport is not None:
            if provider_transport.network_policy != campaign.value["network_policy"]:
                raise ValueError("provider transport network policy differs from campaign")
        if campaign.value["network_policy"] == "allowlist":
            raise ValueError("allowlist network policy has no qualified enforcement adapter")

    @staticmethod
    def option(command: Sequence[str], name: str) -> str | None:
        try:
            return command[command.index(name) + 1]
        except (ValueError, IndexError):
            return None

    def build_agent_command(self, task: OutcomeTask, arm_id: str) -> tuple[list[str], str]:
        self._arm(arm_id)
        sandbox = "read-only" if task.workflow in _ANSWER_WORKFLOWS else "workspace-write"
        command = [
            self.codex_path,
            "exec",
            "--json",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "--strict-config",
            "--model",
            self.model_id,
            "--sandbox",
            sandbox,
            "--cd",
            "/workspace",
            "--output-schema",
            "/run-config/response-schema.json",
            "--config",
            "model_reasoning_effort=" + _toml_string(self.reasoning),
            "--config",
            'approval_policy="never"',
        ]
        if self.provider_transport is not None:
            transport = self.provider_transport
            command.extend([
                "--config", "model_provider=" + _toml_string(transport.provider_id),
                "--config", f'model_providers.{transport.provider_id}.name="qualified-gateway"',
                "--config", f"model_providers.{transport.provider_id}.base_url=" + _toml_string(transport.base_url),
                "--config", f'model_providers.{transport.provider_id}.wire_api="responses"',
                "--config", f"model_providers.{transport.provider_id}.requires_openai_auth=false",
            ])
        if arm_id != "native":
            command.append('--config=mcp_servers.miller.command=' + _toml_string(self.miller_path))
            command.append('--config=mcp_servers.miller.args=["serve"]')
            if arm_id == "native+miller-lexical":
                command.append('--config=mcp_servers.miller.env.MILLER_SEMANTIC="off"')
            command.append('--config=mcp_servers.miller.env.MILLER_CT="off"')
        prompt = task.prompt + "\n\nUse your native read, search, edit, command, and test tools as appropriate. For mutation work, run the repository's focused tests. Return only the requested structured result."
        return command, prompt

    def qualification_configuration_sha256(
        self,
        experiment_root: Path | None = None,
        workspace_mode: str | None = None,
        arm_id: str | None = None,
    ) -> str:
        value = {
            "host": [self.host_name, self.host_version, self.host_binary_sha256],
            "image": self.image_reference,
            "network": self.campaign.value["network_policy"],
            "memory": self.campaign.value["resource_limits"]["memory_bytes"],
            "transport": None if self.provider_transport is None else {
                "provider_id": self.provider_transport.provider_id,
                "base_url": self.provider_transport.base_url,
                "qualification_sha256": self.provider_transport.qualification_sha256,
                "network_policy": self.provider_transport.network_policy,
            },
            "experiment_root_sha256": None if experiment_root is None else _path_sha256(experiment_root.resolve()),
            "mounts": [
                f"task-input:/workspace:{workspace_mode}",
                "agent-output:/run-results:rw",
                "runtime:/runtime:rw",
                "miller-runtime:/workspace/.miller:rw",
                "public-schema:/run-config/response-schema.json:ro",
                "native-mask:/opt/miller:ro" if arm_id == "native" else "miller-visible:/opt/miller:ro",
            ],
        }
        return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()

    def build_run(
        self,
        task: OutcomeTask,
        arm_id: str,
        snapshot: Path,
        output_dir: Path,
        runtime_dir: Path | None = None,
        cidfile: Path | None = None,
    ) -> tuple[list[str], str]:
        agent, prompt = self.build_agent_command(task, arm_id)
        mode = "ro" if task.workflow in _ANSWER_WORKFLOWS else "rw"
        command = [
            self.podman_path,
            "run",
            "--rm",
            *([] if cidfile is None else ["--cidfile", str(cidfile)]),
            *self._runtime_arguments(
                snapshot,
                output_dir,
                mode,
                runtime_dir or output_dir.parent / "runtime-artifacts" / "command-preview",
                arm_id,
            ),
            self.image_reference,
            *agent,
        ]
        return command, prompt

    def build_isolation_probe(
        self,
        experiment_root: Path,
        sentinel: str,
        sentinel_secret: str,
        *,
        mutation: bool,
        arm_id: str,
    ) -> list[str]:
        root = experiment_root.resolve()
        workspace_mode = "rw" if mutation else "ro"
        absolute_sentinel = root / "private-grader" / sentinel
        workspace_probe = "/workspace/.agent-outcomes-probe-" + sentinel
        script = (
            "set -eu; "
            f"test \"$({shlex.quote(self.codex_path)} --version)\" = {shlex.quote(self.host_name + ' ' + self.host_version)}; "
            f"test \"$(sha256sum {shlex.quote(self.codex_path)} | cut -d ' ' -f 1)\" = {shlex.quote(self.host_binary_sha256)}; "
            f"if cat {shlex.quote(str(absolute_sentinel))} >/dev/null 2>&1; then exit 41; fi; "
            f"if cat {shlex.quote('/private-grader/' + sentinel)} >/dev/null 2>&1; then exit 42; fi; "
            + (
                f"printf %s {shlex.quote(sentinel_secret)} > {shlex.quote(workspace_probe)}; "
                f"test \"$(cat {shlex.quote(workspace_probe)})\" = {shlex.quote(sentinel_secret)}; "
                f"rm {shlex.quote(workspace_probe)}; "
                if mutation else
                f"if printf %s {shlex.quote(sentinel_secret)} > {shlex.quote(workspace_probe)} 2>/dev/null; then exit 43; fi; "
            )
            + "test ! -S /run/podman/podman.sock; "
            + ("test ! -e /opt/miller/miller" if arm_id == "native" else f"test -x {shlex.quote(self.miller_path)}")
        )
        return [
            self.podman_path,
            "create",
            "--cidfile",
            str(root / "private-grader" / f"{sentinel}.cid"),
            *self._runtime_arguments(
                root / "task-input",
                root / "agent-output",
                workspace_mode,
                root / "qualification-runtime" / workspace_mode,
                arm_id,
            ),
            self.image_reference,
            "/bin/sh",
            "-c",
            script,
        ]

    def qualify_isolation(self, experiment_root: Path, *, mutation: bool, arm_id: str) -> IsolationProbeResult:
        self._arm(arm_id)
        root = experiment_root.resolve(strict=True)
        snapshot = root / "task-input"
        grader = root / "private-grader"
        output = root / "agent-output"
        if not snapshot.is_dir() or not grader.is_dir():
            raise ValueError("isolation qualification requires task-input and private-grader")
        output.mkdir(mode=0o700, exist_ok=False)
        qualification_runtime = root / "qualification-runtime" / ("rw" if mutation else "ro")
        (qualification_runtime / "miller").mkdir(parents=True)
        (qualification_runtime / "native-miller-mask").mkdir()
        _write_exclusive(qualification_runtime / "public-response-schema.json", "{}")
        sentinel = "sentinel-" + secrets.token_hex(16)
        sentinel_secret = secrets.token_hex(32)
        (grader / sentinel).write_text(sentinel_secret, encoding="utf-8")
        command = self.build_isolation_probe(root, sentinel, sentinel_secret, mutation=mutation, arm_id=arm_id)
        safe_env = {"PATH": os.defpath}
        try:
            created = subprocess.run(
                command,
                capture_output=True,
                text=True,
                check=False,
                timeout=self.probe_timeout_seconds,
                env=safe_env,
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            created = subprocess.CompletedProcess(command, 124, "", str(exc))
        cidfile = grader / f"{sentinel}.cid"
        container_id = created.stdout.strip()
        if cidfile.is_file() and not cidfile.is_symlink():
            try:
                container_id = cidfile.read_text(encoding="ascii").strip()
            except (OSError, UnicodeError):
                container_id = ""
        inspect_stdout = ""
        start_stdout = ""
        stderr = created.stderr
        returncode = created.returncode
        passed = False
        qualification = None
        if created.returncode == 0 and container_id and "\n" not in container_id:
            try:
                inspected = subprocess.run(
                    [self.podman_path, "inspect", container_id],
                    capture_output=True,
                    text=True,
                    check=False,
                    timeout=30,
                    env=safe_env,
                )
                inspect_stdout = inspected.stdout
                stderr += inspected.stderr
                mode = "rw" if mutation else "ro"
                if inspected.returncode == 0 and self._inspect_matches_runtime(
                    inspect_stdout,
                    snapshot,
                    output,
                    mode,
                    qualification_runtime,
                    arm_id,
                ):
                    started = subprocess.run(
                        [self.podman_path, "start", "--attach", container_id],
                        capture_output=True,
                        text=True,
                        check=False,
                        timeout=30,
                        env=safe_env,
                    )
                    start_stdout = started.stdout
                    stderr += started.stderr
                    returncode = started.returncode
                    passed = started.returncode == 0
            except (OSError, subprocess.TimeoutExpired) as exc:
                stderr += str(exc)
                passed = False
        if cidfile.exists():
            cleanup_confirmed, cleanup_error = _cleanup_container(self.podman_path, cidfile)
            passed = passed and cleanup_confirmed
            stderr += cleanup_error
        if passed:
            configuration_sha = self.qualification_configuration_sha256(root, mode, arm_id)
            qualification = RunnerQualification(
                self.host_name,
                self.host_version,
                self.host_binary_sha256,
                self.image_reference,
                self.campaign.value["network_policy"],
                (),
                False,
                configuration_sha,
                _path_sha256(root),
                mode,
                arm_id,
                True,
                "os",
            )
        try:
            output.rmdir()
        except OSError:
            passed = False
            qualification = None
        evidence_dir = grader / "qualification-evidence"
        evidence_dir.mkdir(mode=0o700, exist_ok=True)
        evidence_path = evidence_dir / f"{sentinel}.json"
        _write_exclusive(evidence_path, json.dumps({
            "argv": command,
            "returncode": returncode,
            "stdout": created.stdout + start_stdout,
            "stderr": stderr,
            "inspect_json": inspect_stdout,
            "passed": passed,
            "configuration_sha256": None if qualification is None else qualification.configuration_sha256,
        }, sort_keys=True))
        if qualification is not None:
            self._os_qualifications.add(qualification.configuration_sha256)
        return IsolationProbeResult(
            passed,
            tuple(command),
            returncode,
            created.stdout + start_stdout,
            stderr,
            None if qualification is None else qualification.configuration_sha256,
            inspect_stdout,
            qualification,
            str(evidence_path),
        )

    def run(
        self,
        task: OutcomeTask | VerifiableTask,
        arm_id: str,
        snapshot: Path,
        output_dir: Path,
        *,
        repetition: int = 1,
        order: int = 1,
    ) -> ExecutionResultEnvelope:
        if self.provider_transport is None:
            raise UnsafeLiveExecution("live execution requires a qualified credential transport with no provider secret in the agent container")
        if self.qualification is None or not self.qualification.passed:
            raise UnsafeLiveExecution("live execution requires a passing frozen OS qualification")
        outcome_task = task.task if isinstance(task, VerifiableTask) else task
        snapshot_input = Path(snapshot)
        if snapshot_input.is_symlink() or snapshot_input.parent.is_symlink():
            raise ValueError("experiment input and root cannot be symlinks")
        snapshot = snapshot_input.resolve(strict=True)
        experiment_root = self._validate_experiment_paths(snapshot, output_dir)
        workspace_mode = "ro" if outcome_task.workflow in _ANSWER_WORKFLOWS else "rw"
        if (
            self.qualification.configuration_sha256
            != self.qualification_configuration_sha256(experiment_root, workspace_mode, arm_id)
            or self.qualification.experiment_root_sha256 != _path_sha256(experiment_root)
            or self.qualification.workspace_mode != workspace_mode
            or self.qualification.arm_id != arm_id
        ):
            raise UnsafeLiveExecution("OS qualification does not match the frozen execution configuration")
        transport_host = urlsplit(self.provider_transport.base_url).hostname or ""
        if self.qualification.kind == "fake" and not (
            transport_host.endswith(".invalid") or transport_host in {"127.0.0.1", "::1", "localhost"}
        ):
            raise UnsafeLiveExecution("fake qualification cannot authorize a live provider gateway")
        if (
            self.qualification.kind == "os"
            and self.qualification.configuration_sha256 not in self._os_qualifications
        ):
            raise UnsafeLiveExecution("OS qualification was not produced by this runner's direct isolation probe")
        if self.qualification.kind == "os" and not isinstance(task, VerifiableTask):
            raise UnsafeLiveExecution("OS-qualified execution requires a bound public response schema and verifier")
        if source_snapshot_sha256(snapshot) != outcome_task.snapshot_sha256:
            raise ValueError("task snapshot identity does not match frozen snapshot_sha256")
        if (snapshot / ".miller").exists():
            raise ValueError("frozen task input cannot contain the isolated Miller runtime mount path")
        output_dir.mkdir(parents=True, exist_ok=False)
        run_name = f"{outcome_task.task_id}-{arm_id.replace('+', '-')}-r{repetition}-o{order}"
        candidate_root = experiment_root / "run-workspaces" / run_name
        candidate_root.parent.mkdir(mode=0o700, exist_ok=True)
        shutil.copytree(snapshot, candidate_root, symlinks=True)
        if source_snapshot_sha256(candidate_root) != outcome_task.snapshot_sha256:
            raise ValueError("disposable run copy differs from frozen snapshot")
        runtime_root = experiment_root / "runtime-artifacts" / run_name
        (runtime_root / "miller").mkdir(parents=True)
        (runtime_root / "native-miller-mask").mkdir()
        response_schema = public_response_schema(task) if isinstance(task, VerifiableTask) else {"type": "object"}
        response_schema_json = json.dumps(_plain(response_schema), sort_keys=True, separators=(",", ":"))
        _write_exclusive(runtime_root / "public-response-schema.json", response_schema_json)
        evidence_parent = experiment_root / "private-grader" / "run-evidence"
        evidence_parent.mkdir(mode=0o700, exist_ok=True)
        evidence_dir = evidence_parent / run_name
        evidence_dir.mkdir(mode=0o700)
        raw_path = evidence_dir / "raw-events.jsonl"
        stderr_path = evidence_dir / "stderr.txt"
        cidfile = evidence_dir / "container.cid"
        prompt_path = evidence_dir / "prompt.txt"
        command, prompt = self.build_run(
            outcome_task,
            arm_id,
            candidate_root,
            output_dir,
            runtime_root,
            cidfile,
        )
        _write_exclusive(prompt_path, prompt)
        zero_work_before = self.runtime_observer.snapshot() if self.runtime_observer else None
        started = time.monotonic()
        timed_out = False
        launch_error = None
        descendant_cleanup_performed = False
        process = None
        with (
            _open_exclusive(raw_path) as raw_file,
            _open_exclusive(stderr_path) as stderr_file,
            prompt_path.open("rb") as prompt_file,
        ):
            try:
                process = subprocess.Popen(
                    command,
                    stdin=prompt_file,
                    stdout=raw_file,
                    stderr=stderr_file,
                    start_new_session=True,
                    env={"PATH": os.defpath},
                )
            except OSError as exc:
                launch_error = str(exc)
            if process is not None:
                try:
                    process.wait(timeout=outcome_task.max_wall_seconds)
                except subprocess.TimeoutExpired:
                    timed_out = True
                    descendant_cleanup_performed = _terminate_process_group(process.pid, process)
                else:
                    descendant_cleanup_performed = _terminate_process_group(process.pid, None)
        wall_time = time.monotonic() - started
        container_cleanup_confirmed = None
        container_cleanup_error = ""
        if cidfile.exists():
            container_cleanup_confirmed, container_cleanup_error = _cleanup_container(self.podman_path, cidfile)
        elif self.qualification.kind == "os":
            container_cleanup_confirmed = False
            container_cleanup_error = "owned container id was not captured"
        zero_work_error = None
        if self.runtime_observer and zero_work_before is not None:
            try:
                self.assert_zero_work(arm_id, zero_work_before, self.runtime_observer.snapshot())
            except RuntimeError as exc:
                zero_work_error = str(exc)
        raw_size = raw_path.stat().st_size
        if raw_size > 16 * 1024 * 1024:
            parsed = ParsedAgentEvents({}, 0, (None, None, None), None, "raw JSONL exceeds parse bound", (), None, False)
        else:
            parsed = self.parse_events(raw_path.read_text(encoding="utf-8", errors="replace"))
        if container_cleanup_confirmed is False:
            outcome = "infrastructure_void"
            evidence = {"reason": "owned container cleanup was not confirmed"}
        elif zero_work_error is not None:
            outcome = "infrastructure_void"
            evidence = {"reason": "off-mode observation failed"}
        elif timed_out:
            outcome = "timeout"
            evidence = {"reason": "process timeout"}
        elif launch_error is not None:
            outcome = "infrastructure_void"
            evidence = {"reason": "process launch failed", "detail": launch_error}
        elif parsed.unsupported_reason:
            outcome = "unsupported"
            evidence = {"reason": parsed.unsupported_reason}
        elif parsed.failed:
            outcome = "product_error"
            evidence = {"reason": "agent failure lifecycle event"}
        elif process is not None and process.returncode != 0:
            outcome = "product_error"
            evidence = {"returncode": process.returncode}
        elif not isinstance(task, VerifiableTask):
            outcome = "unsupported"
            evidence = {"reason": "bound verifier is required"}
        else:
            result = _parse_result(parsed.answer)
            verification = verify_result(task, result, candidate_root, executor=self.verification_executor)
            outcome = "correct" if verification.correct else "incorrect"
            evidence = dict(verification.evidence)
            evidence["failures"] = list(verification.failures)
        canonical_campaign = json.dumps(_plain(self.campaign.value), sort_keys=True, separators=(",", ":")).encode()
        campaign_sha = hashlib.sha256(canonical_campaign).hexdigest()
        evidence_sha = hashlib.sha256(json.dumps(evidence, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
        raw_sha = _file_sha256(raw_path)
        input_tokens, cached_tokens, output_tokens = parsed.model_tokens
        record = {
            "contract_id": "agent-outcomes-v1",
            "campaign_sha256": campaign_sha,
            "run_id": f"{outcome_task.task_id}-{arm_id.replace('+', '-')}-r{repetition}",
            "task_id": outcome_task.task_id,
            "arm_id": arm_id,
            "repetition": repetition,
            "order": order,
            "outcome": outcome,
            "verifier_evidence_sha256": evidence_sha,
            "wall_time_seconds": wall_time,
            "native_tool_counts": dict(parsed.native_tool_counts),
            "miller_calls": parsed.miller_calls,
            "total_model_input_tokens": input_tokens,
            "total_model_cached_tokens": cached_tokens,
            "total_model_output_tokens": output_tokens,
            "raw_event_sha256": raw_sha,
            "price_derived_cost": None,
        }
        validated = validate_run_record(record)
        execution = {
            "host": {"name": self.host_name, "version": self.host_version, "binary_sha256": self.host_binary_sha256},
            "image_reference": self.image_reference,
            "provider_transport_qualification_sha256": self.provider_transport.qualification_sha256,
            "public_response_schema_sha256": hashlib.sha256(response_schema_json.encode()).hexdigest(),
            "verifier_sha256": None if not isinstance(task, VerifiableTask) else hashlib.sha256(
                json.dumps(_plain(task.verifier.value), sort_keys=True, separators=(",", ":")).encode()
            ).hexdigest(),
            "argv_sha256": hashlib.sha256(json.dumps(command, separators=(",", ":")).encode()).hexdigest(),
            "prompt_sha256": hashlib.sha256(prompt.encode()).hexdigest(),
            "environment_allowlist_names": ["PATH"],
            "network_policy": self.campaign.value["network_policy"],
            "reasoning_output_tokens": parsed.reasoning_output_tokens,
            "peak_process_memory_bytes": None,
            "setup": {
                "download_bytes": None,
                "download_seconds": None,
                "extraction_seconds": None,
                "sidecar_convergence_seconds": None,
                "model_load_seconds": None,
                "shared_broker_mode": None,
                "steady_state_ready": None,
                "measurement_scope": "agent-process-and-owned-descendants",
            },
            "wall_time_seconds": wall_time,
            "timed_out": timed_out,
            "launch_error": launch_error,
            "zero_work_error": zero_work_error,
            "descendant_cleanup_performed": descendant_cleanup_performed,
            "container_cleanup_confirmed": container_cleanup_confirmed,
            "container_cleanup_error": container_cleanup_error,
            "raw_events_path": str(raw_path),
            "stderr_path": str(stderr_path),
            "candidate_root": str(candidate_root),
            "baseline_snapshot_sha256_after": source_snapshot_sha256(snapshot),
        }
        private_envelope_path = evidence_dir / "execution-private.json"
        execution["private_envelope_path"] = str(private_envelope_path)
        _write_exclusive(private_envelope_path, json.dumps(execution, sort_keys=True))
        _write_exclusive(evidence_dir / "run-record.json", json.dumps(_plain(validated.value), sort_keys=True))
        return ExecutionResultEnvelope(_plain(validated.value), execution)

    @staticmethod
    def assert_zero_work(
        arm_id: str,
        before: ZeroWorkObservation,
        after: ZeroWorkObservation,
    ) -> None:
        new_processes = set(after.process_names) - set(before.process_names)
        new_paths = set(after.accessed_paths) - set(before.accessed_paths)
        if arm_id == "native+miller-lexical":
            forbidden_processes = {name for name in new_processes if "semantic" in name.casefold() or "ct-daemon" in name.casefold()}
            forbidden_paths = {path for path in new_paths if "vectors.db" in path or "/ct" in path}
            if forbidden_processes or forbidden_paths:
                raise RuntimeError("lexical arm performed semantic or continuous-testing work")

    @staticmethod
    def public_record(record: Mapping[str, object]) -> Mapping[str, object]:
        allowed = {
            "contract_id", "campaign_sha256", "run_id", "task_id", "arm_id",
            "repetition", "order", "outcome", "verifier_evidence_sha256",
            "wall_time_seconds", "native_tool_counts", "miller_calls",
            "total_model_input_tokens", "total_model_cached_tokens",
            "total_model_output_tokens", "raw_event_sha256", "price_derived_cost",
        }
        if set(record) != allowed:
            raise ValueError("public run record must contain exactly the frozen fields")
        return _plain(validate_run_record(record).value)

    def parse_events(self, text: str) -> ParsedAgentEvents:
        counts: dict[str, int] = {}
        miller_calls = 0
        usage_totals: list[int] | None = None
        usage_complete = True
        reasoning_total: int | None = None
        answer = None
        unsupported = None
        failed = False
        completed_ids: set[str] = set()
        raw_lines = tuple(line for line in text.splitlines() if line)
        for line in raw_lines:
            try:
                event = json.loads(
                    line,
                    object_pairs_hook=_unique_json_object,
                    parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)),
                )
            except (json.JSONDecodeError, ValueError):
                unsupported = unsupported or "malformed JSONL event"
                continue
            if not isinstance(event, dict):
                unsupported = unsupported or "JSONL event must be an object"
                continue
            event_type = event.get("type")
            if not isinstance(event_type, str):
                unsupported = unsupported or "event type must be a string"
                continue
            if event_type not in _EVENT_TYPES:
                unsupported = unsupported or f"unknown event type: {event_type}"
                continue
            if event_type == "item.completed":
                item = event.get("item", {})
                if not isinstance(item, dict):
                    unsupported = unsupported or "completed item must be an object"
                    continue
                item_id = item.get("id")
                if item_id is not None and not isinstance(item_id, str):
                    unsupported = unsupported or "item id must be a string"
                    continue
                if item_id is not None and item_id in completed_ids:
                    continue
                if item_id is not None:
                    completed_ids.add(item_id)
                item_type = item.get("type")
                if item_type == "command_execution":
                    if not isinstance(item.get("command"), str):
                        unsupported = unsupported or "command item must contain a string command"
                    else:
                        counts["command"] = counts.get("command", 0) + 1
                elif item_type == "file_change":
                    if not isinstance(item.get("changes"), list):
                        unsupported = unsupported or "file change item must contain an array of changes"
                    else:
                        counts["edit"] = counts.get("edit", 0) + 1
                elif item_type == "mcp_tool_call":
                    if not isinstance(item.get("server"), str) or not isinstance(item.get("tool"), str):
                        unsupported = unsupported or "MCP item must contain string server and tool names"
                    elif item.get("server") == "miller":
                        miller_calls += 1
                elif item_type == "agent_message":
                    if not isinstance(item.get("text"), str):
                        unsupported = unsupported or "agent message must contain string text"
                    else:
                        answer = item["text"]
                elif item_type not in {"reasoning", "todo_list", "web_search"}:
                    unsupported = unsupported or f"unknown item type: {item_type}"
            elif event_type == "turn.completed":
                measured = event.get("usage")
                if measured is None:
                    usage_complete = False
                else:
                    if not isinstance(measured, dict):
                        unsupported = unsupported or "unsupported usage event"
                        usage_complete = False
                        continue
                    required = ("input_tokens", "cached_input_tokens", "output_tokens")
                    values = [measured.get(key) for key in required]
                    reasoning = measured.get("reasoning_output_tokens")
                    if not all(_nonnegative_int(value) for value in values) or (
                        reasoning is not None and not _nonnegative_int(reasoning)
                    ):
                        unsupported = unsupported or "unsupported usage event"
                        usage_complete = False
                    else:
                        if usage_totals is None:
                            usage_totals = [0, 0, 0]
                        for index, value in enumerate(values):
                            usage_totals[index] += value
                        if reasoning is not None:
                            reasoning_total = (reasoning_total or 0) + reasoning
            elif event_type in {"turn.failed", "error"}:
                failed = True
                usage_complete = False
        usage = (None, None, None) if usage_totals is None or not usage_complete else tuple(usage_totals)
        if not usage_complete:
            reasoning_total = None
        return ParsedAgentEvents(counts, miller_calls, usage, answer, unsupported, raw_lines, reasoning_total, failed)

    def _arm(self, arm_id: str) -> Mapping[str, object]:
        for arm in self.campaign.value["arms"]:
            if arm["arm_id"] == arm_id:
                return arm
        raise ValueError(f"arm is not frozen in campaign: {arm_id}")

    def _runtime_arguments(
        self,
        workspace: Path,
        output_dir: Path,
        workspace_mode: str,
        runtime_dir: Path | None = None,
        arm_id: str | None = None,
    ) -> list[str]:
        if workspace_mode not in {"ro", "rw"}:
            raise ValueError("workspace mount mode is invalid")
        network_args = ["--network=none"] if self.campaign.value["network_policy"] == "denied" else []
        arguments = [
            "--userns=keep-id",
            "--security-opt=no-new-privileges",
            "--cap-drop=all",
            *network_args,
            "--memory",
            str(self.campaign.value["resource_limits"]["memory_bytes"]),
            "--mount",
            f"type=bind,src={workspace.resolve()},dst=/workspace,{workspace_mode}",
            "--mount",
            f"type=bind,src={output_dir.resolve()},dst=/run-results,rw",
        ]
        if runtime_dir is not None:
            arguments.extend([
                "--mount",
                f"type=bind,src={runtime_dir.resolve()},dst=/runtime,rw",
                "--mount",
                f"type=bind,src={(runtime_dir / 'miller').resolve()},dst=/workspace/.miller,rw",
                "--mount",
                f"type=bind,src={(runtime_dir / 'public-response-schema.json').resolve()},dst=/run-config/response-schema.json,ro",
                "--env", "PYTHONDONTWRITEBYTECODE=1",
                "--env", "DOTNET_CLI_HOME=/runtime/dotnet-home",
                "--env", "NUGET_PACKAGES=/runtime/nuget",
                "--env", "CARGO_TARGET_DIR=/runtime/cargo-target",
                "--env", "GOCACHE=/runtime/go-build",
                "--env", "GOMODCACHE=/runtime/go-mod",
                "--env", "GRADLE_USER_HOME=/runtime/gradle",
            ])
            if arm_id == "native":
                arguments.extend([
                    "--mount",
                    f"type=bind,src={(runtime_dir / 'native-miller-mask').resolve()},dst=/opt/miller,ro",
                ])
        return arguments

    def _inspect_matches_runtime(
        self,
        inspect_text: str,
        workspace: Path,
        output_dir: Path,
        workspace_mode: str,
        runtime_dir: Path,
        arm_id: str,
    ) -> bool:
        try:
            documents = json.loads(inspect_text, object_pairs_hook=_unique_json_object)
            if not isinstance(documents, list) or len(documents) != 1 or not isinstance(documents[0], dict):
                return False
            mounts = documents[0].get("Mounts")
            host_config = documents[0].get("HostConfig")
            if not isinstance(mounts, list) or not isinstance(host_config, dict):
                return False
            image_digest = documents[0].get("ImageDigest")
            if image_digest != "sha256:" + self.campaign.value["platform_toolchain_image_sha256"]:
                return False
            network_mode = host_config.get("NetworkMode")
            expected_network = self.campaign.value["network_policy"]
            if expected_network == "denied" and network_mode != "none":
                return False
            if expected_network == "unrestricted" and network_mode in {None, "none", "host"}:
                return False
            if (
                host_config.get("Privileged") is not False
                or host_config.get("PidMode") == "host"
                or host_config.get("Memory") != self.campaign.value["resource_limits"]["memory_bytes"]
                or "ALL" not in host_config.get("CapDrop", [])
                or "no-new-privileges" not in host_config.get("SecurityOpt", [])
            ):
                return False
            observed = {}
            for mount in mounts:
                if not isinstance(mount, dict):
                    return False
                destination = mount.get("Destination")
                source = mount.get("Source")
                read_write = mount.get("RW")
                if not isinstance(destination, str) or not isinstance(source, str) or not isinstance(read_write, bool):
                    return False
                observed[destination] = (Path(source).resolve(), read_write)
            expected = {
                "/workspace": (workspace.resolve(), workspace_mode == "rw"),
                "/run-results": (output_dir.resolve(), True),
                "/runtime": (runtime_dir.resolve(), True),
                "/workspace/.miller": ((runtime_dir / "miller").resolve(), True),
                "/run-config/response-schema.json": ((runtime_dir / "public-response-schema.json").resolve(), False),
            }
            if arm_id == "native":
                expected["/opt/miller"] = ((runtime_dir / "native-miller-mask").resolve(), False)
            return observed == expected
        except (json.JSONDecodeError, ValueError, OSError):
            return False

    @staticmethod
    def _validate_experiment_paths(snapshot: Path, output_dir: Path) -> Path:
        if snapshot.name != "task-input" or output_dir.name != "agent-output":
            raise ValueError("runner paths must use task-input and agent-output topology")
        experiment_root = snapshot.parent.resolve(strict=True)
        if output_dir.parent.resolve(strict=True) != experiment_root:
            raise ValueError("task input and agent output must share one experiment root")
        if experiment_root in {Path("/"), Path.home().resolve()} or experiment_root.parent == Path("/"):
            raise ValueError("experiment root is unsafe")
        if snapshot.is_symlink() or experiment_root.is_symlink() or output_dir.parent.is_symlink():
            raise ValueError("experiment input and root cannot be symlinks")
        grader = experiment_root / "private-grader"
        if not grader.is_dir():
            raise ValueError("private-grader directory is required outside agent mounts")
        if grader.is_symlink() or grader.resolve(strict=True).parent != experiment_root:
            raise ValueError("private-grader must be a direct non-symlink child")
        if output_dir.exists() or snapshot == output_dir or grader in snapshot.parents or snapshot in grader.parents:
            raise ValueError("experiment input, output, and grader paths overlap")
        return experiment_root


def _plain(value):
    if isinstance(value, Mapping):
        return {key: _plain(item) for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return [_plain(item) for item in value]
    return value


def _toml_string(value: str) -> str:
    return json.dumps(value, ensure_ascii=False)


def _file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _path_sha256(path: Path) -> str:
    return hashlib.sha256(os.fsencode(path)).hexdigest()


def _open_exclusive(path: Path):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    return os.fdopen(descriptor, "wb")


def _write_exclusive(path: Path, value: str) -> None:
    with _open_exclusive(path) as stream:
        stream.write(value.encode("utf-8"))


def _terminate_process_group(process_group_id: int, leader: subprocess.Popen | None) -> bool:
    try:
        os.killpg(process_group_id, 0)
    except ProcessLookupError:
        return False
    try:
        os.killpg(process_group_id, signal.SIGTERM)
    except ProcessLookupError:
        return False
    if leader is not None:
        try:
            leader.wait(timeout=2)
        except subprocess.TimeoutExpired:
            pass
    deadline = time.monotonic() + 2
    while time.monotonic() < deadline:
        try:
            os.killpg(process_group_id, 0)
        except ProcessLookupError:
            return True
        time.sleep(0.02)
    try:
        os.killpg(process_group_id, signal.SIGKILL)
    except ProcessLookupError:
        pass
    if leader is not None:
        leader.wait()
    return True


def _cleanup_container(podman_path: str, cidfile: Path) -> tuple[bool, str]:
    try:
        if cidfile.is_symlink() or not cidfile.is_file() or cidfile.stat().st_size > 128:
            return False, "owned container id file is unsafe"
        container_id = cidfile.read_text(encoding="ascii").strip()
    except (OSError, UnicodeError):
        return False, "owned container id file is unreadable"
    if not re.fullmatch(r"[0-9a-f]{12,64}", container_id):
        return False, "owned container id is invalid"
    safe_env = {"PATH": os.defpath}
    errors = []
    for operation in (["stop", "--time", "2"], ["rm", "--force"]):
        try:
            completed = subprocess.run(
                [podman_path, *operation, container_id],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                check=False,
                timeout=10,
                env=safe_env,
            )
            if completed.returncode != 0:
                errors.append(completed.stderr[:4096].decode("utf-8", errors="replace"))
        except (OSError, subprocess.TimeoutExpired) as exc:
            errors.append(str(exc))
    try:
        exists = subprocess.run(
            [podman_path, "container", "exists", container_id],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            check=False,
            timeout=10,
            env=safe_env,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return False, "; ".join([*errors, str(exc)])
    if exists.returncode == 0:
        return False, "; ".join([*errors, "owned container still exists after cleanup"])
    if exists.returncode != 1:
        detail = exists.stderr[:4096].decode("utf-8", errors="replace")
        return False, "; ".join([*errors, detail or f"container exists failed with {exists.returncode}"])
    return True, "; ".join(error for error in errors if error)


def _unique_json_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"duplicate JSON key: {key}")
        value[key] = item
    return value


def _nonnegative_int(value) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def _parse_result(answer: str | None) -> Mapping[str, object]:
    try:
        value = json.loads(
            answer or "",
            object_pairs_hook=_unique_json_object,
            parse_constant=lambda constant: (_ for _ in ()).throw(ValueError(constant)),
        )
    except (json.JSONDecodeError, ValueError):
        return {}
    return value if isinstance(value, dict) else {}
