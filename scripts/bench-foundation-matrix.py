#!/usr/bin/env python3
"""Run manifest-driven Miller foundation benchmark rows."""

from __future__ import annotations

import argparse
import csv
import json
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

from benchlib import McpProcess, content_text, summarize_by_tool
from benchlib.reporting import summarize_foundation_matrix
from benchlib.scoring import score_manifest_path


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
SUPPORTED_READINESS = {"edit-ready", "inspect-ready", "needs-search", "unsupported", "no-path"}
SUPPORTED_WORKFLOW_OUTCOMES = {"ok", "needs-search", "unsupported", "no-path"}
BASE_REQUIRED_ROW_KEYS = {"id", "repo", "task_class", "intent", "expected", "scoring", "gate"}

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


def gate_failures(results: list[dict[str, Any]]) -> list[str]:
    failures: list[str] = []
    for row in results:
        if not row["hard_gate"]:
            continue
        diagnostic_types = {diagnostic.get("type") for diagnostic in row["diagnostics"]}
        if "tool_error" in diagnostic_types:
            failures.append(f"{row['row_id']}/{row['tool']}: tool returned an error")
        elif str(row.get("scoring_mode", "")).startswith("contract_"):
            if not row["scoring_pass"]:
                failures.append(
                    f"{row['row_id']}/{row['tool']}: contract outcome {row.get('contract_outcome') or 'failed'}"
                )
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


def write_outputs(
    out_dir: Path,
    manifest_path: Path,
    args: argparse.Namespace,
    results: list[dict[str, Any]],
    run_diagnostics: list[dict[str, Any]],
    failures: list[str],
) -> Path:
    out_dir.mkdir(parents=True, exist_ok=True)

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
        summary.extend(f"- {failure}" for failure in failures)
    if run_diagnostics:
        summary.extend(["", "## Run Diagnostics", ""])
        summary.extend(f"- {item['type']}: {item}" for item in run_diagnostics)
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
    args = parser.parse_args()

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
