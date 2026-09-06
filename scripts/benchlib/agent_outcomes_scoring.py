from __future__ import annotations

import math
import random
from collections import defaultdict
from collections.abc import Mapping, Sequence
from typing import Any


def model_token_total(row: Mapping[str, Any]) -> int | None:
    input_tokens = row.get("total_model_input_tokens")
    cached_tokens = row.get("total_model_cached_tokens")
    output_tokens = row.get("total_model_output_tokens")
    values = (input_tokens, cached_tokens, output_tokens)
    if all(value is None for value in values):
        return None
    if not all(
        isinstance(value, int) and not isinstance(value, bool) and value >= 0
        for value in values
    ):
        raise ValueError(
            "model usage must contain all measured token counts or all null"
        )
    if cached_tokens > input_tokens:
        raise ValueError("cached input tokens cannot exceed input tokens")
    return input_tokens + output_tokens


def price_model_usage(
    row: Mapping[str, Any], pricing: Mapping[str, Any]
) -> float | None:
    total = model_token_total(row)
    if total is None:
        return None
    input_tokens = row["total_model_input_tokens"]
    cached_tokens = row["total_model_cached_tokens"]
    output_tokens = row["total_model_output_tokens"]
    return (
        (input_tokens - cached_tokens) * pricing["input_per_million"]
        + cached_tokens * pricing["cached_input_per_million"]
        + output_tokens * pricing["output_per_million"]
    ) / 1_000_000


def cost_per_success(rows: Sequence[Mapping[str, Any]]) -> float | None:
    successes = sum(row.get("correct") is True for row in rows)
    costs = [row.get("cost") for row in rows]
    if successes == 0 or any(not _measured_number(cost) for cost in costs):
        return None
    return sum(costs) / successes


def summarize_attempts(
    rows: Sequence[Mapping[str, Any]],
    setup_components: Sequence[Mapping[str, Any]] = (),
) -> Mapping[str, Any]:
    attempts = list(rows)
    scored = [row for row in attempts if row.get("outcome") != "infrastructure_void"]
    denominator = len(scored)
    success_count = sum(row.get("outcome") == "correct" for row in scored)
    wrong_count = sum(row.get("outcome") == "incorrect" for row in scored)
    error_count = sum(
        row.get("outcome") in {"timeout", "product_error"} for row in scored
    )
    unsupported_count = sum(row.get("outcome") == "unsupported" for row in scored)
    costs = [row.get("cost") for row in attempts]
    setup = _unique_setup_components(setup_components)
    setup_costs = [component.get("cost") for component in setup]
    measured_costs = [value for value in costs + setup_costs if _measured_number(value)]
    all_costs_measured = len(measured_costs) == len(costs) + len(setup_costs)
    tokens = [row.get("tokens") for row in attempts]
    measured_tokens = [value for value in tokens if _measured_number(value)]
    wall_times = [row.get("wall_time_seconds") for row in attempts]
    measured_walls = [value for value in wall_times if _measured_number(value)]
    tool_counts: dict[str, int] = defaultdict(int)
    for row in attempts:
        for name, count in row.get("native_tool_counts", {}).items():
            tool_counts[name] += count
    void_costs = [
        row.get("cost")
        for row in attempts
        if row.get("outcome") == "infrastructure_void"
    ]
    measured_void_costs = [value for value in void_costs if _measured_number(value)]
    total_cost = sum(measured_costs) if all_costs_measured else None
    return {
        "attempt_count": len(attempts),
        "scored_attempt_count": denominator,
        "success_count": success_count,
        "success_rate": _rate(success_count, denominator),
        "wrong_action_rate": _rate(wrong_count, denominator),
        "timeout_product_error_rate": _rate(error_count, denominator),
        "unsupported_rate": _rate(unsupported_count, denominator),
        "outcome_counts": {
            outcome: sum(row.get("outcome") == outcome for row in attempts)
            for outcome in (
                "correct",
                "incorrect",
                "timeout",
                "product_error",
                "infrastructure_void",
                "unsupported",
            )
        },
        "infrastructure_void_count": len(attempts) - denominator,
        "infrastructure_void_spend": sum(measured_void_costs)
        if len(measured_void_costs) == len(void_costs)
        else None,
        "measured_infrastructure_void_spend": sum(measured_void_costs),
        "total_cost": total_cost,
        "measured_cost_subtotal": sum(measured_costs),
        "cost_coverage": {
            "measured": len(measured_costs),
            "total": len(costs) + len(setup_costs),
        },
        "cost_per_verified_success": None
        if success_count == 0 or total_cost is None
        else total_cost / success_count,
        "total_model_tokens": sum(measured_tokens)
        if len(measured_tokens) == len(tokens)
        else None,
        "measured_model_tokens_subtotal": sum(measured_tokens),
        "token_coverage": {"measured": len(measured_tokens), "total": len(tokens)},
        "total_wall_time_seconds": sum(measured_walls)
        if len(measured_walls) == len(wall_times)
        else None,
        "wall_time_coverage": {
            "measured": len(measured_walls),
            "total": len(wall_times),
        },
        "native_tool_counts": dict(sorted(tool_counts.items())),
        "miller_calls": sum(row.get("miller_calls", 0) for row in attempts),
        "setup": amortized_setup_costs(setup),
    }


