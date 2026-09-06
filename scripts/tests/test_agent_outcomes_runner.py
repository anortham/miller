import json
import os
import subprocess
import sys
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path
from unittest import mock

SCRIPTS_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS_ROOT))

from benchlib.agent_outcomes_contract import (
    bind_verifier,
    source_inventory,
    source_snapshot_sha256,
    validate_campaign,
    validate_task,
    validate_verifier,
)
from benchlib.agent_outcomes_runner import (
    NativeAgentRunner,
    PodmanVerificationExecutor,
    ProviderTransport,
    RunnerQualification,
    UnsafeLiveExecution,
    ZeroWorkObservation,
    _parse_result,
    _cleanup_container,
)


SHA = "a" * 64


class FakeRuntimeObserver:
    def __init__(self, snapshots):
        self.snapshots = iter(snapshots)
        self.calls = 0

    def snapshot(self):
        self.calls += 1
        return next(self.snapshots)


def campaign():
    return validate_campaign({
        "contract_id": "agent-outcomes-v1",
        "campaign_id": "fixture-campaign",
        "task_set_sha256": SHA,
        "host": {"name": "codex-cli", "version": "0.153.4", "binary_sha256": SHA},
        "model": {"model_id": "gpt-fixture", "reasoning": "medium"},
        "arms": [
            {"arm_id": "native", "runtime_identity": None, "runtime_qualification_sha256": None},
            {"arm_id": "native+miller-lexical", "runtime_identity": None, "runtime_qualification_sha256": None},
        ],
        "repetition_count": 1,
        "order_seed": 7,
        "platform_toolchain_image_sha256": SHA,
        "network_policy": "denied",
        "resource_limits": {"max_parallel_runs": 1, "memory_bytes": 1073741824},
        "approved_total_run_count": 2,
        "pricing": None,
        "approved_money_ceiling": None,
    })


def task(workflow="repair"):
    return validate_task({
        "contract_id": "agent-outcomes-v1",
        "task_id": "fixture-task",
        "repo_id": "fixture-repo",
        "source_commit": "b" * 40,
        "snapshot_sha256": SHA,
        "language": "python",
        "workflow": workflow,
        "prompt": "Repair the fixture and run its focused test.",
        "verifier_id": "fixture-verifier",
        "allowed_write_paths": ["src/fixture.py"] if workflow == "repair" else [],
        "max_wall_seconds": 60,
        "max_model_tokens": 2000,
    })


def mutation_task(snapshot):
    frozen_task = replace(task("repair"), snapshot_sha256=source_snapshot_sha256(snapshot))
    verifier = validate_verifier({
        "verifier_id": "fixture-verifier",
        "kind": "mutation",
        "expected_changed_paths": ["src/fixture.py"],
        "acceptance_test_paths": ["tests/test_fixture.py"],
        "forbidden_public_paths": [],
        "required_source_fragments": [{"path": "src/fixture.py", "text": "value = 2"}],
        "baseline_files": [dict(item) for item in source_inventory(snapshot)],
        "test_argv": [sys.executable, "-B", "-m", "unittest", "discover", "-s", "tests"],
    })
    return bind_verifier(frozen_task, verifier)


