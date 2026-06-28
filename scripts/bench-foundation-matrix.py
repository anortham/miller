#!/usr/bin/env python3
"""Run manifest-driven Miller foundation benchmark rows."""

from __future__ import annotations

import argparse
import csv
import json
import math
import subprocess
import sys
import time
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from benchlib import McpProcess, content_text, summarize_by_tool
from benchlib.reporting import summarize_adoption_analysis, summarize_foundation_matrix
from benchlib.scoring import count_present_fields, parse_jsonl_objects, score_manifest_path


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_MANIFEST = ROOT / "scripts/benchmarks/miller-foundation-cases.json"
MILLER = ROOT / "src/Miller.Server/bin/Release/net10.0/miller"
JULIE = Path("/Users/murphy/source/julie/target/release/julie-server")

REPO_ROOTS = {
    "miller": "/Users/murphy/source/miller",
    "julie": "/Users/murphy/source/julie",
    "eros": "/Users/murphy/source/eros",
    "express": "/Users/murphy/source/express",
    "flask": "/Users/murphy/source/flask",
    "gson": "/Users/murphy/source/gson",
    "newtonsoft": "/Users/murphy/source/Newtonsoft.Json",
    "zod": "/Users/murphy/source/zod",
    "jq": "/Users/murphy/source/jq",
}

SUPPORTED_ROUTES = {"mcp", "cli"}
SUPPORTED_CLI_FORMATS = {"json", "jsonl"}
SUPPORTED_MILLER_TOOLS = {"search", "inspect", "context", "trace", "impact"}
SUPPORTED_JULIE_TOOLS = {"fast_search", "deep_dive", "get_context", "fast_refs", "call_path", "blast_radius"}
SUPPORTED_SCORING_MODES = {
    "path_present",
    "path_top",
    "path_any_present",
    "workflow_anchors",
    "trace_refs",
    "trace_path",
    "trace_bridge",
    "impact_targets",
    "contract_json",
    "contract_jsonl",
}
WORKFLOW_SCORING_MODES = {"workflow_anchors", "trace_refs", "trace_path", "trace_bridge", "impact_targets"}
SUPPORTED_READINESS = {"edit-ready", "inspect-ready", "needs-search", "unsupported", "no-path"}
SUPPORTED_WORKFLOW_OUTCOMES = {"ok", "needs-search", "unsupported", "no-path"}
BASE_REQUIRED_ROW_KEYS = {"id", "repo", "task_class", "intent", "expected", "scoring", "gate"}
CORE_ADOPTION_TOOLS = ("search", "inspect", "context", "trace", "impact")
TELEMETRY_SCHEMA_FIELDS = ["tool", "ts", "outcome", "result_count"]
ONBOARDING_REQUIRED_FIELDS = ["telemetry", "start_here", "tool_mix"]
TASK4_WORKFLOW_RESULTS = ROOT / "docs/findings/benchmarks/2026-06-27-foundation-matrix/task4-workflows/results.json"
ORIGINAL_NINE_REPOS = ("miller", "julie", "eros", "express", "flask", "gson", "newtonsoft", "zod", "jq")
AGGREGATE_GATE_SPECS: tuple[dict[str, Any], ...] = (
    {
        "id": "miller.exact_symbol.present.original_nine",
        "label": "Miller exact-symbol retrieval present",
        "tool": "miller.search",
        "task_class": "retrieval.symbol",
        "metric": "expected_present",
        "minimum": 9,
        "expected_rows": 9,
        "rationale": "protects shipped exact-symbol lookup across the original nine-repo baseline",
    },
    {
        "id": "miller.file.present.original_nine",
        "label": "Miller file retrieval present",
        "tool": "miller.search",
        "task_class": "retrieval.file",
        "metric": "expected_present",
        "minimum": 7,
        "expected_rows": 9,
        "rationale": "protects file lookup while leaving known route-rank improvement work report-only",
    },
    {
        "id": "miller.source_auto.present.original_nine",
        "label": "Miller source-auto retrieval present",
        "tool": "miller.search",
        "task_class": "retrieval.source_auto",
        "metric": "expected_present",
        "minimum": 8,
        "expected_rows": 9,
        "rationale": "protects automatic source rescue without freezing top-rank tuning",
    },
    {
        "id": "miller.inspect_overview.present.original_nine",
        "label": "Miller inspect overview present",
        "tool": "miller.inspect",
        "task_class": "inspect.overview",
        "metric": "expected_present",
        "minimum": 9,
        "expected_rows": 9,
        "rationale": "protects compact inspect orientation across the original nine-repo baseline",
    },
)
AGGREGATE_GATE_KEYS = {
    (str(spec["tool"]), str(spec["task_class"]))
    for spec in AGGREGATE_GATE_SPECS
}

WORKFLOW_CSV_FIELDS = [
    "expected_anchor_count",
    "expected_anchors_present",
    "first_useful_anchor",
    "follow_up_hint_present",
    "readiness",
    "workflow_outcome",
    "definition_present",
    "reference_count",
    "noise_diagnostic_count",
    "impacted_symbols_present",
    "likely_tests_present",
    "impacted_symbol_count",
    "likely_test_count",
]

CONTRACT_CSV_FIELDS = [
    "cli_command",
    "cli_exit_code",
    "contract_parse_ok",
    "required_fields_present",
    "required_fields_total",
    "required_row_fields_present",
    "required_row_fields_total",
    "advertised_commands_present",
    "advertised_commands_total",
    "sampled_jsonl_rows",
    "jsonl_non_empty_lines",
    "contract_outcome",
]

CSV_FIELDS = [
    "row_id",
    "repo",
    "task_class",
    "tool",
    "route",
    "hard_gate",
    "scoring_mode",
    "scoring_pass",
    "expected_present",
    "expected_top",
    "empty",
    "ms",
    "output_chars",
    "first_path",
    "adaptation_candidate",
    "expected_path",
    "anchor_present",
    "result_count",
] + WORKFLOW_CSV_FIELDS + CONTRACT_CSV_FIELDS


def split_filter(value: str) -> set[str]:
    if value.strip().lower() == "all":
        return set()
    return {item.strip() for item in value.split(",") if item.strip()}


def row_label(row: Any, index: int) -> str:
    if isinstance(row, dict) and row.get("id"):
        return f"row {index} ({row['id']})"
    return f"row {index}"


def row_route(row: dict[str, Any]) -> str:
    route = row.get("route", "mcp")
    return route if isinstance(route, str) else ""


def load_manifest(path: Path) -> tuple[list[dict[str, Any]], list[str]]:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return [], [f"manifest not found: {path}"]
    except json.JSONDecodeError as exc:
        return [], [f"manifest JSON parse failed at line {exc.lineno}, column {exc.colno}: {exc.msg}"]

    if isinstance(document, list):
        rows = document
    elif isinstance(document, dict) and isinstance(document.get("rows"), list):
        rows = document["rows"]
    elif isinstance(document, dict):
        return [], ["manifest must contain a top-level 'rows' list"]
    else:
        return [], ["manifest must be a JSON object with rows or a JSON list of rows"]

    errors = validate_rows(rows)
    return [row for row in rows if isinstance(row, dict)], errors


