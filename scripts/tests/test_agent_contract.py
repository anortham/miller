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
        ]
        for schema_name, manifest_name in pairs:
            with self.subTest(schema=schema_name):
                schema = json.loads((BENCHMARK_ROOT / schema_name).read_text(encoding="utf-8"))
                Draft202012Validator.check_schema(schema)
                self.assertFalse(schema.get("additionalProperties", True))
                if manifest_name:
                    manifest = json.loads((BENCHMARK_ROOT / manifest_name).read_text(encoding="utf-8"))
                    Draft202012Validator(schema).validate(manifest)

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

    def test_visible_corpus_is_balanced_and_uses_five_repo_language_families(self) -> None:
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

        self.assertEqual(12, len(tasks))
        self.assertEqual({workflow: 2 for workflow in expected_classes}, class_counts)
        self.assertEqual(12, len({task.task_id for task in tasks}))
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


if __name__ == "__main__":
    unittest.main()
