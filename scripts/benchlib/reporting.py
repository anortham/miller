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