def validate_rows(rows: list[Any]) -> list[str]:
    errors: list[str] = []
    seen_ids: set[str] = set()

    for index, row in enumerate(rows):
        label = row_label(row, index)
        if not isinstance(row, dict):
            errors.append(f"{label}: expected object row")
            continue

        route = row_route(row)
        required = set(BASE_REQUIRED_ROW_KEYS)
        if route == "cli":
            required.add("cli")
        else:
            required.update({"miller", "julie"})

        missing = sorted(required - set(row))
        for key in missing:
            errors.append(f"{label}: missing required key '{key}'")

        for key in ["id", "repo", "task_class", "intent"]:
            if key in row and not isinstance(row[key], str):
                errors.append(f"{label}: '{key}' must be a string")
            elif key in row and not row[key].strip():
                errors.append(f"{label}: '{key}' must not be empty")

        row_id = row.get("id")
        if isinstance(row_id, str):
            if row_id in seen_ids:
                errors.append(f"{label}: duplicate id '{row_id}'")
            seen_ids.add(row_id)

        repo = row.get("repo")
        if isinstance(repo, str) and repo not in REPO_ROOTS:
            errors.append(f"{label}: unknown repo '{repo}'")

        if not isinstance(row.get("route", "mcp"), str) or route not in SUPPORTED_ROUTES:
            expected = "', '".join(sorted(SUPPORTED_ROUTES))
            errors.append(f"{label}: unsupported route {row.get('route')!r}; expected one of '{expected}'")
        elif route == "cli":
            errors.extend(validate_cli_spec(row, label))
        else:
            errors.extend(validate_tool_spec(row, label, "miller", SUPPORTED_MILLER_TOOLS))
            errors.extend(validate_tool_spec(row, label, "julie", SUPPORTED_JULIE_TOOLS))
        errors.extend(validate_expected(row, label))
        errors.extend(validate_scoring(row, label))
        errors.extend(validate_gate(row, label))

    return errors


def validate_cli_spec(row: dict[str, Any], label: str) -> list[str]:
    spec = row.get("cli")
    if not isinstance(spec, dict):
        return [f"{label}: 'cli' must be an object"]

    errors: list[str] = []
    args = spec.get("args")
    if not isinstance(args, list) or not args:
        errors.append(f"{label}: 'cli.args' must be a non-empty list")
    elif not all(isinstance(arg, str) and arg for arg in args):
        errors.append(f"{label}: 'cli.args' entries must be non-empty strings")

    fmt = spec.get("format")
    if fmt not in SUPPORTED_CLI_FORMATS:
        expected = "', '".join(sorted(SUPPORTED_CLI_FORMATS))
        errors.append(f"{label}: unsupported cli.format {fmt!r}; expected one of '{expected}'")

    if "allow_unsupported" in spec and not isinstance(spec["allow_unsupported"], bool):
        errors.append(f"{label}: 'cli.allow_unsupported' must be a boolean when present")

    exit_codes = spec.get("expected_exit_codes")
    if exit_codes is not None and (
        not isinstance(exit_codes, list) or not all(isinstance(code, int) for code in exit_codes)
    ):
        errors.append(f"{label}: 'cli.expected_exit_codes' must be a list of integers when present")

    return errors


def validate_tool_spec(
    row: dict[str, Any],
    label: str,
    key: str,
    supported_tools: set[str],
) -> list[str]:
    spec = row.get(key)
    if not isinstance(spec, dict):
        return [f"{label}: '{key}' must be an object"]

    errors: list[str] = []
    tool = spec.get("tool")
    if not isinstance(tool, str) or not tool.strip():
        errors.append(f"{label}: '{key}.tool' must be a non-empty string")
    elif tool not in supported_tools:
        errors.append(f"{label}: unsupported {key} tool '{tool}'")

    args = spec.get("args")
    if not isinstance(args, dict):
        errors.append(f"{label}: '{key}.args' must be an object")
    elif key == "miller":
        if tool == "search" and not isinstance(args.get("query"), str):
            errors.append(f"{label}: 'miller.args.query' is required for search")
        if tool == "inspect" and not isinstance(args.get("target"), str):
            errors.append(f"{label}: 'miller.args.target' is required for inspect")
        if tool == "context" and not isinstance(args.get("query"), str):
            errors.append(f"{label}: 'miller.args.query' is required for context")
        if tool in {"trace", "impact"} and not isinstance(args.get("target"), str):
            errors.append(f"{label}: 'miller.args.target' is required for {tool}")
    elif key == "julie":
        if tool == "fast_search" and not isinstance(args.get("query"), str):
            errors.append(f"{label}: 'julie.args.query' is required for fast_search")
        if tool == "deep_dive" and not isinstance(args.get("symbol"), str):
            errors.append(f"{label}: 'julie.args.symbol' is required for deep_dive")
        if tool == "get_context" and not (isinstance(args.get("query"), str) or isinstance(args.get("symbol"), str)):
            errors.append(f"{label}: 'julie.args.query' or 'julie.args.symbol' is required for get_context")
        if tool in {"fast_refs", "blast_radius"} and not (
            isinstance(args.get("target"), str) or isinstance(args.get("symbol"), str)
        ):
            errors.append(f"{label}: 'julie.args.target' or 'julie.args.symbol' is required for {tool}")
        if tool == "call_path" and not (
            (isinstance(args.get("target"), str) or isinstance(args.get("from"), str))
            and isinstance(args.get("to"), str)
        ):
            errors.append(f"{label}: 'julie.args.target/from' and 'julie.args.to' are required for call_path")

    if "report_only" in spec and not isinstance(spec["report_only"], bool):
        errors.append(f"{label}: '{key}.report_only' must be a boolean when present")
    return errors


def validate_expected(row: dict[str, Any], label: str) -> list[str]:
    expected = row.get("expected")
    if not isinstance(expected, dict):
        return [f"{label}: 'expected' must be an object"]
    errors: list[str] = []
    if not isinstance(expected.get("path"), str) or not expected.get("path", "").strip():
        errors.append(f"{label}: 'expected.path' must be a non-empty string")
    if not isinstance(expected.get("anchor"), str) or not expected.get("anchor", "").strip():
        errors.append(f"{label}: 'expected.anchor' must be a non-empty string")
    paths = expected.get("paths")
    if paths is not None:
        if not isinstance(paths, list) or not paths:
            errors.append(f"{label}: 'expected.paths' must be a non-empty list when present")
        elif not all(isinstance(path, str) and path.strip() for path in paths):
            errors.append(f"{label}: 'expected.paths' entries must be non-empty strings")
        elif isinstance(expected.get("path"), str) and expected["path"] not in paths:
            errors.append(f"{label}: 'expected.path' must be included in 'expected.paths'")
    scoring = row.get("scoring")
    if isinstance(scoring, dict) and scoring.get("mode") == "path_any_present" and not isinstance(paths, list):
        errors.append(f"{label}: 'expected.paths' is required for path_any_present scoring")
    return errors


