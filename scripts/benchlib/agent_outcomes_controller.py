from __future__ import annotations

import hashlib
import json
import math
import os
import random
import shutil
import subprocess
import tempfile
from collections.abc import Callable, Mapping, Sequence
from pathlib import Path, PurePosixPath
from typing import Any

from benchlib.agent_outcomes_contract import (
    Campaign,
    VerifiableTask,
    bind_verifier,
    public_response_schema,
    source_snapshot_sha256,
    validate_campaign,
    validate_run_record,
    validate_task,
    validate_verifier,
)
from benchlib.agent_outcomes_scoring import (
    amortized_setup_costs,
    model_token_total,
    paired_cluster_confidence_interval,
    price_model_usage,
    summarize_attempts,
)

_MODES = {
    "primary": ("native", "native+miller-lexical"),
    "secondary": ("native+miller-lexical", "native+miller-semantic"),
    "ct": ("native", "native+miller-lexical"),
}
_EXECUTION_FIELDS = {
    "comparison_mode",
    "task_manifest_path",
    "verifier_manifest_path",
    "source_roots",
    "prepared_environments_path",
    "runtime_qualification_path",
    "semantic_runtime_binding_path",
    "image_reference",
    "codex_path",
    "miller_path",
    "podman_path",
    "setup_components",
    "task_ids",
    "ct_lifecycle",
    "ct_known_changes",
    "sample_size_plan",
    "provider_transport",
}


def validate_task_manifest(path: str | Path) -> Mapping[str, Any]:
    tasks, file_sha = _load_tasks(Path(path))
    return {"task_count": len(tasks), "task_manifest_sha256": file_sha}


def load_strict_json(path: str | Path) -> Any:
    return _load_json(path)


def balanced_attempt_plan(
    task_ids: Sequence[str], arms: Sequence[str], repetitions: int, seed: int
) -> list[Mapping[str, Any]]:
    if len(arms) != 2 or len(set(arms)) != 2:
        raise ValueError("paired execution requires exactly two distinct arms")
    if repetitions < 1:
        raise ValueError("repetitions must be positive")
    plan = []
    for task_id in sorted(task_ids):
        first_arm_indexes = [index % 2 for index in range(repetitions)]
        task_seed = int.from_bytes(
            hashlib.sha256(f"{seed}\0{task_id}".encode()).digest()[:8], "big"
        )
        random.Random(task_seed).shuffle(first_arm_indexes)
        for repetition in range(1, repetitions + 1):
            first = first_arm_indexes[repetition - 1]
            ordered = [arms[first], arms[1 - first]]
            for order, arm_id in enumerate(ordered, 1):
                plan.append(
                    {
                        "task_id": task_id,
                        "arm_id": arm_id,
                        "repetition": repetition,
                        "order": order,
                    }
                )
    return plan


def freeze_campaign(
    config_path: str | Path, output_path: str | Path
) -> Mapping[str, Any]:
    config = _load_json(config_path)
    if set(config) != {"campaign", "execution"}:
        raise ValueError("campaign config must contain exactly campaign and execution")
    campaign = validate_campaign(config["campaign"])
    raw_execution = config["execution"]
    if not isinstance(raw_execution, dict) or set(raw_execution) != _EXECUTION_FIELDS:
        raise ValueError("execution config fields are invalid")
    canonical_execution = _canonicalize_execution_paths(
        raw_execution, Path(config_path).resolve().parent
    )
    execution = _validate_execution_config(canonical_execution, campaign)
    tasks, task_bytes_sha = _load_tasks(Path(execution["task_manifest_path"]))
    selected = [task for task in tasks if task.task_id in execution["task_ids"]]
    if len(selected) != len(execution["task_ids"]):
        raise ValueError("execution task_ids contain an unknown or duplicate task")
    if set(execution["source_roots"]) != set(execution["task_ids"]):
        raise ValueError(
            "execution source_roots must contain exactly one root per selected task"
        )
    task_set_sha = _digest([_task_mapping(task) for task in selected])
    if campaign.value["task_set_sha256"] != task_set_sha:
        raise ValueError("campaign task_set_sha256 does not match selected tasks")
    _validate_mode(execution["comparison_mode"], campaign, selected)
    _validate_ct_task_changes(execution, selected)
    verifiers_path = Path(execution["verifier_manifest_path"])
    verifier_records = _load_json(verifiers_path)
    if not isinstance(verifier_records, list):
        raise ValueError("verifier manifest must be an array")  # noqa: TRY004
    verifiers = {}
    for record in verifier_records:
        verifier = validate_verifier(record)
        if verifier.verifier_id in verifiers:
            raise ValueError(f"duplicate verifier: {verifier.verifier_id}")
        verifiers[verifier.verifier_id] = verifier
    task_bindings = {}
    for task in selected:
        if task.verifier_id not in verifiers:
            raise ValueError(f"missing verifier: {task.verifier_id}")
        bound = bind_verifier(task, verifiers[task.verifier_id])
        source_root = Path(execution["source_roots"].get(task.task_id, ""))
        if not source_root.is_absolute() or not source_root.is_dir():
            raise ValueError(f"source root is unavailable for {task.repo_id}")
        actual_source_sha = source_snapshot_sha256(source_root)
        if actual_source_sha != task.snapshot_sha256:
            raise ValueError(f"source snapshot does not match task {task.task_id}")
        task_bindings[task.task_id] = {
            "repo_id": task.repo_id,
            "source_root": str(source_root.resolve()),
            "source_snapshot_sha256": actual_source_sha,
            "task_sha256": _digest(_task_mapping(task)),
            "verifier_sha256": _digest(_plain(bound.verifier.value)),
            "public_response_schema_sha256": _digest(public_response_schema(bound)),
        }
    envelope = {
        **execution,
        "task_manifest_sha256": task_bytes_sha,
        "verifier_manifest_sha256": _file_sha256(verifiers_path),
        "prepared_environments_sha256": _optional_file_sha(
            execution["prepared_environments_path"]
        ),
        "runtime_qualification_sha256": _optional_file_sha(
            execution["runtime_qualification_path"]
        ),
        "semantic_runtime_binding_sha256": _optional_file_sha(
            execution["semantic_runtime_binding_path"]
        ),
        "task_bindings": task_bindings,
    }
    expected_runs = (
        len(selected) * len(campaign.value["arms"]) * campaign.repetition_count
    )
    if campaign.approved_total_run_count != expected_runs:
        raise ValueError(
            "approved_total_run_count must exactly match the frozen paired plan"
        )
    campaign_value = _plain(campaign.value)
    frozen = {
        "contract_id": "agent-outcomes-controller-v1",
        "campaign": campaign_value,
        "campaign_sha256": _digest(campaign_value),
        "execution_envelope": envelope,
        "execution_envelope_sha256": _digest(envelope),
    }
    Path(output_path).write_text(
        json.dumps(frozen, sort_keys=True, indent=2) + "\n", encoding="utf-8"
    )
    return frozen


def validate_frozen_campaign(
    value_or_path: Mapping[str, Any] | str | Path,
) -> Mapping[str, Any]:
    frozen = (
        _load_json(value_or_path)
        if isinstance(value_or_path, (str, Path))
        else _plain(value_or_path)
    )
    if set(frozen) != {
        "contract_id",
        "campaign",
        "campaign_sha256",
        "execution_envelope",
        "execution_envelope_sha256",
    }:
        raise ValueError("frozen campaign fields are invalid")
    if frozen["contract_id"] != "agent-outcomes-controller-v1":
        raise ValueError("frozen campaign contract_id is invalid")
    campaign = validate_campaign(frozen["campaign"])
    if _digest(_plain(campaign.value)) != frozen["campaign_sha256"]:
        raise ValueError("campaign digest mismatch")
    envelope = frozen["execution_envelope"]
    if _digest(envelope) != frozen["execution_envelope_sha256"]:
        raise ValueError("execution envelope digest mismatch")
    computed_fields = {
        "task_manifest_sha256",
        "verifier_manifest_sha256",
        "prepared_environments_sha256",
        "runtime_qualification_sha256",
        "semantic_runtime_binding_sha256",
        "task_bindings",
    }
    if (
        not isinstance(envelope, dict)
        or set(envelope) != _EXECUTION_FIELDS | computed_fields
    ):
        raise ValueError("execution envelope fields are invalid")
    execution = _validate_execution_config(
        {field: envelope[field] for field in _EXECUTION_FIELDS}, campaign
    )
    if (
        _file_sha256(Path(envelope["task_manifest_path"]))
        != envelope["task_manifest_sha256"]
    ):
        raise ValueError("task manifest drift")
    if (
        _file_sha256(Path(envelope["verifier_manifest_path"]))
        != envelope["verifier_manifest_sha256"]
    ):
        raise ValueError("verifier manifest drift")
    if (
        _optional_file_sha(envelope["prepared_environments_path"])
        != envelope["prepared_environments_sha256"]
    ):
        raise ValueError("prepared environment drift")
    if (
        _optional_file_sha(envelope["runtime_qualification_path"])
        != envelope["runtime_qualification_sha256"]
    ):
        raise ValueError("runtime qualification drift")
    if (
        _optional_file_sha(envelope["semantic_runtime_binding_path"])
        != envelope["semantic_runtime_binding_sha256"]
    ):
        raise ValueError("semantic runtime image binding drift")
    tasks, _ = _load_tasks(Path(execution["task_manifest_path"]))
    selected = [task for task in tasks if task.task_id in execution["task_ids"]]
    if len(selected) != len(execution["task_ids"]):
        raise ValueError("frozen execution references unknown tasks")
    if (
        _digest([_task_mapping(task) for task in selected])
        != campaign.value["task_set_sha256"]
    ):
        raise ValueError("frozen task set drift")
    _validate_mode(execution["comparison_mode"], campaign, selected)
    _validate_ct_task_changes(execution, selected)
    verifier_records = _load_json(execution["verifier_manifest_path"])
    verifiers = _verifier_map(verifier_records)
    if set(envelope["task_bindings"]) != set(execution["task_ids"]):
        raise ValueError("frozen task bindings are incomplete")
    for task in selected:
        binding = envelope["task_bindings"][task.task_id]
        if (
            source_snapshot_sha256(binding["source_root"])
            != binding["source_snapshot_sha256"]
        ):
            raise ValueError("source snapshot drift")
        verifier = verifiers.get(task.verifier_id)
        if verifier is None:
            raise ValueError("frozen verifier binding is unavailable")
        bound = bind_verifier(task, verifier)
        expected = {
            "repo_id": task.repo_id,
            "source_root": binding["source_root"],
            "source_snapshot_sha256": task.snapshot_sha256,
            "task_sha256": _digest(_task_mapping(task)),
            "verifier_sha256": _digest(_plain(verifier.value)),
            "public_response_schema_sha256": _digest(public_response_schema(bound)),
        }
        if binding != expected:
            raise ValueError("frozen task binding drift")
    return frozen