class NativeAgentRunnerTests(unittest.TestCase):
    def setUp(self):
        self.runner = NativeAgentRunner(
            campaign(),
            image_reference="localhost/agent-fixture@sha256:" + SHA,
            codex_path="/usr/local/bin/codex",
            miller_path="/opt/miller/miller",
        )

    def test_native_baseline_has_no_miller_server_and_retains_native_mutation_tools(self):
        command, prompt = self.runner.build_run(task(), "native", Path("/tmp/input"), Path("/tmp/output"))

        self.assertNotIn("Use only the benchmark MCP", prompt)
        self.assertNotIn("mcp_servers.miller", " ".join(command))
        self.assertEqual("workspace-write", self.runner.option(command, "--sandbox"))
        self.assertEqual("/run-config/response-schema.json", self.runner.option(command, "--output-schema"))
        self.assertIn("run the repository's focused tests", prompt)

    def test_answer_task_mounts_snapshot_read_only_and_uses_read_only_sandbox(self):
        command, _ = self.runner.build_run(task("concept"), "native", Path("/tmp/input"), Path("/tmp/output"))

        self.assertEqual("read-only", self.runner.option(command, "--sandbox"))
        self.assertIn("type=bind,src=/tmp/input,dst=/workspace,ro", command)

    def test_miller_is_the_only_paired_agent_configuration_difference(self):
        native, native_prompt = self.runner.build_agent_command(task(), "native")
        treatment, treatment_prompt = self.runner.build_agent_command(task(), "native+miller-lexical")

        self.assertEqual(native_prompt, treatment_prompt)
        self.assertEqual(native, [part for part in treatment if "mcp_servers.miller" not in part])

    def test_native_runtime_masks_miller_binary_while_treatment_keeps_it_visible(self):
        native, _ = self.runner.build_run(task(), "native", Path("/tmp/input"), Path("/tmp/output"))
        treatment, _ = self.runner.build_run(task(), "native+miller-lexical", Path("/tmp/input"), Path("/tmp/output"))

        self.assertTrue(any("dst=/opt/miller,ro" in part for part in native))
        self.assertFalse(any("dst=/opt/miller,ro" in part for part in treatment))

    def test_events_keep_model_usage_native_tools_and_miller_calls_separate(self):
        events = [
            {"type": "thread.started", "thread_id": "t1"},
            {"type": "item.completed", "item": {"type": "command_execution", "command": "rg fixture"}},
            {"type": "item.completed", "item": {"type": "file_change", "changes": []}},
            {"type": "item.completed", "item": {"type": "mcp_tool_call", "server": "miller", "tool": "search"}},
            {"type": "item.completed", "item": {"type": "agent_message", "text": "done"}},
            {"type": "turn.completed", "usage": {"input_tokens": 10, "cached_input_tokens": 4, "output_tokens": 3, "reasoning_output_tokens": 2}},
        ]

        parsed = self.runner.parse_events("\n".join(json.dumps(event) for event in events))

        self.assertEqual({"command": 1, "edit": 1}, parsed.native_tool_counts)
        self.assertEqual(1, parsed.miller_calls)
        self.assertEqual((10, 4, 3), parsed.model_tokens)
        self.assertEqual(2, parsed.reasoning_output_tokens)
        self.assertEqual("done", parsed.answer)
        self.assertIsNone(parsed.unsupported_reason)

    def test_missing_usage_is_unknown_not_zero(self):
        parsed = self.runner.parse_events(json.dumps({"type": "thread.started", "thread_id": "t1"}))
        self.assertEqual((None, None, None), parsed.model_tokens)

    def test_usage_sums_turns_without_double_counting_reasoning_output(self):
        events = [
            {"type": "turn.completed", "usage": {"input_tokens": 10, "cached_input_tokens": 4, "output_tokens": 3, "reasoning_output_tokens": 2}},
            {"type": "turn.completed", "usage": {"input_tokens": 7, "cached_input_tokens": 1, "output_tokens": 5, "reasoning_output_tokens": 4}},
        ]

        parsed = self.runner.parse_events("\n".join(json.dumps(event) for event in events))

        self.assertEqual((17, 5, 8), parsed.model_tokens)
        self.assertEqual(6, parsed.reasoning_output_tokens)

    def test_usage_is_unknown_when_any_completed_turn_has_no_usage(self):
        events = [
            {"type": "turn.completed", "usage": {"input_tokens": 10, "cached_input_tokens": 4, "output_tokens": 3}},
            {"type": "turn.completed"},
        ]

        parsed = self.runner.parse_events("\n".join(json.dumps(event) for event in events))

        self.assertEqual((None, None, None), parsed.model_tokens)

    def test_duplicate_completed_item_id_is_counted_once(self):
        line = json.dumps({"type": "item.completed", "item": {"id": "item-1", "type": "command_execution", "command": "rg fixture"}})

        parsed = self.runner.parse_events(line + "\n" + line)

        self.assertEqual({"command": 1}, parsed.native_tool_counts)

    def test_parser_rejects_malformed_event_and_usage_shapes_without_crashing(self):
        bad_lines = [
            "null",
            "[]",
            '{"type":"thread.started","type":"turn.started"}',
            '{"type":"turn.completed","usage":{"input_tokens":NaN,"cached_input_tokens":0,"output_tokens":0}}',
            json.dumps({"type": "item.completed", "item": None}),
            json.dumps({"type": "item.completed", "item": {"type": "command_execution", "command": None}}),
            json.dumps({"type": "item.completed", "item": {"type": "file_change", "changes": None}}),
            json.dumps({"type": "item.completed", "item": {"type": "agent_message", "text": None}}),
            json.dumps({"type": "turn.completed", "usage": []}),
            json.dumps({"type": "turn.completed", "usage": {"input_tokens": True, "cached_input_tokens": 0, "output_tokens": 0}}),
            json.dumps({"type": "turn.completed", "usage": {"input_tokens": -1, "cached_input_tokens": 0, "output_tokens": 0}}),
        ]

        parsed = self.runner.parse_events("\n".join(bad_lines))

        self.assertIsNotNone(parsed.unsupported_reason)
        self.assertEqual((None, None, None), parsed.model_tokens)

    def test_failure_lifecycle_is_explicit(self):
        parsed = self.runner.parse_events(json.dumps({"type": "turn.failed", "error": {"message": "failed"}}))
        self.assertTrue(parsed.failed)

    def test_failed_turn_makes_prior_usage_subtotal_publicly_unknown(self):
        events = [
            {"type": "turn.completed", "usage": {"input_tokens": 10, "cached_input_tokens": 4, "output_tokens": 3}},
            {"type": "turn.failed", "error": {"message": "failed"}},
        ]

        parsed = self.runner.parse_events("\n".join(json.dumps(event) for event in events))

        self.assertTrue(parsed.failed)
        self.assertEqual((None, None, None), parsed.model_tokens)

    def test_final_answer_rejects_duplicate_keys_nonfinite_and_nonobject_values(self):
        self.assertEqual({}, _parse_result('{"answer":1,"answer":2}'))
        self.assertEqual({}, _parse_result('{"answer":NaN}'))
        self.assertEqual({}, _parse_result("[]"))

    def test_unknown_event_is_preserved_and_marks_adapter_unsupported(self):
        line = json.dumps({"type": "future.event", "secret": "raw"})
        parsed = self.runner.parse_events(line)
        self.assertEqual("unknown event type: future.event", parsed.unsupported_reason)
        self.assertEqual((line,), parsed.raw_lines)

    def test_isolation_command_has_fixed_mounts_without_grader_or_container_socket(self):
        root = Path("/tmp/experiment")
        command = self.runner.build_isolation_probe(
            root,
            "unpredictable-sentinel",
            "secret",
            mutation=True,
            arm_id="native",
        )
        mounts = [command[index + 1] for index, part in enumerate(command[:-1]) if part == "--mount"]

        self.assertIn("type=bind,src=/tmp/experiment/task-input,dst=/workspace,rw", mounts)
        self.assertIn("type=bind,src=/tmp/experiment/agent-output,dst=/run-results,rw", mounts)
        self.assertFalse(any("private-grader" in mount for mount in mounts))
        self.assertFalse(any("podman.sock" in mount for mount in mounts))
        self.assertNotIn("--privileged", command)
        self.assertNotIn("--pid=host", command)
        self.assertNotIn("--network=host", command)
        script = command[-1]
        self.assertIn("if cat /tmp/experiment/private-grader/unpredictable-sentinel", script)
        self.assertIn("if cat /private-grader/unpredictable-sentinel", script)
        self.assertIn("printf %s secret > /workspace/", script)
        self.assertIn("test ! -e /opt/miller/miller", script)
        self.assertIn("sha256sum /usr/local/bin/codex", script)

    def test_isolation_probe_creates_its_own_unpredictable_sentinel(self):
        names = []
        for _ in range(2):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                (root / "task-input").mkdir()
                grader = root / "private-grader"
                grader.mkdir()
                runner = NativeAgentRunner(
                    campaign(),
                    image_reference="localhost/agent-fixture@sha256:" + SHA,
                    codex_path="/usr/local/bin/codex",
                    miller_path="/opt/miller/miller",
                    podman_path=str(SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman_create_fail.py"),
                )

                result = runner.qualify_isolation(root, mutation=False, arm_id="native")

                sentinels = list(grader.glob("sentinel-*"))
                self.assertFalse(result.passed)
                self.assertFalse((root / "agent-output").exists())
                self.assertTrue(Path(result.evidence_path).is_file())
                self.assertEqual(1, len(sentinels))
                self.assertEqual(64, len(sentinels[0].read_text(encoding="utf-8")))
                names.append(sentinels[0].name)
        self.assertNotEqual(names[0], names[1])

    def test_inspected_runtime_must_match_mount_network_and_resource_policy(self):
        root = Path("/tmp/experiment")
        runtime = root / "qualification-runtime" / "ro"
        mounts = [
            {"Source": str(root / "task-input"), "Destination": "/workspace", "RW": False},
            {"Source": str(root / "agent-output"), "Destination": "/run-results", "RW": True},
            {"Source": str(runtime), "Destination": "/runtime", "RW": True},
            {"Source": str(runtime / "miller"), "Destination": "/workspace/.miller", "RW": True},
            {"Source": str(runtime / "public-response-schema.json"), "Destination": "/run-config/response-schema.json", "RW": False},
            {"Source": str(runtime / "native-miller-mask"), "Destination": "/opt/miller", "RW": False},
        ]
        host_config = {
            "NetworkMode": "none",
            "Privileged": False,
            "PidMode": "",
            "Memory": 1073741824,
            "CapDrop": ["ALL"],
            "SecurityOpt": ["no-new-privileges"],
        }

        self.assertTrue(self.runner._inspect_matches_runtime(
            json.dumps([{"Mounts": mounts, "HostConfig": host_config, "ImageDigest": "sha256:" + SHA}]),
            root / "task-input",
            root / "agent-output",
            "ro",
            runtime,
            "native",
        ))
        host_config["NetworkMode"] = "host"
        self.assertFalse(self.runner._inspect_matches_runtime(
            json.dumps([{"Mounts": mounts, "HostConfig": host_config, "ImageDigest": "sha256:" + SHA}]),
            root / "task-input",
            root / "agent-output",
            "ro",
            runtime,
            "native",
        ))

    def test_container_cleanup_requires_explicit_container_exists_not_found(self):
        fixture = SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman_cleanup.py"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cidfile = root / "container.cid"
            cidfile.write_text("c" * 64, encoding="ascii")
            outcomes = {}
            for mode in ("notfound", "error", "exists"):
                executable = root / f"fake-podman-{mode}.py"
                executable.write_bytes(fixture.read_bytes())
                executable.chmod(0o755)
                outcomes[mode] = _cleanup_container(str(executable), cidfile)[0]

        self.assertEqual({"notfound": True, "error": False, "exists": False}, outcomes)

    def test_isolation_probe_cleans_captured_container_after_failed_or_timed_out_create(self):
        fixture = SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman_create_cid_failure.py"
        for mode in ("failure", "timeout"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                (root / "task-input").mkdir()
                (root / "private-grader").mkdir()
                executable = root / f"fake-podman-{mode}.py"
                executable.write_bytes(fixture.read_bytes())
                executable.chmod(0o755)
                runner = NativeAgentRunner(
                    campaign(),
                    image_reference="localhost/agent-fixture@sha256:" + SHA,
                    codex_path="/usr/local/bin/codex",
                    miller_path="/opt/miller/miller",
                    podman_path=str(executable),
                    probe_timeout_seconds=1,
                )

                result = runner.qualify_isolation(root, mutation=False, arm_id="native")

                operations = executable.with_suffix(".log").read_text(encoding="utf-8").splitlines()
                self.assertFalse(result.passed)
                self.assertFalse(executable.with_suffix(".container").exists())
                self.assertIn("stop", operations)
                self.assertIn("rm", operations)

    def test_fake_qualification_requires_no_auth_or_network(self):
        qualification = RunnerQualification.fake(self.runner)
        self.assertEqual((), qualification.environment_allowlist)
        self.assertEqual("denied", qualification.network_policy)
        self.assertFalse(qualification.auth_mounted)

    def test_qualification_is_bound_to_arm_mount_mode_and_experiment_root(self):
        with tempfile.TemporaryDirectory() as first, tempfile.TemporaryDirectory() as second:
            first_root = Path(first)
            qualification = RunnerQualification.fake(self.runner, first_root, mutation=False, arm_id="native")

            self.assertNotEqual(
                qualification.configuration_sha256,
                self.runner.qualification_configuration_sha256(Path(second), "ro", "native"),
            )
            self.assertNotEqual(
                qualification.configuration_sha256,
                self.runner.qualification_configuration_sha256(first_root, "rw", "native"),
            )
            self.assertNotEqual(
                qualification.configuration_sha256,
                self.runner.qualification_configuration_sha256(first_root, "ro", "native+miller-lexical"),
            )

    def test_provider_transport_rejects_injected_or_unqualified_values(self):
        invalid_urls = [
            "http://gateway.example",
            "https://user:secret@gateway.example",
            "https://gateway.example/path?token=secret",
            "https://gateway.example/path#fragment",
            "https://gateway.example/bad\npath",
            'https://gateway.example/"bad',
        ]
        for url in invalid_urls:
            with self.subTest(url=url), self.assertRaises(ValueError):
                ProviderTransport("fixture", url, SHA, "denied")
        with self.assertRaises(ValueError):
            ProviderTransport('bad"id', "https://gateway.example", SHA, "denied")
        with self.assertRaises(ValueError):
            ProviderTransport("fixture", "https://gateway.example", "not-a-digest", "denied")

    def test_fake_qualification_cannot_authorize_real_gateway(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "task-input"
            snapshot.mkdir()
            (root / "private-grader").mkdir()
            (snapshot / "fixture.py").write_text("value = 1\n", encoding="utf-8")
            qualified_task = replace(task("concept"), snapshot_sha256=source_snapshot_sha256(snapshot))
            runner = NativeAgentRunner(
                campaign(),
                image_reference="localhost/agent-fixture@sha256:" + SHA,
                codex_path="/usr/local/bin/codex",
                miller_path="/opt/miller/miller",
                provider_transport=ProviderTransport("fixture", "https://gateway.example", SHA, "denied"),
            )
            runner.qualification = RunnerQualification.fake(runner, root, mutation=False, arm_id="native")

            with self.assertRaisesRegex(UnsafeLiveExecution, "fake qualification"):
                runner.run(qualified_task, "native", snapshot, root / "agent-output")

    def test_constructed_os_qualification_cannot_replace_direct_probe(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "task-input"
            snapshot.mkdir()
            (root / "private-grader").mkdir()
            (snapshot / "fixture.py").write_text("value = 1\n", encoding="utf-8")
            qualified_task = replace(task("concept"), snapshot_sha256=source_snapshot_sha256(snapshot))
            runner = NativeAgentRunner(
                campaign(),
                image_reference="localhost/agent-fixture@sha256:" + SHA,
                codex_path="/usr/local/bin/codex",
                miller_path="/opt/miller/miller",
                provider_transport=ProviderTransport("fixture", "https://gateway.example", SHA, "denied"),
            )
            runner.qualification = replace(
                RunnerQualification.fake(runner, root, mutation=False, arm_id="native"),
                kind="os",
            )

            with self.assertRaisesRegex(UnsafeLiveExecution, "direct isolation probe"):
                runner.run(qualified_task, "native", snapshot, root / "agent-output")

    def test_fake_executable_records_argv_and_emits_supported_events_without_auth(self):
        with tempfile.TemporaryDirectory() as directory:
            capture = Path(directory) / "capture.json"
            environment = {"AGENT_OUTCOMES_FAKE_CAPTURE": str(capture)}
            completed = subprocess.run(
                [sys.executable, str(SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_codex.py"), "exec", "--json"],
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            captured = json.loads(capture.read_text(encoding="utf-8"))

        self.assertEqual(0, completed.returncode)
        self.assertEqual(["exec", "--json"], captured["argv"])
        self.assertIsNone(self.runner.parse_events(completed.stdout).unsupported_reason)

    def test_live_run_refuses_without_secretless_qualified_credential_transport(self):
        with self.assertRaisesRegex(UnsafeLiveExecution, "credential transport"):
            self.runner.run(task(), "native", Path("/tmp/input"), Path("/tmp/output"))

    def test_lexical_zero_work_uses_observed_processes_and_files(self):
        before = ZeroWorkObservation((), ())
        after = ZeroWorkObservation(("julie-semantic-sidecar",), ("/workspace/.miller/vectors.db",))
        with self.assertRaisesRegex(RuntimeError, "semantic or continuous-testing"):
            self.runner.assert_zero_work("native+miller-lexical", before, after)

    def test_public_record_allowlist_excludes_private_paths_prompts_and_secrets(self):
        record = {
            "contract_id": "agent-outcomes-v1",
            "campaign_sha256": SHA,
            "run_id": "fixture-run",
            "task_id": "fixture-task",
            "arm_id": "native",
            "repetition": 1,
            "order": 1,
            "outcome": "unsupported",
            "verifier_evidence_sha256": SHA,
            "wall_time_seconds": 0.1,
            "native_tool_counts": {"command": 1},
            "miller_calls": 0,
            "total_model_input_tokens": None,
            "total_model_cached_tokens": None,
            "total_model_output_tokens": None,
            "raw_event_sha256": SHA,
            "price_derived_cost": None,
        }
        self.assertEqual(record, self.runner.public_record(record))
        record["native_tool_counts"] = {"command": {"source": "private text"}}
        with self.assertRaises(ValueError):
            self.runner.public_record(record)

    def test_run_supervises_auth_free_fake_process_and_retains_raw_accounting(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "task-input"
            snapshot.mkdir()
            (root / "private-grader").mkdir()
            (snapshot / "fixture.py").write_text("value = 1\n", encoding="utf-8")
            qualified_task = replace(task("concept"), snapshot_sha256=source_snapshot_sha256(snapshot))
            fake_podman = SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman.py"
            runner = NativeAgentRunner(
                campaign(),
                image_reference="localhost/agent-fixture@sha256:" + SHA,
                codex_path="/usr/local/bin/codex",
                miller_path="/opt/miller/miller",
                podman_path=str(fake_podman),
                provider_transport=ProviderTransport("fixture", "https://fixture.invalid", SHA, "denied"),
            )
            runner.qualification = RunnerQualification.fake(runner, root, mutation=False, arm_id="native")
            output = root / "agent-output"

            result = runner.run(qualified_task, "native", snapshot, output)

            self.assertEqual("unsupported", result.run_record["outcome"])
            self.assertEqual({"command": 1, "edit": 1}, result.run_record["native_tool_counts"])
            self.assertEqual(10, result.run_record["total_model_input_tokens"])
            self.assertEqual(3, result.run_record["total_model_output_tokens"])
            self.assertEqual(2, result.execution["reasoning_output_tokens"])
            self.assertFalse((output / "raw-events.jsonl").exists())
            self.assertTrue(Path(result.execution["raw_events_path"]).is_file())
            self.assertTrue(Path(result.execution["private_envelope_path"]).is_file())

    def test_agent_output_symlinks_cannot_redirect_authoritative_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "task-input"
            snapshot.mkdir()
            grader = root / "private-grader"
            grader.mkdir()
            victim = grader / "victim.txt"
            victim.write_text("unchanged", encoding="utf-8")
            (snapshot / "fixture.py").write_text("value = 1\n", encoding="utf-8")
            qualified_task = replace(task("concept"), snapshot_sha256=source_snapshot_sha256(snapshot))
            runner = NativeAgentRunner(
                campaign(),
                image_reference="localhost/agent-fixture@sha256:" + SHA,
                codex_path="/usr/local/bin/codex",
                miller_path="/opt/miller/miller",
                podman_path=str(SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman_symlink.py"),
                provider_transport=ProviderTransport("fixture", "https://fixture.invalid", SHA, "denied"),
            )
            runner.qualification = RunnerQualification.fake(runner, root, mutation=False, arm_id="native")

            result = runner.run(qualified_task, "native", snapshot, root / "agent-output")

            self.assertEqual("unchanged", victim.read_text(encoding="utf-8"))
            self.assertNotEqual(root / "agent-output", Path(result.execution["raw_events_path"]).parent)

    def test_run_rejects_overlapping_experiment_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "task-input"
            snapshot.mkdir()
            (snapshot / "fixture.py").write_text("value = 1\n", encoding="utf-8")
            qualified_task = replace(task("concept"), snapshot_sha256=source_snapshot_sha256(snapshot))
            runner = NativeAgentRunner(
                campaign(),
                image_reference="localhost/agent-fixture@sha256:" + SHA,
                codex_path="/usr/local/bin/codex",
                miller_path="/opt/miller/miller",
                podman_path=str(SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman.py"),
                provider_transport=ProviderTransport("fixture", "https://fixture.invalid", SHA, "denied"),
            )
            runner.qualification = RunnerQualification.fake(runner, root, mutation=False, arm_id="native")

            with self.assertRaisesRegex(ValueError, "private-grader"):
                runner.run(qualified_task, "native", snapshot, root / "agent-output")

    def test_timeout_terminates_owned_process_group_and_retains_attempt(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "task-input"
            snapshot.mkdir()
            (root / "private-grader").mkdir()
            (snapshot / "fixture.py").write_text("value = 1\n", encoding="utf-8")
            qualified_task = replace(
                task("concept"),
                snapshot_sha256=source_snapshot_sha256(snapshot),
                max_wall_seconds=1,
                prompt="\U0001f600" * 20_000,
            )
            runner = NativeAgentRunner(
                campaign(),
                image_reference="localhost/agent-fixture@sha256:" + SHA,
                codex_path="/usr/local/bin/codex",
                miller_path="/opt/miller/miller",
                podman_path=str(SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman_timeout.py"),
                provider_transport=ProviderTransport("fixture", "https://fixture.invalid", SHA, "denied"),
            )
            runner.qualification = RunnerQualification.fake(runner, root, mutation=False, arm_id="native")
            output = root / "agent-output"
            result = runner.run(qualified_task, "native", snapshot, output)
            child_pid = int((output / "child.pid").read_text(encoding="utf-8"))

            self.assertEqual("timeout", result.run_record["outcome"])
            self.assertTrue(Path(result.execution["raw_events_path"]).exists())
            with self.assertRaises(ProcessLookupError):
                os.kill(child_pid, 0)

    def test_normal_agent_exit_cleans_remaining_owned_descendants_before_return(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "task-input"
            snapshot.mkdir()
            (root / "private-grader").mkdir()
            (snapshot / "fixture.py").write_text("value = 1\n", encoding="utf-8")
            qualified_task = replace(task("concept"), snapshot_sha256=source_snapshot_sha256(snapshot))
            runner = NativeAgentRunner(
                campaign(),
                image_reference="localhost/agent-fixture@sha256:" + SHA,
                codex_path="/usr/local/bin/codex",
                miller_path="/opt/miller/miller",
                podman_path=str(SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman_orphan.py"),
                provider_transport=ProviderTransport("fixture", "https://fixture.invalid", SHA, "denied"),
            )
            runner.qualification = RunnerQualification.fake(runner, root, mutation=False, arm_id="native")

            result = runner.run(qualified_task, "native", snapshot, root / "agent-output")
            child_pid = int((root / "agent-output" / "child.pid").read_text(encoding="utf-8"))

            self.assertTrue(result.execution["descendant_cleanup_performed"])
            with self.assertRaises(ProcessLookupError):
                os.kill(child_pid, 0)

    def test_launch_failure_retains_private_raw_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "task-input"
            snapshot.mkdir()
            (root / "private-grader").mkdir()
            (snapshot / "fixture.py").write_text("value = 1\n", encoding="utf-8")
            qualified_task = replace(task("concept"), snapshot_sha256=source_snapshot_sha256(snapshot))
            runner = NativeAgentRunner(
                campaign(),
                image_reference="localhost/agent-fixture@sha256:" + SHA,
                codex_path="/usr/local/bin/codex",
                miller_path="/opt/miller/miller",
                podman_path=str(root / "missing-podman"),
                provider_transport=ProviderTransport("fixture", "https://fixture.invalid", SHA, "denied"),
            )
            runner.qualification = RunnerQualification.fake(runner, root, mutation=False, arm_id="native")

            result = runner.run(qualified_task, "native", snapshot, root / "agent-output")

            self.assertEqual("infrastructure_void", result.run_record["outcome"])
            self.assertTrue(Path(result.execution["raw_events_path"]).is_file())
            self.assertTrue(Path(result.execution["stderr_path"]).is_file())

    def test_peak_memory_is_unknown_after_unrelated_larger_child_high_water(self):
        subprocess.run(
            [sys.executable, "-c", "value = bytearray(32 * 1024 * 1024); print(len(value))"],
            check=True,
            capture_output=True,
        )
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "task-input"
            snapshot.mkdir()
            (root / "private-grader").mkdir()
            (snapshot / "fixture.py").write_text("value = 1\n", encoding="utf-8")
            qualified_task = replace(task("concept"), snapshot_sha256=source_snapshot_sha256(snapshot))
            runner = NativeAgentRunner(
                campaign(),
                image_reference="localhost/agent-fixture@sha256:" + SHA,
                codex_path="/usr/local/bin/codex",
                miller_path="/opt/miller/miller",
                podman_path=str(SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman.py"),
                provider_transport=ProviderTransport("fixture", "https://fixture.invalid", SHA, "denied"),
            )
            runner.qualification = RunnerQualification.fake(runner, root, mutation=False, arm_id="native")

            result = runner.run(qualified_task, "native", snapshot, root / "agent-output")

            self.assertIsNone(result.execution["peak_process_memory_bytes"])

    def test_mutation_run_uses_disposable_copy_and_isolated_verifier_after_agent_exit(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "task-input"
            (snapshot / "src").mkdir(parents=True)
            (snapshot / "tests").mkdir()
            (root / "private-grader").mkdir()
            (snapshot / "src" / "fixture.py").write_text("value = 1\n", encoding="utf-8")
            (snapshot / "tests" / "test_fixture.py").write_text(
                "import pathlib\nimport unittest\n\n"
                "class FixtureTests(unittest.TestCase):\n"
                "    def test_value(self):\n"
                "        self.assertEqual('value = 2', pathlib.Path('src/fixture.py').read_text().strip())\n",
                encoding="utf-8",
            )
            fake_source = SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman_mutation.py"
            fake_podman = root / "fake-podman.py"
            fake_podman.write_bytes(fake_source.read_bytes())
            fake_podman.chmod(0o755)
            runner = NativeAgentRunner(
                campaign(),
                image_reference="localhost/agent-fixture@sha256:" + SHA,
                codex_path="/usr/local/bin/codex",
                miller_path="/opt/miller/miller",
                podman_path=str(fake_podman),
                provider_transport=ProviderTransport("fixture", "https://fixture.invalid", SHA, "denied"),
            )
            runner.qualification = RunnerQualification.fake(runner, root, mutation=True, arm_id="native+miller-lexical")

            result = runner.run(mutation_task(snapshot), "native+miller-lexical", snapshot, root / "agent-output")

            self.assertEqual("correct", result.run_record["outcome"])
            self.assertEqual("value = 1\n", (snapshot / "src" / "fixture.py").read_text(encoding="utf-8"))
            self.assertTrue(fake_podman.with_suffix(".agent-done").is_file())
            self.assertFalse(fake_podman.with_suffix(".container").exists())
            self.assertTrue(result.execution["container_cleanup_confirmed"])
            self.assertRegex(result.execution["public_response_schema_sha256"], r"^[0-9a-f]{64}$")
            self.assertRegex(result.execution["verifier_sha256"], r"^[0-9a-f]{64}$")
            self.assertFalse((Path(result.execution["candidate_root"]) / ".miller").exists())
            self.assertTrue((root / "runtime-artifacts" / "fixture-task-native-miller-lexical-r1-o1" / "miller" / "vectors.db").is_file())

    def test_mutation_grading_rejects_extra_source_changes_and_deleted_tests(self):
        for mode in ("extra", "delete"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                snapshot = root / "task-input"
                (snapshot / "src").mkdir(parents=True)
                (snapshot / "tests").mkdir()
                (root / "private-grader").mkdir()
                (snapshot / "src" / "fixture.py").write_text("value = 1\n", encoding="utf-8")
                (snapshot / "tests" / "test_fixture.py").write_text(
                    "import pathlib\nimport unittest\n\n"
                    "class FixtureTests(unittest.TestCase):\n"
                    "    def test_value(self):\n"
                    "        self.assertEqual('value = 2', pathlib.Path('src/fixture.py').read_text().strip())\n",
                    encoding="utf-8",
                )
                fake_source = SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman_mutation.py"
                fake_podman = root / f"fake-podman-{mode}.py"
                fake_podman.write_bytes(fake_source.read_bytes())
                fake_podman.chmod(0o755)
                runner = NativeAgentRunner(
                    campaign(),
                    image_reference="localhost/agent-fixture@sha256:" + SHA,
                    codex_path="/usr/local/bin/codex",
                    miller_path="/opt/miller/miller",
                    podman_path=str(fake_podman),
                    provider_transport=ProviderTransport("fixture", "https://fixture.invalid", SHA, "denied"),
                )
                runner.qualification = RunnerQualification.fake(
                    runner,
                    root,
                    mutation=True,
                    arm_id="native+miller-lexical",
                )

                result = runner.run(
                    mutation_task(snapshot),
                    "native+miller-lexical",
                    snapshot,
                    root / "agent-output",
                )

                self.assertEqual("incorrect", result.run_record["outcome"])

    def test_verifier_cleanup_runs_when_timeout_signal_races_with_process_exit(self):
        fixture = SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman_verifier_lifecycle.py"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            executable = root / "fake-podman-timeout.py"
            executable.write_bytes(fixture.read_bytes())
            executable.chmod(0o755)
            executor = PodmanVerificationExecutor(
                "localhost/agent-fixture@sha256:" + SHA,
                podman_path=str(executable),
            )

            with mock.patch(
                "benchlib.agent_outcomes_runner._terminate_process_group",
                side_effect=ProcessLookupError,
            ):
                result = executor.execute(["true"], root, 1)

            self.assertFalse(result.ran)
            self.assertFalse(executable.with_suffix(".container").exists())

    def test_verifier_normal_exit_cleans_orphaned_descendants_before_return(self):
        fixture = SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman_verifier_lifecycle.py"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            executable = root / "fake-podman-orphan.py"
            executable.write_bytes(fixture.read_bytes())
            executable.chmod(0o755)
            executor = PodmanVerificationExecutor(
                "localhost/agent-fixture@sha256:" + SHA,
                podman_path=str(executable),
            )

            result = executor.execute(["true"], root, 2)
            child_pid = int(executable.with_suffix(".child").read_text(encoding="ascii"))

            self.assertTrue(result.ran)
            self.assertFalse(executable.with_suffix(".container").exists())
            with self.assertRaises(ProcessLookupError):
                os.kill(child_pid, 0)

    def test_run_observes_zero_work_before_and_after_lexical_process(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "task-input"
            snapshot.mkdir()
            (root / "private-grader").mkdir()
            (snapshot / "fixture.py").write_text("value = 1\n", encoding="utf-8")
            qualified_task = replace(task("concept"), snapshot_sha256=source_snapshot_sha256(snapshot))
            observer = FakeRuntimeObserver([
                ZeroWorkObservation(("podman",), ()),
                ZeroWorkObservation(("podman",), ()),
            ])
            runner = NativeAgentRunner(
                campaign(),
                image_reference="localhost/agent-fixture@sha256:" + SHA,
                codex_path="/usr/local/bin/codex",
                miller_path="/opt/miller/miller",
                podman_path=str(SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman.py"),
                provider_transport=ProviderTransport("fixture", "https://fixture.invalid", SHA, "denied"),
                runtime_observer=observer,
            )
            runner.qualification = RunnerQualification.fake(runner, root, mutation=False, arm_id="native+miller-lexical")

            runner.run(qualified_task, "native+miller-lexical", snapshot, root / "agent-output")

            self.assertEqual(2, observer.calls)

    def test_zero_work_violation_retains_run_record_and_private_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "task-input"
            snapshot.mkdir()
            (root / "private-grader").mkdir()
            (snapshot / "fixture.py").write_text("value = 1\n", encoding="utf-8")
            qualified_task = replace(task("concept"), snapshot_sha256=source_snapshot_sha256(snapshot))
            observer = FakeRuntimeObserver([
                ZeroWorkObservation((), ()),
                ZeroWorkObservation(("julie-semantic-sidecar",), ("/workspace/.miller/vectors.db",)),
            ])
            runner = NativeAgentRunner(
                campaign(),
                image_reference="localhost/agent-fixture@sha256:" + SHA,
                codex_path="/usr/local/bin/codex",
                miller_path="/opt/miller/miller",
                podman_path=str(SCRIPTS_ROOT / "tests/fixtures/agent-outcomes/fake_podman.py"),
                provider_transport=ProviderTransport("fixture", "https://fixture.invalid", SHA, "denied"),
                runtime_observer=observer,
            )
            runner.qualification = RunnerQualification.fake(
                runner,
                root,
                mutation=False,
                arm_id="native+miller-lexical",
            )

            result = runner.run(qualified_task, "native+miller-lexical", snapshot, root / "agent-output")

            self.assertEqual("infrastructure_void", result.run_record["outcome"])
            self.assertTrue(Path(result.execution["raw_events_path"]).is_file())
            self.assertIn("semantic or continuous-testing", result.execution["zero_work_error"])


if __name__ == "__main__":
    unittest.main()
