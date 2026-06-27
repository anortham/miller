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