class ApprovalBudget:
    def __init__(
        self,
        approval: Mapping[str, Any],
        campaign_sha256: str,
        execution_envelope_sha256: str,
        exact_run_ceiling: int,
        setup_components: Sequence[Mapping[str, Any]] = (),
    ) -> None:
        required = {
            "campaign_sha256",
            "execution_envelope_sha256",
            "approved_run_ceiling",
            "approved_money_ceiling",
            "run_root",
        }
        if set(approval) != required:
            raise PermissionError("approval record fields are invalid")
        if approval["campaign_sha256"] != campaign_sha256:
            raise PermissionError("approval campaign digest does not match")
        if approval["execution_envelope_sha256"] != execution_envelope_sha256:
            raise PermissionError("approval execution envelope digest does not match")
        if (
            not isinstance(approval["approved_run_ceiling"], int)
            or isinstance(approval["approved_run_ceiling"], bool)
            or approval["approved_run_ceiling"] != exact_run_ceiling
        ):
            raise PermissionError("approval run ceiling is not exact")
        if (
            not isinstance(approval["run_root"], str)
            or not Path(approval["run_root"]).is_absolute()
        ):
            raise PermissionError("approval run_root must be absolute")
        ceiling = approval["approved_money_ceiling"]
        if ceiling is not None and not _nonnegative_number(ceiling):
            raise PermissionError("approval money ceiling is invalid")
        self.run_ceiling = exact_run_ceiling
        self.run_root = Path(approval["run_root"])
        self.money_ceiling = ceiling
        self.completed = 0
        unique_setup = {
            (item["environment_id"], item["component_id"]): item
            for item in setup_components
        }
        setup_costs = [item.get("cost") for item in unique_setup.values()]
        self.measured_cost = sum(
            cost for cost in setup_costs if _nonnegative_number(cost)
        )
        self.usage_complete = True
        self.cost_complete = all(_nonnegative_number(cost) for cost in setup_costs)
        self.model_ceiling_overshot = False
        self.stop_reason = None

    def authorize_next(self) -> None:
        if not self.usage_complete:
            self.stop_reason = "usage_incomplete"
            raise PermissionError("prior attempt usage is incomplete")
        if self.model_ceiling_overshot:
            self.stop_reason = "model_token_ceiling_overshot"
            raise PermissionError("prior attempt exceeded its model token ceiling")
        if self.completed >= self.run_ceiling:
            self.stop_reason = "run_ceiling_exhausted"
            raise PermissionError("approved run ceiling is exhausted")
        if self.money_ceiling is not None and self.measured_cost >= self.money_ceiling:
            self.stop_reason = "money_ceiling_exhausted"
            raise PermissionError("approved money ceiling is exhausted")
        if self.money_ceiling is not None and not self.cost_complete:
            self.stop_reason = "cost_incomplete"
            raise PermissionError("prior attempt cost is incomplete")

    def record_completion(
        self,
        *,
        run_cost: float | None,
        usage_complete: bool,
        observed_model_tokens: int | None = None,
        model_token_ceiling: int | None = None,
    ) -> None:
        self.completed += 1
        self.usage_complete = self.usage_complete and usage_complete
        self.cost_complete = self.cost_complete and run_cost is not None
        if run_cost is not None:
            if not _nonnegative_number(run_cost):
                raise ValueError("completed run cost must be nonnegative")
            self.measured_cost += run_cost
        if (
            observed_model_tokens is not None
            and model_token_ceiling is not None
            and observed_model_tokens > model_token_ceiling
        ):
            self.model_ceiling_overshot = True


def execute_campaign(
    frozen_value_or_path: Mapping[str, Any] | str | Path,
    output_dir: str | Path,
    *,
    dry_run: bool,
    approval: Mapping[str, Any] | None = None,
    attempt_executor: Callable[
        [VerifiableTask, str, Path, Path, int, int], Mapping[str, Any]
    ]
    | None = None,
) -> Mapping[str, Any]:
    frozen = validate_frozen_campaign(frozen_value_or_path)
    campaign = validate_campaign(frozen["campaign"])
    envelope = frozen["execution_envelope"]
    tasks, _ = _load_tasks(Path(envelope["task_manifest_path"]))
    selected = {
        task.task_id: task for task in tasks if task.task_id in envelope["task_ids"]
    }
    verifiers = _verifier_map(_load_json(envelope["verifier_manifest_path"]))
    bound = {
        task_id: bind_verifier(task, verifiers[task.verifier_id])
        for task_id, task in selected.items()
    }
    arms = [arm["arm_id"] for arm in campaign.value["arms"]]
    plan = balanced_attempt_plan(
        envelope["task_ids"],
        arms,
        campaign.repetition_count,
        campaign.value["order_seed"],
    )
    if len(plan) != campaign.approved_total_run_count:
        raise ValueError(
            "approved_total_run_count must exactly match the frozen paired plan"
        )
    output = Path(output_dir)
    budget = None
    if not dry_run:
        if approval is None:
            raise PermissionError("live execution requires approval")
        if (
            approval.get("approved_money_ceiling")
            != campaign.value["approved_money_ceiling"]
        ):
            raise PermissionError(
                "approval money ceiling does not match the frozen campaign"
            )
        budget = ApprovalBudget(
            approval,
            frozen["campaign_sha256"],
            frozen["execution_envelope_sha256"],
            len(plan),
            envelope["setup_components"],
        )
        if output.resolve() != budget.run_root.resolve():
            raise PermissionError("output directory does not match approved run_root")
        if attempt_executor is None:
            attempt_executor = _default_attempt_executor(campaign, envelope)
    output.mkdir(parents=True, exist_ok=False)
    attempts = []
    metadata = {
        "contract_id": "agent-outcomes-run-v1",
        "dry_run": dry_run,
        "campaign_sha256": frozen["campaign_sha256"],
        "execution_envelope_sha256": frozen["execution_envelope_sha256"],
        "planned_attempt_count": len(plan),
        "plan_sha256": _digest(plan),
        "plan": plan,
        "task_repositories": {
            task_id: task.task.repo_id for task_id, task in bound.items()
        },
        "arm_identities": {
            arm["arm_id"]: {
                "arm_id": arm["arm_id"],
                "runtime_identity": arm["runtime_identity"],
                "runtime_qualification_sha256": arm["runtime_qualification_sha256"],
            }
            for arm in campaign.value["arms"]
        },
        "setup_components": envelope["setup_components"],
    }
    _write_json(output / "run-metadata.json", metadata)
    ledger_path = output / "attempts.jsonl"
    ledger_file = ledger_path.open("x", encoding="utf-8")
    status = "running"
    stop_reason = None
    deferred_error = None
    try:
        for index, item in enumerate(plan):
            task = bound[item["task_id"]]
            if budget is not None:
                try:
                    budget.authorize_next()
                except PermissionError as exc:
                    status = "stopped"
                    stop_reason = budget.stop_reason
                    deferred_error = exc
                    break
            _append_json_line(
                ledger_file,
                {"kind": "dispatch", "sequence": index + 1, "scheduled": item},
            )
            if dry_run:
                record = _dry_record(
                    frozen["campaign_sha256"], task, item, index, campaign
                )
                execution_evidence = {
                    "execution_sha256": None,
                    "private_evidence_path": None,
                    "reason": "synthetic_dry_run",
                    "setup": None,
                }
            else:
                execution = {}
                try:
                    result = attempt_executor(
                        task,
                        item["arm_id"],
                        Path(envelope["task_bindings"][item["task_id"]]["source_root"]),
                        output / f"attempt-{index + 1:04d}",
                        item["repetition"],
                        item["order"],
                    )
                    record = _plain(result.get("record", result))
                    execution = _plain(result.get("execution", {}))
                    _validate_returned_identity(
                        record,
                        frozen["campaign_sha256"],
                        task,
                        item,
                        execution,
                    )
                    execution_evidence = {
                        "execution_sha256": _digest(execution),
                        "private_evidence_path": execution.get("private_envelope_path"),
                        "reason": None,
                        "setup": _safe_execution_setup(execution),
                    }
                except Exception as exc:  # noqa: BLE001
                    record = _infrastructure_void_record(
                        frozen["campaign_sha256"], task, item, index, exc
                    )
                    execution_evidence = {
                        "execution_sha256": None,
                        "private_evidence_path": None,
                        "reason": "adapter_or_record_invalid",
                        "setup": _try_safe_execution_setup(execution),
                    }
                record = _derive_cost(record, campaign)
                validated = validate_run_record(record)
                token_fields = (
                    "total_model_input_tokens",
                    "total_model_cached_tokens",
                    "total_model_output_tokens",
                )
                usage_complete = all(
                    validated.value[field] is not None for field in token_fields
                )
                budget.record_completion(
                    run_cost=validated.price_derived_cost,
                    usage_complete=usage_complete,
                    observed_model_tokens=_tokens(validated.value),
                    model_token_ceiling=task.task.max_model_tokens,
                )
            entry = {
                "repo_id": task.task.repo_id,
                "scheduled": item,
                "record": record,
                "execution_evidence": execution_evidence,
            }
            attempts.append(entry)
            _append_json_line(
                ledger_file,
                {"kind": "completion", "sequence": index + 1, **entry},
            )
        if status == "running":
            status = "completed"
    except Exception:
        status = "error"
        stop_reason = "controller_error"
        raise
    finally:
        ledger_file.close()
        result = {
            **metadata,
            "status": status,
            "stop_reason": stop_reason,
            "completed_attempt_count": len(attempts),
            "attempt_journal_sha256": _file_sha256(ledger_path),
            "attempts": attempts,
        }
        _write_json(output / "attempt-ledger.json", result)
    if deferred_error is not None:
        raise deferred_error
    return result


