#!/usr/bin/env python3

import argparse
import hashlib
import importlib.metadata
import json
import os
import queue
import random
import re
import shlex
import signal
import subprocess
import sys
import threading
import time
from dataclasses import asdict
from pathlib import Path
from typing import Any, Mapping, Sequence

from benchlib.agent_contract import (
    BenchmarkTask,
    SnapshotIdentity,
    StructuredAnswer,
    count_tool_output_tokens,
    load_snapshot_manifest,
    load_task_manifest,
    validate_run_result,
)
from benchlib.agent_runner import AgentArm, AgentRun, AgentSnapshot, CodexAgentRunner, isolated_environment_keys


SCRIPT_ROOT = Path(__file__).resolve().parent
BENCHMARK_ROOT = SCRIPT_ROOT / "benchmarks" / "agent-efficiency"
PINNED_CODEX_VERSION = "codex-cli 0.145.0"
PINNED_MODEL = "gpt-5.6-sol"
PINNED_REASONING = "medium"
PINNED_TOKENIZER_VERSION = "0.13.0"
PINNED_TOKENIZER = "o200k_base"
ALLOWED_FAILURE_REASONS = {
    "incorrect",
    "insufficient_evidence",
    "budget_exceeded",
    "disallowed_tool",
    "product_error",
    "invalid_answer",
}
ALLOWED_PRODUCT_ENVIRONMENT = frozenset(
    {
        "HOME",
        "JULIE_EMBEDDING_CACHE_DIR",
        "JULIE_HOME",
        "MILLER_SEMANTIC",
    }
)


class PairedExecution:
    def __init__(self, miller_rows: list[dict[str, Any]], julie_rows: list[dict[str, Any]]):
        self.miller_rows = miller_rows
        self.julie_rows = julie_rows


class ResumedRun:
    def __init__(self, marker: Mapping[str, Any], raw: Mapping[str, Any]):
        self.classification = str(marker["classification"])
        self.outcome = str(marker["outcome"])
        self.failure_reason = marker.get("failure_reason")
        verification = raw["verification"]
        self.verification = SimpleVerification(
            bool(verification["passed"]),
            tuple(verification["failures"]),
            tuple(verification["matched_anchor_ids"]),
        )


class SimpleVerification:
    def __init__(self, passed: bool, failures: tuple[str, ...], matched_anchor_ids: tuple[str, ...]):
        self.passed = passed
        self.failures = failures
        self.matched_anchor_ids = matched_anchor_ids


def balanced_arm_orders(task_ids: Sequence[str], seed: int) -> dict[str, tuple[str, str]]:
    if len(set(task_ids)) != len(task_ids):
        raise ValueError("task ids must be unique")
    shuffled = list(task_ids)
    generator = random.Random(seed)
    generator.shuffle(shuffled)
    first_arm = generator.choice(("miller", "julie"))
    orders: dict[str, tuple[str, str]] = {}
    for index, task_id in enumerate(shuffled):
        arm = first_arm if index % 2 == 0 else _other_arm(first_arm)
        orders[task_id] = (arm, _other_arm(arm))
    return {task_id: orders[task_id] for task_id in task_ids}


