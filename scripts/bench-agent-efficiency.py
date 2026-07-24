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
from dataclasses import asdict, dataclass
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
from benchlib.agent_runner import (
    AgentArm,
    AgentRun,
    AgentSnapshot,
    CodexAgentRunner,
    isolated_environment_keys,
    prompt_contract_sha256,
)
from benchlib.reporting import project_safe_aggregate


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
TAKEOVER_CONTRACT_ID = "takeover-evaluation-v1"
LEGACY_CALIBRATION_CONTRACT_ID = "agent-efficiency-legacy-calibration"
TAKEOVER_CAPABILITIES = frozenset(
    {
        "discovery",
        "exact_symbol_lookup",
        "homonym_disambiguation",
        "context_orientation",
        "callers",
        "callees",
        "call_path",
        "impact_tests",
        "edit",
        "rename",
        "logs",
        "patterns",
        "workspace_recovery",
    }
)
SAFE_AGGREGATE_FIELDS = frozenset(
    {
        "action_verdict",
        "baseline",
        "by_capability",
        "by_language",
        "by_repo",
        "by_workflow",
        "candidate",
        "completion",
        "contract_id",
        "correctness",
        "decision_scope",
        "decision_verdict",
        "efficiency",
        "failure_counts",
        "outcome_counts",
        "parent_manifest_sha256",
        "private_evidence_sha256",
        "relevance",
        "runtime_identity_sha256",
        "schema_version",
        "selection_sha256",
        "selected_capability_ids",
        "selected_task_count",
        "snapshot_manifest_sha256",
        "corpus_role",
        "unresolved_void_count",
    }
)


@dataclass(frozen=True)
class SelectionIdentity:
    tasks: tuple[BenchmarkTask, ...]
    contract_id: str
    schema_version: int
    corpus_role: str
    decision_scope: str
    parent_manifest_sha256: str
    snapshot_manifest_sha256: str
    selected_capability_ids: tuple[str, ...]
    selected_task_count: int
    selected_task_ids: tuple[str, ...]
    selected_task_ids_sha256: str
    selection_sha256: str

    def private_identity(self) -> dict[str, Any]:
        return {
            "contract_id": self.contract_id,
            "schema_version": self.schema_version,
            "corpus_role": self.corpus_role,
            "decision_scope": self.decision_scope,
            "parent_manifest_sha256": self.parent_manifest_sha256,
            "snapshot_manifest_sha256": self.snapshot_manifest_sha256,
            "selected_capability_ids": list(self.selected_capability_ids),
            "selected_task_count": self.selected_task_count,
            "selected_task_ids": list(self.selected_task_ids),
            "selected_task_ids_sha256": self.selected_task_ids_sha256,
            "selection_sha256": self.selection_sha256,
        }