def score_run(
    run_dir: str | Path, output_path: str | Path, *, bootstrap_samples: int = 2_000
) -> Mapping[str, Any]:
    ledger = _load_durable_run(Path(run_dir))
    attempts = ledger["attempts"]
    arms: dict[str, list[Mapping[str, Any]]] = {}
    pair_groups: dict[tuple[str, int], dict[str, Mapping[str, Any]]] = {}
    for entry in attempts:
        record = _plain(validate_run_record(entry["record"]).value)
        tokens = _tokens(record)
        row = {
            "outcome": record["outcome"],
            "correct": record["outcome"] == "correct",
            "cost": record["price_derived_cost"],
            "tokens": tokens,
            "wall_time_seconds": record["wall_time_seconds"],
            "native_tool_counts": record["native_tool_counts"],
            "miller_calls": record["miller_calls"],
        }
        arms.setdefault(record["arm_id"], []).append(row)
        pair_groups.setdefault((record["task_id"], record["repetition"]), {})[
            record["arm_id"]
        ] = {
            **row,
            "repo_id": entry["repo_id"],
            "task_id": record["task_id"],
            "repetition": record["repetition"],
        }
    arm_ids = sorted(arms)
    paired_metrics = {
        "correctness": [],
        "wall_time_seconds": [],
        "model_tokens": [],
        "cost": [],
    }
    if len(arm_ids) == 2:
        baseline, treatment = arm_ids
        for values in pair_groups.values():
            if set(values) != {baseline, treatment}:
                continue
            if any(values[arm]["outcome"] == "infrastructure_void" for arm in arm_ids):
                continue
            identity = {
                "repo_id": values[baseline]["repo_id"],
                "task_id": values[baseline]["task_id"],
                "repetition": values[baseline]["repetition"],
            }
            paired_metrics["correctness"].append(
                {
                    **identity,
                    "paired_delta": int(values[treatment]["correct"])
                    - int(values[baseline]["correct"]),
                }
            )
            for metric in ("wall_time_seconds", "model_tokens", "cost"):
                left = values[baseline][{"model_tokens": "tokens"}.get(metric, metric)]
                right = values[treatment][
                    {"model_tokens": "tokens"}.get(metric, metric)
                ]
                if left is not None and right is not None:
                    paired_metrics[metric].append(
                        {**identity, "paired_delta": right - left}
                    )
    paired_reports = {
        metric: paired_cluster_confidence_interval(
            rows, samples=bootstrap_samples, seed=0
        )
        if rows
        else None
        for metric, rows in paired_metrics.items()
    }
    all_rows = [row for rows in arms.values() for row in rows]
    arm_summaries = {
        arm: summarize_attempts(rows, _setup_for_arm(arm, ledger["setup_components"]))
        for arm, rows in sorted(arms.items())
    }
    for arm_id, summary in arm_summaries.items():
        summary.update(ledger["arm_identities"][arm_id])
        summary["metrics"] = _s1_metrics(summary)
    report = {
        "schema": "agent-outcomes-v1",
        "contract_id": "agent-outcomes-score-v1",
        "campaign_sha256": ledger["campaign_sha256"],
        "execution_envelope_sha256": ledger["execution_envelope_sha256"],
        "dry_run": ledger["dry_run"],
        "run_status": ledger["status"],
        "stop_reason": ledger["stop_reason"],
        "synthetic": ledger["dry_run"],
        "arms": arm_summaries,
        "campaign_setup": summarize_attempts([], ledger["setup_components"])["setup"],
        "observed_setup_by_arm": {
            arm_id: _summarize_execution_setup(
                [entry for entry in attempts if entry["record"]["arm_id"] == arm_id]
            )
            for arm_id in sorted(arms)
        },
        "campaign_totals": summarize_attempts(all_rows, ledger["setup_components"]),
        "void_reasons": _void_reasons(attempts),
        "paired": paired_reports,
        "paired_correctness": paired_reports["correctness"],
    }
    Path(output_path).write_text(
        json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8"
    )
    return report


def _load_durable_run(run_root: Path) -> Mapping[str, Any]:
    metadata = _load_json(run_root / "run-metadata.json")
    metadata_fields = {
        "contract_id",
        "dry_run",
        "campaign_sha256",
        "execution_envelope_sha256",
        "planned_attempt_count",
        "plan_sha256",
        "plan",
        "task_repositories",
        "arm_identities",
        "setup_components",
    }
    if not isinstance(metadata, dict) or set(metadata) != metadata_fields:
        raise ValueError("run metadata fields are invalid")
    if metadata["contract_id"] != "agent-outcomes-run-v1":
        raise ValueError("run metadata contract_id is invalid")
    if not isinstance(metadata["dry_run"], bool):
        raise ValueError("run metadata dry_run must be boolean")  # noqa: TRY004
    for field in ("campaign_sha256", "execution_envelope_sha256", "plan_sha256"):
        _sha256(metadata[field], f"run metadata {field}")
    if (
        not isinstance(metadata["planned_attempt_count"], int)
        or isinstance(metadata["planned_attempt_count"], bool)
        or metadata["planned_attempt_count"] < 1
    ):
        raise ValueError("run metadata planned_attempt_count must be positive")
    plan = metadata["plan"]
    if not isinstance(plan, list) or len(plan) != metadata["planned_attempt_count"]:
        raise ValueError("run metadata plan length is invalid")
    if _digest(plan) != metadata["plan_sha256"]:
        raise ValueError("run metadata plan digest mismatch")
    repositories = metadata["task_repositories"]
    if not isinstance(repositories, dict) or not all(
        isinstance(task_id, str) and task_id and isinstance(repo_id, str) and repo_id
        for task_id, repo_id in repositories.items()
    ):
        raise ValueError("run metadata task repositories are invalid")
    events = _load_json_lines(run_root / "attempts.jsonl")
    completions = []
    pending = None
    run_ids = set()
    for event in events:
        if not isinstance(event, dict) or event.get("kind") not in {
            "dispatch",
            "completion",
        }:
            raise ValueError("attempt journal event is invalid")
        sequence = event.get("sequence")
        if (
            not isinstance(sequence, int)
            or isinstance(sequence, bool)
            or sequence < 1
            or sequence > len(plan)
        ):
            raise ValueError("attempt journal sequence is invalid")
        scheduled = event.get("scheduled")
        if scheduled != plan[sequence - 1]:
            raise ValueError("attempt journal does not match frozen plan prefix")
        if event["kind"] == "dispatch":
            if set(event) != {"kind", "sequence", "scheduled"} or pending is not None:
                raise ValueError("attempt journal dispatch ordering is invalid")
            if sequence != len(completions) + 1:
                raise ValueError("attempt journal dispatch is not the next plan item")
            pending = event
            continue
        if set(event) != {
            "kind",
            "sequence",
            "repo_id",
            "scheduled",
            "record",
            "execution_evidence",
        }:
            raise ValueError("attempt journal completion fields are invalid")
        if pending is None or pending["sequence"] != sequence:
            raise ValueError("attempt completion has no matching dispatch intent")
        expected_repo = repositories.get(scheduled["task_id"])
        if event["repo_id"] != expected_repo:
            raise ValueError("attempt completion repository does not match task")
        record = _plain(validate_run_record(event["record"]).value)
        _validate_journal_record(record, metadata["campaign_sha256"], scheduled)
        if record["run_id"] in run_ids:
            raise ValueError("attempt journal contains a duplicate run_id")
        run_ids.add(record["run_id"])
        _validate_execution_evidence(event["execution_evidence"])
        completions.append(
            {
                "repo_id": event["repo_id"],
                "scheduled": scheduled,
                "record": record,
                "execution_evidence": event["execution_evidence"],
            }
        )
        pending = None
    final_path = run_root / "attempt-ledger.json"
    if final_path.exists():
        final = _load_json(final_path)
        expected_final = metadata_fields | {
            "status",
            "stop_reason",
            "completed_attempt_count",
            "attempt_journal_sha256",
            "attempts",
        }
        if not isinstance(final, dict) or set(final) != expected_final:
            raise ValueError("final attempt ledger fields are invalid")
        for field in metadata_fields:
            if final[field] != metadata[field]:
                raise ValueError("final attempt ledger metadata mismatch")
        if final["attempts"] != completions:
            raise ValueError("final attempt ledger does not match append-only journal")
        if final["attempt_journal_sha256"] != _file_sha256(run_root / "attempts.jsonl"):
            raise ValueError("final attempt journal digest mismatch")
        if final["completed_attempt_count"] != len(completions):
            raise ValueError("final attempt count does not match journal")
        if final["status"] not in {"completed", "stopped", "error"}:
            raise ValueError("final run status is invalid")
        allowed_reasons = {
            "completed": {None},
            "stopped": {
                "usage_incomplete",
                "model_token_ceiling_overshot",
                "run_ceiling_exhausted",
                "money_ceiling_exhausted",
                "cost_incomplete",
            },
            "error": {"controller_error"},
        }
        if final["stop_reason"] not in allowed_reasons[final["status"]]:
            raise ValueError("final run stop reason is invalid")
        if final["status"] == "completed" and (
            pending is not None or len(completions) != len(plan)
        ):
            raise ValueError("completed run does not contain the full frozen plan")
        if pending is not None:
            if final["status"] != "error":
                raise ValueError("final ledger omits an unresolved dispatched attempt")
            scheduled = pending["scheduled"]
            completions.append(
                {
                    "repo_id": repositories[scheduled["task_id"]],
                    "scheduled": scheduled,
                    "record": _unresolved_dispatch_record(
                        metadata["campaign_sha256"], scheduled
                    ),
                    "execution_evidence": {
                        "execution_sha256": None,
                        "private_evidence_path": None,
                        "reason": "unresolved_dispatch",
                        "setup": None,
                    },
                }
            )
            return {
                **final,
                "stop_reason": "unresolved_dispatch",
                "attempts": completions,
            }
        return final
    if pending is not None:
        scheduled = pending["scheduled"]
        completions.append(
            {
                "repo_id": repositories[scheduled["task_id"]],
                "scheduled": scheduled,
                "record": _unresolved_dispatch_record(
                    metadata["campaign_sha256"], scheduled
                ),
                "execution_evidence": {
                    "execution_sha256": None,
                    "private_evidence_path": None,
                    "reason": "unresolved_dispatch",
                    "setup": None,
                },
            }
        )
    return {
        **metadata,
        "status": "interrupted",
        "stop_reason": "unresolved_dispatch"
        if pending is not None
        else "controller_interrupted",
        "completed_attempt_count": len(completions),
        "attempt_journal_sha256": _file_sha256(run_root / "attempts.jsonl"),
        "attempts": completions,
    }


