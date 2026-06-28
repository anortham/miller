"""Scoring helpers for benchmark output."""

from __future__ import annotations

import json
import re
from typing import Any


PATH_LINE_RE = re.compile(r"(?P<path>[A-Za-z0-9_@./+\-]+\.[A-Za-z0-9_+\-]+):(?P<line>\d+)")
REFERENCE_LINE_RE = re.compile(r"^\s+\S.*:\d+\s+")
SECTION_COUNT_RE = re.compile(r"^# (?P<section>impacted|likely tests) \((?P<count>\d+)\)", re.MULTILINE)
PATH_SCORING_MODES = {"path_present", "path_top", "path_any_present"}
WORKFLOW_SCORING_MODES = {"workflow_anchors", "trace_refs", "trace_path", "trace_bridge", "impact_targets"}
NOISE_DIAGNOSTIC_PHRASES = {
    "Multiple candidates": "trace_ambiguous",
    "No extracted refs": "trace_no_refs",
    "reference trace truncated": "trace_truncated",
}


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


def _string_list(value: Any) -> list[str]:
    if not isinstance(value, list):
        return []
    return [str(item) for item in value if isinstance(item, str) and item]


def _first_present_anchor(text: str, anchors: list[str]) -> str:
    positions = [(text.find(anchor), anchor) for anchor in anchors if anchor in text]
    positions = [(position, anchor) for position, anchor in positions if position >= 0]
    if not positions:
        return ""
    return min(positions, key=lambda item: item[0])[1]


def _workflow_base(text: str, expected: dict[str, Any], scoring: dict[str, Any], anchors: list[str]) -> dict[str, Any]:
    expected_path = str(expected.get("path") or "")
    anchor = expected.get("anchor")
    expected_present = bool(expected_path and expected_path in text)
    present_anchors = [value for value in anchors if value in text]
    return {
        "empty": False if expected_present or present_anchors else is_empty_text(text),
        "expected_present": expected_present,
        "expected_top": False,
        "first_path": first_path(text) or "",
        "output_chars": len(text),
        "anchor_present": bool(anchor and str(anchor) in text) if anchor else "",
        "expected_anchor_count": len(anchors),
        "expected_anchors_present": len(present_anchors),
        "first_useful_anchor": _first_present_anchor(text, anchors),
        "follow_up_hint_present": "",
        "readiness": str(scoring.get("readiness") or ""),
        "workflow_outcome": "ok",
        "diagnostics": [],
    }


def _workflow_diagnostics(text: str) -> list[dict[str, Any]]:
    diagnostics: list[dict[str, Any]] = []
    for phrase, diagnostic_type in NOISE_DIAGNOSTIC_PHRASES.items():
        if phrase in text:
            diagnostics.append({"type": diagnostic_type, "phrase": phrase})
    return diagnostics


def _reference_count(text: str) -> int:
    in_references = False
    count = 0
    for line in text.splitlines():
        stripped = line.strip()
        if stripped == "references:":
            in_references = True
            continue
        if not in_references:
            continue
        if stripped.startswith("reference trace truncated"):
            continue
        if REFERENCE_LINE_RE.match(line):
            count += 1
    return count


def _detect_trace_outcome(text: str) -> str:
    lower = text.lower()
    if "no path from" in lower:
        return "no-path"
    if "multiple candidates" in text:
        return "needs-search"
    if (
        "not supported" in lower
        or "unsupported" in lower
        or "provider-scoped" in lower
        or "not on a cross-language bridge" in lower
    ):
        return "unsupported"
    return "ok"


def _section_count(text: str, section: str) -> int:
    for match in SECTION_COUNT_RE.finditer(text):
        if match.group("section") == section:
            return int(match.group("count"))
    return 0


def score_workflow_anchors(text: str, expected: dict[str, Any], scoring: dict[str, Any]) -> dict[str, Any]:
    anchors = _string_list(scoring.get("required_anchors"))
    scored = _workflow_base(text, expected, scoring, anchors)
    follow_up_hint = str(scoring.get("follow_up_hint") or "")
    follow_up_present = bool(follow_up_hint and follow_up_hint.lower() in text.lower())
    scored["follow_up_hint_present"] = follow_up_present if follow_up_hint else ""
    anchors_pass = scored["expected_anchors_present"] == scored["expected_anchor_count"]
    scored["scoring_pass"] = bool(anchors_pass and (follow_up_present if follow_up_hint else True))
    scored["score"] = int(scored["expected_anchors_present"]) + (1 if follow_up_present else 0)
    scored["scoring_mode"] = "workflow_anchors"
    return scored


