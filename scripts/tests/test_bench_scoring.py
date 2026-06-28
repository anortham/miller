import sys
import unittest
import importlib.util
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from benchlib.scoring import _detect_trace_outcome


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


if __name__ == "__main__":
    unittest.main()
