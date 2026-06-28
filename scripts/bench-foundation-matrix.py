#!/usr/bin/env python3
"""Run manifest-driven Miller foundation benchmark rows."""

from __future__ import annotations

import argparse
import csv
import json
import sys
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

SUPPORTED_MILLER_TOOLS = {"search", "inspect"}
SUPPORTED_JULIE_TOOLS = {"fast_search", "deep_dive"}
SUPPORTED_SCORING_MODES = {"path_present", "path_top", "path_any_present"}
REQUIRED_ROW_KEYS = {"id", "repo", "task_class", "intent", "miller", "julie", "expected", "scoring", "gate"}

CSV_FIELDS = [
    "row_id",
    "repo",
    "task_class",
    "tool",
    "route",
    "hard_gate",
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
]


def split_filter(value: str) -> set[str]:
    if value.strip().lower() == "all":
        return set()
    return {item.strip() for item in value.split(",") if item.strip()}


def row_label(row: Any, index: int) -> str:
    if isinstance(row, dict) and row.get("id"):
        return f"row {index} ({row['id']})"
    return f"row {index}"


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

        missing = sorted(REQUIRED_ROW_KEYS - set(row))
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

        errors.extend(validate_tool_spec(row, label, "miller", SUPPORTED_MILLER_TOOLS))
        errors.extend(validate_tool_spec(row, label, "julie", SUPPORTED_JULIE_TOOLS))
        errors.extend(validate_expected(row, label))
        errors.extend(validate_scoring(row, label))
        errors.extend(validate_gate(row, label))

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
    elif key == "julie":
        if tool == "fast_search" and not isinstance(args.get("query"), str):
            errors.append(f"{label}: 'julie.args.query' is required for fast_search")
        if tool == "deep_dive" and not isinstance(args.get("symbol"), str):
            errors.append(f"{label}: 'julie.args.symbol' is required for deep_dive")

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
    expected_present = bool(scored.get("expected_present"))
    empty = bool(scored.get("empty"))
    anchor_missing = scored.get("anchor_present") is False
    return {
        "row_id": row["id"],
        "repo": row["repo"],
        "task_class": row["task_class"],
        "intent": row["intent"],
        "tool": tool,
        "route": route,
        "hard_gate": hard_gate,
        "expected_present": expected_present,
        "expected_top": bool(scored.get("expected_top")),
        "empty": empty,
        "ms": int(ms),
        "output_chars": int(scored.get("output_chars") or 0),
        "first_path": str(scored.get("first_path") or ""),
        "adaptation_candidate": bool(hard_gate and (empty or not expected_present or anchor_missing)),
        "expected_path": row["expected"]["path"],
        "anchor_present": scored.get("anchor_present", ""),
        "result_count": scored.get("result_count", ""),
        "diagnostics": row_diagnostics,
    }


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
            "first_path": "",
            "output_chars": 0,
            "anchor_present": "",
            "result_count": "",
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
        elif row["empty"]:
            failures.append(f"{row['row_id']}/{row['tool']}: output was empty")
        elif not row["expected_present"]:
            failures.append(f"{row['row_id']}/{row['tool']}: expected path was absent")
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
        writer = csv.DictWriter(f, fieldnames=CSV_FIELDS, extrasaction="ignore")
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
        "Scoring: `present` means the expected file path appeared in the result. `top` records whether the first parsed path was the expected file. Hard gates require presence, while Julie rows are report-only.",
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

    try:
        miller_mcp = McpProcess([str(MILLER), "serve"], timeout=60)
        if not args.skip_miller_refresh:
            run_diagnostics.extend(refresh_miller_repos(miller_mcp, {row["repo"] for row in selected_rows}))

        for row in selected_rows:
            print(f"== {row['id']} ==", file=sys.stderr)
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
