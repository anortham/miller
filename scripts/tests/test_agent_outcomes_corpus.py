import hashlib
import json
import sys
import tempfile
import unittest
from collections import defaultdict
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))

from benchlib.agent_outcomes_contract import (
    bind_verifier,
    load_json,
    public_response_schema,
    validate_task,
    validate_verifier,
)

CORPUS = SCRIPTS / "benchmarks" / "agent-outcomes"
WORKFLOWS = {
    "location",
    "concept",
    "references",
    "safe_edit",
    "repair",
    "test_selection",
}
CONCEPT_TARGETS = {
    "flask": "get_debug_flag",
    "express": "res.json",
    "chi": "urlparamfromctx",
    "ripgrep": "parse_human_readable_size",
    "command-line-api": "splitcommandline",
    "rake": "task#enhance",
}
EXPECTED_NATIVE_EXECUTIONS = {
    "flask": 494,
    "express": 1260,
    "chi": 290,
    "ripgrep": 1228,
    "command-line-api": 920,
    "rake": 606,
}


def load_tasks(path):
    records = []
    for line_number, line in enumerate(
        path.read_text(encoding="utf-8").splitlines(), 1
    ):
        if not line.strip():
            continue
        try:
            records.append(json.loads(line, object_pairs_hook=unique_object))
        except json.JSONDecodeError as error:
            raise ValueError(f"tasks line {line_number}: {error}") from error
    if not records:
        raise ValueError("corpus must contain tasks")
    return records


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"duplicate JSON key: {key}")
        value[key] = item
    return value


def assert_strict_schema(test, schema):
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
                    assert_strict_schema(test, property_schema)
            elif key not in {"required", "enum", "type", "description"}:
                assert_strict_schema(test, value)
    elif isinstance(schema, list):
        for value in schema:
            assert_strict_schema(test, value)


