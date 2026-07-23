import unittest
import sys
from pathlib import Path

SCRIPTS_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS_ROOT))
from benchlib import reporting


TAKEOVER_CAPABILITIES = [
    "discovery",
    "exact_symbol_lookup",
    "homonym_disambiguation",
    "context_orientation",
    "callers",
    "callees",
    "call_path",
    "impact_tests",
    "edit",
    "rename",
    "logs",
    "patterns",
    "workspace_recovery",
]


def _completion() -> dict:
    return {
        "both_correct": 6,
        "baseline_only": 0,
        "candidate_only": 0,
        "neither_correct": 0,
    }


def _outcome_counts() -> dict:
    arm = {
        "success": 6,
        "empty": 0,
        "refusal": 0,
        "hard_error": 0,
        "wrong_answer": 0,
    }
    return {"baseline": dict(arm), "candidate": dict(arm)}


def _subgroup() -> dict:
    return {
        "task_count": 6,
        "completion": _completion(),
        "outcome_counts": _outcome_counts(),
        "baseline_wrong_action_task_count": 0,
        "candidate_wrong_action_task_count": 0,
    }


def _aggregate() -> dict:
    return {
        "contract_id": "takeover-evaluation-v1",
        "schema_version": 1,
        "decision_scope": "subset",
        "decision_verdict": "not_decisional",
        "action_verdict": "pass",
        "task_count": 6,
        "completion": _completion(),
        "outcome_counts": _outcome_counts(),
        "relevance": {
            "verdict": "pass",
            "task_count": 6,
            "baseline": {
                "recall_at_6": 1.0,
                "ndcg_at_6": 1.0,
                "mrr": 1.0,
                "top_1": 1.0,
            },
            "candidate": {
                "recall_at_6": 1.0,
                "ndcg_at_6": 1.0,
                "mrr": 1.0,
                "top_1": 1.0,
            },
        },
        "correctness": {
            "verdict": "pass",
            "baseline_correct_count": 6,
            "candidate_correct_count": 6,
            "critical_loss_count": 0,
            "baseline_wrong_action_task_count": 0,
            "candidate_wrong_action_task_count": 0,
            "baseline_wrong_action_rate": 0.0,
            "candidate_wrong_action_rate": 0.0,
        },
        "efficiency": {
            "verdict": "pass",
            "measurable": True,
            "both_correct_task_count": 6,
            "token_route_passed": True,
            "call_route_passed": False,
            "wall_guard_passed": True,
        },
        "baseline": {
            "median_tool_output_tokens": 100.0,
            "median_tool_calls": 3.0,
            "p75_duration_ms": 100.0,
        },
        "candidate": {
            "median_tool_output_tokens": 80.0,
            "median_tool_calls": 3.0,
            "p75_duration_ms": 100.0,
        },
        "failure_counts": {"baseline": {}, "candidate": {}},
        "by_workflow": {"concept_search": _subgroup()},
        "by_capability": {"discovery": _subgroup()},
        "by_repo": {},
        "by_language": {},
    }


def _identity() -> dict:
    return {
        "contract_id": "takeover-evaluation-v1",
        "schema_version": 1,
        "corpus_role": "calibration",
        "decision_scope": "subset",
        "parent_manifest_sha256": "a" * 64,
        "snapshot_manifest_sha256": "b" * 64,
        "runtime_identity_sha256": "c" * 64,
        "selection_sha256": "d" * 64,
        "selected_capability_ids": ["discovery"],
        "selected_task_count": 6,
    }