def _load_json_lines(path: Path) -> list[Any]:
    records = []
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as exc:
        raise ValueError(f"attempt journal: {exc}") from exc
    for line_number, line in enumerate(lines, 1):
        if not line:
            raise ValueError(f"attempt journal line {line_number} is empty")
        try:
            records.append(
                json.loads(
                    line,
                    object_pairs_hook=_unique_object,
                    parse_constant=_reject_constant,
                )
            )
        except (json.JSONDecodeError, ValueError) as exc:
            raise ValueError(f"attempt journal line {line_number}: {exc}") from exc
    return records


def _validate_journal_record(
    record: Mapping[str, Any], campaign_sha256: str, scheduled: Mapping[str, Any]
) -> None:
    expected = {
        "campaign_sha256": campaign_sha256,
        "task_id": scheduled["task_id"],
        "arm_id": scheduled["arm_id"],
        "repetition": scheduled["repetition"],
        "order": scheduled["order"],
        "run_id": f"{scheduled['task_id']}-{scheduled['arm_id'].replace('+', '-')}-r{scheduled['repetition']}",
    }
    if any(record[field] != value for field, value in expected.items()):
        raise ValueError("attempt record identity does not match frozen plan")
    model_token_total(record)


def _validate_execution_evidence(value: Any) -> None:
    fields = {"execution_sha256", "private_evidence_path", "reason", "setup"}
    if not isinstance(value, dict) or set(value) != fields:
        raise ValueError("attempt execution evidence fields are invalid")
    if value["execution_sha256"] is not None:
        _sha256(value["execution_sha256"], "attempt execution evidence digest")
    if value["private_evidence_path"] is not None and not isinstance(
        value["private_evidence_path"], str
    ):
        raise ValueError("attempt private evidence path is invalid")
    if value["reason"] not in {
        None,
        "synthetic_dry_run",
        "adapter_or_record_invalid",
        "unresolved_dispatch",
    }:
        raise ValueError("attempt execution evidence reason is invalid")
    if value["setup"] is not None and not isinstance(value["setup"], dict):
        raise ValueError("attempt execution setup evidence is invalid")


def _unresolved_dispatch_record(
    campaign_sha256: str, scheduled: Mapping[str, Any]
) -> Mapping[str, Any]:
    return _plain(
        validate_run_record(
            {
                "contract_id": "agent-outcomes-v1",
                "campaign_sha256": campaign_sha256,
                "run_id": f"{scheduled['task_id']}-{scheduled['arm_id'].replace('+', '-')}-r{scheduled['repetition']}",
                "task_id": scheduled["task_id"],
                "arm_id": scheduled["arm_id"],
                "repetition": scheduled["repetition"],
                "order": scheduled["order"],
                "outcome": "infrastructure_void",
                "verifier_evidence_sha256": _digest({"unresolved_dispatch": True}),
                "wall_time_seconds": 0.0,
                "native_tool_counts": {},
                "miller_calls": 0,
                "total_model_input_tokens": None,
                "total_model_cached_tokens": None,
                "total_model_output_tokens": None,
                "raw_event_sha256": _digest({"raw_event": "unavailable"}),
                "price_derived_cost": None,
            }
        ).value
    )


def _void_reasons(attempts: Sequence[Mapping[str, Any]]) -> Mapping[str, int]:
    reasons = {}
    for entry in attempts:
        reason = entry["execution_evidence"]["reason"]
        if entry["record"]["outcome"] == "infrastructure_void" and reason is not None:
            reasons[reason] = reasons.get(reason, 0) + 1
    return dict(sorted(reasons.items()))


def _safe_execution_setup(execution: Mapping[str, Any]) -> Mapping[str, Any]:
    prepared = execution.get("prepared_environment")
    qualification = execution.get("isolation_qualification", {})
    qualification_setup = qualification.get("prepared_setup")
    ct_setup = None
    if "ct_attempt_evidence_sha256" in execution:
        digest_fields = (
            "ct_attempt_evidence_sha256",
            "ct_lifecycle_evidence_sha256",
            "ct_transition_evidence_sha256",
            "ct_cleanup_evidence_sha256",
            "baseline_snapshot_sha256",
            "changed_snapshot_sha256",
            "measured_snapshot_sha256",
        )
        for field in digest_fields:
            _sha256(execution.get(field), f"CT execution {field}")
        for field in ("setup_wall_time_seconds", "agent_wall_time_seconds"):
            if not _nonnegative_number(execution.get(field)):
                raise ValueError(f"CT execution {field} is invalid")
        if not isinstance(execution.get("container_removed"), bool):
            raise ValueError("CT execution container_removed is invalid")
        ct_setup = {
            field: execution[field]
            for field in (
                *digest_fields,
                "setup_wall_time_seconds",
                "agent_wall_time_seconds",
                "container_removed",
            )
        }
    return {
        "prepared_environment": _validated_prepared_setup(prepared),
        "isolation_prepared_environment": _validated_prepared_setup(
            qualification_setup
        ),
        "ct": ct_setup,
    }


def _try_safe_execution_setup(value: Any) -> Mapping[str, Any] | None:
    if not isinstance(value, dict):
        return None
    try:
        return _safe_execution_setup(value)
    except ValueError:
        return None


def _summarize_execution_setup(
    attempts: Sequence[Mapping[str, Any]],
) -> Mapping[str, Any]:
    measured = 0
    total_seconds = 0.0
    for attempt in attempts:
        setup = attempt["execution_evidence"]["setup"]
        if setup is None:
            continue
        measured += 1
        prepared = setup["prepared_environment"]
        qualification = setup["isolation_prepared_environment"]
        ct = setup["ct"]
        if prepared is not None:
            total_seconds += prepared["materialization_seconds"]
            total_seconds += prepared["image_verification_seconds"]
        if qualification is not None:
            total_seconds += qualification["materialization_seconds"]
            total_seconds += qualification["image_verification_seconds"]
        if ct is not None:
            total_seconds += ct["setup_wall_time_seconds"]
    return {
        "wall_time_seconds": total_seconds if measured == len(attempts) else None,
        "measured_wall_time_seconds": total_seconds,
        "coverage": {"measured": measured, "total": len(attempts)},
    }


def _validated_prepared_setup(value: Any) -> Mapping[str, Any] | None:
    if value is None:
        return None
    fields = {
        "manifest_sha256",
        "repo_id",
        "materialization_seconds",
        "download_bytes",
        "download_seconds",
        "image_verification_seconds",
    }
    if not isinstance(value, dict) or set(value) != fields:
        raise ValueError("prepared execution setup fields are invalid")
    _sha256(value["manifest_sha256"], "prepared execution manifest")
    if not isinstance(value["repo_id"], str) or not value["repo_id"]:
        raise ValueError("prepared execution repository is invalid")
    for field in fields - {"manifest_sha256", "repo_id"}:
        if value[field] is not None and not _nonnegative_number(value[field]):
            raise ValueError(f"prepared execution {field} is invalid")
    return _plain(value)


def public_frozen_campaign(frozen: Mapping[str, Any]) -> Mapping[str, Any]:
    validated = validate_frozen_campaign(frozen)
    envelope = validated["execution_envelope"]
    return {
        "contract_id": validated["contract_id"],
        "campaign": validated["campaign"],
        "campaign_sha256": validated["campaign_sha256"],
        "execution_envelope_sha256": validated["execution_envelope_sha256"],
        "execution": {
            "comparison_mode": envelope["comparison_mode"],
            "task_ids": envelope["task_ids"],
            "task_bindings": {
                task_id: {
                    key: value for key, value in binding.items() if key != "source_root"
                }
                for task_id, binding in envelope["task_bindings"].items()
            },
        },
    }