def execute_paired_tasks(
    *,
    tasks: Sequence[BenchmarkTask],
    snapshots: Mapping[str, AgentSnapshot],
    arms: Mapping[str, AgentArm],
    runner: CodexAgentRunner,
    output_root: Path,
    seed: int,
    identity_sha256: str,
    void_ledger_path: Path | None = None,
    max_void_attempts: int = 3,
) -> PairedExecution:
    if set(arms) != {"miller", "julie"}:
        raise ValueError("paired execution requires exactly miller and julie arms")
    if max_void_attempts < 1:
        raise ValueError("max_void_attempts must be positive")
    output_root = Path(output_root)
    output_root.mkdir(parents=True, exist_ok=True)
    ledger = Path(void_ledger_path) if void_ledger_path else output_root.parent / "void-ledger.jsonl"
    orders = balanced_arm_orders([task.task_id for task in tasks], seed)
    order_path = output_root / "arm-order.json"
    order_value = {
        "schema_version": 1,
        "seed": seed,
        "identity_sha256": identity_sha256,
        "orders": {task_id: list(order) for task_id, order in orders.items()},
    }
    if order_path.exists():
        existing_order = json.loads(order_path.read_text(encoding="utf-8"))
        if existing_order != order_value:
            raise ValueError("raw run identity mismatch")
    else:
        with order_path.open("x", encoding="utf-8") as stream:
            stream.write(_pretty_json(order_value))
    rows = {"miller": [], "julie": []}

    for task in tasks:
        if task.snapshot_id not in snapshots:
            raise ValueError(f"task {task.task_id}: unknown snapshot {task.snapshot_id}")
        for pair_attempt in range(1, max_void_attempts + 1):
            attempt_rows: dict[str, list[dict[str, Any]]] = {"miller": [], "julie": []}
            void_reasons: list[dict[str, Any]] = []
            first = _run_repetition(
                task,
                snapshots[task.snapshot_id],
                arms,
                runner,
                output_root,
                orders[task.task_id],
                pair_attempt,
                1,
                identity_sha256,
            )
            for arm_name, raw, scorer in first:
                attempt_rows[arm_name].append(scorer)
                if raw.classification == "harness_failure":
                    void_reasons.append(_void_reason(arm_name, raw))

            if not void_reasons:
                initial = {arm_name: scorer["completed"] for arm_name, _, scorer in first}
                if initial["miller"] != initial["julie"]:
                    for repetition in (2, 3):
                        reruns = _run_repetition(
                            task,
                            snapshots[task.snapshot_id],
                            arms,
                            runner,
                            output_root,
                            orders[task.task_id],
                            pair_attempt,
                            repetition,
                            identity_sha256,
                        )
                        for arm_name, raw, scorer in reruns:
                            attempt_rows[arm_name].append(scorer)
                            if raw.classification == "harness_failure":
                                void_reasons.append(_void_reason(arm_name, raw))
                        if void_reasons:
                            break

            if void_reasons:
                _append_void_ledger(
                    ledger,
                    {
                        "schema_version": 1,
                        "task_id": task.task_id,
                        "pair_attempt": pair_attempt,
                        "reasons": void_reasons,
                    },
                )
                continue

            rows["miller"].extend(attempt_rows["miller"])
            rows["julie"].extend(attempt_rows["julie"])
            break
        else:
            raise RuntimeError(f"task {task.task_id}: harness void persisted for {max_void_attempts} pairs")

    return PairedExecution(rows["miller"], rows["julie"])


def _run_repetition(
    task: BenchmarkTask,
    snapshot: AgentSnapshot,
    arms: Mapping[str, AgentArm],
    runner: CodexAgentRunner,
    output_root: Path,
    order: tuple[str, str],
    pair_attempt: int,
    repetition: int,
    identity_sha256: str,
) -> list[tuple[str, AgentRun, dict[str, Any]]]:
    completed: list[tuple[str, AgentRun, dict[str, Any]]] = []
    for arm_name in order:
        run_dir = (
            output_root
            / task.task_id
            / f"pair-{pair_attempt:02d}"
            / f"repetition-{repetition}"
            / arm_name
        )
        if run_dir.exists():
            run, raw = _resume_run(run_dir, task, arm_name, repetition, pair_attempt, identity_sha256)
            completed.append((arm_name, run, _scorer_row(raw, repetition)))
            continue
        run = runner.run(task, arms[arm_name], snapshot, run_dir)
        raw = _raw_result(task, arm_name, repetition, pair_attempt, run)
        validate_run_result(raw)
        summary = run_dir / "run-result.json"
        summary.write_text(_pretty_json(raw), encoding="utf-8")
        artifact_names = sorted(
            path.name for path in run_dir.iterdir() if path.is_file() and path.name != "COMPLETE.json"
        )
        marker = {
            "schema_version": 1,
            "identity_sha256": identity_sha256,
            "classification": run.classification,
            "outcome": run.outcome,
            "failure_reason": run.failure_reason,
            "run_result_sha256": _sha256(summary),
            "artifacts": [
                {"path": name, "bytes": (run_dir / name).stat().st_size, "sha256": _sha256(run_dir / name)}
                for name in artifact_names
            ],
        }
        (run_dir / "COMPLETE.json").write_text(_pretty_json(marker), encoding="utf-8")
        completed.append((arm_name, run, _scorer_row(raw, repetition)))
    return completed


def _resume_run(
    run_dir: Path,
    task: BenchmarkTask,
    arm_name: str,
    repetition: int,
    pair_attempt: int,
    identity_sha256: str,
) -> tuple[ResumedRun, dict[str, Any]]:
    marker_path = run_dir / "COMPLETE.json"
    result_path = run_dir / "run-result.json"
    if not marker_path.is_file() or not result_path.is_file():
        raise ValueError(f"partial run directory cannot be resumed: {run_dir}")
    marker = json.loads(marker_path.read_text(encoding="utf-8"))
    if marker.get("identity_sha256") != identity_sha256:
        raise ValueError(f"run identity mismatch: {run_dir}")
    artifacts = marker.get("artifacts")
    if not isinstance(artifacts, list) or not artifacts:
        raise ValueError(f"run evidence manifest is invalid: {run_dir}")
    for artifact in artifacts:
        relative = Path(str(artifact.get("path", ""))) if isinstance(artifact, dict) else Path()
        if relative.is_absolute() or len(relative.parts) != 1:
            raise ValueError(f"run artifact path is unsafe: {run_dir}")
        path = run_dir / relative
        if (
            not path.is_file()
            or path.stat().st_size != artifact.get("bytes")
            or _sha256(path) != artifact.get("sha256")
        ):
            raise ValueError(f"run artifact hash mismatch: {path}")
    if _sha256(result_path) != marker.get("run_result_sha256"):
        raise ValueError(f"run result hash mismatch: {run_dir}")
    raw = json.loads(result_path.read_text(encoding="utf-8"))
    validate_run_result(raw)
    expected_run_id = f"{task.task_id}.{arm_name}.p{pair_attempt}.r{repetition}"
    if (
        raw.get("run_id") != expected_run_id
        or raw.get("task_id") != task.task_id
        or raw.get("snapshot_id") != task.snapshot_id
        or raw.get("product") != arm_name
    ):
        raise ValueError(f"run identity mismatch: {run_dir}")
    for key in ("classification", "outcome"):
        if not isinstance(marker.get(key), str) or not marker[key]:
            raise ValueError(f"run completion marker is invalid: {run_dir}")
    return ResumedRun(marker, raw), raw