def score_trace_refs(text: str, expected: dict[str, Any], scoring: dict[str, Any]) -> dict[str, Any]:
    anchors = _string_list(scoring.get("required_anchors"))
    scored = _workflow_base(text, expected, scoring, anchors)
    diagnostics = _workflow_diagnostics(text)
    reference_count = _reference_count(text)
    definition_anchor = str(scoring.get("definition_anchor") or "definition:")
    definition_present = bool(definition_anchor and definition_anchor in text)
    min_references = int(scoring.get("min_references") or 0)
    expected_outcome = str(scoring.get("expected_outcome") or "ok")
    outcome = _detect_trace_outcome(text)
    anchors_pass = scored["expected_anchors_present"] == scored["expected_anchor_count"]
    outcome_pass = outcome == expected_outcome if expected_outcome != "ok" else outcome == "ok"
    scored.update(
        {
            "definition_present": definition_present,
            "reference_count": reference_count,
            "noise_diagnostic_count": len(diagnostics),
            "workflow_outcome": outcome if outcome != "ok" else ("needs-search" if reference_count == 0 else "ok"),
            "diagnostics": diagnostics,
            "scoring_pass": bool(definition_present and reference_count >= min_references and anchors_pass and outcome_pass),
            "score": reference_count + int(scored["expected_anchors_present"]),
            "scoring_mode": "trace_refs",
        }
    )
    return scored


def score_trace_path(text: str, expected: dict[str, Any], scoring: dict[str, Any], mode: str) -> dict[str, Any]:
    anchors = _string_list(scoring.get("required_anchors"))
    scored = _workflow_base(text, expected, scoring, anchors)
    outcome = _detect_trace_outcome(text)
    expected_outcome = str(scoring.get("expected_outcome") or "ok")
    diagnostics = _workflow_diagnostics(text)
    if outcome == "no-path":
        diagnostics.append({"type": "trace_no_path"})
    elif outcome == "unsupported":
        diagnostics.append({"type": "trace_unsupported"})
    anchors_pass = scored["expected_anchors_present"] == scored["expected_anchor_count"]
    outcome_pass = outcome == expected_outcome if expected_outcome != "ok" else outcome == "ok"
    scored.update(
        {
            "workflow_outcome": outcome,
            "diagnostics": diagnostics,
            "scoring_pass": bool(outcome_pass and anchors_pass),
            "score": int(scored["expected_anchors_present"]) + (1 if outcome_pass else 0),
            "scoring_mode": mode,
        }
    )
    return scored


def score_impact_targets(text: str, expected: dict[str, Any], scoring: dict[str, Any]) -> dict[str, Any]:
    symbols = _string_list(scoring.get("required_symbols"))
    tests = _string_list(scoring.get("required_tests"))
    anchors = symbols + tests
    scored = _workflow_base(text, expected, scoring, anchors)
    symbols_present = sum(1 for symbol in symbols if symbol in text)
    tests_present = sum(1 for test in tests if test in text)
    scored.update(
        {
            "impacted_symbols_present": symbols_present,
            "likely_tests_present": tests_present,
            "impacted_symbol_count": _section_count(text, "impacted"),
            "likely_test_count": _section_count(text, "likely tests"),
            "scoring_pass": bool(symbols_present == len(symbols) and tests_present == len(tests)),
            "score": symbols_present + tests_present,
            "scoring_mode": "impact_targets",
        }
    )
    return scored


def score_manifest_path(
    text: str,
    expected: dict[str, Any],
    scoring: dict[str, Any],
    *,
    parse_json: bool = False,
) -> dict[str, Any]:
    mode = scoring.get("mode")
    if mode not in PATH_SCORING_MODES | WORKFLOW_SCORING_MODES:
        raise ValueError(f"unsupported scoring mode: {mode!r}")
    if mode == "workflow_anchors":
        return score_workflow_anchors(text, expected, scoring)
    if mode == "trace_refs":
        return score_trace_refs(text, expected, scoring)
    if mode in {"trace_path", "trace_bridge"}:
        return score_trace_path(text, expected, scoring, str(mode))
    if mode == "impact_targets":
        return score_impact_targets(text, expected, scoring)

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
