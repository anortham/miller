import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))

from benchlib.agent_outcomes_contract import (
    bind_verifier,
    source_snapshot_sha256,
    validate_task,
    validate_verifier,
)
from benchlib.agent_outcomes_controller import (
    ApprovalBudget,
    CtRunnerAttemptExecutor,
    NativeRunnerAttemptExecutor,
    balanced_attempt_plan,
    execute_campaign,
    freeze_campaign,
    load_strict_json,
    public_frozen_campaign,
    s1_join_projection,
    score_run,
    validate_frozen_campaign,
)

SHA = "a" * 64


class AgentOutcomesControllerTests(unittest.TestCase):
    def test_paired_order_is_deterministic_balanced_and_has_no_selective_reruns(self):
        first = balanced_attempt_plan(
            ["task-a", "task-b"], ["native", "native+miller-lexical"], 5, 19
        )
        second = balanced_attempt_plan(
            ["task-a", "task-b"], ["native", "native+miller-lexical"], 5, 19
        )

        self.assertEqual(first, second)
        self.assertEqual(20, len(first))
        for task_id in ("task-a", "task-b"):
            first_positions = [
                row["arm_id"]
                for row in first
                if row["task_id"] == task_id and row["order"] == 1
            ]
            self.assertLessEqual(
                abs(
                    first_positions.count("native")
                    - first_positions.count("native+miller-lexical")
                ),
                1,
            )
            for repetition in range(1, 6):
                pair = [
                    row
                    for row in first
                    if row["task_id"] == task_id and row["repetition"] == repetition
                ]
                self.assertEqual(
                    {"native", "native+miller-lexical"}, {row["arm_id"] for row in pair}
                )
                self.assertEqual({1, 2}, {row["order"] for row in pair})

    def test_freeze_binds_private_inputs_and_detects_source_or_verifier_drift(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            frozen = freeze_campaign(paths["config"], paths["frozen"])
            validate_frozen_campaign(frozen)

            paths["source"].joinpath("fixture.py").write_text(
                "value = 2\n", encoding="utf-8"
            )
            with self.assertRaisesRegex(ValueError, "source snapshot drift"):
                validate_frozen_campaign(frozen)

            paths["source"].joinpath("fixture.py").write_text(
                "value = 1\n", encoding="utf-8"
            )
            verifier = json.loads(paths["verifiers"].read_text(encoding="utf-8"))
            verifier[0]["claims"] = ["different"]
            paths["verifiers"].write_text(json.dumps(verifier), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "verifier manifest drift"):
                validate_frozen_campaign(frozen)

    def test_freeze_refuses_primary_campaign_below_five_repetitions(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory), repetitions=4)
            with self.assertRaisesRegex(ValueError, "at least five"):
                freeze_campaign(paths["config"], paths["frozen"])

    def test_approval_binds_both_digests_and_soft_ceiling_refuses_followup_after_overshoot(
        self,
    ):
        approval = {
            "campaign_sha256": SHA,
            "execution_envelope_sha256": "b" * 64,
            "approved_run_ceiling": 10,
            "approved_money_ceiling": 5.0,
            "run_root": "/tmp/agent-outcomes-fixture-run",
        }
        budget = ApprovalBudget(approval, SHA, "b" * 64, 10)
        budget.authorize_next()
        budget.record_completion(run_cost=6.0, usage_complete=True)
        with self.assertRaisesRegex(PermissionError, "money ceiling"):
            budget.authorize_next()

        missing = ApprovalBudget(approval, SHA, "b" * 64, 10)
        missing.record_completion(run_cost=None, usage_complete=False)
        with self.assertRaisesRegex(PermissionError, "usage is incomplete"):
            missing.authorize_next()

        with self.assertRaisesRegex(PermissionError, "execution envelope"):
            ApprovalBudget(approval, SHA, "c" * 64, 10)

    def test_approval_budget_counts_setup_before_first_attempt(self):
        approval = {
            "campaign_sha256": SHA,
            "execution_envelope_sha256": "b" * 64,
            "approved_run_ceiling": 10,
            "approved_money_ceiling": 5.0,
            "run_root": "/tmp/agent-outcomes-setup-budget",
        }
        setup = [
            {
                "environment_id": "env",
                "component_id": "restore",
                "cost": 6.0,
            }
        ]

        budget = ApprovalBudget(approval, SHA, "b" * 64, 10, setup)

        with self.assertRaisesRegex(PermissionError, "money ceiling"):
            budget.authorize_next()

    def test_public_export_omits_private_paths_and_hidden_verifier_data(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            frozen = freeze_campaign(paths["config"], paths["frozen"])

            exported = public_frozen_campaign(frozen)
            encoded = json.dumps(exported, sort_keys=True)

            self.assertNotIn(str(paths["source"]), encoded)
            self.assertNotIn(str(paths["verifiers"]), encoded)
            self.assertNotIn("value is one", encoded)
            self.assertIn("public_response_schema_sha256", encoded)

    def test_provider_transport_freeze_binds_credential_free_qualification(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            qualification = paths["root"] / "provider-qualification.json"
            qualification_value = {
                "schema": "agent-outcomes-provider-transport-v1",
                "provider_id": "fixture-gateway",
                "base_url": "https://gateway.invalid/v1",
                "network_policy": "denied",
                "passed": True,
            }
            qualification.write_text(
                json.dumps(qualification_value, sort_keys=True), encoding="utf-8"
            )
            config = json.loads(paths["config"].read_text(encoding="utf-8"))
            config["execution"]["provider_transport"] = {
                "provider_id": "fixture-gateway",
                "base_url": "https://gateway.invalid/v1",
                "qualification_path": str(qualification),
                "qualification_sha256": hashlib.sha256(
                    qualification.read_bytes()
                ).hexdigest(),
                "network_policy": "denied",
            }
            paths["config"].write_text(json.dumps(config), encoding="utf-8")

            frozen = freeze_campaign(paths["config"], paths["frozen"])
            public = public_frozen_campaign(frozen)

            self.assertNotIn("gateway.invalid", json.dumps(public))
            self.assertEqual(
                hashlib.sha256(qualification.read_bytes()).hexdigest(),
                frozen["execution_envelope"]["provider_transport"][
                    "qualification_sha256"
                ],
            )

    def test_live_run_refuses_missing_provider_transport(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            frozen = freeze_campaign(paths["config"], paths["frozen"])
            approval = {
                "campaign_sha256": frozen["campaign_sha256"],
                "execution_envelope_sha256": frozen["execution_envelope_sha256"],
                "approved_run_ceiling": 10,
                "approved_money_ceiling": None,
                "run_root": str(paths["root"] / "live"),
            }
            with self.assertRaisesRegex(RuntimeError, "provider transport"):
                execute_campaign(
                    frozen,
                    paths["root"] / "live",
                    dry_run=False,
                    approval=approval,
                )

    def test_ct_freeze_requires_explicit_enable_start_and_inventory_warmup(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            config = json.loads(paths["config"].read_text(encoding="utf-8"))
            config["execution"]["comparison_mode"] = "ct"
            paths["config"].write_text(json.dumps(config), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "enable/start/inventory warmup"):
                freeze_campaign(paths["config"], paths["frozen"])

    def test_ct_freeze_proves_exact_baseline_patch_paths_and_changed_snapshot(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            task = json.loads(paths["tasks"].read_text(encoding="utf-8"))
            task["workflow"] = "test_selection"
            task["prompt"] = "Select the affected native test."
            paths["tasks"].write_text(
                json.dumps(task, sort_keys=True) + "\n", encoding="utf-8"
            )
            verifier = {
                "verifier_id": "fixture-concept-v1",
                "kind": "test_selection",
                "test_cases": [{"path": "fixture.py", "test_id": "fixture::value"}],
            }
            paths["verifiers"].write_text(json.dumps([verifier]), encoding="utf-8")
            patch = paths["root"] / "known-change.patch"
            patch.write_text(
                "diff --git a/fixture.py b/fixture.py\n"
                "--- a/fixture.py\n"
                "+++ b/fixture.py\n"
                "@@ -1 +1 @@\n"
                "-value = 1\n"
                "+value = 2\n",
                encoding="utf-8",
            )
            changed = paths["root"] / "changed"
            changed.mkdir()
            changed.joinpath("fixture.py").write_text("value = 2\n", encoding="utf-8")
            config = json.loads(paths["config"].read_text(encoding="utf-8"))
            config["campaign"]["task_set_sha256"] = hashlib.sha256(
                json.dumps([task], sort_keys=True, separators=(",", ":")).encode()
            ).hexdigest()
            config["execution"]["comparison_mode"] = "ct"
            config["execution"]["ct_lifecycle"] = {
                "schema_version": 1,
                "enabled_arm": "native+miller-lexical",
                "command_timeout_seconds": 30,
                "readiness_timeout_seconds": 120,
                "poll_interval_seconds": 1.0,
            }
            config["execution"]["ct_known_changes"] = {
                "fixture-concept": {
                    "path": str(patch),
                    "sha256": hashlib.sha256(patch.read_bytes()).hexdigest(),
                    "changed_paths": ["fixture.py"],
                    "baseline_snapshot_sha256": task["snapshot_sha256"],
                    "changed_snapshot_sha256": source_snapshot_sha256(changed),
                    "expected_ct_test_case_ids": ["fixture/provider::value"],
                    "expected_baseline_ct_verdict": "green",
                    "expected_baseline_ct_failure_ids": [],
                    "qualification_evidence_sha256": SHA,
                }
            }
            config["execution"]["setup_components"] = [
                {
                    "environment_id": "fixture-ct",
                    "component_id": "warmup",
                    "bucket": "ct",
                    "applies_to_arms": ["native+miller-lexical"],
                    "cost": 0.0,
                    "wall_time_seconds": 1.0,
                    "evidence_sha256": SHA,
                }
            ]
            paths["config"].write_text(json.dumps(config), encoding="utf-8")

            frozen = freeze_campaign(paths["config"], paths["frozen"])

            self.assertEqual(
                source_snapshot_sha256(changed),
                frozen["execution_envelope"]["ct_known_changes"]["fixture-concept"][
                    "changed_snapshot_sha256"
                ],
            )
            self.assertEqual(
                ["fixture/provider::value"],
                frozen["execution_envelope"]["ct_known_changes"]["fixture-concept"][
                    "expected_ct_test_case_ids"
                ],
            )
            self.assertEqual(
                SHA,
                frozen["execution_envelope"]["ct_known_changes"]["fixture-concept"][
                    "qualification_evidence_sha256"
                ],
            )
            self.assertEqual(
                "green",
                frozen["execution_envelope"]["ct_known_changes"]["fixture-concept"][
                    "expected_baseline_ct_verdict"
                ],
            )
            self.assertEqual(
                [],
                frozen["execution_envelope"]["ct_known_changes"]["fixture-concept"][
                    "expected_baseline_ct_failure_ids"
                ],
            )

            change = config["execution"]["ct_known_changes"]["fixture-concept"]
            change["expected_ct_test_case_ids"] = ["duplicate", "duplicate"]
            paths["config"].write_text(json.dumps(config), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "expected test case IDs"):
                freeze_campaign(paths["config"], paths["frozen"])

            change["expected_ct_test_case_ids"] = ["fixture/provider::value"]
            change["qualification_evidence_sha256"] = "not-a-digest"
            paths["config"].write_text(json.dumps(config), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "qualification_evidence_sha256"):
                freeze_campaign(paths["config"], paths["frozen"])

            change["qualification_evidence_sha256"] = SHA
            change["expected_baseline_ct_verdict"] = "red"
            paths["config"].write_text(json.dumps(config), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "baseline failure IDs"):
                freeze_campaign(paths["config"], paths["frozen"])

    def test_secondary_freeze_binds_exact_semantic_identity_and_qualification_bytes(
        self,
    ):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            config = json.loads(paths["config"].read_text(encoding="utf-8"))
            identity = self.runtime_identity()
            qualification = paths["root"] / "semantic-qualification.json"
            qualification.write_text(
                json.dumps({"runtime_identity": identity}, sort_keys=True),
                encoding="utf-8",
            )
            qualification_sha = hashlib.sha256(qualification.read_bytes()).hexdigest()
            config["campaign"]["arms"] = [
                {
                    "arm_id": "native+miller-lexical",
                    "runtime_identity": None,
                    "runtime_qualification_sha256": None,
                },
                {
                    "arm_id": "native+miller-semantic",
                    "runtime_identity": identity,
                    "runtime_qualification_sha256": qualification_sha,
                },
            ]
            config["execution"]["comparison_mode"] = "secondary"
            config["execution"]["runtime_qualification_path"] = str(qualification)
            runtime_binding = paths["root"] / "semantic-image-binding.json"
            runtime_binding.write_text(
                json.dumps(
                    {
                        "schema": "agent-outcomes-semantic-image-binding-v1",
                        "image_digest": SHA,
                        "runtime_identity": identity,
                        "runtime_qualification_sha256": qualification_sha,
                        "observation_evidence_sha256": SHA,
                        "passed": True,
                    },
                    sort_keys=True,
                ),
                encoding="utf-8",
            )
            config["execution"]["semantic_runtime_binding_path"] = str(runtime_binding)
            config["execution"]["setup_components"] = [
                {
                    "environment_id": "fixture-semantic",
                    "component_id": "model-load",
                    "bucket": "semantic",
                    "applies_to_arms": ["native+miller-semantic"],
                    "cost": 0.0,
                    "wall_time_seconds": 1.0,
                    "evidence_sha256": SHA,
                }
            ]
            paths["config"].write_text(json.dumps(config), encoding="utf-8")

            frozen = freeze_campaign(paths["config"], paths["frozen"])
            self.assertEqual(
                qualification_sha,
                frozen["execution_envelope"]["runtime_qualification_sha256"],
            )

            wrong_binding = paths["root"] / "wrong-semantic-image-binding.json"
            wrong_binding.write_text(
                json.dumps(
                    {
                        "schema": "agent-outcomes-semantic-image-binding-v1",
                        "image_digest": SHA,
                        "runtime_identity": {**identity, "process_mode": "stdio"},
                        "runtime_qualification_sha256": qualification_sha,
                        "observation_evidence_sha256": SHA,
                        "passed": True,
                    }
                ),
                encoding="utf-8",
            )
            bad_config = json.loads(paths["config"].read_text(encoding="utf-8"))
            bad_config["execution"]["semantic_runtime_binding_path"] = str(
                wrong_binding
            )
            bad_path = paths["root"] / "bad-secondary.json"
            bad_path.write_text(json.dumps(bad_config), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "image binding does not match"):
                freeze_campaign(bad_path, paths["root"] / "bad-secondary.frozen.json")

            qualification.write_text(
                json.dumps({"runtime_identity": {**identity, "model_id": "changed"}}),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "qualification"):
                validate_frozen_campaign(frozen)

    def test_native_runner_adapter_builds_distinct_experiment_root_and_qualifies_same_runner(
        self,
    ):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source"
            source.mkdir()
            source.joinpath("fixture.py").write_text("value = 1\n", encoding="utf-8")
            calls = []

            class FakeRunner:
                qualification = None

                def qualify_isolation(
                    self, experiment_root, *, mutation, arm_id, repo_id
                ):
                    calls.append(("qualify", experiment_root, mutation, arm_id))
                    self_outer.assertEqual("fixture-repo", repo_id)
                    self.assert_layout(experiment_root)
                    return SimpleNamespace(
                        qualification=object(),
                        passed=True,
                        qualification_sha256=SHA,
                        evidence_path="/private/qualification.json",
                        prepared_setup=None,
                    )

                def run(self, task, arm_id, snapshot, output, *, repetition, order):
                    calls.append(("run", snapshot, output, repetition, order))
                    self.assert_layout(snapshot.parent)
                    self_outer.assertIsNotNone(self.qualification)
                    return SimpleNamespace(
                        run_record={"task_id": task.task.task_id},
                        execution={"private": True},
                    )

                @staticmethod
                def assert_layout(experiment_root):
                    if not (experiment_root / "task-input" / "fixture.py").is_file():
                        raise AssertionError("task input missing")
                    if not (experiment_root / "private-grader").is_dir():
                        raise AssertionError("private grader missing")

            self_outer = self
            adapter = NativeRunnerAttemptExecutor(lambda *_: FakeRunner())
            fixture_root = root / "fixture"
            fixture_root.mkdir()
            paths = self.fixture(fixture_root)
            tasks = json.loads(paths["verifiers"].read_text(encoding="utf-8"))
            task = validate_task(json.loads(paths["tasks"].read_text(encoding="utf-8")))
            bound = bind_verifier(task, validate_verifier(tasks[0]))

            result = adapter(bound, "native", source, root / "attempt", 1, 2)

            self.assertEqual("fixture-concept", result["record"]["task_id"])
            self.assertEqual(["qualify", "run"], [call[0] for call in calls])

    def test_ct_runner_adapter_routes_both_arms_through_runner_owned_changed_source_path(
        self,
    ):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source"
            source.mkdir()
            source.joinpath("fixture.py").write_text("value = 1\n", encoding="utf-8")
            changed_sha = "e" * 64
            calls = []
            self_outer = self

            class FakeRunner:
                qualification = None

                def qualify_isolation(self, _root, *, mutation, arm_id, repo_id):
                    calls.append(("qualify", arm_id, mutation, repo_id))
                    return SimpleNamespace(
                        qualification=object(),
                        passed=True,
                        qualification_sha256=SHA,
                        evidence_path="/private/probe.json",
                        prepared_setup=None,
                    )

                def build_ct_container_spec(
                    self,
                    _task,
                    arm_id,
                    _snapshot,
                    _output,
                    _runtime,
                    _cid,
                    _known,
                ):
                    calls.append(("build", arm_id))
                    return object()

                def run_ct(
                    self,
                    _supervisor,
                    _lifecycle,
                    _spec,
                    task,
                    arm_id,
                    *,
                    repetition,
                    order,
                ):
                    calls.append(("run", arm_id))
                    return SimpleNamespace(
                        run_record=self_outer.live_record(
                            frozen, task, arm_id, repetition, order
                        ),
                        execution={
                            "measured_snapshot_sha256": changed_sha,
                            "reasoning_output_tokens": 0,
                            "prepared_environment": None,
                            "private_envelope_path": "/private/ct.json",
                        },
                    )

            class FakeKnown:
                changed_snapshot_sha256 = changed_sha

                @classmethod
                def from_manifest(cls, _value):
                    return cls()

            class FakeLifecycle:
                @classmethod
                def from_manifest(cls, *_args, **_kwargs):
                    return cls()

            fixture_root = root / "fixture"
            fixture_root.mkdir()
            paths = self.fixture(fixture_root)
            frozen = freeze_campaign(paths["config"], paths["frozen"])
            verifier = json.loads(paths["verifiers"].read_text(encoding="utf-8"))[0]
            task = bind_verifier(
                validate_task(json.loads(paths["tasks"].read_text(encoding="utf-8"))),
                validate_verifier(verifier),
            )
            adapter = CtRunnerAttemptExecutor(
                lambda *_: FakeRunner(),
                {"schema_version": 1},
                {"fixture-concept": {}},
                "/opt/miller/miller",
            )
            with (
                mock.patch("benchlib.agent_outcomes_ct.CtKnownChange", FakeKnown),
                mock.patch("benchlib.agent_outcomes_ct.CtLifecycle", FakeLifecycle),
                mock.patch("benchlib.agent_outcomes_ct.PersistentCtAttemptSupervisor"),
            ):
                for index, arm_id in enumerate(("native", "native+miller-lexical"), 1):
                    result = adapter(
                        task,
                        arm_id,
                        source,
                        root / f"attempt-{index}",
                        1,
                        index,
                    )
                    self.assertEqual(arm_id, result["record"]["arm_id"])

            self.assertEqual(
                ["native", "native+miller-lexical"],
                [call[1] for call in calls if call[0] == "run"],
            )
            self.assertTrue(
                all(call[2] is True for call in calls if call[0] == "qualify")
            )

    def test_live_adapter_failure_is_appended_as_void_before_missing_usage_stops_followup(
        self,
    ):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            frozen = freeze_campaign(paths["config"], paths["frozen"])
            output = paths["root"] / "failed-live"
            approval = {
                "campaign_sha256": frozen["campaign_sha256"],
                "execution_envelope_sha256": frozen["execution_envelope_sha256"],
                "approved_run_ceiling": 10,
                "approved_money_ceiling": None,
                "run_root": str(output),
            }

            with self.assertRaisesRegex(PermissionError, "usage is incomplete"):
                execute_campaign(
                    frozen,
                    output,
                    dry_run=False,
                    approval=approval,
                    attempt_executor=lambda *args: (_ for _ in ()).throw(
                        RuntimeError("provider disconnected")
                    ),
                )

            rows = [
                json.loads(line)
                for line in (output / "attempts.jsonl")
                .read_text(encoding="utf-8")
                .splitlines()
            ]
            self.assertEqual(["dispatch", "completion"], [row["kind"] for row in rows])
            completion = rows[1]
            self.assertEqual("infrastructure_void", completion["record"]["outcome"])
            self.assertIsNone(completion["record"]["total_model_input_tokens"])
            ledger = json.loads(
                (output / "attempt-ledger.json").read_text(encoding="utf-8")
            )
            self.assertEqual("stopped", ledger["status"])
            self.assertEqual("usage_incomplete", ledger["stop_reason"])
            report = score_run(
                output, paths["root"] / "failed-report.json", bootstrap_samples=20
            )
            self.assertEqual("stopped", report["run_status"])

    def test_model_token_overshoot_is_recorded_then_stops_the_next_dispatch(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            frozen = freeze_campaign(paths["config"], paths["frozen"])
            output = paths["root"] / "overshoot-live"
            approval = self.approval(frozen, output)

            def executor(task, arm_id, _snapshot, _output, repetition, order):
                return {
                    "record": self.live_record(
                        frozen, task, arm_id, repetition, order, input_tokens=1001
                    ),
                    "execution": {"reasoning_output_tokens": 0},
                }

            with self.assertRaisesRegex(PermissionError, "model token ceiling"):
                execute_campaign(
                    frozen,
                    output,
                    dry_run=False,
                    approval=approval,
                    attempt_executor=executor,
                )

            ledger = json.loads(
                (output / "attempt-ledger.json").read_text(encoding="utf-8")
            )
            self.assertEqual(1, ledger["completed_attempt_count"])
            self.assertEqual("model_token_ceiling_overshot", ledger["stop_reason"])

    def test_score_rejects_dropped_or_duplicate_attempt_rows(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            frozen = freeze_campaign(paths["config"], paths["frozen"])
            run_root = paths["root"] / "run"
            execute_campaign(frozen, run_root, dry_run=True)
            final_path = run_root / "attempt-ledger.json"
            final = json.loads(final_path.read_text(encoding="utf-8"))
            final["attempts"].pop()
            final_path.write_text(json.dumps(final), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "append-only journal"):
                score_run(run_root, paths["root"] / "bad-report.json")

            execute_root = paths["root"] / "run-duplicate"
            execute_campaign(frozen, execute_root, dry_run=True)
            journal = execute_root / "attempts.jsonl"
            lines = journal.read_text(encoding="utf-8").splitlines()
            journal.write_text("\n".join(lines + [lines[-1]]) + "\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "matching dispatch"):
                score_run(execute_root, paths["root"] / "duplicate-report.json")

    def test_score_reconstructs_unresolved_dispatch_as_cost_unknown_void(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            frozen = freeze_campaign(paths["config"], paths["frozen"])
            run_root = paths["root"] / "interrupted"
            execute_campaign(frozen, run_root, dry_run=True)
            journal = run_root / "attempts.jsonl"
            lines = journal.read_text(encoding="utf-8").splitlines()
            journal.write_text("\n".join(lines[:-1]) + "\n", encoding="utf-8")
            final_path = run_root / "attempt-ledger.json"
            final = json.loads(final_path.read_text(encoding="utf-8"))
            final["status"] = "error"
            final["stop_reason"] = "controller_error"
            final["attempts"].pop()
            final["completed_attempt_count"] -= 1
            final["attempt_journal_sha256"] = hashlib.sha256(
                journal.read_bytes()
            ).hexdigest()
            final_path.write_text(json.dumps(final), encoding="utf-8")

            report = score_run(
                run_root,
                paths["root"] / "interrupted-report.json",
                bootstrap_samples=20,
            )

            self.assertEqual("unresolved_dispatch", report["stop_reason"])
            self.assertEqual(1, report["void_reasons"]["unresolved_dispatch"])
            self.assertIsNone(report["campaign_totals"]["total_cost"])

    def test_setup_applicability_gives_each_arm_full_cost_and_campaign_deduplicates_shared(
        self,
    ):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            config = json.loads(paths["config"].read_text(encoding="utf-8"))
            config["execution"]["setup_components"] = [
                {
                    "environment_id": "shared-image",
                    "component_id": "base",
                    "bucket": "shared",
                    "applies_to_arms": ["native", "native+miller-lexical"],
                    "cost": 4.0,
                    "wall_time_seconds": 2.0,
                    "evidence_sha256": SHA,
                },
                {
                    "environment_id": "lexical-runtime",
                    "component_id": "index",
                    "bucket": "miller-lexical",
                    "applies_to_arms": ["native+miller-lexical"],
                    "cost": 3.0,
                    "wall_time_seconds": 1.0,
                    "evidence_sha256": SHA,
                },
            ]
            paths["config"].write_text(json.dumps(config), encoding="utf-8")
            frozen = freeze_campaign(paths["config"], paths["frozen"])
            run_root = paths["root"] / "setup-run"
            execute_campaign(frozen, run_root, dry_run=True)

            report = score_run(
                run_root, paths["root"] / "setup-report.json", bootstrap_samples=20
            )

            self.assertEqual(4.0, report["arms"]["native"]["setup"]["cold_setup_cost"])
            self.assertEqual(
                7.0, report["arms"]["native+miller-lexical"]["setup"]["cold_setup_cost"]
            )
            self.assertEqual(7.0, report["campaign_setup"]["cold_setup_cost"])

    def test_dry_run_is_deterministic_preserves_failures_and_voids_and_scores(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            frozen = freeze_campaign(paths["config"], paths["frozen"])
            first = execute_campaign(frozen, paths["root"] / "run-one", dry_run=True)
            second = execute_campaign(frozen, paths["root"] / "run-two", dry_run=True)

            self.assertEqual(first["attempts"], second["attempts"])
            outcomes = {entry["record"]["outcome"] for entry in first["attempts"]}
            self.assertIn("incorrect", outcomes)
            self.assertIn("infrastructure_void", outcomes)
            report = score_run(
                paths["root"] / "run-one",
                paths["root"] / "report.json",
                bootstrap_samples=100,
            )
            self.assertEqual("inconclusive", report["paired_correctness"]["conclusion"])
            self.assertTrue(report["dry_run"])
            self.assertTrue(report["synthetic"])
            self.assertEqual(0.0, report["campaign_totals"]["total_cost"])
            self.assertEqual(
                report["campaign_totals"]["cost_coverage"]["total"],
                report["campaign_totals"]["cost_coverage"]["measured"],
            )
            with self.assertRaisesRegex(ValueError, "dry-run"):
                s1_join_projection(report)
            required_metrics = {
                "correctness",
                "sufficient_evidence",
                "calls",
                "tokens",
                "wall_time_seconds",
                "retries",
                "irrelevant_output",
                "retrieval_diagnostics",
                "fallback",
            }
            for arm in report["arms"].values():
                self.assertEqual(required_metrics, set(arm["metrics"]))
                self.assertTrue(
                    all(
                        isinstance(value, dict) and value
                        for value in arm["metrics"].values()
                    )
                )
            self.assertEqual(
                10,
                report["arms"]["native"]["attempt_count"]
                + report["arms"]["native+miller-lexical"]["attempt_count"],
            )

    def test_exact_cli_validate_freeze_run_and_score_contract(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            commands = [
                ["validate", "--tasks", str(paths["tasks"])],
                [
                    "freeze",
                    "--config",
                    str(paths["config"]),
                    "--output",
                    str(paths["frozen"]),
                ],
                [
                    "run",
                    "--campaign",
                    str(paths["frozen"]),
                    "--dry-run",
                    "--output",
                    str(paths["root"] / "run-dry"),
                ],
                [
                    "score",
                    "--run",
                    str(paths["root"] / "run-dry"),
                    "--output",
                    str(paths["root"] / "report-dry.json"),
                ],
            ]
            for command in commands:
                completed = subprocess.run(
                    [
                        sys.executable,
                        str(SCRIPTS / "bench-agent-outcomes.py"),
                        *command,
                    ],
                    cwd=SCRIPTS.parent,
                    text=True,
                    capture_output=True,
                    check=False,
                )
                self.assertEqual(0, completed.returncode, completed.stderr)
            self.assertTrue((paths["root"] / "report-dry.json").is_file())

    def test_approval_json_rejects_duplicate_keys_and_nonfinite_numbers(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "approval.json"
            path.write_text(
                '{"approved_run_ceiling":1,"approved_run_ceiling":2}', encoding="utf-8"
            )
            with self.assertRaisesRegex(ValueError, "duplicate JSON key"):
                load_strict_json(path)
            path.write_text('{"approved_money_ceiling":NaN}', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "non-finite"):
                load_strict_json(path)

    def test_approval_is_bound_to_one_exclusive_run_root(self):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            frozen = freeze_campaign(paths["config"], paths["frozen"])
            run_root = paths["root"] / "approved-live"
            approval = self.approval(frozen, run_root)

            def executor(task, arm_id, _snapshot, _output, repetition, order):
                return {
                    "record": self.live_record(frozen, task, arm_id, repetition, order),
                    "execution": {"reasoning_output_tokens": 0},
                }

            execute_campaign(
                frozen,
                run_root,
                dry_run=False,
                approval=approval,
                attempt_executor=executor,
            )
            report = score_run(
                run_root, paths["root"] / "approved-report.json", bootstrap_samples=20
            )
            joined = s1_join_projection(report)
            self.assertEqual("agent-outcomes-v1", joined["schema"])
            self.assertEqual(2, len(joined["arms"]))
            contaminated = json.loads(json.dumps(report))
            contaminated["private_path"] = "/secret/provider.json"
            with self.assertRaisesRegex(ValueError, "report fields"):
                s1_join_projection(contaminated)
            malformed = json.loads(json.dumps(report))
            malformed["arms"]["native"]["metrics"]["fallback"]["secret"] = "value"
            with self.assertRaisesRegex(ValueError, "fallback metric fields"):
                s1_join_projection(malformed)
            with self.assertRaises(FileExistsError):
                execute_campaign(
                    frozen,
                    run_root,
                    dry_run=False,
                    approval=approval,
                    attempt_executor=executor,
                )
            with self.assertRaisesRegex(PermissionError, "approved run_root"):
                execute_campaign(
                    frozen,
                    paths["root"] / "different-live",
                    dry_run=False,
                    approval=approval,
                    attempt_executor=executor,
                )

    def test_live_controller_constructs_credential_free_native_runner_from_frozen_envelope(
        self,
    ):
        with tempfile.TemporaryDirectory() as directory:
            paths = self.fixture(Path(directory))
            qualification = paths["root"] / "provider-qualification.json"
            qualification_value = {
                "schema": "agent-outcomes-provider-transport-v1",
                "provider_id": "fixture-gateway",
                "base_url": "https://gateway.invalid/v1",
                "network_policy": "denied",
                "passed": True,
            }
            qualification.write_text(
                json.dumps(qualification_value, sort_keys=True), encoding="utf-8"
            )
            config = json.loads(paths["config"].read_text(encoding="utf-8"))
            config["execution"]["provider_transport"] = {
                "provider_id": "fixture-gateway",
                "base_url": "https://gateway.invalid/v1",
                "qualification_path": str(qualification),
                "qualification_sha256": hashlib.sha256(
                    qualification.read_bytes()
                ).hexdigest(),
                "network_policy": "denied",
            }
            paths["config"].write_text(json.dumps(config), encoding="utf-8")
            frozen = freeze_campaign(paths["config"], paths["frozen"])
            output = paths["root"] / "default-live"
            approval = self.approval(frozen, output)
            self_outer = self

            class FakeRunner:
                def __init__(self, campaign, **kwargs):
                    self.campaign = campaign
                    self.kwargs = kwargs
                    self.qualification = None

                def qualify_isolation(self, _root, *, mutation, arm_id, repo_id):
                    self_outer.assertFalse(mutation)
                    self_outer.assertEqual("fixture-repo", repo_id)
                    return SimpleNamespace(
                        qualification=object(),
                        passed=True,
                        qualification_sha256=SHA,
                        evidence_path="/private/qualification.json",
                        prepared_setup=None,
                    )

                def run(self, task, arm_id, _snapshot, _output, *, repetition, order):
                    self_outer.assertEqual(
                        "fixture-gateway", self.kwargs["provider_transport"].provider_id
                    )
                    return SimpleNamespace(
                        run_record=self_outer.live_record(
                            frozen, task, arm_id, repetition, order
                        ),
                        execution={
                            "reasoning_output_tokens": 0,
                            "prepared_environment": None,
                            "private_envelope_path": "/private/execution.json",
                        },
                    )

            with mock.patch(
                "benchlib.agent_outcomes_runner.NativeAgentRunner", FakeRunner
            ):
                result = execute_campaign(
                    frozen, output, dry_run=False, approval=approval
                )

            self.assertEqual("completed", result["status"])
            self.assertEqual(10, result["completed_attempt_count"])

    def fixture(self, root, repetitions=5):
        source = root / "source"
        source.mkdir()
        source.joinpath("fixture.py").write_text("value = 1\n", encoding="utf-8")
        snapshot_sha = source_snapshot_sha256(source)
        task = {
            "contract_id": "agent-outcomes-v1",
            "task_id": "fixture-concept",
            "repo_id": "fixture-repo",
            "source_commit": "b" * 40,
            "snapshot_sha256": snapshot_sha,
            "language": "python",
            "workflow": "concept",
            "prompt": "Explain the fixture behavior.",
            "verifier_id": "fixture-concept-v1",
            "allowed_write_paths": [],
            "max_wall_seconds": 60,
            "max_model_tokens": 1000,
        }
        tasks = root / "tasks.jsonl"
        tasks.write_text(json.dumps(task, sort_keys=True) + "\n", encoding="utf-8")
        verifier = {
            "verifier_id": "fixture-concept-v1",
            "kind": "concept",
            "claims": [
                {
                    "claim_id": "value-behavior",
                    "acceptable_alternatives": ["value is one"],
                    "evidence": [
                        {
                            "path": "fixture.py",
                            "name": "value",
                            "signatures": ["value = 1"],
                            "spans": [{"line_start": 1, "line_end": 1}],
                        }
                    ],
                }
            ],
        }
        verifiers = root / "verifiers.json"
        verifiers.write_text(json.dumps([verifier], sort_keys=True), encoding="utf-8")
        task_set_sha = hashlib.sha256(
            json.dumps([task], sort_keys=True, separators=(",", ":")).encode()
        ).hexdigest()
        campaign = {
            "contract_id": "agent-outcomes-v1",
            "campaign_id": "fixture-primary",
            "task_set_sha256": task_set_sha,
            "host": {"name": "codex-cli", "version": "fixture", "binary_sha256": SHA},
            "model": {"model_id": "fixture/model", "reasoning": "medium"},
            "arms": [
                {
                    "arm_id": "native",
                    "runtime_identity": None,
                    "runtime_qualification_sha256": None,
                },
                {
                    "arm_id": "native+miller-lexical",
                    "runtime_identity": None,
                    "runtime_qualification_sha256": None,
                },
            ],
            "repetition_count": repetitions,
            "order_seed": 17,
            "platform_toolchain_image_sha256": SHA,
            "network_policy": "denied",
            "resource_limits": {"max_parallel_runs": 1, "memory_bytes": 1073741824},
            "approved_total_run_count": repetitions * 2,
            "pricing": None,
            "approved_money_ceiling": None,
        }
        config = {
            "campaign": campaign,
            "execution": {
                "comparison_mode": "primary",
                "task_manifest_path": str(tasks),
                "verifier_manifest_path": str(verifiers),
                "source_roots": {"fixture-concept": str(source)},
                "prepared_environments_path": None,
                "runtime_qualification_path": None,
                "semantic_runtime_binding_path": None,
                "image_reference": "localhost/fixture@sha256:" + SHA,
                "codex_path": "/usr/local/bin/codex",
                "miller_path": "/opt/miller/miller",
                "podman_path": "podman",
                "setup_components": [],
                "task_ids": ["fixture-concept"],
                "ct_lifecycle": None,
                "ct_known_changes": None,
                "sample_size_plan": {
                    "phase": "pilot",
                    "pilot_variance": None,
                    "approved_budget_sha256": SHA,
                },
                "provider_transport": None,
            },
        }
        config_path = root / "campaign.json"
        config_path.write_text(json.dumps(config, sort_keys=True), encoding="utf-8")
        return {
            "root": root,
            "source": source,
            "tasks": tasks,
            "verifiers": verifiers,
            "config": config_path,
            "frozen": root / "campaign.frozen.json",
        }

    @staticmethod
    def runtime_identity():
        return {
            "sidecar_commit": "c" * 40,
            "binary_sha256": SHA,
            "runtime_payload_sha256": SHA,
            "model_id": "fixture-embedding",
            "model_sha256": SHA,
            "model_manifest_sha256": SHA,
            "miller_fixture_commit": "d" * 40,
            "resolved_backend": "cpu",
            "process_mode": "broker",
            "served_dimensions": 384,
            "conformance_harness_sha256": SHA,
            "throughput_harness_sha256": SHA,
            "concurrency_harness_sha256": SHA,
        }

    @staticmethod
    def approval(frozen, run_root):
        return {
            "campaign_sha256": frozen["campaign_sha256"],
            "execution_envelope_sha256": frozen["execution_envelope_sha256"],
            "approved_run_ceiling": 10,
            "approved_money_ceiling": None,
            "run_root": str(run_root),
        }

    @staticmethod
    def live_record(frozen, task, arm_id, repetition, order, *, input_tokens=10):
        return {
            "contract_id": "agent-outcomes-v1",
            "campaign_sha256": frozen["campaign_sha256"],
            "run_id": f"{task.task.task_id}-{arm_id.replace('+', '-')}-r{repetition}",
            "task_id": task.task.task_id,
            "arm_id": arm_id,
            "repetition": repetition,
            "order": order,
            "outcome": "correct",
            "verifier_evidence_sha256": SHA,
            "wall_time_seconds": 1.0,
            "native_tool_counts": {},
            "miller_calls": 0,
            "total_model_input_tokens": input_tokens,
            "total_model_cached_tokens": 0,
            "total_model_output_tokens": 1,
            "raw_event_sha256": SHA,
            "price_derived_cost": None,
        }


if __name__ == "__main__":
    unittest.main()