def _raw_result(
    task: BenchmarkTask,
    arm_name: str,
    repetition: int,
    pair_attempt: int,
    run: AgentRun,
) -> dict[str, Any]:
    events = _load_jsonl(run.proxy_events_path)
    calls = _tool_calls(events)
    output_tokens = sum(int(event.get("output_tokens", 0)) for event in events if event.get("event") in {"tool_result", "tool_error"})
    output_bytes = sum(int(event.get("output_bytes", 0)) for event in events if event.get("event") in {"tool_result", "tool_error"})
    failure_reason = run.failure_reason
    status = run.outcome if run.outcome in {"completed", "timeout", "failed", "invalid_answer"} else "failed"
    answer = _answer_mapping(run.answer)
    budget_exceeded = any(
        event.get("event") == "tool_call_rejected" and event.get("budget") in {"tool_calls", "tool_output_tokens"}
        or event.get("event") == "budget_transition"
        and event.get("budget") == "tool_output_tokens"
        and int(event.get("used", 0)) > int(event.get("limit", 0))
        for event in events
    )
    if budget_exceeded:
        status = "budget_exceeded"
        failure_reason = "budget_exceeded"
        answer = None
    elif run.failure_reason == "disallowed_tool" or run.outcome == "disallowed_tool":
        status = "disallowed_tool"
        failure_reason = "disallowed_tool"
        answer = None
    elif status == "completed" and not run.verification.passed:
        failure_reason = failure_reason or "incorrect"
    elif status == "completed":
        failure_reason = None
    elif status == "invalid_answer":
        failure_reason = "invalid_answer"
        answer = None
    elif status in {"timeout", "failed"}:
        failure_reason = "product_error"
        answer = None

    if failure_reason not in ALLOWED_FAILURE_REASONS and failure_reason is not None:
        failure_reason = "product_error"
    product_errors = [
        str(event.get("error") or event.get("reason") or event.get("event"))
        for event in events
        if event.get("event") in {"tool_error", "proxy_failure"}
    ]
    if run.classification == "product_failure" and not product_errors:
        product_errors.append("product process failed")
    return {
        "schema_version": 1,
        "run_id": f"{task.task_id}.{arm_name}.p{pair_attempt}.r{repetition}",
        "task_id": task.task_id,
        "snapshot_id": task.snapshot_id,
        "product": arm_name,
        "status": status,
        "failure_reason": failure_reason,
        "answer": answer,
        "tool_calls": calls,
        "tool_call_count": len(calls),
        "tool_output_bytes": output_bytes,
        "tool_output_tokens": output_tokens,
        "model_input_tokens": run.model_input_tokens,
        "model_output_tokens": run.model_output_tokens,
        "product_errors": product_errors,
        "duplicate_calls": _duplicate_calls(events),
        "uncited_tool_output_tokens": _uncited_tokens(events, run.answer),
        "wall_clock_ms": run.wall_clock_ms,
        "verification": {
            "passed": bool(run.verification.passed and not budget_exceeded and status == "completed"),
            "failures": list(run.verification.failures),
            "matched_anchor_ids": list(run.verification.matched_anchor_ids),
        },
    }