def validate_scoring(row: dict[str, Any], label: str) -> list[str]:
    scoring = row.get("scoring")
    if not isinstance(scoring, dict):
        return [f"{label}: 'scoring' must be an object"]
    errors: list[str] = []
    if scoring.get("mode") not in SUPPORTED_SCORING_MODES:
        expected = "', '".join(sorted(SUPPORTED_SCORING_MODES))
        errors.append(f"{label}: unsupported scoring.mode {scoring.get('mode')!r}; expected one of '{expected}'")
    if "top_path" in scoring and not isinstance(scoring["top_path"], bool):
        errors.append(f"{label}: 'scoring.top_path' must be a boolean when present")
    mode = scoring.get("mode")
    if mode in {"contract_json", "contract_jsonl"}:
        expected_format = "jsonl" if mode == "contract_jsonl" else "json"
        if row_route(row) == "cli" and isinstance(row.get("cli"), dict) and row["cli"].get("format") != expected_format:
            errors.append(f"{label}: scoring.mode {mode!r} requires cli.format {expected_format!r}")
        for key in ["required_fields", "required_row_fields", "advertises_commands"]:
            values = scoring.get(key)
            if values is not None and (
                not isinstance(values, list) or not all(isinstance(value, str) and value.strip() for value in values)
            ):
                errors.append(f"{label}: 'scoring.{key}' must be a list of non-empty strings when present")
        if row.get("gate", {}).get("hard") is True and not scoring.get("advertises_commands"):
            errors.append(f"{label}: hard-gated contract rows must set 'scoring.advertises_commands'")
        if "rows_path" in scoring and not isinstance(scoring["rows_path"], str):
            errors.append(f"{label}: 'scoring.rows_path' must be a string when present")
        if "sample_limit" in scoring and (
            not isinstance(scoring["sample_limit"], int) or scoring["sample_limit"] <= 0
        ):
            errors.append(f"{label}: 'scoring.sample_limit' must be a positive integer when present")
        if "min_rows" in scoring and (not isinstance(scoring["min_rows"], int) or scoring["min_rows"] < 0):
            errors.append(f"{label}: 'scoring.min_rows' must be a non-negative integer when present")
        if "empty_allowed" in scoring and not isinstance(scoring["empty_allowed"], bool):
            errors.append(f"{label}: 'scoring.empty_allowed' must be a boolean when present")
        return errors
    if mode in {"workflow_anchors", "trace_refs", "trace_path", "trace_bridge", "impact_targets"}:
        readiness = scoring.get("readiness")
        if readiness not in SUPPORTED_READINESS:
            expected = "', '".join(sorted(SUPPORTED_READINESS))
            errors.append(f"{label}: unsupported scoring.readiness {readiness!r}; expected one of '{expected}'")
    if mode in {"workflow_anchors", "trace_refs", "trace_path", "trace_bridge"}:
        anchors = scoring.get("required_anchors")
        if anchors is not None and (
            not isinstance(anchors, list) or not all(isinstance(anchor, str) and anchor.strip() for anchor in anchors)
        ):
            errors.append(f"{label}: 'scoring.required_anchors' must be a list of non-empty strings when present")
    if mode == "workflow_anchors":
        anchors = scoring.get("required_anchors")
        if not isinstance(anchors, list) or not anchors:
            errors.append(f"{label}: 'scoring.required_anchors' is required for workflow_anchors")
        if "follow_up_hint" in scoring and not isinstance(scoring["follow_up_hint"], str):
            errors.append(f"{label}: 'scoring.follow_up_hint' must be a string when present")
    if mode == "trace_refs":
        if "definition_anchor" in scoring and not isinstance(scoring["definition_anchor"], str):
            errors.append(f"{label}: 'scoring.definition_anchor' must be a string when present")
        if "min_references" in scoring and (
            not isinstance(scoring["min_references"], int) or scoring["min_references"] < 0
        ):
            errors.append(f"{label}: 'scoring.min_references' must be a non-negative integer when present")
    if mode in {"trace_refs", "trace_path", "trace_bridge"} and "expected_outcome" in scoring:
        if scoring["expected_outcome"] not in SUPPORTED_WORKFLOW_OUTCOMES:
            expected = "', '".join(sorted(SUPPORTED_WORKFLOW_OUTCOMES))
            errors.append(f"{label}: unsupported scoring.expected_outcome {scoring['expected_outcome']!r}; expected one of '{expected}'")
    if mode == "impact_targets":
        for key in ["required_symbols", "required_tests"]:
            values = scoring.get(key)
            if not isinstance(values, list) or not all(isinstance(value, str) and value.strip() for value in values):
                errors.append(f"{label}: 'scoring.{key}' must be a list of non-empty strings")
    return errors


def validate_gate(row: dict[str, Any], label: str) -> list[str]:
    gate = row.get("gate")
    if not isinstance(gate, dict):
        return [f"{label}: 'gate' must be an object"]
    if not isinstance(gate.get("hard"), bool):
        return [f"{label}: 'gate.hard' must be a boolean"]
    return []


def select_rows(
    rows: list[dict[str, Any]],
    repo_filters: set[str],
    task_filters: set[str],
) -> tuple[list[dict[str, Any]], list[str]]:
    errors: list[str] = []
    available_repos = {row["repo"] for row in rows}
    available_tasks = {row["task_class"] for row in rows} | {row["id"] for row in rows}

    unknown_repos = sorted(repo_filters - available_repos)
    unknown_tasks = sorted(task_filters - available_tasks)
    if unknown_repos:
        errors.append(f"unknown --repos value(s): {', '.join(unknown_repos)}")
    if unknown_tasks:
        errors.append(f"unknown --tasks value(s): {', '.join(unknown_tasks)}")
    if errors:
        return [], errors

    selected = [
        row
        for row in rows
        if (not repo_filters or row["repo"] in repo_filters)
        and (not task_filters or row["task_class"] in task_filters or row["id"] in task_filters)
    ]
    if not selected:
        errors.append("filters selected 0 manifest rows")
    return selected, errors


def apply_miller_defaults(row: dict[str, Any]) -> dict[str, Any]:
    args = dict(row["miller"]["args"])
    args["workspace_id"] = REPO_ROOTS[row["repo"]]
    args.setdefault("ensure_fresh", False)
    if row["miller"]["tool"] == "search":
        args.setdefault("limit", 5)
    elif row["miller"]["tool"] == "inspect":
        args.setdefault("format", "compact")
    elif row["miller"]["tool"] == "context":
        args.setdefault("format", "compact")
        args.setdefault("token_budget", 1500)
    elif row["miller"]["tool"] == "trace":
        args.setdefault("format", "compact")
        args.setdefault("limit", 20)
    elif row["miller"]["tool"] == "impact":
        args.setdefault("format", "compact")
        args.setdefault("limit", 40)
    return args


def result_from_score(
    row: dict[str, Any],
    *,
    tool: str,
    route: str,
    ms: int,
    hard_gate: bool,
    scored: dict[str, Any],
    diagnostics: list[dict[str, Any]] | None = None,
) -> dict[str, Any]:
    row_diagnostics = list(scored.pop("diagnostics", []))
    if diagnostics:
        row_diagnostics.extend(diagnostics)
    diagnostic_types = {diagnostic.get("type") for diagnostic in row_diagnostics}
    expected_present = bool(scored.get("expected_present"))
    scoring_pass = bool(scored.get("scoring_pass"))
    empty = bool(scored.get("empty"))
    anchor_missing = scored.get("anchor_present") is False
    scoring_mode = str(scored.get("scoring_mode") or row["scoring"]["mode"])
    report_only_candidate = bool(tool.startswith("julie.") and row.get("julie", {}).get("report_only"))
    can_adapt = hard_gate or (report_only_candidate and "skipped_tool" not in diagnostic_types)
    result = {
        "row_id": row["id"],
        "repo": row["repo"],
        "task_class": row["task_class"],
        "intent": row["intent"],
        "tool": tool,
        "route": route,
        "hard_gate": hard_gate,
        "scoring_mode": scoring_mode,
        "scoring_pass": scoring_pass,
        "expected_present": expected_present,
        "expected_top": bool(scored.get("expected_top")),
        "empty": empty,
        "ms": int(ms),
        "output_chars": int(scored.get("output_chars") or 0),
        "first_path": str(scored.get("first_path") or ""),
        "adaptation_candidate": bool(can_adapt and (empty or not scoring_pass or anchor_missing)),
        "expected_path": row["expected"]["path"],
        "anchor_present": scored.get("anchor_present", ""),
        "result_count": scored.get("result_count", ""),
        "diagnostics": row_diagnostics,
    }
    for field in WORKFLOW_CSV_FIELDS:
        result[field] = scored.get(field, "")
    for field in CONTRACT_CSV_FIELDS:
        result[field] = scored.get(field, "")
    return result


