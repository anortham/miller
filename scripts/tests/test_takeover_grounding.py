import copy
import json
import tempfile
import unittest
from pathlib import Path

from benchlib.agent_contract import load_task_manifest, verify_answer


SYMBOL_ID = "python:src/factory.py:create_widget"


def _manifest() -> dict:
    return {
        "contract_id": "takeover-evaluation-v1",
        "schema_version": 1,
        "tasks": [
            {
                "task_id": "dev-001",
                "repo_id": "fixture",
                "snapshot_id": "snapshot-001",
                "language": "python",
                "workflow_class": "context_assembly",
                "evidence_critical": False,
                "prompt": "Find the exact create_widget definition.",
                "fact_predicates": [],
                "path_cited": [],
                "symbol_cited": [],
                "evidence_anchors": [
                    {
                        "anchor_id": "anchor-001",
                        "path": "src/factory.py",
                        "symbol": "create_widget",
                        "line_start": 1,
                        "line_end": 1,
                        "relevance_grade": 3,
                    }
                ],
                "forbidden_claims": [],
                "capabilities": ["exact_symbol_lookup"],
                "expected_outcome": "success",
                "acceptable_actions": [
                    {
                        "action_id": "action-001",
                        "kind": "inspect_symbol",
                        "target": {"symbol_id": SYMBOL_ID},
                        "requirement_group": "resolve",
                        "evidence_anchor_ids": ["anchor-001"],
                    }
                ],
                "forbidden_actions": [],
                "reference_sites": [],
                "uncertainty_expectation": "must_resolve",
            }
        ],
    }


def _load(root: Path, manifest: dict):
    path = root / "tasks.json"
    path.write_text(json.dumps(manifest), encoding="utf-8")
    return load_task_manifest(path)[0]


class TakeoverGroundingTests(unittest.TestCase):
    def test_success_requires_at_least_one_graded_evidence_anchor(self) -> None:
        manifest = _manifest()
        manifest["tasks"][0]["evidence_anchors"] = []
        manifest["tasks"][0]["acceptable_actions"][0].pop("evidence_anchor_ids")

        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                _load(Path(directory), manifest)

    def test_must_resolve_rejects_ungrounded_path_alternative(self) -> None:
        manifest = _manifest()
        manifest["tasks"][0]["acceptable_actions"].append(
            {
                "action_id": "action-002",
                "kind": "inspect_file",
                "target": {"path": "src/factory.py"},
                "requirement_group": "resolve",
            }
        )

        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, "must_resolve.*exact identity"):
                _load(Path(directory), manifest)

    def test_must_resolve_accepts_path_tied_to_exact_path_anchor(self) -> None:
        manifest = _manifest()
        manifest["tasks"][0]["acceptable_actions"] = [
            {
                "action_id": "action-001",
                "kind": "inspect_file",
                "target": {"path": "src/factory.py"},
                "requirement_group": "resolve",
                "evidence_anchor_ids": ["anchor-001"],
            }
        ]
        answer = {
            "contract_id": "takeover-evaluation-v1",
            "status": "answered",
            "answer": "src/factory.py contains the exact definition.",
            "evidence": [
                {
                    "path": "src/factory.py",
                    "symbol": "create_widget",
                    "line": 1,
                    "claim": "This path contains create_widget.",
                }
            ],
            "actions": [
                {
                    "kind": "inspect_file",
                    "target": {"path": "src/factory.py"},
                }
            ],
        }

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "src" / "factory.py"
            source.parent.mkdir()
            source.write_text("def create_widget(): pass\n", encoding="utf-8")
            task = _load(root, manifest)
            result = verify_answer(task, answer, root)

        self.assertTrue(result.passed)

    def test_exact_symbol_obligation_requires_grounded_evidence_and_action(self) -> None:
        manifest = _manifest()
        answer = {
            "contract_id": "takeover-evaluation-v1",
            "status": "answered",
            "answer": "create_widget is the exact definition.",
            "evidence": [
                {
                    "path": "src/factory.py",
                    "symbol": "create_widget",
                    "line": 1,
                    "claim": "This is the exact create_widget definition.",
                }
            ],
            "actions": [
                {
                    "kind": "inspect_symbol",
                    "target": {"symbol_id": SYMBOL_ID},
                }
            ],
        }

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "src" / "factory.py"
            source.parent.mkdir()
            source.write_text("def create_widget(): pass\n", encoding="utf-8")
            task = _load(root, manifest)

            passed = verify_answer(task, answer, root)
            missing_evidence = copy.deepcopy(answer)
            missing_evidence["evidence"] = []
            failed = verify_answer(task, missing_evidence, root)
            missing_action = copy.deepcopy(answer)
            missing_action["actions"] = []
            action_failed = verify_answer(task, missing_action, root)

        self.assertTrue(passed.passed)
        self.assertEqual("success", passed.observed_outcome)
        self.assertFalse(failed.passed)
        self.assertEqual("wrong_answer", failed.observed_outcome)
        self.assertIn("missing requirement group resolve", "\n".join(failed.failures))
        self.assertFalse(action_failed.passed)
        self.assertIn(
            "must_resolve requires a grounded exact identity action",
            "\n".join(action_failed.failures),
        )


if __name__ == "__main__":
    unittest.main()
