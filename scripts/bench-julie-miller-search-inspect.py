#!/usr/bin/env python3
"""Compare Miller search/inspect against Julie fast_search/deep_dive.

The benchmark is intentionally small and evidence-oriented:

* Repos are real local workspaces.
* Tasks name an expected file, not a subjective expected paragraph.
* Search is scored on whether the expected file is first/present/absent.
* Inspect/deep_dive is scored on whether it resolves to the expected file.
* Julie workspace-open time is recorded separately from per-call time.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import os
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from benchlib import (
    McpProcess,
    content_text,
    score_miller_search_json,
    score_text,
    summarize_by_task,
    summarize_by_tool,
)


ROOT = Path(__file__).resolve().parents[1]
MILLER = ROOT / "src/Miller.Server/bin/Release/net10.0/miller"
JULIE = Path("/Users/murphy/source/julie/target/release/julie-server")


@dataclass(frozen=True)
class RepoCase:
    name: str
    root: str
    language: str
    search_symbol: str
    search_file: str
    source_query: str
    source_expected_file: str
    inspect_target: str
    inspect_expected_file: str


REPOS: list[RepoCase] = [
    RepoCase(
        name="miller",
        root="/Users/murphy/source/miller",
        language="csharp",
        search_symbol="SearchTool",
        search_file="src/Miller.Server/Tools/SearchTool.cs",
        source_query="No results. Try a shorter symbol query",
        source_expected_file="src/Miller.Server/Tools/SearchTool.cs",
        inspect_target="SearchTool",
        inspect_expected_file="src/Miller.Server/Tools/SearchTool.cs",
    ),
    RepoCase(
        name="julie",
        root="/Users/murphy/source/julie",
        language="rust",
        search_symbol="FastSearchTool",
        search_file="crates/julie-tools/src/search/mod.rs",
        source_query="semantic fallback candidates",
        source_expected_file="crates/julie-tools/src/search/mod.rs",
        inspect_target="FastSearchTool",
        inspect_expected_file="crates/julie-tools/src/search/mod.rs",
    ),
    RepoCase(
        name="eros",
        root="/Users/murphy/source/eros",
        language="csharp",
        search_symbol="SemanticMillerImporter",
        search_file="src/Eros.Semantic/SemanticMillerImporter.cs",
        source_query="ReplaceSemanticInputs storeWorkspaceId inputs",
        source_expected_file="src/Eros.Semantic/SemanticMillerImporter.cs",
        inspect_target="SemanticMillerImporter",
        inspect_expected_file="src/Eros.Semantic/SemanticMillerImporter.cs",
    ),
    RepoCase(
        name="express",
        root="/Users/murphy/source/express",
        language="javascript",
        search_symbol="createApplication",
        search_file="lib/express.js",
        source_query="createServer this",
        source_expected_file="lib/application.js",
        inspect_target="createApplication",
        inspect_expected_file="lib/express.js",
    ),
    RepoCase(
        name="flask",
        root="/Users/murphy/source/flask",
        language="python",
        search_symbol="Flask",
        search_file="src/flask/app.py",
        source_query="The flask object implements a WSGI application",
        source_expected_file="src/flask/app.py",
        inspect_target="Flask",
        inspect_expected_file="src/flask/app.py",
    ),
    RepoCase(
        name="gson",
        root="/Users/murphy/source/gson",
        language="java",
        search_symbol="JsonParser",
        search_file="gson/src/main/java/com/google/gson/JsonParser.java",
        source_query="parseReader json",
        source_expected_file="gson/src/main/java/com/google/gson/JsonParser.java",
        inspect_target="JsonParser",
        inspect_expected_file="gson/src/main/java/com/google/gson/JsonParser.java",
    ),
    RepoCase(
        name="newtonsoft",
        root="/Users/murphy/source/Newtonsoft.Json",
        language="csharp",
        search_symbol="JsonConvert",
        search_file="Src/Newtonsoft.Json/JsonConvert.cs",
        source_query="SerializeObject",
        source_expected_file="Src/Newtonsoft.Json/JsonConvert.cs",
        inspect_target="JsonConvert",
        inspect_expected_file="Src/Newtonsoft.Json/JsonConvert.cs",
    ),
    RepoCase(
        name="zod",
        root="/Users/murphy/source/zod",
        language="typescript",
        search_symbol="ZodObject",
        search_file="packages/zod/src/v4/classic/schemas.ts",
        source_query="export const safeParseAsync",
        source_expected_file="packages/zod/src/v4/core/parse.ts",
        inspect_target="ZodObject",
        inspect_expected_file="packages/zod/src/v4/classic/schemas.ts",
    ),
    RepoCase(
        name="jq",
        root="/Users/murphy/source/jq",
        language="c",
        search_symbol="jv_parse",
        search_file="src/jv.h",
        source_query="Invalid literal",
        source_expected_file="src/jv_parse.c",
        inspect_target="jv_parse",
        inspect_expected_file="src/jv.h",
    ),
]


def now_ms() -> int:
    return int(time.perf_counter() * 1000)


def run_cmd(args: list[str], timeout: int = 120) -> tuple[int, int, str, str]:
    start = now_ms()
    proc = subprocess.run(args, text=True, capture_output=True, timeout=timeout)
    elapsed = now_ms() - start
    return elapsed, proc.returncode, proc.stdout, proc.stderr


def miller_search_cli(repo: RepoCase, query: str, expected: str, mode: str, json_output: bool = True) -> dict[str, Any]:
    args = [str(MILLER), "search", query, "--workspace", repo.root, "--mode", mode, "--limit", "5"]
    if json_output:
        args.append("--json")
    elapsed, code, stdout, stderr = run_cmd(args, timeout=90)
    scored = score_miller_search_json(stdout, expected) if json_output else score_text(stdout, expected)
    scored.update({"tool": f"miller.search.{mode}", "ms": elapsed, "exit_code": code, "stderr": stderr.strip()})
    return scored


def miller_search_mcp(
    mcp: McpProcess,
    repo: RepoCase,
    query: str,
    expected: str,
    mode: str,
    json_output: bool = True,
) -> dict[str, Any]:
    arguments: dict[str, Any] = {
        "query": query,
        "workspace_id": repo.root,
        "ensure_fresh": False,
        "mode": mode,
        "limit": 5,
    }
    if json_output:
        arguments["format"] = "json"
    elapsed, response = mcp.call_tool(
        "search",
        arguments,
        timeout=90,
    )
    stdout = content_text(response)
    scored = score_miller_search_json(stdout, expected) if json_output else score_text(stdout, expected)
    scored.update(
        {
            "tool": f"miller.search.{mode}",
            "ms": elapsed,
            "exit_code": 0 if "error" not in response else 1,
            "stderr": json.dumps(response.get("error", "")) if "error" in response else "",
        }
    )
    return scored


def miller_inspect_cli(repo: RepoCase, depth: str = "full") -> dict[str, Any]:
    args = [
        str(MILLER),
        "inspect",
        repo.inspect_target,
        "--workspace",
        repo.root,
        "--depth",
        depth,
    ]
    elapsed, code, stdout, stderr = run_cmd(args, timeout=90)
    scored = score_text(stdout, repo.inspect_expected_file)
    scored.update({"tool": f"miller.inspect.{depth}", "ms": elapsed, "exit_code": code, "stderr": stderr.strip()})
    return scored


def miller_inspect_mcp(mcp: McpProcess, repo: RepoCase, depth: str = "full") -> dict[str, Any]:
    elapsed, response = mcp.call_tool(
        "inspect",
        {
            "target": repo.inspect_target,
            "workspace_id": repo.root,
            "ensure_fresh": False,
            "depth": depth,
            "format": "compact",
        },
        timeout=90,
    )
    stdout = content_text(response)
    scored = score_text(stdout, repo.inspect_expected_file)
    scored.update(
        {
            "tool": f"miller.inspect.{depth}",
            "ms": elapsed,
            "exit_code": 0 if "error" not in response else 1,
            "stderr": json.dumps(response.get("error", "")) if "error" in response else "",
        }
    )
    return scored


def miller_refresh(repo: RepoCase) -> dict[str, Any]:
    elapsed, code, stdout, stderr = run_cmd(
        [str(MILLER), "refresh", "--workspace", repo.root, "--wait", "--json"],
        timeout=180,
    )
    return {
        "repo": repo.name,
        "repo_root": repo.root,
        "prep_tool": "miller.refresh",
        "ms": elapsed,
        "text": stdout.strip().replace("\n", "\\n"),
        "error": stderr.strip().replace("\n", "\\n") if code != 0 else "",
    }


def gate_threshold(n: int, numerator: int, denominator: int) -> int:
    return math.ceil(n * numerator / denominator)


def gate_failures(rows: list[dict[str, Any]]) -> list[str]:
    failures: list[str] = []

    def require_present(tool: str, task: str, numerator: int, denominator: int) -> None:
        bucket = [row for row in rows if row["tool"] == tool and row["task"] == task]
        if not bucket:
            failures.append(f"{tool}/{task}: expected rows, found 0")
            return
        present = sum(1 for row in bucket if row["expected_present"])
        required = gate_threshold(len(bucket), numerator, denominator)
        if present < required:
            failures.append(
                f"{tool}/{task}: present {present}/{len(bucket)} below "
                f"required {required}/{len(bucket)}"
            )

    require_present("miller.search.auto", "source_auto", 8, 9)
    require_present("miller.search.auto", "symbol", 9, 9)
    require_present("miller.search.auto", "file", 7, 9)
    return failures


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out-dir", default=str(ROOT / "docs/findings/benchmarks/2026-06-27-search-inspect"))
    parser.add_argument("--repos", default=",".join(repo.name for repo in REPOS))
    parser.add_argument("--skip-julie", action="store_true")
    parser.add_argument("--skip-miller-refresh", action="store_true")
    parser.add_argument("--miller-transport", choices=["mcp", "cli"], default="mcp")
    parser.add_argument("--gate", action="store_true", help="fail when Miller acceptance thresholds are missed")
    args = parser.parse_args()

    selected = {name.strip() for name in args.repos.split(",") if name.strip()}
    repos = [repo for repo in REPOS if repo.name in selected]
    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    rows: list[dict[str, Any]] = []
    prep_rows: list[dict[str, Any]] = []
    miller_mcp: McpProcess | None = None
    if args.miller_transport == "mcp":
        miller_mcp = McpProcess([str(MILLER), "serve"], timeout=60)

    try:
        for repo in repos:
            print(f"== {repo.name} ==", file=sys.stderr)
            if not args.skip_miller_refresh:
                prep_rows.append(miller_refresh(repo))

            for task_name, query, expected, mode in [
                ("symbol", repo.search_symbol, repo.search_file, "auto"),
                ("file", Path(repo.search_file).name, repo.search_file, "auto"),
                ("source_auto", repo.source_query, repo.source_expected_file, "auto"),
                ("source_best", repo.source_query, repo.source_expected_file, "source"),
            ]:
                json_output = task_name != "source_auto"
                row = (
                    miller_search_mcp(miller_mcp, repo, query, expected, mode, json_output)
                    if miller_mcp is not None
                    else miller_search_cli(repo, query, expected, mode, json_output)
                )
                row.update(
                    {
                        "repo": repo.name,
                        "repo_root": repo.root,
                        "language": repo.language,
                        "task": task_name,
                        "query": query,
                        "expected_file": expected,
                    }
                )
                rows.append(row)
            for depth in ["full", "overview"]:
                row = (
                    miller_inspect_mcp(miller_mcp, repo, depth)
                    if miller_mcp is not None
                    else miller_inspect_cli(repo, depth)
                )
                row.update(
                    {
                        "repo": repo.name,
                        "repo_root": repo.root,
                        "language": repo.language,
                        "task": "inspect_symbol",
                        "query": repo.inspect_target,
                        "expected_file": repo.inspect_expected_file,
                    }
                )
                rows.append(row)

            if args.skip_julie:
                continue

            julie = McpProcess([str(JULIE), "--workspace", repo.root], timeout=60)
            try:
                prep_ms, open_response = julie.call_tool(
                    "manage_workspace", {"operation": "open", "path": repo.root}, timeout=240
                )
                prep_rows.append(
                    {
                        "repo": repo.name,
                        "repo_root": repo.root,
                        "prep_tool": "julie.manage_workspace.open",
                        "ms": prep_ms,
                        "text": content_text(open_response).replace("\n", "\\n"),
                        "error": json.dumps(open_response.get("error", "")),
                    }
                )

                for task_name, query, expected in [
                    ("symbol", repo.search_symbol, repo.search_file),
                    ("file", Path(repo.search_file).name, repo.search_file),
                    ("source", repo.source_query, repo.source_expected_file),
                ]:
                    elapsed, response = julie.call_tool(
                        "fast_search", {"query": query, "limit": 5, "return_format": "full"}, timeout=120
                    )
                    text = content_text(response)
                    row = score_text(text, expected)
                    row.update(
                        {
                            "repo": repo.name,
                            "repo_root": repo.root,
                            "language": repo.language,
                            "task": task_name,
                            "query": query,
                            "expected_file": expected,
                            "tool": "julie.fast_search",
                            "ms": elapsed,
                            "exit_code": 0 if "error" not in response else 1,
                            "stderr": "",
                        }
                    )
                    rows.append(row)

                elapsed, response = julie.call_tool(
                    "deep_dive", {"symbol": repo.inspect_target, "depth": "overview"}, timeout=120
                )
                text = content_text(response)
                row = score_text(text, repo.inspect_expected_file)
                row.update(
                    {
                        "repo": repo.name,
                        "repo_root": repo.root,
                        "language": repo.language,
                        "task": "inspect_symbol",
                        "query": repo.inspect_target,
                        "expected_file": repo.inspect_expected_file,
                        "tool": "julie.deep_dive.overview",
                        "ms": elapsed,
                        "exit_code": 0 if "error" not in response else 1,
                        "stderr": "",
                    }
                )
                rows.append(row)
            finally:
                julie.close()
    finally:
        if miller_mcp is not None:
            miller_mcp.close()

    csv_path = out_dir / "results.csv"
    fieldnames = [
        "repo",
        "repo_root",
        "language",
        "task",
        "query",
        "expected_file",
        "tool",
        "ms",
        "exit_code",
        "empty",
        "expected_top",
        "expected_present",
        "first_path",
        "result_count",
        "output_chars",
        "score",
        "stderr",
    ]
    with csv_path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)

    prep_path = out_dir / "prep.csv"
    with prep_path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=["repo", "repo_root", "prep_tool", "ms", "text", "error"])
        writer.writeheader()
        writer.writerows(prep_rows)

    summary_path = out_dir / "summary.md"
    summary = [
        "# Julie vs Miller Search/Inspect Benchmark",
        "",
        f"Repos: {', '.join(repo.name for repo in repos)}",
        "",
        "Scoring: `top` means the first visible file/result was the expected file. `present` means the expected file appeared anywhere in the output. `empty` means the tool returned a no-result/not-found/index-required response.",
        "",
        summarize_by_tool(rows),
        "",
        "## Breakdown By Task",
        "",
        summarize_by_task(rows),
        "",
        f"Raw results: `{csv_path}`",
        f"Prep timings: `{prep_path}`",
    ]
    failures = gate_failures(rows) if args.gate else []
    if args.gate:
        summary.extend(
            [
                "",
                "## Gate",
                "",
                "Status: " + ("FAIL" if failures else "PASS"),
            ]
        )
        if failures:
            summary.extend(f"- {failure}" for failure in failures)
    if args.gate and failures:
        print("benchmark gate failed:", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
    summary_path.write_text("\n".join(summary) + "\n", encoding="utf-8")
    print(summary_path)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
