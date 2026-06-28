"""Markdown report helpers for benchmark result rows."""

from __future__ import annotations

import math
import statistics
from typing import Any


def _as_int(value: Any) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def _task_name(row: dict[str, Any]) -> str:
    return str(row.get("task") or row.get("task_class") or "")


def _passed(row: dict[str, Any]) -> bool:
    return bool(row.get("scoring_pass", row.get("expected_present")))


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
    lines.append("| task class | tool | route | rows | hard | pass | present | top | empty | adaptations | median ms |")
    lines.append("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|")
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
            f"{sum(1 for row in bucket if row.get('empty'))} | "
            f"{sum(1 for row in bucket if row.get('adaptation_candidate'))} | "
            f"{int(statistics.median(ms)) if ms else 0} |"
        )
    return "\n".join(lines)
