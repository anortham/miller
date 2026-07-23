import json
import os
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock
from pathlib import Path


SCRIPTS_ROOT = Path(__file__).resolve().parents[1]
FIXTURES_ROOT = SCRIPTS_ROOT / "tests" / "fixtures" / "agent-efficiency"
sys.path.insert(0, str(SCRIPTS_ROOT))

from benchlib import agent_runner
from benchlib.agent_contract import (
    SnapshotIdentity,
    StructuredAnswer,
    VerificationResult,
    load_task_manifest,
)
from benchlib.agent_runner import AgentArm, AgentSnapshot, CodexAgentRunner, isolated_environment_keys


def _git(root: Path, *args: str) -> str:
    return subprocess.run(
        ["git", "-C", str(root), *args],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def _create_snapshot(root: Path) -> AgentSnapshot:
    _git(root, "init", "-q")
    _git(root, "config", "user.name", "Runner Fixture")
    _git(root, "config", "user.email", "runner@example.invalid")
    (root / "src").mkdir()
    (root / "src" / "factory.py").write_text(
        "def create_candidate():\n    return 'token-baseline'\n",
        encoding="utf-8",
    )
    _git(root, "add", ".")
    _git(root, "commit", "-qm", "fixture")
    identity = SnapshotIdentity.capture("snapshot-001", "fixture", ("python",), root)
    (root / ".miller").mkdir()
    (root / ".miller" / "vectors.db").write_text("vectors", encoding="utf-8")
    (root / ".julie").mkdir()
    (root / ".julie" / "symbols.db").write_text("symbols", encoding="utf-8")
    return AgentSnapshot(identity=identity, root=root)


def _create_task(root: Path):
    value = {
        "schema_version": 1,
        "tasks": [
            {
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
        ],
    }
    path = root / "task.json"
    path.write_text(json.dumps(value), encoding="utf-8")
    return load_task_manifest(path)[0]


def _write_fake_codex(
    path: Path,
    fixture: Path | None,
    capture_path: Path,
    *,
    exit_code: int = 0,
    proxy_event: dict | None = None,
    child_pid_path: Path | None = None,
    prelude: str = "",
) -> None:
    script = f"""#!{sys.executable}
import json
import os
import pathlib
import stat
import subprocess
import sys
import time
import tomllib

argv = sys.argv[1:]
home = pathlib.Path(os.environ["CODEX_HOME"])
working = pathlib.Path(os.getcwd())
capture = {{
    "argv": argv,
    "cwd": str(working),
    "cwd_entries": sorted(item.name for item in working.iterdir()),
    "env_keys": sorted(os.environ),
    "home_entries": sorted(item.name for item in home.iterdir()),
    "home_mode": stat.S_IMODE(home.stat().st_mode),
    "auth_mode": stat.S_IMODE((home / "auth.json").stat().st_mode) if (home / "auth.json").exists() else None,
    "stdin": sys.stdin.read(),
}}
pathlib.Path({str(capture_path)!r}).write_text(json.dumps(capture), encoding="utf-8")
proxy_event = {proxy_event!r}
if proxy_event is not None:
    configs = {{argv[index + 1].split("=", 1)[0]: argv[index + 1].split("=", 1)[1] for index, value in enumerate(argv[:-1]) if value == "-c"}}
    proxy_args = tomllib.loads("value=" + configs["mcp_servers.benchmark.args"])["value"]
    events_path = pathlib.Path(proxy_args[proxy_args.index("--events") + 1])
    events_path.write_text(json.dumps(proxy_event) + "\\n", encoding="utf-8")
child_pid_path = {str(child_pid_path) if child_pid_path else None!r}
prelude = {prelude!r}
if prelude:
    sys.stdout.write(prelude)
    sys.stdout.flush()
if child_pid_path is not None:
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    pathlib.Path(child_pid_path).write_text(str(child.pid), encoding="utf-8")
    time.sleep(60)
fixture = {str(fixture) if fixture else None!r}
if fixture is not None:
    sys.stdout.write(pathlib.Path(fixture).read_text(encoding="utf-8"))
    sys.stdout.flush()
sys.exit({exit_code})
"""
    path.write_text(script, encoding="utf-8")
    path.chmod(0o700)


def _runner(fake_codex: Path, source_home: Path, timeout: float = 3) -> CodexAgentRunner:
    return CodexAgentRunner(
        codex_executable=fake_codex,
        proxy_command=(sys.executable, str(SCRIPTS_ROOT / "benchlib" / "recording_mcp_proxy.py")),
        source_codex_home=source_home,
        timeout_seconds=timeout,
    )


class AgentRunnerTests(unittest.TestCase):
    def test_classification_copies_success_empty_and_refusal_from_the_verifier(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            task = _create_task(root)
            paths = {
                name: root / filename
                for name, filename in {
                    "manifest": "command.json",
                    "events": "events.jsonl",
                    "proxy": "proxy.jsonl",
                    "stderr": "stderr.txt",
                }.items()
            }
            paths["proxy"].write_text("", encoding="utf-8")
            parsed = agent_runner._ParsedEvents(
                answer=StructuredAnswer(status="answered", answer="fixture", evidence=()),
                answer_error=None,
                diagnostics=(),
                disallowed_item=None,
                malformed=None,
                turn_completed=True,
                turn_failed=False,
                model_input_tokens=1,
                model_output_tokens=1,
            )
            for observed in ("success", "empty", "refusal"):
                with self.subTest(observed=observed), mock.patch.object(
                    agent_runner,
                    "verify_answer",
                    return_value=VerificationResult(
                        True,
                        (),
                        (),
                        observed_outcome=observed,
                        wrong_action_count=0,
                    ),
                ):
                    result = agent_runner._classify_run(
                        task,
                        root,
                        parsed,
                        paths,
                        exit_code=0,
                        timed_out=False,
                        wall_clock_ms=1,
                    )
                    self.assertEqual("valid", result.classification)
                    self.assertEqual(observed, result.observed_outcome)
                    self.assertEqual(0, result.wrong_action_count)

    def test_success_uses_exact_isolated_command_and_verifies_answer(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot_root = root / "snapshot"
            snapshot_root.mkdir()
            snapshot = _create_snapshot(snapshot_root)
            task = _create_task(root)
            source_home = root / "source-home"
            source_home.mkdir()
            (source_home / "auth.json").write_text("secret-auth-value", encoding="utf-8")
            (source_home / "config.toml").write_text("global=true", encoding="utf-8")
            for name in ["AGENTS.md", "history.jsonl", "plugins", "skills", "hooks", "memories"]:
                target = source_home / name
                if "." in name:
                    target.write_text("global", encoding="utf-8")
                else:
                    target.mkdir()
            capture = root / "capture.json"
            executable = root / "codex"
            _write_fake_codex(executable, FIXTURES_ROOT / "codex-success.jsonl", capture)
            output = root / "output"

            result = _runner(executable, source_home).run(
                task,
                AgentArm(
                    role="baseline",
                    adapter_name="fixture",
                    product_command=("miller", "serve"),
                    product_environment=(("JULIE_HOME", "/private/bench/julie-home"),),
                ),
                snapshot,
                output,
            )

            captured = json.loads(capture.read_text(encoding="utf-8"))
            argv = captured["argv"]
            self.assertEqual("completed", result.outcome)
            self.assertEqual("valid", result.classification)
            self.assertTrue(result.verification.passed)
            self.assertEqual(120, result.model_input_tokens)
            self.assertEqual(30, result.model_output_tokens)
            self.assertEqual(["auth.json"], captured["home_entries"])
            self.assertEqual([], captured["cwd_entries"])
            self.assertEqual(0o700, captured["home_mode"])
            self.assertEqual(0o600, captured["auth_mode"])
            self.assertIn("Use only the benchmark MCP server", captured["stdin"])
            self.assertIn(
                "Use exact symbol IDs returned by product tools; never invent an ID from a path or name.",
                captured["stdin"],
            )
            self.assertIn(
                "For every action target, leave every unlisted field null.",
                captured["stdin"],
            )
            for action_kind in (
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
            ):
                self.assertIn(f"- {action_kind}:", captured["stdin"])
            for option in [
                "--json",
                "--ephemeral",
                "--ignore-user-config",
                "--ignore-rules",
                "--strict-config",
                "--output-schema",
                "--sandbox",
                "read-only",
                "--cd",
                "--skip-git-repo-check",
                "--model",
                "gpt-5.6-sol",
            ]:
                self.assertIn(option, argv)
            configs = [argv[index + 1] for index, value in enumerate(argv[:-1]) if value == "-c"]
            self.assertIn('model_reasoning_effort="medium"', configs)
            self.assertIn('approval_policy="never"', configs)
            self.assertIn('mcp_servers.benchmark.default_tools_approval_mode="approve"', configs)
            self.assertIn("mcp_servers.benchmark.required=true", configs)
            self.assertTrue(any(value.startswith("mcp_servers.benchmark.command=") for value in configs))
            self.assertTrue(any(value.startswith("mcp_servers.benchmark.args=") for value in configs))
            self.assertTrue(any("--product-env" in value and "JULIE_HOME=" in value for value in configs))
            self.assertTrue(any(value.startswith("mcp_servers.benchmark.cwd=") for value in configs))
            self.assertIn("mcp_servers.benchmark.startup_timeout_sec=30", configs)
            self.assertIn("mcp_servers.benchmark.tool_timeout_sec=120", configs)
            self.assertFalse(
                any(value.startswith("mcp_servers.benchmark.startup_timeout=") for value in configs)
            )
            self.assertFalse(
                any(value.startswith("mcp_servers.benchmark.tool_timeout=") for value in configs)
            )
            manifest = json.loads(result.command_manifest_path.read_text(encoding="utf-8"))
            self.assertEqual(list(isolated_environment_keys()), manifest["environment_keys"])
            self.assertNotIn("secret-auth-value", json.dumps(manifest))
            self.assertNotIn("config.toml", json.dumps(manifest))
            self.assertTrue(any(item["kind"] == "unknown_event" for item in result.diagnostics))
            artifacts = "\n".join(
                path.read_text(encoding="utf-8", errors="replace")
                for path in output.rglob("*")
                if path.is_file()
            )
            self.assertNotIn("secret-auth-value", artifacts)
            self.assertTrue(result.child_home_removed)

    def test_disallowed_item_fails_closed_even_with_a_valid_final_answer(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot_root = root / "snapshot"
            snapshot_root.mkdir()
            snapshot = _create_snapshot(snapshot_root)
            task = _create_task(root)
            source_home = root / "source-home"
            source_home.mkdir()
            capture = root / "capture.json"
            executable = root / "codex"
            _write_fake_codex(executable, FIXTURES_ROOT / "codex-disallowed-tool.jsonl", capture)

            result = _runner(executable, source_home).run(
                task,
                AgentArm(role="baseline", adapter_name="fixture", product_command=("miller", "serve")),
                snapshot,
                root / "output",
            )

            self.assertEqual("disallowed_tool", result.outcome)
            self.assertEqual("agent_insufficiency", result.classification)
            self.assertEqual("disallowed_tool", result.failure_reason)
            self.assertEqual("wrong_answer", result.observed_outcome)
            self.assertIsNone(result.answer)
            self.assertFalse(result.verification.passed)

    def test_every_non_benchmark_access_item_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot_root = root / "snapshot"
            snapshot_root.mkdir()
            snapshot = _create_snapshot(snapshot_root)
            task = _create_task(root)
            source_home = root / "source-home"
            source_home.mkdir()
            items = [
                {"type": "file_change", "path": "src/factory.py"},
                {"type": "file_read", "path": "src/factory.py"},
                {"type": "web_search", "query": "factory"},
                {"type": "mcp_tool_call", "server": "other", "tool": "search"},
                {"type": "computer_use", "action": "screenshot"},
            ]
            for index, item in enumerate(items):
                with self.subTest(item_type=item["type"]):
                    event = {
                        "type": "item.started",
                        "item": {"id": f"item-{index}", "status": "in_progress", **item},
                    }
                    fixture = root / f"disallowed-{index}.jsonl"
                    fixture.write_text(
                        "\n".join(
                            json.dumps(value)
                            for value in [
                                {"type": "thread.started", "thread_id": "t"},
                                {"type": "turn.started"},
                                event,
                                {"type": "turn.completed", "usage": {}},
                            ]
                        )
                        + "\n",
                        encoding="utf-8",
                    )
                    executable = root / f"codex-disallowed-{index}"
                    _write_fake_codex(
                        executable,
                        fixture,
                        root / f"capture-disallowed-{index}.json",
                    )
                    result = _runner(executable, source_home).run(
                        task,
                        AgentArm(role="baseline", adapter_name="fixture", product_command=("miller", "serve")),
                        snapshot,
                        root / f"output-disallowed-{index}",
                    )
                    self.assertEqual("disallowed_tool", result.outcome)
                    self.assertEqual("agent_insufficiency", result.classification)

    def test_malformed_missing_and_incorrect_answers_are_distinct(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot_root = root / "snapshot"
            snapshot_root.mkdir()
            snapshot = _create_snapshot(snapshot_root)
            task = _create_task(root)
            source_home = root / "source-home"
            source_home.mkdir()
            cases = [
                ("malformed", "{not-json}\n", 0, "failed", "harness_failure"),
                (
                    "invalid-answer",
                    "{\"type\":\"thread.started\",\"thread_id\":\"t\"}\n"
                    "{\"type\":\"turn.started\"}\n"
                    "{\"type\":\"item.completed\",\"item\":{\"id\":\"i\",\"type\":\"agent_message\",\"text\":\"{}\"}}\n"
                    "{\"type\":\"turn.completed\",\"usage\":{}}\n",
                    0,
                    "invalid_answer",
                    "agent_insufficiency",
                ),
                (
                    "incorrect",
                    "{\"type\":\"thread.started\",\"thread_id\":\"t\"}\n"
                    "{\"type\":\"turn.started\"}\n"
                    "{\"type\":\"item.completed\",\"item\":{\"id\":\"i\",\"type\":\"agent_message\",\"text\":\"{\\\"status\\\":\\\"answered\\\",\\\"answer\\\":\\\"Embeddings are selected.\\\",\\\"evidence\\\":[]}\"}}\n"
                    "{\"type\":\"turn.completed\",\"usage\":{}}\n",
                    0,
                    "completed",
                    "agent_insufficiency",
                ),
            ]
            for label, lines, exit_code, outcome, classification in cases:
                with self.subTest(label=label):
                    fixture = root / f"{label}.jsonl"
                    fixture.write_text(lines, encoding="utf-8")
                    executable = root / f"codex-{label}"
                    _write_fake_codex(executable, fixture, root / f"capture-{label}.json", exit_code=exit_code)
                    result = _runner(executable, source_home).run(
                        task,
                        AgentArm(role="baseline", adapter_name="fixture", product_command=("miller", "serve")),
                        snapshot,
                        root / f"output-{label}",
                    )
                    self.assertEqual(outcome, result.outcome)
                    self.assertEqual(classification, result.classification)
                    self.assertEqual(
                        "wrong_answer" if label == "incorrect" else "hard_error",
                        result.observed_outcome,
                    )

    def test_final_answer_rejects_duplicate_json_object_keys(self) -> None:
        answer = (
            '{"status":"answered","status":"answered",'
            '"answer":"token-baseline fallback",'
            '"evidence":[]}'
        )
        event = json.dumps(
            {
                "type": "item.completed",
                "item": {
                    "id": "i",
                    "type": "agent_message",
                    "text": answer,
                },
            }
        )

        parsed = agent_runner._parse_events(event)

        self.assertIsNone(parsed.answer)
        self.assertIn("duplicate JSON object key: status", parsed.answer_error or "")

    def test_cli_and_product_failures_are_classified_from_proxy_evidence(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot_root = root / "snapshot"
            snapshot_root.mkdir()
            snapshot = _create_snapshot(snapshot_root)
            task = _create_task(root)
            source_home = root / "source-home"
            source_home.mkdir()
            for label, proxy_event, expected in [
                ("cli", None, "harness_failure"),
                ("product", {"type": "process_exit", "returncode": 2}, "product_failure"),
            ]:
                with self.subTest(label=label):
                    executable = root / f"codex-{label}"
                    _write_fake_codex(
                        executable,
                        FIXTURES_ROOT / "codex-failure.jsonl",
                        root / f"capture-{label}.json",
                        exit_code=1,
                        proxy_event=proxy_event,
                    )
                    result = _runner(executable, source_home).run(
                        task,
                        AgentArm(role="baseline", adapter_name="fixture", product_command=("miller", "serve")),
                        snapshot,
                        root / f"output-{label}",
                    )
                    self.assertEqual("failed", result.outcome)
                    self.assertEqual(expected, result.classification)
                    self.assertEqual("hard_error", result.observed_outcome)

    def test_reused_output_directory_cannot_reuse_stale_proxy_failure_evidence(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot_root = root / "snapshot"
            snapshot_root.mkdir()
            snapshot = _create_snapshot(snapshot_root)
            task = _create_task(root)
            source_home = root / "source-home"
            source_home.mkdir()
            output = root / "output"
            product_executable = root / "codex-product"
            _write_fake_codex(
                product_executable,
                FIXTURES_ROOT / "codex-failure.jsonl",
                root / "capture-product.json",
                exit_code=1,
                proxy_event={"type": "process_exit", "returncode": 2},
            )
            first = _runner(product_executable, source_home).run(
                task,
                AgentArm(role="baseline", adapter_name="fixture", product_command=("miller", "serve")),
                snapshot,
                output,
            )
            cli_executable = root / "codex-cli"
            _write_fake_codex(
                cli_executable,
                FIXTURES_ROOT / "codex-failure.jsonl",
                root / "capture-cli.json",
                exit_code=1,
            )

            second = _runner(cli_executable, source_home).run(
                task,
                AgentArm(role="baseline", adapter_name="fixture", product_command=("miller", "serve")),
                snapshot,
                output,
            )

            self.assertEqual("product_failure", first.classification)
            self.assertEqual("harness_failure", second.classification)
            self.assertFalse(second.proxy_events_path.exists())

    def test_isolated_home_authentication_rejection_is_a_preflight_failure(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot_root = root / "snapshot"
            snapshot_root.mkdir()
            snapshot = _create_snapshot(snapshot_root)
            task = _create_task(root)
            source_home = root / "source-home"
            source_home.mkdir()
            fixture = root / "auth-failure.jsonl"
            fixture.write_text(
                '{"type":"thread.started","thread_id":"t"}\n'
                '{"type":"turn.started"}\n'
                '{"type":"error","message":"Not logged in. Run codex login."}\n'
                '{"type":"turn.failed","error":{"message":"Not logged in. Run codex login."}}\n',
                encoding="utf-8",
            )
            executable = root / "codex"
            _write_fake_codex(executable, fixture, root / "capture.json", exit_code=1)

            result = _runner(executable, source_home).run(
                task,
                AgentArm(role="baseline", adapter_name="fixture", product_command=("miller", "serve")),
                snapshot,
                root / "output",
            )

            self.assertEqual("preflight_failure", result.outcome)
            self.assertEqual("harness_failure", result.classification)
            self.assertTrue(any("logged in" in failure.lower() for failure in result.verification.failures))

    @unittest.skipIf(os.name == "nt", "POSIX process-group assertion")
    def test_timeout_terminates_the_real_child_process_group(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot_root = root / "snapshot"
            snapshot_root.mkdir()
            snapshot = _create_snapshot(snapshot_root)
            task = _create_task(root)
            source_home = root / "source-home"
            source_home.mkdir()
            child_pid_path = root / "child.pid"
            executable = root / "codex"
            _write_fake_codex(
                executable,
                None,
                root / "capture.json",
                child_pid_path=child_pid_path,
                prelude='{"type":"thread.started","thread_id":"partial"}\n',
            )

            result = _runner(executable, source_home, timeout=0.5).run(
                task,
                AgentArm(role="baseline", adapter_name="fixture", product_command=("miller", "serve")),
                snapshot,
                root / "output",
            )

            self.assertEqual("timeout", result.outcome)
            self.assertEqual("product_failure", result.classification)
            self.assertEqual(
                ['{"type":"thread.started","thread_id":"partial"}'],
                result.codex_events_path.read_text(encoding="utf-8").splitlines(),
            )
            child_pid = int(child_pid_path.read_text(encoding="utf-8"))
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                try:
                    os.kill(child_pid, 0)
                except ProcessLookupError:
                    break
                time.sleep(0.05)
            else:
                os.kill(child_pid, signal.SIGKILL)
                self.fail(f"child process {child_pid} survived runner timeout")
            self.assertTrue(result.child_home_removed)

    def test_snapshot_preflight_fails_before_codex_launch(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot_root = root / "snapshot"
            snapshot_root.mkdir()
            snapshot = _create_snapshot(snapshot_root)
            task = _create_task(root)
            (snapshot_root / "src" / "factory.py").write_text("dirty\n", encoding="utf-8")
            capture = root / "capture.json"
            executable = root / "codex"
            _write_fake_codex(executable, FIXTURES_ROOT / "codex-success.jsonl", capture)
            source_home = root / "source-home"
            source_home.mkdir()

            result = _runner(executable, source_home).run(
                task,
                AgentArm(role="baseline", adapter_name="fixture", product_command=("miller", "serve")),
                snapshot,
                root / "output",
            )

            self.assertEqual("preflight_failure", result.outcome)
            self.assertEqual("harness_failure", result.classification)
            self.assertFalse(capture.exists())


if __name__ == "__main__":
    unittest.main()
