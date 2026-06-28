"""Scoring helpers for benchmark output."""

from __future__ import annotations

import json
import re
from typing import Any


PATH_LINE_RE = re.compile(r"(?P<path>[A-Za-z0-9_@./+\-]+\.[A-Za-z0-9_+\-]+):(?P<line>\d+)")


def is_empty_text(text: str) -> bool:
    lower = text.lower()
    return (
        "no results" in lower
        or "not found" in lower
        or "requires a tantivy index" in lower
        or "no indexed symbols" in lower
    )


def first_path(text: str) -> str | None:
    for match in PATH_LINE_RE.finditer(text):
        path = match.group("path")
        if "/" in path or path.startswith("src") or path.startswith("lib") or path.startswith("test"):
            return path
    return None


def score_text(text: str, expected_file: str) -> dict[str, Any]:
    present = expected_file in text
    first = first_path(text)
    top = first == expected_file
    return {
        "empty": False if present else is_empty_text(text),
        "expected_present": present,
        "expected_top": top,
        "first_path": first or "",
        "output_chars": len(text),
        "score": 2 if top else 1 if present else 0,
    }


def score_text_paths(text: str, expected_files: list[str], *, require_top: bool = False) -> dict[str, Any]:
    present = any(expected_file in text for expected_file in expected_files)
    first = first_path(text)
    top = first in expected_files
    return {
        "empty": False if present else is_empty_text(text),
        "expected_present": present,
        "expected_top": top,
        "scoring_pass": top if require_top else present,
        "first_path": first or "",
        "output_chars": len(text),
        "score": 2 if top else 1 if present else 0,
    }


def score_miller_search_json(text: str, expected_file: str) -> dict[str, Any]:
    try:
        rows = json.loads(text)
    except json.JSONDecodeError:
        scored = score_text(text, expected_file)
        scored["result_count"] = ""
        return scored
    if isinstance(rows, dict) and "results" in rows:
        rows = rows["results"]
    if not isinstance(rows, list):
        rows = []

    def row_path(row: dict[str, Any]) -> str:
        return str(row.get("file") or row.get("path") or row.get("display_path") or "")

    first = row_path(rows[0]) if rows and isinstance(rows[0], dict) else ""
    present = any(row_path(row) == expected_file for row in rows if isinstance(row, dict))
    top = first == expected_file
    return {
        "empty": len(rows) == 0,
        "expected_present": present,
        "expected_top": top,
        "first_path": first,
        "result_count": len(rows),
        "output_chars": len(text),
        "score": 2 if top else 1 if present else 0,
    }


def json_row_path(row: dict[str, Any]) -> str:
    return str(row.get("file") or row.get("path") or row.get("display_path") or "")


def json_result_rows(text: str) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    diagnostics: list[dict[str, Any]] = []
    try:
        parsed = json.loads(text)
    except json.JSONDecodeError as exc:
        diagnostics.append(
            {
                "type": "parse_failure",
                "format": "json",
                "message": str(exc),
                "line": exc.lineno,
                "column": exc.colno,
            }
        )
        return [], diagnostics

    rows: Any = parsed
    if isinstance(parsed, dict) and "results" in parsed:
        rows = parsed["results"]
    if not isinstance(rows, list):
        diagnostics.append(
            {
                "type": "parse_failure",
                "format": "json",
                "message": "expected a JSON list or an object with a results list",
            }
        )
        return [], diagnostics

    dict_rows = [row for row in rows if isinstance(row, dict)]
    if len(dict_rows) != len(rows):
        diagnostics.append(
            {
                "type": "parse_warning",
                "format": "json",
                "message": "ignored non-object result rows",
                "ignored_rows": len(rows) - len(dict_rows),
            }
        )
    return dict_rows, diagnostics


def expected_paths(expected: dict[str, Any], mode: str) -> list[str]:
    if mode == "path_any_present":
        paths = expected.get("paths")
        if not isinstance(paths, list) or not all(isinstance(path, str) and path for path in paths):
            raise ValueError("expected.paths is required for path_any_present scoring")
        return [str(path) for path in paths]

    expected_path = str(expected.get("path") or "")
    if not expected_path:
        raise ValueError(f"expected.path is required for {mode} scoring")
    return [expected_path]


def score_json_rows(
    rows: list[dict[str, Any]],
    expected_files: list[str],
    *,
    require_top: bool = False,
    output_chars: int,
) -> dict[str, Any]:
    first = json_row_path(rows[0]) if rows else ""
    present = any(json_row_path(row) in expected_files for row in rows)
    top = first in expected_files
    return {
        "empty": len(rows) == 0,
        "expected_present": present,
        "expected_top": top,
        "scoring_pass": top if require_top else present,
        "first_path": first,
        "result_count": len(rows),
        "output_chars": output_chars,
        "score": 2 if top else 1 if present else 0,
    }


def score_manifest_path(
    text: str,
    expected: dict[str, Any],
    scoring: dict[str, Any],
    *,
    parse_json: bool = False,
) -> dict[str, Any]:
    mode = scoring.get("mode")
    if mode not in {"path_present", "path_top", "path_any_present"}:
        raise ValueError(f"unsupported scoring mode: {mode!r}")

    expected_files = expected_paths(expected, str(mode))
    require_top = mode == "path_top"

    diagnostics: list[dict[str, Any]] = []
    if parse_json:
        rows, diagnostics = json_result_rows(text)
        scored = score_json_rows(rows, expected_files, require_top=require_top, output_chars=len(text))
        if diagnostics and not rows:
            fallback = score_text_paths(text, expected_files, require_top=require_top)
            scored.update(
                {
                    "empty": fallback["empty"],
                    "expected_present": fallback["expected_present"],
                    "expected_top": fallback["expected_top"],
                    "scoring_pass": fallback["scoring_pass"],
                    "first_path": fallback["first_path"],
                    "score": fallback["score"],
                }
            )
            scored["result_count"] = ""
    else:
        scored = score_text_paths(text, expected_files, require_top=require_top)
        scored["result_count"] = ""

    anchor = expected.get("anchor")
    scored["anchor_present"] = bool(anchor and str(anchor) in text) if anchor else ""
    scored["scoring_mode"] = mode
    scored["diagnostics"] = diagnostics
    return scored
