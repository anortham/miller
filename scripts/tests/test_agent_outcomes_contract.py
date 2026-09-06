import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS_ROOT))

from benchlib.agent_outcomes_contract import (
    VerificationExecution,
    bind_verifier,
    load_json,
    public_response_schema,
    source_inventory,
    source_snapshot_sha256,
    validate_campaign,
    validate_run_record,
    validate_task,
    validate_verifier,
    verify_result,
)

SHA256 = "a" * 64
COMMIT = "b" * 40


def assert_strict_output_objects(test, schema):
    if isinstance(schema, dict):
        allowed = {
            "type",
            "description",
            "properties",
            "required",
            "additionalProperties",
            "items",
            "enum",
            "minItems",
            "maxItems",
            "minimum",
        }
        test.assertFalse(set(schema) - allowed)
        if schema.get("type") == "object":
            test.assertFalse(schema["additionalProperties"])
            test.assertEqual(
                set(schema.get("properties", {})), set(schema.get("required", []))
            )
        for key, value in schema.items():
            if key == "properties":
                for property_schema in value.values():
                    assert_strict_output_objects(test, property_schema)
            elif key not in {"required", "enum", "type", "description"}:
                assert_strict_output_objects(test, value)
    elif isinstance(schema, list):
        for value in schema:
            assert_strict_output_objects(test, value)


class CopyExecutor:
    def __init__(self) -> None:
        self.roots: list[Path] = []

    def execute(self, argv, candidate_root, timeout_seconds):
        self.roots.append(candidate_root)
        completed = subprocess.run(
            argv,
            cwd=candidate_root,
            check=False,
            capture_output=True,
            text=True,
            timeout=timeout_seconds,
        )
        return VerificationExecution(
            ran=True,
            returncode=completed.returncode,
            stdout=completed.stdout,
            stderr=completed.stderr,
        )


class DidNotRunExecutor:
    def execute(self, argv, candidate_root, timeout_seconds):
        return VerificationExecution(ran=False, returncode=None)


class LinkInspectingExecutor:
    def __init__(self) -> None:
        self.copied_root = None

    def execute(self, argv, candidate_root, timeout_seconds):
        self.copied_root = candidate_root
        file_link = candidate_root / "linked-service.py"
        directory_link = candidate_root / "linked-src"
        passed = (
            file_link.is_symlink()
            and directory_link.is_symlink()
            and file_link.resolve().is_relative_to(candidate_root)
            and directory_link.resolve().is_relative_to(candidate_root)
        )
        return VerificationExecution(ran=True, returncode=0 if passed else 1)


class AgentOutcomesContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()
        (self.root / "src").mkdir()
        (self.root / "tests").mkdir()
        (self.root / "src" / "service.py").write_text(
            "def save(value):\n    return value\n",
            encoding="utf-8",
        )
        self.snapshot_sha256 = self.fixture_snapshot_sha256()

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def task_mapping(self, workflow="concept", verifier_id="location-save"):
        return {
            "contract_id": "agent-outcomes-v1",
            "task_id": "task-001",
            "repo_id": "fixture-python",
            "source_commit": getattr(self, "source_commit", COMMIT),
            "snapshot_sha256": getattr(self, "snapshot_sha256", SHA256),
            "language": "python",
            "workflow": workflow,
            "prompt": "Find the save definition.",
            "verifier_id": verifier_id,
            "allowed_write_paths": [],
            "max_wall_seconds": 60,
            "max_model_tokens": 2000,
        }

    def location_task(self, path="src/service.py", name="save", line=1):
        task = validate_task(self.task_mapping(workflow="location"))
        verifier = validate_verifier(
            {
                "verifier_id": "location-save",
                "kind": "location",
                "locations": [
                    {
                        "path": path,
                        "name": name,
                        "signatures": ["def save(value)"],
                        "spans": [{"line_start": line, "line_end": line}],
                    }
                ],
            }
        )
        return bind_verifier(task, verifier)

    def test_all_six_workflows_validate(self):
        workflows = (
            "location",
            "concept",
            "references",
            "safe_edit",
            "repair",
            "test_selection",
        )
        for workflow in workflows:
            with self.subTest(workflow=workflow):
                self.assertEqual(
                    workflow,
                    validate_task(self.task_mapping(workflow=workflow)).workflow,
                )

    def test_correct_location_needs_no_product_symbol_id(self):
        task = self.location_task(path="src/service.py", name="save", line=1)
        result = {"path": "src/service.py", "name": "save", "line": 1}
        self.assertTrue(verify_result(task, result, self.root).correct)
        self.assertFalse(verify_result(task, {**result, "line": 99}, self.root).correct)

    def test_read_only_grading_rejects_changed_source_snapshot(self):
        task = self.location_task()
        (self.root / "src" / "service.py").write_text(
            "def save(value):\n    return str(value)\n",
            encoding="utf-8",
        )
        result = {"path": "src/service.py", "name": "save", "line": 1}
        checked = verify_result(task, result, self.root)
        self.assertFalse(checked.correct)
        self.assertTrue(
            any("snapshot_sha256" in failure for failure in checked.failures)
        )

    def test_signature_is_a_native_location_identity(self):
        task = self.location_task()
        result = {
            "path": "src/service.py",
            "signature": "def save(value)",
            "line": 1,
        }
        self.assertTrue(verify_result(task, result, self.root).correct)

    def test_concept_grading_requires_frozen_claim_and_evidence(self):
        task = validate_task(
            self.task_mapping(workflow="concept", verifier_id="concept-save")
        )
        verifier = validate_verifier(
            {
                "verifier_id": "concept-save",
                "kind": "concept",
                "claims": [
                    {
                        "claim_id": "claim-save",
                        "acceptable_alternatives": [
                            "save returns its value",
                            "save is an identity function",
                        ],
                        "evidence": [
                            {
                                "path": "src/service.py",
                                "name": "save",
                                "signatures": ["def save(value)"],
                                "spans": [{"line_start": 1, "line_end": 1}],
                            }
                        ],
                    }
                ],
            }
        )
        result = {
            "claims": ["save returns its value"],
            "evidence": [{"path": "src/service.py", "name": "save", "line": 1}],
        }
        bound = bind_verifier(task, verifier)
        self.assertTrue(verify_result(bound, result, self.root).correct)
        wrong = {**result, "claims": ["save does not return its value"]}
        self.assertFalse(verify_result(bound, wrong, self.root).correct)
        missing_evidence = {**result, "evidence": []}
        self.assertFalse(verify_result(bound, missing_evidence, self.root).correct)
        extra_wrong_evidence = {
            **result,
            "evidence": [
                *result["evidence"],
                {"path": "src/service.py", "name": "other", "line": 1},
            ],
        }
        self.assertFalse(verify_result(bound, extra_wrong_evidence, self.root).correct)

    def test_fact_concept_grading_is_typed_and_paraphrase_independent(self):
        task = validate_task(
            self.task_mapping(workflow="concept", verifier_id="concept-facts")
        )
        location = {
            "path": "src/service.py",
            "name": "save",
            "signatures": ["def save(value)"],
            "spans": [{"line_start": 1, "line_end": 1}],
        }
        verifier = validate_verifier(
            {
                "verifier_id": "concept-facts",
                "kind": "concept",
                "facts": [
                    {
                        "fact_id": "returns-input",
                        "expected": True,
                        "evidence": [location],
                    },
                    {
                        "fact_id": "result-kind",
                        "expected": "unchanged",
                        "evidence": [location],
                    },
                    {
                        "fact_id": "traits",
                        "expected": ["identity", "pure"],
                        "evidence": [location],
                    },
                ],
            }
        )
        result = {
            "facts": {
                "returns-input": True,
                "result-kind": "unchanged",
                "traits": ["pure", "identity"],
            },
            "evidence": [{"path": "src/service.py", "name": "save", "line": 1}],
        }
        bound = bind_verifier(task, verifier)
        self.assertTrue(verify_result(bound, result, self.root).correct)
        for wrong in (
            {**result, "facts": {**result["facts"], "returns-input": False}},
            {**result, "facts": {**result["facts"], "unknown-fact": True}},
            {**result, "facts": {"returns-input": True, "result-kind": "unchanged"}},
            {**result, "evidence": []},
        ):
            with self.subTest(wrong=wrong):
                self.assertFalse(verify_result(bound, wrong, self.root).correct)

    def test_public_response_schema_exposes_shape_without_expected_values(self):
        task = validate_task(
            self.task_mapping(workflow="concept", verifier_id="concept-facts")
        )
        verifier = validate_verifier(
            {
                "verifier_id": "concept-facts",
                "kind": "concept",
                "facts": [
                    {
                        "fact_id": "returns-input",
                        "expected": True,
                        "evidence": [
                            {
                                "path": "src/service.py",
                                "name": "save",
                                "signatures": ["def save(value)"],
                                "spans": [{"line_start": 1, "line_end": 1}],
                            }
                        ],
                    }
                ],
            }
        )
        schema = public_response_schema(bind_verifier(task, verifier))
        encoded = json.dumps(schema, sort_keys=True, separators=(",", ":"))
        self.assertIn('"facts"', encoded)
        self.assertIn('"evidence"', encoded)
        self.assertIn("returns-input", encoded)
        self.assertNotIn('"expected"', encoded)
        self.assertNotIn('"const"', encoded)
        assert_strict_output_objects(self, schema)
        self.assertEqual(
            encoded,
            json.dumps(
                public_response_schema(bind_verifier(task, verifier)),
                sort_keys=True,
                separators=(",", ":"),
            ),
        )

    def test_explicit_refusal_and_empty_results_are_distinct_from_missing_evidence(
        self,
    ):
        refusal_task = validate_task(
            self.task_mapping(workflow="concept", verifier_id="concept-refusal")
        )
        refusal = validate_verifier(
            {
                "verifier_id": "concept-refusal",
                "kind": "concept",
                "expected_status": "refused",
                "claims": [
                    {
                        "claim_id": "claim-refusal",
                        "acceptable_alternatives": [
                            "the source cannot establish the behavior"
                        ],
                        "evidence": [
                            {
                                "path": "src/service.py",
                                "name": "save",
                                "signatures": ["def save(value)"],
                                "spans": [{"line_start": 1, "line_end": 1}],
                            }
                        ],
                    }
                ],
            }
        )
        refusal_result = {
            "status": "refused",
            "claims": ["The source cannot establish the behavior."],
            "evidence": [{"path": "src/service.py", "name": "save", "line": 1}],
        }
        self.assertTrue(
            verify_result(
                bind_verifier(refusal_task, refusal), refusal_result, self.root
            ).correct
        )

        empty_task = validate_task(
            self.task_mapping(workflow="references", verifier_id="refs-empty")
        )
        empty = validate_verifier(
            {
                "verifier_id": "refs-empty",
                "kind": "references",
                "expected_status": "empty",
                "locations": [],
            }
        )
        bound = bind_verifier(empty_task, empty)
        self.assertTrue(
            verify_result(
                bound, {"status": "empty", "references": []}, self.root
            ).correct
        )
        self.assertFalse(verify_result(bound, {"references": []}, self.root).correct)

        empty_tests_task = validate_task(
            self.task_mapping(workflow="test_selection", verifier_id="tests-empty")
        )
        empty_tests = validate_verifier(
            {
                "verifier_id": "tests-empty",
                "kind": "test_selection",
                "expected_status": "empty",
                "test_cases": [],
            }
        )
        self.assertTrue(
            verify_result(
                bind_verifier(empty_tests_task, empty_tests),
                {"status": "empty", "tests": []},
                self.root,
            ).correct
        )

    def test_reference_and_test_selection_workflows_use_frozen_native_locations(self):
        location = {
            "path": "src/service.py",
            "name": "save",
            "signatures": ["def save(value)"],
            "spans": [{"line_start": 1, "line_end": 1}],
        }
        references = bind_verifier(
            validate_task(
                self.task_mapping(workflow="references", verifier_id="refs-save")
            ),
            validate_verifier(
                {
                    "verifier_id": "refs-save",
                    "kind": "references",
                    "locations": [location],
                }
            ),
        )
        reference_result = {
            "references": [{"path": "src/service.py", "name": "save", "line": 1}]
        }
        self.assertTrue(verify_result(references, reference_result, self.root).correct)

        test_path = self.root / "tests" / "test_service.py"
        test_path.write_text("def test_save():\n    pass\n", encoding="utf-8")
        self.snapshot_sha256 = self.fixture_snapshot_sha256()
        selection = bind_verifier(
            validate_task(
                self.task_mapping(workflow="test_selection", verifier_id="tests-save")
            ),
            validate_verifier(
                {
                    "verifier_id": "tests-save",
                    "kind": "test_selection",
                    "test_cases": [
                        {"path": "tests/test_service.py", "test_id": "test_save"}
                    ],
                }
            ),
        )
        self.assertTrue(
            verify_result(
                selection,
                {"tests": [{"path": "tests/test_service.py", "test_id": "test_save"}]},
                self.root,
            ).correct
        )
        self.assertFalse(
            verify_result(
                selection,
                {"tests": [{"path": "tests/test_service.py", "test_id": "test_other"}]},
                self.root,
            ).correct
        )

    def test_product_identity_cannot_be_the_only_acceptance_condition(self):
        with self.assertRaisesRegex(ValueError, "unknown field.*product_symbol_id"):
            validate_verifier(
                {
                    "verifier_id": "location-save",
                    "kind": "location",
                    "product_symbol_id": "python:src/service.py:save",
                }
            )

    def test_paths_reject_absolute_traversal_and_symlink_escape(self):
        for path in ("/tmp/service.py", "../service.py", "src/../service.py"):
            with self.subTest(path=path):
                result = {"path": path, "name": "save", "line": 1}
                self.assertFalse(
                    verify_result(self.location_task(), result, self.root).correct
                )

        outside = self.root.parent / f"{self.root.name}-outside.py"
        outside.write_text("def save(value):\n    return value\n", encoding="utf-8")
        try:
            os.symlink(outside, self.root / "src" / "escape.py")
            result = {"path": "src/escape.py", "name": "save", "line": 1}
            self.assertFalse(
                verify_result(
                    self.location_task(path="src/escape.py"), result, self.root
                ).correct
            )
        finally:
            outside.unlink()

        missing = {"path": "src/missing.py", "name": "save", "line": 1}
        checked = verify_result(
            self.location_task(path="src/missing.py"), missing, self.root
        )
        self.assertFalse(checked.correct)

    def test_source_inventory_preserves_safe_internal_links_and_rejects_bad_links(self):
        os.symlink("src/service.py", self.root / "linked-service.py")
        os.symlink("src", self.root / "linked-src")
        inventory = source_inventory(self.root)
        entries = {entry["path"]: entry for entry in inventory}
        self.assertEqual("src/service.py", entries["linked-service.py"]["link_target"])
        self.assertEqual("src", entries["linked-src"]["link_target"])
        self.assertEqual(
            hashlib.sha256(b"src/service.py").hexdigest(),
            entries["linked-service.py"]["sha256"],
        )
        before = source_snapshot_sha256(self.root)
        (self.root / "linked-service.py").unlink()
        os.symlink("src", self.root / "linked-service.py")
        self.assertNotEqual(before, source_snapshot_sha256(self.root))

        for name, target in (
            ("absolute", str((self.root / "src").resolve())),
            ("escape", "../outside"),
            ("dangling", "missing"),
        ):
            link = self.root / name
            os.symlink(target, link)
            with self.subTest(name=name), self.assertRaises(ValueError):
                source_inventory(self.root)
            link.unlink()
        cycle_a = self.root / "cycle-a"
        cycle_b = self.root / "cycle-b"
        os.symlink("cycle-b", cycle_a)
        os.symlink("cycle-a", cycle_b)
        with self.assertRaises(ValueError):
            source_inventory(self.root)

    def test_mutation_executor_copy_keeps_internal_links_inside_copy(self):
        os.symlink("src/service.py", self.root / "linked-service.py")
        os.symlink("src", self.root / "linked-src")
        baseline = list(source_inventory(self.root))
        self.snapshot_sha256 = source_snapshot_sha256(self.root)
        (self.root / "src" / "service.py").write_text(
            "def save(value):\n    return str(value)\n",
            encoding="utf-8",
        )
        task = validate_task(
            self.task_mapping(workflow="safe_edit", verifier_id="edit-linked")
            | {"allowed_write_paths": ["src/service.py"]}
        )
        verifier = validate_verifier(
            {
                "verifier_id": "edit-linked",
                "kind": "mutation",
                "expected_changed_paths": ["src/service.py"],
                "acceptance_test_paths": [],
                "forbidden_public_paths": [],
                "required_source_fragments": [
                    {"path": "src/service.py", "text": "return str(value)"}
                ],
                "baseline_files": baseline,
                "test_argv": [sys.executable, "-c", "raise SystemExit(0)"],
            }
        )
        executor = LinkInspectingExecutor()
        checked = verify_result(
            bind_verifier(task, verifier), {}, self.root, executor=executor
        )
        self.assertTrue(checked.correct, checked.failures)

    def test_task_and_result_reject_unknown_fields(self):
        task = self.task_mapping()
        task["symbol_id"] = "python:src/service.py:save"
        with self.assertRaisesRegex(ValueError, "unknown field.*symbol_id"):
            validate_task(task)
        result = {
            "path": "src/service.py",
            "name": "save",
            "line": 1,
            "confidence": 1.0,
        }
        checked = verify_result(self.location_task(), result, self.root)
        self.assertFalse(checked.correct)
        self.assertTrue(any("confidence" in failure for failure in checked.failures))

    def test_load_json_rejects_duplicate_keys(self):
        path = self.root / "duplicate.json"
        path.write_text('{"contract_id":"agent-outcomes-v1","contract_id":"other"}')
        with self.assertRaisesRegex(
            ValueError, "duplicate JSON object key: contract_id"
        ):
            load_json(path)
        nonfinite = self.root / "nonfinite.json"
        nonfinite.write_text('{"value":NaN}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "non-finite"):
            load_json(nonfinite)

    def test_campaign_validates_runtime_join_and_nullable_cost_contract(self):
        campaign = validate_campaign(valid_campaign())
        self.assertEqual(campaign.repetition_count, 3)

        record = valid_run_record()
        self.assertIsNone(validate_run_record(record).price_derived_cost)
        missing = dict(record)
        del missing["price_derived_cost"]
        with self.assertRaisesRegex(ValueError, "missing field.*price_derived_cost"):
            validate_run_record(missing)
        zero = dict(record)
        zero["price_derived_cost"] = 0
        self.assertEqual(0, validate_run_record(zero).price_derived_cost)

        infinite = dict(record)
        infinite["wall_time_seconds"] = float("inf")
        with self.assertRaisesRegex(ValueError, "finite"):
            validate_run_record(infinite)

        dotted = valid_campaign()
        dotted["model"]["model_id"] = "openai/gpt-5.2.codex"
        validate_campaign(dotted)
        lexical = valid_campaign()
        lexical["arms"][1] = {
            "arm_id": "native+miller-lexical",
            "runtime_identity": None,
            "runtime_qualification_sha256": None,
        }
        validate_campaign(lexical)
        for arm_id in (
            "native",
            "native+miller-lexical",
            "native+miller-semantic",
        ):
            record = valid_run_record()
            record["arm_id"] = arm_id
            validate_run_record(record)

    def test_published_schemas_accept_contract_examples_and_reject_unknown_fields(self):
        root = SCRIPTS_ROOT / "benchmarks" / "agent-outcomes"
        task_schema = json.loads(
            (root / "task.schema.json").read_text(encoding="utf-8")
        )
        campaign_schema = json.loads(
            (root / "campaign.schema.json").read_text(encoding="utf-8")
        )
        assert_schema_valid(self, task_schema, self.task_mapping(), task_schema)
        assert_schema_valid(self, campaign_schema, valid_campaign(), campaign_schema)
        validate_task(self.task_mapping())
        validate_campaign(valid_campaign())
        invalid = self.task_mapping() | {
            "product_symbol_id": "python:src/service.py:save"
        }
        self.assertTrue(schema_errors(task_schema, invalid, task_schema, "$"))
        with self.assertRaisesRegex(ValueError, "product_symbol_id"):
            validate_task(invalid)
        overlong = self.task_mapping() | {"prompt": "x" * 20_001}
        self.assertTrue(schema_errors(task_schema, overlong, task_schema, "$"))
        with self.assertRaisesRegex(ValueError, "20000"):
            validate_task(overlong)
        self.assertIn("runRecord", campaign_schema["$defs"])
        assert_schema_valid(
            self,
            campaign_schema["$defs"]["runRecord"],
            valid_run_record(),
            campaign_schema,
        )
        validate_run_record(valid_run_record())
        malformed = valid_run_record() | {"outcome": "success"}
        self.assertTrue(
            schema_errors(
                campaign_schema["$defs"]["runRecord"],
                malformed,
                campaign_schema,
                "$",
            )
        )
        with self.assertRaisesRegex(ValueError, "outcome"):
            validate_run_record(malformed)

    def test_campaign_rejects_semantic_arm_without_frozen_runtime_identity(self):
        campaign = valid_campaign()
        campaign["arms"][1]["runtime_identity"] = None
        with self.assertRaisesRegex(ValueError, "semantic arm.*runtime_identity"):
            validate_campaign(campaign)

    def test_malformed_event_identity_is_rejected(self):
        record = valid_run_record()
        record["outcome"] = "success"
        with self.assertRaisesRegex(ValueError, "outcome"):
            validate_run_record(record)
        record = valid_run_record()
        record["total_model_input_tokens"] = 0
        record["total_model_cached_tokens"] = None
        with self.assertRaisesRegex(ValueError, "token counts must all be null"):
            validate_run_record(record)

    def test_mutation_grading_uses_diff_and_an_explicit_isolated_executor(self):
        baseline = self.init_candidate_repository()
        (self.root / "src" / "service.py").write_text(
            "def save(value):\n    return str(value)\n",
            encoding="utf-8",
        )
        task = validate_task(
            self.task_mapping(workflow="safe_edit", verifier_id="edit-save")
            | {"allowed_write_paths": ["src/service.py"]}
        )
        verifier = validate_verifier(
            {
                "verifier_id": "edit-save",
                "kind": "mutation",
                "expected_changed_paths": ["src/service.py"],
                "acceptance_test_paths": ["tests/test_service.py"],
                "forbidden_public_paths": ["src/public.py"],
                "required_source_fragments": [
                    {"path": "src/service.py", "text": "return str(value)"}
                ],
                "baseline_files": baseline,
                "test_argv": [
                    sys.executable,
                    "-c",
                    "from pathlib import Path; Path('executor-proof').write_text('ok')",
                ],
            }
        )
        bound = bind_verifier(task, verifier)

        refused = verify_result(bound, {}, self.root)
        self.assertFalse(refused.correct)
        self.assertIn("isolated verification executor is required", refused.failures)

        executor = CopyExecutor()
        checked = verify_result(bound, {}, self.root, executor=executor)
        self.assertTrue(checked.correct, checked.failures)
        self.assertEqual(len(executor.roots), 1)
        self.assertNotEqual(executor.roots[0], self.root)
        self.assertFalse((self.root / "executor-proof").exists())
        not_run = verify_result(bound, {}, self.root, executor=DidNotRunExecutor())
        self.assertFalse(not_run.correct)
        self.assertIn("frozen test command did not run", not_run.failures)

    def test_mutation_grading_rejects_wrong_actions_without_trusting_result_prose(self):
        baseline = self.init_candidate_repository()
        (self.root / "src" / "public.py").write_text("PUBLIC = 2\n", encoding="utf-8")
        task = validate_task(
            self.task_mapping(workflow="repair", verifier_id="repair-save")
            | {"allowed_write_paths": ["src/service.py"]}
        )
        verifier = validate_verifier(
            {
                "verifier_id": "repair-save",
                "kind": "mutation",
                "expected_changed_paths": ["src/service.py"],
                "acceptance_test_paths": ["tests/test_service.py"],
                "forbidden_public_paths": ["src/public.py"],
                "required_source_fragments": [
                    {"path": "src/service.py", "text": "return str(value)"}
                ],
                "baseline_files": baseline,
                "test_argv": [sys.executable, "-c", "raise SystemExit(0)"],
            }
        )
        checked = verify_result(
            bind_verifier(task, verifier),
            {},
            self.root,
            executor=CopyExecutor(),
        )
        self.assertFalse(checked.correct)
        self.assertTrue(any("changed path" in failure for failure in checked.failures))

    def test_renaming_an_acceptance_test_is_detected_as_deletion(self):
        baseline = self.init_candidate_repository()
        (self.root / "tests" / "test_service.py").rename(
            self.root / "tests" / "renamed_service.py"
        )
        self.snapshot_sha256 = hashlib.sha256(
            json.dumps(baseline, sort_keys=True, separators=(",", ":")).encode()
        ).hexdigest()
        task = validate_task(
            self.task_mapping(workflow="repair", verifier_id="repair-tests")
            | {
                "allowed_write_paths": [
                    "tests/test_service.py",
                    "tests/renamed_service.py",
                ]
            }
        )
        verifier = validate_verifier(
            {
                "verifier_id": "repair-tests",
                "kind": "mutation",
                "expected_changed_paths": [
                    "tests/test_service.py",
                    "tests/renamed_service.py",
                ],
                "acceptance_test_paths": ["tests/test_service.py"],
                "forbidden_public_paths": [],
                "required_source_fragments": [
                    {"path": "src/service.py", "text": "return value"}
                ],
                "baseline_files": baseline,
                "test_argv": [sys.executable, "-c", "raise SystemExit(0)"],
            }
        )
        checked = verify_result(
            bind_verifier(task, verifier), {}, self.root, executor=CopyExecutor()
        )
        self.assertFalse(checked.correct)
        self.assertIn(
            "acceptance test was deleted: tests/test_service.py", checked.failures
        )

    def init_candidate_repository(self):
        (self.root / "src" / "public.py").write_text("PUBLIC = 1\n", encoding="utf-8")
        (self.root / "tests" / "test_service.py").write_text(
            "from src.service import save\n",
            encoding="utf-8",
        )
        baseline = []
        for path in sorted(self.root.rglob("*")):
            if path.is_file():
                baseline.append(
                    {
                        "path": path.relative_to(self.root).as_posix(),
                        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                    }
                )
        self.snapshot_sha256 = hashlib.sha256(
            json.dumps(baseline, sort_keys=True, separators=(",", ":")).encode()
        ).hexdigest()
        return baseline

    def fixture_snapshot_sha256(self):
        inventory = []
        for path in sorted(self.root.rglob("*")):
            if path.is_file():
                inventory.append(
                    {
                        "path": path.relative_to(self.root).as_posix(),
                        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                    }
                )
        return hashlib.sha256(
            json.dumps(inventory, sort_keys=True, separators=(",", ":")).encode()
        ).hexdigest()


def assert_schema_valid(test_case, schema, value, root_schema):
    errors = schema_errors(schema, value, root_schema, "$")
    test_case.assertEqual([], errors)


def schema_errors(schema, value, root_schema, path):
    if "$ref" in schema:
        target = root_schema
        for part in schema["$ref"].removeprefix("#/").split("/"):
            target = target[part]
        return schema_errors(target, value, root_schema, path)
    errors = []
    if "oneOf" in schema:
        matches = [
            not schema_errors(option, value, root_schema, path)
            for option in schema["oneOf"]
        ]
        if sum(matches) != 1:
            errors.append(f"{path}: oneOf matched {sum(matches)} branches")
            return errors
    if "allOf" in schema:
        for option in schema["allOf"]:
            errors.extend(schema_errors(option, value, root_schema, path))
    if "const" in schema and value != schema["const"]:
        errors.append(f"{path}: const mismatch")
    if "enum" in schema and value not in schema["enum"]:
        errors.append(f"{path}: enum mismatch")
    expected_types = schema.get("type")
    if expected_types is not None:
        if isinstance(expected_types, str):
            expected_types = [expected_types]
        if not any(schema_type_matches(kind, value) for kind in expected_types):
            errors.append(f"{path}: type mismatch")
            return errors
    if isinstance(value, dict):
        required = set(schema.get("required", ()))
        for key in sorted(required - set(value)):
            errors.append(f"{path}: missing {key}")
        properties = schema.get("properties", {})
        additional = schema.get("additionalProperties", True)
        for key, item in value.items():
            child = f"{path}.{key}"
            if key in properties:
                errors.extend(schema_errors(properties[key], item, root_schema, child))
            elif additional is False:
                errors.append(f"{child}: additional property")
            elif isinstance(additional, dict):
                errors.extend(schema_errors(additional, item, root_schema, child))
        if "propertyNames" in schema:
            for key in value:
                errors.extend(
                    schema_errors(
                        schema["propertyNames"], key, root_schema, f"{path}.<key>"
                    )
                )
    if isinstance(value, list):
        if len(value) < schema.get("minItems", 0):
            errors.append(f"{path}: too few items")
        if schema.get("uniqueItems"):
            rendered = [json.dumps(item, sort_keys=True) for item in value]
            if len(rendered) != len(set(rendered)):
                errors.append(f"{path}: duplicate items")
        if "items" in schema:
            for index, item in enumerate(value):
                errors.extend(
                    schema_errors(
                        schema["items"], item, root_schema, f"{path}[{index}]"
                    )
                )
    if isinstance(value, str):
        if len(value) < schema.get("minLength", 0):
            errors.append(f"{path}: too short")
        if "maxLength" in schema and len(value) > schema["maxLength"]:
            errors.append(f"{path}: too long")
        if "pattern" in schema and re.search(schema["pattern"], value) is None:
            errors.append(f"{path}: pattern mismatch")
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if "minimum" in schema and value < schema["minimum"]:
            errors.append(f"{path}: below minimum")
        if "exclusiveMinimum" in schema and value <= schema["exclusiveMinimum"]:
            errors.append(f"{path}: below exclusive minimum")
    return errors


def schema_type_matches(kind, value):
    return {
        "null": value is None,
        "object": isinstance(value, dict),
        "array": isinstance(value, list),
        "string": isinstance(value, str),
        "integer": isinstance(value, int) and not isinstance(value, bool),
        "number": isinstance(value, (int, float)) and not isinstance(value, bool),
        "boolean": isinstance(value, bool),
    }[kind]


def runtime_identity():
    return {
        "sidecar_commit": COMMIT,
        "binary_sha256": SHA256,
        "runtime_payload_sha256": SHA256,
        "model_id": "embedding-fixture",
        "model_sha256": SHA256,
        "model_manifest_sha256": SHA256,
        "miller_fixture_commit": COMMIT,
        "resolved_backend": "cpu",
        "process_mode": "broker",
        "served_dimensions": 384,
        "conformance_harness_sha256": SHA256,
        "throughput_harness_sha256": SHA256,
        "concurrency_harness_sha256": SHA256,
    }


def valid_campaign():
    return {
        "contract_id": "agent-outcomes-v1",
        "campaign_id": "campaign-001",
        "task_set_sha256": SHA256,
        "host": {"name": "codex", "version": "0.153.3", "binary_sha256": SHA256},
        "model": {"model_id": "gpt-fixture", "reasoning": "high"},
        "arms": [
            {
                "arm_id": "native",
                "runtime_identity": None,
                "runtime_qualification_sha256": None,
            },
            {
                "arm_id": "native+miller-semantic",
                "runtime_identity": runtime_identity(),
                "runtime_qualification_sha256": SHA256,
            },
        ],
        "repetition_count": 3,
        "order_seed": 1729,
        "platform_toolchain_image_sha256": SHA256,
        "network_policy": "denied",
        "resource_limits": {"max_parallel_runs": 1, "memory_bytes": 1073741824},
        "approved_total_run_count": 30,
        "pricing": None,
        "approved_money_ceiling": None,
    }


def valid_run_record():
    return {
        "contract_id": "agent-outcomes-v1",
        "campaign_sha256": SHA256,
        "run_id": "run-001",
        "task_id": "task-001",
        "arm_id": "native+miller-semantic",
        "repetition": 1,
        "order": 1,
        "outcome": "correct",
        "verifier_evidence_sha256": SHA256,
        "wall_time_seconds": 1.25,
        "native_tool_counts": {"shell": 2},
        "miller_calls": 0,
        "total_model_input_tokens": None,
        "total_model_cached_tokens": None,
        "total_model_output_tokens": None,
        "raw_event_sha256": SHA256,
        "price_derived_cost": None,
    }


if __name__ == "__main__":
    unittest.main()