def skipped_result(row: dict[str, Any], *, tool: str, reason: str) -> dict[str, Any]:
    return result_from_score(
        row,
        tool=tool,
        route="skipped",
        ms=0,
        hard_gate=False,
        scored={
            "empty": True,
            "expected_present": False,
            "expected_top": False,
            "scoring_pass": False,
            "first_path": "",
            "output_chars": 0,
            "anchor_present": "",
            "result_count": "",
            "workflow_outcome": "skipped",
            "readiness": "unsupported",
        },
        diagnostics=[{"type": "skipped_tool", "reason": reason}],
    )


def execute_miller_row(mcp: McpProcess, row: dict[str, Any]) -> dict[str, Any]:
    tool_name = row["miller"]["tool"]
    tool_args = apply_miller_defaults(row)
    elapsed, response = mcp.call_tool(tool_name, tool_args, timeout=90)
    text = content_text(response)
    parse_json = bool(tool_name == "search" and tool_args.get("format") == "json")
    scored = score_manifest_path(text, row["expected"], row["scoring"], parse_json=parse_json)
    diagnostics: list[dict[str, Any]] = []
    if "error" in response:
        diagnostics.append({"type": "tool_error", "message": response["error"]})
    return result_from_score(
        row,
        tool=f"miller.{tool_name}",
        route="mcp",
        ms=elapsed,
        hard_gate=bool(row["gate"]["hard"]),
        scored=scored,
        diagnostics=diagnostics,
    )


def cli_command_label(args: list[str]) -> str:
    return " ".join(["miller", *args])


def run_cli(args: list[str], *, timeout: int = 180) -> tuple[int, subprocess.CompletedProcess[str]]:
    started = time.perf_counter()
    proc = subprocess.run(
        [str(MILLER), *args],
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=timeout,
    )
    elapsed_ms = int((time.perf_counter() - started) * 1000)
    return elapsed_ms, proc


def load_cli_capabilities() -> tuple[dict[str, Any] | None, list[dict[str, Any]]]:
    elapsed, proc = run_cli(["capabilities", "--json"], timeout=30)
    diagnostics: list[dict[str, Any]] = []
    if proc.returncode != 0:
        diagnostics.append(
            {
                "type": "capabilities_error",
                "ms": elapsed,
                "exit_code": proc.returncode,
                "stderr": proc.stderr.strip(),
            }
        )
        return None, diagnostics
    try:
        parsed = json.loads(proc.stdout)
    except json.JSONDecodeError as exc:
        diagnostics.append(
            {
                "type": "capabilities_parse_failure",
                "ms": elapsed,
                "message": str(exc),
                "line": exc.lineno,
                "column": exc.colno,
            }
        )
        return None, diagnostics
    if not isinstance(parsed, dict):
        diagnostics.append({"type": "capabilities_parse_failure", "ms": elapsed, "message": "expected object"})
        return None, diagnostics
    return parsed, diagnostics


def cli_exit_score(
    row: dict[str, Any],
    *,
    output_chars: int,
    outcome: str,
    scoring_pass: bool,
) -> dict[str, Any]:
    return {
        "empty": output_chars == 0,
        "expected_present": False,
        "expected_top": False,
        "scoring_pass": scoring_pass,
        "first_path": "",
        "output_chars": output_chars,
        "anchor_present": "",
        "result_count": "",
        "contract_parse_ok": False,
        "contract_outcome": outcome,
        "scoring_mode": row["scoring"]["mode"],
        "diagnostics": [],
    }


def execute_cli_row(row: dict[str, Any], capabilities: dict[str, Any] | None) -> dict[str, Any]:
    args = list(row["cli"]["args"])
    elapsed, proc = run_cli(args)
    text = proc.stdout
    diagnostics: list[dict[str, Any]] = []
    command = cli_command_label(args)

    if proc.stderr.strip():
        diagnostics.append({"type": "cli_stderr", "message": proc.stderr.strip()})

    expected_exit_codes = set(row["cli"].get("expected_exit_codes") or [])
    if proc.returncode != 0:
        diagnostics.append({"type": "cli_exit_code", "exit_code": proc.returncode})
        optional_unsupported = bool(row["cli"].get("allow_unsupported")) or proc.returncode in expected_exit_codes
        scored = cli_exit_score(
            row,
            output_chars=len(text),
            outcome="unsupported" if optional_unsupported else "nonzero_exit",
            scoring_pass=False,
        )
    else:
        scored = score_manifest_path(text, row["expected"], row["scoring"], capabilities=capabilities)

    scored["cli_command"] = command
    scored["cli_exit_code"] = proc.returncode
    return result_from_score(
        row,
        tool="miller.cli",
        route="cli",
        ms=elapsed,
        hard_gate=bool(row["gate"]["hard"]),
        scored=scored,
        diagnostics=diagnostics,
    )


def execute_julie_row(julie: McpProcess, row: dict[str, Any]) -> dict[str, Any]:
    tool_name = row["julie"]["tool"]
    elapsed, response = julie.call_tool(tool_name, dict(row["julie"]["args"]), timeout=120)
    text = content_text(response)
    scored = score_manifest_path(text, row["expected"], row["scoring"])
    diagnostics: list[dict[str, Any]] = []
    if "error" in response:
        diagnostics.append({"type": "tool_error", "message": response["error"]})
    hard_gate = bool(row["gate"]["hard"] and not row["julie"].get("report_only", False))
    return result_from_score(
        row,
        tool=f"julie.{tool_name}",
        route="mcp",
        ms=elapsed,
        hard_gate=hard_gate,
        scored=scored,
        diagnostics=diagnostics,
    )


def refresh_miller_repos(mcp: McpProcess, repos: set[str]) -> list[dict[str, Any]]:
    diagnostics: list[dict[str, Any]] = []
    for repo in sorted(repos):
        elapsed, response = mcp.call_tool(
            "workspace",
            {"operation": "refresh", "path": REPO_ROOTS[repo]},
            timeout=180,
        )
        if "error" in response:
            diagnostics.append(
                {
                    "type": "refresh_error",
                    "repo": repo,
                    "ms": elapsed,
                    "message": response["error"],
                }
            )
    return diagnostics


def julie_process_for_repo(
    repo: str,
    processes: dict[str, McpProcess],
    diagnostics: list[dict[str, Any]],
) -> McpProcess | None:
    if repo in processes:
        return processes[repo]
    if not JULIE.exists():
        return None

    julie = McpProcess([str(JULIE), "--workspace", REPO_ROOTS[repo]], timeout=60)
    processes[repo] = julie
    elapsed, response = julie.call_tool(
        "manage_workspace",
        {"operation": "open", "path": REPO_ROOTS[repo]},
        timeout=240,
    )
    if "error" in response:
        diagnostics.append(
            {
                "type": "julie_open_error",
                "repo": repo,
                "ms": elapsed,
                "message": response["error"],
            }
        )
    return julie


def aggregate_gate_required(spec: dict[str, Any], selected_rows: int) -> int:
    if selected_rows <= 0:
        return 0
    expected_rows = int(spec["expected_rows"])
    if selected_rows >= expected_rows:
        return int(spec["minimum"])
    return max(1, math.ceil(int(spec["minimum"]) * selected_rows / expected_rows))


def is_aggregate_gate_row(row: dict[str, Any]) -> bool:
    return (
        row.get("repo") in ORIGINAL_NINE_REPOS
        and (str(row.get("tool")), str(row.get("task_class"))) in AGGREGATE_GATE_KEYS
    )


