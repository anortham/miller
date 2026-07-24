import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path

from jsonschema import Draft202012Validator


SCRIPTS_ROOT = Path(__file__).resolve().parents[1]
BENCHMARK_ROOT = SCRIPTS_ROOT / "benchmarks" / "agent-efficiency"
sys.path.insert(0, str(SCRIPTS_ROOT))

from benchlib.agent_contract import (
    BenchmarkTask,
    SnapshotIdentity,
    StructuredAnswer,
    VerificationResult,
    action_target_guidance,
    count_tool_output_tokens,
    load_snapshot_manifest,
    load_task_manifest,
    validate_run_result,
    verify_answer,
)


def _valid_task() -> dict:
    return {
        "task_id": "dev-001",
        "repo_id": "fixture",
        "snapshot_id": "snapshot-001",
        "language": "python",
        "workflow_class": "concept_search",
        "evidence_critical": False,
        "prompt": "Explain the fallback selected by the factory.",
        "fact_predicates": [
            {
                "predicate_id": "fact-001",
                "source": "answer",
                "all_terms": ["token-baseline"],
                "any_terms": ["fallback", "default"],
                "evidence_anchor_ids": ["anchor-001"],
            },
            {
                "predicate_id": "fact-002",
                "source": "evidence_claim",
                "all_terms": ["factory"],
                "any_terms": ["returns", "selects"],
                "evidence_anchor_ids": ["anchor-001"],
            },
        ],
        "path_cited": [
            {
                "predicate_id": "path-001",
                "path": "src/factory.py",
                "evidence_anchor_ids": ["anchor-001"],
            }
        ],
        "symbol_cited": [
            {
                "predicate_id": "symbol-001",
                "symbol": "create_candidate",
                "evidence_anchor_ids": ["anchor-001"],
            }
        ],
        "evidence_anchors": [
            {
                "anchor_id": "anchor-001",
                "path": "src/factory.py",
                "symbol": "create_candidate",
                "line_start": 1,
                "line_end": 3,
            }
        ],
        "forbidden_claims": ["always uses embeddings"],
    }


def _reference_site(
    *,
    path: str = "src/factory.py",
    target_symbol_id: str | None = "python:src/factory.py:create_candidate",
    resolution: str = "exact",
) -> dict:
    return {
        "path": path,
        "line_start": 2,
        "line_end": 2,
        "column_start": 4,
        "column_end": 27,
        "reference_kind": "call",
        "containing_symbol_id": "python:src/app.py:build",
        "source_symbol_id": "python:src/app.py:build",
        "target_symbol_id": target_symbol_id,
        "resolution": resolution,
    }


def _valid_v1_task() -> dict:
    task = _valid_task()
    task["capabilities"] = ["exact_symbol_lookup", "homonym_disambiguation"]
    task["expected_outcome"] = "success"
    task["evidence_anchors"][0]["relevance_grade"] = 3
    task["reference_sites"] = [{"site_id": "site-001", **_reference_site()}]
    task["acceptable_actions"] = [
        {
            "action_id": "action-001",
            "kind": "inspect_symbol",
            "target": {"symbol_id": "python:src/factory.py:create_candidate"},
            "requirement_group": "identify-target",
            "evidence_anchor_ids": ["anchor-001"],
        },
        {
            "action_id": "action-002",
            "kind": "cite_reference_site",
            "target": {"reference_site": _reference_site()},
            "requirement_group": "cite-call-site",
            "reference_site_ids": ["site-001"],
        },
    ]
    task["forbidden_actions"] = [
        {
            "action_id": "action-003",
            "kind": "inspect_symbol",
            "target": {"symbol_id": "python:src/other.py:create_candidate"},
            "reason": "wrong homonym",
        }
    ]
    task["uncertainty_expectation"] = "must_resolve"
    return task


def _valid_v1_manifest(task: dict | None = None) -> dict:
    return {
        "contract_id": "takeover-evaluation-v1",
        "schema_version": 1,
        "tasks": [task or _valid_v1_task()],
    }


def _valid_v1_answer() -> dict:
    return {
        "contract_id": "takeover-evaluation-v1",
        "status": "answered",
        "answer": "The factory selects the token-baseline fallback.",
        "evidence": [
            {
                "path": "src/factory.py",
                "symbol": "create_candidate",
                "line": 2,
                "claim": "The factory returns the token-baseline fallback.",
            }
        ],
        "actions": [
            {
                "kind": "inspect_symbol",
                "target": {"symbol_id": "python:src/factory.py:create_candidate"},
            },
            {
                "kind": "cite_reference_site",
                "target": {"reference_site": _reference_site()},
            },
        ],
    }


def _load_v1_task(task: dict | None = None) -> BenchmarkTask:
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "tasks.json"
        _write_json(path, _valid_v1_manifest(task))
        return load_task_manifest(path)[0]


def _create_answer_snapshot(root: Path) -> None:
    (root / "src").mkdir()
    (root / "src" / "factory.py").write_text(
        "def create_candidate():\n    return 'token-baseline'\n",
        encoding="utf-8",
    )
    (root / "src" / "other.py").write_text(
        "def create_candidate():\n    return 'other'\n",
        encoding="utf-8",
    )


def _write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value), encoding="utf-8")


def _valid_run_result() -> dict:
    return {
        "schema_version": 1,
        "run_id": "run-001",
        "task_id": "dev-001",
        "snapshot_id": "snapshot-001",
        "product": "miller",
        "status": "completed",
        "failure_reason": None,
        "answer": {"status": "answered", "answer": "A grounded answer.", "evidence": []},
        "tool_calls": [],
        "tool_call_count": 0,
        "tool_output_bytes": 0,
        "tool_output_tokens": 0,
        "model_input_tokens": None,
        "model_output_tokens": None,
        "product_errors": [],
        "duplicate_calls": 0,
        "uncited_tool_output_tokens": 0,
        "wall_clock_ms": 0,
        "verification": {"passed": True, "failures": [], "matched_anchor_ids": []},
    }


