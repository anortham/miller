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
CONTRACT_SCORING_MODES = {"contract_json", "contract_jsonl"}
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


def _field_present(data: Any, path: str) -> bool:
    current = data
    for part in path.split("."):
        if not isinstance(current, dict) or part not in current:
            return False
        current = current[part]
    return True


def _field_counts(data: Any, fields: list[str]) -> tuple[int, list[str]]:
    missing = [field for field in fields if not _field_present(data, field)]
    return len(fields) - len(missing), missing


def _rows_at_path(data: Any, path: str) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    diagnostics: list[dict[str, Any]] = []
    current = data
    if path:
        for part in path.split("."):
            if not isinstance(current, dict) or part not in current:
                diagnostics.append({"type": "missing_rows_path", "path": path})
                return [], diagnostics
            current = current[part]

    if not isinstance(current, list):
        if path:
            diagnostics.append({"type": "invalid_rows_path", "path": path, "expected": "list"})
        return [], diagnostics

    rows = [row for row in current if isinstance(row, dict)]
    if len(rows) != len(current):
        diagnostics.append(
            {
                "type": "parse_warning",
                "format": "json",
                "message": "ignored non-object rows",
                "ignored_rows": len(current) - len(rows),
            }
        )
    return rows, diagnostics


def _sample_limit(scoring: dict[str, Any]) -> int:
    value = scoring.get("sample_limit")
    if isinstance(value, int) and value > 0:
        return value
    return 20


def _min_rows(scoring: dict[str, Any], row_fields: list[str]) -> int:
    value = scoring.get("min_rows")
    if isinstance(value, int) and value >= 0:
        return value
    return 1 if row_fields else 0


def _score_row_fields(
    rows: list[dict[str, Any]],
    fields: list[str],
    *,
    min_rows: int,
) -> tuple[int, int, list[dict[str, Any]]]:
    diagnostics: list[dict[str, Any]] = []
    if len(rows) < min_rows:
        diagnostics.append({"type": "missing_rows", "required": min_rows, "actual": len(rows)})

    total = len(rows) * len(fields)
    present = 0
    for index, row in enumerate(rows):
        _, missing = _field_counts(row, fields)
        present += len(fields) - len(missing)
        if missing:
            diagnostics.append({"type": "missing_required_row_fields", "row": index, "fields": missing})
    return present, total, diagnostics


def _add_command_variants(commands: set[str], command: str) -> None:
    command = command.strip()
    if not command:
        return
    commands.add(command)
    if command.startswith("miller "):
        commands.add(command.removeprefix("miller "))
    else:
        commands.add(f"miller {command}")


def _advertised_commands(capabilities: Any) -> set[str]:
    if not isinstance(capabilities, dict):
        return set()

    commands: set[str] = set()
    for command in capabilities.get("json_commands", []):
        if isinstance(command, str):
            _add_command_variants(commands, command)
    for contract in capabilities.get("json_contracts", []):
        if isinstance(contract, dict) and isinstance(contract.get("command"), str):
            _add_command_variants(commands, contract["command"])
    for export in capabilities.get("supported_export_formats", []):
        if isinstance(export, dict) and isinstance(export.get("command"), str):
            _add_command_variants(commands, export["command"])
    return commands


def _score_advertised_commands(capabilities: Any, commands: list[str]) -> tuple[int, list[str]]:
    advertised = _advertised_commands(capabilities)
    missing = [command for command in commands if command not in advertised]
    return len(commands) - len(missing), missing


def parse_jsonl_objects(text: str, *, sample_limit: int | None = None) -> tuple[list[dict[str, Any]], int, list[dict[str, Any]]]:
    """Parse non-empty JSONL object rows, optionally bounded to a sample."""
    diagnostics: list[dict[str, Any]] = []
    lines = [line for line in text.splitlines() if line.strip()]
    sample = lines[:sample_limit] if sample_limit is not None else lines
    rows: list[dict[str, Any]] = []
    for index, line in enumerate(sample):
        try:
            parsed = json.loads(line)
        except json.JSONDecodeError as exc:
            diagnostics.append(
                {
                    "type": "parse_failure",
                    "format": "jsonl",
                    "line_index": index,
                    "message": str(exc),
                    "line": exc.lineno,
                    "column": exc.colno,
                }
            )
            continue
        if not isinstance(parsed, dict):
            diagnostics.append({"type": "parse_failure", "format": "jsonl", "line_index": index, "message": "expected object"})
            continue
        rows.append(parsed)
    return rows, len(lines), diagnostics


def count_present_fields(data: Any, fields: list[str]) -> tuple[int, list[str]]:
    return _field_counts(data, fields)