def evaluate_gate_thresholds(results: list[dict[str, Any]]) -> list[dict[str, Any]]:
    checks: list[dict[str, Any]] = []
    for spec in AGGREGATE_GATE_SPECS:
        bucket = [
            row
            for row in results
            if row.get("repo") in ORIGINAL_NINE_REPOS
            and row.get("tool") == spec["tool"]
            and row.get("task_class") == spec["task_class"]
        ]
        selected_rows = len(bucket)
        required = aggregate_gate_required(spec, selected_rows)
        observed = sum(1 for row in bucket if row.get(str(spec["metric"])))
        if selected_rows == 0:
            status = "SKIP"
        else:
            status = "PASS" if observed >= required else "FAIL"
        checks.append(
            {
                "id": spec["id"],
                "label": spec["label"],
                "tool": spec["tool"],
                "task_class": spec["task_class"],
                "metric": spec["metric"],
                "observed": observed,
                "required": required,
                "selected_rows": selected_rows,
                "expected_rows": spec["expected_rows"],
                "status": status,
                "rationale": spec["rationale"],
            }
        )

    contract_rows = [
        row
        for row in results
        if row.get("tool") == "miller.cli" and str(row.get("scoring_mode", "")).startswith("contract_")
    ]
    parse_failures = [row for row in contract_rows if row.get("contract_parse_ok") is not True]
    checks.append(
        {
            "id": "eros.contracts.parse_failures",
            "label": "Eros-facing CLI contract parse failures",
            "tool": "miller.cli",
            "task_class": "contract.cli.*",
            "metric": "contract_parse_ok",
            "observed": len(parse_failures),
            "required": 0,
            "selected_rows": len(contract_rows),
            "expected_rows": len(contract_rows),
            "status": "PASS" if not parse_failures else "FAIL",
            "rationale": "protects JSON/JSONL parseability for active Eros process contracts",
        }
    )
    return checks


def gate_failures(results: list[dict[str, Any]]) -> list[str]:
    failures: list[str] = []
    for check in evaluate_gate_thresholds(results):
        if check["status"] != "FAIL":
            continue
        if check["id"] == "eros.contracts.parse_failures":
            failures.append(
                f"{check['id']}: {check['observed']} parse failures across {check['selected_rows']} contract rows"
            )
            continue
        failures.append(
            f"{check['id']}: {check['observed']}/{check['selected_rows']} {check['metric']} below "
            f"{check['required']}/{check['expected_rows']}"
        )
    for row in results:
        if not row["hard_gate"]:
            continue
        if is_aggregate_gate_row(row):
            continue
        diagnostic_types = {diagnostic.get("type") for diagnostic in row["diagnostics"]}
        if "tool_error" in diagnostic_types:
            failures.append(f"{row['row_id']}/{row['tool']}: tool returned an error")
        elif str(row.get("scoring_mode", "")).startswith("contract_"):
            if not row["scoring_pass"]:
                failures.append(
                    f"{row['row_id']}/{row['tool']}: contract outcome {row.get('contract_outcome') or 'failed'}"
                )
        elif row.get("scoring_mode") in WORKFLOW_SCORING_MODES:
            if not row["scoring_pass"]:
                failures.append(f"{row['row_id']}/{row['tool']}: scoring mode {row['scoring_mode']} did not pass")
        elif row["empty"]:
            failures.append(f"{row['row_id']}/{row['tool']}: output was empty")
        elif not row["expected_present"]:
            failures.append(f"{row['row_id']}/{row['tool']}: expected path was absent")
        elif not row["scoring_pass"] and row.get("scoring_mode") == "path_top":
            failures.append(f"{row['row_id']}/{row['tool']}: expected path was not top-ranked")
        elif not row["scoring_pass"]:
            failures.append(f"{row['row_id']}/{row['tool']}: scoring mode {row['scoring_mode']} did not pass")
        elif row["anchor_present"] is False:
            failures.append(f"{row['row_id']}/{row['tool']}: expected anchor was absent")
    return failures


def adoption_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def adoption_int(value: Any) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def adoption_float(value: Any) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def adoption_p95(values: list[float]) -> int:
    if not values:
        return 0
    index = max(0, math.ceil(len(values) * 0.95) - 1)
    return int(sorted(values)[index])


def adoption_op(value: Any) -> str:
    return str(value) if value is not None and str(value) else "default"


def telemetry_is_error(row: dict[str, Any]) -> bool:
    if row.get("error_kind"):
        return True
    outcome = str(row.get("outcome") or "").lower()
    return bool(outcome and outcome not in {"ok", "empty", "no_results", "not_found"})


def telemetry_is_empty(row: dict[str, Any]) -> bool:
    outcome = str(row.get("outcome") or "").lower()
    return adoption_int(row.get("result_count")) == 0 or outcome in {"empty", "no_results", "not_found"}


def telemetry_tool_rows(rows: list[dict[str, Any]], workspace_root: str) -> tuple[list[dict[str, Any]], bool]:
    workspace_rows = [row for row in rows if row.get("workspace_root") == workspace_root]
    return workspace_rows, bool(workspace_rows or not rows)


def summarize_telemetry_rows(rows: list[dict[str, Any]], workspace_root: str) -> dict[str, Any]:
    workspace_rows, workspace_filter_matched = telemetry_tool_rows(rows, workspace_root)
    timestamps = sorted(str(row.get("ts") or "") for row in workspace_rows if row.get("ts"))
    tool_counts = {tool: 0 for tool in CORE_ADOPTION_TOOLS}
    buckets: dict[tuple[str, str], dict[str, Any]] = defaultdict(
        lambda: {
            "calls": 0,
            "result_count": 0,
            "empty_count": 0,
            "error_count": 0,
            "durations": [],
        }
    )

    for row in workspace_rows:
        tool = str(row.get("tool") or "")
        op = adoption_op(row.get("op"))
        if tool in tool_counts:
            tool_counts[tool] += 1
        bucket = buckets[(tool, op)]
        bucket["calls"] += 1
        bucket["result_count"] += adoption_int(row.get("result_count"))
        bucket["empty_count"] += 1 if telemetry_is_empty(row) else 0
        bucket["error_count"] += 1 if telemetry_is_error(row) else 0
        bucket["durations"].append(adoption_float(row.get("duration_ms")))

    core_rows: list[dict[str, Any]] = []
    for tool in CORE_ADOPTION_TOOLS:
        tool_keys = sorted((key for key in buckets if key[0] == tool), key=lambda key: (-buckets[key]["calls"], key[1]))
        if not tool_keys:
            tool_keys = [(tool, "default")]
        for key in tool_keys:
            bucket = buckets[key]
            durations = bucket["durations"]
            calls = adoption_int(bucket["calls"])
            core_rows.append(
                {
                    "tool": key[0],
                    "op": key[1],
                    "calls": calls,
                    "result_count": adoption_int(bucket["result_count"]),
                    "empty_count": adoption_int(bucket["empty_count"]),
                    "error_count": adoption_int(bucket["error_count"]),
                    "avg_ms": int(sum(durations) / len(durations)) if durations else 0,
                    "p95_ms": adoption_p95(durations),
                }
            )

    total_core_calls = sum(tool_counts.values())
    low_use_tools: list[dict[str, Any]] = []
    for tool, calls in sorted(tool_counts.items(), key=lambda item: (item[1], item[0])):
        share = (calls / total_core_calls) if total_core_calls else 0.0
        if calls == 0 or share <= 0.05:
            low_use_tools.append(
                {
                    "tool": tool,
                    "calls": calls,
                    "share": share,
                    "note": "report-only low usage in this local telemetry window",
                }
            )
    if not low_use_tools and tool_counts:
        tool, calls = min(tool_counts.items(), key=lambda item: (item[1], item[0]))
        low_use_tools.append(
            {
                "tool": tool,
                "calls": calls,
                "share": (calls / total_core_calls) if total_core_calls else 0.0,
                "note": "lowest observed core-tool usage; report-only",
            }
        )

    return {
        "workspace_root": workspace_root,
        "workspace_filter_matched": workspace_filter_matched,
        "exported_total_calls": len(rows),
        "workspace_total_calls": len(workspace_rows),
        "window_start_ts": timestamps[0] if timestamps else "",
        "window_end_ts": timestamps[-1] if timestamps else "",
        "tool_counts": tool_counts,
        "core_tool_op_mix": core_rows,
        "core_empty_error_rates": core_rows,
        "low_use_tools": low_use_tools,
    }


