"""Isolated Codex runner for the agent-efficiency benchmark."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import signal
import subprocess
import tempfile
import time
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from benchlib.agent_contract import (
    BenchmarkTask,
    SnapshotIdentity,
    StructuredAnswer,
    VerificationResult,
    verify_answer,
)


_BENCHMARK_ROOT = Path(__file__).resolve().parents[1] / "benchmarks" / "agent-efficiency"
_ALLOWED_ENVIRONMENT = frozenset(
    {
        "ALL_PROXY",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "LANG",
        "LC_ALL",
        "LC_CTYPE",
        "NO_PROXY",
        "PATH",
        "SSL_CERT_DIR",
        "SSL_CERT_FILE",
        "TMPDIR",
    }
)
_ALLOWED_ITEM_TYPES = frozenset({"agent_message", "reasoning", "plan", "plan_update"})


def isolated_environment_keys() -> tuple[str, ...]:
    inherited = {name for name in os.environ if name in _ALLOWED_ENVIRONMENT}
    return tuple(sorted(inherited | {"CODEX_HOME", "HOME", "TMPDIR"}))


@dataclass(frozen=True)
class AgentArm:
    product: str
    product_command: tuple[str, ...]
    product_environment: tuple[tuple[str, str], ...] = ()

    def __post_init__(self) -> None:
        if self.product not in {"miller", "julie"}:
            raise ValueError(f"unsupported product: {self.product}")
        if not self.product_command or any(not value for value in self.product_command):
            raise ValueError("product command must contain non-empty arguments")
        names = [name for name, _ in self.product_environment]
        if len(names) != len(set(names)) or any(not name or "=" in name for name in names):
            raise ValueError("product environment names must be unique and non-empty")
        if any("\0" in value for _, value in self.product_environment):
            raise ValueError("product environment values cannot contain NUL")


@dataclass(frozen=True)
class AgentSnapshot:
    identity: SnapshotIdentity
    root: Path


@dataclass(frozen=True)
class AgentRun:
    outcome: str
    classification: str
    failure_reason: str | None
    answer: StructuredAnswer | None
    verification: VerificationResult
    command_manifest_path: Path
    codex_events_path: Path
    proxy_events_path: Path
    stderr_path: Path
    diagnostics: tuple[Mapping[str, Any], ...]
    model_input_tokens: int | None
    model_output_tokens: int | None
    wall_clock_ms: int
    exit_code: int | None
    child_home_removed: bool


@dataclass(frozen=True)
class _ParsedEvents:
    answer: StructuredAnswer | None
    answer_error: str | None
    diagnostics: tuple[Mapping[str, Any], ...]
    disallowed_item: str | None
    malformed: str | None
    turn_completed: bool
    turn_failed: bool
    model_input_tokens: int | None
    model_output_tokens: int | None


class CodexAgentRunner:
    def __init__(
        self,
        codex_executable: str | Path,
        proxy_command: Sequence[str],
        source_codex_home: str | Path,
        timeout_seconds: float = 120,
    ) -> None:
        self._codex_executable = str(codex_executable)
        self._proxy_command = tuple(str(value) for value in proxy_command)
        self._source_codex_home = Path(source_codex_home)
        self._timeout_seconds = timeout_seconds
        if not self._proxy_command:
            raise ValueError("proxy command must not be empty")
        if timeout_seconds <= 0:
            raise ValueError("timeout must be positive")

    def run(
        self,
        task: BenchmarkTask,
        arm: AgentArm,
        snapshot: AgentSnapshot,
        output_dir: str | Path,
    ) -> AgentRun:
        output = Path(output_dir)
        output.mkdir(parents=True, exist_ok=True)
        paths = _artifact_paths(output)
        _clear_previous_artifacts(paths.values())
        preflight = self._preflight(task, snapshot)
        if not preflight.passed:
            return AgentRun(
                outcome="preflight_failure",
                classification="harness_failure",
                failure_reason="product_error",
                answer=None,
                verification=preflight,
                command_manifest_path=paths["manifest"],
                codex_events_path=paths["events"],
                proxy_events_path=paths["proxy"],
                stderr_path=paths["stderr"],
                diagnostics=(),
                model_input_tokens=None,
                model_output_tokens=None,
                wall_clock_ms=0,
                exit_code=None,
                child_home_removed=True,
            )

        execution_root = Path(tempfile.mkdtemp(prefix="miller-agent-run-"))
        child_home = execution_root / "codex-home"
        working_dir = execution_root / "working"
        schema_path = execution_root / "answer-schema.json"
        start = time.monotonic()
        exit_code: int | None = None
        timed_out = False
        stdout = ""
        stderr = ""
        parsed = _ParsedEvents(None, None, (), None, None, False, False, None, None)
        process: subprocess.Popen[str] | None = None
        try:
            child_home.mkdir(mode=0o700)
            child_home.chmod(0o700)
            working_dir.mkdir(mode=0o700)
            schema_path.write_bytes((_BENCHMARK_ROOT / "answer-schema.json").read_bytes())
            self._copy_auth(child_home)
            command = self._command(arm, snapshot.root, working_dir, schema_path, paths["proxy"])
            prompt = _prompt(task)
            environment = _isolated_environment(child_home, execution_root)
            _write_manifest(paths["manifest"], command, working_dir, environment, prompt, child_home)
            process = _start_process(command, working_dir, environment)
            try:
                stdout, stderr = process.communicate(prompt, timeout=self._timeout_seconds)
                exit_code = process.returncode
            except subprocess.TimeoutExpired as exc:
                timed_out = True
                stdout = _stream_text(exc.stdout)
                stderr = _stream_text(exc.stderr)
                _terminate_process_tree(process)
                complete_stdout, complete_stderr = process.communicate()
                stdout = complete_stdout if complete_stdout is not None else stdout
                stderr = complete_stderr if complete_stderr is not None else stderr
                exit_code = process.returncode
            paths["events"].write_text(stdout, encoding="utf-8")
            paths["stderr"].write_text(stderr, encoding="utf-8")
            parsed = _parse_events(stdout)
            elapsed_ms = max(0, int((time.monotonic() - start) * 1000))
            result = _classify_run(
                task,
                snapshot.root,
                parsed,
                paths,
                elapsed_ms,
                exit_code,
                timed_out,
            )
        except OSError as exc:
            elapsed_ms = max(0, int((time.monotonic() - start) * 1000))
            paths["events"].write_text(stdout, encoding="utf-8")
            paths["stderr"].write_text(str(exc), encoding="utf-8")
            result = AgentRun(
                outcome="preflight_failure",
                classification="harness_failure",
                failure_reason="product_error",
                answer=None,
                verification=VerificationResult(False, (f"runner: {exc}",), ()),
                command_manifest_path=paths["manifest"],
                codex_events_path=paths["events"],
                proxy_events_path=paths["proxy"],
                stderr_path=paths["stderr"],
                diagnostics=parsed.diagnostics,
                model_input_tokens=parsed.model_input_tokens,
                model_output_tokens=parsed.model_output_tokens,
                wall_clock_ms=elapsed_ms,
                exit_code=exit_code,
                child_home_removed=False,
            )
        finally:
            if process is not None and process.poll() is None:
                _terminate_process_tree(process)
            shutil.rmtree(execution_root, ignore_errors=True)
        return AgentRun(
            outcome=result.outcome,
            classification=result.classification,
            failure_reason=result.failure_reason,
            answer=result.answer,
            verification=result.verification,
            command_manifest_path=result.command_manifest_path,
            codex_events_path=result.codex_events_path,
            proxy_events_path=result.proxy_events_path,
            stderr_path=result.stderr_path,
            diagnostics=result.diagnostics,
            model_input_tokens=result.model_input_tokens,
            model_output_tokens=result.model_output_tokens,
            wall_clock_ms=result.wall_clock_ms,
            exit_code=result.exit_code,
            child_home_removed=not child_home.exists(),
        )

    def _preflight(self, task: BenchmarkTask, snapshot: AgentSnapshot) -> VerificationResult:
        failures: list[str] = []
        if task.snapshot_id != snapshot.identity.snapshot_id:
            failures.append("runner: task and snapshot ids differ")
        if task.repo_id != snapshot.identity.repo_id:
            failures.append("runner: task and snapshot repositories differ")
        failures.extend(snapshot.identity.verify_prepared_root(snapshot.root).failures)
        ordered = tuple(dict.fromkeys(failures))
        return VerificationResult(not ordered, ordered, ())

    def _copy_auth(self, child_home: Path) -> None:
        source = self._source_codex_home / "auth.json"
        if not source.is_file():
            return
        target = child_home / "auth.json"
        shutil.copyfile(source, target)
        target.chmod(0o600)

    def _command(
        self,
        arm: AgentArm,
        snapshot_root: Path,
        working_dir: Path,
        schema_path: Path,
        proxy_events_path: Path,
    ) -> list[str]:
        proxy_args = [
            *self._proxy_command[1:],
            "--events",
            str(proxy_events_path),
            "--tokenizer",
            "o200k_base",
            "--max-calls",
            "8",
            "--max-output-tokens",
            "12000",
            "--cwd",
            str(snapshot_root.resolve()),
        ]
        for name, value in arm.product_environment:
            proxy_args.extend(("--product-env", f"{name}={value}"))
        proxy_args.extend(("--", *arm.product_command))
        configs = [
            'model_reasoning_effort="medium"',
            'approval_policy="never"',
            f"mcp_servers.benchmark.command={_toml_value(self._proxy_command[0])}",
            f"mcp_servers.benchmark.args={_toml_value(proxy_args)}",
            f"mcp_servers.benchmark.cwd={_toml_value(str(working_dir))}",
            'mcp_servers.benchmark.default_tools_approval_mode="approve"',
            "mcp_servers.benchmark.required=true",
            "mcp_servers.benchmark.startup_timeout_sec=30",
            "mcp_servers.benchmark.tool_timeout_sec=120",
        ]
        command = [
            self._codex_executable,
            "exec",
            "--json",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "--strict-config",
            "--output-schema",
            str(schema_path),
            "--sandbox",
            "read-only",
            "--cd",
            str(working_dir),
            "--skip-git-repo-check",
            "--model",
            "gpt-5.6-sol",
        ]
        for config in configs:
            command.extend(("-c", config))
        command.append("-")
        return command


def _artifact_paths(output: Path) -> dict[str, Path]:
    return {
        "manifest": output / "command.json",
        "events": output / "codex-events.jsonl",
        "proxy": output / "proxy-events.jsonl",
        "stderr": output / "codex-stderr.txt",
    }


def _clear_previous_artifacts(paths: Iterable[Path]) -> None:
    for path in paths:
        if path.exists():
            path.unlink()


def _prompt(task: BenchmarkTask) -> str:
    return (
        "Use only the benchmark MCP server to answer this task. Do not execute commands, read files "
        "or the filesystem directly, modify files, access the web, or use any other tool or MCP server. "
        "Return only the requested structured JSON answer.\n\n"
        f"Task: {task.prompt}\n"
    )


def _isolated_environment(child_home: Path, execution_root: Path) -> dict[str, str]:
    environment = {
        name: value
        for name, value in os.environ.items()
        if name in _ALLOWED_ENVIRONMENT
    }
    environment["CODEX_HOME"] = str(child_home)
    environment["HOME"] = str(child_home)
    environment["TMPDIR"] = str(execution_root)
    return environment


def _write_manifest(
    path: Path,
    command: Sequence[str],
    working_dir: Path,
    environment: Mapping[str, str],
    prompt: str,
    child_home: Path,
) -> None:
    value = {
        "schema_version": 1,
        "argv": list(command),
        "cwd": str(working_dir),
        "environment_keys": sorted(environment),
        "child_home_mode": oct(child_home.stat().st_mode & 0o777),
        "auth_file_present": (child_home / "auth.json").is_file(),
        "prompt_sha256": hashlib.sha256(prompt.encode("utf-8")).hexdigest(),
    }
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def _toml_value(value: str | Sequence[str]) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _start_process(
    command: Sequence[str],
    working_dir: Path,
    environment: Mapping[str, str],
) -> subprocess.Popen[str]:
    options: dict[str, Any] = {}
    if os.name == "nt":
        options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        options["start_new_session"] = True
    return subprocess.Popen(
        list(command),
        cwd=working_dir,
        env=dict(environment),
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        **options,
    )


def _terminate_process_tree(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return
    if os.name == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
            capture_output=True,
            check=False,
        )
        process.wait(timeout=2)
        return
    try:
        os.killpg(process.pid, signal.SIGTERM)
        process.wait(timeout=1)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=2)
    except ProcessLookupError:
        process.wait(timeout=2)


def _stream_text(value: bytes | str | None) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace")
    return value


def _parse_events(text: str) -> _ParsedEvents:
    diagnostics: list[Mapping[str, Any]] = []
    answer: StructuredAnswer | None = None
    answer_error: str | None = None
    disallowed: str | None = None
    malformed: str | None = None
    turn_completed = False
    turn_failed = False
    model_input: int | None = None
    model_output: int | None = None
    for line_number, line in enumerate(text.splitlines(), start=1):
        if not line.strip():
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError as exc:
            malformed = f"codex JSONL line {line_number}: {exc.msg}"
            diagnostics.append({"kind": "malformed_event", "line": line_number})
            continue
        if not isinstance(event, dict) or not isinstance(event.get("type"), str):
            malformed = f"codex JSONL line {line_number}: event must have a string type"
            diagnostics.append({"kind": "malformed_event", "line": line_number, "event": event})
            continue
        event_type = event["type"]
        if event_type == "turn.completed":
            turn_completed = True
            usage = event.get("usage")
            if isinstance(usage, dict):
                model_input = _optional_nonnegative_int(usage.get("input_tokens"))
                model_output = _optional_nonnegative_int(usage.get("output_tokens"))
        elif event_type == "turn.failed":
            turn_failed = True
            diagnostics.append({"kind": "turn_failed", "event": event})
        elif event_type == "error":
            diagnostics.append({"kind": "codex_error", "event": event})
        elif event_type.startswith("item."):
            item = event.get("item")
            if not isinstance(item, dict) or not isinstance(item.get("type"), str):
                malformed = f"codex JSONL line {line_number}: item event is malformed"
                diagnostics.append({"kind": "malformed_item", "event": event})
                continue
            item_type = item["type"]
            if item_type == "mcp_tool_call":
                if not _is_benchmark_mcp(item):
                    disallowed = f"non-benchmark MCP item: {_item_label(item)}"
            elif item_type not in _ALLOWED_ITEM_TYPES:
                disallowed = f"disallowed Codex item type: {item_type}"
            if event_type == "item.completed" and item_type == "agent_message":
                text_value = item.get("text")
                if not isinstance(text_value, str):
                    answer_error = "final agent message must contain text"
                else:
                    try:
                        value = json.loads(text_value)
                        if not isinstance(value, dict):
                            raise ValueError("final answer must be a JSON object")
                        answer = StructuredAnswer.from_mapping(value)
                        answer_error = None
                    except (json.JSONDecodeError, ValueError) as exc:
                        answer = None
                        answer_error = str(exc)
        elif not (event_type.startswith("thread.") or event_type.startswith("turn.")):
            diagnostics.append({"kind": "unknown_event", "event": event})
    return _ParsedEvents(
        answer=answer,
        answer_error=answer_error,
        diagnostics=tuple(diagnostics),
        disallowed_item=disallowed,
        malformed=malformed,
        turn_completed=turn_completed,
        turn_failed=turn_failed,
        model_input_tokens=model_input,
        model_output_tokens=model_output,
    )


def _is_benchmark_mcp(item: Mapping[str, Any]) -> bool:
    server = item.get("server", item.get("server_name", item.get("mcp_server")))
    if server == "benchmark":
        return True
    tool = item.get("tool", item.get("tool_name", ""))
    return isinstance(tool, str) and (
        tool.startswith("benchmark.") or tool.startswith("benchmark__")
    )


def _item_label(item: Mapping[str, Any]) -> str:
    return str(item.get("tool", item.get("tool_name", item.get("type", "unknown"))))


def _optional_nonnegative_int(value: Any) -> int | None:
    return value if isinstance(value, int) and value >= 0 else None


def _classify_run(
    task: BenchmarkTask,
    snapshot_root: Path,
    parsed: _ParsedEvents,
    paths: Mapping[str, Path],
    wall_clock_ms: int,
    exit_code: int | None,
    timed_out: bool,
) -> AgentRun:
    verification = VerificationResult(False, ("runner: no valid answer",), ())
    outcome = "failed"
    classification = "harness_failure"
    failure_reason: str | None = "product_error"
    answer: StructuredAnswer | None = None
    if timed_out:
        outcome = "timeout"
        classification = "product_failure"
    elif parsed.disallowed_item:
        outcome = "disallowed_tool"
        failure_reason = "disallowed_tool"
        verification = VerificationResult(False, (parsed.disallowed_item,), ())
    elif parsed.malformed:
        verification = VerificationResult(False, (parsed.malformed,), ())
    elif authentication_failure := _authentication_failure(parsed.diagnostics):
        outcome = "preflight_failure"
        verification = VerificationResult(False, (authentication_failure,), ())
    elif exit_code != 0 or parsed.turn_failed:
        if _proxy_reports_product_failure(paths["proxy"]):
            classification = "product_failure"
        detail = f"codex exited with status {exit_code}" if exit_code is not None else "codex failed"
        verification = VerificationResult(False, (detail,), ())
    elif not parsed.turn_completed:
        verification = VerificationResult(False, ("codex JSONL ended before turn.completed",), ())
    elif parsed.answer is None:
        outcome = "invalid_answer"
        failure_reason = "invalid_answer"
        detail = parsed.answer_error or "final structured answer was missing"
        verification = VerificationResult(False, (detail,), ())
    else:
        answer = parsed.answer
        verification = verify_answer(task, answer, snapshot_root)
        outcome = "completed"
        if verification.passed:
            classification = "valid"
            failure_reason = None
        else:
            classification = "agent_insufficiency"
            failure_reason = (
                "insufficient_evidence" if answer.status in {"not_found", "blocked"} else "incorrect"
            )
    return AgentRun(
        outcome=outcome,
        classification=classification,
        failure_reason=failure_reason,
        answer=answer,
        verification=verification,
        command_manifest_path=paths["manifest"],
        codex_events_path=paths["events"],
        proxy_events_path=paths["proxy"],
        stderr_path=paths["stderr"],
        diagnostics=parsed.diagnostics,
        model_input_tokens=parsed.model_input_tokens,
        model_output_tokens=parsed.model_output_tokens,
        wall_clock_ms=wall_clock_ms,
        exit_code=exit_code,
        child_home_removed=False,
    )


def _authentication_failure(diagnostics: Sequence[Mapping[str, Any]]) -> str | None:
    indicators = ("not logged in", "authentication failed", "unauthorized", "missing api key")
    for diagnostic in diagnostics:
        text = json.dumps(diagnostic, sort_keys=True).casefold()
        if any(indicator in text for indicator in indicators):
            return "runner preflight: isolated Codex home is not logged in"
    return None


def _proxy_reports_product_failure(path: Path) -> bool:
    if not path.is_file():
        return False
    try:
        for line in path.read_text(encoding="utf-8").splitlines():
            event = json.loads(line)
            if not isinstance(event, dict):
                return False
            if event.get("type") in {"process_exit", "product_exit"}:
                return_code = event.get("returncode", event.get("exit_code"))
                if isinstance(return_code, int) and return_code != 0:
                    return True
    except (OSError, json.JSONDecodeError):
        return False
    return False