def _tool_calls(events: Sequence[Mapping[str, Any]]) -> list[dict[str, Any]]:
    results = {
        event.get("call_number"): event
        for event in events
        if event.get("event") in {"tool_result", "tool_error"}
    }
    calls = []
    for event in events:
        if event.get("event") != "tool_call":
            continue
        number = int(event.get("call_number", len(calls) + 1))
        result = results.get(number, {})
        output = result.get("result")
        calls.append(
            {
                "sequence": number,
                "tool": str(event.get("name") or "unknown"),
                "arguments": event.get("arguments") if isinstance(event.get("arguments"), dict) else {},
                "output": json.dumps(output, ensure_ascii=False, sort_keys=True) if output is not None else "",
                "duration_ms": max(0, int(result.get("duration_ns", 0)) // 1_000_000),
                "error": str(result.get("error")) if result.get("error") is not None else None,
            }
        )
    return calls


def _duplicate_calls(events: Sequence[Mapping[str, Any]]) -> int:
    seen: set[str] = set()
    duplicates = 0
    for event in events:
        if event.get("event") != "tool_call":
            continue
        key = json.dumps(
            [event.get("name"), event.get("arguments")], ensure_ascii=False, sort_keys=True, separators=(",", ":")
        )
        if key in seen:
            duplicates += 1
        seen.add(key)
    return duplicates


def _uncited_tokens(events: Sequence[Mapping[str, Any]], answer: StructuredAnswer | None) -> int:
    citations = []
    if answer is not None:
        for evidence in answer.evidence:
            citations.extend(value for value in (evidence.path, evidence.symbol) if value)
    total = 0
    for event in events:
        if event.get("event") not in {"tool_result", "tool_error"}:
            continue
        output = json.dumps(event.get("result"), ensure_ascii=False, sort_keys=True)
        if not citations or not any(citation in output for citation in citations):
            total += int(event.get("output_tokens", 0))
    return total


def _answer_mapping(answer: StructuredAnswer | None) -> dict[str, Any] | None:
    if answer is None:
        return None
    evidence = []
    for item in answer.evidence:
        value = {"path": item.path, "claim": item.claim}
        if item.symbol is not None:
            value["symbol"] = item.symbol
        if item.line is not None:
            value["line"] = item.line
        evidence.append(value)
    return {"status": answer.status, "answer": answer.answer, "evidence": evidence}


def _scorer_row(raw: Mapping[str, Any], repetition: int) -> dict[str, Any]:
    completed = bool(raw["verification"]["passed"])
    return {
        "task_id": raw["task_id"],
        "repetition": repetition,
        "completed": completed,
        "failure_reason": None if completed else raw["failure_reason"],
        "duration_ms": raw["wall_clock_ms"],
        "tool_calls": raw["tool_call_count"],
        "tool_output_bytes": raw["tool_output_bytes"],
        "tool_output_tokens": raw["tool_output_tokens"],
        "model_input_tokens": raw["model_input_tokens"] or 0,
        "model_output_tokens": raw["model_output_tokens"] or 0,
        "product_errors": len(raw["product_errors"]),
        "duplicate_calls": raw["duplicate_calls"],
        "uncited_tool_output_tokens": raw["uncited_tool_output_tokens"],
    }


def empty_scorer_row(task_id: str, repetition: int, completed: bool) -> dict[str, Any]:
    return {
        "task_id": task_id,
        "repetition": repetition,
        "completed": completed,
        "failure_reason": None if completed else "incorrect",
        "duration_ms": 0,
        "tool_calls": 0,
        "tool_output_bytes": 0,
        "tool_output_tokens": 0,
        "model_input_tokens": 0,
        "model_output_tokens": 0,
        "product_errors": 0,
        "duplicate_calls": 0,
        "uncited_tool_output_tokens": 0,
    }


def completed_export_matches(exports: Path, identity_sha256: str) -> bool:
    exports = Path(exports)
    manifest_path = exports / "identity-manifest.json"
    evidence_path = exports / "evidence-manifest.json"
    complete_path = exports / "COMPLETE"
    if not exports.exists():
        return False
    if not manifest_path.exists() or not evidence_path.exists() or not complete_path.exists():
        raise ValueError("partial benchmark output cannot be resumed")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    recorded = manifest.get("run_identity_sha256")
    marker = complete_path.read_text(encoding="utf-8").strip()
    if recorded != identity_sha256 or marker != identity_sha256:
        raise ValueError("completed benchmark identity mismatch")
    evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
    artifacts = evidence.get("artifacts") if isinstance(evidence, dict) else None
    if not isinstance(artifacts, list) or not artifacts:
        raise ValueError("completed benchmark evidence manifest is invalid")
    for artifact in artifacts:
        if not isinstance(artifact, dict) or set(artifact) != {"path", "sha256", "bytes"}:
            raise ValueError("completed benchmark evidence manifest is invalid")
        relative = Path(str(artifact["path"]))
        if relative.is_absolute() or len(relative.parts) != 1 or relative.name in {".", ".."}:
            raise ValueError("completed benchmark artifact path is unsafe")
        path = exports / relative
        if (
            not path.is_file()
            or path.stat().st_size != artifact["bytes"]
            or _sha256(path) != artifact["sha256"]
        ):
            raise ValueError(f"completed benchmark artifact hash mismatch: {relative}")
    return True


def export_scorer_artifacts(
    exports: Path,
    tasks: Sequence[BenchmarkTask],
    execution: PairedExecution,
    identity_manifest: Mapping[str, Any],
) -> None:
    exports = Path(exports)
    if exports.exists() and any(exports.iterdir()):
        if completed_export_matches(exports, str(identity_manifest["run_identity_sha256"])):
            return
    exports.mkdir(parents=True, exist_ok=True)
    task_rows = [
        {
            "task_id": task.task_id,
            "repo": task.repo_id,
            "language": task.language,
            "workflow_class": task.workflow_class,
            "evidence_critical": task.evidence_critical,
        }
        for task in tasks
    ]
    _write_jsonl(exports / "agent-tasks.jsonl", task_rows)
    _write_jsonl(exports / "miller-results.jsonl", execution.miller_rows)
    _write_jsonl(exports / "julie-results.jsonl", execution.julie_rows)
    (exports / "identity-manifest.json").write_text(_pretty_json(dict(identity_manifest)), encoding="utf-8")
    command = (
        'dotnet run --project eval/retrieval-eval/RetrievalEval.csproj -- agent-score '
        '--tasks "$AGENT_EFFICIENCY_EXPORT/agent-tasks.jsonl" '
        '--miller "$AGENT_EFFICIENCY_EXPORT/miller-results.jsonl" '
        '--julie "$AGENT_EFFICIENCY_EXPORT/julie-results.jsonl" '
        '--out "$AGENT_EFFICIENCY_EXPORT/aggregate.json"\n'
    )
    (exports / "agent-score-command.txt").write_text(command, encoding="utf-8")
    artifact_names = [
        "agent-tasks.jsonl",
        "miller-results.jsonl",
        "julie-results.jsonl",
        "identity-manifest.json",
        "agent-score-command.txt",
    ]
    evidence = {
        "schema_version": 1,
        "artifacts": [
            {"path": name, "sha256": _sha256(exports / name), "bytes": (exports / name).stat().st_size}
            for name in artifact_names
        ],
    }
    (exports / "evidence-manifest.json").write_text(_pretty_json(evidence), encoding="utf-8")
    (exports / "COMPLETE").write_text(str(identity_manifest["run_identity_sha256"]) + "\n", encoding="utf-8")


def _other_arm(arm: str) -> str:
    return "julie" if arm == "miller" else "miller"


def _void_reason(arm_name: str, run: AgentRun) -> dict[str, Any]:
    return {
        "arm": arm_name,
        "outcome": run.outcome,
        "failure_reason": run.failure_reason,
        "failures": list(run.verification.failures),
    }


def _append_jsonl(path: Path, value: Mapping[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    flags = os.O_APPEND | os.O_CREAT | os.O_WRONLY
    descriptor = os.open(path, flags, 0o600)
    try:
        data = (json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n").encode()
        written = os.write(descriptor, data)
        if written != len(data):
            raise OSError(f"partial ledger write: {written} of {len(data)} bytes")
    finally:
        os.close(descriptor)


def _append_void_ledger(path: Path, value: Mapping[str, Any]) -> None:
    existing = _load_jsonl(path)
    matching = [
        row
        for row in existing
        if row.get("task_id") == value.get("task_id")
        and row.get("pair_attempt") == value.get("pair_attempt")
    ]
    if matching:
        if len(matching) == 1 and matching[0] == value:
            return
        raise ValueError(
            f"conflicting void ledger row for {value.get('task_id')} pair {value.get('pair_attempt')}"
        )
    _append_jsonl(path, value)


def _write_jsonl(path: Path, values: Sequence[Mapping[str, Any]]) -> None:
    text = "".join(json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n" for value in values)
    path.write_text(text, encoding="utf-8")


def _load_jsonl(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    values = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            value = json.loads(line)
            if isinstance(value, dict):
                values.append(value)
    return values


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _pretty_json(value: Mapping[str, Any]) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n"


def _json_sha256(value: Any) -> str:
    return hashlib.sha256(
        json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Run the frozen paired Miller/Julie agent-efficiency benchmark")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--snapshots", required=True)
    parser.add_argument("--snapshot-root", action="append", default=[], metavar="REPO=DIR")
    parser.add_argument("--arm", choices=("miller", "julie", "both"), required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--seed", required=True, type=int)
    parser.add_argument("--model", default=PINNED_MODEL)
    parser.add_argument("--reasoning", default=PINNED_REASONING)
    parser.add_argument("--runtime-identity", required=True)
    parser.add_argument("--codex", default="codex")
    parser.add_argument("--codex-home", default=str(Path.home() / ".codex"))
    parser.add_argument("--preflight-only", action="store_true")
    args = parser.parse_args(argv)
    try:
        return _run_cli(args)
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as exc:
        print(str(exc), file=sys.stderr)
        return 2


def _run_cli(args: argparse.Namespace) -> int:
    if args.arm != "both":
        raise ValueError("decision runs require --arm both")
    tasks = load_task_manifest(args.manifest)
    snapshots = load_snapshot_manifest(args.snapshots)
    roots = _snapshot_roots(args.snapshot_root)
    runtime = json.loads(Path(args.runtime_identity).read_text(encoding="utf-8"))
    identity, arms, agent_snapshots = preflight_run(
        tasks=tasks,
        snapshots=snapshots,
        roots=roots,
        runtime=runtime,
        codex_executable=args.codex,
        model=args.model,
        reasoning=args.reasoning,
        seed=args.seed,
    )
    out = Path(args.out)
    exports = out / "exports"
    identity_hash = str(identity["run_identity_sha256"])
    if completed_export_matches(exports, identity_hash):
        return 0
    if args.preflight_only:
        print(_pretty_json(identity), end="")
        return 0
    proxy = (sys.executable, str(SCRIPT_ROOT / "benchlib" / "recording_mcp_proxy.py"))
    runner = CodexAgentRunner(args.codex, proxy, args.codex_home)
    execution = execute_paired_tasks(
        tasks=tasks,
        snapshots=agent_snapshots,
        arms=arms,
        runner=runner,
        output_root=out / "raw",
        seed=args.seed,
        identity_sha256=identity_hash,
        void_ledger_path=out / "void-ledger.jsonl",
    )
    export_scorer_artifacts(exports, tasks, execution, identity)
    print((exports / "agent-score-command.txt").read_text(encoding="utf-8"), end="")
    return 0


def preflight_run(
    *,
    tasks: Sequence[BenchmarkTask],
    snapshots: Sequence[SnapshotIdentity],
    roots: Mapping[str, Path],
    runtime: Mapping[str, Any],
    codex_executable: str,
    model: str,
    reasoning: str,
    seed: int,
) -> tuple[dict[str, Any], dict[str, AgentArm], dict[str, AgentSnapshot]]:
    if runtime.get("schema_version") != 1:
        raise ValueError("runtime identity schema_version must be 1")
    if model != PINNED_MODEL or reasoning != PINNED_REASONING:
        raise ValueError(f"model identity must be {PINNED_MODEL} with reasoning {PINNED_REASONING}")
    codex_path = _resolve_executable(codex_executable)
    codex_version = _command_output((str(codex_path), "--version")).strip()
    if codex_version != PINNED_CODEX_VERSION:
        raise ValueError(f"unsupported Codex version: {codex_version}")
    tokenizer_version = importlib.metadata.version("tiktoken")
    if tokenizer_version != PINNED_TOKENIZER_VERSION:
        raise ValueError(f"unsupported tiktoken version: {tokenizer_version}")
    count_tool_output_tokens("preflight")
    snapshot_by_id = {snapshot.snapshot_id: snapshot for snapshot in snapshots}
    if len(snapshot_by_id) != len(snapshots):
        raise ValueError("snapshot ids must be unique")
    agents: dict[str, AgentSnapshot] = {}
    for snapshot in snapshots:
        root = roots.get(snapshot.repo_id)
        if root is None:
            raise ValueError(f"missing --snapshot-root for {snapshot.repo_id}")
        verified = snapshot.verify_prepared_root(root)
        if not verified.passed:
            raise ValueError("; ".join(verified.failures))
        agents[snapshot.snapshot_id] = AgentSnapshot(snapshot, root)
    for task in tasks:
        if task.snapshot_id not in snapshot_by_id:
            raise ValueError(f"task {task.task_id}: unknown snapshot {task.snapshot_id}")

    products = runtime.get("products")
    if not isinstance(products, dict) or set(products) != {"miller", "julie"}:
        raise ValueError("runtime identity must contain exactly miller and julie products")
    arms: dict[str, AgentArm] = {}
    safe_products: dict[str, Any] = {}
    for product in ("miller", "julie"):
        spec = products[product]
        if not isinstance(spec, dict):
            raise ValueError(f"{product}: identity must be an object")
        expected_product_fields = {
            "command",
            "version_command",
            "version",
            "binary_path",
            "binary_sha256",
            "commit",
            "readiness_commands",
            "readiness",
            "environment",
        }
        if set(spec) != expected_product_fields:
            raise ValueError(f"{product}: identity fields must be {sorted(expected_product_fields)}")
        commit = spec.get("commit")
        if not isinstance(commit, str) or re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", commit) is None:
            raise ValueError(f"{product}: commit must be a full lowercase hexadecimal commit")
        command = _string_sequence(spec.get("command"), f"{product}.command")
        environment_value = spec.get("environment")
        if not isinstance(environment_value, dict) or any(
            name not in ALLOWED_PRODUCT_ENVIRONMENT
            or not isinstance(value, str)
            or not value
            or "\0" in value
            for name, value in environment_value.items()
        ):
            raise ValueError(
                f"{product}: environment must contain only {sorted(ALLOWED_PRODUCT_ENVIRONMENT)} with non-empty string values"
            )
        environment = tuple(sorted(environment_value.items()))
        _resolve_executable(command[0])
        binary_path = Path(spec.get("binary_path", "")).expanduser().resolve()
        if not binary_path.is_file():
            raise ValueError(f"{product}: binary_path must name a file")
        if _sha256(binary_path) != spec.get("binary_sha256"):
            raise ValueError(f"{product}: binary hash mismatch")
        version_command = _string_sequence(spec.get("version_command"), f"{product}.version_command")
        version = _command_output(tuple(version_command), environment=environment_value).strip()
        if version != spec.get("version"):
            raise ValueError(f"{product}: version mismatch")
        readiness_commands = spec.get("readiness_commands")
        expected_readiness = spec.get("readiness")
        expected_snapshot_ids = set(agents)
        if (
            not isinstance(readiness_commands, dict)
            or not isinstance(expected_readiness, dict)
            or set(readiness_commands) != expected_snapshot_ids
            or set(expected_readiness) != expected_snapshot_ids
        ):
            raise ValueError(f"{product}: readiness identities must cover every snapshot")
        safe_snapshot_identities: dict[str, Any] = {}
        for snapshot_id in sorted(expected_snapshot_ids):
            readiness_command = _string_sequence(
                readiness_commands[snapshot_id], f"{product}.readiness_commands.{snapshot_id}"
            )
            readiness = json.loads(
                _command_output(
                    tuple(readiness_command),
                    cwd=agents[snapshot_id].root,
                    environment=environment_value,
                )
            )
            expected = expected_readiness[snapshot_id]
            if readiness != expected or not isinstance(readiness, dict) or readiness.get("ready") is not True:
                raise ValueError(f"{product}: stale or incomplete readiness identity for {snapshot_id}")
            identity_values = {
                key: readiness.get(key)
                for key in ("workspace_identity", "index_identity", "vector_identity", "model_identity")
            }
            if any(not isinstance(value, str) or not value for value in identity_values.values()):
                raise ValueError(f"{product}: stale or incomplete readiness identity for {snapshot_id}")
            mcp = _probe_mcp(
                tuple(command),
                agents[snapshot_id].root,
                environment=environment_value,
            )
            safe_snapshot_identities[snapshot_id] = {
                **{
                    f"{key}_sha256": hashlib.sha256(value.encode()).hexdigest()
                    for key, value in identity_values.items()
                },
                "instructions_sha256": mcp["instructions_sha256"],
                "tools_sha256": mcp["tools_sha256"],
            }
        arms[product] = AgentArm(product, tuple(command), environment)
        safe_products[product] = {
            "version": version,
            "binary_sha256": spec["binary_sha256"],
            "commit": commit,
            "command_sha256": _json_sha256(command),
            "environment_keys": [name for name, _ in environment],
            "environment_sha256": _json_sha256(environment_value),
            "snapshots": safe_snapshot_identities,
        }

    schema_hashes = {
        name: _sha256(BENCHMARK_ROOT / name)
        for name in ("answer-schema.json", "run-result.schema.json", "task-manifest.schema.json", "snapshot-manifest.schema.json")
    }
    safe = {
        "schema_version": 1,
        "seed": seed,
        "model": model,
        "reasoning": reasoning,
        "codex": {"version": codex_version, "binary_sha256": _sha256(codex_path)},
        "tokenizer": {"package": "tiktoken", "version": tokenizer_version, "encoding": PINNED_TOKENIZER},
        "environment_keys": list(isolated_environment_keys()),
        "schemas": schema_hashes,
        "inputs": {
            "task_manifest_sha256": _json_sha256([asdict(task) for task in tasks]),
            "snapshot_manifest_sha256": _json_sha256([asdict(snapshot) for snapshot in snapshots]),
        },
        "snapshots": [
            {
                "snapshot_id": snapshot.snapshot_id,
                "repo_id": snapshot.repo_id,
                "commit": snapshot.commit,
                "content_sha256": snapshot.content_sha256,
                "languages": list(snapshot.languages),
            }
            for snapshot in snapshots
        ],
        "products": safe_products,
    }
    safe["run_identity_sha256"] = hashlib.sha256(
        json.dumps(safe, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()
    return safe, arms, agents


def _probe_mcp(
    command: tuple[str, ...],
    cwd: Path,
    timeout: float = 10,
    environment: Mapping[str, str] | None = None,
) -> dict[str, str]:
    process_options: dict[str, Any] = {}
    if os.name == "nt":
        process_options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        process_options["start_new_session"] = True
    process = subprocess.Popen(
        command,
        cwd=cwd,
        env={**os.environ, **dict(environment or {})},
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        **process_options,
    )
    try:
        initialize = {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "miller-agent-preflight", "version": "1"}},
        }
        first = _mcp_request(process, initialize, timeout)
        if first.get("error") is not None or not isinstance(first.get("result"), dict):
            raise ValueError("product MCP initialize failed")
        _mcp_notify(process, {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})
        listed = _mcp_request(process, {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}, timeout)
        tools = listed.get("result", {}).get("tools") if isinstance(listed.get("result"), dict) else None
        if listed.get("error") is not None or not isinstance(tools, list) or not tools:
            raise ValueError("product MCP tools/list failed")
        instructions = str(first["result"].get("instructions", ""))
        return {
            "instructions_sha256": hashlib.sha256(instructions.encode()).hexdigest(),
            "tools_sha256": hashlib.sha256(
                json.dumps(tools, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
            ).hexdigest(),
        }
    finally:
        _terminate_process_tree(process)
        if process.stdin:
            process.stdin.close()
        if process.stdout:
            process.stdout.close()
        if process.stderr:
            process.stderr.close()


def _mcp_request(process: subprocess.Popen[str], message: Mapping[str, Any], timeout: float) -> dict[str, Any]:
    _mcp_notify(process, message)
    if process.stdout is None:
        raise ValueError("product MCP stdout is unavailable")
    responses: queue.Queue[dict[str, Any] | BaseException] = queue.Queue(maxsize=1)

    def read_response() -> None:
        try:
            for line in process.stdout:
                value = json.loads(line)
                if isinstance(value, dict) and value.get("id") == message.get("id"):
                    responses.put(value)
                    return
            responses.put(ValueError(f"product MCP exited with status {process.poll()}"))
        except BaseException as exc:
            responses.put(exc)

    threading.Thread(target=read_response, name="agent-preflight-mcp", daemon=True).start()
    try:
        response = responses.get(timeout=timeout)
    except queue.Empty as exc:
        raise ValueError("product MCP preflight timed out") from exc
    if isinstance(response, BaseException):
        raise ValueError(f"product MCP preflight failed: {response}") from response
    return response


def _mcp_notify(process: subprocess.Popen[str], message: Mapping[str, Any]) -> None:
    if process.stdin is None:
        raise ValueError("product MCP stdin is unavailable")
    process.stdin.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
    process.stdin.flush()


def _command_output(
    command: tuple[str, ...],
    timeout: float = 10,
    cwd: Path | None = None,
    environment: Mapping[str, str] | None = None,
) -> str:
    process_options: dict[str, Any] = {}
    if os.name == "nt":
        process_options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        process_options["start_new_session"] = True
    process = subprocess.Popen(
        command,
        cwd=cwd,
        env={**os.environ, **dict(environment or {})},
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        **process_options,
    )
    try:
        try:
            stdout, _ = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired as exc:
            _terminate_process_tree(process)
            process.communicate()
            raise ValueError(f"identity command timed out: {shlex.join(command)}") from exc
    finally:
        _terminate_process_tree(process)
    if process.returncode != 0:
        raise ValueError(f"identity command failed: {shlex.join(command)}")
    return stdout


def _terminate_process_tree(process: subprocess.Popen[str]) -> None:
    if os.name == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
            capture_output=True,
            check=False,
        )
        if process.poll() is None:
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=2)
        return
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except (ProcessLookupError, PermissionError):
        pass
    deadline = time.monotonic() + 0.5
    while time.monotonic() < deadline:
        try:
            os.killpg(process.pid, 0)
        except (ProcessLookupError, PermissionError):
            break
        time.sleep(0.02)
    else:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass
    if process.poll() is None:
        try:
            process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=2)


def _resolve_executable(value: str) -> Path:
    candidate = Path(value)
    if candidate.is_file():
        return candidate.resolve()
    resolved = shutil_which(value)
    if resolved is None:
        raise ValueError(f"executable not found: {value}")
    return Path(resolved).resolve()


def shutil_which(value: str) -> str | None:
    for directory in os.environ.get("PATH", "").split(os.pathsep):
        candidate = Path(directory) / value
        if candidate.is_file() and os.access(candidate, os.X_OK):
            return str(candidate)
    return None


def _snapshot_roots(values: Sequence[str]) -> dict[str, Path]:
    roots = {}
    for value in values:
        repo, separator, path = value.partition("=")
        if not separator or not repo or not path or repo in roots:
            raise ValueError(f"invalid --snapshot-root: {value}")
        roots[repo] = Path(path).resolve()
    return roots


def _string_sequence(value: Any, name: str, allow_empty: bool = False) -> list[str]:
    if not isinstance(value, list) or (not value and not allow_empty) or any(not isinstance(item, str) or not item for item in value):
        raise ValueError(f"{name} must be a JSON array of non-empty strings")
    return list(value)


if __name__ == "__main__":
    raise SystemExit(main())