def summarize_onboarding(parsed: dict[str, Any]) -> dict[str, Any]:
    return {
        "telemetry": parsed.get("telemetry") if isinstance(parsed.get("telemetry"), dict) else {},
        "start_here": parsed.get("start_here") if isinstance(parsed.get("start_here"), list) else [],
        "tool_mix": parsed.get("tool_mix")[:10] if isinstance(parsed.get("tool_mix"), list) else [],
        "common_misses": parsed.get("common_misses")[:10] if isinstance(parsed.get("common_misses"), list) else [],
        "friction": parsed.get("friction")[:10] if isinstance(parsed.get("friction"), list) else [],
    }


def workflow_candidates_from_prior_results(path: Path) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    diagnostics: list[dict[str, Any]] = []
    if not path.exists():
        return [], [{"type": "workflow_results_missing", "path": str(path)}]
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        return [], [{"type": "workflow_results_parse_failure", "path": str(path), "message": str(exc)}]
    results = document.get("results") if isinstance(document, dict) else None
    if not isinstance(results, list):
        return [], [{"type": "workflow_results_invalid", "path": str(path), "message": "missing results list"}]

    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in results:
        if isinstance(row, dict) and row.get("row_id"):
            grouped[str(row["row_id"])].append(row)

    candidates: list[dict[str, Any]] = []
    skipped_julie_rows = 0
    for row_id, rows in sorted(grouped.items()):
        miller_rows = [row for row in rows if str(row.get("tool", "")).startswith("miller.")]
        julie_rows = [row for row in rows if str(row.get("tool", "")).startswith("julie.")]
        skipped_julie_rows += sum(1 for row in julie_rows if row.get("route") == "skipped")
        julie_pass = [row for row in julie_rows if row.get("scoring_pass")]
        miller_friction = [
            row
            for row in miller_rows
            if row.get("adaptation_candidate")
            or not row.get("scoring_pass")
            or str(row.get("workflow_outcome") or "") in {"needs-search", "unsupported", "no-path"}
        ]
        if not julie_pass or not miller_friction:
            continue
        miller = miller_friction[0]
        julie = julie_pass[0]
        candidates.append(
            {
                "row_id": row_id,
                "repo": miller.get("repo") or julie.get("repo") or "",
                "intent": miller.get("intent") or julie.get("intent") or "",
                "miller_outcome": str(miller.get("workflow_outcome") or miller.get("scoring_mode") or ""),
                "julie_outcome": str(julie.get("workflow_outcome") or julie.get("scoring_mode") or ""),
                "note": "Julie-like one-call row passed while the Miller row still showed friction",
            }
        )
    if not candidates and skipped_julie_rows:
        diagnostics.append(
            {
                "type": "workflow_candidates_unavailable",
                "reason": "prior Task 4 workflow evidence skipped Julie rows",
                "skipped_julie_rows": skipped_julie_rows,
            }
        )
    return candidates, diagnostics


def run_adoption_analysis(workspace_root: str) -> dict[str, Any]:
    failures: list[str] = []
    diagnostics: list[dict[str, Any]] = []
    telemetry_elapsed, telemetry_proc = run_cli(["telemetry", "export", "--jsonl"], timeout=120)
    onboarding_elapsed, onboarding_proc = run_cli(
        ["workspace", "onboarding", "--json", "--workspace-id", workspace_root],
        timeout=90,
    )

    telemetry_rows: list[dict[str, Any]] = []
    telemetry_non_empty_lines = 0
    telemetry_parse_diagnostics: list[dict[str, Any]] = []
    telemetry_no_rows = True
    if telemetry_proc.returncode != 0:
        failures.append(f"telemetry export exited {telemetry_proc.returncode}")
        diagnostics.append({"type": "telemetry_exit_code", "exit_code": telemetry_proc.returncode, "stderr": telemetry_proc.stderr.strip()})
    else:
        telemetry_rows, telemetry_non_empty_lines, telemetry_parse_diagnostics = parse_jsonl_objects(telemetry_proc.stdout)
        telemetry_no_rows = telemetry_non_empty_lines == 0
        if telemetry_parse_diagnostics:
            failures.append("telemetry JSONL parse failed")
            diagnostics.extend(telemetry_parse_diagnostics)
        schema_sample = telemetry_rows[:200]
        for index, row in enumerate(schema_sample):
            _, missing_fields = count_present_fields(row, TELEMETRY_SCHEMA_FIELDS)
            if missing_fields:
                failures.append(f"telemetry row {index} missing fields: {', '.join(missing_fields)}")
                diagnostics.append({"type": "telemetry_schema_missing_fields", "row": index, "fields": missing_fields})
                break

    telemetry_summary = summarize_telemetry_rows(telemetry_rows, workspace_root)

    onboarding_parse: dict[str, Any] | None = None
    onboarding_missing_fields: list[str] = []
    onboarding_shape_errors: list[dict[str, Any]] = []
    if onboarding_proc.returncode != 0:
        failures.append(f"workspace onboarding exited {onboarding_proc.returncode}")
        diagnostics.append({"type": "onboarding_exit_code", "exit_code": onboarding_proc.returncode, "stderr": onboarding_proc.stderr.strip()})
    else:
        try:
            parsed = json.loads(onboarding_proc.stdout)
        except json.JSONDecodeError as exc:
            failures.append("onboarding JSON parse failed")
            diagnostics.append({"type": "onboarding_parse_failure", "message": str(exc), "line": exc.lineno, "column": exc.colno})
        else:
            if not isinstance(parsed, dict):
                failures.append("onboarding JSON was not an object")
                diagnostics.append({"type": "onboarding_parse_failure", "message": "expected object"})
            else:
                onboarding_parse = parsed
                _, onboarding_missing_fields = count_present_fields(parsed, ONBOARDING_REQUIRED_FIELDS)
                if onboarding_missing_fields:
                    failures.append(f"onboarding missing fields: {', '.join(onboarding_missing_fields)}")
                    diagnostics.append({"type": "onboarding_missing_fields", "fields": onboarding_missing_fields})
                for field in ["common_misses", "friction"]:
                    if field in parsed and not isinstance(parsed[field], list):
                        onboarding_shape_errors.append({"type": "onboarding_invalid_field", "field": field, "expected": "list"})
                if onboarding_shape_errors:
                    failures.append("onboarding friction/miss fields had invalid shapes")
                    diagnostics.extend(onboarding_shape_errors)

    workflow_candidates, workflow_diagnostics = workflow_candidates_from_prior_results(TASK4_WORKFLOW_RESULTS)
    diagnostics.extend(workflow_diagnostics)
    workflow_candidate_note = ""
    if any(item.get("type") == "workflow_candidates_unavailable" for item in workflow_diagnostics):
        workflow_candidate_note = "Prior Task 4 workflow evidence was run with Julie rows skipped, so no Julie-style one-call superiority conclusion is drawn."

    analysis = {
        "generated_at": adoption_now(),
        "commands": {
            "telemetry": cli_command_label(["telemetry", "export", "--jsonl"]),
            "onboarding": cli_command_label(["workspace", "onboarding", "--json", "--workspace-id", workspace_root]),
        },
        "gate": {
            "status": "FAIL" if failures else "PASS",
            "failures": failures,
        },
        "parseability": {
            "telemetry": {
                "exit_code": telemetry_proc.returncode,
                "ms": telemetry_elapsed,
                "parsed": telemetry_proc.returncode == 0 and not telemetry_parse_diagnostics,
                "no_telemetry": telemetry_no_rows,
                "non_empty_lines": telemetry_non_empty_lines,
                "parsed_rows": len(telemetry_rows),
                "sampled_rows": min(len(telemetry_rows), 200),
                "required_fields": TELEMETRY_SCHEMA_FIELDS,
            },
            "onboarding": {
                "exit_code": onboarding_proc.returncode,
                "ms": onboarding_elapsed,
                "parsed": onboarding_parse is not None,
                "required_fields": ONBOARDING_REQUIRED_FIELDS,
                "required_fields_present": len(ONBOARDING_REQUIRED_FIELDS) - len(onboarding_missing_fields),
                "required_fields_total": len(ONBOARDING_REQUIRED_FIELDS),
                "missing_fields": onboarding_missing_fields,
                "friction_miss_fields_present": [field for field in ["common_misses", "friction"] if onboarding_parse and field in onboarding_parse],
            },
        },
        "telemetry": telemetry_summary,
        "core_tool_op_mix": telemetry_summary["core_tool_op_mix"],
        "core_empty_error_rates": telemetry_summary["core_empty_error_rates"],
        "low_use_tools": telemetry_summary["low_use_tools"],
        "onboarding": summarize_onboarding(onboarding_parse or {}),
        "workflow_candidates": workflow_candidates,
        "workflow_candidate_note": workflow_candidate_note,
        "diagnostics": diagnostics,
    }
    return analysis