def s1_join_projection(report: Mapping[str, Any]) -> Mapping[str, Any]:
    report_fields = {
        "schema",
        "contract_id",
        "campaign_sha256",
        "execution_envelope_sha256",
        "dry_run",
        "run_status",
        "stop_reason",
        "synthetic",
        "arms",
        "campaign_setup",
        "observed_setup_by_arm",
        "campaign_totals",
        "void_reasons",
        "paired",
        "paired_correctness",
    }
    if not isinstance(report, dict) or set(report) != report_fields:
        raise ValueError("S1 join report fields are invalid")
    if report.get("schema") != "agent-outcomes-v1":
        raise ValueError("S1 join report schema is invalid")
    if report.get("dry_run") is not False or report.get("synthetic") is not False:
        raise ValueError("synthetic dry-run results cannot produce an S1 join")
    campaign_sha256 = report.get("campaign_sha256")
    _sha256(campaign_sha256, "S1 join campaign_sha256")
    required_metrics = {
        "correctness",
        "sufficient_evidence",
        "calls",
        "tokens",
        "wall_time_seconds",
        "retries",
        "irrelevant_output",
        "retrieval_diagnostics",
        "fallback",
    }
    arms = []
    arm_values = report.get("arms")
    if not isinstance(arm_values, dict) or not arm_values:
        raise ValueError("S1 join arms are unavailable")
    for arm_id, arm in sorted(arm_values.items()):
        if arm.get("arm_id") != arm_id:
            raise ValueError("S1 join arm identity is invalid")
        metrics = arm.get("metrics")
        if not isinstance(metrics, dict) or set(metrics) != required_metrics:
            raise ValueError("S1 join metrics keys are invalid")
        _validate_s1_metrics(metrics)
        qualification = arm.get("runtime_qualification_sha256")
        identity = arm.get("runtime_identity")
        if arm_id == "native+miller-semantic":
            if not isinstance(identity, dict) or qualification is None:
                raise ValueError("semantic S1 join arm is unqualified")
            _sha256(qualification, "semantic S1 join qualification")
            _validate_runtime_identity_projection(identity)
        elif identity is not None or qualification is not None:
            raise ValueError(
                "non-semantic S1 join arm cannot claim runtime qualification"
            )
        arms.append(
            {
                "arm_id": arm_id,
                "runtime_identity": identity,
                "runtime_qualification_sha256": qualification,
                "metrics": metrics,
            }
        )
    return {
        "schema": "agent-outcomes-v1",
        "campaign_sha256": campaign_sha256,
        "arms": arms,
    }


def _validate_runtime_identity_projection(identity: Mapping[str, Any]) -> None:
    fields = {
        "sidecar_commit",
        "binary_sha256",
        "runtime_payload_sha256",
        "model_id",
        "model_sha256",
        "model_manifest_sha256",
        "miller_fixture_commit",
        "resolved_backend",
        "process_mode",
        "served_dimensions",
        "conformance_harness_sha256",
        "throughput_harness_sha256",
        "concurrency_harness_sha256",
    }
    if set(identity) != fields:
        raise ValueError("semantic S1 runtime identity fields are invalid")
    for field in (
        "binary_sha256",
        "runtime_payload_sha256",
        "model_sha256",
        "model_manifest_sha256",
        "conformance_harness_sha256",
        "throughput_harness_sha256",
        "concurrency_harness_sha256",
    ):
        _sha256(identity[field], f"semantic S1 runtime {field}")


class NativeRunnerAttemptExecutor:
    def __init__(
        self, runner_factory: Callable[[Path, VerifiableTask, str], Any]
    ) -> None:
        self.runner_factory = runner_factory

    def __call__(
        self,
        task: VerifiableTask,
        arm_id: str,
        snapshot: Path,
        attempt_root: Path,
        repetition: int,
        order: int,
    ) -> Mapping[str, Any]:
        input_root = attempt_root / "task-input"
        shutil.copytree(snapshot, input_root, symlinks=True)
        (attempt_root / "private-grader").mkdir()
        output_root = attempt_root / "agent-output"
        runner = self.runner_factory(attempt_root, task, arm_id)
        probe = runner.qualify_isolation(
            attempt_root,
            mutation=bool(task.task.allowed_write_paths),
            arm_id=arm_id,
            repo_id=task.task.repo_id,
        )
        if probe.qualification is None:
            raise RuntimeError("native runner isolation qualification failed")
        runner.qualification = probe.qualification
        envelope = runner.run(
            task,
            arm_id,
            input_root,
            output_root,
            repetition=repetition,
            order=order,
        )
        execution = _plain(envelope.execution)
        execution["isolation_qualification"] = {
            "passed": probe.passed,
            "qualification_sha256": probe.qualification_sha256,
            "evidence_path": probe.evidence_path,
            "prepared_setup": _plain(probe.prepared_setup),
        }
        return {
            "record": _plain(envelope.run_record),
            "execution": execution,
        }


class CtRunnerAttemptExecutor:
    def __init__(
        self,
        runner_factory: Callable[[Path, VerifiableTask, str], Any],
        lifecycle_manifest: Mapping[str, Any],
        known_changes: Mapping[str, Mapping[str, Any]],
        miller_path: str,
    ) -> None:
        self.runner_factory = runner_factory
        self.lifecycle_manifest = _plain(lifecycle_manifest)
        self.known_changes = _plain(known_changes)
        self.miller_path = miller_path

    def __call__(
        self,
        task: VerifiableTask,
        arm_id: str,
        snapshot: Path,
        attempt_root: Path,
        repetition: int,
        order: int,
    ) -> Mapping[str, Any]:
        from benchlib.agent_outcomes_ct import (
            CtKnownChange,
            CtLifecycle,
            PersistentCtAttemptSupervisor,
        )

        input_root = attempt_root / "task-input"
        shutil.copytree(snapshot, input_root, symlinks=True)
        (attempt_root / "private-grader").mkdir()
        runner = self.runner_factory(attempt_root, task, arm_id)
        probe = runner.qualify_isolation(
            attempt_root,
            mutation=True,
            arm_id=arm_id,
            repo_id=task.task.repo_id,
        )
        if probe.qualification is None:
            raise RuntimeError("CT runner isolation qualification failed")
        runner.qualification = probe.qualification
        supervisor = PersistentCtAttemptSupervisor()
        lifecycle = CtLifecycle.from_manifest(
            self.lifecycle_manifest,
            miller_path=self.miller_path,
            executor=supervisor,
        )
        known_change = CtKnownChange.from_manifest(
            self.known_changes[task.task.task_id]
        )
        private_output = attempt_root / "private-grader" / "ct-run"
        spec = runner.build_ct_container_spec(
            task,
            arm_id,
            input_root,
            private_output,
            attempt_root / "runtime-artifacts" / "ct-run",
            private_output / "container.cid",
            known_change,
        )
        envelope = runner.run_ct(
            supervisor,
            lifecycle,
            spec,
            task,
            arm_id,
            repetition=repetition,
            order=order,
        )
        execution = _plain(envelope.execution)
        execution["isolation_qualification"] = {
            "passed": probe.passed,
            "qualification_sha256": probe.qualification_sha256,
            "evidence_path": probe.evidence_path,
            "prepared_setup": _plain(probe.prepared_setup),
        }
        if (
            execution.get("measured_snapshot_sha256")
            != known_change.changed_snapshot_sha256
        ):
            raise RuntimeError(
                "CT measured source does not match frozen changed snapshot"
            )
        return {"record": _plain(envelope.run_record), "execution": execution}


def _default_attempt_executor(
    campaign: Campaign, envelope: Mapping[str, Any]
) -> NativeRunnerAttemptExecutor | CtRunnerAttemptExecutor:
    transport_value = envelope["provider_transport"]
    if transport_value is None:
        raise RuntimeError("live execution requires a frozen provider transport")
    from benchlib import agent_outcomes_runner

    transport = agent_outcomes_runner.ProviderTransport(
        transport_value["provider_id"],
        transport_value["base_url"],
        transport_value["qualification_sha256"],
        transport_value["network_policy"],
    )
    prepared = None
    if envelope["prepared_environments_path"] is not None:
        prepared_type = getattr(agent_outcomes_runner, "PreparedEnvironment", None)
        if prepared_type is None:
            raise RuntimeError("prepared environment runner support is not available")
        prepared = prepared_type.from_manifest(
            Path(envelope["prepared_environments_path"]), envelope["image_reference"]
        )
    semantic_binding = None
    if envelope["comparison_mode"] == "secondary":
        semantic_binding = agent_outcomes_runner.SemanticRuntimeBinding.from_manifest(
            Path(envelope["semantic_runtime_binding_path"]),
            envelope["image_reference"],
        )

    def factory(_root: Path, _task: VerifiableTask, _arm_id: str):
        arguments = {
            "image_reference": envelope["image_reference"],
            "codex_path": envelope["codex_path"],
            "miller_path": envelope["miller_path"],
            "podman_path": envelope["podman_path"],
            "provider_transport": transport,
        }
        if prepared is not None:
            arguments["prepared_environment"] = prepared
        if semantic_binding is not None:
            arguments["semantic_runtime_binding"] = semantic_binding
        return agent_outcomes_runner.NativeAgentRunner(campaign, **arguments)

    if envelope["comparison_mode"] == "ct":
        return CtRunnerAttemptExecutor(
            factory,
            envelope["ct_lifecycle"],
            envelope["ct_known_changes"],
            envelope["miller_path"],
        )
    return NativeRunnerAttemptExecutor(factory)


