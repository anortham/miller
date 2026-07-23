"""Markdown report helpers for benchmark result rows."""

from __future__ import annotations

import math
import re
import statistics
from typing import Any


_AGGREGATE_FIELDS = frozenset(
    {
        "contract_id",
        "schema_version",
        "decision_scope",
        "decision_verdict",
        "action_verdict",
        "task_count",
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
    }
)
_IDENTITY_FIELDS = frozenset(
    {
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
    }
)
_TAKEOVER_CAPABILITIES = frozenset(
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
_WORKFLOW_CLASSES = frozenset(
    {
        "exact_lookup",
        "concept_search",
        "docs_config",
        "context_assembly",
        "references_trace",
        "impact_tests",
    }
)
_FAILURE_REASONS = frozenset(
    {
        "incorrect",
        "insufficient_evidence",
        "budget_exceeded",
        "disallowed_tool",
        "product_error",
        "invalid_answer",
    }
)
_OUTCOMES = frozenset(
    {"success", "empty", "refusal", "hard_error", "wrong_answer"}
)
_VERDICTS = frozenset({"pass", "fail", "not_decisional"})


def project_safe_aggregate(
    aggregate: dict[str, Any],
    identity: dict[str, Any],
    *,
    unresolved_void_count: int,
    private_evidence_sha256: dict[str, str],
) -> dict[str, Any]:
    _require_fields(aggregate, _AGGREGATE_FIELDS, "aggregate")
    _require_fields(identity, _IDENTITY_FIELDS, "identity")
    if not isinstance(unresolved_void_count, int) or isinstance(
        unresolved_void_count, bool
    ):
        raise ValueError("unresolved void count must be an integer")
    if unresolved_void_count < 0:
        raise ValueError("unresolved void count cannot be negative")
    if not isinstance(private_evidence_sha256, dict):
        raise ValueError("private evidence hashes must be an object")
    if not private_evidence_sha256:
        raise ValueError("private evidence hashes are required")
    for name, digest in private_evidence_sha256.items():
        if not isinstance(name, str) or re.fullmatch(r"[a-z][a-z0-9_]*", name) is None:
            raise ValueError("private evidence hash labels must be public identifiers")
        if not isinstance(digest, str) or re.fullmatch(r"[0-9a-f]{64}", digest) is None:
            raise ValueError("private evidence hashes must be lowercase SHA-256")
    _validate_identity(identity)
    corpus_role = identity["corpus_role"]
    decision_scope = identity["decision_scope"]
    expected_identity_values = {
        "contract_id": identity["contract_id"],
        "schema_version": identity["schema_version"],
        "decision_scope": decision_scope,
        "task_count": identity["selected_task_count"],
    }
    for field, expected in expected_identity_values.items():
        if aggregate[field] != expected:
            raise ValueError(f"aggregate and identity {field} mismatch")
    decisional = corpus_role == "decision" and decision_scope == "full"
    if decisional:
        selected_capabilities = set(identity["selected_capability_ids"])
        if selected_capabilities != _TAKEOVER_CAPABILITIES:
            raise ValueError("decision output requires all takeover capabilities")
    if corpus_role == "decision" and unresolved_void_count != 0:
        raise ValueError("decision output requires zero unresolved voids")
    normalized = _validate_aggregate(aggregate, decision_output=decisional)
    decision_verdict = (
        "pass"
        if decisional
        and normalized["relevance"]["verdict"] == "pass"
        and normalized["correctness"]["verdict"] == "pass"
        and normalized["efficiency"]["verdict"] == "pass"
        and normalized["action_verdict"] == "pass"
        else "fail" if decisional else "not_decisional"
    )
    return {
        "contract_id": identity["contract_id"],
        "schema_version": identity["schema_version"],
        "corpus_role": corpus_role,
        "decision_scope": decision_scope,
        "parent_manifest_sha256": identity["parent_manifest_sha256"],
        "snapshot_manifest_sha256": identity["snapshot_manifest_sha256"],
        "runtime_identity_sha256": identity["runtime_identity_sha256"],
        "selection_sha256": identity["selection_sha256"],
        "selected_capability_ids": list(identity["selected_capability_ids"]),
        "selected_task_count": identity["selected_task_count"],
        **{
            key: normalized[key]
            for key in (
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
                "action_verdict",
            )
        },
        "unresolved_void_count": unresolved_void_count,
        "private_evidence_sha256": dict(sorted(private_evidence_sha256.items())),
        "decision_verdict": decision_verdict,
    }


def _validate_identity(identity: dict[str, Any]) -> None:
    if identity["contract_id"] != "takeover-evaluation-v1":
        raise ValueError("identity contract_id is unsupported")
    if identity["schema_version"] != 1:
        raise ValueError("identity schema_version is unsupported")
    if identity["corpus_role"] not in {"calibration", "decision"}:
        raise ValueError("identity corpus_role is unsupported")
    if identity["decision_scope"] not in {"subset", "full"}:
        raise ValueError("identity decision_scope is unsupported")
    for field in (
        "parent_manifest_sha256",
        "snapshot_manifest_sha256",
        "runtime_identity_sha256",
        "selection_sha256",
    ):
        if (
            not isinstance(identity[field], str)
            or re.fullmatch(r"[0-9a-f]{64}", identity[field]) is None
        ):
            raise ValueError(f"identity {field} must be lowercase SHA-256")
    capabilities = identity["selected_capability_ids"]
    if (
        not isinstance(capabilities, list)
        or any(not isinstance(value, str) for value in capabilities)
        or capabilities != sorted(set(capabilities))
        or not set(capabilities).issubset(_TAKEOVER_CAPABILITIES)
    ):
        raise ValueError("identity selected capabilities are invalid")
    _require_nonnegative_int(identity["selected_task_count"], "identity.selected_task_count")


def _validate_aggregate(
    aggregate: dict[str, Any], *, decision_output: bool
) -> dict[str, Any]:
    if aggregate["contract_id"] != "takeover-evaluation-v1":
        raise ValueError("aggregate contract_id is unsupported")
    if aggregate["schema_version"] != 1:
        raise ValueError("aggregate schema_version is unsupported")
    if aggregate["decision_scope"] not in {"subset", "full"}:
        raise ValueError("aggregate decision_scope is unsupported")
    if aggregate["decision_verdict"] not in _VERDICTS:
        raise ValueError("aggregate decision_verdict is invalid")
    if aggregate["action_verdict"] not in {"pass", "fail"}:
        raise ValueError("aggregate action_verdict is invalid")
    task_count = _require_positive_int(aggregate["task_count"], "aggregate.task_count")
    completion = _validate_completion(aggregate["completion"], task_count, "completion")
    outcome_counts = _validate_outcome_counts(
        aggregate["outcome_counts"], task_count, "outcome_counts"
    )
    relevance = _validate_relevance(aggregate["relevance"], task_count)
    correctness = _validate_correctness(
        aggregate["correctness"], completion, task_count
    )
    baseline = _validate_arm_metrics(aggregate["baseline"], "baseline")
    candidate = _validate_arm_metrics(aggregate["candidate"], "candidate")
    efficiency = _validate_efficiency(
        aggregate["efficiency"], completion, baseline, candidate
    )
    action_verdict = (
        "pass"
        if correctness["verdict"] == "pass" and efficiency["verdict"] == "pass"
        else "fail"
    )
    failure_counts = _validate_failure_counts(aggregate["failure_counts"])
    by_workflow = _validate_subgroups(
        aggregate["by_workflow"], "by_workflow", _WORKFLOW_CLASSES
    )
    by_capability = _validate_subgroups(
        aggregate["by_capability"], "by_capability", _TAKEOVER_CAPABILITIES
    )
    by_repo = _validate_dynamic_subgroups(
        aggregate["by_repo"], "by_repo", decision_output
    )
    by_language = _validate_dynamic_subgroups(
        aggregate["by_language"], "by_language", decision_output
    )
    return {
        "completion": completion,
        "outcome_counts": outcome_counts,
        "relevance": relevance,
        "correctness": correctness,
        "efficiency": efficiency,
        "baseline": baseline,
        "candidate": candidate,
        "failure_counts": failure_counts,
        "by_workflow": by_workflow,
        "by_capability": by_capability,
        "by_repo": by_repo,
        "by_language": by_language,
        "action_verdict": action_verdict,
    }


def _validate_completion(value: Any, task_count: int, path: str) -> dict[str, int]:
    fields = frozenset(
        {"both_correct", "baseline_only", "candidate_only", "neither_correct"}
    )
    _require_fields(value, fields, path)
    result = {
        field: _require_nonnegative_int(value[field], f"{path}.{field}")
        for field in fields
    }
    if sum(result.values()) != task_count:
        raise ValueError(f"{path} counts do not equal task_count")
    return result


def _validate_outcome_counts(
    value: Any, task_count: int, path: str
) -> dict[str, dict[str, int]]:
    _require_fields(value, frozenset({"baseline", "candidate"}), path)
    result: dict[str, dict[str, int]] = {}
    for role in ("baseline", "candidate"):
        role_value = value[role]
        _require_fields(role_value, _OUTCOMES, f"{path}.{role}")
        counts = {
            outcome: _require_nonnegative_int(
                role_value[outcome], f"{path}.{role}.{outcome}"
            )
            for outcome in _OUTCOMES
        }
        if sum(counts.values()) != task_count:
            raise ValueError(f"{path}.{role} counts do not equal task_count")
        result[role] = counts
    return result


def _validate_relevance(value: Any, task_count: int) -> dict[str, Any]:
    fields = frozenset({"verdict", "task_count", "baseline", "candidate"})
    _require_fields(value, fields, "relevance")
    if value["verdict"] not in {"pass", "fail"}:
        raise ValueError("relevance.verdict is invalid")
    relevant_task_count = _require_positive_int(
        value["task_count"], "relevance.task_count"
    )
    if relevant_task_count > task_count:
        raise ValueError("relevance.task_count exceeds task_count")
    baseline = _validate_relevance_metrics(value["baseline"], "relevance.baseline")
    candidate = _validate_relevance_metrics(
        value["candidate"], "relevance.candidate"
    )
    passed = all(
        candidate[field] >= baseline[field]
        for field in ("recall_at_6", "ndcg_at_6", "mrr", "top_1")
    )
    return {
        "verdict": "pass" if passed else "fail",
        "task_count": relevant_task_count,
        "baseline": baseline,
        "candidate": candidate,
    }


def _validate_relevance_metrics(value: Any, path: str) -> dict[str, float]:
    fields = frozenset({"recall_at_6", "ndcg_at_6", "mrr", "top_1"})
    _require_fields(value, fields, path)
    result = {
        field: _require_number(value[field], f"{path}.{field}") for field in fields
    }
    if any(number < 0 or number > 1 for number in result.values()):
        raise ValueError(f"{path} metrics must be between zero and one")
    return result


def _validate_correctness(
    value: Any, completion: dict[str, int], task_count: int
) -> dict[str, Any]:
    fields = frozenset(
        {
            "verdict",
            "baseline_correct_count",
            "candidate_correct_count",
            "critical_loss_count",
            "baseline_wrong_action_task_count",
            "candidate_wrong_action_task_count",
            "baseline_wrong_action_rate",
            "candidate_wrong_action_rate",
        }
    )
    _require_fields(value, fields, "correctness")
    if value["verdict"] not in {"pass", "fail"}:
        raise ValueError("correctness.verdict is invalid")
    integers = {
        field: _require_nonnegative_int(value[field], f"correctness.{field}")
        for field in fields
        if field not in {"verdict", "baseline_wrong_action_rate", "candidate_wrong_action_rate"}
    }
    expected_baseline = completion["both_correct"] + completion["baseline_only"]
    expected_candidate = completion["both_correct"] + completion["candidate_only"]
    if integers["baseline_correct_count"] != expected_baseline:
        raise ValueError("correctness baseline count does not match completion")
    if integers["candidate_correct_count"] != expected_candidate:
        raise ValueError("correctness candidate count does not match completion")
    baseline_rate = _require_number(
        value["baseline_wrong_action_rate"], "correctness.baseline_wrong_action_rate"
    )
    candidate_rate = _require_number(
        value["candidate_wrong_action_rate"], "correctness.candidate_wrong_action_rate"
    )
    expected_baseline_rate = integers["baseline_wrong_action_task_count"] / task_count
    expected_candidate_rate = integers["candidate_wrong_action_task_count"] / task_count
    if not math.isclose(baseline_rate, expected_baseline_rate, rel_tol=0, abs_tol=1e-12):
        raise ValueError("correctness baseline wrong-action rate is inconsistent")
    if not math.isclose(candidate_rate, expected_candidate_rate, rel_tol=0, abs_tol=1e-12):
        raise ValueError("correctness candidate wrong-action rate is inconsistent")
    passed = (
        integers["candidate_correct_count"] >= integers["baseline_correct_count"]
        and integers["critical_loss_count"] == 0
        and candidate_rate <= baseline_rate
    )
    return {
        "verdict": "pass" if passed else "fail",
        **integers,
        "baseline_wrong_action_rate": baseline_rate,
        "candidate_wrong_action_rate": candidate_rate,
    }


def _validate_arm_metrics(value: Any, path: str) -> dict[str, float | None]:
    fields = frozenset(
        {"median_tool_output_tokens", "median_tool_calls", "p75_duration_ms"}
    )
    _require_fields(value, fields, path)
    result: dict[str, float | None] = {}
    for field in fields:
        raw = value[field]
        result[field] = (
            None if raw is None else _require_number(raw, f"{path}.{field}")
        )
        if result[field] is not None and result[field] < 0:
            raise ValueError(f"{path}.{field} cannot be negative")
    return result


def _validate_efficiency(
    value: Any,
    completion: dict[str, int],
    baseline: dict[str, float | None],
    candidate: dict[str, float | None],
) -> dict[str, Any]:
    fields = frozenset(
        {
            "verdict",
            "measurable",
            "both_correct_task_count",
            "token_route_passed",
            "call_route_passed",
            "wall_guard_passed",
        }
    )
    _require_fields(value, fields, "efficiency")
    if value["verdict"] not in {"pass", "fail"}:
        raise ValueError("efficiency.verdict is invalid")
    both_correct = _require_nonnegative_int(
        value["both_correct_task_count"], "efficiency.both_correct_task_count"
    )
    if both_correct != completion["both_correct"]:
        raise ValueError("efficiency population does not match completion")
    measurable = both_correct > 0
    if not isinstance(value["measurable"], bool):
        raise ValueError("efficiency.measurable must be boolean")
    if value["measurable"] != measurable:
        raise ValueError("efficiency.measurable is inconsistent")
    if not measurable:
        if any(metric is not None for metric in (*baseline.values(), *candidate.values())):
            raise ValueError("unmeasurable efficiency metrics must be null")
        token_route = call_route = wall_guard = False
    else:
        if any(metric is None for metric in (*baseline.values(), *candidate.values())):
            raise ValueError("measurable efficiency metrics cannot be null")
        baseline_tokens = baseline["median_tool_output_tokens"]
        candidate_tokens = candidate["median_tool_output_tokens"]
        baseline_calls = baseline["median_tool_calls"]
        candidate_calls = candidate["median_tool_calls"]
        baseline_wall = baseline["p75_duration_ms"]
        candidate_wall = candidate["p75_duration_ms"]
        token_route = baseline_tokens > 0 and candidate_tokens <= 0.80 * baseline_tokens
        call_route = (
            candidate_calls <= baseline_calls - 1
            and candidate_tokens <= baseline_tokens
        )
        wall_guard = candidate_wall <= 1.20 * baseline_wall
    for field, expected in (
        ("token_route_passed", token_route),
        ("call_route_passed", call_route),
        ("wall_guard_passed", wall_guard),
    ):
        if not isinstance(value[field], bool):
            raise ValueError(f"efficiency.{field} must be boolean")
        if value[field] != expected:
            raise ValueError(f"efficiency.{field} is inconsistent")
    passed = measurable and wall_guard and (token_route or call_route)
    return {
        "verdict": "pass" if passed else "fail",
        "measurable": measurable,
        "both_correct_task_count": both_correct,
        "token_route_passed": token_route,
        "call_route_passed": call_route,
        "wall_guard_passed": wall_guard,
    }


def _validate_failure_counts(value: Any) -> dict[str, dict[str, int]]:
    _require_fields(value, frozenset({"baseline", "candidate"}), "failure_counts")
    result: dict[str, dict[str, int]] = {}
    for role in ("baseline", "candidate"):
        counts = value[role]
        if not isinstance(counts, dict):
            raise ValueError(f"failure_counts.{role} must be an object")
        unsupported = set(counts) - _FAILURE_REASONS
        if unsupported:
            raise ValueError(
                f"failure_counts.{role} has unsupported field: {sorted(unsupported)[0]}"
            )
        result[role] = {
            reason: _require_nonnegative_int(
                count, f"failure_counts.{role}.{reason}"
            )
            for reason, count in sorted(counts.items())
        }
    return result


def _validate_subgroups(
    value: Any, path: str, allowed_names: frozenset[str]
) -> dict[str, dict[str, Any]]:
    if not isinstance(value, dict):
        raise ValueError(f"{path} must be an object")
    unsupported = set(value) - allowed_names
    if unsupported:
        raise ValueError(f"{path} has unsupported subgroup: {sorted(unsupported)[0]}")
    return {
        name: _validate_subgroup(group, f"{path}.{name}")
        for name, group in sorted(value.items())
    }


def _validate_dynamic_subgroups(
    value: Any, path: str, decision_output: bool
) -> dict[str, dict[str, Any]]:
    if not isinstance(value, dict):
        raise ValueError(f"{path} must be an object")
    for name in value:
        if (
            not isinstance(name, str)
            or re.fullmatch(r"[A-Za-z][A-Za-z0-9_.+-]*", name) is None
        ):
            raise ValueError(f"{path} contains an invalid public subgroup")
    validated = {
        name: _validate_subgroup(group, f"{path}.{name}")
        for name, group in sorted(value.items())
    }
    return {} if decision_output else validated


def _validate_subgroup(value: Any, path: str) -> dict[str, Any]:
    fields = frozenset(
        {
            "task_count",
            "completion",
            "outcome_counts",
            "baseline_wrong_action_task_count",
            "candidate_wrong_action_task_count",
        }
    )
    _require_fields(value, fields, path)
    task_count = _require_positive_int(value["task_count"], f"{path}.task_count")
    if task_count < 5:
        raise ValueError(f"{path} violates the five-task floor")
    completion = _validate_completion(value["completion"], task_count, f"{path}.completion")
    outcome_counts = _validate_outcome_counts(
        value["outcome_counts"], task_count, f"{path}.outcome_counts"
    )
    baseline_wrong = _require_nonnegative_int(
        value["baseline_wrong_action_task_count"],
        f"{path}.baseline_wrong_action_task_count",
    )
    candidate_wrong = _require_nonnegative_int(
        value["candidate_wrong_action_task_count"],
        f"{path}.candidate_wrong_action_task_count",
    )
    if baseline_wrong > task_count or candidate_wrong > task_count:
        raise ValueError(f"{path} wrong-action count exceeds task_count")
    return {
        "task_count": task_count,
        "completion": completion,
        "outcome_counts": outcome_counts,
        "baseline_wrong_action_task_count": baseline_wrong,
        "candidate_wrong_action_task_count": candidate_wrong,
    }


def _require_fields(value: Any, fields: frozenset[str], path: str) -> None:
    if not isinstance(value, dict):
        raise ValueError(f"{path} must be an object")
    extra = set(value) - fields
    if extra:
        raise ValueError(f"{path} has unsupported field: {sorted(extra)[0]}")
    missing = fields - set(value)
    if missing:
        raise ValueError(f"{path} is missing field: {sorted(missing)[0]}")


def _require_nonnegative_int(value: Any, path: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool):
        raise ValueError(f"{path} must be an integer")
    if value < 0:
        raise ValueError(f"{path} cannot be negative")
    return value


def _require_positive_int(value: Any, path: str) -> int:
    result = _require_nonnegative_int(value, path)
    if result == 0:
        raise ValueError(f"{path} must be positive")
    return result


def _require_number(value: Any, path: str) -> float:
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        raise ValueError(f"{path} must be numeric")
    result = float(value)
    if not math.isfinite(result):
        raise ValueError(f"{path} must be finite")
    return result


def _as_int(value: Any) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def _task_name(row: dict[str, Any]) -> str:
    return str(row.get("task") or row.get("task_class") or "")


def _passed(row: dict[str, Any]) -> bool:
    return bool(row.get("scoring_pass", row.get("expected_present")))


def _cell(value: Any) -> str:
    return str(value).replace("|", "\\|").replace("\n", " ")


def _pct(numerator: Any, denominator: Any) -> str:
    total = _as_int(denominator)
    if total == 0:
        return "0.0%"
    return f"{(_as_int(numerator) / total) * 100:.1f}%"


def _anchor_summary(rows: list[dict[str, Any]]) -> str:
    total = sum(_as_int(row.get("expected_anchor_count")) for row in rows)
    if total == 0:
        return ""
    present = sum(_as_int(row.get("expected_anchors_present")) for row in rows)
    return f"{present}/{total}"


def _readiness_summary(rows: list[dict[str, Any]]) -> str:
    values = sorted({str(row.get("readiness")) for row in rows if row.get("readiness")})
    return ", ".join(values)


def summarize_by_tool(rows: list[dict[str, Any]]) -> str:
    lines: list[str] = []
    lines.append("| tool | tasks | pass | top | present | empty | median ms | p95 ms | median chars |")
    lines.append("|---|---:|---:|---:|---:|---:|---:|---:|---:|")
    for tool in sorted({row["tool"] for row in rows}):
        bucket = [row for row in rows if row["tool"] == tool]
        ms = [_as_int(row["ms"]) for row in bucket]
        chars = [_as_int(row["output_chars"]) for row in bucket]
        p95_index = max(0, math.ceil(len(ms) * 0.95) - 1) if ms else 0
        p95 = sorted(ms)[p95_index] if ms else 0
        lines.append(
            f"| {tool} | {len(bucket)} | "
            f"{sum(1 for row in bucket if _passed(row))} | "
            f"{sum(1 for row in bucket if row.get('expected_top'))} | "
            f"{sum(1 for row in bucket if row.get('expected_present'))} | "
            f"{sum(1 for row in bucket if row.get('empty'))} | "
            f"{int(statistics.median(ms)) if ms else 0} | {p95} | "
            f"{int(statistics.median(chars)) if chars else 0} |"
        )
    return "\n".join(lines)


def summarize_by_task(rows: list[dict[str, Any]]) -> str:
    lines: list[str] = []
    lines.append("| task | tool | tasks | top | present | empty | median ms | median chars |")
    lines.append("|---|---|---:|---:|---:|---:|---:|---:|")
    keys = sorted({(_task_name(row), row["tool"]) for row in rows})
    for task, tool in keys:
        bucket = [row for row in rows if _task_name(row) == task and row["tool"] == tool]
        ms = [_as_int(row["ms"]) for row in bucket]
        chars = [_as_int(row["output_chars"]) for row in bucket]
        lines.append(
            f"| {task} | {tool} | {len(bucket)} | "
            f"{sum(1 for row in bucket if row.get('expected_top'))} | "
            f"{sum(1 for row in bucket if row.get('expected_present'))} | "
            f"{sum(1 for row in bucket if row.get('empty'))} | "
            f"{int(statistics.median(ms)) if ms else 0} | "
            f"{int(statistics.median(chars)) if chars else 0} |"
        )
    return "\n".join(lines)


def summarize_foundation_matrix(rows: list[dict[str, Any]]) -> str:
    lines: list[str] = []
    lines.append(
        "| task class | tool | route | rows | hard | pass | present | top | anchors | readiness | empty | adaptations | median ms |"
    )
    lines.append("|---|---|---|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|")
    keys = sorted({(str(row["task_class"]), str(row["tool"]), str(row["route"])) for row in rows})
    for task_class, tool, route in keys:
        bucket = [
            row
            for row in rows
            if row["task_class"] == task_class and row["tool"] == tool and row["route"] == route
        ]
        ms = [_as_int(row["ms"]) for row in bucket]
        lines.append(
            f"| {task_class} | {tool} | {route} | {len(bucket)} | "
            f"{sum(1 for row in bucket if row.get('hard_gate'))} | "
            f"{sum(1 for row in bucket if _passed(row))} | "
            f"{sum(1 for row in bucket if row.get('expected_present'))} | "
            f"{sum(1 for row in bucket if row.get('expected_top'))} | "
            f"{_anchor_summary(bucket)} | "
            f"{_readiness_summary(bucket)} | "
            f"{sum(1 for row in bucket if row.get('empty'))} | "
            f"{sum(1 for row in bucket if row.get('adaptation_candidate'))} | "
            f"{int(statistics.median(ms)) if ms else 0} |"
        )
    return "\n".join(lines)


def summarize_adoption_analysis(analysis: dict[str, Any]) -> str:
    parseability = analysis.get("parseability", {})
    telemetry = analysis.get("telemetry", {})
    onboarding = analysis.get("onboarding", {})
    telemetry_parse = parseability.get("telemetry", {})
    onboarding_parse = parseability.get("onboarding", {})

    lines: list[str] = [
        "# Miller Foundation Adoption Analysis",
        "",
        "## Parseability Gate",
        "",
        f"Status: {analysis.get('gate', {}).get('status', 'UNKNOWN')}",
        f"Telemetry JSONL: parsed={telemetry_parse.get('parsed')} no_telemetry={telemetry_parse.get('no_telemetry')} non_empty_lines={telemetry_parse.get('non_empty_lines', 0)} sampled_rows={telemetry_parse.get('sampled_rows', 0)}",
        f"Onboarding JSON: parsed={onboarding_parse.get('parsed')} required_fields={onboarding_parse.get('required_fields_present', 0)}/{onboarding_parse.get('required_fields_total', 0)}",
        "",
        "Parseability is the hard gate. Usage and friction interpretation below is report-only.",
        "",
        "## Telemetry Window",
        "",
        f"Window: {telemetry.get('window_start_ts', '') or 'n/a'} to {telemetry.get('window_end_ts', '') or 'n/a'}",
        f"Exported calls: {telemetry.get('exported_total_calls', 0)}",
        f"Miller workspace calls: {telemetry.get('workspace_total_calls', 0)}",
        "",
        "## Core Tool/Op Mix",
        "",
        "| tool | op | calls | result count | avg ms | p95 ms |",
        "|---|---|---:|---:|---:|---:|",
    ]

    for row in analysis.get("core_tool_op_mix", []):
        lines.append(
            f"| {_cell(row.get('tool', ''))} | {_cell(row.get('op') or 'default')} | "
            f"{_as_int(row.get('calls'))} | {_as_int(row.get('result_count'))} | "
            f"{_as_int(row.get('avg_ms'))} | {_as_int(row.get('p95_ms'))} |"
        )

    lines.extend(
        [
            "",
            "## Empty And Error Rates",
            "",
            "| tool | op | calls | empty | empty rate | errors | error rate |",
            "|---|---|---:|---:|---:|---:|---:|",
        ]
    )
    for row in analysis.get("core_empty_error_rates", []):
        calls = row.get("calls", 0)
        lines.append(
            f"| {_cell(row.get('tool', ''))} | {_cell(row.get('op') or 'default')} | "
            f"{_as_int(calls)} | {_as_int(row.get('empty_count'))} | {_pct(row.get('empty_count'), calls)} | "
            f"{_as_int(row.get('error_count'))} | {_pct(row.get('error_count'), calls)} |"
        )

    lines.extend(["", "## Onboarding Starter Commands", ""])
    start_here = onboarding.get("start_here") or []
    if start_here:
        lines.extend(f"- {_cell(command)}" for command in start_here)
    else:
        lines.append("- No starter commands reported.")

    lines.extend(
        [
            "",
            "## Common Misses And Friction",
            "",
            "| source | tool | op | calls | reason | empty | errors | p95 ms |",
            "|---|---|---|---:|---|---:|---:|---:|",
        ]
    )
    for row in onboarding.get("common_misses", []):
        lines.append(
            f"| common_misses | {_cell(row.get('tool', ''))} | {_cell(row.get('op') or 'default')} | "
            f"{_as_int(row.get('calls'))} | {_cell(row.get('reason', ''))} |  |  |  |"
        )
    for row in onboarding.get("friction", []):
        lines.append(
            f"| friction | {_cell(row.get('tool', ''))} | {_cell(row.get('op') or 'default')} | "
            f"{_as_int(row.get('calls'))} |  | {_as_int(row.get('empty_count'))} | "
            f"{_as_int(row.get('error_count'))} | {_as_int(row.get('p95_ms'))} |"
        )

    lines.extend(
        [
            "",
            "## Low-Use Deterministic Tools",
            "",
            "Low-use is report-only. It identifies where current agents rarely exercise existing deterministic tools; it does not recommend new MCP tools.",
            "",
            "| tool | calls | share | note |",
            "|---|---:|---:|---|",
        ]
    )
    for row in analysis.get("low_use_tools", []):
        lines.append(
            f"| {_cell(row.get('tool', ''))} | {_as_int(row.get('calls'))} | "
            f"{float(row.get('share', 0.0)):.1%} | {_cell(row.get('note', ''))} |"
        )

    lines.extend(
        [
            "",
            "## Julie-Style Workflow Candidates",
            "",
            "| row | repo | intent | Miller outcome | Julie outcome | note |",
            "|---|---|---|---|---|---|",
        ]
    )
    candidates = analysis.get("workflow_candidates", [])
    if candidates:
        for row in candidates:
            lines.append(
                f"| {_cell(row.get('row_id', ''))} | {_cell(row.get('repo', ''))} | "
                f"{_cell(row.get('intent', ''))} | {_cell(row.get('miller_outcome', ''))} | "
                f"{_cell(row.get('julie_outcome', ''))} | {_cell(row.get('note', ''))} |"
            )
    else:
        note = analysis.get("workflow_candidate_note") or "No prior workflow candidate rows were available."
        lines.append(f"| n/a | n/a | n/a | n/a | n/a | {_cell(note)} |")

    lines.extend(
        [
            "",
            "## Usage/Adoption Interpretation",
            "",
            "- Tool exists and is parseable: proven by the parseability gate above.",
            "- Agents actually use it: estimated only from the available local telemetry window.",
            "- Workflow still causes friction: inferred only from empty/error rates, onboarding misses, and prior workflow candidates.",
            "",
            "## Do Not Infer",
            "",
            "- Do not rank product quality by raw usage volume alone.",
            "- Do not treat low usage as proof that a tool is unnecessary.",
            "- Do not propose MCP surface expansion by default; prefer improving existing tools, CLI/export contracts, skills, or dashboard presentation.",
        ]
    )
    return "\n".join(lines)
