import hashlib
import importlib.util
import json
import os
import stat
import subprocess
import sys
import tempfile
import time
import unittest
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

SCRIPTS_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS_ROOT))
MODULE_PATH = SCRIPTS_ROOT / "bench-agent-efficiency.py"

from benchlib.agent_contract import BenchmarkTask, SnapshotIdentity, StructuredAnswer, VerificationResult
from benchlib.agent_runner import AgentArm, AgentRun, CodexAgentRunner


def _load_module():
    spec = importlib.util.spec_from_file_location("bench_agent_efficiency", MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _task(task_id: str, workflow_class: str = "concept_search") -> BenchmarkTask:
    return BenchmarkTask(
        task_id=task_id,
        repo_id="fixture",
        snapshot_id="snapshot-001",
        language="python",
        workflow_class=workflow_class,
        evidence_critical=workflow_class in {"exact_lookup", "references_trace", "impact_tests"},
        prompt=f"Prompt for {task_id}",
        fact_predicates=(),
        path_cited=(),
        symbol_cited=(),
        evidence_anchors=(),
        forbidden_claims=(),
        contract_id="takeover-evaluation-v1",
        capabilities=("discovery",),
        expected_outcome="success",
    )


def _snapshot(root: Path) -> SimpleNamespace:
    return SimpleNamespace(
        identity=SnapshotIdentity(
            snapshot_id="snapshot-001",
            repo_id="fixture",
            commit="a" * 40,
            content_sha256="b" * 64,
            languages=("python",),
        ),
        root=root,
    )


def _git(root: Path, *args: str) -> str:
    completed = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, check=True)
    return completed.stdout.strip()


def _create_clean_snapshot(
    root: Path,
    snapshot_id: str = "snapshot-001",
    repo_id: str = "fixture",
) -> SnapshotIdentity:
    root.mkdir()
    _git(root, "init", "-q")
    _git(root, "config", "user.name", "Agent Fixture")
    _git(root, "config", "user.email", "agent@example.test")
    (root / "fixture.py").write_text("VALUE = 1\n", encoding="utf-8")
    _git(root, "add", "fixture.py")
    _git(root, "commit", "-qm", "fixture")
    identity = SnapshotIdentity.capture(snapshot_id, repo_id, ("python",), root)
    (root / ".miller").mkdir()
    (root / ".julie").mkdir()
    return identity


def _write_executable(path: Path, body: str) -> None:
    path.write_text(f"#!{sys.executable}\n{body}", encoding="utf-8")
    path.chmod(path.stat().st_mode | stat.S_IXUSR)


def _wait_for_process_exit(pid: int) -> bool:
    deadline = time.monotonic() + 3
    while time.monotonic() < deadline:
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return True
        time.sleep(0.05)
    return False


def _runtime_identity(product: Path, snapshot_roots=None):
    snapshot_roots = snapshot_roots or {"snapshot-001": "snapshot"}
    digest = hashlib.sha256(product.read_bytes()).hexdigest()
    products = {}
    for name in ("miller", "julie"):
        readiness = {
            snapshot_id: {
                "ready": True,
                "workspace_identity": f"{name}-{root_name}-workspace",
                "index_identity": f"{name}-{root_name}-index",
                "vector_identity": f"{name}-{root_name}-vector",
                "model_identity": f"{name}-{root_name}-model",
            }
            for snapshot_id, root_name in snapshot_roots.items()
        }
        products[name] = {
            "command": [str(product), "serve", name],
            "version_command": [str(product), "version", name],
            "version": f"{name} 1.0.0",
            "readiness_commands": {
                snapshot_id: [str(product), "readiness", name]
                for snapshot_id in snapshot_roots
            },
            "readiness": readiness,
            "binary_path": str(product),
            "binary_sha256": digest,
            "commit": "c" * 40,
            "environment": {
                "JULIE_HOME": str(product.parent / "isolated-julie-home"),
            },
        }
    return {"schema_version": 1, "products": products}


def _proxy_events(path: Path, tool_calls: int = 1, tool_tokens: int = 10) -> None:
    events = []
    for sequence in range(1, tool_calls + 1):
        events.extend(
            [
                {
                    "event": "tool_call",
                    "sequence": sequence * 2 - 1,
                    "name": "search",
                    "arguments": {"query": f"query-{sequence}"},
                },
                {
                    "event": "tool_result",
                    "sequence": sequence * 2,
                    "name": "search",
                    "result": {"content": [{"type": "text", "text": "fixture result"}]},
                    "error": None,
                    "output_bytes": 14,
                    "output_tokens": tool_tokens,
                    "duration_ns": 2_000_000,
                },
            ]
        )
    path.write_text("".join(json.dumps(event) + "\n" for event in events), encoding="utf-8")


class ScriptedCodexAgentRunner(CodexAgentRunner):
    def __init__(self, outcomes):
        self.outcomes = {key: list(value) for key, value in outcomes.items()}
        self.calls = []

    def run(self, task, arm, snapshot, output_dir):
        output = Path(output_dir)
        output.mkdir(parents=True, exist_ok=False)
        repetition = len([call for call in self.calls if call[:2] == (task.task_id, arm.role)]) + 1
        self.calls.append((task.task_id, arm.role, repetition, task.prompt))
        classification = self.outcomes[(task.task_id, arm.role)].pop(0)
        proxy = output / "proxy-events.jsonl"
        _proxy_events(proxy)
        verification = VerificationResult(
            classification == "valid",
            () if classification == "valid" else ("failed",),
            (),
            observed_outcome=task.expected_outcome if classification == "valid" else "wrong_answer",
        )
        failure_reason = None if classification == "valid" else "incorrect"
        if classification in {"harness_failure", "product_failure"}:
            failure_reason = "product_error"
        return AgentRun(
            outcome=(
                "failed" if classification == "harness_failure"
                else "timeout" if classification == "product_failure"
                else "completed"
            ),
            classification=classification,
            failure_reason=failure_reason,
            answer=(
                StructuredAnswer(
                    status="answered",
                    answer="fixture answer",
                    evidence=(),
                    contract_id="takeover-evaluation-v1",
                )
                if classification not in {"harness_failure", "product_failure"}
                else None
            ),
            verification=verification,
            command_manifest_path=output / "command-manifest.json",
            codex_events_path=output / "codex-events.jsonl",
            proxy_events_path=proxy,
            stderr_path=output / "stderr.txt",
            diagnostics=(),
            model_input_tokens=20,
            model_output_tokens=5,
            wall_clock_ms=50,
            exit_code=0,
            child_home_removed=True,
        )


class DisallowedToolCodexAgentRunner(CodexAgentRunner):
    def __init__(self):
        self.calls = []

    def run(self, task, arm, snapshot, output_dir):
        output = Path(output_dir)
        output.mkdir(parents=True, exist_ok=False)
        self.calls.append((task.task_id, arm.role))
        proxy = output / "proxy-events.jsonl"
        _proxy_events(proxy)
        return AgentRun(
            outcome="disallowed_tool",
            classification="agent_insufficiency",
            failure_reason="disallowed_tool",
            answer=None,
            verification=VerificationResult(
                False,
                ("runner: disallowed item type function_call",),
                (),
                observed_outcome="wrong_answer",
            ),
            command_manifest_path=output / "command-manifest.json",
            codex_events_path=output / "codex-events.jsonl",
            proxy_events_path=proxy,
            stderr_path=output / "stderr.txt",
            diagnostics=(),
            model_input_tokens=20,
            model_output_tokens=5,
            wall_clock_ms=50,
            exit_code=0,
            child_home_removed=True,
            observed_outcome="wrong_answer",
        )