def _validate_returned_identity(
    record: Mapping[str, Any],
    campaign_sha256: str,
    task: VerifiableTask,
    scheduled: Mapping[str, Any],
    execution: Mapping[str, Any],
) -> None:
    validated = validate_run_record(record)
    expected = {
        "campaign_sha256": campaign_sha256,
        "task_id": task.task.task_id,
        "arm_id": scheduled["arm_id"],
        "repetition": scheduled["repetition"],
        "order": scheduled["order"],
        "run_id": f"{task.task.task_id}-{scheduled['arm_id'].replace('+', '-')}-r{scheduled['repetition']}",
    }
    for field, value in expected.items():
        if validated.value[field] != value:
            raise ValueError(f"returned run record {field} does not match dispatch")
    output_tokens = validated.value["total_model_output_tokens"]
    reasoning_tokens = execution.get("reasoning_output_tokens")
    if reasoning_tokens is not None and (
        not isinstance(reasoning_tokens, int)
        or isinstance(reasoning_tokens, bool)
        or output_tokens is None
        or reasoning_tokens > output_tokens
    ):
        raise ValueError("reasoning output tokens cannot exceed output tokens")
    model_token_total(validated.value)


def _validate_execution_config(value: Any, campaign: Campaign) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != _EXECUTION_FIELDS:
        raise ValueError("execution config fields are invalid")
    result = _plain(value)
    mode = result["comparison_mode"]
    if mode not in _MODES:
        raise ValueError("comparison_mode is unsupported")
    if campaign.repetition_count < 5:
        raise ValueError("paired pilot requires at least five predeclared repetitions")
    for field in (
        "task_manifest_path",
        "verifier_manifest_path",
        "image_reference",
        "codex_path",
        "miller_path",
        "podman_path",
    ):
        if not isinstance(result[field], str) or not result[field]:
            raise ValueError(f"execution {field} must be non-empty")
    if not result["image_reference"].endswith(
        "@sha256:" + campaign.value["platform_toolchain_image_sha256"]
    ):
        raise ValueError(
            "execution image_reference does not match the frozen platform image digest"
        )
    for field in (
        "prepared_environments_path",
        "runtime_qualification_path",
        "semantic_runtime_binding_path",
    ):
        if result[field] is not None and (
            not isinstance(result[field], str) or not result[field]
        ):
            raise ValueError(f"execution {field} must be null or non-empty")
    if not isinstance(result["source_roots"], dict) or not result["source_roots"]:
        raise ValueError("execution source_roots must be a non-empty object")
    if (
        not isinstance(result["task_ids"], list)
        or not result["task_ids"]
        or len(set(result["task_ids"])) != len(result["task_ids"])
    ):
        raise ValueError("execution task_ids must be a unique non-empty array")
    if not isinstance(result["setup_components"], list):
        raise ValueError("execution setup_components must be an array")  # noqa: TRY004
    for component in result["setup_components"]:
        if set(component) != {
            "environment_id",
            "component_id",
            "bucket",
            "applies_to_arms",
            "cost",
            "wall_time_seconds",
            "evidence_sha256",
        }:
            raise ValueError("setup component fields are invalid")
        if component["bucket"] not in {
            "native",
            "miller-lexical",
            "semantic",
            "ct",
            "shared",
        }:
            raise ValueError("setup component bucket is invalid")
        applies_to = component["applies_to_arms"]
        campaign_arms = {arm["arm_id"] for arm in campaign.value["arms"]}
        if (
            not isinstance(applies_to, list)
            or not applies_to
            or len(set(applies_to)) != len(applies_to)
            or not set(applies_to) <= campaign_arms
        ):
            raise ValueError("setup component applies_to_arms is invalid")
        for field in ("cost", "wall_time_seconds"):
            if component[field] is not None and not _nonnegative_number(
                component[field]
            ):
                raise ValueError(f"setup component {field} must be null or nonnegative")
        _sha256(component["evidence_sha256"], "setup component evidence_sha256")
    amortized_setup_costs(result["setup_components"])
    if (
        result["prepared_environments_path"] is not None
        and not result["setup_components"]
    ):
        raise ValueError(
            "prepared environments require explicit setup accounting components"
        )
    if result["prepared_environments_path"] is not None:
        from benchlib.agent_outcomes_runner import PreparedEnvironment

        PreparedEnvironment.from_manifest(
            Path(result["prepared_environments_path"]), result["image_reference"]
        )
    sample_size = result["sample_size_plan"]
    required_sample = {"phase", "pilot_variance", "approved_budget_sha256"}
    if not isinstance(sample_size, dict) or set(sample_size) != required_sample:
        raise ValueError("sample_size_plan fields are invalid")
    if sample_size["phase"] not in {"pilot", "powered"}:
        raise ValueError("sample_size_plan phase is invalid")
    variance = sample_size["pilot_variance"]
    if sample_size["phase"] == "pilot" and variance is not None:
        raise ValueError("pilot sample size cannot claim pilot variance")
    if sample_size["phase"] == "powered" and not _nonnegative_number(variance):
        raise ValueError("powered sample size requires measured pilot variance")
    _sha256(
        sample_size["approved_budget_sha256"], "sample_size_plan approved_budget_sha256"
    )
    ct_lifecycle = result["ct_lifecycle"]
    if mode == "ct":
        required = {
            "schema_version",
            "enabled_arm",
            "command_timeout_seconds",
            "readiness_timeout_seconds",
            "poll_interval_seconds",
        }
        if not isinstance(ct_lifecycle, dict) or set(ct_lifecycle) != required:
            raise ValueError(
                "CT comparison requires a typed enable/start/inventory warmup lifecycle"
            )
        if ct_lifecycle["schema_version"] != 1:
            raise ValueError("CT lifecycle schema_version is unsupported")
        if ct_lifecycle["enabled_arm"] != "native+miller-lexical":
            raise ValueError("CT enabled_arm is invalid")
        for field, maximum in (
            ("command_timeout_seconds", 300),
            ("readiness_timeout_seconds", 600),
        ):
            value = ct_lifecycle[field]
            if (
                not isinstance(value, int)
                or isinstance(value, bool)
                or value < 1
                or value > maximum
            ):
                raise ValueError(f"CT {field} is invalid")
        poll = ct_lifecycle["poll_interval_seconds"]
        if not _nonnegative_number(poll) or poll <= 0 or poll > 10:
            raise ValueError("CT poll_interval_seconds is invalid")
        changes = result["ct_known_changes"]
        if not isinstance(changes, dict) or not changes:
            raise ValueError("CT comparison requires per-task known changes")
        for task_id, change in changes.items():
            if not isinstance(task_id, str) or not task_id:
                raise ValueError("CT known change task_id is invalid")
            fields = {
                "path",
                "sha256",
                "changed_paths",
                "baseline_snapshot_sha256",
                "changed_snapshot_sha256",
                "expected_ct_test_case_ids",
                "expected_baseline_ct_verdict",
                "expected_baseline_ct_failure_ids",
                "qualification_evidence_sha256",
            }
            if not isinstance(change, dict) or set(change) != fields:
                raise ValueError("CT known change fields are invalid")
            if _file_sha256(Path(change["path"])) != change["sha256"]:
                raise ValueError("CT known change patch digest does not match")
            for field in (
                "sha256",
                "baseline_snapshot_sha256",
                "changed_snapshot_sha256",
                "qualification_evidence_sha256",
            ):
                _sha256(change[field], f"CT known change {field}")
            paths = change["changed_paths"]
            if (
                not isinstance(paths, list)
                or not paths
                or len(set(paths)) != len(paths)
            ):
                raise ValueError("CT known change changed_paths is invalid")
            for path in paths:
                _safe_relative_path(path, "CT known change changed path")
            case_ids = change["expected_ct_test_case_ids"]
            if (
                not isinstance(case_ids, list)
                or not case_ids
                or len(set(case_ids)) != len(case_ids)
                or not all(isinstance(case_id, str) and case_id for case_id in case_ids)
            ):
                raise ValueError("CT known change expected test case IDs are invalid")
            baseline_verdict = change["expected_baseline_ct_verdict"]
            if baseline_verdict not in {"green", "red", "partial"}:
                raise ValueError("CT known change baseline verdict is invalid")
            baseline_failure_ids = change["expected_baseline_ct_failure_ids"]
            if (
                not isinstance(baseline_failure_ids, list)
                or len(set(baseline_failure_ids)) != len(baseline_failure_ids)
                or not all(
                    isinstance(case_id, str) and case_id
                    for case_id in baseline_failure_ids
                )
                or (baseline_verdict == "green") != (not baseline_failure_ids)
            ):
                raise ValueError("CT known change baseline failure IDs are invalid")
    elif ct_lifecycle is not None or result["ct_known_changes"] is not None:
        raise ValueError("non-CT comparison cannot contain CT lifecycle data")
    if mode != "secondary" and result["runtime_qualification_path"] is not None:
        raise ValueError(
            "only a secondary campaign may contain semantic qualification bytes"
        )
    if mode != "secondary" and result["semantic_runtime_binding_path"] is not None:
        raise ValueError(
            "only a secondary campaign may contain semantic runtime image binding"
        )
    forbidden_buckets = {
        "primary": {"semantic", "ct"},
        "secondary": {"ct"},
        "ct": {"semantic"},
    }[mode]
    if any(
        component["bucket"] in forbidden_buckets
        for component in result["setup_components"]
    ):
        raise ValueError(f"{mode} campaign contains setup from another comparison")
    transport = result["provider_transport"]
    if transport is not None:
        required_transport = {
            "provider_id",
            "base_url",
            "qualification_path",
            "qualification_sha256",
            "network_policy",
        }
        if not isinstance(transport, dict) or set(transport) != required_transport:
            raise ValueError("provider_transport fields are invalid")
        from benchlib.agent_outcomes_runner import ProviderTransport

        ProviderTransport(
            transport["provider_id"],
            transport["base_url"],
            transport["qualification_sha256"],
            transport["network_policy"],
        )
        if transport["network_policy"] != campaign.value["network_policy"]:
            raise ValueError(
                "provider transport network policy does not match campaign"
            )
        if (
            _file_sha256(Path(transport["qualification_path"]))
            != transport["qualification_sha256"]
        ):
            raise ValueError("provider transport qualification bytes do not match")
        qualification = _load_json(transport["qualification_path"])
        expected_qualification = {
            "schema": "agent-outcomes-provider-transport-v1",
            "provider_id": transport["provider_id"],
            "base_url": transport["base_url"],
            "network_policy": transport["network_policy"],
            "passed": True,
        }
        if qualification != expected_qualification:
            raise ValueError("provider transport qualification content does not match")
    if mode == "secondary":
        semantic = next(
            arm
            for arm in campaign.value["arms"]
            if arm["arm_id"] == "native+miller-semantic"
        )
        qualification_path = result["runtime_qualification_path"]
        if qualification_path is None:
            raise ValueError(
                "secondary campaign requires frozen semantic qualification bytes"
            )
        if (
            _file_sha256(Path(qualification_path))
            != semantic["runtime_qualification_sha256"]
        ):
            raise ValueError(
                "semantic qualification digest does not match the campaign arm"
            )
        qualification = _load_json(qualification_path)
        if not isinstance(qualification, dict) or qualification.get(
            "runtime_identity"
        ) != _plain(semantic["runtime_identity"]):
            raise ValueError(
                "semantic qualification runtime identity does not match the campaign arm"
            )
        if not any(
            component["bucket"] == "semantic"
            for component in result["setup_components"]
        ):
            raise ValueError("secondary campaign requires semantic setup accounting")
        binding_path = result["semantic_runtime_binding_path"]
        if binding_path is None:
            raise ValueError(
                "secondary campaign requires semantic runtime image binding"
            )
        binding = _load_json(binding_path)
        binding_fields = {
            "schema",
            "image_digest",
            "runtime_identity",
            "runtime_qualification_sha256",
            "observation_evidence_sha256",
            "passed",
        }
        if not isinstance(binding, dict) or set(binding) != binding_fields:
            raise ValueError("semantic runtime image binding fields are invalid")
        if (
            binding["schema"] != "agent-outcomes-semantic-image-binding-v1"
            or binding["passed"] is not True
            or binding["image_digest"]
            != campaign.value["platform_toolchain_image_sha256"]
            or binding["runtime_identity"] != _plain(semantic["runtime_identity"])
            or binding["runtime_qualification_sha256"]
            != semantic["runtime_qualification_sha256"]
        ):
            raise ValueError("semantic runtime image binding does not match campaign")
        _sha256(
            binding["observation_evidence_sha256"],
            "semantic runtime image observation evidence",
        )
    if mode == "ct" and not any(
        component["bucket"] == "ct" for component in result["setup_components"]
    ):
        raise ValueError("CT campaign requires CT setup accounting")
    return result


