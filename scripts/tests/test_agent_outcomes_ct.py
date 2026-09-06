import hashlib
import json
import sys
import tempfile
import unittest
from dataclasses import replace
from itertools import pairwise
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))

from benchlib.agent_outcomes_contract import (
    VerificationExecution,
    source_snapshot_sha256,
)
from benchlib.agent_outcomes_ct import (
    CtContainerSpec,
    CtKnownChange,
    CtLifecycle,
    PersistentCtAttemptSupervisor,
)


class ManualClock:
    def __init__(self):
        self.now = 0.0

    def __call__(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


class FakeExecutor:
    def __init__(self, replies):
        self.replies = list(replies)
        self.calls = []

    def execute(self, argv, candidate_root, timeout_seconds):
        self.calls.append((tuple(argv), candidate_root, timeout_seconds))
        if not self.replies:
            raise AssertionError("unexpected lifecycle command")
        return self.replies.pop(0)


class AdvancingExecutor(FakeExecutor):
    def __init__(self, replies, clock, elapsed_per_call):
        super().__init__(replies)
        self.clock = clock
        self.elapsed_per_call = elapsed_per_call

    def execute(self, argv, candidate_root, timeout_seconds):
        result = super().execute(argv, candidate_root, timeout_seconds)
        self.clock.sleep(self.elapsed_per_call)
        return result


class FakeHost:
    def __init__(self, cidfile, replies, on_execute=None):
        self.cidfile = cidfile
        self.replies = list(replies)
        self.calls = []
        self.on_execute = on_execute

    def execute(
        self,
        argv,
        timeout_seconds,
        *,
        stdin_path=None,
        stdout_path=None,
        stderr_path=None,
    ):
        self.calls.append(
            (
                tuple(argv),
                timeout_seconds,
                stdin_path,
                stdout_path,
                stderr_path,
            )
        )
        if len(self.calls) == 1:
            self.cidfile.write_text("ct-container\n", encoding="utf-8")
        if self.on_execute is not None:
            self.on_execute(tuple(argv), stdin_path)
        if not self.replies:
            raise AssertionError("unexpected host command")
        return self.replies.pop(0)


def reply(value, returncode=0, ran=True, stderr=""):
    return VerificationExecution(ran, returncode, json.dumps(value), stderr)


def task(workflow="test_selection", snapshot_sha256=None):
    outcome = type(
        "Outcome",
        (),
        {"workflow": workflow, "snapshot_sha256": snapshot_sha256},
    )()
    verifier = type(
        "Verifier",
        (),
        {"value": {"test_cases": [{"test_id": "fixture::test", "path": "test.py"}]}},
    )()
    return type("Task", (), {"task": outcome, "verifier": verifier})()


def config(**overrides):
    value = {
        "schema_version": 1,
        "enabled_arm": "native+miller-lexical",
        "command_timeout_seconds": 30,
        "readiness_timeout_seconds": 2,
        "poll_interval_seconds": 0.5,
    }
    value.update(overrides)
    return value


def enabled(projects=None, unsupported_count=0):
    return {
        "operation": "enable",
        "enabled_count": len(projects or []),
        "projects": projects or [],
        "changed_count": len(projects or []),
        "changed_projects": projects or [],
        "unsupported_count": unsupported_count,
        "unsupported_projects": [],
    }


def status(*, cases=3, running=True, activity="idle", selected=True, revision=1):
    return {
        "schema_version": 1,
        "miller_version": "test",
        "enabled": True,
        "kill_switch": False,
        "projects_discovered": False,
        "projects": [
            {
                "id": "ct-project:tests",
                "project_path": "/workspace/tests",
                "framework": "pytest",
                "command": None,
                "enabled": True,
                "unsupported_reason": None,
                "exclude_traits": [],
                "case_count": cases,
                "stale_count": cases,
                "red_count": 0,
                "verdict": "unknown",
                "last_run_at": None,
            }
        ],
        "daemon": {
            "state": "running" if running else "stopped",
            "reason": "ready",
            "running": running,
            "paused": False,
            "auto_runs_paused": False,
            "pause_reason": None,
            "activity": activity,
            "run": None,
            "miller_version": "test",
            "version_match": "same",
            "version_mismatch": False,
            "version_reason": "same",
            "loop_stalled": False,
            "loop_stall_seconds": 0,
        },
        "verdict": "unknown",
        "selected": {"index_identity": "fixture", "revision": revision}
        if selected
        else None,
        "stale_count": cases,
        "selected_count": cases,
        "last_run": f"run-{revision}",
        "budget_holder": None,
    }


def warmup(verdict="green"):
    return {
        "execution": "daemon",
        "verdict": verdict,
        "reason": None,
        "waited": True,
        "paused": False,
        "selected": {"index_identity": "fixture", "revision": 1},
        "wait": {
            "wait_complete": True,
            "state": "completed",
            "elapsed_seconds": 1.0,
            "timeout_seconds": 60.0,
            "command_id": "fixture-command",
            "run_id": "fixture-run",
        },
    }


def failure_report(ids=(), *, identity="fixture", revision=1):
    return {
        "failures": [
            {
                "test_case_id": case_id,
                "state": "red",
                "index_identity": identity,
                "revision": revision,
                "failure_summary": "assertion failed",
            }
            for case_id in ids
        ],
        "truncated": 0,
        "total": len(ids),
        "offset": 0,
    }


def baseline_expectation(verdict="green", ids=()):
    return type(
        "KnownChangeExpectation",
        (),
        {
            "changed_paths": ("test.py",),
            "expected_baseline_ct_verdict": verdict,
            "expected_baseline_ct_failure_ids": tuple(ids),
            "qualification_evidence_sha256": "d" * 64,
        },
    )()


def discovery(projects):
    value = status(cases=0, running=False, selected=False)
    value["enabled"] = False
    value["projects_discovered"] = True
    value["projects"] = projects
    return value


class AgentOutcomesCtTests(unittest.TestCase):
    def test_native_control_performs_zero_ct_work(self):
        executor = FakeExecutor([])
        lifecycle = CtLifecycle.from_manifest(
            config(), miller_path="/opt/miller/miller", executor=executor
        )

        with tempfile.TemporaryDirectory() as directory:
            session = lifecycle.prepare(Path(directory), task(), "native")
            cleanup = session.cleanup()

        self.assertTrue(session.evidence.success)
        self.assertEqual("not_applicable", session.evidence.disposition)
        self.assertEqual((), session.evidence.commands)
        self.assertEqual((), cleanup.commands)
        self.assertEqual([], executor.calls)

    def test_primary_and_secondary_modes_perform_zero_ct_work(self):
        executor = FakeExecutor([])
        lifecycle = CtLifecycle.from_manifest(
            config(), miller_path="/opt/miller/miller", executor=executor
        )

        with tempfile.TemporaryDirectory() as directory:
            primary = lifecycle.prepare(
                Path(directory),
                task(),
                "native+miller-lexical",
                comparison_mode="primary",
            )
            secondary = lifecycle.prepare(
                Path(directory),
                task("concept"),
                "native+miller-semantic",
                comparison_mode="secondary",
            )

        self.assertEqual("not_applicable", primary.evidence.disposition)
        self.assertEqual("not_applicable", secondary.evidence.disposition)
        self.assertEqual([], executor.calls)

    def test_enabled_arm_reaches_ready_inventory_and_cleans_up_once(self):
        project = {"project_path": "/workspace/tests", "framework": "pytest"}
        executor = FakeExecutor(
            [
                reply(discovery([project])),
                reply(enabled([project])),
                reply({"status": "started", "reason": None, "pid": 42}),
                reply(warmup()),
                reply(failure_report()),
                reply(status(cases=7)),
                reply({"status": "stopped", "reason": None}),
                reply({**enabled([]), "operation": "disable"}),
            ]
        )
        lifecycle = CtLifecycle.from_manifest(
            config(), miller_path="/opt/miller/miller", executor=executor
        )

        with tempfile.TemporaryDirectory() as directory:
            session = lifecycle.prepare(
                Path(directory),
                task(),
                "native+miller-lexical",
                known_change=baseline_expectation(),
            )
            first = session.cleanup()
            second = session.cleanup()

        self.assertTrue(session.evidence.success)
        self.assertEqual("ready", session.evidence.disposition)
        self.assertEqual(1, session.evidence.project_count)
        self.assertEqual(7, session.evidence.case_count)
        self.assertEqual(6, len(session.evidence.commands))
        self.assertEqual((), session.evidence.baseline_failure_test_case_ids)
        self.assertEqual("d" * 64, session.evidence.qualification_evidence_sha256)
        self.assertTrue(first.success)
        self.assertIs(first, second)
        self.assertEqual(
            [
                "status",
                "enable",
                "serve",
                "run",
                "failures",
                "status",
                "stop",
                "disable",
            ],
            [call[0][2] for call in executor.calls],
        )
        self.assertRegex(session.evidence.evidence_sha256, r"^[0-9a-f]{64}$")

    def test_enable_refusal_is_unsuccessful_and_still_cleans_up(self):
        project = {"project_path": "/workspace/tests", "framework": "pytest"}
        executor = FakeExecutor(
            [
                reply(discovery([project])),
                reply({"error": "no supported projects"}, returncode=3),
                reply({"status": "already_stopped", "reason": None}),
                reply({**enabled([]), "operation": "disable"}),
            ]
        )
        lifecycle = CtLifecycle.from_manifest(
            config(), miller_path="/opt/miller/miller", executor=executor
        )

        with tempfile.TemporaryDirectory() as directory:
            session = lifecycle.prepare(
                Path(directory),
                task(),
                "native+miller-lexical",
                known_change=baseline_expectation(),
            )

        self.assertFalse(session.evidence.success)
        self.assertEqual("enable_refused", session.evidence.disposition)
        self.assertTrue(session.cleanup().success)
        self.assertEqual(
            ["status", "enable", "stop", "disable"],
            [call[0][2] for call in executor.calls],
        )

    def test_qualified_known_red_baseline_is_admitted_with_exact_evidence(self):
        project = {"project_path": "/workspace/tests", "framework": "pytest"}
        executor = FakeExecutor(
            [
                reply(discovery([project])),
                reply(enabled([project])),
                reply({"status": "started", "reason": None, "pid": 42}),
                reply(warmup("red")),
                reply(failure_report(("known-red",))),
                reply(status(cases=2)),
                reply({"status": "stopped", "reason": None}),
                reply({**enabled([]), "operation": "disable"}),
            ]
        )
        lifecycle = CtLifecycle.from_manifest(
            config(), miller_path="miller", executor=executor
        )

        with tempfile.TemporaryDirectory() as directory:
            session = lifecycle.prepare(
                Path(directory),
                task(),
                "native+miller-lexical",
                known_change=baseline_expectation("red", ("known-red",)),
            )

        self.assertTrue(session.evidence.success)
        self.assertEqual("red", session.evidence.warmup_verdict)
        self.assertEqual(
            ("known-red",), session.evidence.baseline_failure_test_case_ids
        )

    def test_unexpected_baseline_failure_is_rejected(self):
        project = {"project_path": "/workspace/tests", "framework": "pytest"}
        executor = FakeExecutor(
            [
                reply(discovery([project])),
                reply(enabled([project])),
                reply({"status": "started", "reason": None, "pid": 42}),
                reply(warmup("red")),
                reply(failure_report(("known-red", "new-red"))),
                reply({"status": "stopped", "reason": None}),
                reply({**enabled([]), "operation": "disable"}),
            ]
        )
        lifecycle = CtLifecycle.from_manifest(
            config(), miller_path="miller", executor=executor
        )

        with tempfile.TemporaryDirectory() as directory:
            session = lifecycle.prepare(
                Path(directory),
                task(),
                "native+miller-lexical",
                known_change=baseline_expectation("red", ("known-red",)),
            )

        self.assertFalse(session.evidence.success)
        self.assertEqual("baseline_failures_mismatch", session.evidence.disposition)

    def test_provider_discovery_failure_cannot_be_qualified_as_baseline_red(self):
        project = {"project_path": "/workspace/tests", "framework": "pytest"}
        provider_failure = "ct-discovery-failure:fixture"
        executor = FakeExecutor(
            [
                reply(discovery([project])),
                reply(enabled([project])),
                reply({"status": "started", "reason": None, "pid": 42}),
                reply(warmup("red")),
                reply(failure_report((provider_failure,))),
                reply({"status": "stopped", "reason": None}),
                reply({**enabled([]), "operation": "disable"}),
            ]
        )
        lifecycle = CtLifecycle.from_manifest(
            config(), miller_path="miller", executor=executor
        )

        with tempfile.TemporaryDirectory() as directory:
            session = lifecycle.prepare(
                Path(directory),
                task(),
                "native+miller-lexical",
                known_change=baseline_expectation("red", (provider_failure,)),
            )

        self.assertFalse(session.evidence.success)
        self.assertEqual("baseline_provider_failure", session.evidence.disposition)

    def test_unsupported_provider_never_qualifies(self):
        project = {"project_path": "/workspace/tests", "framework": "pytest"}
        executor = FakeExecutor(
            [
                reply(discovery([project])),
                reply(enabled([project], unsupported_count=1)),
                reply({"status": "already_stopped", "reason": None}),
                reply({**enabled([]), "operation": "disable"}),
            ]
        )
        lifecycle = CtLifecycle.from_manifest(
            config(), miller_path="miller", executor=executor
        )

        with tempfile.TemporaryDirectory() as directory:
            session = lifecycle.prepare(
                Path(directory),
                task(),
                "native+miller-lexical",
                known_change=baseline_expectation(),
            )

        self.assertFalse(session.evidence.success)
        self.assertEqual("unsupported_provider", session.evidence.disposition)

    def test_changed_paths_select_only_the_nearest_governing_project(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            candidate = root / "candidate"
            candidate.mkdir()
            source = candidate / "context.go"
            source.write_text("old\n", encoding="utf-8")
            baseline_sha = source_snapshot_sha256(candidate)
            patch = root / "change.patch"
            patch.write_text(
                "--- a/context.go\n+++ b/context.go\n@@ -1 +1 @@\n-old\n+new\n",
                encoding="utf-8",
            )
            source.write_text("new\n", encoding="utf-8")
            changed_sha = source_snapshot_sha256(candidate)
            source.write_text("old\n", encoding="utf-8")
            known = CtKnownChange(
                patch,
                hashlib.sha256(patch.read_bytes()).hexdigest(),
                ("context.go",),
                baseline_sha,
                changed_sha,
                ("fixture::test",),
                "d" * 64,
                "green",
                (),
            )
            root_project = {
                "project_path": "/workspace/go.mod",
                "framework": "go",
                "unsupported_reason": None,
            }
            example_project = {
                "project_path": "/workspace/_examples/rest/go.mod",
                "framework": "go",
                "unsupported_reason": None,
            }
            executor = FakeExecutor(
                [
                    reply(discovery([root_project, example_project])),
                    reply(enabled([root_project])),
                    reply({"status": "started", "reason": None, "pid": 42}),
                    reply(warmup()),
                    reply(failure_report()),
                    reply(status(cases=7)),
                    reply({"status": "stopped", "reason": None}),
                    reply({**enabled([]), "operation": "disable"}),
                ]
            )
            lifecycle = CtLifecycle.from_manifest(
                config(), miller_path="/opt/miller/miller", executor=executor
            )

            session = lifecycle.prepare(
                candidate,
                task(snapshot_sha256=baseline_sha),
                "native+miller-lexical",
                known_change=known,
            )
            cleanup = session.cleanup()

        self.assertTrue(session.evidence.success)
        self.assertEqual(
            ("/workspace/go.mod",), session.evidence.selected_project_paths
        )
        self.assertEqual(
            ("/workspace/_examples/rest/go.mod", "/workspace/go.mod"),
            session.evidence.discovered_project_paths,
        )
        enable_argv = next(call[0] for call in executor.calls if call[0][2] == "enable")
        self.assertEqual(
            ("enable", "--project", "/workspace/go.mod", "--json"), enable_argv[-4:]
        )
        self.assertTrue(cleanup.success)

    def test_empty_inventory_polls_until_deadline_then_cleans_up(self):
        clock = ManualClock()
        project = {"project_path": "/workspace/tests", "framework": "pytest"}
        executor = FakeExecutor(
            [
                reply(discovery([project])),
                reply(enabled([project])),
                reply({"status": "started", "reason": None, "pid": 42}),
                reply(warmup()),
                reply(failure_report()),
                *[reply(status(cases=0)) for _ in range(4)],
                reply({"status": "stopped", "reason": None}),
                reply({**enabled([]), "operation": "disable"}),
            ]
        )
        lifecycle = CtLifecycle.from_manifest(
            config(),
            miller_path="miller",
            executor=executor,
            clock=clock,
            sleeper=clock.sleep,
        )

        with tempfile.TemporaryDirectory() as directory:
            session = lifecycle.prepare(
                Path(directory),
                task(),
                "native+miller-lexical",
                known_change=baseline_expectation(),
            )

        self.assertFalse(session.evidence.success)
        self.assertEqual("inventory_timeout", session.evidence.disposition)
        self.assertGreaterEqual(
            len([call for call in executor.calls if call[0][2] == "status"]), 2
        )
        self.assertTrue(session.cleanup().success)

    def test_inventory_warmup_shares_one_setup_deadline(self):
        clock = ManualClock()
        project = {"project_path": "/workspace/tests", "framework": "pytest"}
        executor = AdvancingExecutor(
            [
                reply(discovery([project])),
                reply(enabled([project])),
                reply({"status": "started", "reason": None, "pid": 42}),
                reply({"status": "stopped", "reason": None}),
                reply({**enabled([]), "operation": "disable"}),
            ],
            clock,
            0.75,
        )
        lifecycle = CtLifecycle.from_manifest(
            config(command_timeout_seconds=30, readiness_timeout_seconds=2),
            miller_path="miller",
            executor=executor,
            clock=clock,
            sleeper=clock.sleep,
        )

        with tempfile.TemporaryDirectory() as directory:
            session = lifecycle.prepare(
                Path(directory),
                task(),
                "native+miller-lexical",
                known_change=baseline_expectation(),
            )

        self.assertFalse(session.evidence.success)
        self.assertEqual("daemon_start_failed", session.evidence.disposition)
        setup_calls = executor.calls[:3]
        self.assertEqual([2.0, 1.25, 0.5], [call[2] for call in setup_calls])
        self.assertFalse(any(call[0][2] == "run" for call in executor.calls))

    def test_changed_index_must_advance_beyond_baseline_before_measurement(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "candidate"
            root.mkdir()
            source = root / "value.py"
            source.write_text("value = 1\n", encoding="utf-8")
            baseline_sha = source_snapshot_sha256(root)
            patch = Path(directory) / "change.patch"
            patch.write_text(
                "--- a/value.py\n+++ b/value.py\n@@ -1 +1 @@\n-value = 1\n+value = 2\n",
                encoding="utf-8",
            )
            source.write_text("value = 2\n", encoding="utf-8")
            changed_sha = source_snapshot_sha256(root)
            source.write_text("value = 1\n", encoding="utf-8")
            known_change = CtKnownChange(
                patch,
                hashlib.sha256(patch.read_bytes()).hexdigest(),
                ("value.py",),
                baseline_sha,
                changed_sha,
                ("fixture::test",),
                "d" * 64,
                "green",
                (),
            )
            project = {"project_path": "/workspace/tests", "framework": "pytest"}
            clock = ManualClock()
            executor = FakeExecutor(
                [
                    reply(discovery([project])),
                    reply(enabled([project])),
                    reply({"status": "started", "reason": None, "pid": 42}),
                    reply(warmup()),
                    reply(failure_report()),
                    reply(status(cases=3, revision=1)),
                    reply({"workspace_id": "fixture", "fresh": True}),
                    *[reply(status(cases=3, revision=1)) for _ in range(4)],
                    reply({"status": "stopped", "reason": None}),
                    reply({**enabled([]), "operation": "disable"}),
                ]
            )
            lifecycle = CtLifecycle.from_manifest(
                config(),
                miller_path="miller",
                executor=executor,
                clock=clock,
                sleeper=clock.sleep,
            )
            session = lifecycle.prepare(
                root,
                task(),
                "native+miller-lexical",
                known_change=known_change,
            )
            source.write_text("value = 2\n", encoding="utf-8")

            transition = lifecycle.wait_for_transition(
                root, session.evidence, known_change
            )
            cleanup = session.cleanup()

        self.assertFalse(transition.success)
        self.assertEqual("freshness_timeout", transition.disposition)
        self.assertEqual(1, transition.baseline_revision)
        self.assertIsNone(transition.changed_revision)
        self.assertTrue(cleanup.success)

    def test_daemon_not_ready_and_cleanup_failure_are_reported(self):
        project = {"project_path": "/workspace/tests", "framework": "pytest"}
        executor = FakeExecutor(
            [
                reply(discovery([project])),
                reply(enabled([project])),
                reply(
                    {"status": "failed", "reason": "daemon failed", "pid": None},
                    returncode=3,
                ),
                VerificationExecution(False, None, "", "stop launch failed"),
                reply({"error": "disable failed"}, returncode=3),
            ]
        )
        lifecycle = CtLifecycle.from_manifest(
            config(), miller_path="miller", executor=executor
        )

        with tempfile.TemporaryDirectory() as directory:
            session = lifecycle.prepare(
                Path(directory),
                task(),
                "native+miller-lexical",
                known_change=baseline_expectation(),
            )

        self.assertFalse(session.evidence.success)
        self.assertEqual("daemon_start_failed", session.evidence.disposition)
        self.assertFalse(session.cleanup().success)
        self.assertEqual("cleanup_failed", session.cleanup().disposition)

    def test_cleanup_retries_one_graceful_stop_before_disabling(self):
        project = {"project_path": "/workspace/tests", "framework": "pytest"}
        executor = FakeExecutor(
            [
                reply(discovery([project])),
                reply(enabled([project])),
                reply({"status": "started", "reason": None, "pid": 42}),
                reply(warmup()),
                reply(failure_report()),
                reply(status(cases=2)),
                reply(
                    {"status": "failed", "reason": "process still live"},
                    returncode=3,
                ),
                reply({"status": "stopped", "reason": None}),
                reply({**enabled([]), "operation": "disable"}),
            ]
        )
        lifecycle = CtLifecycle.from_manifest(
            config(), miller_path="miller", executor=executor, sleeper=lambda _: None
        )

        with tempfile.TemporaryDirectory() as directory:
            session = lifecycle.prepare(
                Path(directory),
                task(),
                "native+miller-lexical",
                known_change=baseline_expectation(),
            )
            cleanup = session.cleanup()

        self.assertTrue(cleanup.success)
        self.assertEqual(
            ["stop", "stop", "disable"],
            [command.action for command in cleanup.commands],
        )

    def test_manifest_and_workflow_are_strict(self):
        executor = FakeExecutor([])
        with self.assertRaisesRegex(ValueError, "fields"):
            CtLifecycle.from_manifest(
                {**config(), "enable_argv": ["anything"]},
                miller_path="miller",
                executor=executor,
            )
        lifecycle = CtLifecycle.from_manifest(
            config(), miller_path="miller", executor=executor
        )
        with (
            tempfile.TemporaryDirectory() as directory,
            self.assertRaisesRegex(ValueError, "test_selection"),
        ):
            lifecycle.prepare(Path(directory), task("concept"), "native+miller-lexical")

    def test_known_change_rejects_hash_and_declared_path_mismatch(self):
        with tempfile.TemporaryDirectory() as directory:
            patch = Path(directory) / "change.patch"
            patch.write_text(
                "--- a/value.py\n+++ b/value.py\n@@ -1 +1 @@\n-a\n+b\n",
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "bytes"):
                CtKnownChange(
                    patch,
                    "a" * 64,
                    ("value.py",),
                    "b" * 64,
                    "c" * 64,
                    ("fixture::test",),
                    "d" * 64,
                    "green",
                    (),
                )
            with self.assertRaisesRegex(ValueError, "declared paths"):
                CtKnownChange(
                    patch,
                    hashlib.sha256(patch.read_bytes()).hexdigest(),
                    ("other.py",),
                    "b" * 64,
                    "c" * 64,
                    ("fixture::test",),
                    "d" * 64,
                    "green",
                    (),
                )
            valid_sha = hashlib.sha256(patch.read_bytes()).hexdigest()
            with self.assertRaisesRegex(ValueError, "partial"):
                CtKnownChange(
                    patch,
                    valid_sha,
                    ("value.py",),
                    "b" * 64,
                    "c" * 64,
                    ("fixture::test",),
                    "d" * 64,
                    "partial",
                    (),
                )
            with self.assertRaisesRegex(ValueError, "qualification"):
                CtKnownChange(
                    patch,
                    valid_sha,
                    ("value.py",),
                    "b" * 64,
                    "c" * 64,
                    ("fixture::test",),
                    "not-a-digest",
                    "green",
                    (),
                )

    def test_persistent_supervisor_uses_one_cid_for_lifecycle_and_agent(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            candidate = root / "candidate"
            candidate.mkdir()
            source = candidate / "context.go"
            source.write_text("old\n", encoding="utf-8")
            baseline_sha = source_snapshot_sha256(candidate)
            source.write_text("new\n", encoding="utf-8")
            changed_sha = source_snapshot_sha256(candidate)
            source.write_text("old\n", encoding="utf-8")
            cidfile = root / "private" / "attempt.cid"
            cidfile.parent.mkdir()
            prompt = root / "private" / "prompt.txt"
            prompt.write_text("select tests", encoding="utf-8")
            prompt.chmod(0o600)
            patch = root / "private" / "known-change.patch"
            patch.write_text(
                "--- a/context.go\n+++ b/context.go\n@@ -1 +1 @@\n-old\n+new\n",
                encoding="utf-8",
            )
            raw = root / "private" / "events.jsonl"
            stderr = root / "private" / "stderr.log"
            project = {"project_path": "/workspace/tests", "framework": "pytest"}

            def apply_change(argv, stdin_path):
                if argv[-7:] == (
                    "/opt/miller/miller",
                    "workspace",
                    "open",
                    "--path",
                    "/workspace",
                    "--full",
                    "--json",
                ):
                    runtime = candidate / ".miller"
                    runtime.mkdir()
                    (runtime / "symbols.db").write_text("runtime", encoding="utf-8")
                if (
                    "git" in argv
                    and "apply" in argv
                    and argv[-1] == "-"
                    and "--check" not in argv
                ):
                    self.assertEqual(patch, stdin_path)
                    source.write_text("new\n", encoding="utf-8")

            host = FakeHost(
                cidfile,
                [
                    VerificationExecution(True, 0),
                    VerificationExecution(True, 0),
                    reply({"workspace_id": "fixture", "fresh": True}),
                    reply(discovery([project])),
                    reply(enabled([project])),
                    reply({"status": "started", "reason": None, "pid": 42}),
                    reply(warmup()),
                    reply(failure_report()),
                    reply(status(cases=4, revision=1)),
                    VerificationExecution(True, 0),
                    VerificationExecution(True, 0),
                    reply({"workspace_id": "fixture", "fresh": True}),
                    reply(status(cases=4, revision=2)),
                    reply(
                        {
                            "failures": [
                                {
                                    "test_case_id": "fixture::test",
                                    "state": "red",
                                    "index_identity": "fixture",
                                    "revision": 2,
                                    "failure_summary": "assertion failed",
                                }
                            ],
                            "truncated": 0,
                            "total": 1,
                            "offset": 0,
                        }
                    ),
                    VerificationExecution(True, 0),
                    reply({"status": "stopped", "reason": None}),
                    reply({**enabled([]), "operation": "disable"}),
                    VerificationExecution(True, 0),
                ],
                on_execute=apply_change,
            )
            supervisor = PersistentCtAttemptSupervisor(host)
            lifecycle = CtLifecycle.from_manifest(
                config(), miller_path="/opt/miller/miller", executor=supervisor
            )
            digest = "a" * 64
            spec = CtContainerSpec(
                podman_path="podman",
                image_reference="localhost/miller@sha256:" + digest,
                container_create_argv=(
                    "podman",
                    "create",
                    "--init",
                    "--cidfile",
                    str(cidfile),
                    "--network=none",
                    "--mount",
                    f"type=bind,src={candidate.resolve()},dst=/workspace,rw,Z",
                    "localhost/miller@sha256:" + digest,
                    "sleep",
                    "infinity",
                ),
                codex_exec_argv=(
                    "/usr/local/bin/codex",
                    "exec",
                    "--json",
                    '--config=mcp_servers.miller.env.MILLER_SEMANTIC="off"',
                ),
                prompt_path=prompt,
                raw_events_path=raw,
                stderr_path=stderr,
                cidfile=cidfile,
                timeout_seconds=60,
                candidate_root=candidate,
                arm_id="native+miller-lexical",
                known_change=CtKnownChange(
                    patch,
                    hashlib.sha256(patch.read_bytes()).hexdigest(),
                    ("context.go",),
                    baseline_sha,
                    changed_sha,
                    ("fixture::test",),
                    "d" * 64,
                    "green",
                    (),
                ),
            )
            with self.assertRaisesRegex(ValueError, "init"):
                replace(
                    spec,
                    container_create_argv=tuple(
                        part for part in spec.container_create_argv if part != "--init"
                    ),
                )

            outcome = supervisor.run(
                spec,
                lifecycle,
                task(snapshot_sha256=baseline_sha),
                "native+miller-lexical",
            )

        self.assertTrue(outcome.success)
        self.assertEqual(0, outcome.returncode)
        self.assertEqual("ready", outcome.lifecycle.disposition)
        self.assertEqual(baseline_sha, outcome.transition.baseline_snapshot_sha256)
        self.assertEqual(changed_sha, outcome.transition.changed_snapshot_sha256)
        self.assertEqual(1, outcome.transition.baseline_revision)
        self.assertEqual(2, outcome.transition.changed_revision)
        self.assertEqual(
            ("fixture::test",), outcome.transition.observed_failure_test_case_ids
        )
        self.assertEqual(changed_sha, outcome.measured_snapshot_sha256)
        commands = [call[0] for call in host.calls]
        exec_commands = [
            command for command in commands if command[:2] == ("podman", "exec")
        ]
        self.assertEqual(15, len(exec_commands))
        self.assertTrue(
            all(command.count("ct-container") == 1 for command in exec_commands)
        )
        self.assertTrue(
            all(
                "--env" in command
                and ("--env", "HOME=/runtime/home")
                == command[command.index("--env") : command.index("--env") + 2]
                for command in exec_commands
            )
        )
        self.assertTrue(
            all(
                ("--env", "MILLER_SEMANTIC=off") in tuple(pairwise(command))
                for command in exec_commands
            )
        )
        patch_commands = [command for command in exec_commands if "git" in command]
        self.assertEqual(2, len(patch_commands))
        self.assertTrue(all("--unidiff-zero" in command for command in patch_commands))
        self.assertEqual(("podman", "rm", "--force", "ct-container"), commands[-1])
        self.assertTrue(outcome.container_removed)

    def test_persistent_supervisor_cleans_same_cid_when_agent_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            candidate = root / "candidate"
            candidate.mkdir()
            source = candidate / "value.py"
            source.write_text("value = 1\n", encoding="utf-8")
            baseline_sha = source_snapshot_sha256(candidate)
            source.write_text("value = 2\n", encoding="utf-8")
            changed_sha = source_snapshot_sha256(candidate)
            source.write_text("value = 1\n", encoding="utf-8")
            cidfile = root / "private" / "attempt.cid"
            cidfile.parent.mkdir()
            prompt = root / "private" / "prompt.txt"
            prompt.write_text("select tests", encoding="utf-8")
            prompt.chmod(0o600)
            patch = root / "private" / "known-change.patch"
            patch.write_text(
                "--- a/value.py\n+++ b/value.py\n@@ -1 +1 @@\n-value = 1\n+value = 2\n",
                encoding="utf-8",
            )
            raw = root / "private" / "events.jsonl"
            stderr = root / "private" / "stderr.log"
            project = {"project_path": "/workspace/tests", "framework": "pytest"}

            def apply_change(argv, stdin_path):
                if (
                    "git" in argv
                    and "apply" in argv
                    and argv[-1] == "-"
                    and "--check" not in argv
                ):
                    source.write_text("value = 2\n", encoding="utf-8")

            host = FakeHost(
                cidfile,
                [
                    VerificationExecution(True, 0),
                    VerificationExecution(True, 0),
                    reply({"workspace_id": "fixture", "fresh": True}),
                    reply(discovery([project])),
                    reply(enabled([project])),
                    reply({"status": "started", "reason": None, "pid": 42}),
                    reply(warmup()),
                    reply(failure_report()),
                    reply(status(cases=2, revision=1)),
                    VerificationExecution(True, 0),
                    VerificationExecution(True, 0),
                    reply({"workspace_id": "fixture", "fresh": True}),
                    reply(status(cases=2, revision=2)),
                    reply(
                        {
                            "failures": [
                                {
                                    "test_case_id": "fixture::test",
                                    "state": "red",
                                    "index_identity": "fixture",
                                    "revision": 2,
                                    "failure_summary": "assertion failed",
                                }
                            ],
                            "truncated": 0,
                            "total": 1,
                            "offset": 0,
                        }
                    ),
                    VerificationExecution(True, 7),
                    reply({"status": "stopped", "reason": None}),
                    reply({**enabled([]), "operation": "disable"}),
                    VerificationExecution(True, 0),
                ],
                on_execute=apply_change,
            )
            supervisor = PersistentCtAttemptSupervisor(host)
            lifecycle = CtLifecycle.from_manifest(
                config(), miller_path="/opt/miller/miller", executor=supervisor
            )
            digest = "b" * 64
            spec = CtContainerSpec(
                "podman",
                "localhost/miller@sha256:" + digest,
                (
                    "podman",
                    "create",
                    "--init",
                    "--cidfile",
                    str(cidfile),
                    "--mount",
                    f"type=bind,src={candidate.resolve()},dst=/workspace,rw,Z",
                    "localhost/miller@sha256:" + digest,
                    "sleep",
                    "infinity",
                ),
                (
                    "/usr/local/bin/codex",
                    "exec",
                    "--json",
                    '--config=mcp_servers.miller.env.MILLER_SEMANTIC="off"',
                ),
                prompt,
                raw,
                stderr,
                cidfile,
                60,
                candidate,
                "native+miller-lexical",
                CtKnownChange(
                    patch,
                    hashlib.sha256(patch.read_bytes()).hexdigest(),
                    ("value.py",),
                    baseline_sha,
                    changed_sha,
                    ("fixture::test",),
                    "d" * 64,
                    "green",
                    (),
                ),
            )

            outcome = supervisor.run(
                spec,
                lifecycle,
                task(snapshot_sha256=baseline_sha),
                "native+miller-lexical",
            )

        self.assertFalse(outcome.success)
        self.assertEqual(7, outcome.returncode)
        self.assertTrue(outcome.cleanup.success)
        self.assertEqual(("podman", "rm", "--force", "ct-container"), host.calls[-1][0])

    def test_persistent_supervisor_refuses_source_drift_before_applying_change(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            candidate = root / "candidate"
            candidate.mkdir()
            source = candidate / "value.py"
            source.write_text("value = 1\n", encoding="utf-8")
            baseline_sha = source_snapshot_sha256(candidate)
            source.write_text("value = 2\n", encoding="utf-8")
            changed_sha = source_snapshot_sha256(candidate)
            source.write_text("value = 1\n", encoding="utf-8")
            private = root / "private"
            private.mkdir()
            cidfile = private / "attempt.cid"
            prompt = private / "prompt.txt"
            prompt.write_text("select tests", encoding="utf-8")
            prompt.chmod(0o600)
            patch = private / "known-change.patch"
            patch.write_text(
                "--- a/value.py\n+++ b/value.py\n@@ -1 +1 @@\n-value = 1\n+value = 2\n",
                encoding="utf-8",
            )
            project = {"project_path": "/workspace/tests", "framework": "pytest"}

            def drift_source(argv, stdin_path):
                del stdin_path
                if argv[-3:] == ("tests", "status", "--json"):
                    source.write_text("tampered\n", encoding="utf-8")

            host = FakeHost(
                cidfile,
                [
                    VerificationExecution(True, 0),
                    VerificationExecution(True, 0),
                    reply({"workspace_id": "fixture", "fresh": True}),
                    reply(discovery([project])),
                    reply(enabled([project])),
                    reply({"status": "started", "reason": None, "pid": 42}),
                    reply(warmup()),
                    reply(failure_report()),
                    reply(status(cases=2, revision=1)),
                    reply({"status": "stopped", "reason": None}),
                    reply({**enabled([]), "operation": "disable"}),
                    VerificationExecution(True, 0),
                ],
                on_execute=drift_source,
            )
            supervisor = PersistentCtAttemptSupervisor(host)
            lifecycle = CtLifecycle.from_manifest(
                config(), miller_path="/opt/miller/miller", executor=supervisor
            )
            digest = "c" * 64
            spec = CtContainerSpec(
                "podman",
                "localhost/miller@sha256:" + digest,
                (
                    "podman",
                    "create",
                    "--init",
                    "--cidfile",
                    str(cidfile),
                    "--mount",
                    f"type=bind,src={candidate.resolve()},dst=/workspace,rw,Z",
                    "localhost/miller@sha256:" + digest,
                    "sleep",
                    "infinity",
                ),
                (
                    "/usr/local/bin/codex",
                    "exec",
                    "--json",
                    '--config=mcp_servers.miller.env.MILLER_SEMANTIC="off"',
                ),
                prompt,
                private / "events.jsonl",
                private / "stderr.log",
                cidfile,
                60,
                candidate,
                "native+miller-lexical",
                CtKnownChange(
                    patch,
                    hashlib.sha256(patch.read_bytes()).hexdigest(),
                    ("value.py",),
                    baseline_sha,
                    changed_sha,
                    ("fixture::test",),
                    "d" * 64,
                    "green",
                    (),
                ),
            )

            outcome = supervisor.run(
                spec,
                lifecycle,
                task(snapshot_sha256=baseline_sha),
                "native+miller-lexical",
            )

        self.assertFalse(outcome.success)
        self.assertEqual("baseline_snapshot_mismatch", outcome.disposition)
        self.assertFalse(
            any("git" in call[0] and "apply" in call[0] for call in host.calls)
        )
        self.assertTrue(outcome.cleanup.success)
        self.assertTrue(outcome.container_removed)

    def test_native_control_applies_identical_change_without_ct_commands(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            candidate = root / "candidate"
            candidate.mkdir()
            source = candidate / "value.py"
            source.write_text("value = 1\n", encoding="utf-8")
            baseline_sha = source_snapshot_sha256(candidate)
            source.write_text("value = 2\n", encoding="utf-8")
            changed_sha = source_snapshot_sha256(candidate)
            source.write_text("value = 1\n", encoding="utf-8")
            private = root / "private"
            private.mkdir()
            cidfile = private / "attempt.cid"
            prompt = private / "prompt.txt"
            prompt.write_text("select tests", encoding="utf-8")
            prompt.chmod(0o600)
            patch = private / "known-change.patch"
            patch.write_text(
                "--- a/value.py\n+++ b/value.py\n@@ -1 +1 @@\n-value = 1\n+value = 2\n",
                encoding="utf-8",
            )

            def apply_change(argv, stdin_path):
                if (
                    "git" in argv
                    and "apply" in argv
                    and argv[-1] == "-"
                    and "--check" not in argv
                ):
                    self.assertEqual(patch, stdin_path)
                    source.write_text("value = 2\n", encoding="utf-8")

            host = FakeHost(
                cidfile,
                [
                    VerificationExecution(True, 0),
                    VerificationExecution(True, 0),
                    VerificationExecution(True, 0),
                    VerificationExecution(True, 0),
                    VerificationExecution(True, 0),
                    VerificationExecution(True, 0),
                ],
                on_execute=apply_change,
            )
            supervisor = PersistentCtAttemptSupervisor(host)
            lifecycle = CtLifecycle.from_manifest(
                config(), miller_path="/opt/miller/miller", executor=supervisor
            )
            digest = "d" * 64
            spec = CtContainerSpec(
                "podman",
                "localhost/miller@sha256:" + digest,
                (
                    "podman",
                    "create",
                    "--init",
                    "--cidfile",
                    str(cidfile),
                    "--mount",
                    f"type=bind,src={candidate.resolve()},dst=/workspace,rw,Z",
                    "localhost/miller@sha256:" + digest,
                    "sleep",
                    "infinity",
                ),
                ("/usr/local/bin/codex", "exec", "--json"),
                prompt,
                private / "events.jsonl",
                private / "stderr.log",
                cidfile,
                60,
                candidate,
                "native",
                CtKnownChange(
                    patch,
                    hashlib.sha256(patch.read_bytes()).hexdigest(),
                    ("value.py",),
                    baseline_sha,
                    changed_sha,
                    ("fixture::test",),
                    "d" * 64,
                    "green",
                    (),
                ),
            )

            outcome = supervisor.run(
                spec,
                lifecycle,
                task(snapshot_sha256=baseline_sha),
                "native",
            )

        self.assertTrue(outcome.success)
        self.assertEqual(changed_sha, outcome.measured_snapshot_sha256)
        self.assertEqual(
            "native_control_changed_source", outcome.transition.disposition
        )
        self.assertEqual((), outcome.lifecycle.commands)
        self.assertEqual((), outcome.transition.commands)
        self.assertEqual((), outcome.cleanup.commands)
        self.assertFalse(
            any("/opt/miller/miller" in part for call in host.calls for part in call[0])
        )


if __name__ == "__main__":
    unittest.main()