def _git(root: Path, *args: str) -> str:
    return subprocess.run(
        ["git", "-C", str(root), *args],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def _create_snapshot(root: Path) -> SnapshotIdentity:
    _git(root, "init", "-q")
    _git(root, "config", "user.name", "Benchmark Fixture")
    _git(root, "config", "user.email", "benchmark@example.invalid")
    (root / "src").mkdir()
    (root / "src" / "factory.py").write_text(
        "def create_candidate():\n    return 'token-baseline'\n",
        encoding="utf-8",
    )
    (root / ".gitignore").write_text(".miller/\n.razorback/\nnested/\n*.tmp\n", encoding="utf-8")
    _git(root, "add", ".")
    _git(root, "commit", "-qm", "fixture")
    return SnapshotIdentity.capture("snapshot-001", "fixture", ("python",), root)


class AgentContractTests(unittest.TestCase):
    def test_model_guidance_exposes_evidence_and_minimal_action_contract(self) -> None:
        guidance = action_target_guidance()

        self.assertIn(
            "evidence.symbol is the human-readable symbol name, never a symbol ID",
            guidance,
        )
        self.assertIn(
            "Actions are the minimum typed evidence needed to ground the answer, not a transcript",
            guidance,
        )
        self.assertIn(
            "Choose action kinds from the task outcome and cited evidence, not from the tools you happened to call",
            guidance,
        )
        self.assertIn(
            "Configuration evidence is inspect_file even when a product tool exposes the config object as a symbol",
            guidance,
        )
        self.assertIn(
            "Every cited call site needs cite_reference_site",
            guidance,
        )
        self.assertIn("inspect_file means a file, document, or config fact", guidance)
        self.assertIn("select_tests means tests selected for the task", guidance)
        self.assertIn("read_log means evidence from captured logs or command output", guidance)
        self.assertIn("trace_call_path means a required source-to-target path", guidance)

    def test_count_tool_output_tokens_uses_frozen_o200k_encoding(self) -> None:
        self.assertEqual(0, count_tool_output_tokens(""))
        self.assertEqual(1, count_tool_output_tokens("hello"))
        self.assertEqual(2, count_tool_output_tokens("semantic search"))

    def test_count_tool_output_tokens_fails_when_tiktoken_is_unavailable(self) -> None:
        code = (
            f"import sys; sys.path.insert(0, {str(SCRIPTS_ROOT)!r}); "
            "from benchlib.agent_contract import count_tool_output_tokens; "
            "count_tool_output_tokens('hello')"
        )

        completed = subprocess.run(
            [sys.executable, "-S", "-c", code],
            capture_output=True,
            text=True,
        )

        self.assertNotEqual(0, completed.returncode)
        self.assertIn("ModuleNotFoundError", completed.stderr)
        self.assertNotIn("estimate", completed.stderr.lower())

    def test_public_contract_models_are_immutable_and_typed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            task_path = root / "tasks.json"
            _write_json(task_path, {"schema_version": 1, "tasks": [_valid_task()]})

            tasks = load_task_manifest(task_path)

        self.assertIsInstance(tasks[0], BenchmarkTask)
        self.assertEqual("concept_search", tasks[0].workflow_class)
        self.assertEqual(("anchor-001",), tasks[0].fact_predicates[0].evidence_anchor_ids)
        with self.assertRaises((AttributeError, TypeError)):
            tasks[0].task_id = "changed"

    def test_takeover_v1_loader_exposes_typed_semantics_and_keeps_legacy_explicit(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            v1_path = root / "v1-tasks.json"
            legacy_path = root / "legacy-tasks.json"
            _write_json(v1_path, _valid_v1_manifest())
            _write_json(legacy_path, {"schema_version": 1, "tasks": [_valid_task()]})

            v1_task = load_task_manifest(v1_path)[0]
            legacy_task = load_task_manifest(legacy_path)[0]
            v1_answer = StructuredAnswer.from_mapping(_valid_v1_answer())
            legacy_answer = StructuredAnswer.from_mapping(
                {"status": "answered", "answer": "Legacy answer.", "evidence": []}
            )

        self.assertTrue(hasattr(v1_task, "contract_id"))
        self.assertEqual("takeover-evaluation-v1", v1_task.contract_id)
        self.assertEqual(
            ("exact_symbol_lookup", "homonym_disambiguation"),
            v1_task.capabilities,
        )
        self.assertEqual("success", v1_task.expected_outcome)
        self.assertEqual(3, v1_task.evidence_anchors[0].relevance_grade)
        self.assertEqual("site-001", v1_task.reference_sites[0].site_id)
        self.assertEqual("identify-target", v1_task.acceptable_actions[0].requirement_group)
        self.assertEqual("wrong homonym", v1_task.forbidden_actions[0].reason)
        self.assertEqual("must_resolve", v1_task.uncertainty_expectation)
        self.assertEqual("takeover-evaluation-v1", v1_answer.contract_id)
        self.assertEqual("inspect_symbol", v1_answer.actions[0].kind)

        self.assertIsNone(legacy_task.contract_id)
        self.assertEqual((), legacy_task.capabilities)
        self.assertIsNone(legacy_task.expected_outcome)
        self.assertIsNone(legacy_answer.contract_id)
        self.assertEqual((), legacy_answer.actions)

    def test_takeover_v1_loader_rejects_semantically_invalid_labels(self) -> None:
        mutations = [
            (
                "missing v1 field",
                lambda value: value["tasks"][0].pop("capabilities"),
                "capabilities",
            ),
            (
                "duplicate capability",
                lambda value: value["tasks"][0]["capabilities"].append("exact_symbol_lookup"),
                "unique",
            ),
            (
                "empty capabilities",
                lambda value: value["tasks"][0].update({"capabilities": []}),
                "non-empty",
            ),
            (
                "invalid expected outcome",
                lambda value: value["tasks"][0].update({"expected_outcome": "hard_error"}),
                "hard_error",
            ),
            (
                "bad relevance grade",
                lambda value: value["tasks"][0]["evidence_anchors"][0].update(
                    {"relevance_grade": 0}
                ),
                "minimum",
            ),
            (
                "overlapping evidence anchors",
                lambda value: value["tasks"][0]["evidence_anchors"].append(
                    {
                        "anchor_id": "anchor-002",
                        "path": "src/factory.py",
                        "line_start": 2,
                        "line_end": 4,
                        "relevance_grade": 2,
                    }
                ),
                "overlapping",
            ),
            (
                "empty action target",
                lambda value: value["tasks"][0]["acceptable_actions"][0].update({"target": {}}),
                "non-empty",
            ),
            (
                "wrong typed action target",
                lambda value: value["tasks"][0]["acceptable_actions"][0].update(
                    {"target": {"pattern_id": "python.call"}}
                ),
                "typed target",
            ),
            (
                "executable grader",
                lambda value: value["tasks"][0]["acceptable_actions"][0].update(
                    {"callback": "grader.verify"}
                ),
                "callback",
            ),
            (
                "duplicate action id",
                lambda value: value["tasks"][0]["forbidden_actions"][0].update(
                    {"action_id": "action-001"}
                ),
                "duplicate",
            ),
            (
                "missing action anchor",
                lambda value: value["tasks"][0]["acceptable_actions"][0].update(
                    {"evidence_anchor_ids": ["anchor-999"]}
                ),
                "anchor-999",
            ),
            (
                "missing action reference site",
                lambda value: value["tasks"][0]["acceptable_actions"][1].update(
                    {"reference_site_ids": ["site-999"]}
                ),
                "site-999",
            ),
            (
                "reversed reference lines",
                lambda value: value["tasks"][0]["reference_sites"][0].update(
                    {"line_start": 3, "line_end": 2}
                ),
                "line_end",
            ),
            (
                "reversed reference columns",
                lambda value: value["tasks"][0]["reference_sites"][0].update(
                    {"column_start": 28, "column_end": 27}
                ),
                "column_end",
            ),
            (
                "exact reference without target",
                lambda value: value["tasks"][0]["reference_sites"][0].update(
                    {"target_symbol_id": None}
                ),
                "target_symbol_id",
            ),
            (
                "refusal mismatch",
                lambda value: value["tasks"][0].update(
                    {"uncertainty_expectation": "must_refuse"}
                ),
                "must_refuse",
            ),
        ]

        for label, mutate, expected in mutations:
            with self.subTest(label=label), tempfile.TemporaryDirectory() as directory:
                value = _valid_v1_manifest()
                mutate(value)
                path = Path(directory) / "tasks.json"
                _write_json(path, value)

                with self.assertRaisesRegex(ValueError, expected):
                    load_task_manifest(path)

    def test_takeover_v1_verifier_returns_ordered_matches_and_success_outcome(self) -> None:
        task = _load_v1_task()
        answer = _valid_v1_answer()
        answer["evidence"].extend(
            [
                dict(answer["evidence"][0]),
                {
                    "path": "src/other.py",
                    "symbol": "create_candidate",
                    "line": 2,
                    "claim": "This unrelated homonym returns another value.",
                },
            ]
        )

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _create_answer_snapshot(root)
            result = verify_answer(task, answer, root)

        self.assertEqual(
            VerificationResult(
                passed=True,
                failures=(),
                matched_anchor_ids=("anchor-001",),
                ordered_evidence_matches=("anchor-001", None, None),
                observed_outcome="success",
                wrong_action_count=0,
            ),
            result,
        )

    def test_takeover_v1_verifier_accepts_grounded_symbol_path_metadata(self) -> None:
        task = _load_v1_task()
        answer = _valid_v1_answer()
        answer["actions"][0]["target"]["path"] = "src/factory.py"

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _create_answer_snapshot(root)
            accepted = verify_answer(task, answer, root)

            answer["actions"][0]["target"]["path"] = "src/other.py"
            rejected = verify_answer(task, answer, root)

        self.assertTrue(accepted.passed)
        self.assertFalse(rejected.passed)
        self.assertEqual(1, rejected.wrong_action_count)

    def test_takeover_v1_verifier_accepts_current_workspace_selector_aliases(self) -> None:
        task_value = _valid_v1_task()
        task_value.update(
            {
                "expected_outcome": "empty",
                "fact_predicates": [],
                "path_cited": [],
                "symbol_cited": [],
                "evidence_anchors": [],
                "reference_sites": [],
                "acceptable_actions": [
                    {
                        "action_id": "action-001",
                        "kind": "report_empty",
                        "target": {"workspace_selector": "fixture"},
                        "requirement_group": "outcome",
                    }
                ],
                "forbidden_actions": [],
                "uncertainty_expectation": "must_resolve",
            }
        )
        task = _load_v1_task(task_value)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            canonical_root = str(root.resolve())
            normalized = (
                canonical_root.lower()
                if sys.platform in {"darwin", "win32"}
                else canonical_root
            )
            workspace_id = hashlib.sha256(normalized.encode()).hexdigest()
            for selector in (".", "current", "fixture", workspace_id):
                with self.subTest(selector=selector):
                    answer = {
                        "contract_id": "takeover-evaluation-v1",
                        "status": "not_found",
                        "answer": "No qualifying result exists.",
                        "evidence": [],
                        "actions": [
                            {
                                "kind": "report_empty",
                                "target": {"workspace_selector": selector},
                            }
                        ],
                    }
                    self.assertTrue(verify_answer(task, answer, root).passed)

    def test_takeover_v1_verifier_rejects_wrong_homonym_site_and_forbidden_actions(self) -> None:
        task = _load_v1_task()
        cases: list[tuple[str, dict, str]] = []

        wrong_homonym = _valid_v1_answer()
        wrong_homonym["actions"][0]["target"]["symbol_id"] = (
            "python:src/other.py:create_candidate"
        )
        wrong_homonym["actions"].append(dict(wrong_homonym["actions"][0]))
        cases.append(("wrong homonym", wrong_homonym, "forbidden"))

        wrong_site = _valid_v1_answer()
        wrong_site["actions"][1]["target"]["reference_site"]["line_start"] = 1
        wrong_site["actions"][1]["target"]["reference_site"]["line_end"] = 1
        cases.append(("wrong site", wrong_site, "unrecognized"))

        unexpected_empty = _valid_v1_answer()
        unexpected_empty["status"] = "not_found"
        cases.append(("unexpected empty", unexpected_empty, "expected success"))

        unexpected_refusal = _valid_v1_answer()
        unexpected_refusal["status"] = "blocked"
        cases.append(("unexpected refusal", unexpected_refusal, "expected success"))

        missing_actions = _valid_v1_answer()
        missing_actions["actions"] = []
        cases.append(("missing actions", missing_actions, "missing requirement group"))

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _create_answer_snapshot(root)
            for label, answer, expected_failure in cases:
                with self.subTest(label=label):
                    result = verify_answer(task, answer, root)
                    self.assertFalse(result.passed)
                    self.assertEqual("wrong_answer", result.observed_outcome)
                    self.assertIn(expected_failure, "\n".join(result.failures))
                    if label == "wrong homonym":
                        self.assertEqual(1, result.wrong_action_count)

    def test_takeover_v1_verifier_accepts_only_expected_empty_and_refusal(self) -> None:
        empty_task_value = _valid_v1_task()
        empty_task_value.update(
            {
                "expected_outcome": "empty",
                "fact_predicates": [],
                "path_cited": [],
                "symbol_cited": [],
                "evidence_anchors": [],
                "reference_sites": [],
                "acceptable_actions": [
                    {
                        "action_id": "action-001",
                        "kind": "report_empty",
                        "target": {"workspace_selector": "current"},
                        "requirement_group": "outcome",
                    }
                ],
                "forbidden_actions": [],
                "uncertainty_expectation": "must_resolve",
            }
        )
        refusal_task_value = _valid_v1_task()
        refusal_task_value.update(
            {
                "expected_outcome": "refusal",
                "fact_predicates": [],
                "path_cited": [],
                "symbol_cited": [],
                "evidence_anchors": [],
                "reference_sites": [],
                "acceptable_actions": [
                    {
                        "action_id": "action-001",
                        "kind": "refuse_unsafe",
                        "target": {
                            "symbol_id": "python:src/factory.py:create_candidate"
                        },
                        "requirement_group": "outcome",
                    }
                ],
                "forbidden_actions": [],
                "uncertainty_expectation": "must_refuse",
            }
        )
        empty_task = _load_v1_task(empty_task_value)
        refusal_task = _load_v1_task(refusal_task_value)
        empty_answer = {
            "contract_id": "takeover-evaluation-v1",
            "status": "not_found",
            "answer": "No qualifying result exists.",
            "evidence": [],
            "actions": [
                {"kind": "report_empty", "target": {"workspace_selector": "current"}}
            ],
        }
        refusal_answer = {
            "contract_id": "takeover-evaluation-v1",
            "status": "blocked",
            "answer": "The exact action is unsafe.",
            "evidence": [],
            "actions": [
                {
                    "kind": "refuse_unsafe",
                    "target": {"symbol_id": "python:src/factory.py:create_candidate"},
                }
            ],
        }

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.assertEqual("empty", verify_answer(empty_task, empty_answer, root).observed_outcome)
            self.assertTrue(verify_answer(empty_task, empty_answer, root).passed)
            self.assertEqual(
                "refusal",
                verify_answer(refusal_task, refusal_answer, root).observed_outcome,
            )
            self.assertTrue(verify_answer(refusal_task, refusal_answer, root).passed)

            wrong_empty = verify_answer(empty_task, {**empty_answer, "status": "answered"}, root)
            wrong_refusal = verify_answer(
                refusal_task,
                {**refusal_answer, "status": "not_found"},
                root,
            )
            conflicting_refusal_answer = {
                **refusal_answer,
                "actions": [
                    *refusal_answer["actions"],
                    {
                        "kind": "inspect_symbol",
                        "target": {"symbol_id": "python:src/factory.py:create_candidate"},
                    },
                ],
            }
            conflicting_refusal = verify_answer(
                refusal_task,
                conflicting_refusal_answer,
                root,
            )
        self.assertEqual("wrong_answer", wrong_empty.observed_outcome)
        self.assertEqual("wrong_answer", wrong_refusal.observed_outcome)
        self.assertEqual("wrong_answer", conflicting_refusal.observed_outcome)
        self.assertEqual(1, conflicting_refusal.wrong_action_count)
        self.assertIn("conflicting", "\n".join(conflicting_refusal.failures))

    def test_takeover_v1_refusal_uses_one_exact_symbol_target(self) -> None:
        task_value = _valid_v1_task()
        task_value.update(
            {
                "expected_outcome": "refusal",
                "fact_predicates": [],
                "path_cited": [],
                "symbol_cited": [],
                "evidence_anchors": [],
                "reference_sites": [],
                "acceptable_actions": [
                    {
                        "action_id": "action-001",
                        "kind": "refuse_unsafe",
                        "target": {
                            "symbol_id": "python:src/factory.py:create_candidate"
                        },
                        "requirement_group": "outcome",
                    }
                ],
                "forbidden_actions": [],
                "uncertainty_expectation": "must_refuse",
            }
        )
        answer = {
            "contract_id": "takeover-evaluation-v1",
            "status": "blocked",
            "answer": "The homonym cannot be changed safely.",
            "evidence": [],
            "actions": [
                {
                    "kind": "refuse_unsafe",
                    "target": {"symbol_id": "python:src/factory.py:create_candidate"},
                }
            ],
        }

        with tempfile.TemporaryDirectory() as directory:
            result = verify_answer(_load_v1_task(task_value), answer, directory)

        self.assertTrue(result.passed)
        self.assertEqual("refusal", result.observed_outcome)

        answer["actions"][0]["target"]["path"] = "src/factory.py"
        with self.assertRaisesRegex(ValueError, "typed target"):
            StructuredAnswer.from_mapping(answer)

    def test_takeover_v1_verifier_rejects_wrong_edit_and_rename_targets(self) -> None:
        task_value = _valid_v1_task()
        task_value["capabilities"] = ["edit", "rename"]
        task_value["acceptable_actions"] = [
            {
                "action_id": "action-001",
                "kind": "propose_edit",
                "target": {
                    "path": "src/factory.py",
                    "symbol_id": "python:src/factory.py:create_candidate",
                },
                "requirement_group": "edit-target",
                "evidence_anchor_ids": ["anchor-001"],
            },
            {
                "action_id": "action-002",
                "kind": "propose_rename",
                "target": {"symbol_id": "python:src/factory.py:create_candidate"},
                "requirement_group": "rename-target",
            },
        ]
        task_value["forbidden_actions"] = []
        answer = _valid_v1_answer()
        answer["actions"] = [
            {
                "kind": "propose_edit",
                "target": {
                    "path": "src/other.py",
                    "symbol_id": "python:src/other.py:create_candidate",
                },
            },
            {
                "kind": "propose_rename",
                "target": {"symbol_id": "python:src/other.py:create_candidate"},
            },
        ]

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _create_answer_snapshot(root)
            result = verify_answer(_load_v1_task(task_value), answer, root)

        self.assertFalse(result.passed)
        self.assertEqual("wrong_answer", result.observed_outcome)
        self.assertEqual(2, result.wrong_action_count)
        self.assertIn("edit-target", "\n".join(result.failures))
        self.assertIn("rename-target", "\n".join(result.failures))

    def test_takeover_v1_verifier_enforces_all_uncertainty_expectations(self) -> None:
        resolved_task = _load_v1_task()
        fallback_task_value = _valid_v1_task()
        fallback_site = _reference_site(resolution="fallback")
        fallback_task_value["reference_sites"] = [{"site_id": "site-001", **fallback_site}]
        fallback_task_value["acceptable_actions"][1]["target"] = {
            "reference_site": fallback_site
        }
        fallback_task_value["uncertainty_expectation"] = "must_disclose"
        fallback_task = _load_v1_task(fallback_task_value)
        fallback_answer = _valid_v1_answer()
        fallback_answer["actions"][1]["target"] = {"reference_site": fallback_site}

        unresolved_answer = _valid_v1_answer()
        unresolved_answer["actions"][1]["target"]["reference_site"].update(
            {"resolution": "unresolved", "target_symbol_id": None}
        )

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _create_answer_snapshot(root)
            resolved_failure = verify_answer(resolved_task, unresolved_answer, root)
            disclosed = verify_answer(fallback_task, fallback_answer, root)
            undisclosed_answer = _valid_v1_answer()
            undisclosed_answer["actions"] = [undisclosed_answer["actions"][0]]
            undisclosed = verify_answer(fallback_task, undisclosed_answer, root)

        self.assertEqual("wrong_answer", resolved_failure.observed_outcome)
        self.assertIn("must_resolve", "\n".join(resolved_failure.failures))
        self.assertTrue(disclosed.passed)
        self.assertEqual("success", disclosed.observed_outcome)
        self.assertEqual("wrong_answer", undisclosed.observed_outcome)
        self.assertIn("must_disclose", "\n".join(undisclosed.failures))

    def test_task_loader_rejects_extra_executable_fields_and_invalid_criticality(self) -> None:
        mutations = [
            ("task", lambda value: value["tasks"][0].update({"regex": ".*"}), "regex"),
            (
                "predicate",
                lambda value: value["tasks"][0]["fact_predicates"][0].update(
                    {"callback": "module.verify"}
                ),
                "callback",
            ),
            (
                "absolute path",
                lambda value: value["tasks"][0]["evidence_anchors"][0].update(
                    {"path": "/tmp/source.py"}
                ),
                "path",
            ),
            (
                "missing anchor",
                lambda value: value["tasks"][0]["fact_predicates"][0].update(
                    {"evidence_anchor_ids": ["anchor-999"]}
                ),
                "anchor-999",
            ),
            (
                "derived criticality",
                lambda value: value["tasks"][0].update({"evidence_critical": True}),
                "evidence_critical",
            ),
            (
                "reversed line bounds",
                lambda value: value["tasks"][0]["evidence_anchors"][0].update(
                    {"line_start": 3, "line_end": 2}
                ),
                "line_end",
            ),
            (
                "duplicate anchor",
                lambda value: value["tasks"][0]["evidence_anchors"].append(
                    dict(value["tasks"][0]["evidence_anchors"][0])
                ),
                "duplicate",
            ),
        ]

        for label, mutate, expected in mutations:
            with self.subTest(label=label), tempfile.TemporaryDirectory() as directory:
                value = {"schema_version": 1, "tasks": [_valid_task()]}
                mutate(value)
                path = Path(directory) / "tasks.json"
                _write_json(path, value)

                with self.assertRaisesRegex(ValueError, expected):
                    load_task_manifest(path)

    def test_manifest_loaders_reject_duplicate_json_object_keys(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            task_path = root / "tasks.json"
            task_path.write_text(
                '{"schema_version":1,"schema_version":1,"tasks":[]}',
                encoding="utf-8",
            )
            snapshot_path = root / "snapshots.json"
            snapshot_path.write_text(
                '{"schema_version":1,"snapshots":[],"snapshots":[]}',
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "duplicate.*schema_version"):
                load_task_manifest(task_path)
            with self.assertRaisesRegex(ValueError, "duplicate.*snapshots"):
                load_snapshot_manifest(snapshot_path)

    def test_snapshot_loader_rejects_absolute_roots_and_extra_fields(self) -> None:
        valid = {
            "schema_version": 1,
            "snapshots": [
                {
                    "snapshot_id": "snapshot-001",
                    "repo_id": "fixture",
                    "commit": "a" * 40,
                    "content_sha256": "b" * 64,
                    "languages": ["python"],
                }
            ],
        }
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "snapshots.json"
            _write_json(path, valid)
            snapshots = load_snapshot_manifest(path)
            self.assertIsInstance(snapshots[0], SnapshotIdentity)

            valid["snapshots"][0]["root"] = "/tmp/fixture"
            _write_json(path, valid)
            with self.assertRaisesRegex(ValueError, "root"):
                load_snapshot_manifest(path)

    def test_snapshot_identity_accepts_only_exact_clean_root(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            identity = _create_snapshot(root)

            self.assertEqual(40, len(identity.commit))
            self.assertEqual(64, len(identity.content_sha256))
            self.assertEqual(VerificationResult(True, (), ()), identity.verify_root(root))

            wrong_commit = replace(identity, commit="0" * 40).verify_root(root)
            wrong_hash = replace(identity, content_sha256="0" * 64).verify_root(root)
            self.assertIn("snapshot: commit mismatch", wrong_commit.failures)
            self.assertIn("snapshot: content SHA-256 mismatch", wrong_hash.failures)

            (root / "src" / "factory.py").write_text("changed\n", encoding="utf-8")
            dirty = identity.verify_root(root)
            self.assertIn("snapshot: working tree is dirty", dirty.failures)

    def test_snapshot_identity_rejects_nested_worktrees_and_product_artifacts(self) -> None:
        for relative, expected in [
            ("nested/.git", "nested Git worktree"),
            (".miller/vectors.db", "product or benchmark artifact"),
            (".razorback/sdd/progress.md", "product or benchmark artifact"),
        ]:
            with self.subTest(relative=relative), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                identity = _create_snapshot(root)
                artifact = root / relative
                artifact.parent.mkdir(parents=True, exist_ok=True)
                artifact.write_text("artifact", encoding="utf-8")

                result = identity.verify_root(root)

                self.assertFalse(result.passed)
                self.assertTrue(any(expected in failure for failure in result.failures))

    def test_prepared_snapshot_accepts_only_top_level_miller_and_julie_artifacts(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            identity = _create_snapshot(root)
            (root / ".miller").mkdir()
            (root / ".miller" / "vectors.db").write_text("vectors", encoding="utf-8")
            (root / ".julie").mkdir()
            (root / ".julie" / "symbols.db").write_text("symbols", encoding="utf-8")

            self.assertEqual(
                VerificationResult(True, (), ()),
                identity.verify_prepared_root(root),
            )
            self.assertFalse(identity.verify_root(root).passed)

    def test_prepared_snapshot_requires_both_artifact_directories(self) -> None:
        for present, missing in [(".miller", ".julie"), (".julie", ".miller")]:
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                identity = _create_snapshot(root)
                (root / present).mkdir()

                result = identity.verify_prepared_root(root)

                self.assertFalse(result.passed)
                self.assertIn(f"snapshot: required prepared directory is missing: {missing}", result.failures)

    def test_prepared_snapshot_rejects_dirt_outside_permitted_artifact_directories(self) -> None:
        cases = [
            ("src/factory.py", "changed\n", "working tree is dirty"),
            ("untracked.txt", "untracked\n", "working tree is dirty"),
            ("ignored.tmp", "ignored\n", "working tree is dirty"),
            ("nested/.git", "gitdir: elsewhere\n", "nested Git worktree"),
            ("nested/.miller/vectors.db", "vectors", "product or benchmark artifact"),
            (".eros/cache.db", "cache", "product or benchmark artifact"),
            (".razorback/progress.md", "progress", "product or benchmark artifact"),
        ]
        for relative, content, expected in cases:
            with self.subTest(relative=relative), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                identity = _create_snapshot(root)
                (root / ".miller").mkdir()
                (root / ".julie").mkdir()
                target = root / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(content, encoding="utf-8")

                result = identity.verify_prepared_root(root)

                self.assertFalse(result.passed)
                self.assertTrue(any(expected in failure for failure in result.failures))

    def test_prepared_snapshot_still_verifies_repository_identity(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            identity = _create_snapshot(root)
            (root / ".miller").mkdir()
            (root / ".julie").mkdir()

            wrong_commit = replace(identity, commit="0" * 40).verify_prepared_root(root)
            wrong_hash = replace(identity, content_sha256="0" * 64).verify_prepared_root(root)

            self.assertIn("snapshot: commit mismatch", wrong_commit.failures)
            self.assertIn("snapshot: content SHA-256 mismatch", wrong_hash.failures)

        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            root = parent / "repo"
            root.mkdir()
            identity = _create_snapshot(root)
            (root / ".miller").mkdir()
            (root / ".julie").mkdir()

            result = identity.verify_prepared_root(root / "src")

            self.assertFalse(result.passed)
            self.assertIn("snapshot: root is not the repository top level", result.failures)

    def test_prepared_snapshot_rejects_symlinks_in_artifact_trees(self) -> None:
        cases = [
            (".miller", True),
            (".miller/vectors.db", False),
            (".julie/cache", True),
        ]
        for relative, target_is_directory in cases:
            with self.subTest(relative=relative), tempfile.TemporaryDirectory() as directory:
                parent = Path(directory)
                root = parent / "repo"
                root.mkdir()
                identity = _create_snapshot(root)
                external = parent / "external"
                if target_is_directory:
                    external.mkdir()
                else:
                    external.write_text("external", encoding="utf-8")
                for artifact in [".miller", ".julie"]:
                    if relative != artifact:
                        (root / artifact).mkdir()
                link = root / relative
                link.parent.mkdir(parents=True, exist_ok=True)
                link.symlink_to(external, target_is_directory=target_is_directory)

                result = identity.verify_prepared_root(root)

                self.assertFalse(result.passed)
                self.assertTrue(any("symbolic link" in failure for failure in result.failures))

    def test_prepared_snapshot_rejects_git_markers_in_artifact_trees(self) -> None:
        for relative in [".miller/nested/.git", ".julie/nested/.git/config"]:
            with self.subTest(relative=relative), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                identity = _create_snapshot(root)
                (root / ".miller").mkdir()
                (root / ".julie").mkdir()
                marker = root / relative
                marker.parent.mkdir(parents=True, exist_ok=True)
                marker.write_text("gitdir: elsewhere\n", encoding="utf-8")

                result = identity.verify_prepared_root(root)

                self.assertFalse(result.passed)
                self.assertTrue(any("nested Git worktree" in failure for failure in result.failures))

    def test_verify_answer_enforces_facts_anchors_paths_symbols_and_forbidden_claims(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _create_snapshot(root)
            task_path = root / "task-manifest.json"
            _write_json(task_path, {"schema_version": 1, "tasks": [_valid_task()]})
            _git(root, "add", "task-manifest.json")
            _git(root, "commit", "-qm", "task manifest")
            task = load_task_manifest(task_path)[0]
            answer = {
                "status": "answered",
                "answer": "The token-baseline is the default fallback.",
                "evidence": [
                    {
                        "path": "src/factory.py",
                        "symbol": "create_candidate",
                        "line": 2,
                        "claim": "The factory returns token-baseline.",
                    }
                ],
            }

            passed = verify_answer(task, answer, root)

            self.assertEqual(VerificationResult(True, (), ("anchor-001",)), passed)
            self.assertIsInstance(StructuredAnswer.from_mapping(answer), StructuredAnswer)

            cases = [
                ({**answer, "answer": "The embedding candidate is selected."}, "fact-001"),
                ({**answer, "evidence": []}, "anchor-001"),
                (
                    {
                        **answer,
                        "answer": "The factory still selects token-baseline.",
                        "evidence": [{**answer["evidence"][0], "claim": "It returns token-baseline."}],
                    },
                    "fact-002",
                ),
                (
                    {
                        **answer,
                        "evidence": [{**answer["evidence"][0], "claim": "The factory chooses token-baseline."}],
                    },
                    "any_terms",
                ),
                (
                    {
                        **answer,
                        "evidence": [{**answer["evidence"][0], "symbol": "wrong_symbol"}],
                    },
                    "symbol-001",
                ),
                (
                    {
                        **answer,
                        "evidence": [
                            {**answer["evidence"][0], "path": "task-manifest.json"}
                        ],
                    },
                    "path-001",
                ),
                (
                    {**answer, "answer": "It always uses embeddings."},
                    "forbidden claim",
                ),
                (
                    {
                        **answer,
                        "evidence": [{**answer["evidence"][0], "line": 4}],
                    },
                    "no accepted anchor",
                ),
                (
                    {
                        **answer,
                        "evidence": [{**answer["evidence"][0], "path": "src/missing.py"}],
                    },
                    "path does not exist",
                ),
            ]
            for invalid, expected in cases:
                with self.subTest(expected=expected):
                    result = verify_answer(task, invalid, root)
                    self.assertFalse(result.passed)
                    self.assertTrue(any(expected in item for item in result.failures))

            escaping = {
                **answer,
                "evidence": [{**answer["evidence"][0], "path": "../outside.py"}],
            }
            with self.assertRaisesRegex(ValueError, "does not match"):
                verify_answer(task, escaping, root)

    def test_verify_answer_unions_multi_anchor_path_and_symbol_citations_across_rows(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _create_snapshot(root)
            task_value = _valid_task()
            task_value["fact_predicates"] = [
                {
                    "predicate_id": "fact-001",
                    "source": "evidence_claim",
                    "all_terms": ["factory"],
                    "any_terms": ["returns", "selects"],
                    "evidence_anchor_ids": ["anchor-001", "anchor-002"],
                }
            ]
            task_value["path_cited"][0]["evidence_anchor_ids"] = ["anchor-001", "anchor-002"]
            task_value["symbol_cited"][0]["evidence_anchor_ids"] = ["anchor-001", "anchor-002"]
            task_value["evidence_anchors"][0].update({"line_start": 1, "line_end": 1})
            task_value["evidence_anchors"].append(
                {
                    "anchor_id": "anchor-002",
                    "path": "src/factory.py",
                    "symbol": "create_candidate",
                    "line_start": 2,
                    "line_end": 2,
                }
            )
            task_path = root / "task-manifest.json"
            _write_json(task_path, {"schema_version": 1, "tasks": [task_value]})
            task = load_task_manifest(task_path)[0]
            answer = {
                "status": "answered",
                "answer": "The implementation is grounded in both rows.",
                "evidence": [
                    {
                        "path": "src/factory.py",
                        "symbol": "create_candidate",
                        "line": 1,
                        "claim": "The factory selects a candidate.",
                    },
                    {
                        "path": "src/factory.py",
                        "symbol": "create_candidate",
                        "line": 2,
                        "claim": "The factory returns token-baseline.",
                    },
                ],
            }

            result = verify_answer(task, answer, root)

            self.assertEqual(VerificationResult(True, (), ("anchor-001", "anchor-002")), result)

    def test_schemas_are_strict_and_accept_the_committed_visible_manifests(self) -> None:
        pairs = [
            ("task-manifest.schema.json", "dev-tasks.json"),
            ("snapshot-manifest.schema.json", "dev-snapshots.json"),
            ("answer-schema.json", None),
            ("run-result.schema.json", None),
            ("product-verdict-attestation.schema.json", None),
        ]
        for schema_name, manifest_name in pairs:
            with self.subTest(schema=schema_name):
                schema = json.loads((BENCHMARK_ROOT / schema_name).read_text(encoding="utf-8"))
                Draft202012Validator.check_schema(schema)
                self.assertFalse(schema.get("additionalProperties", True))
                if manifest_name:
                    manifest = json.loads((BENCHMARK_ROOT / manifest_name).read_text(encoding="utf-8"))
                    Draft202012Validator(schema).validate(manifest)

    def test_product_verdict_attestation_schema_is_strict_and_privacy_safe(self):
        schema = json.loads(
            (BENCHMARK_ROOT / "product-verdict-attestation.schema.json").read_text(encoding="utf-8")
        )
        self.assertEqual(
            "https://miller.local/schemas/agent-efficiency/product-verdict-attestation-v1.json",
            schema["$id"],
        )
        validator = Draft202012Validator(schema)
        attestation = {
            "attestation_contract_id": "takeover-product-verdict-v1",
            "safe_aggregate_sha256": "a" * 64,
            "product_under_test": "Miller",
            "product_verdict": "pass",
            "mapping_frozen_before_preflight": True,
            "mapping_changed": False,
            "preflight_passed": True,
            "automatic_reruns_complete": True,
            "artifact_verification_passed": True,
            "unresolved_void_count": 0,
        }

        self.assertEqual([], list(validator.iter_errors(attestation)))
        missing = dict(attestation)
        del missing["preflight_passed"]
        self.assertNotEqual([], list(validator.iter_errors(missing)))

        for field, value in (
            ("safe_aggregate_sha256", "A" * 64),
            ("product_under_test", "candidate"),
            ("product_verdict", "not_decisional"),
            ("mapping_frozen_before_preflight", False),
            ("mapping_changed", True),
            ("unresolved_void_count", 1),
        ):
            with self.subTest(field=field):
                invalid = {**attestation, field: value}
                self.assertNotEqual([], list(validator.iter_errors(invalid)))

        self.assertNotEqual(
            [],
            list(validator.iter_errors({**attestation, "neutral_role_mapping": "candidate"})),
        )

    def test_run_result_schema_keeps_typed_diagnostics_and_process_status_consistent(self) -> None:
        schema = json.loads((BENCHMARK_ROOT / "run-result.schema.json").read_text(encoding="utf-8"))
        validator = Draft202012Validator(schema)
        value = _valid_run_result()
        validator.validate(value)
        validate_run_result(value)

        completed_but_incorrect = {
            **value,
            "verification": {**value["verification"], "passed": False},
            "failure_reason": "incorrect",
        }
        validator.validate(completed_but_incorrect)
        timed_out = {
            **value,
            "status": "timeout",
            "answer": None,
            "verification": {**value["verification"], "passed": False},
            "failure_reason": "product_error",
        }
        validator.validate(timed_out)
        for outcome in ["disallowed_tool", "budget_exceeded"]:
            with self.subTest(outcome=outcome):
                validator.validate(
                    {
                        **value,
                        "status": outcome,
                        "answer": None,
                        "verification": {**value["verification"], "passed": False},
                        "failure_reason": outcome,
                    }
                )

        invalid = [
            ({**value, "answer": {**value["answer"], "unexpected": "not allowed"}}, "unexpected"),
            ({**value, "answer": None}, "object"),
            ({**value, "failure_reason": "incorrect"}, "null"),
            (
                {
                    **value,
                    "verification": {**value["verification"], "passed": False},
                    "failure_reason": None,
                },
                "not one of",
            ),
            (
                {
                    **value,
                    "status": "timeout",
                    "answer": None,
                    "verification": {**value["verification"], "passed": False},
                    "failure_reason": "incorrect",
                },
                "product_error",
            ),
            (
                {
                    **value,
                    "status": "timeout",
                    "verification": {**value["verification"], "passed": False},
                    "failure_reason": "product_error",
                },
                "null",
            ),
            (
                {
                    **value,
                    "status": "timeout",
                    "answer": None,
                    "failure_reason": None,
                },
                "False",
            ),
            (
                {
                    **value,
                    "status": "invalid_answer",
                    "answer": None,
                    "verification": {**value["verification"], "passed": False},
                    "failure_reason": "product_error",
                },
                "invalid_answer",
            ),
            (
                {
                    **value,
                    "status": "disallowed_tool",
                    "answer": None,
                    "verification": {**value["verification"], "passed": False},
                    "failure_reason": "budget_exceeded",
                },
                "disallowed_tool",
            ),
            (
                {
                    **value,
                    "status": "budget_exceeded",
                    "answer": None,
                    "verification": {**value["verification"], "passed": False},
                    "failure_reason": "disallowed_tool",
                },
                "budget_exceeded",
            ),
        ]
        for candidate, expected in invalid:
            with self.subTest(expected=expected):
                errors = list(validator.iter_errors(candidate))
                self.assertTrue(errors)
                self.assertTrue(any(expected in error.message for error in errors), [error.message for error in errors])

        overflow = {**value, "tool_output_tokens": 2, "uncited_tool_output_tokens": 3}
        with self.assertRaisesRegex(ValueError, "uncited_tool_output_tokens"):
            validate_run_result(overflow)

    def test_schema_enums_and_numeric_floors_are_exact(self) -> None:
        task = json.loads((BENCHMARK_ROOT / "task-manifest.schema.json").read_text(encoding="utf-8"))
        answer = json.loads((BENCHMARK_ROOT / "answer-schema.json").read_text(encoding="utf-8"))
        run = json.loads((BENCHMARK_ROOT / "run-result.schema.json").read_text(encoding="utf-8"))

        self.assertEqual(
            [
                "exact_lookup",
                "concept_search",
                "docs_config",
                "context_assembly",
                "references_trace",
                "impact_tests",
            ],
            task["$defs"]["task"]["properties"]["workflow_class"]["enum"],
        )
        self.assertEqual(
            ["answer", "evidence_claim"],
            task["$defs"]["factPredicate"]["properties"]["source"]["enum"],
        )
        self.assertEqual(1, task["$defs"]["evidenceAnchor"]["properties"]["line_start"]["minimum"])
        self.assertEqual(["answered", "not_found", "blocked"], answer["properties"]["status"]["enum"])
        self.assertEqual(8, answer["properties"]["evidence"]["maxItems"])
        serialized_answer_schema = json.dumps(answer, sort_keys=True)
        for unsupported_keyword in (
            '"allOf"',
            '"dependentRequired"',
            '"minProperties"',
            '"oneOf"',
            '"pattern"',
        ):
            self.assertNotIn(unsupported_keyword, serialized_answer_schema)
        self.assertEqual(
            [
                {"$ref": "#/$defs/canonicalSymbolId"},
                {"type": "null"},
            ],
            answer["$defs"]["nullableCanonicalSymbolId"]["anyOf"],
        )
        for object_schema in (
            answer,
            answer["properties"]["evidence"]["items"],
            answer["$defs"]["referenceSiteIdentity"],
            answer["$defs"]["actionTarget"],
            answer["$defs"]["submittedAction"],
        ):
            self.assertEqual(set(object_schema["properties"]), set(object_schema["required"]))
        evidence = answer["properties"]["evidence"]["items"]
        self.assertEqual(set(evidence["properties"]), set(evidence["required"]))
        self.assertEqual(["string", "null"], evidence["properties"]["symbol"]["type"])
        self.assertEqual(["integer", "null"], evidence["properties"]["line"]["type"])
        self.assertNotIn("pattern", evidence["properties"]["path"])
        self.assertEqual(["miller", "julie"], run["properties"]["product"]["enum"])
        self.assertEqual(
            [
                "incorrect",
                "insufficient_evidence",
                "budget_exceeded",
                "disallowed_tool",
                "product_error",
                "invalid_answer",
            ],
            run["$defs"]["failureReason"]["enum"],
        )
        self.assertEqual(0, run["properties"]["tool_output_tokens"]["minimum"])
        self.assertEqual(8, run["properties"]["tool_call_count"]["maximum"])
        for field in [
            "tool_output_bytes",
            "tool_output_tokens",
            "model_input_tokens",
            "model_output_tokens",
            "duplicate_calls",
            "uncited_tool_output_tokens",
        ]:
            self.assertEqual(0, run["properties"][field]["minimum"])
        self.assertNotIn("maximum", run["properties"]["tool_output_tokens"])

    def test_answer_actions_keep_repo_relative_paths_and_symbol_ids_outside_the_output_schema(self) -> None:
        for label, action in (
            (
                "path",
                {"kind": "inspect_file", "target": {"path": "/private/source.py"}},
            ),
            (
                "symbol",
                {"kind": "inspect_symbol", "target": {"symbol_id": "../private:symbol"}},
            ),
            (
                "reference site",
                {
                    "kind": "cite_reference_site",
                    "target": {
                        "reference_site": {
                            **_reference_site(),
                            "path": "/private/source.py",
                        }
                    },
                },
            ),
        ):
            with self.subTest(label=label):
                answer = _valid_v1_answer()
                answer["actions"] = [action]
                with self.assertRaisesRegex(ValueError, "repo-relative"):
                    StructuredAnswer.from_mapping(answer)

    def test_takeover_v1_schemas_freeze_semantics_without_exposing_labels(self) -> None:
        task = json.loads((BENCHMARK_ROOT / "task-manifest.schema.json").read_text(encoding="utf-8"))
        answer = json.loads((BENCHMARK_ROOT / "answer-schema.json").read_text(encoding="utf-8"))
        capabilities = [
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
        action_kinds = [
            "inspect_symbol",
            "inspect_file",
            "assemble_context",
            "trace_callers",
            "trace_callees",
            "trace_call_path",
            "cite_reference_site",
            "select_tests",
            "propose_edit",
            "propose_rename",
            "read_log",
            "query_pattern",
            "recover_workspace",
            "report_empty",
            "refuse_unsafe",
        ]

        self.assertIn("contract_id", task["properties"])
        self.assertIn("v1Task", task["$defs"])
        v1_task = task["$defs"]["v1Task"]
        self.assertTrue(
            {
                "capabilities",
                "expected_outcome",
                "acceptable_actions",
                "forbidden_actions",
                "reference_sites",
                "uncertainty_expectation",
            }.issubset(v1_task["required"])
        )
        self.assertEqual(
            "#/$defs/capability",
            v1_task["properties"]["capabilities"]["items"]["$ref"],
        )
        self.assertEqual(capabilities, task["$defs"]["capability"]["enum"])
        self.assertTrue(v1_task["properties"]["capabilities"]["uniqueItems"])
        self.assertEqual(
            ["success", "empty", "refusal"],
            v1_task["properties"]["expected_outcome"]["enum"],
        )
        self.assertEqual(
            ["must_resolve", "must_disclose", "must_refuse"],
            v1_task["properties"]["uncertainty_expectation"]["enum"],
        )
        self.assertEqual(action_kinds, task["$defs"]["actionKind"]["enum"])
        self.assertEqual(1, task["$defs"]["v1EvidenceAnchor"]["properties"]["relevance_grade"]["minimum"])
        self.assertEqual(3, task["$defs"]["v1EvidenceAnchor"]["properties"]["relevance_grade"]["maximum"])
        self.assertFalse(task["$defs"]["referenceSite"]["additionalProperties"])
        self.assertIn("target_symbol_id", task["$defs"]["referenceSite"]["properties"])
        self.assertFalse(task["$defs"]["acceptableAction"]["additionalProperties"])
        self.assertFalse(task["$defs"]["forbiddenAction"]["additionalProperties"])
        self.assertNotIn("regex", task["$defs"]["acceptableAction"]["properties"])
        self.assertNotIn("callback", task["$defs"]["acceptableAction"]["properties"])

        self.assertIn("contract_id", answer["properties"])
        self.assertIn("actions", answer["properties"])
        self.assertEqual(action_kinds, answer["$defs"]["actionKind"]["enum"])
        submitted_action = answer["$defs"]["submittedAction"]
        self.assertEqual({"kind", "target"}, set(submitted_action["properties"]))
        self.assertEqual({"kind", "target"}, set(submitted_action["required"]))
        self.assertFalse(submitted_action["additionalProperties"])
        for private_label_field in [
            "action_id",
            "requirement_group",
            "evidence_anchor_ids",
            "reference_site_ids",
            "reason",
        ]:
            self.assertNotIn(private_label_field, submitted_action["properties"])

    def test_visible_corpus_preserves_workflow_and_repo_language_breadth(self) -> None:
        tasks = load_task_manifest(BENCHMARK_ROOT / "dev-tasks.json")
        snapshots = load_snapshot_manifest(BENCHMARK_ROOT / "dev-snapshots.json")
        expected_classes = {
            "exact_lookup",
            "concept_search",
            "docs_config",
            "context_assembly",
            "references_trace",
            "impact_tests",
        }
        class_counts = {workflow: 0 for workflow in expected_classes}
        for task in tasks:
            class_counts[task.workflow_class] += 1
            expected_critical = task.workflow_class in {
                "exact_lookup",
                "references_trace",
                "impact_tests",
            }
            self.assertEqual(expected_critical, task.evidence_critical)

        self.assertEqual(15, len(tasks))
        self.assertTrue(all(count >= 2 for count in class_counts.values()))
        self.assertEqual(15, len({task.task_id for task in tasks}))
        self.assertGreaterEqual(len({(task.repo_id, task.language) for task in tasks}), 5)
        self.assertEqual(
            {snapshot.snapshot_id for snapshot in snapshots},
            {task.snapshot_id for task in tasks},
        )
        snapshot_languages = {snapshot.snapshot_id: set(snapshot.languages) for snapshot in snapshots}
        snapshot_repos = {snapshot.snapshot_id: snapshot.repo_id for snapshot in snapshots}
        self.assertTrue(
            all(task.language in snapshot_languages[task.snapshot_id] for task in tasks)
        )
        self.assertTrue(all(task.repo_id == snapshot_repos[task.snapshot_id] for task in tasks))

    def test_visible_corpus_covers_takeover_v1_capabilities_outcomes_and_actions(self) -> None:
        manifest = json.loads((BENCHMARK_ROOT / "dev-tasks.json").read_text(encoding="utf-8"))
        tasks = load_task_manifest(BENCHMARK_ROOT / "dev-tasks.json")
        expected_capabilities = {
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
        }

        self.assertEqual("takeover-evaluation-v1", manifest.get("contract_id"))
        self.assertEqual(expected_capabilities, {capability for task in tasks for capability in task.capabilities})
        self.assertEqual({"success", "empty", "refusal"}, {task.expected_outcome for task in tasks})

        action_kinds = {
            action.kind
            for task in tasks
            for action in (*task.acceptable_actions, *task.forbidden_actions)
        }
        self.assertTrue({"propose_edit", "propose_rename", "report_empty", "refuse_unsafe"}.issubset(action_kinds))
        ambiguity_tasks = [
            task
            for task in tasks
            if "homonym_disambiguation" in task.capabilities
            and task.reference_sites
            and task.forbidden_actions
        ]
        self.assertTrue(ambiguity_tasks)
        self.assertTrue(
            any(
                action.kind == "cite_reference_site"
                for task in ambiguity_tasks
                for action in task.acceptable_actions
            )
        )

    def test_visible_corpus_labels_resolve_in_declared_frozen_snapshots(self) -> None:
        manifest = json.loads((BENCHMARK_ROOT / "dev-tasks.json").read_text(encoding="utf-8"))
        snapshots = {
            snapshot.snapshot_id: snapshot
            for snapshot in load_snapshot_manifest(BENCHMARK_ROOT / "dev-snapshots.json")
        }
        source_root = Path.home() / "source"
        missing_repos = sorted(
            {
                snapshot.repo_id
                for snapshot in snapshots.values()
                if not (source_root / snapshot.repo_id / ".git").exists()
            }
        )
        if missing_repos:
            self.skipTest(f"visible source repositories unavailable: {', '.join(missing_repos)}")

        def blob_lines(snapshot_id: str, path: str) -> list[str]:
            snapshot = snapshots[snapshot_id]
            completed = subprocess.run(
                ["git", "-C", str(source_root / snapshot.repo_id), "show", f"{snapshot.commit}:{path}"],
                check=True,
                capture_output=True,
                text=True,
            )
            return completed.stdout.splitlines()

        for task in manifest["tasks"]:
            snapshot_id = task["snapshot_id"]
            for anchor in task["evidence_anchors"]:
                with self.subTest(task=task["task_id"], anchor=anchor["anchor_id"]):
                    lines = blob_lines(snapshot_id, anchor["path"])
                    if "line_start" in anchor:
                        self.assertLessEqual(anchor["line_end"], len(lines))
                        if "symbol" in anchor:
                            self.assertIn(anchor["symbol"], "\n".join(lines))
            for site in task.get("reference_sites", []):
                with self.subTest(task=task["task_id"], site=site["site_id"]):
                    lines = blob_lines(snapshot_id, site["path"])
                    self.assertLessEqual(site["line_end"], len(lines))
            for action in (
                *task.get("acceptable_actions", []),
                *task.get("forbidden_actions", []),
            ):
                target = action["target"]
                for field in ("path", "test_path"):
                    if field in target:
                        with self.subTest(task=task["task_id"], action=action["action_id"]):
                            blob_lines(snapshot_id, target[field])


if __name__ == "__main__":
    unittest.main()