def write_adoption_outputs(out_dir: Path, analysis: dict[str, Any]) -> Path:
    out_dir.mkdir(parents=True, exist_ok=True)
    json_path = out_dir / "adoption-summary.json"
    json_path.write_text(json.dumps(analysis, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    summary_path = out_dir / "adoption-summary.md"
    summary_path.write_text(summarize_adoption_analysis(analysis) + "\n", encoding="utf-8")
    return summary_path


def median_int(values: list[int]) -> int:
    if not values:
        return 0
    ordered = sorted(values)
    midpoint = len(ordered) // 2
    if len(ordered) % 2:
        return ordered[midpoint]
    return int((ordered[midpoint - 1] + ordered[midpoint]) / 2)


def calibration_row_count(rows: list[dict[str, Any]], field: str) -> int:
    return sum(1 for row in rows if row.get(field))


def build_threshold_lines(thresholds: list[dict[str, Any]]) -> list[str]:
    lines = [
        "| gate | observed | required | status | rationale |",
        "|---|---:|---:|---|---|",
    ]
    for check in thresholds:
        if check["id"] == "eros.contracts.parse_failures":
            observed = f"{check['observed']} parse failures / {check['selected_rows']} rows"
            required = "0 parse failures"
        else:
            observed = f"{check['observed']} / {check['selected_rows']}"
            required = f"{check['required']} / {check['expected_rows']}"
        lines.append(
            f"| `{check['id']}` | {observed} | {required} | {check['status']} | {check['rationale']} |"
        )
    return lines


def build_report_only_lines(results: list[dict[str, Any]]) -> list[str]:
    lines: list[str] = []
    julie_rows = [row for row in results if str(row.get("tool", "")).startswith("julie.")]
    if julie_rows:
        skipped = sum(
            1
            for row in julie_rows
            if any(diagnostic.get("type") == "skipped_tool" for diagnostic in row.get("diagnostics", []))
        )
        lines.append(
            f"- Julie rows are report-only: {calibration_row_count(julie_rows, 'expected_present')}/{len(julie_rows)} "
            f"present, {calibration_row_count(julie_rows, 'expected_top')}/{len(julie_rows)} top-ranked, "
            f"{calibration_row_count(julie_rows, 'scoring_pass')}/{len(julie_rows)} selected-mode pass"
            f"{f', {skipped} skipped' if skipped else ''}."
        )

    miller_rows = [row for row in results if str(row.get("tool", "")).startswith("miller.")]
    top_gap_rows = [
        row
        for row in miller_rows
        if row.get("scoring_mode") in {"path_present", "path_any_present"}
        and row.get("expected_present")
        and not row.get("expected_top")
    ]
    if top_gap_rows:
        by_task: dict[str, int] = defaultdict(int)
        for row in top_gap_rows:
            by_task[str(row["task_class"])] += 1
        summary = ", ".join(f"{task}={count}" for task, count in sorted(by_task.items()))
        lines.append(
            "- Miller top-rank gaps stay report-only unless a row uses `path_top`: "
            f"{len(top_gap_rows)} present-but-not-top rows ({summary})."
        )

    workflow_rows = [
        row
        for row in results
        if str(row.get("task_class", "")).endswith(".workflow") or row.get("scoring_mode") in {"workflow_anchors", "impact_targets"}
    ]
    anchor_total = sum(adoption_int(row.get("expected_anchor_count")) for row in workflow_rows)
    anchor_present = sum(adoption_int(row.get("expected_anchors_present")) for row in workflow_rows)
    if workflow_rows:
        lines.append(
            "- Workflow call-count-to-anchor remains report-only: "
            f"{anchor_present}/{anchor_total} required anchors present across {len(workflow_rows)} workflow rows."
        )

    for tool in sorted({str(row.get("tool")) for row in miller_rows}):
        bucket = [row for row in miller_rows if row.get("tool") == tool]
        ms = [adoption_int(row.get("ms")) for row in bucket]
        chars = [adoption_int(row.get("output_chars")) for row in bucket]
        lines.append(
            f"- {tool} latency/output-size are report-only: median {median_int(ms)} ms, "
            f"median {median_int(chars)} chars across {len(bucket)} rows."
        )

    metrics_rows = [
        row
        for row in results
        if "metrics" in str(row.get("row_id", "")).lower()
        or "metrics" in str(row.get("cli_command", "")).lower()
    ]
    if metrics_rows:
        lines.append(f"- Metrics CLI contract rows are report-only here: {len(metrics_rows)} rows present.")
    else:
        lines.append("- No metrics CLI contract rows are present in this manifest; metrics remain report-only.")

    lines.append(
        "- Adoption and telemetry interpretation remains report-only; parseability evidence lives in the Task 6 adoption run."
    )
    return lines


def write_calibration_notes(
    out_dir: Path,
    thresholds: list[dict[str, Any]],
    results: list[dict[str, Any]],
    failures: list[str],
) -> Path:
    calibration_path = out_dir / "calibration.md"
    status = "FAIL" if failures else "PASS"
    lines = [
        "# Foundation Matrix Gate Calibration",
        "",
        f"Gate status: **{status}**",
        "",
        "## Hard Gates",
        "",
        "These are the only hard gates for this final baseline run. They protect shipped Miller behavior and active Eros-facing process contracts without turning known product-improvement work into a blocker.",
        "",
        *build_threshold_lines(thresholds),
        "",
        "## Report-Only Miss Summary",
        "",
        *build_report_only_lines(results),
    ]
    if failures:
        lines.extend(["", "## Gate Failures", ""])
        lines.extend(f"- {failure}" for failure in failures)
    calibration_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return calibration_path


def write_outputs(
    out_dir: Path,
    manifest_path: Path,
    args: argparse.Namespace,
    results: list[dict[str, Any]],
    run_diagnostics: list[dict[str, Any]],
    failures: list[str],
) -> Path:
    out_dir.mkdir(parents=True, exist_ok=True)
    thresholds = evaluate_gate_thresholds(results)
    calibration_path = write_calibration_notes(out_dir, thresholds, results, failures)

    csv_path = out_dir / "results.csv"
    with csv_path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=CSV_FIELDS, extrasaction="ignore", lineterminator="\n")
        writer.writeheader()
        writer.writerows(results)

    json_path = out_dir / "results.json"
    json_path.write_text(
        json.dumps(
            {
                "manifest": str(manifest_path),
                "filters": {
                    "repos": args.repos,
                    "tasks": args.tasks,
                    "skip_julie": args.skip_julie,
                    "skip_miller_refresh": args.skip_miller_refresh,
                },
                "diagnostics": run_diagnostics,
                "gate": {
                    "enabled": args.gate,
                    "status": "FAIL" if failures else "PASS",
                    "failures": failures,
                    "thresholds": thresholds,
                },
                "calibration": {
                    "path": str(calibration_path),
                },
                "results": results,
            },
            indent=2,
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )

    summary_path = out_dir / "summary.md"
    selected_repos = sorted({row["repo"] for row in results})
    summary = [
        "# Miller Foundation Matrix Benchmark",
        "",
        f"Manifest: `{manifest_path}`",
        f"Repos: {', '.join(selected_repos)}",
        "",
        "Scoring: `present` means the expected file path appeared in the result. `top` records whether the first parsed path was the expected file. `pass` records the selected scoring mode: top-ranked for `path_top`, otherwise presence. Hard gates require the selected scoring mode to pass, while Julie rows are report-only.",
        "",
        "Workflow fields keep path scoring intact: `expected_anchor_count`/`expected_anchors_present` score required workflow anchors, `first_useful_anchor` records the first matched anchor, `follow_up_hint_present` records guidance such as `next inspect`, `readiness` records edit/inspect/search state, and `workflow_outcome` records structured `ok`, `needs-search`, `unsupported`, or `no-path` outcomes.",
        "",
        "Contract fields are explicit: `contract_parse_ok` records JSON/JSONL parsing, `required_fields_present` and `required_row_fields_present` record required contract fields, `advertised_commands_present` records `capabilities --json` coverage, `sampled_jsonl_rows` records the JSONL sample checked, and `contract_outcome` records `ok`, `empty_allowed`, `unsupported`, or the failure class.",
        "",
        "Calibrated hard gates are named aggregate thresholds for original-nine Miller retrieval/inspect behavior plus Eros-facing CLI contract parseability. Julie deltas, top-rank gaps, workflow call-count-to-anchor, latency, output-size, metrics CLI rows, and adoption interpretation are report-only calibration notes.",
        "",
        summarize_by_tool(results),
        "",
        "## Breakdown By Task Class",
        "",
        summarize_foundation_matrix(results),
        "",
        f"Raw CSV: `{csv_path}`",
        f"Raw JSON: `{json_path}`",
    ]
    if args.gate:
        summary.extend(["", "## Gate", "", "Status: " + ("FAIL" if failures else "PASS")])
        summary.extend(["", "### Thresholds", ""])
        summary.extend(build_threshold_lines(thresholds))
        if failures:
            summary.extend(["", "### Failures", ""])
            summary.extend(f"- {failure}" for failure in failures)
    if run_diagnostics:
        summary.extend(["", "## Run Diagnostics", ""])
        summary.extend(f"- {item['type']}: {item}" for item in run_diagnostics)
    summary.extend(["", f"Calibration notes: `{calibration_path}`"])
    summary_path.write_text("\n".join(summary) + "\n", encoding="utf-8")
    return summary_path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", default=str(DEFAULT_MANIFEST))
    parser.add_argument("--out-dir", default=str(ROOT / "docs/findings/benchmarks/foundation-matrix"))
    parser.add_argument("--repos", default="all")
    parser.add_argument("--tasks", default="all")
    parser.add_argument("--skip-julie", action="store_true")
    parser.add_argument("--skip-miller-refresh", action="store_true")
    parser.add_argument("--gate", action="store_true", help="fail when hard Miller rows miss expected paths")
    parser.add_argument("--adoption-only", action="store_true", help="write telemetry/onboarding adoption evidence and exit")
    args = parser.parse_args()

    if args.adoption_only:
        if not MILLER.exists():
            print(f"Miller binary not found: {MILLER}", file=sys.stderr)
            return 2
        analysis = run_adoption_analysis(REPO_ROOTS["miller"])
        summary_path = write_adoption_outputs(Path(args.out_dir), analysis)
        if analysis["gate"]["failures"]:
            print("adoption analysis gate failed:", file=sys.stderr)
            for failure in analysis["gate"]["failures"]:
                print(f"- {failure}", file=sys.stderr)
        print(summary_path)
        return 1 if analysis["gate"]["failures"] else 0

    manifest_path = Path(args.manifest)
    rows, errors = load_manifest(manifest_path)
    if errors:
        print("manifest validation failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 2

    selected_rows, errors = select_rows(rows, split_filter(args.repos), split_filter(args.tasks))
    if errors:
        print("manifest selection failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 2

    if not MILLER.exists():
        print(f"Miller binary not found: {MILLER}", file=sys.stderr)
        return 2

    results: list[dict[str, Any]] = []
    run_diagnostics: list[dict[str, Any]] = []
    julie_processes: dict[str, McpProcess] = {}
    miller_mcp: McpProcess | None = None
    mcp_rows = [row for row in selected_rows if row_route(row) == "mcp"]
    cli_rows = [row for row in selected_rows if row_route(row) == "cli"]
    capabilities: dict[str, Any] | None = None
    if cli_rows:
        capabilities, capability_diagnostics = load_cli_capabilities()
        run_diagnostics.extend(capability_diagnostics)

    try:
        if mcp_rows:
            miller_mcp = McpProcess([str(MILLER), "serve"], timeout=60)
            if not args.skip_miller_refresh:
                run_diagnostics.extend(refresh_miller_repos(miller_mcp, {row["repo"] for row in mcp_rows}))

        for row in selected_rows:
            print(f"== {row['id']} ==", file=sys.stderr)
            if row_route(row) == "cli":
                results.append(execute_cli_row(row, capabilities))
                continue

            if miller_mcp is None:
                results.append(skipped_result(row, tool=f"miller.{row['miller']['tool']}", reason="Miller MCP was not started"))
                continue
            results.append(execute_miller_row(miller_mcp, row))

            julie_tool = f"julie.{row['julie']['tool']}"
            if args.skip_julie:
                results.append(skipped_result(row, tool=julie_tool, reason="--skip-julie"))
                continue

            julie = julie_process_for_repo(row["repo"], julie_processes, run_diagnostics)
            if julie is None:
                results.append(skipped_result(row, tool=julie_tool, reason=f"Julie binary not found: {JULIE}"))
                continue
            results.append(execute_julie_row(julie, row))
    finally:
        if miller_mcp is not None:
            miller_mcp.close()
        for julie in julie_processes.values():
            julie.close()

    failures = gate_failures(results) if args.gate else []
    summary_path = write_outputs(Path(args.out_dir), manifest_path, args, results, run_diagnostics, failures)
    if failures:
        print("foundation matrix gate failed:", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
    print(summary_path)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
