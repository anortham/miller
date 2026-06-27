"""Markdown report helpers for benchmark result rows."""

from __future__ import annotations

import math
import statistics
from typing import Any


def summarize_by_tool(rows: list[dict[str, Any]]) -> str:
    lines: list[str] = []
    lines.append("| tool | tasks | top | present | empty | median ms | p95 ms | median chars |")
    lines.append("|---|---:|---:|---:|---:|---:|---:|---:|")
    for tool in sorted({row["tool"] for row in rows}):
        bucket = [row for row in rows if row["tool"] == tool]
        ms = [int(row["ms"]) for row in bucket]
        chars = [int(row["output_chars"]) for row in bucket]
        p95_index = max(0, math.ceil(len(ms) * 0.95) - 1) if ms else 0
        p95 = sorted(ms)[p95_index] if ms else 0
        lines.append(
            f"| {tool} | {len(bucket)} | "
            f"{sum(1 for row in bucket if row['expected_top'])} | "
            f"{sum(1 for row in bucket if row['expected_present'])} | "
            f"{sum(1 for row in bucket if row['empty'])} | "
            f"{int(statistics.median(ms)) if ms else 0} | {p95} | "
            f"{int(statistics.median(chars)) if chars else 0} |"
        )
    return "\n".join(lines)


def summarize_by_task(rows: list[dict[str, Any]]) -> str:
    lines: list[str] = []
    lines.append("| task | tool | tasks | top | present | empty | median ms | median chars |")
    lines.append("|---|---|---:|---:|---:|---:|---:|---:|")
    keys = sorted({(row["task"], row["tool"]) for row in rows})
    for task, tool in keys:
        bucket = [row for row in rows if row["task"] == task and row["tool"] == tool]
        ms = [int(row["ms"]) for row in bucket]
        chars = [int(row["output_chars"]) for row in bucket]
        lines.append(
            f"| {task} | {tool} | {len(bucket)} | "
            f"{sum(1 for row in bucket if row['expected_top'])} | "
            f"{sum(1 for row in bucket if row['expected_present'])} | "
            f"{sum(1 for row in bucket if row['empty'])} | "
            f"{int(statistics.median(ms)) if ms else 0} | "
            f"{int(statistics.median(chars)) if chars else 0} |"
        )
    return "\n".join(lines)