def _validate_mode(mode: str, campaign: Campaign, tasks: Sequence[Any]) -> None:
    arms = tuple(arm["arm_id"] for arm in campaign.value["arms"])
    if set(arms) != set(_MODES[mode]) or len(arms) != 2:
        raise ValueError(f"{mode} campaign arms are invalid")
    if mode == "secondary" and any(task.workflow != "concept" for task in tasks):
        raise ValueError("secondary campaign is restricted to concept tasks")
    if mode == "ct" and any(task.workflow != "test_selection" for task in tasks):
        raise ValueError("CT campaign is restricted to test_selection tasks")
    semantic = next(
        (
            arm
            for arm in campaign.value["arms"]
            if arm["arm_id"] == "native+miller-semantic"
        ),
        None,
    )
    if mode == "secondary" and semantic is None:
        raise ValueError("secondary campaign requires semantic runtime identity")


def _validate_ct_task_changes(
    execution: Mapping[str, Any], tasks: Sequence[Any]
) -> None:
    if execution["comparison_mode"] != "ct":
        return
    changes = execution["ct_known_changes"]
    if set(changes) != {task.task_id for task in tasks}:
        raise ValueError("CT known changes must bind every selected task exactly once")
    for task in tasks:
        change = changes[task.task_id]
        if change["baseline_snapshot_sha256"] != task.snapshot_sha256:
            raise ValueError("CT known change baseline does not match task snapshot")
        _verify_ct_known_change(Path(execution["source_roots"][task.task_id]), change)


def _verify_ct_known_change(
    source_root: Path, change_manifest: Mapping[str, Any]
) -> None:
    from benchlib.agent_outcomes_ct import CtKnownChange

    change = CtKnownChange.from_manifest(change_manifest)
    if source_snapshot_sha256(source_root) != change.baseline_snapshot_sha256:
        raise ValueError("CT known change source does not match baseline snapshot")
    with tempfile.TemporaryDirectory(prefix="agent-outcomes-ct-freeze-") as directory:
        candidate = Path(directory) / "candidate"
        shutil.copytree(
            source_root,
            candidate,
            symlinks=True,
            ignore=shutil.ignore_patterns(".git"),
        )
        patch_bytes = change.path.read_bytes()
        for arguments in (("--check",), ()):
            completed = subprocess.run(
                [
                    "git",
                    "-c",
                    "core.hooksPath=/dev/null",
                    "-c",
                    "core.fsmonitor=false",
                    "apply",
                    *arguments,
                    "--whitespace=nowarn",
                    "-",
                ],
                cwd=candidate,
                input=patch_bytes,
                capture_output=True,
                check=False,
                timeout=30,
                env={
                    "PATH": os.defpath,
                    "GIT_CONFIG_NOSYSTEM": "1",
                    "GIT_CONFIG_GLOBAL": "/dev/null",
                },
            )
            if completed.returncode != 0:
                raise ValueError(
                    "CT known change does not apply to the frozen baseline"
                )
        if source_snapshot_sha256(candidate) != change.changed_snapshot_sha256:
            raise ValueError("CT known change result does not match changed snapshot")


def _dry_record(
    campaign_sha: str,
    task: VerifiableTask,
    plan: Mapping[str, Any],
    index: int,
    campaign: Campaign,
) -> Mapping[str, Any]:
    outcomes = (
        "correct",
        "incorrect",
        "infrastructure_void",
        "product_error",
        "timeout",
    )
    outcome = outcomes[(index // 2) % len(outcomes)]
    token_base = 100 + index
    tokens = (token_base, index % 7, 20 + index)
    cost = 0.0
    record = {
        "contract_id": "agent-outcomes-v1",
        "campaign_sha256": campaign_sha,
        "run_id": f"{task.task.task_id}-{plan['arm_id'].replace('+', '-')}-r{plan['repetition']}",
        "task_id": task.task.task_id,
        "arm_id": plan["arm_id"],
        "repetition": plan["repetition"],
        "order": plan["order"],
        "outcome": outcome,
        "verifier_evidence_sha256": _digest({"dry_run": True, "index": index}),
        "wall_time_seconds": float(index + 1),
        "native_tool_counts": {"command": index % 3},
        "miller_calls": 0 if plan["arm_id"] == "native" else index % 4,
        "total_model_input_tokens": tokens[0],
        "total_model_cached_tokens": tokens[1],
        "total_model_output_tokens": tokens[2],
        "raw_event_sha256": _digest({"dry_event": index}),
        "price_derived_cost": cost,
    }
    return _plain(validate_run_record(record).value)


def _infrastructure_void_record(
    campaign_sha: str,
    task: VerifiableTask,
    plan: Mapping[str, Any],
    index: int,
    error: Exception,
) -> Mapping[str, Any]:
    return {
        "contract_id": "agent-outcomes-v1",
        "campaign_sha256": campaign_sha,
        "run_id": f"{task.task.task_id}-{plan['arm_id'].replace('+', '-')}-r{plan['repetition']}",
        "task_id": task.task.task_id,
        "arm_id": plan["arm_id"],
        "repetition": plan["repetition"],
        "order": plan["order"],
        "outcome": "infrastructure_void",
        "verifier_evidence_sha256": _digest(
            {"adapter_error_type": type(error).__name__}
        ),
        "wall_time_seconds": 0.0,
        "native_tool_counts": {},
        "miller_calls": 0,
        "total_model_input_tokens": None,
        "total_model_cached_tokens": None,
        "total_model_output_tokens": None,
        "raw_event_sha256": _digest({"missing_raw_event": index}),
        "price_derived_cost": None,
    }


def _derive_cost(record: Mapping[str, Any], campaign: Campaign) -> Mapping[str, Any]:
    value = _plain(record)
    pricing = campaign.value["pricing"]
    if pricing is None:
        value["price_derived_cost"] = None
        return value
    value["price_derived_cost"] = price_model_usage(value, pricing)
    return value


def _load_tasks(path: Path) -> tuple[list[Any], str]:
    records = []
    task_ids = set()
    for line_number, line in enumerate(
        path.read_text(encoding="utf-8").splitlines(), 1
    ):
        if line.strip():
            try:
                task = validate_task(
                    json.loads(
                        line,
                        object_pairs_hook=_unique_object,
                        parse_constant=_reject_constant,
                    )
                )
                if task.task_id in task_ids:
                    raise ValueError(f"duplicate task_id: {task.task_id}")
                task_ids.add(task.task_id)
                records.append(task)
            except (json.JSONDecodeError, ValueError) as exc:
                raise ValueError(f"tasks line {line_number}: {exc}") from exc
    if not records:
        raise ValueError("task manifest must not be empty")
    return records, _file_sha256(path)


def _verifier_map(records: Any) -> Mapping[str, Any]:
    if not isinstance(records, list):
        raise ValueError("verifier manifest must be an array")  # noqa: TRY004
    verifiers = {}
    for record in records:
        verifier = validate_verifier(record)
        if verifier.verifier_id in verifiers:
            raise ValueError(f"duplicate verifier: {verifier.verifier_id}")
        verifiers[verifier.verifier_id] = verifier
    return verifiers


def _task_mapping(task: Any) -> Mapping[str, Any]:
    return {
        "contract_id": task.contract_id,
        "task_id": task.task_id,
        "repo_id": task.repo_id,
        "source_commit": task.source_commit,
        "snapshot_sha256": task.snapshot_sha256,
        "language": task.language,
        "workflow": task.workflow,
        "prompt": task.prompt,
        "verifier_id": task.verifier_id,
        "allowed_write_paths": list(task.allowed_write_paths),
        "max_wall_seconds": task.max_wall_seconds,
        "max_model_tokens": task.max_model_tokens,
    }


def _tokens(record: Mapping[str, Any]) -> int | None:
    return model_token_total(record)


def _load_json(path: str | Path) -> Any:
    try:
        return json.loads(
            Path(path).read_text(encoding="utf-8"),
            object_pairs_hook=_unique_object,
            parse_constant=_reject_constant,
        )
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"JSON document: {exc}") from exc


def _write_json(path: Path, value: Mapping[str, Any]) -> None:
    with path.open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(value, sort_keys=True, indent=2) + "\n")
        stream.flush()
        os.fsync(stream.fileno())