class BenchAgentEfficiencyTests(unittest.TestCase):
    def test_takeover_selection_is_capability_derived_and_byte_exact(self):
        module = _load_module()
        self.assertTrue(hasattr(module, "build_selection"))
        capabilities = (
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
        )
        tasks = tuple(
            replace(
                _task(f"dev-{index:03d}"),
                contract_id="takeover-evaluation-v1",
                capabilities=(capability,),
                expected_outcome="success",
            )
            for index, capability in enumerate(capabilities, start=1)
        )
        snapshots = (
            SnapshotIdentity("snapshot-001", "fixture", "a" * 40, "b" * 64, ("python",)),
        )
        parent_bytes = b'{"contract_id":"takeover-evaluation-v1","schema_version":1}\\n'
        snapshot_bytes = b'{"schema_version":1}\\n'
        selection = module.build_selection(
            tasks=tasks,
            snapshots=snapshots,
            parent_manifest_bytes=parent_bytes,
            snapshot_manifest_bytes=snapshot_bytes,
            corpus_role="calibration",
            decision_scope="subset",
            capability_ids=("rename", "discovery"),
        )
        expected_ids = ("dev-001", "dev-010")
        expected_ids_hash = hashlib.sha256(
            ("\n".join(expected_ids) + "\n").encode()
        ).hexdigest()
        payload = {
            "contract_id": "takeover-evaluation-v1",
            "schema_version": 1,
            "corpus_role": "calibration",
            "decision_scope": "subset",
            "parent_manifest_sha256": hashlib.sha256(parent_bytes).hexdigest(),
            "snapshot_manifest_sha256": hashlib.sha256(snapshot_bytes).hexdigest(),
            "selected_capability_ids": ["discovery", "rename"],
            "selected_task_count": 2,
            "selected_task_ids_sha256": expected_ids_hash,
        }
        expected_selection_hash = hashlib.sha256(
            json.dumps(payload, sort_keys=True, separators=(",", ":")).encode()
        ).hexdigest()

        self.assertEqual(expected_ids, tuple(task.task_id for task in selection.tasks))
        self.assertEqual(expected_ids, selection.selected_task_ids)
        self.assertEqual(expected_ids_hash, selection.selected_task_ids_sha256)
        self.assertEqual(expected_selection_hash, selection.selection_sha256)
        changed_parent = module.build_selection(
            tasks=tasks,
            snapshots=snapshots,
            parent_manifest_bytes=parent_bytes + b" ",
            snapshot_manifest_bytes=snapshot_bytes,
            corpus_role="calibration",
            decision_scope="subset",
            capability_ids=("rename", "discovery"),
        )
        changed_snapshot = module.build_selection(
            tasks=tasks,
            snapshots=snapshots,
            parent_manifest_bytes=parent_bytes,
            snapshot_manifest_bytes=snapshot_bytes + b" ",
            corpus_role="calibration",
            decision_scope="subset",
            capability_ids=("rename", "discovery"),
        )
        full_decision = module.build_selection(
            tasks=tasks,
            snapshots=snapshots,
            parent_manifest_bytes=parent_bytes,
            snapshot_manifest_bytes=snapshot_bytes,
            corpus_role="decision",
            decision_scope="full",
            capability_ids=(),
        )
        self.assertNotEqual(selection.selection_sha256, changed_parent.selection_sha256)
        self.assertNotEqual(selection.selection_sha256, changed_snapshot.selection_sha256)
        self.assertNotEqual(selection.selection_sha256, full_decision.selection_sha256)
        for selectors, expected in [
            (("unknown",), "unknown capability"),
            (("discovery", "discovery"), "duplicate capability"),
            ((), "requires at least one"),
        ]:
            with self.subTest(selectors=selectors), self.assertRaisesRegex(ValueError, expected):
                module.build_selection(
                    tasks=tasks,
                    snapshots=snapshots,
                    parent_manifest_bytes=parent_bytes,
                    snapshot_manifest_bytes=snapshot_bytes,
                    corpus_role="calibration",
                    decision_scope="subset",
                    capability_ids=selectors,
                )
        with self.assertRaisesRegex(ValueError, "full scope"):
            module.build_selection(
                tasks=tasks,
                snapshots=snapshots,
                parent_manifest_bytes=parent_bytes,
                snapshot_manifest_bytes=snapshot_bytes,
                corpus_role="calibration",
                decision_scope="full",
                capability_ids=("discovery",),
            )
        with self.assertRaisesRegex(ValueError, "all 13 capabilities"):
            module.build_selection(
                tasks=tasks[:-1],
                snapshots=snapshots,
                parent_manifest_bytes=parent_bytes,
                snapshot_manifest_bytes=snapshot_bytes,
                corpus_role="calibration",
                decision_scope="full",
                capability_ids=(),
            )

    def test_neutral_roles_are_the_only_execution_identity(self):
        module = _load_module()
        self.assertIn("role", AgentArm.__annotations__)
        baseline = AgentArm(
            role="baseline",
            adapter_name="adapter-a",
            product_command=("tool-a", "serve"),
        )
        candidate = AgentArm(
            role="candidate",
            adapter_name="adapter-b",
            product_command=("tool-b", "serve"),
        )
        orders = module.balanced_arm_orders(("dev-001", "dev-002"), 7)

        self.assertEqual({"baseline", "candidate"}, {baseline.role, candidate.role})
        self.assertTrue(
            all(set(order) == {"baseline", "candidate"} for order in orders.values())
        )
        with self.assertRaisesRegex(ValueError, "role"):
            AgentArm(
                role="miller",
                adapter_name="adapter-a",
                product_command=("tool",),
            )
        with self.assertRaisesRegex(ValueError, "exactly baseline and candidate"):
            module.execute_paired_tasks(
                tasks=(),
                snapshots={},
                arms={"baseline": baseline},
                runner=mock.Mock(),
                output_root=Path("/tmp/not-used"),
                seed=7,
                identity_sha256="a" * 64,
            )

    def test_legacy_runtime_adapter_is_explicit_and_never_decisional(self):
        module = _load_module()
        self.assertTrue(hasattr(module, "adapt_legacy_runtime"))
        with tempfile.TemporaryDirectory() as directory:
            product = Path(directory) / "product"
            product.write_text("fixture", encoding="utf-8")
            runtime = _runtime_identity(product)
        adapted = module.adapt_legacy_runtime(
            runtime,
            corpus_role="calibration",
            decision_scope="subset",
        )
        self.assertEqual("agent-efficiency-legacy-calibration", adapted["contract_id"])
        self.assertEqual({"baseline", "candidate"}, set(adapted["adapters"]))
        self.assertEqual("julie", adapted["adapters"]["baseline"]["adapter_name"])
        self.assertEqual("miller", adapted["adapters"]["candidate"]["adapter_name"])
        with self.assertRaisesRegex(ValueError, "legacy.*calibration"):
            module.adapt_legacy_runtime(
                runtime,
                corpus_role="decision",
                decision_scope="full",
            )

    def test_legacy_calibration_rows_execute_and_resume_with_neutral_roles(self):
        module = _load_module()
        task = replace(
            _task("dev-001"),
            contract_id=None,
            capabilities=(),
            expected_outcome=None,
        )
        arms = {
            "baseline": AgentArm("baseline", "julie", ("tool-a",)),
            "candidate": AgentArm("candidate", "miller", ("tool-b",)),
        }
        first_runner = ScriptedCodexAgentRunner(
            {
                ("dev-001", "baseline"): ["valid"],
                ("dev-001", "candidate"): ["valid"],
            }
        )

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = module.execute_paired_tasks(
                tasks=(task,),
                snapshots={"snapshot-001": _snapshot(root)},
                arms=arms,
                runner=first_runner,
                output_root=root / "raw",
                seed=19,
                identity_sha256="7" * 64,
            )
            resumed = module.execute_paired_tasks(
                tasks=(task,),
                snapshots={"snapshot-001": _snapshot(root)},
                arms=arms,
                runner=ScriptedCodexAgentRunner(
                    {
                        ("dev-001", "baseline"): [],
                        ("dev-001", "candidate"): [],
                    }
                ),
                output_root=root / "raw",
                seed=19,
                identity_sha256="7" * 64,
            )
            baseline_raw = json.loads(
                (
                    root
                    / "raw"
                    / "dev-001"
                    / "pair-01"
                    / "repetition-1"
                    / "baseline"
                    / "run-result.json"
                ).read_text(encoding="utf-8")
            )

        self.assertEqual("julie", baseline_raw["product"])
        self.assertNotIn("contract_id", baseline_raw)
        self.assertEqual(first.baseline_rows, resumed.baseline_rows)
        self.assertEqual(first.candidate_rows, resumed.candidate_rows)

    def test_takeover_runtime_roles_are_exact_and_duplicate_json_keys_fail_closed(self):
        module = _load_module()
        selection = SimpleNamespace(corpus_role="calibration", decision_scope="subset")
        runtime = {
            "contract_id": "takeover-evaluation-v1",
            "schema_version": 1,
            "adapters": {"baseline": {}, "candidate": {}},
        }
        self.assertEqual(runtime, module.normalize_runtime(runtime, selection))
        for adapters in (
            {"baseline": {}},
            {"baseline": {}, "candidate": {}, "shadow": {}},
        ):
            with self.subTest(adapters=adapters), self.assertRaisesRegex(
                ValueError, "exactly baseline and candidate"
            ):
                module.normalize_runtime({**runtime, "adapters": adapters}, selection)
        with self.assertRaisesRegex(ValueError, "duplicate JSON key: baseline"):
            module._load_json_no_duplicates(
                '{"contract_id":"takeover-evaluation-v1","schema_version":1,'
                '"adapters":{"baseline":{},"baseline":{},"candidate":{}}}'
            )

    def test_decision_private_paths_must_be_external_and_contained(self):
        module = _load_module()
        self.assertTrue(hasattr(module, "validate_decision_paths"))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            implementation = root / "implementation"
            snapshot = root / "snapshot"
            private = root / "operator-private"
            implementation.mkdir()
            snapshot.mkdir()
            private.mkdir()
            artifacts = [private / name for name in ("tasks.json", "snapshots.json", "runtime.json", "run")]

            module.validate_decision_paths(
                private_root=private,
                implementation_root=implementation,
                snapshot_roots=(snapshot,),
                artifact_paths=artifacts,
            )
            with self.assertRaisesRegex(ValueError, "implementation checkout"):
                module.validate_decision_paths(
                    private_root=implementation / "private",
                    implementation_root=implementation,
                    snapshot_roots=(snapshot,),
                    artifact_paths=artifacts,
                )
            with self.assertRaisesRegex(ValueError, "snapshot repository"):
                module.validate_decision_paths(
                    private_root=snapshot / "private",
                    implementation_root=implementation,
                    snapshot_roots=(snapshot,),
                    artifact_paths=artifacts,
                )
            with self.assertRaisesRegex(ValueError, "outside private root"):
                module.validate_decision_paths(
                    private_root=private,
                    implementation_root=implementation,
                    snapshot_roots=(snapshot,),
                    artifact_paths=(*artifacts[:-1], root / "escaped-run"),
                )

    @unittest.skipIf(os.name == "nt", "POSIX process-group orphan assertion")
    def test_preflight_process_cleanup_terminates_real_descendants_on_timeout_and_forced_shutdown(self):
        module = _load_module()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            timeout_pid = root / "timeout-child.pid"
            timeout_command = root / "timeout-command"
            _write_executable(
                timeout_command,
                """import pathlib
import subprocess
import sys
import time

child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
pathlib.Path(sys.argv[1]).write_text(str(child.pid), encoding="utf-8")
time.sleep(60)
""",
            )
            with self.assertRaisesRegex(ValueError, "timed out"):
                module._command_output((str(timeout_command), str(timeout_pid)), timeout=2.0)
            timeout_child = int(timeout_pid.read_text(encoding="utf-8"))

            probe_pid = root / "probe-child.pid"
            probe_command = root / "probe-command"
            _write_executable(
                probe_command,
                """import json
import pathlib
import subprocess
import sys

child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
pathlib.Path(sys.argv[1]).write_text(str(child.pid), encoding="utf-8")
for line in sys.stdin:
    request = json.loads(line)
    if request.get("method") == "initialize":
        result = {"protocolVersion": "2024-11-05", "capabilities": {}, "instructions": "fixture"}
        print(json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}), flush=True)
    elif request.get("method") == "tools/list":
        result = {"tools": [{"name": "search", "inputSchema": {"type": "object"}}]}
        print(json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}), flush=True)
""",
            )
            module._probe_mcp((str(probe_command), str(probe_pid)), root)
            probe_child = int(probe_pid.read_text(encoding="utf-8"))

            self.assertTrue(_wait_for_process_exit(timeout_child))
            self.assertTrue(_wait_for_process_exit(probe_child))

    def test_identity_command_default_timeout_covers_cold_product_start(self):
        module = _load_module()
        process = SimpleNamespace(returncode=0)
        process.communicate = mock.Mock(return_value=("ready", ""))

        with (
            mock.patch.object(module.subprocess, "Popen", return_value=process),
            mock.patch.object(module, "_terminate_process_tree"),
        ):
            self.assertEqual("ready", module._command_output(("probe",)))

        process.communicate.assert_called_once_with(timeout=30)

    def test_balanced_orders_are_seeded_repeatable_and_nearly_even(self):
        module = _load_module()
        task_ids = [f"dev-{index:03d}" for index in range(1, 13)]

        first = module.balanced_arm_orders(task_ids, 731)
        second = module.balanced_arm_orders(task_ids, 731)
        changed = module.balanced_arm_orders(task_ids, 732)

        self.assertEqual(first, second)
        self.assertNotEqual(first, changed)
        baseline_first = sum(order[0] == "baseline" for order in first.values())
        self.assertLessEqual(abs(baseline_first - (len(task_ids) - baseline_first)), 1)
        self.assertTrue(all(set(order) == {"baseline", "candidate"} for order in first.values()))

    def test_initial_agreement_runs_once_and_disagreement_runs_exactly_three_times(self):
        module = _load_module()
        tasks = (_task("dev-001"), _task("dev-002"))
        runner = ScriptedCodexAgentRunner(
            {
                ("dev-001", "baseline"): ["valid"],
                ("dev-001", "candidate"): ["valid"],
                ("dev-002", "baseline"): ["valid", "valid", "valid"],
                ("dev-002", "candidate"): ["agent_insufficiency", "valid", "agent_insufficiency"],
            }
        )
        arms = {
            "baseline": AgentArm("baseline", "fixture-a", ("miller", "serve")),
            "candidate": AgentArm("candidate", "fixture-b", ("julie",)),
        }

        with tempfile.TemporaryDirectory() as directory:
            result = module.execute_paired_tasks(
                tasks=tasks,
                snapshots={"snapshot-001": _snapshot(Path(directory))},
                arms=arms,
                runner=runner,
                output_root=Path(directory) / "raw",
                seed=731,
                identity_sha256="1" * 64,
            )
            order_manifest = json.loads((Path(directory) / "raw" / "arm-order.json").read_text(encoding="utf-8"))

        counts = {}
        for task_id, arm, _, _ in runner.calls:
            counts[(task_id, arm)] = counts.get((task_id, arm), 0) + 1
        self.assertEqual(counts[("dev-001", "baseline")], 1)
        self.assertEqual(counts[("dev-001", "candidate")], 1)
        self.assertEqual(counts[("dev-002", "baseline")], 3)
        self.assertEqual(counts[("dev-002", "candidate")], 3)
        self.assertEqual({row["repetition"] for row in result.baseline_rows if row["task_id"] == "dev-002"}, {1, 2, 3})
        self.assertEqual(set(order_manifest["orders"]), {"dev-001", "dev-002"})
        self.assertEqual(len({prompt for task_id, _, _, prompt in runner.calls if task_id == "dev-002"}), 1)

    def test_harness_void_retries_the_whole_pair_and_retains_append_only_reason(self):
        module = _load_module()
        task = _task("dev-001")
        runner = ScriptedCodexAgentRunner(
            {
                ("dev-001", "baseline"): ["harness_failure", "valid"],
                ("dev-001", "candidate"): ["valid", "valid"],
            }
        )
        arms = {
            "baseline": AgentArm("baseline", "fixture-a", ("miller", "serve")),
            "candidate": AgentArm("candidate", "fixture-b", ("julie",)),
        }

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result = module.execute_paired_tasks(
                tasks=(task,),
                snapshots={"snapshot-001": _snapshot(root)},
                arms=arms,
                runner=runner,
                output_root=root / "raw",
                seed=9,
                identity_sha256="2" * 64,
                void_ledger_path=root / "void-ledger.jsonl",
            )
            resumed_runner = ScriptedCodexAgentRunner(
                {("dev-001", "baseline"): [], ("dev-001", "candidate"): []}
            )
            resumed = module.execute_paired_tasks(
                tasks=(task,),
                snapshots={"snapshot-001": _snapshot(root)},
                arms=arms,
                runner=resumed_runner,
                output_root=root / "raw",
                seed=9,
                identity_sha256="2" * 64,
                void_ledger_path=root / "void-ledger.jsonl",
            )
            ledger = [json.loads(line) for line in (root / "void-ledger.jsonl").read_text(encoding="utf-8").splitlines()]

        self.assertEqual(len(runner.calls), 4)
        self.assertEqual(len(ledger), 1)
        self.assertEqual(ledger[0]["task_id"], "dev-001")
        self.assertEqual(ledger[0]["pair_attempt"], 1)
        self.assertEqual(len(result.baseline_rows), 1)
        self.assertEqual(len(result.candidate_rows), 1)
        self.assertEqual(resumed_runner.calls, [])
        self.assertEqual(resumed.baseline_rows, result.baseline_rows)

    def test_product_timeout_is_scored_without_voiding_the_pair(self):
        module = _load_module()
        task = _task("dev-001")
        runner = ScriptedCodexAgentRunner(
            {
                ("dev-001", "baseline"): ["product_failure"],
                ("dev-001", "candidate"): ["product_failure"],
            }
        )
        arms = {
            "baseline": AgentArm("baseline", "fixture-a", ("miller", "serve")),
            "candidate": AgentArm("candidate", "fixture-b", ("julie",)),
        }

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result = module.execute_paired_tasks(
                tasks=(task,),
                snapshots={"snapshot-001": _snapshot(root)},
                arms=arms,
                runner=runner,
                output_root=root / "raw",
                seed=9,
                identity_sha256="3" * 64,
                void_ledger_path=root / "void-ledger.jsonl",
            )

            self.assertFalse((root / "void-ledger.jsonl").exists())

        self.assertEqual(len(runner.calls), 2)
        self.assertEqual(
            (result.baseline_rows[0]["observed_outcome"], result.baseline_rows[0]["failure_reason"]),
            ("hard_error", "product_error"),
        )
        self.assertEqual(
            (result.candidate_rows[0]["observed_outcome"], result.candidate_rows[0]["failure_reason"]),
            ("hard_error", "product_error"),
        )

    def test_disallowed_tool_is_scored_without_voiding_the_pair(self):
        module = _load_module()
        task = _task("dev-001")
        runner = DisallowedToolCodexAgentRunner()
        arms = {
            "baseline": AgentArm("baseline", "fixture-a", ("miller", "serve")),
            "candidate": AgentArm("candidate", "fixture-b", ("julie",)),
        }

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result = module.execute_paired_tasks(
                tasks=(task,),
                snapshots={"snapshot-001": _snapshot(root)},
                arms=arms,
                runner=runner,
                output_root=root / "raw",
                seed=9,
                identity_sha256="4" * 64,
                void_ledger_path=root / "void-ledger.jsonl",
                max_void_attempts=1,
            )

            self.assertFalse((root / "void-ledger.jsonl").exists())

        self.assertCountEqual(runner.calls, [("dev-001", "baseline"), ("dev-001", "candidate")])
        self.assertEqual(result.baseline_rows[0]["failure_reason"], "disallowed_tool")
        self.assertEqual(result.candidate_rows[0]["failure_reason"], "disallowed_tool")
        self.assertEqual(result.baseline_rows[0]["observed_outcome"], "wrong_answer")
        self.assertEqual(result.candidate_rows[0]["observed_outcome"], "wrong_answer")

    def test_discordant_rerun_stops_after_a_pair_void(self):
        module = _load_module()
        task = _task("dev-001")
        runner = ScriptedCodexAgentRunner(
            {
                ("dev-001", "baseline"): ["valid", "harness_failure", "valid"],
                ("dev-001", "candidate"): ["agent_insufficiency", "valid", "valid"],
            }
        )
        arms = {
            "baseline": AgentArm("baseline", "fixture-a", ("miller", "serve")),
            "candidate": AgentArm("candidate", "fixture-b", ("julie",)),
        }

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaisesRegex(RuntimeError, "harness void"):
                module.execute_paired_tasks(
                    tasks=(task,),
                    snapshots={"snapshot-001": _snapshot(root)},
                    arms=arms,
                    runner=runner,
                    output_root=root / "raw",
                    seed=4,
                    identity_sha256="8" * 64,
                    max_void_attempts=1,
                )

        self.assertEqual(len(runner.calls), 4)

    def test_void_ledger_rejects_conflicting_resume_state(self):
        module = _load_module()
        with tempfile.TemporaryDirectory() as directory:
            ledger = Path(directory) / "void-ledger.jsonl"
            original = {
                "schema_version": 1,
                "task_id": "dev-001",
                "pair_attempt": 1,
                "reasons": [{"arm": "miller", "outcome": "failed"}],
            }
            module._append_void_ledger(ledger, original)
            with self.assertRaisesRegex(ValueError, "conflicting void ledger"):
                module._append_void_ledger(
                    ledger,
                    {
                        **original,
                        "reasons": [{"arm": "julie", "outcome": "failed"}],
                    },
                )
            self.assertEqual(len(ledger.read_text(encoding="utf-8").splitlines()), 1)

    def test_complete_raw_runs_resume_per_arm_without_duplicate_agent_calls(self):
        module = _load_module()
        task = _task("dev-001")
        arms = {
            "baseline": AgentArm("baseline", "fixture-a", ("miller", "serve")),
            "candidate": AgentArm("candidate", "fixture-b", ("julie",)),
        }
        first_runner = ScriptedCodexAgentRunner(
            {("dev-001", "baseline"): ["valid"], ("dev-001", "candidate"): ["valid"]}
        )

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = module.execute_paired_tasks(
                tasks=(task,), snapshots={"snapshot-001": _snapshot(root)}, arms=arms,
                runner=first_runner, output_root=root / "raw", seed=22, identity_sha256="9" * 64,
            )
            resume_runner = ScriptedCodexAgentRunner(
                {("dev-001", "baseline"): [], ("dev-001", "candidate"): []}
            )
            resumed = module.execute_paired_tasks(
                tasks=(task,), snapshots={"snapshot-001": _snapshot(root)}, arms=arms,
                runner=resume_runner, output_root=root / "raw", seed=22, identity_sha256="9" * 64,
            )
            with self.assertRaisesRegex(ValueError, "identity mismatch"):
                module.execute_paired_tasks(
                    tasks=(task,), snapshots={"snapshot-001": _snapshot(root)}, arms=arms,
                    runner=resume_runner, output_root=root / "raw", seed=22, identity_sha256="0" * 64,
                )
            run_dir = next((root / "raw" / "dev-001" / "pair-01" / "repetition-1").iterdir())
            result_path = run_dir / "run-result.json"
            raw = json.loads(result_path.read_text(encoding="utf-8"))
            raw["snapshot_id"] = "snapshot-002"
            result_path.write_text(json.dumps(raw, indent=2, sort_keys=True) + "\n", encoding="utf-8")
            marker_path = run_dir / "COMPLETE.json"
            marker = json.loads(marker_path.read_text(encoding="utf-8"))
            marker["run_result_sha256"] = hashlib.sha256(result_path.read_bytes()).hexdigest()
            for artifact in marker["artifacts"]:
                if artifact["path"] == "run-result.json":
                    artifact["bytes"] = result_path.stat().st_size
                    artifact["sha256"] = hashlib.sha256(result_path.read_bytes()).hexdigest()
            marker_path.write_text(json.dumps(marker, indent=2, sort_keys=True) + "\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "run identity mismatch"):
                module.execute_paired_tasks(
                    tasks=(task,), snapshots={"snapshot-001": _snapshot(root)}, arms=arms,
                    runner=resume_runner, output_root=root / "raw", seed=22, identity_sha256="9" * 64,
                )

        self.assertEqual(resume_runner.calls, [])
        self.assertEqual(resumed.baseline_rows, first.baseline_rows)
        self.assertEqual(resumed.candidate_rows, first.candidate_rows)

    def test_canonical_outcomes_survive_completion_resume_and_scorer_rows(self):
        module = _load_module()
        tasks = tuple(
            replace(_task(f"dev-{index:03d}"), expected_outcome=outcome)
            for index, outcome in enumerate(("success", "empty", "refusal"), start=1)
        )
        arms = {
            "baseline": AgentArm("baseline", "fixture-a", ("tool-a",)),
            "candidate": AgentArm("candidate", "fixture-b", ("tool-b",)),
        }
        scripted = {
            (task.task_id, role): ["valid"]
            for task in tasks
            for role in ("baseline", "candidate")
        }
        first_runner = ScriptedCodexAgentRunner(scripted)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = module.execute_paired_tasks(
                tasks=tasks,
                snapshots={"snapshot-001": _snapshot(root)},
                arms=arms,
                runner=first_runner,
                output_root=root / "raw",
                seed=31,
                identity_sha256="8" * 64,
            )
            resume_runner = ScriptedCodexAgentRunner(
                {(task.task_id, role): [] for task in tasks for role in arms}
            )
            resumed = module.execute_paired_tasks(
                tasks=tasks,
                snapshots={"snapshot-001": _snapshot(root)},
                arms=arms,
                runner=resume_runner,
                output_root=root / "raw",
                seed=31,
                identity_sha256="8" * 64,
            )
            for task in tasks:
                for role in arms:
                    run_dir = root / "raw" / task.task_id / "pair-01" / "repetition-1" / role
                    raw = json.loads((run_dir / "run-result.json").read_text(encoding="utf-8"))
                    marker = json.loads((run_dir / "COMPLETE.json").read_text(encoding="utf-8"))
                    self.assertEqual(task.expected_outcome, raw["observed_outcome"])
                    self.assertEqual(task.expected_outcome, marker["observed_outcome"])
                    self.assertEqual(0, raw["wrong_action_count"])
                    self.assertEqual(0, marker["wrong_action_count"])

        self.assertEqual([], resume_runner.calls)
        self.assertEqual(first.baseline_rows, resumed.baseline_rows)
        self.assertEqual(first.candidate_rows, resumed.candidate_rows)
        self.assertEqual(
            {"success", "empty", "refusal"},
            {row["observed_outcome"] for row in resumed.baseline_rows},
        )

    def test_complete_matching_output_resumes_without_calls_but_partial_or_mismatched_refuses(self):
        module = _load_module()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            exports = root / "exports"
            exports.mkdir()
            (exports / "identity-manifest.json").write_text(
                json.dumps({"run_identity_sha256": "3" * 64}), encoding="utf-8"
            )
            artifact = exports / "agent-tasks.jsonl"
            artifact.write_text("{}\n", encoding="utf-8")
            (exports / "evidence-manifest.json").write_text(
                json.dumps(
                    {
                        "schema_version": 1,
                        "artifacts": [
                            {
                                "path": "agent-tasks.jsonl",
                                "sha256": hashlib.sha256(artifact.read_bytes()).hexdigest(),
                                "bytes": artifact.stat().st_size,
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )
            (exports / "COMPLETE").write_text("3" * 64 + "\n", encoding="utf-8")

            self.assertTrue(module.completed_export_matches(exports, "3" * 64))
            artifact.write_text("changed\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "artifact hash"):
                module.completed_export_matches(exports, "3" * 64)
            artifact.write_text("{}\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "identity mismatch"):
                module.completed_export_matches(exports, "4" * 64)
            (exports / "COMPLETE").unlink()
            with self.assertRaisesRegex(ValueError, "partial"):
                module.completed_export_matches(exports, "3" * 64)

    def test_exports_are_privacy_safe_digest_verified_and_score_command_is_copyable(self):
        module = _load_module()
        task = replace(
            _task("dev-001"),
            evidence_anchors=(
                SimpleNamespace(
                    anchor_id="private-anchor-001",
                    relevance_grade=3,
                ),
            ),
        )
        baseline_row = module.empty_scorer_row("dev-001", 1, True)
        baseline_row["ordered_evidence_matches"] = ["private-anchor-001"]
        candidate_row = module.empty_scorer_row("dev-001", 1, True)
        candidate_row["ordered_evidence_matches"] = ["private-anchor-001"]
        result = SimpleNamespace(
            baseline_rows=[baseline_row],
            candidate_rows=[candidate_row],
        )
        source_root = "/Users/private/secret-source"
        identity = {
            "schema_version": 1,
            "decision_scope": "subset",
            "run_identity_sha256": hashlib.sha256(b"identity").hexdigest(),
            "seed": 7,
            "model": "gpt-5.6-sol",
            "reasoning": "medium",
            "products": {"miller": {"version": "1"}, "julie": {"version": "2"}},
        }

        with tempfile.TemporaryDirectory() as directory:
            exports = Path(directory) / "exports"
            module.export_scorer_artifacts(exports, (task,), result, identity)
            self.assertTrue((exports / "agent-score-command.txt").is_file())
            manifest = json.loads((exports / "evidence-manifest.json").read_text(encoding="utf-8"))
            for artifact in manifest["artifacts"]:
                path = exports / artifact["path"]
                self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), artifact["sha256"])
            combined = "\n".join(
                path.read_text(encoding="utf-8") for path in exports.iterdir() if path.is_file()
            )

        self.assertNotIn(source_root, combined)
        self.assertNotIn(task.prompt, combined)
        self.assertIn("evidence_anchors", combined)
        self.assertIn("ordered_evidence_matches", combined)
        self.assertIn("retrieval-eval", combined)
        self.assertIn("decision-score", combined)
        self.assertIn("--baseline", combined)
        self.assertIn("--candidate", combined)
        self.assertIn("finalize-safe", combined)
        self.assertNotIn("--miller", combined)
        self.assertNotIn("--julie", combined)

    def test_legacy_export_keeps_the_calibration_only_agent_score_adapter(self):
        module = _load_module()
        task = replace(
            _task("dev-001"),
            contract_id=None,
            capabilities=(),
            expected_outcome=None,
        )
        legacy_row = {
            "task_id": "dev-001",
            "repetition": 1,
            "completed": True,
            "failure_reason": None,
            "duration_ms": 100,
            "tool_calls": 3,
            "tool_output_bytes": 400,
            "tool_output_tokens": 100,
            "model_input_tokens": 50,
            "model_output_tokens": 20,
            "product_errors": 0,
            "duplicate_calls": 0,
            "uncited_tool_output_tokens": 0,
        }
        result = SimpleNamespace(
            baseline_rows=[dict(legacy_row)],
            candidate_rows=[dict(legacy_row)],
        )
        identity = {
            "schema_version": 1,
            "decision_scope": "subset",
            "run_identity_sha256": hashlib.sha256(b"legacy-identity").hexdigest(),
        }

        with tempfile.TemporaryDirectory() as directory:
            exports = Path(directory) / "exports"
            module.export_scorer_artifacts(exports, (task,), result, identity)
            task_row = json.loads(
                (exports / "agent-tasks.jsonl").read_text(encoding="utf-8")
            )
            command = (exports / "agent-score-command.txt").read_text(encoding="utf-8")

        self.assertEqual(
            {
                "task_id",
                "repo",
                "language",
                "workflow_class",
                "evidence_critical",
            },
            set(task_row),
        )
        self.assertIn("-- agent-score", command)
        self.assertIn('--miller "$AGENT_EFFICIENCY_EXPORT/candidate-results.jsonl"', command)
        self.assertIn('--julie "$AGENT_EFFICIENCY_EXPORT/baseline-results.jsonl"', command)
        self.assertNotIn("decision-score", command)

    def test_safe_aggregate_finalize_exposes_hashes_without_private_filenames(self):
        module = _load_module()
        self.assertTrue(hasattr(module, "finalize_safe_export"))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            exports = root / "private"
            exports.mkdir()
            private = exports / "SECRET-private-row.jsonl"
            private.write_text("{}\n", encoding="utf-8")
            aggregate = {
                "contract_id": "takeover-evaluation-v1",
                "schema_version": 1,
                "decision_scope": "subset",
                "decision_verdict": "not_decisional",
                "action_verdict": "pass",
                "task_count": 6,
                "completion": {
                    "both_correct": 6,
                    "baseline_only": 0,
                    "candidate_only": 0,
                    "neither_correct": 0,
                },
                "outcome_counts": {
                    "baseline": {
                        "success": 6,
                        "empty": 0,
                        "refusal": 0,
                        "hard_error": 0,
                        "wrong_answer": 0,
                    },
                    "candidate": {
                        "success": 6,
                        "empty": 0,
                        "refusal": 0,
                        "hard_error": 0,
                        "wrong_answer": 0,
                    },
                },
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
                "by_workflow": {},
                "by_capability": {},
                "by_repo": {},
                "by_language": {},
            }
            (exports / "aggregate.json").write_text(json.dumps(aggregate), encoding="utf-8")
            identity = {
                "contract_id": "takeover-evaluation-v1",
                "schema_version": 1,
                "corpus_role": "calibration",
                "decision_scope": "subset",
                "run_identity_sha256": "c" * 64,
                "inputs": {
                    "parent_manifest_sha256": "a" * 64,
                    "snapshot_manifest_sha256": "b" * 64,
                    "selection_sha256": "d" * 64,
                    "selected_capability_ids": ["discovery"],
                    "selected_task_count": 6,
                },
            }
            (exports / "identity-manifest.json").write_text(
                json.dumps(identity), encoding="utf-8"
            )
            (exports / "void-status.json").write_text(
                json.dumps({"unresolved_void_count": 0}), encoding="utf-8"
            )
            retained_paths = [
                private,
                exports / "identity-manifest.json",
                exports / "void-status.json",
            ]
            (exports / "evidence-manifest.json").write_text(
                json.dumps(
                    {
                        "artifacts": [
                            {
                                "path": path.name,
                                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                                "bytes": path.stat().st_size,
                            }
                            for path in retained_paths
                        ]
                    }
                ),
                encoding="utf-8",
            )
            (exports / "COMPLETE").write_text(
                identity["run_identity_sha256"] + "\n", encoding="utf-8"
            )
            safe_path = root / "safe.json"
            module.finalize_safe_export(exports, safe_path, identity, unresolved_void_count=0)
            safe_text = safe_path.read_text(encoding="utf-8")
            cli_safe_path = root / "cli-safe.json"
            self.assertEqual(
                0,
                module.main(
                    [
                        "finalize-safe",
                        "--exports",
                        str(exports),
                        "--safe-output",
                        str(cli_safe_path),
                    ]
                ),
            )
            cli_safe_text = cli_safe_path.read_text(encoding="utf-8")

        self.assertNotIn("SECRET", safe_text)
        self.assertNotIn(".jsonl", safe_text)
        self.assertIn("artifact_001", safe_text)
        self.assertIn(
            hashlib.sha256(json.dumps(aggregate).encode()).hexdigest(),
            safe_text,
        )
        self.assertEqual(safe_text, cli_safe_text)

    def test_preflight_uses_real_fixture_processes_and_fails_closed_on_every_frozen_identity(self):
        module = _load_module()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot_root = root / "snapshot"
            snapshot = _create_clean_snapshot(snapshot_root)
            second_root = root / "snapshot-two"
            second_snapshot = _create_clean_snapshot(second_root, "snapshot-002", "fixture-two")
            product = root / "product"
            _write_executable(
                product,
                """import json
import os
import sys
from pathlib import Path

if not os.environ.get("JULIE_HOME"):
    raise SystemExit("missing JULIE_HOME")

if len(sys.argv) > 1 and sys.argv[1] == "version":
    print(f"{sys.argv[2]} 1.0.0")
    raise SystemExit(0)
if len(sys.argv) > 1 and sys.argv[1] == "readiness":
    name = sys.argv[2]
    root_name = Path.cwd().name
    print(json.dumps({
        "ready": True,
        "workspace_identity": f"{name}-{root_name}-workspace",
        "index_identity": f"{name}-{root_name}-index",
        "vector_identity": f"{name}-{root_name}-vector",
        "model_identity": f"{name}-{root_name}-model",
    }))
    raise SystemExit(0)
for line in sys.stdin:
    request = json.loads(line)
    if request.get("method") == "initialize":
        result = {"protocolVersion": "2024-11-05", "capabilities": {}, "instructions": "fixture"}
        print(json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}), flush=True)
    elif request.get("method") == "tools/list":
        result = {"tools": [{"name": "search", "description": "fixture", "inputSchema": {"type": "object"}}]}
        print(json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}), flush=True)
""",
            )
            codex = root / "codex"
            _write_executable(codex, "print('codex-cli 0.145.0')\n")
            runtime = _runtime_identity(
                product,
                {"snapshot-001": snapshot_root.name, "snapshot-002": second_root.name},
            )
            product_artifact = root / "product.dll"
            product_artifact.write_bytes(b"framework-dependent product code")
            artifact_digest = hashlib.sha256(product_artifact.read_bytes()).hexdigest()
            for product_spec in runtime["products"].values():
                product_spec["binary_path"] = str(product_artifact)
                product_spec["binary_sha256"] = artifact_digest
            task = _task("dev-001")
            second_task = replace(
                _task("dev-002"),
                repo_id="fixture-two",
                snapshot_id="snapshot-002",
            )
            tasks_input = (task, second_task)
            snapshots_input = (snapshot, second_snapshot)
            roots_input = {"fixture": snapshot_root, "fixture-two": second_root}

            identity, arms, snapshots = module.preflight_run(
                tasks=tasks_input,
                snapshots=snapshots_input,
                roots=roots_input,
                runtime=runtime,
                codex_executable=str(codex),
                model="gpt-5.6-sol",
                reasoning="medium",
                seed=17,
            )

            selection = SimpleNamespace(
                contract_id="takeover-evaluation-v1",
                corpus_role="calibration",
                decision_scope="subset",
                private_identity=lambda: {
                    "parent_manifest_sha256": "1" * 64,
                    "snapshot_manifest_sha256": "2" * 64,
                    "selected_capability_ids": ["discovery"],
                    "selected_task_count": 2,
                    "selected_task_ids_sha256": "3" * 64,
                    "selection_sha256": "4" * 64,
                },
            )
            v1_identity, _, _ = module.preflight_run(
                tasks=tasks_input,
                snapshots=snapshots_input,
                roots=roots_input,
                runtime=runtime,
                codex_executable=str(codex),
                model="gpt-5.6-sol",
                reasoning="medium",
                seed=17,
                selection=selection,
            )

            self.assertEqual(set(arms), {"baseline", "candidate"})
            self.assertEqual(set(snapshots), {"snapshot-001", "snapshot-002"})
            self.assertEqual("takeover-evaluation-v1", v1_identity["contract_id"])
            self.assertEqual(
                "agent-efficiency-legacy-calibration",
                v1_identity["runtime_contract_id"],
            )
            self.assertNotIn(str(snapshot_root), json.dumps(identity))
            self.assertNotIn("miller-snapshot-workspace", json.dumps(identity))
            self.assertIn(
                "workspace_identity_sha256",
                identity["adapters"]["candidate"]["snapshots"]["snapshot-001"],
            )
            self.assertEqual(
                set(identity["adapters"]["candidate"]["snapshots"]),
                {"snapshot-001", "snapshot-002"},
            )
            self.assertIn("command_sha256", identity["adapters"]["candidate"])
            self.assertEqual(artifact_digest, identity["adapters"]["candidate"]["binary_sha256"])
            self.assertEqual(["JULIE_HOME"], identity["adapters"]["candidate"]["environment_keys"])
            self.assertIn("environment_sha256", identity["adapters"]["candidate"])
            self.assertNotIn(str(root / "isolated-julie-home"), json.dumps(identity))
            self.assertEqual(set(identity["inputs"]), {"task_manifest_sha256", "snapshot_manifest_sha256"})
            self.assertIn("environment_keys", identity)
            self.assertEqual(identity["tokenizer"]["version"], "0.13.0")
            probe = module._probe_mcp(
                (str(product), "serve", "miller"),
                snapshot_root,
                environment=runtime["products"]["miller"]["environment"],
            )
            self.assertIn("tools_sha256", probe)

            with self.assertRaisesRegex(ValueError, "model identity"):
                module.preflight_run(
                    tasks=(task,), snapshots=(snapshot,), roots={"fixture": snapshot_root}, runtime=runtime,
                    codex_executable=str(codex), model="wrong", reasoning="medium", seed=17,
                )
            bad_hash = json.loads(json.dumps(runtime))
            bad_hash["products"]["miller"]["binary_sha256"] = "0" * 64
            with self.assertRaisesRegex(ValueError, "binary hash"):
                module.preflight_run(
                    tasks=tasks_input, snapshots=snapshots_input, roots=roots_input, runtime=bad_hash,
                    codex_executable=str(codex), model="gpt-5.6-sol", reasoning="medium", seed=17,
                )
            bad_version = json.loads(json.dumps(runtime))
            bad_version["products"]["miller"]["version"] = "miller 2.0.0"
            with self.assertRaisesRegex(ValueError, "version mismatch"):
                module.preflight_run(
                    tasks=tasks_input, snapshots=snapshots_input, roots=roots_input, runtime=bad_version,
                    codex_executable=str(codex), model="gpt-5.6-sol", reasoning="medium", seed=17,
                )
            bad_commit = json.loads(json.dumps(runtime))
            bad_commit["products"]["miller"]["commit"] = "/private/secret"
            with self.assertRaisesRegex(ValueError, "commit"):
                module.preflight_run(
                    tasks=tasks_input, snapshots=snapshots_input, roots=roots_input, runtime=bad_commit,
                    codex_executable=str(codex), model="gpt-5.6-sol", reasoning="medium", seed=17,
                )
            missing_vector = json.loads(json.dumps(runtime))
            missing_vector["products"]["miller"]["readiness"]["snapshot-001"]["vector_identity"] = None
            with self.assertRaisesRegex(ValueError, "readiness"):
                module.preflight_run(
                    tasks=tasks_input, snapshots=snapshots_input, roots=roots_input, runtime=missing_vector,
                    codex_executable=str(codex), model="gpt-5.6-sol", reasoning="medium", seed=17,
                )
            with mock.patch.object(module.importlib.metadata, "version", return_value="0.12.0"):
                with self.assertRaisesRegex(ValueError, "tiktoken"):
                    module.preflight_run(
                        tasks=(task,), snapshots=(snapshot,), roots={"fixture": snapshot_root}, runtime=runtime,
                        codex_executable=str(codex), model="gpt-5.6-sol", reasoning="medium", seed=17,
                    )
            dirty = snapshot_root / "dirty.txt"
            dirty.write_text("dirty\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "dirty"):
                module.preflight_run(
                    tasks=tasks_input, snapshots=snapshots_input, roots=roots_input, runtime=runtime,
                    codex_executable=str(codex), model="gpt-5.6-sol", reasoning="medium", seed=17,
                )
            dirty.unlink()
            bad_codex = root / "bad-codex"
            _write_executable(bad_codex, "print('codex-cli 0.144.0')\n")
            with self.assertRaisesRegex(ValueError, "unsupported Codex"):
                module.preflight_run(
                    tasks=tasks_input, snapshots=snapshots_input, roots=roots_input, runtime=runtime,
                    codex_executable=str(bad_codex), model="gpt-5.6-sol", reasoning="medium", seed=17,
                )
            dead_server = root / "dead-server"
            _write_executable(dead_server, "raise SystemExit(3)\n")
            uninitializable = json.loads(json.dumps(runtime))
            uninitializable["products"]["miller"]["command"] = [str(dead_server)]
            uninitializable["products"]["miller"]["binary_path"] = str(dead_server)
            uninitializable["products"]["miller"]["binary_sha256"] = hashlib.sha256(dead_server.read_bytes()).hexdigest()
            with self.assertRaisesRegex(ValueError, "MCP"):
                module.preflight_run(
                    tasks=tasks_input, snapshots=snapshots_input, roots=roots_input, runtime=uninitializable,
                    codex_executable=str(codex), model="gpt-5.6-sol", reasoning="medium", seed=17,
                )

    def test_raw_contract_preserves_budget_and_disallowed_failures_without_answers(self):
        module = _load_module()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            task = _task("dev-001")
            proxy = root / "proxy.jsonl"
            proxy.write_text(
                json.dumps({
                    "event": "budget_transition",
                    "budget": "tool_output_tokens",
                    "used": 12001,
                    "limit": 12000,
                }) + "\n",
                encoding="utf-8",
            )
            run = AgentRun(
                outcome="completed",
                classification="valid",
                failure_reason=None,
                answer=StructuredAnswer(status="answered", answer="answer", evidence=()),
                verification=VerificationResult(
                    True,
                    (),
                    (),
                    observed_outcome="wrong_answer",
                    wrong_action_count=2,
                ),
                command_manifest_path=root / "manifest.json",
                codex_events_path=root / "codex.jsonl",
                proxy_events_path=proxy,
                stderr_path=root / "stderr.txt",
                diagnostics=(),
                model_input_tokens=1,
                model_output_tokens=1,
                wall_clock_ms=1,
                exit_code=0,
                child_home_removed=True,
            )

            budget = module._raw_result(task, "miller", 1, 1, run)
            disallowed_proxy = root / "disallowed-proxy.jsonl"
            disallowed_proxy.write_text("", encoding="utf-8")
            disallowed = module._raw_result(
                task,
                "julie",
                1,
                1,
                replace(
                    run,
                    outcome="disallowed_tool",
                    classification="agent_insufficiency",
                    failure_reason="disallowed_tool",
                    proxy_events_path=disallowed_proxy,
                ),
            )

        self.assertEqual(
            (
                budget["status"],
                budget["failure_reason"],
                budget["answer"],
                budget["observed_outcome"],
                budget["wrong_action_count"],
            ),
            ("budget_exceeded", "budget_exceeded", None, "hard_error", 0),
        )
        self.assertEqual((disallowed["status"], disallowed["failure_reason"], disallowed["answer"]), ("disallowed_tool", "disallowed_tool", None))

    def test_v1_raw_and_scorer_rows_preserve_canonical_outcomes(self):
        module = _load_module()
        task = replace(
            _task("dev-001"),
            contract_id="takeover-evaluation-v1",
            capabilities=("discovery",),
            expected_outcome="success",
        )
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            proxy = root / "proxy.jsonl"
            proxy.write_text("", encoding="utf-8")
            run = AgentRun(
                outcome="completed",
                classification="valid",
                failure_reason=None,
                answer=StructuredAnswer(status="answered", answer="answer", evidence=()),
                verification=VerificationResult(
                    True,
                    (),
                    (),
                    observed_outcome="success",
                    wrong_action_count=0,
                ),
                command_manifest_path=root / "manifest.json",
                codex_events_path=root / "codex.jsonl",
                proxy_events_path=proxy,
                stderr_path=root / "stderr.txt",
                diagnostics=(),
                model_input_tokens=1,
                model_output_tokens=1,
                wall_clock_ms=1,
                exit_code=0,
                child_home_removed=True,
            )
            raw = module._raw_result(task, "baseline", 1, 1, run)
            scorer = module._scorer_row(raw, 1)

        self.assertIn("contract_id", raw)
        self.assertEqual("takeover-evaluation-v1", raw["contract_id"])
        self.assertEqual("baseline", raw["role"])
        self.assertEqual("success", raw["expected_outcome"])
        self.assertEqual("success", raw["observed_outcome"])
        self.assertEqual(0, raw["wrong_action_count"])
        self.assertEqual("success", scorer["observed_outcome"])
        self.assertEqual(0, scorer["wrong_action_count"])


if __name__ == "__main__":
    unittest.main()