def amortized_setup_costs(
    components: Sequence[Mapping[str, Any]],
    task_counts: Sequence[int] = (1, 10, 100),
) -> Mapping[str, Any]:
    unique = _unique_setup_components(components)
    costs = [component.get("cost") for component in unique]
    walls = [component.get("wall_time_seconds") for component in unique]
    cost_known = all(_measured_number(value) for value in costs)
    wall_known = all(_measured_number(value) for value in walls)
    total_cost = sum(costs) if cost_known else None
    return {
        "component_count": len(unique),
        "cold_setup_cost": total_cost,
        "cold_setup_wall_time_seconds": sum(walls) if wall_known else None,
        "cost_coverage": {
            "measured": sum(_measured_number(value) for value in costs),
            "total": len(costs),
        },
        "wall_time_coverage": {
            "measured": sum(_measured_number(value) for value in walls),
            "total": len(walls),
        },
        "cost_per_task": {
            str(count): None if total_cost is None else total_cost / count
            for count in task_counts
        },
    }


def paired_cluster_confidence_interval(
    rows: Sequence[Mapping[str, Any]],
    *,
    samples: int = 2_000,
    seed: int = 0,
) -> Mapping[str, Any]:
    if samples < 1:
        raise ValueError("bootstrap samples must be positive")
    task_values: dict[str, dict[str, list[float]]] = defaultdict(
        lambda: defaultdict(list)
    )
    for row in rows:
        repository = row.get("repo_id")
        task = row.get("task_id")
        delta = row.get("paired_delta")
        if (
            not isinstance(repository, str)
            or not repository
            or not isinstance(task, str)
            or not task
        ):
            raise ValueError("paired rows require repository and task identities")
        if not _finite_number(delta):
            raise ValueError("paired rows require a finite paired_delta")
        task_values[repository][task].append(float(delta))
    if not task_values:
        raise ValueError("paired rows must not be empty")
    repositories = sorted(task_values)
    rng = random.Random(seed)
    estimates = []
    for _ in range(samples):
        sampled_task_means = []
        for repository in rng.choices(repositories, k=len(repositories)):
            tasks = sorted(task_values[repository])
            for task in rng.choices(tasks, k=len(tasks)):
                repetitions = task_values[repository][task]
                sampled_task_means.append(sum(repetitions) / len(repetitions))
        estimates.append(sum(sampled_task_means) / len(sampled_task_means))
    estimates.sort()
    lower = _percentile(estimates, 0.025)
    upper = _percentile(estimates, 0.975)
    observed_tasks = [
        values for tasks in task_values.values() for values in tasks.values()
    ]
    estimate = sum(sum(values) / len(values) for values in observed_tasks) / len(
        observed_tasks
    )
    return {
        "estimate": estimate,
        "confidence_level": 0.95,
        "lower": lower,
        "upper": upper,
        "bootstrap_samples": samples,
        "repository_cluster_count": len(task_values),
        "task_cluster_count": len(observed_tasks),
        "observation_count": sum(len(values) for values in observed_tasks),
        "conclusion": "difference" if lower > 0 or upper < 0 else "inconclusive",
    }


def _unique_setup_components(
    components: Sequence[Mapping[str, Any]],
) -> list[Mapping[str, Any]]:
    unique: dict[tuple[str, str], Mapping[str, Any]] = {}
    for component in components:
        environment = component.get("environment_id")
        component_id = component.get("component_id")
        if (
            not isinstance(environment, str)
            or not environment
            or not isinstance(component_id, str)
            or not component_id
        ):
            raise ValueError(
                "setup components require stable environment_id and component_id"
            )
        key = (environment, component_id)
        prior = unique.get(key)
        if prior is not None and dict(prior) != dict(component):
            raise ValueError(
                "duplicate setup component identity has conflicting measurements"
            )
        unique[key] = component
    return [unique[key] for key in sorted(unique)]


def _rate(numerator: int, denominator: int) -> float | None:
    return None if denominator == 0 else numerator / denominator


def _measured_number(value: Any) -> bool:
    return _finite_number(value) and value >= 0


def _finite_number(value: Any) -> bool:
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(value)
    )


def _percentile(values: Sequence[float], probability: float) -> float:
    index = round((len(values) - 1) * probability)
    return values[index]