def _append_json_line(stream, value: Mapping[str, Any]) -> None:
    stream.write(json.dumps(value, sort_keys=True) + "\n")
    stream.flush()
    os.fsync(stream.fileno())


def _unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"duplicate JSON key: {key}")
        value[key] = item
    return value


def _reject_constant(value):
    raise ValueError(f"non-finite JSON number: {value}")


def _plain(value: Any) -> Any:
    if isinstance(value, Mapping):
        return {key: _plain(item) for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return [_plain(item) for item in value]
    return value


def _digest(value: Any) -> str:
    return hashlib.sha256(
        json.dumps(_plain(value), sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()


def _file_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _optional_file_sha(path: str | None) -> str | None:
    return None if path is None else _file_sha256(Path(path))


def _nonnegative_number(value: Any) -> bool:
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(value)
        and value >= 0
    )


def _canonicalize_execution_paths(
    execution: Mapping[str, Any], base: Path
) -> dict[str, Any]:
    result = _plain(execution)

    def canonical(value: str) -> str:
        path = Path(value)
        return str((path if path.is_absolute() else base / path).resolve())

    for field in ("task_manifest_path", "verifier_manifest_path"):
        if not isinstance(result[field], str) or not result[field]:
            raise ValueError(f"execution {field} must be non-empty")
        result[field] = canonical(result[field])
    for field in (
        "prepared_environments_path",
        "runtime_qualification_path",
        "semantic_runtime_binding_path",
    ):
        if result[field] is not None:
            result[field] = canonical(result[field])
    if not isinstance(result["source_roots"], dict) or not result["source_roots"]:
        raise ValueError("execution source_roots must be a non-empty object")
    if not all(
        isinstance(task_id, str) and task_id and isinstance(path, str) and path
        for task_id, path in result["source_roots"].items()
    ):
        raise ValueError(
            "execution source_roots keys and paths must be non-empty strings"
        )
    result["source_roots"] = {
        task_id: canonical(path) for task_id, path in result["source_roots"].items()
    }
    if result["provider_transport"] is not None:
        if not isinstance(result["provider_transport"], dict):
            raise ValueError("provider_transport must be null or an object")
        transport = _plain(result["provider_transport"])
        if isinstance(transport.get("qualification_path"), str):
            transport["qualification_path"] = canonical(transport["qualification_path"])
        result["provider_transport"] = transport
    if isinstance(result["ct_known_changes"], dict):
        for change in result["ct_known_changes"].values():
            if isinstance(change, dict) and isinstance(change.get("path"), str):
                change["path"] = canonical(change["path"])
    return result


def _sha256(value: Any, label: str) -> None:
    if (
        not isinstance(value, str)
        or len(value) != 64
        or any(character not in "0123456789abcdef" for character in value)
    ):
        raise ValueError(f"{label} must be a lowercase SHA-256 digest")


def _safe_relative_path(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value or "\\" in value or "\0" in value:
        raise ValueError(f"{label} must be a repository-relative POSIX path")
    path = PurePosixPath(value)
    if (
        path.is_absolute()
        or path.as_posix() != value
        or any(part in {"", ".", ".."} for part in path.parts)
    ):
        raise ValueError(f"{label} must be a repository-relative POSIX path")
    return value


def _setup_for_arm(
    arm_id: str, components: Sequence[Mapping[str, Any]]
) -> list[Mapping[str, Any]]:
    return [
        component for component in components if arm_id in component["applies_to_arms"]
    ]


def _s1_metrics(summary: Mapping[str, Any]) -> Mapping[str, Any]:
    attempts = summary["attempt_count"]
    unknown = {"value": None, "coverage": {"measured": 0, "total": attempts}}
    return {
        "correctness": {
            "verified_successes": summary["success_count"],
            "denominator": summary["scored_attempt_count"],
            "rate": summary["success_rate"],
        },
        "sufficient_evidence": _plain(unknown),
        "calls": {
            "native": summary["native_tool_counts"],
            "miller": summary["miller_calls"],
        },
        "tokens": {
            "total": summary["total_model_tokens"],
            "measured_subtotal": summary["measured_model_tokens_subtotal"],
            "coverage": summary["token_coverage"],
        },
        "wall_time_seconds": {
            "total": summary["total_wall_time_seconds"],
            "coverage": summary["wall_time_coverage"],
        },
        "retries": _plain(unknown),
        "irrelevant_output": _plain(unknown),
        "retrieval_diagnostics": {
            "miller_calls": summary["miller_calls"],
            "quality": None,
            "quality_coverage": {"measured": 0, "total": attempts},
        },
        "fallback": _plain(unknown),
    }


def _validate_s1_metrics(metrics: Mapping[str, Any]) -> None:
    correctness = metrics["correctness"]
    if not isinstance(correctness, dict) or set(correctness) != {
        "verified_successes",
        "denominator",
        "rate",
    }:
        raise ValueError("S1 correctness metric fields are invalid")
    _count(correctness["verified_successes"], "S1 verified successes")
    _count(correctness["denominator"], "S1 correctness denominator")
    if correctness["verified_successes"] > correctness["denominator"]:
        raise ValueError("S1 verified successes exceed denominator")
    _rate_or_null(correctness["rate"], "S1 correctness rate")
    calls = metrics["calls"]
    if not isinstance(calls, dict) or set(calls) != {"native", "miller"}:
        raise ValueError("S1 calls metric fields are invalid")
    if not isinstance(calls["native"], dict):
        raise ValueError("S1 native calls must be an object")  # noqa: TRY004
    for value in calls["native"].values():
        _count(value, "S1 native call count")
    _count(calls["miller"], "S1 Miller call count")
    tokens = metrics["tokens"]
    if not isinstance(tokens, dict) or set(tokens) != {
        "total",
        "measured_subtotal",
        "coverage",
    }:
        raise ValueError("S1 tokens metric fields are invalid")
    _optional_count(tokens["total"], "S1 total tokens")
    _count(tokens["measured_subtotal"], "S1 measured tokens")
    _coverage(tokens["coverage"], "S1 token coverage")
    wall = metrics["wall_time_seconds"]
    if not isinstance(wall, dict) or set(wall) != {"total", "coverage"}:
        raise ValueError("S1 wall metric fields are invalid")
    if wall["total"] is not None and not _nonnegative_number(wall["total"]):
        raise ValueError("S1 total wall time is invalid")
    _coverage(wall["coverage"], "S1 wall coverage")
    for name in (
        "sufficient_evidence",
        "retries",
        "irrelevant_output",
        "fallback",
    ):
        value = metrics[name]
        if not isinstance(value, dict) or set(value) != {"value", "coverage"}:
            raise ValueError(f"S1 {name} metric fields are invalid")
        if value["value"] is not None:
            raise ValueError(f"S1 {name} is not measured by this controller")
        _coverage(value["coverage"], f"S1 {name} coverage")
    retrieval = metrics["retrieval_diagnostics"]
    if not isinstance(retrieval, dict) or set(retrieval) != {
        "miller_calls",
        "quality",
        "quality_coverage",
    }:
        raise ValueError("S1 retrieval diagnostics fields are invalid")
    _count(retrieval["miller_calls"], "S1 retrieval Miller calls")
    if retrieval["quality"] is not None:
        raise ValueError("S1 retrieval quality is not measured by this controller")
    _coverage(retrieval["quality_coverage"], "S1 retrieval quality coverage")


def _coverage(value: Any, label: str) -> None:
    if not isinstance(value, dict) or set(value) != {"measured", "total"}:
        raise ValueError(f"{label} fields are invalid")
    _count(value["measured"], f"{label} measured")
    _count(value["total"], f"{label} total")
    if value["measured"] > value["total"]:
        raise ValueError(f"{label} measured exceeds total")


def _count(value: Any, label: str) -> None:
    if not isinstance(value, int) or isinstance(value, bool) or value < 0:
        raise ValueError(f"{label} must be a nonnegative integer")


def _optional_count(value: Any, label: str) -> None:
    if value is not None:
        _count(value, label)


def _rate_or_null(value: Any, label: str) -> None:
    if value is not None and (not _nonnegative_number(value) or value > 1):
        raise ValueError(f"{label} must be null or between zero and one")