def _contract_result(
    *,
    mode: str,
    output_chars: int,
    empty: bool,
    parse_ok: bool,
    outcome: str,
    scoring_pass: bool,
    diagnostics: list[dict[str, Any]],
    required_fields_present: int = 0,
    required_fields_total: int = 0,
    required_row_fields_present: int = 0,
    required_row_fields_total: int = 0,
    advertised_commands_present: int = 0,
    advertised_commands_total: int = 0,
    result_count: int | str = "",
    sampled_jsonl_rows: int = 0,
    jsonl_non_empty_lines: int = 0,
) -> dict[str, Any]:
    return {
        "empty": empty,
        "expected_present": parse_ok,
        "expected_top": False,
        "first_path": "",
        "output_chars": output_chars,
        "result_count": result_count,
        "anchor_present": "",
        "score": int(scoring_pass),
        "scoring_pass": scoring_pass,
        "scoring_mode": mode,
        "contract_parse_ok": parse_ok,
        "required_fields_present": required_fields_present,
        "required_fields_total": required_fields_total,
        "required_row_fields_present": required_row_fields_present,
        "required_row_fields_total": required_row_fields_total,
        "advertised_commands_present": advertised_commands_present,
        "advertised_commands_total": advertised_commands_total,
        "sampled_jsonl_rows": sampled_jsonl_rows,
        "jsonl_non_empty_lines": jsonl_non_empty_lines,
        "contract_outcome": outcome,
        "diagnostics": diagnostics,
    }


def score_contract_json(
    text: str,
    expected: dict[str, Any],
    scoring: dict[str, Any],
    capabilities: dict[str, Any] | None = None,
) -> dict[str, Any]:
    diagnostics: list[dict[str, Any]] = []
    try:
        parsed = json.loads(text)
    except json.JSONDecodeError as exc:
        return _contract_result(
            mode="contract_json",
            output_chars=len(text),
            empty=not bool(text.strip()),
            parse_ok=False,
            outcome="malformed_json",
            scoring_pass=False,
            diagnostics=[
                {
                    "type": "parse_failure",
                    "format": "json",
                    "message": str(exc),
                    "line": exc.lineno,
                    "column": exc.colno,
                }
            ],
        )

    required_fields = _string_list(scoring.get("required_fields"))
    required_fields_present, missing_fields = _field_counts(parsed, required_fields)
    if missing_fields:
        diagnostics.append({"type": "missing_required_fields", "fields": missing_fields})

    rows_path = str(scoring.get("rows_path") or "")
    rows, row_diagnostics = _rows_at_path(parsed, rows_path) if rows_path or isinstance(parsed, list) else ([], [])
    diagnostics.extend(row_diagnostics)
    sample_rows = rows[: _sample_limit(scoring)]
    row_fields = _string_list(scoring.get("required_row_fields"))
    row_fields_present, row_fields_total, row_field_diagnostics = _score_row_fields(
        sample_rows,
        row_fields,
        min_rows=_min_rows(scoring, row_fields),
    )
    diagnostics.extend(row_field_diagnostics)

    advertised_source = capabilities if capabilities is not None else parsed
    advertised_commands = _string_list(scoring.get("advertises_commands"))
    advertised_present, missing_commands = _score_advertised_commands(advertised_source, advertised_commands)
    if missing_commands:
        diagnostics.append({"type": "missing_capability", "commands": missing_commands})

    if missing_fields:
        outcome = "missing_required_fields"
    elif row_field_diagnostics:
        outcome = "missing_required_row_fields"
    elif missing_commands:
        outcome = "missing_capability"
    else:
        outcome = "ok"

    return _contract_result(
        mode="contract_json",
        output_chars=len(text),
        empty=False,
        parse_ok=True,
        outcome=outcome,
        scoring_pass=outcome == "ok",
        diagnostics=diagnostics,
        required_fields_present=required_fields_present,
        required_fields_total=len(required_fields),
        required_row_fields_present=row_fields_present,
        required_row_fields_total=row_fields_total,
        advertised_commands_present=advertised_present,
        advertised_commands_total=len(advertised_commands),
        result_count=len(rows) if rows_path or isinstance(parsed, list) else "",
    )