class ReportingTests(unittest.TestCase):
    def test_safe_projection_is_allowlisted_and_subset_is_never_decisional(self):
        self.assertTrue(hasattr(reporting, "project_safe_aggregate"))
        value = reporting.project_safe_aggregate(
            _aggregate(),
            _identity(),
            unresolved_void_count=0,
            private_evidence_sha256={"run_artifacts": "e" * 64},
        )

        self.assertEqual("not_decisional", value["decision_verdict"])
        self.assertEqual("subset", value["decision_scope"])
        self.assertNotIn("task_id", str(value))
        self.assertNotIn("adapter", str(value))

    def test_safe_projection_rejects_private_fields_paths_and_small_subgroups(self):
        for label, mutate, expected in [
            (
                "private key",
                lambda aggregate: aggregate.update({"task_ids": ["sealed-001"]}),
                "unsupported field",
            ),
            (
                "private path",
                lambda aggregate: aggregate["correctness"].update(
                    {"detail": "/private/sealed/task.json"}
                ),
                "unsupported field",
            ),
            (
                "sealed task id under allowed-looking field",
                lambda aggregate: aggregate["correctness"].update(
                    {"verdict": "sealed-001"}
                ),
                "verdict",
            ),
            (
                "prompt text under allowed-looking failure key",
                lambda aggregate: aggregate["failure_counts"]["baseline"].update(
                    {"incorrect": "SecretPromptText"}
                ),
                "integer",
            ),
            (
                "small subgroup",
                lambda aggregate: aggregate["by_capability"].update(
                    {"rename": {**_subgroup(), "task_count": 4}}
                ),
                "five-task floor",
            ),
        ]:
            with self.subTest(label=label):
                aggregate = _aggregate()
                mutate(aggregate)
                with self.assertRaisesRegex(ValueError, expected):
                    reporting.project_safe_aggregate(
                        aggregate,
                        _identity(),
                        unresolved_void_count=0,
                        private_evidence_sha256={"run_artifacts": "e" * 64},
                    )

    def test_safe_projection_rejects_aggregate_identity_mismatch(self):
        for field, value in (
            ("contract_id", "other-contract"),
            ("schema_version", 2),
            ("task_count", 7),
            ("decision_scope", "full"),
        ):
            with self.subTest(field=field):
                aggregate = _aggregate()
                aggregate[field] = value
                with self.assertRaisesRegex(ValueError, "aggregate and identity"):
                    reporting.project_safe_aggregate(
                        aggregate,
                        _identity(),
                        unresolved_void_count=0,
                        private_evidence_sha256={"run_artifacts": "e" * 64},
                    )

    def test_safe_projection_accepts_only_frozen_workflow_classes(self):
        workflow_classes = {
            "exact_lookup",
            "concept_search",
            "docs_config",
            "context_assembly",
            "references_trace",
            "impact_tests",
        }
        aggregate = _aggregate()
        aggregate["by_workflow"] = {
            workflow_class: _subgroup()
            for workflow_class in workflow_classes
        }

        value = reporting.project_safe_aggregate(
            aggregate,
            _identity(),
            unresolved_void_count=0,
            private_evidence_sha256={"run_artifacts": "e" * 64},
        )

        self.assertEqual(workflow_classes, set(value["by_workflow"]))

        aggregate["by_workflow"] = {"impact_analysis": _subgroup()}
        with self.assertRaisesRegex(ValueError, "unsupported subgroup"):
            reporting.project_safe_aggregate(
                aggregate,
                _identity(),
                unresolved_void_count=0,
                private_evidence_sha256={"run_artifacts": "e" * 64},
            )

    def test_safe_projection_rejects_non_string_digest_inputs_as_validation_errors(self):
        with self.assertRaisesRegex(ValueError, "must be an integer"):
            reporting.project_safe_aggregate(
                _aggregate(),
                _identity(),
                unresolved_void_count="0",
                private_evidence_sha256={"run_artifacts": "e" * 64},
            )
        with self.assertRaisesRegex(ValueError, "hashes must be an object"):
            reporting.project_safe_aggregate(
                _aggregate(),
                _identity(),
                unresolved_void_count=0,
                private_evidence_sha256=["e" * 64],
            )
        with self.assertRaisesRegex(ValueError, "lowercase SHA-256"):
            reporting.project_safe_aggregate(
                _aggregate(),
                _identity(),
                unresolved_void_count=0,
                private_evidence_sha256={"run_artifacts": 7},
            )
        with self.assertRaisesRegex(ValueError, "public identifiers"):
            reporting.project_safe_aggregate(
                _aggregate(),
                _identity(),
                unresolved_void_count=0,
                private_evidence_sha256={7: "e" * 64},
            )
        identity = _identity()
        identity["selection_sha256"] = 7
        with self.assertRaisesRegex(ValueError, "lowercase SHA-256"):
            reporting.project_safe_aggregate(
                _aggregate(),
                identity,
                unresolved_void_count=0,
                private_evidence_sha256={"run_artifacts": "e" * 64},
            )

    def test_decision_projection_requires_full_scope_and_zero_voids(self):
        aggregate = _aggregate()
        aggregate["decision_scope"] = "full"
        aggregate["by_capability"] = {
            capability: _subgroup() for capability in TAKEOVER_CAPABILITIES
        }
        identity = _identity()
        identity.update(
            {
                "corpus_role": "decision",
                "decision_scope": "full",
                "selected_capability_ids": sorted(TAKEOVER_CAPABILITIES),
            }
        )

        value = reporting.project_safe_aggregate(
            aggregate,
            identity,
            unresolved_void_count=0,
            private_evidence_sha256={"run_artifacts": "e" * 64},
        )
        self.assertEqual("pass", value["decision_verdict"])
        with self.assertRaisesRegex(ValueError, "zero unresolved voids"):
            reporting.project_safe_aggregate(
                aggregate,
                identity,
                unresolved_void_count=1,
                private_evidence_sha256={"run_artifacts": "e" * 64},
            )
        identity["selected_capability_ids"] = sorted(TAKEOVER_CAPABILITIES[:-1])
        with self.assertRaisesRegex(ValueError, "all takeover capabilities"):
            reporting.project_safe_aggregate(
                aggregate,
                identity,
                unresolved_void_count=0,
                private_evidence_sha256={"run_artifacts": "e" * 64},
            )

    def test_decision_projection_validates_then_strips_dynamic_subgroup_labels(self):
        aggregate = _aggregate()
        aggregate["decision_scope"] = "full"
        aggregate["by_capability"] = {
            capability: _subgroup() for capability in TAKEOVER_CAPABILITIES
        }
        aggregate["by_repo"] = {"private-repo": _subgroup()}
        aggregate["by_language"] = {"csharp": _subgroup()}
        identity = _identity()
        identity.update(
            {
                "corpus_role": "decision",
                "decision_scope": "full",
                "selected_capability_ids": sorted(TAKEOVER_CAPABILITIES),
            }
        )

        value = reporting.project_safe_aggregate(
            aggregate,
            identity,
            unresolved_void_count=0,
            private_evidence_sha256={"run_artifacts": "e" * 64},
        )

        self.assertEqual({}, value["by_repo"])
        self.assertEqual({}, value["by_language"])

        aggregate["by_repo"] = {"private-repo": {**_subgroup(), "task_count": 4}}
        with self.assertRaisesRegex(ValueError, "five-task floor"):
            reporting.project_safe_aggregate(
                aggregate,
                identity,
                unresolved_void_count=0,
                private_evidence_sha256={"run_artifacts": "e" * 64},
            )

    def test_decision_verdict_is_derived_from_relevance_correctness_wrong_actions_and_efficiency(self):
        aggregate = _aggregate()
        aggregate["decision_scope"] = "full"
        aggregate["decision_verdict"] = "pass"
        aggregate["by_capability"] = {
            capability: _subgroup() for capability in TAKEOVER_CAPABILITIES
        }
        identity = _identity()
        identity.update(
            {
                "corpus_role": "decision",
                "decision_scope": "full",
                "selected_capability_ids": sorted(TAKEOVER_CAPABILITIES),
            }
        )

        aggregate["relevance"]["candidate"]["top_1"] = 0.0
        aggregate["relevance"]["verdict"] = "pass"
        value = reporting.project_safe_aggregate(
            aggregate,
            identity,
            unresolved_void_count=0,
            private_evidence_sha256={"run_artifacts": "e" * 64},
        )
        self.assertEqual("fail", value["relevance"]["verdict"])
        self.assertEqual("fail", value["decision_verdict"])

        aggregate = _aggregate()
        aggregate["decision_scope"] = "full"
        aggregate["by_capability"] = {
            capability: _subgroup() for capability in TAKEOVER_CAPABILITIES
        }
        aggregate["correctness"]["candidate_wrong_action_task_count"] = 1
        aggregate["correctness"]["candidate_wrong_action_rate"] = 1 / 6
        aggregate["correctness"]["verdict"] = "pass"
        value = reporting.project_safe_aggregate(
            aggregate,
            identity,
            unresolved_void_count=0,
            private_evidence_sha256={"run_artifacts": "e" * 64},
        )
        self.assertEqual("fail", value["correctness"]["verdict"])
        self.assertEqual("fail", value["action_verdict"])
        self.assertEqual("fail", value["decision_verdict"])


if __name__ == "__main__":
    unittest.main()
