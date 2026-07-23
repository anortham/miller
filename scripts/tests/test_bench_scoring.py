import sys
import unittest
import importlib.util
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from benchlib.scoring import _detect_trace_outcome, _reference_count, score_workflow_anchors


def load_bench_foundation_matrix():
    path = Path(__file__).resolve().parents[1] / "bench-foundation-matrix.py"
    spec = importlib.util.spec_from_file_location("bench_foundation_matrix", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class BenchScoringTests(unittest.TestCase):
    def test_detect_trace_outcome_handles_capitalized_ambiguous_candidates(self) -> None:
        text = "Multiple candidates - pass scope=<file> to disambiguate:"

        self.assertEqual("needs-search", _detect_trace_outcome(text))

    def test_gate_accepts_scored_no_path_workflow_without_expected_path(self) -> None:
        runner = load_bench_foundation_matrix()
        result = {
            "row_id": "miller.trace.path.no-path",
            "tool": "miller.trace",
            "hard_gate": True,
            "diagnostics": [{"type": "trace_no_path"}],
            "empty": False,
            "expected_present": False,
            "scoring_pass": True,
            "scoring_mode": "trace_path",
            "anchor_present": True,
            "workflow_outcome": "no-path",
        }

        self.assertEqual([], runner.gate_failures([result]))

    def test_workflow_anchors_fail_when_output_exceeds_limit(self) -> None:
        scored = score_workflow_anchors(
            "Match proof:\n- disk_verified: true\n" + ("x" * 40),
            {"path": "fixture.md", "anchor": "disk_verified: true"},
            {
                "mode": "workflow_anchors",
                "readiness": "edit-ready",
                "required_anchors": ["Match proof:", "disk_verified: true"],
                "max_output_chars": 20,
            },
        )

        self.assertFalse(scored["scoring_pass"])
        self.assertFalse(scored["output_chars_within_limit"])
        self.assertEqual(20, scored["max_output_chars"])
        self.assertIn("output_chars_exceeded", {item["type"] for item in scored["diagnostics"]})

    def test_reference_count_accepts_exact_and_fallback_sections(self) -> None:
        text = """# trace refs Target (2 reference(s), exact=1, fallback=1)
exact:
  src/Exact.cs:10  call  in=Caller  [exact source=identifier_direct confidence=1.00]
fallback (unresolved):
  src/Fallback.cs:20  call  [fallback source=name_fallback confidence=0.50]
next: impact target="Target" — before editing
"""

        self.assertEqual(2, _reference_count(text))


if __name__ == "__main__":
    unittest.main()