def build_selection(
    *,
    tasks: Sequence[BenchmarkTask],
    snapshots: Sequence[SnapshotIdentity],
    parent_manifest_bytes: bytes,
    snapshot_manifest_bytes: bytes,
    corpus_role: str,
    decision_scope: str,
    capability_ids: Sequence[str],
) -> SelectionIdentity:
    if corpus_role not in {"calibration", "decision"}:
        raise ValueError("corpus_role must be calibration or decision")
    if decision_scope not in {"subset", "full"}:
        raise ValueError("decision_scope must be subset or full")
    if corpus_role == "decision" and decision_scope != "full":
        raise ValueError("decision corpus requires full scope")
    if any(task.contract_id != TAKEOVER_CONTRACT_ID for task in tasks):
        raise ValueError("takeover selection requires a v1 parent manifest")
    task_ids = [task.task_id for task in tasks]
    if len(task_ids) != len(set(task_ids)):
        raise ValueError("task ids must be unique")
    snapshot_ids = {snapshot.snapshot_id for snapshot in snapshots}
    if len(snapshot_ids) != len(snapshots):
        raise ValueError("snapshot ids must be unique")
    for task in tasks:
        if task.snapshot_id not in snapshot_ids:
            raise ValueError(f"task {task.task_id}: missing snapshot {task.snapshot_id}")
    parent_capabilities = {
        capability
        for task in tasks
        for capability in task.capabilities
    }
    if parent_capabilities != TAKEOVER_CAPABILITIES:
        raise ValueError("parent manifest must cover all 13 capabilities")
    selectors = tuple(capability_ids)
    if len(selectors) != len(set(selectors)):
        raise ValueError("duplicate capability selector")
    unknown = set(selectors) - TAKEOVER_CAPABILITIES
    if unknown:
        raise ValueError(f"unknown capability: {sorted(unknown)[0]}")
    if decision_scope == "full":
        if selectors:
            raise ValueError("full scope does not accept capability selectors")
        selected = tuple(tasks)
        selected_capabilities = tuple(sorted(TAKEOVER_CAPABILITIES))
    else:
        if not selectors:
            raise ValueError("subset scope requires at least one capability selector")
        selected_capabilities = tuple(sorted(selectors))
        selected = tuple(
            task
            for task in tasks
            if set(task.capabilities).intersection(selected_capabilities)
        )
        if not selected:
            raise ValueError("capability selection is empty")
    selected_task_ids = tuple(sorted(task.task_id for task in selected))
    selected_task_ids_sha256 = hashlib.sha256(
        ("".join(f"{task_id}\n" for task_id in selected_task_ids)).encode()
    ).hexdigest()
    payload = {
        "contract_id": TAKEOVER_CONTRACT_ID,
        "schema_version": 1,
        "corpus_role": corpus_role,
        "decision_scope": decision_scope,
        "parent_manifest_sha256": hashlib.sha256(parent_manifest_bytes).hexdigest(),
        "snapshot_manifest_sha256": hashlib.sha256(snapshot_manifest_bytes).hexdigest(),
        "selected_capability_ids": list(selected_capabilities),
        "selected_task_count": len(selected),
        "selected_task_ids_sha256": selected_task_ids_sha256,
    }
    selection_sha256 = hashlib.sha256(
        json.dumps(payload, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()
    return SelectionIdentity(
        tasks=selected,
        contract_id=TAKEOVER_CONTRACT_ID,
        schema_version=1,
        corpus_role=corpus_role,
        decision_scope=decision_scope,
        parent_manifest_sha256=payload["parent_manifest_sha256"],
        snapshot_manifest_sha256=payload["snapshot_manifest_sha256"],
        selected_capability_ids=selected_capabilities,
        selected_task_count=len(selected),
        selected_task_ids=selected_task_ids,
        selected_task_ids_sha256=selected_task_ids_sha256,
        selection_sha256=selection_sha256,
    )


def adapt_legacy_runtime(
    runtime: Mapping[str, Any],
    *,
    corpus_role: str,
    decision_scope: str,
) -> dict[str, Any]:
    if corpus_role != "calibration" or decision_scope != "subset":
        raise ValueError("legacy runtime is calibration subset evidence only")
    if set(runtime) != {"schema_version", "products"} or runtime.get("schema_version") != 1:
        raise ValueError("legacy runtime identity has an invalid shape")
    products = runtime.get("products")
    if not isinstance(products, dict) or set(products) != {"miller", "julie"}:
        raise ValueError("legacy runtime must contain exactly Miller and Julie products")
    return {
        "contract_id": LEGACY_CALIBRATION_CONTRACT_ID,
        "schema_version": 1,
        "corpus_role": "calibration",
        "decision_scope": "subset",
        "adapters": {
            "baseline": {"adapter_name": "julie", **products["julie"]},
            "candidate": {"adapter_name": "miller", **products["miller"]},
        },
    }


def normalize_runtime(
    runtime: Mapping[str, Any],
    selection: SelectionIdentity | Any | None,
) -> dict[str, Any]:
    if runtime.get("schema_version") != 1:
        raise ValueError("runtime identity schema_version must be 1")
    if runtime.get("contract_id") == TAKEOVER_CONTRACT_ID:
        if set(runtime) != {"contract_id", "schema_version", "adapters"}:
            raise ValueError("takeover runtime identity has unsupported fields")
        if selection is None:
            raise ValueError("takeover runtime requires selection identity")
        adapters = runtime.get("adapters")
        if not isinstance(adapters, dict) or set(adapters) != {"baseline", "candidate"}:
            raise ValueError("runtime identity must contain exactly baseline and candidate adapters")
        return dict(runtime)
    corpus_role = selection.corpus_role if selection is not None else "calibration"
    decision_scope = selection.decision_scope if selection is not None else "subset"
    return adapt_legacy_runtime(
        runtime,
        corpus_role=corpus_role,
        decision_scope=decision_scope,
    )


def _load_json_no_duplicates(value: str) -> Any:
    def object_from_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, item in pairs:
            if key in result:
                raise ValueError(f"duplicate JSON key: {key}")
            result[key] = item
        return result

    return json.loads(value, object_pairs_hook=object_from_pairs)


def validate_decision_paths(
    *,
    private_root: Path,
    implementation_root: Path,
    snapshot_roots: Sequence[Path],
    artifact_paths: Sequence[Path],
) -> None:
    private = Path(private_root).expanduser().resolve()
    implementation = Path(implementation_root).resolve()
    if _paths_overlap(private, implementation):
        raise ValueError("decision private root overlaps the implementation checkout")
    for snapshot_root in snapshot_roots:
        snapshot = Path(snapshot_root).resolve()
        if _paths_overlap(private, snapshot):
            raise ValueError("decision private root overlaps a snapshot repository")
    for artifact_path in artifact_paths:
        artifact = Path(artifact_path).expanduser().resolve()
        if not _is_within(artifact, private):
            raise ValueError(f"decision artifact is outside private root: {artifact}")


def _is_within(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def _paths_overlap(first: Path, second: Path) -> bool:
    return _is_within(first, second) or _is_within(second, first)


class PairedExecution:
    def __init__(self, baseline_rows: list[dict[str, Any]], candidate_rows: list[dict[str, Any]]):
        self.baseline_rows = baseline_rows
        self.candidate_rows = candidate_rows


class ResumedRun:
    def __init__(self, marker: Mapping[str, Any], raw: Mapping[str, Any]):
        self.classification = str(marker["classification"])
        self.outcome = str(marker["outcome"])
        self.failure_reason = marker.get("failure_reason")
        self.observed_outcome = raw.get("observed_outcome")
        self.wrong_action_count = int(raw.get("wrong_action_count", 0))
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
    first_arm = generator.choice(("baseline", "candidate"))
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
    if set(arms) != {"baseline", "candidate"}:
        raise ValueError("paired execution requires exactly baseline and candidate roles")
    if any(arms[role].role != role for role in arms):
        raise ValueError("role adapter key does not match its role")
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
        existing_order = _load_json_no_duplicates(order_path.read_text(encoding="utf-8"))
        if existing_order != order_value:
            raise ValueError("raw run identity mismatch")
    else:
        with order_path.open("x", encoding="utf-8") as stream:
            stream.write(_pretty_json(order_value))
    rows = {"baseline": [], "candidate": []}

    for task in tasks:
        if task.snapshot_id not in snapshots:
            raise ValueError(f"task {task.task_id}: unknown snapshot {task.snapshot_id}")
        for pair_attempt in range(1, max_void_attempts + 1):
            attempt_rows: dict[str, list[dict[str, Any]]] = {"baseline": [], "candidate": []}
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
                initial = {
                    arm_name: _scorer_row_is_correct(task, scorer)
                    for arm_name, _, scorer in first
                }
                if initial["baseline"] != initial["candidate"]:
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

            rows["baseline"].extend(attempt_rows["baseline"])
            rows["candidate"].extend(attempt_rows["candidate"])
            break
        else:
            raise RuntimeError(f"task {task.task_id}: harness void persisted for {max_void_attempts} pairs")

    return PairedExecution(rows["baseline"], rows["candidate"])


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
            "contract_id": raw.get("contract_id"),
            "role": raw.get("role"),
            "expected_outcome": raw.get("expected_outcome"),
            "observed_outcome": raw.get("observed_outcome"),
            "wrong_action_count": raw.get("wrong_action_count"),
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
    marker = _load_json_no_duplicates(marker_path.read_text(encoding="utf-8"))
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
    raw = _load_json_no_duplicates(result_path.read_text(encoding="utf-8"))
    validate_run_result(raw)
    expected_run_id = f"{task.task_id}.{arm_name}.p{pair_attempt}.r{repetition}"
    if (
        raw.get("run_id") != expected_run_id
        or raw.get("task_id") != task.task_id
        or raw.get("snapshot_id") != task.snapshot_id
        or (
            raw.get("role") != arm_name
            if raw.get("contract_id") == TAKEOVER_CONTRACT_ID
            else raw.get("product") != _legacy_product_for_role(arm_name)
        )
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
    observed_outcome = run.observed_outcome or run.verification.observed_outcome
    wrong_action_count = run.wrong_action_count or run.verification.wrong_action_count
    if budget_exceeded:
        status = "budget_exceeded"
        failure_reason = "budget_exceeded"
        answer = None
        observed_outcome = "hard_error"
        wrong_action_count = 0
    elif run.failure_reason == "disallowed_tool" or run.outcome == "disallowed_tool":
        status = "disallowed_tool"
        failure_reason = "disallowed_tool"
        answer = None
        observed_outcome = "wrong_answer"
    elif status == "completed" and not run.verification.passed:
        failure_reason = failure_reason or "incorrect"
    elif status == "completed":
        failure_reason = None
    elif status == "invalid_answer":
        failure_reason = "invalid_answer"
        answer = None
        observed_outcome = "hard_error"
    elif status in {"timeout", "failed"}:
        failure_reason = "product_error"
        answer = None
        observed_outcome = "hard_error"

    if failure_reason not in ALLOWED_FAILURE_REASONS and failure_reason is not None:
        failure_reason = "product_error"
    product_errors = [
        str(event.get("error") or event.get("reason") or event.get("event"))
        for event in events
        if event.get("event") in {"tool_error", "proxy_failure"}
    ]
    if run.classification == "product_failure" and not product_errors:
        product_errors.append("product process failed")
    value = {
        "schema_version": 1,
        "run_id": f"{task.task_id}.{arm_name}.p{pair_attempt}.r{repetition}",
        "task_id": task.task_id,
        "snapshot_id": task.snapshot_id,
        "product": _legacy_product_for_role(arm_name),
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
    if task.contract_id == "takeover-evaluation-v1":
        value.update(
            {
                "contract_id": task.contract_id,
                "role": arm_name,
                "expected_outcome": task.expected_outcome,
                "observed_outcome": observed_outcome or "wrong_answer",
                "wrong_action_count": wrong_action_count,
            }
        )
        value["verification"].update(
            {
                "ordered_evidence_matches": list(
                    run.verification.ordered_evidence_matches
                ),
                "observed_outcome": observed_outcome or "wrong_answer",
                "wrong_action_count": wrong_action_count,
            }
        )
        value.pop("product")
    return value


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
    value = {"status": answer.status, "answer": answer.answer, "evidence": evidence}
    if answer.contract_id is not None:
        value["contract_id"] = answer.contract_id
        value["actions"] = [asdict(action) for action in answer.actions]
    return value


def _scorer_row(raw: Mapping[str, Any], repetition: int) -> dict[str, Any]:
    if raw.get("contract_id") == "takeover-evaluation-v1":
        return {
            "contract_id": raw["contract_id"],
            "schema_version": raw["schema_version"],
            "task_id": raw["task_id"],
            "repetition": repetition,
            "observed_outcome": raw["observed_outcome"],
            "wrong_action_count": raw["wrong_action_count"],
            "failure_reason": raw["failure_reason"],
            "duration_ms": raw["wall_clock_ms"],
            "tool_calls": raw["tool_call_count"],
            "tool_output_bytes": raw["tool_output_bytes"],
            "tool_output_tokens": raw["tool_output_tokens"],
            "model_input_tokens": raw["model_input_tokens"] or 0,
            "model_output_tokens": raw["model_output_tokens"] or 0,
            "product_errors": len(raw["product_errors"]),
            "duplicate_calls": raw["duplicate_calls"],
            "uncited_tool_output_tokens": raw["uncited_tool_output_tokens"],
            "ordered_evidence_matches": list(
                raw["verification"]["ordered_evidence_matches"]
            ),
        }
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


def _scorer_row_is_correct(task: BenchmarkTask, row: Mapping[str, Any]) -> bool:
    if row.get("contract_id") == TAKEOVER_CONTRACT_ID:
        return (
            row.get("observed_outcome") == task.expected_outcome
            and row.get("wrong_action_count") == 0
        )
    return bool(row.get("completed"))


def empty_scorer_row(task_id: str, repetition: int, completed: bool) -> dict[str, Any]:
    return {
        "contract_id": TAKEOVER_CONTRACT_ID,
        "schema_version": 1,
        "task_id": task_id,
        "repetition": repetition,
        "observed_outcome": "success" if completed else "wrong_answer",
        "wrong_action_count": 0,
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
        "ordered_evidence_matches": [],
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
    manifest = _load_json_no_duplicates(manifest_path.read_text(encoding="utf-8"))
    recorded = manifest.get("run_identity_sha256")
    marker = complete_path.read_text(encoding="utf-8").strip()
    if recorded != identity_sha256 or marker != identity_sha256:
        raise ValueError("completed benchmark identity mismatch")
    evidence = _load_json_no_duplicates(evidence_path.read_text(encoding="utf-8"))
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
    takeover_v1 = all(task.contract_id == TAKEOVER_CONTRACT_ID for task in tasks)
    legacy = all(task.contract_id is None for task in tasks)
    if not takeover_v1 and not legacy:
        raise ValueError("scorer export cannot mix takeover-v1 and legacy tasks")
    if takeover_v1:
        task_rows = [
            {
                "contract_id": task.contract_id,
                "schema_version": 1,
                "task_id": task.task_id,
                "repo": task.repo_id,
                "language": task.language,
                "workflow_class": task.workflow_class,
                "evidence_critical": task.evidence_critical,
                "expected_outcome": task.expected_outcome,
                "capabilities": list(task.capabilities),
                "evidence_anchors": [
                    {
                        "anchor_id": anchor.anchor_id,
                        "relevance_grade": anchor.relevance_grade,
                    }
                    for anchor in task.evidence_anchors
                ],
            }
            for task in tasks
        ]
    else:
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
    _write_jsonl(exports / "baseline-results.jsonl", execution.baseline_rows)
    _write_jsonl(exports / "candidate-results.jsonl", execution.candidate_rows)
    (exports / "identity-manifest.json").write_text(_pretty_json(dict(identity_manifest)), encoding="utf-8")
    (exports / "void-status.json").write_text(
        _pretty_json({"unresolved_void_count": 0}),
        encoding="utf-8",
    )
    finalizer = (
        '"${AGENT_EFFICIENCY_PYTHON:-.venv-agent-efficiency/bin/python}" '
        "scripts/bench-agent-efficiency.py"
    )
    if takeover_v1:
        command = (
            'dotnet run --project eval/retrieval-eval/RetrievalEval.csproj -- decision-score '
            '--tasks "$AGENT_EFFICIENCY_EXPORT/agent-tasks.jsonl" '
            '--baseline "$AGENT_EFFICIENCY_EXPORT/baseline-results.jsonl" '
            '--candidate "$AGENT_EFFICIENCY_EXPORT/candidate-results.jsonl" '
            f'--decision-scope {identity_manifest["decision_scope"]} '
            '--out "$AGENT_EFFICIENCY_EXPORT/aggregate.json" && '
            f"{finalizer} finalize-safe "
            '--exports "$AGENT_EFFICIENCY_EXPORT" '
            '--safe-output "$AGENT_EFFICIENCY_EXPORT/safe-aggregate.json"\n'
        )
    else:
        command = (
            'dotnet run --project eval/retrieval-eval/RetrievalEval.csproj -- agent-score '
            '--tasks "$AGENT_EFFICIENCY_EXPORT/agent-tasks.jsonl" '
            '--miller "$AGENT_EFFICIENCY_EXPORT/candidate-results.jsonl" '
            '--julie "$AGENT_EFFICIENCY_EXPORT/baseline-results.jsonl" '
            '--out "$AGENT_EFFICIENCY_EXPORT/aggregate.json"\n'
        )
    (exports / "agent-score-command.txt").write_text(command, encoding="utf-8")
    artifact_names = [
        "agent-tasks.jsonl",
        "baseline-results.jsonl",
        "candidate-results.jsonl",
        "identity-manifest.json",
        "void-status.json",
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


def finalize_safe_export(
    private_exports: Path,
    safe_output: Path,
    identity_manifest: Mapping[str, Any],
    *,
    unresolved_void_count: int,
) -> None:
    exports = Path(private_exports)
    aggregate_path = exports / "aggregate.json"
    evidence_path = exports / "evidence-manifest.json"
    if not aggregate_path.is_file() or not evidence_path.is_file():
        raise ValueError("private scorer aggregate and evidence manifest are required")
    evidence = _load_json_no_duplicates(evidence_path.read_text(encoding="utf-8"))
    artifacts = evidence.get("artifacts") if isinstance(evidence, dict) else None
    if not isinstance(artifacts, list) or not artifacts:
        raise ValueError("private evidence manifest is invalid")
    retained: dict[str, str] = {}
    for index, artifact in enumerate(artifacts, start=1):
        if not isinstance(artifact, dict) or set(artifact) != {"path", "sha256", "bytes"}:
            raise ValueError("private evidence manifest is invalid")
        relative = Path(str(artifact["path"]))
        path = exports / relative
        if (
            relative.is_absolute()
            or len(relative.parts) != 1
            or not path.is_file()
            or path.stat().st_size != artifact["bytes"]
            or _sha256(path) != artifact["sha256"]
        ):
            raise ValueError("private evidence artifact failed digest verification")
        retained[f"artifact_{index:03d}"] = str(artifact["sha256"])
    retained[f"artifact_{len(retained) + 1:03d}"] = _sha256(aggregate_path)
    inputs = identity_manifest.get("inputs")
    if not isinstance(inputs, Mapping):
        raise ValueError("selection identity is missing")
    safe_identity = {
        "contract_id": identity_manifest["contract_id"],
        "schema_version": identity_manifest["schema_version"],
        "corpus_role": identity_manifest["corpus_role"],
        "decision_scope": identity_manifest["decision_scope"],
        "parent_manifest_sha256": inputs["parent_manifest_sha256"],
        "snapshot_manifest_sha256": inputs["snapshot_manifest_sha256"],
        "runtime_identity_sha256": identity_manifest["run_identity_sha256"],
        "selection_sha256": inputs["selection_sha256"],
        "selected_capability_ids": inputs["selected_capability_ids"],
        "selected_task_count": inputs["selected_task_count"],
    }
    aggregate = _load_json_no_duplicates(aggregate_path.read_text(encoding="utf-8"))
    safe = project_safe_aggregate(
        aggregate,
        safe_identity,
        unresolved_void_count=unresolved_void_count,
        private_evidence_sha256=retained,
    )
    output = Path(safe_output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(_pretty_json(safe), encoding="utf-8")


def create_product_verdict_attestation(
    safe_aggregate_path: Path,
    output_path: Path,
    *,
    product_verdict: str,
) -> None:
    safe_bytes, _ = _validate_decision_safe_aggregate(safe_aggregate_path)
    attestation = {
        "attestation_contract_id": "takeover-product-verdict-v1",
        "safe_aggregate_sha256": hashlib.sha256(safe_bytes).hexdigest(),
        "product_under_test": "Miller",
        "product_verdict": product_verdict,
        "mapping_frozen_before_preflight": True,
        "mapping_changed": False,
        "preflight_passed": True,
        "automatic_reruns_complete": True,
        "artifact_verification_passed": True,
        "unresolved_void_count": 0,
    }
    _validate_product_verdict_attestation(attestation)
    output = Path(output_path)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(_pretty_json(attestation), encoding="utf-8")


def validate_safe_return(
    safe_aggregate_path: Path,
    attestation_path: Path,
) -> dict[str, Any]:
    safe_bytes, safe = _validate_decision_safe_aggregate(safe_aggregate_path)
    attestation = _load_json_no_duplicates(Path(attestation_path).read_text(encoding="utf-8"))
    _validate_product_verdict_attestation(attestation)
    if attestation["safe_aggregate_sha256"] != hashlib.sha256(safe_bytes).hexdigest():
        raise ValueError("product attestation safe aggregate hash mismatch")
    if safe["decision_verdict"] != "pass":
        raise ValueError("safe aggregate decision verdict is fail")
    if attestation["product_verdict"] != "pass":
        raise ValueError("Miller product verdict is fail")
    return dict(attestation)


def _validate_decision_safe_aggregate(path: Path) -> tuple[bytes, dict[str, Any]]:
    safe_bytes = Path(path).read_bytes()
    safe = _load_json_no_duplicates(safe_bytes.decode("utf-8"))
    if not isinstance(safe, dict) or set(safe) != SAFE_AGGREGATE_FIELDS:
        raise ValueError("safe aggregate fields are invalid")
    selected_capabilities = safe["selected_capability_ids"]
    if (
        safe["contract_id"] != TAKEOVER_CONTRACT_ID
        or safe["corpus_role"] != "decision"
        or safe["decision_scope"] != "full"
        or safe["selected_task_count"] != 30
        or not isinstance(selected_capabilities, list)
        or any(not isinstance(value, str) for value in selected_capabilities)
        or set(selected_capabilities) != TAKEOVER_CAPABILITIES
        or safe["unresolved_void_count"] != 0
    ):
        raise ValueError("safe aggregate is not a complete full sealed decision")
    identity = {
        key: safe[key]
        for key in (
            "contract_id",
            "schema_version",
            "corpus_role",
            "decision_scope",
            "parent_manifest_sha256",
            "snapshot_manifest_sha256",
            "runtime_identity_sha256",
            "selection_sha256",
            "selected_capability_ids",
            "selected_task_count",
        )
    }
    aggregate = {
        key: safe[key]
        for key in (
            "contract_id",
            "schema_version",
            "decision_scope",
            "decision_verdict",
            "action_verdict",
            "completion",
            "outcome_counts",
            "relevance",
            "correctness",
            "efficiency",
            "baseline",
            "candidate",
            "failure_counts",
            "by_workflow",
            "by_capability",
            "by_repo",
            "by_language",
        )
    }
    aggregate["task_count"] = safe["selected_task_count"]
    projected = project_safe_aggregate(
        aggregate,
        identity,
        unresolved_void_count=safe["unresolved_void_count"],
        private_evidence_sha256=safe["private_evidence_sha256"],
    )
    if projected != safe:
        raise ValueError("safe aggregate failed canonical validation")
    return safe_bytes, safe


def _validate_product_verdict_attestation(value: Any) -> None:
    from jsonschema import Draft202012Validator

    schema = _load_json_no_duplicates(
        (BENCHMARK_ROOT / "product-verdict-attestation.schema.json").read_text(encoding="utf-8")
    )
    errors = sorted(
        Draft202012Validator(schema).iter_errors(value),
        key=lambda error: tuple(str(part) for part in error.absolute_path),
    )
    if not errors:
        return
    error = errors[0]
    location = ".".join(str(part) for part in error.absolute_path) or "$"
    raise ValueError(f"product verdict attestation {location}: {error.message}")


def _other_arm(arm: str) -> str:
    return "candidate" if arm == "baseline" else "baseline"


def _legacy_product_for_role(role: str) -> str:
    return "julie" if role == "baseline" else "miller"


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
            value = _load_json_no_duplicates(line)
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
    arguments = list(sys.argv[1:] if argv is None else argv)
    if arguments[:1] == ["attest-product"]:
        parser = argparse.ArgumentParser(
            description="Create the privacy-safe Miller product verdict attestation"
        )
        parser.add_argument("command", choices=("attest-product",))
        parser.add_argument("--safe-aggregate", required=True)
        parser.add_argument("--output", required=True)
        parser.add_argument("--product-verdict", choices=("pass", "fail"), required=True)
        args = parser.parse_args(arguments)
        try:
            create_product_verdict_attestation(
                Path(args.safe_aggregate).expanduser().resolve(),
                Path(args.output).expanduser().resolve(),
                product_verdict=args.product_verdict,
            )
            return 0
        except (OSError, UnicodeError, ValueError, RuntimeError, json.JSONDecodeError) as exc:
            print(str(exc), file=sys.stderr)
            return 2
    if arguments[:1] == ["validate-safe-return"]:
        parser = argparse.ArgumentParser(
            description="Validate the sealed safe aggregate and Miller product verdict attestation"
        )
        parser.add_argument("command", choices=("validate-safe-return",))
        parser.add_argument("--safe-aggregate", required=True)
        parser.add_argument("--attestation", required=True)
        args = parser.parse_args(arguments)
        try:
            validate_safe_return(
                Path(args.safe_aggregate).expanduser().resolve(),
                Path(args.attestation).expanduser().resolve(),
            )
            return 0
        except (OSError, UnicodeError, ValueError, RuntimeError, json.JSONDecodeError) as exc:
            print(str(exc), file=sys.stderr)
            return 2
    if arguments[:1] == ["finalize-safe"]:
        parser = argparse.ArgumentParser(
            description="Project a private agent-efficiency aggregate into the safe sealed output"
        )
        parser.add_argument("command", choices=("finalize-safe",))
        parser.add_argument("--exports", required=True)
        parser.add_argument("--safe-output", required=True)
        args = parser.parse_args(arguments)
        try:
            return _finalize_safe_cli(args)
        except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as exc:
            print(str(exc), file=sys.stderr)
            return 2
    parser = argparse.ArgumentParser(description="Run the frozen paired agent-efficiency benchmark")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--snapshots", required=True)
    parser.add_argument("--snapshot-root", action="append", default=[], metavar="REPO=DIR")
    parser.add_argument("--arm", choices=("both",), required=True)
    parser.add_argument("--corpus-role", choices=("calibration", "decision"), required=True)
    parser.add_argument("--decision-scope", choices=("subset", "full"), required=True)
    parser.add_argument("--task-family", action="append", default=[])
    parser.add_argument("--private-root")
    parser.add_argument("--out", required=True)
    parser.add_argument("--seed", required=True, type=int)
    parser.add_argument("--model", default=PINNED_MODEL)
    parser.add_argument("--reasoning", default=PINNED_REASONING)
    parser.add_argument("--runtime-identity", required=True)
    parser.add_argument("--codex", default="codex")
    parser.add_argument("--codex-home", default=str(Path.home() / ".codex"))
    parser.add_argument("--preflight-only", action="store_true")
    args = parser.parse_args(arguments)
    try:
        return _run_cli(args)
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as exc:
        print(str(exc), file=sys.stderr)
        return 2


def _finalize_safe_cli(args: argparse.Namespace) -> int:
    exports = Path(args.exports).expanduser().resolve()
    identity_path = exports / "identity-manifest.json"
    void_status_path = exports / "void-status.json"
    if not identity_path.is_file() or not void_status_path.is_file():
        raise ValueError("private identity and void status are required")
    identity = _load_json_no_duplicates(identity_path.read_text(encoding="utf-8"))
    identity_hash = str(identity.get("run_identity_sha256", ""))
    if not completed_export_matches(exports, identity_hash):
        raise ValueError("private scorer export is incomplete")
    void_status = _load_json_no_duplicates(void_status_path.read_text(encoding="utf-8"))
    if set(void_status) != {"unresolved_void_count"}:
        raise ValueError("private void status is invalid")
    unresolved_void_count = void_status["unresolved_void_count"]
    if not isinstance(unresolved_void_count, int):
        raise ValueError("private void status is invalid")
    finalize_safe_export(
        exports,
        Path(args.safe_output).expanduser().resolve(),
        identity,
        unresolved_void_count=unresolved_void_count,
    )
    return 0


def _run_cli(args: argparse.Namespace) -> int:
    if args.arm != "both":
        raise ValueError("decision runs require --arm both")
    manifest_path = Path(args.manifest).expanduser().resolve()
    snapshots_path = Path(args.snapshots).expanduser().resolve()
    runtime_path = Path(args.runtime_identity).expanduser().resolve()
    tasks = load_task_manifest(manifest_path)
    snapshots = load_snapshot_manifest(snapshots_path)
    roots = _snapshot_roots(args.snapshot_root)
    selection = build_selection(
        tasks=tasks,
        snapshots=snapshots,
        parent_manifest_bytes=manifest_path.read_bytes(),
        snapshot_manifest_bytes=snapshots_path.read_bytes(),
        corpus_role=args.corpus_role,
        decision_scope=args.decision_scope,
        capability_ids=args.task_family,
    )
    out = Path(args.out).expanduser().resolve()
    if args.corpus_role == "decision":
        if not args.private_root:
            raise ValueError("decision corpus requires --private-root")
        private_root = Path(args.private_root).expanduser().resolve()
        validate_decision_paths(
            private_root=private_root,
            implementation_root=SCRIPT_ROOT.parent,
            snapshot_roots=tuple(roots.values()),
            artifact_paths=(manifest_path, snapshots_path, runtime_path, out),
        )
    runtime = _load_json_no_duplicates(runtime_path.read_text(encoding="utf-8"))
    identity, arms, agent_snapshots = preflight_run(
        tasks=selection.tasks,
        snapshots=snapshots,
        roots=roots,
        runtime=runtime,
        codex_executable=args.codex,
        model=args.model,
        reasoning=args.reasoning,
        seed=args.seed,
        selection=selection,
    )
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
        tasks=selection.tasks,
        snapshots=agent_snapshots,
        arms=arms,
        runner=runner,
        output_root=out / "raw",
        seed=args.seed,
        identity_sha256=identity_hash,
        void_ledger_path=out / "void-ledger.jsonl",
    )
    export_scorer_artifacts(exports, selection.tasks, execution, identity)
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
    selection: SelectionIdentity | None = None,
) -> tuple[dict[str, Any], dict[str, AgentArm], dict[str, AgentSnapshot]]:
    normalized_runtime = normalize_runtime(runtime, selection)
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

    adapters = normalized_runtime.get("adapters")
    if not isinstance(adapters, dict) or set(adapters) != {"baseline", "candidate"}:
        raise ValueError("runtime identity must contain exactly baseline and candidate adapters")
    arms: dict[str, AgentArm] = {}
    safe_adapters: dict[str, Any] = {}
    for role in ("baseline", "candidate"):
        spec = adapters[role]
        if not isinstance(spec, dict):
            raise ValueError(f"{role}: identity must be an object")
        expected_product_fields = {
            "adapter_name",
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
            raise ValueError(f"{role}: identity fields must be {sorted(expected_product_fields)}")
        adapter_name = spec.get("adapter_name")
        if not isinstance(adapter_name, str) or not adapter_name:
            raise ValueError(f"{role}: adapter_name must be non-empty")
        commit = spec.get("commit")
        if not isinstance(commit, str) or re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", commit) is None:
            raise ValueError(f"{role}: commit must be a full lowercase hexadecimal commit")
        command = _string_sequence(spec.get("command"), f"{role}.command")
        environment_value = spec.get("environment")
        if not isinstance(environment_value, dict) or any(
            name not in ALLOWED_PRODUCT_ENVIRONMENT
            or not isinstance(value, str)
            or not value
            or "\0" in value
            for name, value in environment_value.items()
        ):
            raise ValueError(
                f"{role}: environment must contain only {sorted(ALLOWED_PRODUCT_ENVIRONMENT)} with non-empty string values"
            )
        environment = tuple(sorted(environment_value.items()))
        _resolve_executable(command[0])
        binary_path = Path(spec.get("binary_path", "")).expanduser().resolve()
        if not binary_path.is_file():
            raise ValueError(f"{role}: binary_path must name a file")
        if _sha256(binary_path) != spec.get("binary_sha256"):
            raise ValueError(f"{role}: binary hash mismatch")
        version_command = _string_sequence(spec.get("version_command"), f"{role}.version_command")
        version = _command_output(tuple(version_command), environment=environment_value).strip()
        if version != spec.get("version"):
            raise ValueError(f"{role}: version mismatch")
        readiness_commands = spec.get("readiness_commands")
        expected_readiness = spec.get("readiness")
        expected_snapshot_ids = set(agents)
        if (
            not isinstance(readiness_commands, dict)
            or not isinstance(expected_readiness, dict)
            or set(readiness_commands) != expected_snapshot_ids
            or set(expected_readiness) != expected_snapshot_ids
        ):
            raise ValueError(f"{role}: readiness identities must cover every snapshot")
        safe_snapshot_identities: dict[str, Any] = {}
        for snapshot_id in sorted(expected_snapshot_ids):
            readiness_command = _string_sequence(
                readiness_commands[snapshot_id], f"{role}.readiness_commands.{snapshot_id}"
            )
            readiness = _load_json_no_duplicates(
                _command_output(
                    tuple(readiness_command),
                    cwd=agents[snapshot_id].root,
                    environment=environment_value,
                )
            )
            expected = expected_readiness[snapshot_id]
            if readiness != expected or not isinstance(readiness, dict) or readiness.get("ready") is not True:
                raise ValueError(f"{role}: stale or incomplete readiness identity for {snapshot_id}")
            identity_values = {
                key: readiness.get(key)
                for key in ("workspace_identity", "index_identity", "vector_identity", "model_identity")
            }
            if any(not isinstance(value, str) or not value for value in identity_values.values()):
                raise ValueError(f"{role}: stale or incomplete readiness identity for {snapshot_id}")
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
        arms[role] = AgentArm(role, adapter_name, tuple(command), environment)
        safe_adapters[role] = {
            "adapter_name": adapter_name,
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
        for name in (
            "answer-schema.json",
            "product-verdict-attestation.schema.json",
            "run-result.schema.json",
            "snapshot-manifest.schema.json",
            "task-manifest.schema.json",
        )
    }
    safe = {
        "contract_id": (
            selection.contract_id
            if selection is not None
            else normalized_runtime["contract_id"]
        ),
        "runtime_contract_id": normalized_runtime["contract_id"],
        "schema_version": 1,
        "corpus_role": selection.corpus_role if selection else "calibration",
        "decision_scope": selection.decision_scope if selection else "subset",
        "seed": seed,
        "model": model,
        "reasoning": reasoning,
        "prompt_contract_sha256": prompt_contract_sha256(),
        "codex": {"version": codex_version, "binary_sha256": _sha256(codex_path)},
        "tokenizer": {"package": "tiktoken", "version": tokenizer_version, "encoding": PINNED_TOKENIZER},
        "environment_keys": list(isolated_environment_keys()),
        "schemas": schema_hashes,
        "inputs": (
            selection.private_identity()
            if selection
            else {
                "task_manifest_sha256": _json_sha256([asdict(task) for task in tasks]),
                "snapshot_manifest_sha256": _json_sha256([asdict(snapshot) for snapshot in snapshots]),
            }
        ),
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
        "adapters": safe_adapters,
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
                value = _load_json_no_duplicates(line)
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
    timeout: float = 30,
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