def score_contract_jsonl(
    text: str,
    expected: dict[str, Any],
    scoring: dict[str, Any],
    capabilities: dict[str, Any] | None = None,
) -> dict[str, Any]:
    del expected
    diagnostics: list[dict[str, Any]] = []
    lines = [line for line in text.splitlines() if line.strip()]
    sample_lines = lines[: _sample_limit(scoring)]
    empty_allowed = bool(scoring.get("empty_allowed"))

    if not lines:
        outcome = "empty_allowed" if empty_allowed else "empty"
        return _contract_result(
            mode="contract_jsonl",
            output_chars=len(text),
            empty=True,
            parse_ok=True,
            outcome=outcome,
            scoring_pass=empty_allowed,
            diagnostics=[] if empty_allowed else [{"type": "empty_jsonl"}],
            jsonl_non_empty_lines=0,
        )

    rows: list[dict[str, Any]] = []
    for index, line in enumerate(sample_lines):
        try:
            parsed = json.loads(line)
        except json.JSONDecodeError as exc:
            diagnostics.append(
                {
                    "type": "parse_failure",
                    "format": "jsonl",
                    "line_index": index,
                    "message": str(exc),
                    "line": exc.lineno,
                    "column": exc.colno,
                }
            )
            continue
        if not isinstance(parsed, dict):
            diagnostics.append({"type": "parse_failure", "format": "jsonl", "line_index": index, "message": "expected object"})
            continue
        rows.append(parsed)

    if any(diagnostic.get("type") == "parse_failure" for diagnostic in diagnostics):
        return _contract_result(
            mode="contract_jsonl",
            output_chars=len(text),
            empty=False,
            parse_ok=False,
            outcome="malformed_jsonl",
            scoring_pass=False,
            diagnostics=diagnostics,
            sampled_jsonl_rows=len(rows),
            jsonl_non_empty_lines=len(lines),
        )

    required_fields = _string_list(scoring.get("required_fields"))
    fields_present, fields_total, field_diagnostics = _score_row_fields(
        rows,
        required_fields,
        min_rows=_min_rows(scoring, required_fields),
    )
    diagnostics.extend(field_diagnostics)

    advertised_commands = _string_list(scoring.get("advertises_commands"))
    advertised_present, missing_commands = _score_advertised_commands(capabilities, advertised_commands)
    if missing_commands:
        diagnostics.append({"type": "missing_capability", "commands": missing_commands})

    if field_diagnostics:
        outcome = "missing_required_fields"
    elif missing_commands:
        outcome = "missing_capability"
    else:
        outcome = "ok"

    return _contract_result(
        mode="contract_jsonl",
        output_chars=len(text),
        empty=False,
        parse_ok=True,
        outcome=outcome,
        scoring_pass=outcome == "ok",
        diagnostics=diagnostics,
        required_fields_present=fields_present,
        required_fields_total=fields_total,
        advertised_commands_present=advertised_present,
        advertised_commands_total=len(advertised_commands),
        result_count=len(rows),
        sampled_jsonl_rows=len(rows),
        jsonl_non_empty_lines=len(lines),
    )


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
    if "multiple candidates" in lower:
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
    max_output_chars = scoring.get("max_output_chars")
    output_chars_within_limit = not isinstance(max_output_chars, int) or len(text) <= max_output_chars
    if isinstance(max_output_chars, int):
        scored["max_output_chars"] = max_output_chars
        scored["output_chars_within_limit"] = output_chars_within_limit
        if not output_chars_within_limit:
            scored["diagnostics"].append(
                {
                    "type": "output_chars_exceeded",
                    "output_chars": len(text),
                    "max_output_chars": max_output_chars,
                }
            )
    scored["follow_up_hint_present"] = follow_up_present if follow_up_hint else ""
    anchors_pass = scored["expected_anchors_present"] == scored["expected_anchor_count"]
    scored["scoring_pass"] = bool(
        anchors_pass and (follow_up_present if follow_up_hint else True) and output_chars_within_limit
    )
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
    capabilities: dict[str, Any] | None = None,
) -> dict[str, Any]:
    mode = scoring.get("mode")
    if mode not in PATH_SCORING_MODES | WORKFLOW_SCORING_MODES | CONTRACT_SCORING_MODES:
        raise ValueError(f"unsupported scoring mode: {mode!r}")
    json_parse_diagnostics: list[dict[str, Any]] = []
    if parse_json and mode in WORKFLOW_SCORING_MODES:
        try:
            json.loads(text)
        except json.JSONDecodeError as exc:
            json_parse_diagnostics.append(
                {
                    "type": "parse_failure",
                    "format": "json",
                    "message": str(exc),
                    "line": exc.lineno,
                    "column": exc.colno,
                }
            )
    if mode == "contract_json":
        return score_contract_json(text, expected, scoring, capabilities=capabilities)
    if mode == "contract_jsonl":
        return score_contract_jsonl(text, expected, scoring, capabilities=capabilities)
    if mode == "workflow_anchors":
        scored = score_workflow_anchors(text, expected, scoring)
        if json_parse_diagnostics:
            scored["diagnostics"].extend(json_parse_diagnostics)
            scored["scoring_pass"] = False
        return scored
    if mode == "trace_refs":
        scored = score_trace_refs(text, expected, scoring)
        if json_parse_diagnostics:
            scored["diagnostics"].extend(json_parse_diagnostics)
            scored["scoring_pass"] = False
        return scored
    if mode in {"trace_path", "trace_bridge"}:
        scored = score_trace_path(text, expected, scoring, str(mode))
        if json_parse_diagnostics:
            scored["diagnostics"].extend(json_parse_diagnostics)
            scored["scoring_pass"] = False
        return scored
    if mode == "impact_targets":
        scored = score_impact_targets(text, expected, scoring)
        if json_parse_diagnostics:
            scored["diagnostics"].extend(json_parse_diagnostics)
            scored["scoring_pass"] = False
        return scored

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