class AgentOutcomesCorpusTests(unittest.TestCase):
    def test_corpus_has_six_repositories_and_every_workflow(self):
        repositories = load_json(CORPUS / "repositories.json")
        tasks = load_tasks(CORPUS / "tasks.jsonl")

        self.assertEqual(6, len(repositories))
        self.assertEqual(36, len(tasks))
        self.assertEqual(2, sum(repo["split"] == "holdout" for repo in repositories))
        self.assertEqual(6, len({repo["language"] for repo in repositories}))
        self.assertEqual(6, len({repo["upstream"] for repo in repositories}))

        by_repo = defaultdict(list)
        for raw in tasks:
            task = validate_task(raw)
            by_repo[task.repo_id].append(task)
        self.assertEqual({repo["repo_id"] for repo in repositories}, set(by_repo))
        for records in by_repo.values():
            self.assertEqual(WORKFLOWS, {record.workflow for record in records})
            self.assertEqual(6, len(records))
        self.assertEqual(
            12,
            sum(
                task.workflow in {"safe_edit", "repair"}
                for values in by_repo.values()
                for task in values
            ),
        )

    def test_repository_identities_are_frozen_and_qualified(self):
        repositories = load_json(CORPUS / "repositories.json")
        required = {
            "repo_id",
            "upstream",
            "commit",
            "source_snapshot_sha256",
            "language",
            "license",
            "dependency_lock",
            "native_test",
            "split",
            "qualification",
        }
        for repo in repositories:
            self.assertEqual(required, set(repo))
            self.assertRegex(repo["commit"], r"^[0-9a-f]{40}$")
            self.assertRegex(repo["source_snapshot_sha256"], r"^[0-9a-f]{64}$")
            self.assertIn(repo["split"], {"development", "holdout"})
            self.assertTrue(repo["license"]["spdx"])
            self.assertIsInstance(repo["dependency_lock"]["present"], bool)
            self.assertEqual(0, repo["qualification"]["returncode"])
            self.assertRegex(repo["qualification"]["output_sha256"], r"^[0-9a-f]{64}$")

    def test_every_task_binds_to_a_hidden_exercised_verifier(self):
        tasks = load_tasks(CORPUS / "tasks.jsonl")
        verifier_records = load_json(CORPUS / "verifiers" / "verifiers.json")
        verifiers = {record["verifier_id"]: record for record in verifier_records}
        evidence_records = load_json(CORPUS / "verifiers" / "evidence.json")
        for raw in tasks:
            task = validate_task(raw)
            verifier_dir = CORPUS / "verifiers" / task.repo_id / task.verifier_id
            verifier = validate_verifier(verifiers[task.verifier_id])
            bind_verifier(task, verifier)
            evidence = evidence_records[task.verifier_id]
            self.assertTrue(evidence["positive"]["correct"])
            self.assertFalse(evidence["negative"]["correct"])
            self.assertRegex(evidence["positive"]["evidence_sha256"], r"^[0-9a-f]{64}$")
            self.assertRegex(evidence["negative"]["evidence_sha256"], r"^[0-9a-f]{64}$")
            if task.workflow in {"safe_edit", "repair"}:
                self.assertEqual("failed", evidence["baseline"]["outcome"])
                self.assertEqual("passed", evidence["reference"]["outcome"])
                self.assertEqual("failed", evidence["plausible_wrong"]["outcome"])
                self.assertNotEqual(0, evidence["baseline"]["returncode"])
                self.assertEqual(0, evidence["reference"]["returncode"])
                self.assertNotEqual(0, evidence["plausible_wrong"]["returncode"])
                self.assertTrue((verifier_dir / "reference.patch").is_file())
                self.assertTrue((verifier_dir / "plausible-wrong.patch").is_file())
                if task.workflow == "repair":
                    self.assertTrue((verifier_dir / "seed.patch").is_file())
            if task.workflow == "test_selection":
                inventory = load_json(verifier_dir / "case-inventory.json")
                self.assertGreaterEqual(len(inventory["cases"]), 2)
                self.assertEqual(
                    len(inventory["cases"]),
                    len(
                        {(case["path"], case["test_id"]) for case in inventory["cases"]}
                    ),
                )
                selected = {
                    (case["path"], case["test_id"])
                    for case in verifier.value["test_cases"]
                }
                available = {
                    (case["path"], case["test_id"]) for case in inventory["cases"]
                }
                self.assertTrue(selected)
                self.assertLessEqual(selected, available)

    def test_repair_seed_records_bind_patch_and_snapshot_separately(self):
        repositories = load_json(CORPUS / "repositories.json")
        tasks = {
            task["repo_id"]: task
            for task in load_tasks(CORPUS / "tasks.jsonl")
            if task["workflow"] == "repair"
        }
        for repo in repositories:
            seed = repo["qualification"]["repair_seed"]
            patch = CORPUS / seed["overlay_path"]
            self.assertEqual(
                seed["overlay_sha256"], hashlib.sha256(patch.read_bytes()).hexdigest()
            )
            self.assertEqual(
                seed["task_snapshot_sha256"], tasks[repo["repo_id"]]["snapshot_sha256"]
            )
            self.assertNotEqual(
                repo["source_snapshot_sha256"], seed["task_snapshot_sha256"]
            )

    def test_public_tasks_do_not_contain_hidden_labels(self):
        public = (CORPUS / "tasks.jsonl").read_text(encoding="utf-8")
        for forbidden in (
            "acceptable_alternatives",
            "baseline_files",
            "required_source_fragments",
            "test_argv",
            "test_cases",
        ):
            self.assertNotIn(forbidden, public)

    def test_concept_prompts_are_behavior_first(self):
        tasks = load_tasks(CORPUS / "tasks.jsonl")
        for task in tasks:
            if task["workflow"] == "concept":
                self.assertNotIn(
                    CONCEPT_TARGETS[task["repo_id"]], task["prompt"].casefold()
                )
                verifier_records = {
                    record["verifier_id"]: record
                    for record in load_json(CORPUS / "verifiers" / "verifiers.json")
                }
                for fact in verifier_records[task["verifier_id"]]["facts"]:
                    self.assertIn(fact["fact_id"], task["prompt"])

    def test_public_response_schemas_cover_every_corpus_workflow_and_are_strict(self):
        verifiers = {
            record["verifier_id"]: validate_verifier(record)
            for record in load_json(CORPUS / "verifiers" / "verifiers.json")
        }
        observed = set()
        for raw in load_tasks(CORPUS / "tasks.jsonl"):
            task = validate_task(raw)
            schema = public_response_schema(
                bind_verifier(task, verifiers[task.verifier_id])
            )
            json.dumps(schema, sort_keys=True, separators=(",", ":"))
            assert_strict_schema(self, schema)
            observed.add(task.workflow)
        self.assertEqual(WORKFLOWS, observed)

    def test_test_selection_uses_a_replayed_known_change(self):
        tasks = load_tasks(CORPUS / "tasks.jsonl")
        verifiers = {
            record["verifier_id"]: record
            for record in load_json(CORPUS / "verifiers" / "verifiers.json")
        }
        for task in tasks:
            if task["workflow"] != "test_selection":
                continue
            directory = CORPUS / "verifiers" / task["repo_id"] / task["verifier_id"]
            self.assertTrue((directory / "known-change.patch").is_file())
            selection = load_json(directory / "selection-evidence.json")
            self.assertEqual("completed", selection["baseline"]["outcome"])
            self.assertEqual("completed", selection["changed"]["outcome"])
            self.assertEqual(
                EXPECTED_NATIVE_EXECUTIONS[task["repo_id"]],
                selection["baseline"]["runner_execution_count"],
            )
            changed_or_unstable = {
                (case["path"], case["test_id"])
                for case in selection["outcome_transitions"]
                + selection["unstable_cases"]
            }
            self.assertEqual(
                selection["baseline"]["case_count"],
                selection["unchanged_case_count"] + len(changed_or_unstable),
            )
            self.assertEqual(
                verifiers[task["verifier_id"]]["test_cases"],
                selection["derived_impacted_cases"],
            )

    def test_native_verifiers_use_prepared_dependencies_and_execution_markers(self):
        environments = load_json(CORPUS / "verifiers" / "prepared-environments.json")
        self.assertEqual(6, len(environments))
        self.assertTrue((CORPUS / "verifiers" / "replay.py").is_file())
        evidence = load_json(CORPUS / "verifiers" / "evidence.json")
        execution_contracts = load_json(
            CORPUS / "verifiers" / "execution-contracts.json"
        )
        verifiers = load_json(CORPUS / "verifiers" / "verifiers.json")
        for verifier in verifiers:
            if verifier["kind"] != "mutation":
                continue
            command = " ".join(verifier["test_argv"])
            self.assertNotIn("npm install", command)
            self.assertNotIn("bundle install", command)
            self.assertNotIn("uv run", command)
            record = evidence[verifier["verifier_id"]]
            for state in ("baseline", "reference", "plausible_wrong"):
                self.assertTrue(record[state]["expected_test_executed"])
                self.assertRegex(record[state]["raw_stdout_sha256"], r"^[0-9a-f]{64}$")
                self.assertRegex(record[state]["raw_stderr_sha256"], r"^[0-9a-f]{64}$")
                stdout = Path(record[state]["raw_stdout_path"])
                stderr = Path(record[state]["raw_stderr_path"])
                if stdout.is_file() and stderr.is_file():
                    self.assertEqual(
                        record[state]["raw_stdout_sha256"],
                        hashlib.sha256(stdout.read_bytes()).hexdigest(),
                    )
                    self.assertEqual(
                        record[state]["raw_stderr_sha256"],
                        hashlib.sha256(stderr.read_bytes()).hexdigest(),
                    )
                    self.assertRegex(
                        stdout.read_text(encoding="utf-8")
                        + stderr.read_text(encoding="utf-8"),
                        execution_contracts[verifier["verifier_id"]][
                            "executed_patterns"
                        ][state],
                    )

    def test_external_replay_records_are_content_addressed(self):
        evidence = load_json(CORPUS / "verifiers" / "external-evidence.json")
        for name in (
            "mutation_replay",
            "selection_replay",
            "interrupted_setup_attempt",
        ):
            record = evidence[name]
            self.assertTrue(Path(record["path"]).is_absolute())
            self.assertRegex(record["sha256"], r"^[0-9a-f]{64}$")
            path = Path(record["path"])
            if path.is_file():
                self.assertEqual(
                    record["sha256"], hashlib.sha256(path.read_bytes()).hexdigest()
                )
        selection_path = Path(evidence["selection_replay"]["path"])
        if selection_path.is_file():
            tasks = {
                task["verifier_id"]: task for task in load_tasks(CORPUS / "tasks.jsonl")
            }
            for verifier_id, replay in load_json(selection_path).items():
                self.assertEqual(
                    tasks[verifier_id]["snapshot_sha256"],
                    replay["states"]["baseline"]["snapshot_sha256"],
                )
                self.assertNotEqual(
                    replay["states"]["baseline"]["snapshot_sha256"],
                    replay["states"]["changed"]["snapshot_sha256"],
                )
                for state in replay["states"].values():
                    output = Path(state["raw_output_path"])
                    self.assertTrue(output.is_file())
                    self.assertEqual(
                        state["raw_output_sha256"],
                        hashlib.sha256(output.read_bytes()).hexdigest(),
                    )
                    for artifact in state.get("result_artifacts", []):
                        artifact_path = Path(artifact["path"])
                        self.assertTrue(artifact_path.is_file())
                        self.assertEqual(
                            artifact["sha256"],
                            hashlib.sha256(artifact_path.read_bytes()).hexdigest(),
                        )

    def test_empty_corpus_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tasks.jsonl"
            path.write_text("\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "must contain tasks"):
                load_tasks(path)

    def test_duplicate_task_fields_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tasks.jsonl"
            path.write_text('{"task_id":"one","task_id":"two"}\n', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "duplicate JSON key"):
                load_tasks(path)


if __name__ == "__main__":
    unittest.main()
