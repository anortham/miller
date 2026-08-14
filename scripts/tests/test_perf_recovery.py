from __future__ import annotations

import contextlib
import dataclasses
import hashlib
import importlib.util
import io
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import textwrap
import time
import unittest
from types import SimpleNamespace
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "perf-recovery.py"
SPEC = importlib.util.spec_from_file_location("perf_recovery", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
perf_recovery = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = perf_recovery
SPEC.loader.exec_module(perf_recovery)


def _python_command(source: str) -> list[str]:
    return [sys.executable, "-c", textwrap.dedent(source)]


class PerfRecoveryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.workspace = self.root / "workspace"
        self.workspace.mkdir()
        miller_dir = self.workspace / ".miller"
        miller_dir.mkdir()
        self.live_store = miller_dir / "store.db"
        self.live_store.write_bytes(b"live store")
        self.store_copy = self._family_root("store-copy")
        self._write_pointer(self.store_copy)
        self.miller_home = self.root / "miller-home"

    def tearDown(self) -> None:
        self.temp.cleanup()

    def _request(self, **kwargs):
        values = {
            "store_copy": self.store_copy,
            "live_store": self.live_store,
            "workspace": self.workspace,
            "miller_home": self.miller_home,
        }
        values.update(kwargs)
        return perf_recovery.ReplayRequest(**values)

    def _family_root(self, name: str) -> Path:
        family = self.root / name
        generation = family / "gen-001"
        generation.mkdir(parents=True)
        (family / "CURRENT").write_text("gen-001\n", encoding="utf-8")
        (generation / "store.db").write_bytes(b"family store")
        (family / "coord.db").write_bytes(b"coordinator")
        return family

    def _write_pointer(self, store_root: Path) -> None:
        self._write_pointer_at(self.workspace, store_root, view_id="view-1")

    @staticmethod
    def _write_pointer_at(workspace: Path, store_root: Path, *, view_id: str, family_id: str = "11111111-1111-1111-1111-111111111111") -> None:
        pointer = workspace / ".miller" / "store.json"
        pointer.write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "family_id": family_id,
                    "store_root": str(store_root),
                    "view_id": view_id,
                    "workspace_root": str(workspace),
                }
            ),
            encoding="utf-8",
        )

    @staticmethod
    def _result(*, stdout: bytes = b"{}", exit_code: int = 0) -> perf_recovery.CommandResult:
        return perf_recovery.CommandResult(
            exit_code=exit_code,
            timed_out=False,
            wall_ms=1,
            cpu_ms=1,
            output_sha256=hashlib.sha256(stdout).hexdigest(),
            stderr_sha256=hashlib.sha256(b"").hexdigest(),
            peak_rss_bytes=None,
            peak_pss_bytes=None,
            private_usage_bytes=None,
            hard_memory_bytes=None,
            hard_memory_metric=None,
            io={},
            stdout=stdout,
            stderr=b"",
        )

    def _resolve_item(self, *, resolution_scope: str = "full") -> dict[str, object]:
        return {
            "id": f"producer.resolve.{resolution_scope}",
            "execution_kind": "julie_store",
            "command": [
                "store",
                "resolve",
                "--store",
                "{store_copy}",
                "--view",
                "{view}",
                "--request-id",
                f"resolve-{resolution_scope}",
                "--idempotency-key",
                f"resolve-{resolution_scope}-key",
                "--request-timeout-seconds",
                "1501" if resolution_scope == "full" else "30",
                "--json",
            ],
            "warmups": 0,
            "runs": 1,
            "timeout_ms": 1_531_000 if resolution_scope == "full" else 60_000,
            "hard_budget_ms": {"development": 60_000, "windows": 120_000},
            "mutates_store": False,
            "metadata": {"resolution_scope": resolution_scope, "changed_path": "README.md"},
        }

    def _record_for_pair(self, workload_id: str) -> perf_recovery.ReplayRecord:
        return perf_recovery.ReplayRecord(
            workload_id=workload_id,
            platform="linux",
            commit="abc",
            producer_version=None,
            wall_ms=1,
            cpu_ms=1,
            peak_rss_bytes=None,
            peak_pss_bytes=None,
            output_sha256="digest",
            exit_code=0,
            timed_out=False,
            hard_gate_passed=True,
        )

    @staticmethod
    def _mcp_workload(workload_id: str) -> perf_recovery.Workload:
        return perf_recovery.Workload(
            workload_id=workload_id,
            command=("serve",),
            warmups=0,
            runs=1,
            hard_budget_ms={"development": 2_000, "windows": 5_000},
            timeout_ms=60_000,
            execution_kind="mcp_bootstrap",
        )

    def test_refuses_live_store_path_before_process_launch(self) -> None:
        marker = self.root / "launched"
        request = self._request(store_copy=self.live_store)
        command = _python_command(f"from pathlib import Path; Path({str(marker)!r}).touch()")
        with self.assertRaisesRegex(ValueError, "store-copy must not be the live store"):
            perf_recovery.run_command(request, command, timeout_ms=100)
        self.assertFalse(marker.exists())

    def test_refuses_parent_child_and_canonical_aliases(self) -> None:
        store_directory = self.root / "store-directory"
        store_directory.mkdir()
        live = store_directory / "store.db"
        live.write_bytes(b"live")
        child_copy = store_directory / "copy.db"
        child_copy.write_bytes(b"copy")
        with self.assertRaisesRegex(ValueError, "live store"):
            perf_recovery.validate_request(self._request(live_store=store_directory, store_copy=child_copy))
        alias = self.root / "alias.db"
        alias.symlink_to(self.live_store)
        with self.assertRaisesRegex(ValueError, "store-copy must not be the live store"):
            perf_recovery.validate_request(self._request(store_copy=alias))
        hard_link = self.root / "hard-link.db"
        os.link(self.live_store, hard_link)
        with self.assertRaisesRegex(ValueError, "store-copy must not be the live store"):
            perf_recovery.validate_request(self._request(store_copy=hard_link))

    def test_rejects_workspace_pointer_to_live_family_before_process_launch(self) -> None:
        live_family = self._family_root("live-family")
        copied_family = self._family_root("unrelated-copy")
        self._write_pointer(live_family)
        marker = self.root / "live-pointer-launched"
        request = self._request(live_store=live_family, store_copy=copied_family)
        command = _python_command(f"from pathlib import Path; Path({str(marker)!r}).touch()")
        with self.assertRaisesRegex(ValueError, "live store"):
            perf_recovery.run_command(request, command, timeout_ms=100)
        self.assertFalse(marker.exists())

    def test_rejects_pointer_outside_store_copy_before_process_launch(self) -> None:
        live_family = self._family_root("live-family")
        copied_family = self._family_root("copied-family")
        unrelated_family = self._family_root("unrelated-family")
        self._write_pointer(unrelated_family)
        marker = self.root / "wrong-pointer-launched"
        request = self._request(live_store=live_family, store_copy=copied_family)
        command = _python_command(f"from pathlib import Path; Path({str(marker)!r}).touch()")
        with self.assertRaisesRegex(ValueError, "store-copy"):
            perf_recovery.run_command(request, command, timeout_ms=100)
        self.assertFalse(marker.exists())

    def test_correctly_staged_family_pointer_is_active_before_launch(self) -> None:
        live_family = self._family_root("live-family")
        copied_family = self._family_root("copied-family")
        self._write_pointer(copied_family)
        marker = self.root / "copied-pointer-launched"
        request = self._request(live_store=live_family, store_copy=copied_family)
        command = _python_command(f"from pathlib import Path; Path({str(marker)!r}).touch()")
        result = perf_recovery.run_command(request, command, timeout_ms=1_000)
        self.assertEqual(0, result.exit_code)
        self.assertTrue(marker.exists())

    def test_family_pointer_accepts_the_copied_generation_database_selector(self) -> None:
        live_family = self._family_root("live-family")
        copied_family = self._family_root("copied-family")
        self._write_pointer(copied_family)
        marker = self.root / "generation-selector-launched"
        request = self._request(
            live_store=live_family,
            store_copy=copied_family / "gen-001" / "store.db",
        )
        command = _python_command(f"from pathlib import Path; Path({str(marker)!r}).touch()")
        result = perf_recovery.run_command(request, command, timeout_ms=1_000)
        self.assertEqual(0, result.exit_code)
        self.assertTrue(marker.exists())

    def test_rejects_an_unrelated_sibling_file_as_family_copy_selector(self) -> None:
        live_family = self._family_root("live-family")
        copied_family = self._family_root("copied-family")
        unrelated_sibling = copied_family / "unrelated.db"
        unrelated_sibling.write_bytes(b"unrelated")
        self._write_pointer(copied_family)
        marker = self.root / "sibling-selector-launched"
        request = self._request(live_store=live_family, store_copy=unrelated_sibling)
        command = _python_command(f"from pathlib import Path; Path({str(marker)!r}).touch()")
        with self.assertRaisesRegex(ValueError, "store-copy"):
            perf_recovery.run_command(request, command, timeout_ms=100)
        self.assertFalse(marker.exists())

    def test_rejects_family_copy_with_a_live_file_alias_before_launch(self) -> None:
        live_family = self._family_root("live-family")
        copied_family = self._family_root("copied-family")
        (copied_family / "coord.db").unlink()
        (copied_family / "coord.db").symlink_to(live_family / "coord.db")
        self._write_pointer(copied_family)
        marker = self.root / "family-alias-launched"
        request = self._request(live_store=live_family, store_copy=copied_family)
        command = _python_command(f"from pathlib import Path; Path({str(marker)!r}).touch()")
        with self.assertRaisesRegex(ValueError, "complete|outside|alias|live"):
            perf_recovery.run_command(request, command, timeout_ms=100)
        self.assertFalse(marker.exists())

    def test_rejects_family_copy_with_a_live_hardlink_before_launch(self) -> None:
        live_family = self._family_root("live-family")
        copied_family = self._family_root("copied-family")
        (copied_family / "coord.db").unlink()
        os.link(live_family / "coord.db", copied_family / "coord.db")
        self._write_pointer(copied_family)
        marker = self.root / "family-hardlink-launched"
        request = self._request(live_store=live_family, store_copy=copied_family)
        command = _python_command(f"from pathlib import Path; Path({str(marker)!r}).touch()")
        with self.assertRaisesRegex(ValueError, "live store"):
            perf_recovery.run_command(request, command, timeout_ms=100)
        self.assertFalse(marker.exists())

    def test_legacy_artifact_mode_requires_the_active_copy(self) -> None:
        live_family = self._family_root("live-family")
        (self.workspace / ".miller" / "store.json").unlink()
        artifact = self.workspace / ".miller" / "symbols.db"
        artifact.write_bytes(b"legacy copy")
        marker = self.root / "legacy-launched"
        request = self._request(live_store=live_family, store_copy=artifact, store_mode="off")
        command = _python_command(f"from pathlib import Path; Path({str(marker)!r}).touch()")
        result = perf_recovery.run_command(request, command, timeout_ms=1_000)
        self.assertEqual(0, result.exit_code)
        self.assertTrue(marker.exists())

    def test_legacy_artifact_mode_rejects_the_live_artifact_before_launch(self) -> None:
        (self.workspace / ".miller" / "store.json").unlink()
        live_artifact = self.workspace / ".miller" / "symbols.db"
        live_artifact.write_bytes(b"live legacy artifact")
        copied_artifact = self.root / "copied-symbols.db"
        copied_artifact.write_bytes(b"copied legacy artifact")
        marker = self.root / "legacy-live-launched"
        request = self._request(live_store=live_artifact, store_copy=copied_artifact, store_mode="off")
        command = _python_command(f"from pathlib import Path; Path({str(marker)!r}).touch()")
        with self.assertRaisesRegex(ValueError, "live store"):
            perf_recovery.run_command(request, command, timeout_ms=100)
        self.assertFalse(marker.exists())

    def test_legacy_artifact_mode_rejects_an_unrelated_sibling_selector(self) -> None:
        live_family = self._family_root("live-family")
        (self.workspace / ".miller" / "store.json").unlink()
        artifact = self.workspace / ".miller" / "symbols.db"
        artifact.write_bytes(b"legacy copy")
        unrelated_sibling = self.workspace / ".miller" / "unrelated.db"
        unrelated_sibling.write_bytes(b"unrelated")
        marker = self.root / "legacy-sibling-launched"
        request = self._request(live_store=live_family, store_copy=unrelated_sibling, store_mode="off")
        command = _python_command(f"from pathlib import Path; Path({str(marker)!r}).touch()")
        with self.assertRaisesRegex(ValueError, "store-copy"):
            perf_recovery.run_command(request, command, timeout_ms=100)
        self.assertFalse(marker.exists())

    def test_public_cli_requires_an_explicit_live_store(self) -> None:
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                perf_recovery.parse_args(
                    ["--out", str(self.root / "records.jsonl"), "--store-copy", str(self.store_copy)]
                )

    def test_public_cli_launches_a_correctly_staged_family_copy(self) -> None:
        live_family = self._family_root("live-family")
        copied_family = self._family_root("copied-family")
        self._write_pointer(copied_family)
        source_root = self.root / "source-root"
        source_root.mkdir()
        marker = self.root / "cli-launched"

        def fake_replay(request, workloads):
            self.assertEqual(source_root, request.source_root)
            self.assertEqual(self.workspace, request.workspace)
            self.assertEqual(["startup.reader.warm", "tool.trace.warm"], list(workloads))
            self.assertEqual(3, workloads["startup.reader.warm"].runs)
            self.assertEqual(1, workloads["startup.reader.warm"].warmups)
            result = perf_recovery.run_command(
                request,
                _python_command(f"from pathlib import Path; Path({str(marker)!r}).touch()"),
                timeout_ms=1_000,
            )
            self.assertEqual(0, result.exit_code)
            return []

        with mock.patch.object(perf_recovery, "run_replay", side_effect=fake_replay):
            exit_code = perf_recovery.main(
                [
                    "--workloads",
                    str(Path(__file__).resolve().parents[1] / "benchmarks" / "perf-recovery-workloads.json"),
                    "--out",
                    str(self.root / "records.jsonl"),
                    "--miller",
                    "unused-miller",
                    "--workspace",
                    str(self.workspace),
                    "--source-root",
                    str(source_root),
                    "--store-copy",
                    str(copied_family),
                    "--live-store",
                    str(live_family),
                    "--only",
                    "tool.trace.warm,startup.reader.warm",
                    "--runs",
                    "3",
                ]
            )
        self.assertEqual(0, exit_code)
        self.assertTrue(marker.exists())

    def test_public_cli_rejects_live_copy_alias_before_process_launch(self) -> None:
        marker = self.root / "cli-alias-launched"
        with contextlib.redirect_stderr(io.StringIO()):
            with mock.patch.object(perf_recovery, "run_replay", side_effect=AssertionError("Popen must not run")):
                exit_code = perf_recovery.main(
                    [
                        "--out",
                        str(self.root / "records.jsonl"),
                        "--miller",
                        "unused-miller",
                        "--workspace",
                        str(self.workspace),
                        "--store-copy",
                        str(self.store_copy),
                        "--live-store",
                        str(self.store_copy),
                    ]
                )
        self.assertEqual(2, exit_code)
        self.assertFalse(marker.exists())

    def test_public_cli_parses_optional_verification_report(self) -> None:
        args = perf_recovery.parse_args(
            [
                "--out",
                str(self.root / "records.jsonl"),
                "--verification-report",
                str(self.root / "verification.json"),
                "--store-copy",
                str(self.store_copy),
                "--live-store",
                str(self.live_store),
            ]
        )
        self.assertEqual(self.root / "verification.json", args.verification_report)

    def test_public_cli_writes_jsonl_then_atomic_strict_verification_report(self) -> None:
        live_family = self._family_root("live-family-report")
        copied_family = self._family_root("copied-family-report")
        self._write_pointer_at(self.workspace, copied_family, view_id="view-1")
        records_path = self.root / "records.jsonl"
        report_path = self.root / "verification.json"
        records = self._verification_records()
        with mock.patch.object(perf_recovery, "run_replay", return_value=records):
            exit_code = perf_recovery.main(
                [
                    "--out",
                    str(records_path),
                    "--verification-report",
                    str(report_path),
                    "--workspace",
                    str(self.workspace),
                    "--store-copy",
                    str(copied_family),
                    "--live-store",
                    str(live_family),
                ]
            )
        self.assertEqual(0, exit_code)
        self.assertTrue(records_path.is_file())
        self.assertTrue(report_path.is_file())
        self.assertTrue(json.loads(report_path.read_text(encoding="utf-8"))["passed"])

    def test_public_cli_writes_ledger_but_never_pass_report_for_failed_verification(self) -> None:
        live_family = self._family_root("live-family-report-fail")
        copied_family = self._family_root("copied-family-report-fail")
        self._write_pointer_at(self.workspace, copied_family, view_id="view-1")
        records_path = self.root / "records-fail.jsonl"
        report_path = self.root / "verification-fail.json"
        report_path.write_text(json.dumps({"passed": True}), encoding="utf-8")
        records = self._verification_records()
        records[0] = dataclasses.replace(records[0], hard_gate_passed=False)
        with contextlib.redirect_stderr(io.StringIO()), mock.patch.object(perf_recovery, "run_replay", return_value=records):
            exit_code = perf_recovery.main(
                [
                    "--out",
                    str(records_path),
                    "--verification-report",
                    str(report_path),
                    "--workspace",
                    str(self.workspace),
                    "--store-copy",
                    str(copied_family),
                    "--live-store",
                    str(live_family),
                ]
            )
        self.assertEqual(2, exit_code)
        self.assertTrue(records_path.is_file())
        self.assertFalse(report_path.exists())

    def test_public_cli_removes_old_pass_report_when_replay_fails(self) -> None:
        live_family = self._family_root("live-family-report-replay-fail")
        copied_family = self._family_root("copied-family-report-replay-fail")
        self._write_pointer_at(self.workspace, copied_family, view_id="view-1")
        records_path = self.root / "records-replay-fail.jsonl"
        report_path = self.root / "verification-replay-fail.json"
        report_path.write_text(json.dumps({"passed": True}), encoding="utf-8")
        with contextlib.redirect_stderr(io.StringIO()), mock.patch.object(
            perf_recovery, "run_replay", side_effect=RuntimeError("replay failed")
        ):
            exit_code = perf_recovery.main(
                [
                    "--out",
                    str(records_path),
                    "--verification-report",
                    str(report_path),
                    "--workspace",
                    str(self.workspace),
                    "--store-copy",
                    str(copied_family),
                    "--live-store",
                    str(live_family),
                ]
            )
        self.assertEqual(2, exit_code)
        self.assertFalse(report_path.exists())

    def test_only_selection_preserves_manifest_order_and_runs_override_preserves_warmups(self) -> None:
        manifest = perf_recovery.load_manifest(
            Path(__file__).resolve().parents[1] / "benchmarks" / "perf-recovery-workloads.json"
        )
        selected = perf_recovery.select_workloads(
            manifest,
            only="tool.trace.warm,startup.reader.warm",
            runs=3,
        )
        self.assertEqual(["startup.reader.warm", "tool.trace.warm"], list(selected))
        self.assertEqual(3, selected["startup.reader.warm"].runs)
        self.assertEqual(1, selected["startup.reader.warm"].warmups)
        self.assertEqual(3, selected["tool.trace.warm"].runs)
        self.assertEqual(1, selected["tool.trace.warm"].warmups)
        self.assertEqual(3, manifest["startup.reader.warm"].runs)

    def test_only_selection_rejects_empty_duplicate_and_unknown_ids(self) -> None:
        manifest = perf_recovery.load_manifest(
            Path(__file__).resolve().parents[1] / "benchmarks" / "perf-recovery-workloads.json"
        )
        with self.assertRaisesRegex(ValueError, "non-empty"):
            perf_recovery.select_workloads(manifest, only="")
        with self.assertRaisesRegex(ValueError, "empty"):
            perf_recovery.select_workloads(manifest, only="tool.inspect.warm,,tool.trace.warm")
        with self.assertRaisesRegex(ValueError, "duplicate"):
            perf_recovery.select_workloads(manifest, only="tool.inspect.warm,tool.inspect.warm")
        with self.assertRaisesRegex(ValueError, "unknown"):
            perf_recovery.select_workloads(manifest, only="not.a.workload")

    def test_runs_override_rejects_non_positive_values(self) -> None:
        manifest = perf_recovery.load_manifest(
            Path(__file__).resolve().parents[1] / "benchmarks" / "perf-recovery-workloads.json"
        )
        with self.assertRaisesRegex(ValueError, "positive"):
            perf_recovery.select_workloads(manifest, runs=0)
        with self.assertRaisesRegex(ValueError, "positive"):
            perf_recovery.select_workloads(manifest, runs=-1)

    def test_depth_pair_records_output_parity_and_delta(self) -> None:
        depth0 = perf_recovery.ReplayRecord(
            workload_id="tool.context.references.depth0",
            platform="linux",
            commit="abc",
            producer_version="2.32.0",
            wall_ms=100,
            cpu_ms=20,
            peak_rss_bytes=1,
            peak_pss_bytes=1,
            output_sha256="same",
            exit_code=0,
            timed_out=False,
            hard_gate_passed=True,
        )
        depth1 = depth0.__class__(**{**depth0.to_dict(), "workload_id": "tool.context.references.depth1", "wall_ms": 145})
        result = perf_recovery.compare_pair(depth0, depth1)
        self.assertTrue(result.output_digest_match)
        self.assertEqual(45, result.delta_wall_ms)
        self.assertTrue(result.exit_code_match)
        mapping_result = perf_recovery.compare_pair(
            {"output_digest": "left", "wall_ms": 1, "exit_code": 0, "timed_out": False},
            {"output_digest": "right", "wall_ms": 2, "exit_code": 0, "timed_out": False},
        )
        self.assertFalse(mapping_result.output_digest_match)

    def test_timeout_and_exit_are_recorded_without_shell(self) -> None:
        request = self._request()
        timed_out = perf_recovery.run_command(
            request,
            _python_command("import time; time.sleep(0.25)"),
            timeout_ms=40,
        )
        self.assertTrue(timed_out.timed_out)
        self.assertIsNotNone(timed_out.exit_code)
        failed = perf_recovery.run_command(
            request,
            _python_command("import sys; sys.stdout.write('output'); sys.exit(7)"),
            timeout_ms=1_000,
        )
        self.assertEqual(7, failed.exit_code)
        self.assertEqual(hashlib.sha256(b"output").hexdigest(), failed.output_sha256)
        self.assertIn("read_bytes", failed.io)

    def test_environment_isolated_and_lexical_control_is_explicit(self) -> None:
        request = self._request()
        workload = perf_recovery.Workload(
            workload_id="tool.inspect.warm",
            command=("inspect", "--json"),
            warmups=0,
            runs=1,
            hard_budget_ms={"development": 2_000, "windows": 5_000},
            semantic=False,
        )
        command = _python_command(
            "import json, os; print(json.dumps({k: os.environ.get(k) for k in "
            "('MILLER_HOME', 'MILLER_SEMANTIC')}))"
        )
        record = perf_recovery.run_workload(request, workload, command=command)[0]
        self.assertEqual(str(self.miller_home), record.environment["MILLER_HOME"])
        self.assertEqual("off", record.environment["MILLER_SEMANTIC"])
        self.assertEqual("on", record.environment["MILLER_INDEX_STORE"])
        self.assertNotIn("MILLER_PERF_STORE_COPY", record.environment)

    def test_environment_removes_inherited_store_copy_marker(self) -> None:
        environment = perf_recovery.build_environment(
            self._request(),
            base={"MILLER_PERF_STORE_COPY": "stale", "MILLER_HOME": str(self.miller_home)},
        )
        self.assertNotIn("MILLER_PERF_STORE_COPY", environment)

    def test_manifest_has_fixed_ids_and_only_real_cli_contract_flags(self) -> None:
        manifest = perf_recovery.load_manifest(
            Path(__file__).resolve().parents[1] / "benchmarks" / "perf-recovery-workloads.json"
        )
        expected = {
            "startup.reader.warm",
            "startup.leader.no_change",
            "workspace.open.no_change",
            "producer.retry.identical",
            "producer.resolve.one_file",
            "producer.resolve.full",
            "tool.inspect.warm",
            "tool.context.references.depth0",
            "tool.context.references.depth1",
            "tool.context.references.depth1.semantic",
            "tool.context.references.depth1.batch_off",
            "tool.context.references.depth1.batch_on",
            "tool.impact.bounded",
            "tool.trace.warm",
        }
        self.assertEqual(expected, set(manifest))
        for workload in manifest.values():
            self.assertGreaterEqual(workload.warmups, 0)
            self.assertGreaterEqual(workload.runs, 1)
            self.assertGreaterEqual(workload.hard_budget_ms["development"], 1)
            self.assertGreaterEqual(workload.hard_budget_ms["windows"], 1)
            self.assertIsInstance(workload.command, tuple)
            self.assertNotIn("shell", workload.metadata)
        self.assertEqual("mcp_bootstrap", manifest["startup.leader.no_change"].execution_kind)
        self.assertEqual("mcp_bootstrap", manifest["startup.reader.warm"].execution_kind)
        self.assertEqual("julie_store", manifest["producer.resolve.one_file"].execution_kind)
        self.assertEqual("julie_store", manifest["producer.resolve.full"].execution_kind)
        self.assertEqual("store", manifest["producer.resolve.full"].command[0])
        self.assertEqual("resolve", manifest["producer.resolve.full"].command[1])
        retry_command = manifest["producer.retry.identical"].command
        self.assertEqual("{source_view}", retry_command[retry_command.index("--view") + 1])
        self.assertIn("{spool_dir}", retry_command)
        self.assertIn("{progress_file}", retry_command)
        self.assertIn("{parent_pid}", retry_command)
        self.assertEqual("{retry_identity}", retry_command[retry_command.index("--request-id") + 1])
        self.assertEqual("{retry_identity}", retry_command[retry_command.index("--idempotency-key") + 1])
        full_command = manifest["producer.resolve.full"].command
        producer_timeout_ms = int(full_command[full_command.index("--request-timeout-seconds") + 1]) * 1000
        self.assertGreater(
            producer_timeout_ms,
            1_500_000,
        )
        self.assertEqual("1501", full_command[full_command.index("--request-timeout-seconds") + 1])
        self.assertEqual(60_000, manifest["producer.resolve.one_file"].timeout_ms)
        self.assertEqual(1_531_000, manifest["producer.resolve.full"].timeout_ms)
        self.assertGreaterEqual(manifest["producer.resolve.full"].timeout_ms, producer_timeout_ms + 30_000)
        self.assertEqual(1_531_000, manifest["producer.resolve.one_file"].setup_timeout_ms)
        self.assertEqual(1_531_000, manifest["producer.resolve.full"].setup_timeout_ms)
        one_file_command = manifest["producer.resolve.one_file"].command
        self.assertEqual("30", one_file_command[one_file_command.index("--request-timeout-seconds") + 1])
        self.assertIsNone(manifest["tool.context.references.depth1"].parity_with)
        self.assertEqual(
            "tool.context.references.depth1.batch_off",
            manifest["tool.context.references.depth1.batch_on"].parity_with,
        )
        self.assertTrue(all(workload.timeout_ms is not None for workload in manifest.values()))
        for workload in manifest.values():
            self.assertGreater(workload.timeout_ms, max(workload.hard_budget_ms.values()))

    def test_execution_kind_validation_rejects_misclassified_startup_and_producer(self) -> None:
        base = {
            "schema_version": 1,
            "workloads": [
                {
                    "id": "startup.bad",
                    "execution_kind": "miller_cli",
                    "command": ["version"],
                    "warmups": 0,
                    "runs": 1,
                    "timeout_ms": 61_000,
                    "hard_budget_ms": {"development": 1_000, "windows": 2_000},
                }
            ],
        }
        path = self.root / "bad-kind.json"
        path.write_text(json.dumps(base), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "mcp_bootstrap"):
            perf_recovery.load_manifest(path, require_fixed_ids=False)

        base["workloads"][0] = {
            "id": "producer.resolve.bad",
            "execution_kind": "miller_cli",
            "command": ["refresh"],
            "warmups": 0,
            "runs": 1,
            "timeout_ms": 61_000,
            "hard_budget_ms": {"development": 1_000, "windows": 2_000},
        }
        path.write_text(json.dumps(base), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "julie_store"):
            perf_recovery.load_manifest(path, require_fixed_ids=False)

    def test_execution_kind_argvs_use_mcp_and_producer_contracts(self) -> None:
        request = self._request(miller="miller", producer="julie-extract")
        self.assertEqual(["miller", "serve"], perf_recovery._mcp_argv(request))
        workload = perf_recovery.Workload(
            workload_id="producer.resolve.full",
            command=(
                "store",
                "resolve",
                "--store",
                "{store_copy}",
                "--view",
                "{view}",
                "--request-id",
                "resolve-full",
                "--idempotency-key",
                "resolve-full-key",
                "--request-timeout-seconds",
                "30",
                "--json",
            ),
            warmups=0,
            runs=1,
            hard_budget_ms={"development": 1_000, "windows": 2_000},
            timeout_ms=3_000,
            execution_kind="julie_store",
        )
        argv = perf_recovery._producer_argv(request, workload.command)
        self.assertEqual("julie-extract", argv[0])
        self.assertEqual("store", argv[1])
        self.assertIn(str(self.store_copy), argv)

    def test_producer_import_uses_explicit_source_root(self) -> None:
        source_root = self.root / "source-root"
        source_root.mkdir()
        request = self._request(source_root=source_root)
        command = (
            "store",
            "import",
            "--store",
            "{store_copy}",
            "--family",
            "{family}",
            "--root",
            "{source_root}",
            "--view",
            "{view}",
            "--level",
            "full",
            "--request-id",
            "import",
            "--idempotency-key",
            "import-key",
            "--request-timeout-seconds",
            "30",
            "--json",
        )
        argv = perf_recovery._producer_argv(request, command)
        self.assertIn(str(source_root), argv)

    def test_retry_import_uses_source_view_and_disposable_supervision_paths(self) -> None:
        live_family = self._family_root("live-family")
        source_root = self.root / "source-root"
        (source_root / ".miller").mkdir(parents=True)
        self._write_pointer_at(source_root, live_family, view_id="source-view")
        request = self._request(source_root=source_root, live_store=live_family)
        command = (
            "store",
            "import",
            "--store",
            "{store_copy}",
            "--family",
            "{family}",
            "--root",
            "{source_root}",
            "--view",
            "{source_view}",
            "--level",
            "full",
            "--spool-dir",
            "{spool_dir}",
            "--progress-file",
            "{progress_file}",
            "--parent-pid",
            "{parent_pid}",
            "--request-id",
            "retry",
            "--idempotency-key",
            "retry-key",
            "--request-timeout-seconds",
            "30",
            "--json",
        )

        argv = perf_recovery._producer_argv(request, command)

        self.assertEqual("source-view", argv[argv.index("--view") + 1])
        self.assertNotEqual("view-1", argv[argv.index("--view") + 1])
        spool = Path(argv[argv.index("--spool-dir") + 1])
        progress = Path(argv[argv.index("--progress-file") + 1])
        self.assertEqual("spool", spool.name)
        self.assertEqual(spool.parent, progress.parent)

    def test_retry_uses_one_generated_identity_across_attempts_and_avoids_history_conflict(self) -> None:
        live_family = self._family_root("live-family")
        source_root = self.root / "source-root"
        (source_root / ".miller").mkdir(parents=True)
        self._write_pointer_at(source_root, live_family, view_id="source-view")
        request = self._request(source_root=source_root, live_store=live_family)
        manifest_path = Path(__file__).resolve().parents[1] / "benchmarks" / "perf-recovery-workloads.json"
        workload = perf_recovery.load_manifest(manifest_path)["producer.retry.identical"]
        history = {"perf-recovery-import"}
        calls: list[tuple[str, str, str]] = []

        def copy_family(source: Path, destination: Path, *, live_root: Path | None = None) -> None:
            shutil.copytree(source, destination)

        def run_process(process_request, argv, _timeout, _environment):
            request_id = argv[argv.index("--request-id") + 1]
            idempotency_key = argv[argv.index("--idempotency-key") + 1]
            view = argv[argv.index("--view") + 1]
            calls.append((request_id, idempotency_key, view))
            if request_id in history:
                payload = {
                    "state": "failed",
                    "family_id": perf_recovery.resolve_active_store(process_request).family_id,
                    "view_id": view,
                    "manifest": {"generation": None},
                    "failure_class": "idempotency_conflict",
                }
            else:
                payload = {
                    "state": "committed",
                    "family_id": perf_recovery.resolve_active_store(process_request).family_id,
                    "view_id": view,
                    "manifest": {"generation": 5},
                }
            return dataclasses.replace(
                self._result(stdout=json.dumps(payload).encode()),
                peak_pss_bytes=1_024,
                hard_memory_bytes=1_024,
                hard_memory_metric="linux_pss",
            )

        with mock.patch.object(
            perf_recovery,
            "_snapshot_helper_module",
            return_value=SimpleNamespace(snapshot_family=copy_family),
        ), mock.patch.object(perf_recovery, "_run_process", side_effect=run_process):
            records = perf_recovery.run_workload(request, workload)

        self.assertEqual(4, len(records))
        self.assertEqual(1, len({request_id for request_id, _, _ in calls}))
        self.assertEqual(1, len({idempotency for _, idempotency, _ in calls}))
        self.assertEqual(calls[0][0], calls[0][1])
        self.assertNotIn(calls[0][0], history)
        self.assertRegex(calls[0][0], r"^perf-recovery-retry-[0-9a-f]{32}$")
        self.assertNotEqual("view-1", calls[0][2])
        self.assertTrue(all(record.hard_gate_passed for record in records))

    def test_failed_retry_report_with_null_manifest_generation_is_recorded(self) -> None:
        live_family = self._family_root("live-family")
        source_root = self.root / "source-root"
        (source_root / ".miller").mkdir(parents=True)
        self._write_pointer_at(source_root, live_family, view_id="source-view")
        request = self._request(source_root=source_root, live_store=live_family)
        manifest_path = Path(__file__).resolve().parents[1] / "benchmarks" / "perf-recovery-workloads.json"
        workload = dataclasses.replace(
            perf_recovery.load_manifest(manifest_path)["producer.retry.identical"],
            warmups=0,
            runs=1,
        )

        def copy_family(source: Path, destination: Path, *, live_root: Path | None = None) -> None:
            shutil.copytree(source, destination)

        def run_process(process_request, argv, _timeout, _environment):
            payload = {
                "state": "failed",
                "family_id": perf_recovery.resolve_active_store(process_request).family_id,
                "view_id": argv[argv.index("--view") + 1],
                "manifest": {"generation": None, "disposition": "not_published"},
                "failure_class": "idempotency_conflict",
                "error": {"class": "idempotency_conflict", "message": "idempotency_conflict"},
            }
            return self._result(stdout=json.dumps(payload).encode())

        with mock.patch.object(
            perf_recovery,
            "_snapshot_helper_module",
            return_value=SimpleNamespace(snapshot_family=copy_family),
        ), mock.patch.object(perf_recovery, "_run_process", side_effect=run_process):
            records = perf_recovery.run_workload(request, workload)

        self.assertEqual(1, len(records))
        self.assertFalse(records[0].hard_gate_passed)
        self.assertEqual(0, records[0].exit_code)
        self.assertIsNone(records[0].generation)
        self.assertEqual("idempotency_conflict", records[0].metadata["producer_failure"]["failure_class"])

    def test_retry_source_pointer_must_match_copied_family_and_live_store(self) -> None:
        live_family = self._family_root("live-family")
        source_root = self.root / "source-root"
        (source_root / ".miller").mkdir(parents=True)
        command = (
            "store",
            "import",
            "--store",
            "{store_copy}",
            "--family",
            "{family}",
            "--root",
            "{source_root}",
            "--view",
            "{source_view}",
            "--level",
            "full",
            "--request-id",
            "retry",
            "--idempotency-key",
            "retry-key",
            "--request-timeout-seconds",
            "30",
            "--json",
        )
        self._write_pointer_at(
            source_root,
            live_family,
            view_id="source-view",
            family_id="22222222-2222-2222-2222-222222222222",
        )
        with self.assertRaisesRegex(ValueError, "family"):
            perf_recovery._producer_argv(
                self._request(source_root=source_root, live_store=live_family),
                command,
            )

        other_family = self._family_root("other-family")
        self._write_pointer_at(source_root, other_family, view_id="source-view")
        with self.assertRaisesRegex(ValueError, "live store|store"):
            perf_recovery._producer_argv(
                self._request(source_root=source_root, live_store=live_family),
                command,
            )

    def test_source_changing_setup_clones_source_and_adopts_fresh_view_in_order(self) -> None:
        live_family = self._family_root("live-family")
        source_root = self.root / "source-root"
        source_miller = source_root / ".miller"
        source_miller.mkdir(parents=True)
        source_file = source_root / "README.md"
        source_file.write_text("original\n", encoding="utf-8")
        source_pointer = source_miller / "store.json"
        self._write_pointer_at(source_root, live_family, view_id="source-view")
        source_pointer_before = source_pointer.read_bytes()
        source_file_before = source_file.read_bytes()
        request = self._request(source_root=source_root, live_store=live_family)
        item = self._resolve_item(resolution_scope="one_file")
        item["mutates_store"] = True
        item["isolated_snapshot"] = True
        item["timeout_ms"] = 121_000
        workload = perf_recovery._workload_from_mapping(item)
        calls: list[tuple[Path, list[str], str, bytes | None]] = []
        pointer_workspace_roots: list[Path] = []

        def copy_family(source: Path, destination: Path, *, live_root: Path | None = None) -> None:
            shutil.copytree(source, destination)

        def run_process(process_request, argv, _timeout, _environment):
            view = argv[argv.index("--view") + 1]
            root_arg = Path(argv[argv.index("--root") + 1]) if "--root" in argv else None
            content_root = root_arg or process_request.workspace
            content = (content_root / "README.md").read_bytes()
            pointer = json.loads(
                (process_request.workspace / ".miller" / "store.json").read_text(encoding="utf-8")
            )
            pointer_workspace_roots.append(Path(pointer["workspace_root"]))
            calls.append((process_request.workspace, list(argv), pointer["view_id"], content))
            payload = {"family_id": pointer["family_id"], "view": view, "manifest": {"generation": 5}}
            return self._result(stdout=json.dumps(payload).encode())

        with mock.patch.object(
            perf_recovery,
            "_snapshot_helper_module",
            return_value=SimpleNamespace(snapshot_family=copy_family),
        ), mock.patch.object(perf_recovery, "_run_process", side_effect=run_process):
            records = perf_recovery.run_workload(request, workload)

        self.assertEqual(1, len(records))
        self.assertEqual(["import", "resolve", "import", "resolve"], [argv[2] for _, argv, _, _ in calls])
        self.assertEqual(["source-view", "source-view", calls[2][1][calls[2][1].index("--view") + 1], calls[2][1][calls[2][1].index("--view") + 1]], [view for _, _, view, _ in calls])
        self.assertNotEqual("source-view", calls[2][1][calls[2][1].index("--view") + 1])
        self.assertTrue(all(workspace != self.workspace for workspace, _, _, _ in calls))
        self.assertEqual([workspace for workspace, _, _, _ in calls], pointer_workspace_roots)
        self.assertTrue(
            all(
                Path(argv[argv.index("--root") + 1]) == workspace
                for workspace, argv, _, _ in calls
                if "--root" in argv
            )
        )
        self.assertEqual(b"original\n", calls[0][3])
        self.assertEqual(b"original\n", calls[1][3])
        self.assertEqual(b"original\n\n", calls[2][3])
        self.assertEqual(source_pointer_before, source_pointer.read_bytes())
        self.assertEqual(source_file_before, source_file.read_bytes())

    def test_resolve_setup_uses_manifest_timeout_and_observer_grace(self) -> None:
        live_family = self._family_root("live-family")
        source_root = self.root / "source-root"
        source_miller = source_root / ".miller"
        source_miller.mkdir(parents=True)
        (source_root / "README.md").write_text("original\n", encoding="utf-8")
        self._write_pointer_at(source_root, live_family, view_id="source-view")
        request = self._request(source_root=source_root, live_store=live_family)
        item = self._resolve_item(resolution_scope="one_file")
        item["mutates_store"] = True
        item["isolated_snapshot"] = True
        item["hard_budget_ms"] = {"development": 5_000, "windows": 10_000}
        item["setup_timeout_ms"] = 1_531_000
        workload = perf_recovery._workload_from_mapping(item)
        calls: list[tuple[list[str], int]] = []

        def copy_family(source: Path, destination: Path, *, live_root: Path | None = None) -> None:
            shutil.copytree(source, destination)

        def run_process(process_request, argv, timeout, _environment):
            calls.append((list(argv), timeout))
            view = argv[argv.index("--view") + 1]
            family = perf_recovery.resolve_active_store(process_request).family_id
            payload = {"family_id": family, "view": view, "manifest": {"generation": 5}}
            return self._result(stdout=json.dumps(payload).encode())

        with mock.patch.object(
            perf_recovery,
            "_snapshot_helper_module",
            return_value=SimpleNamespace(snapshot_family=copy_family),
        ), mock.patch.object(perf_recovery, "_run_process", side_effect=run_process):
            records = perf_recovery.run_workload(request, workload)

        self.assertEqual(1, len(records))
        self.assertEqual(
            [1_531_000, 1_531_000, 1_531_000, 60_000],
            [timeout for _, timeout in calls],
        )
        self.assertEqual(
            ["1501", "1501", "1501", "30"],
            [argv[argv.index("--request-timeout-seconds") + 1] for argv, _ in calls],
        )

    def test_source_changing_baseline_failure_aborts_before_file_change_or_measurement(self) -> None:
        live_family = self._family_root("live-family")
        source_root = self.root / "source-root"
        (source_root / ".miller").mkdir(parents=True)
        source_file = source_root / "README.md"
        source_file.write_text("original\n", encoding="utf-8")
        self._write_pointer_at(source_root, live_family, view_id="source-view")
        source_before = source_file.read_bytes()
        request = self._request(source_root=source_root, live_store=live_family)
        item = self._resolve_item(resolution_scope="one_file")
        item["mutates_store"] = True
        item["isolated_snapshot"] = True
        item["timeout_ms"] = 121_000
        workload = perf_recovery._workload_from_mapping(item)
        calls: list[list[str]] = []

        def copy_family(source: Path, destination: Path, *, live_root: Path | None = None) -> None:
            shutil.copytree(source, destination)

        def run_process(_request, argv, _timeout, _environment):
            calls.append(list(argv))
            view = argv[argv.index("--view") + 1]
            payload = {"family_id": "11111111-1111-1111-1111-111111111111", "view": view, "generation": "gen-001"}
            return self._result(stdout=json.dumps(payload).encode(), exit_code=1 if len(calls) == 2 else 0)

        with mock.patch.object(
            perf_recovery,
            "_snapshot_helper_module",
            return_value=SimpleNamespace(snapshot_family=copy_family),
        ), mock.patch.object(perf_recovery, "_run_process", side_effect=run_process):
            with self.assertRaisesRegex(RuntimeError, "baseline|setup|resolve"):
                perf_recovery.run_workload(request, workload)

        self.assertEqual(2, len(calls))
        self.assertEqual(source_before, source_file.read_bytes())

    def test_source_root_is_read_only_and_resolve_gets_a_staged_change_root(self) -> None:
        live_family = self._family_root("source-live-family")
        source_root = self.root / "source-root"
        source_miller = source_root / ".miller"
        source_miller.mkdir(parents=True)
        source_file = source_root / "README.md"
        source_file.write_text("original\n", encoding="utf-8")
        source_pointer = source_miller / "store.json"
        self._write_pointer_at(source_root, live_family, view_id="source-view")
        source_pointer_before = source_pointer.read_bytes()
        request = self._request(source_root=source_root, live_store=live_family)

        def copy_family(source: Path, destination: Path, *, live_root: Path | None = None) -> None:
            shutil.copytree(source, destination)

        with mock.patch.object(
            perf_recovery,
            "_snapshot_helper_module",
            return_value=SimpleNamespace(snapshot_family=copy_family),
        ):
            isolated, temporary = perf_recovery._isolated_request(request, source_changing=True)
        try:
            self.assertNotEqual(source_root, isolated.workspace)
            self.assertEqual(isolated.workspace, isolated.source_root)
            self.assertEqual("original\n", source_file.read_text(encoding="utf-8"))
            self.assertEqual(source_pointer_before, source_pointer.read_bytes())
            changed = isolated.workspace / "README.md"
            changed.write_text("changed\n", encoding="utf-8")
            self.assertEqual("original\n", source_file.read_text(encoding="utf-8"))
        finally:
            temporary.cleanup()

    def test_mutating_resolve_setup_does_not_edit_original_source(self) -> None:
        live_family = self._family_root("source-live-family")
        source_root = self.root / "source-root"
        source_miller = source_root / ".miller"
        source_miller.mkdir(parents=True)
        source_file = source_root / "README.md"
        source_file.write_text("original\n", encoding="utf-8")
        source_pointer = source_miller / "store.json"
        self._write_pointer_at(source_root, live_family, view_id="source-view")
        source_pointer_before = source_pointer.read_bytes()
        request = self._request(source_root=source_root, live_store=live_family)
        item = self._resolve_item(resolution_scope="one_file")
        item["mutates_store"] = True
        item["isolated_snapshot"] = True
        item["timeout_ms"] = 121_000
        workload = perf_recovery._workload_from_mapping(item)
        calls: list[list[str]] = []

        def copy_family(source: Path, destination: Path, *, live_root: Path | None = None) -> None:
            shutil.copytree(source, destination)

        def run_process(_request, argv, _timeout, _environment):
            calls.append(list(argv))
            view = argv[argv.index("--view") + 1]
            family = perf_recovery.resolve_active_store(_request).family_id
            return self._result(
                stdout=json.dumps(
                    {"family_id": family, "view": view, "manifest": {"generation": 5}}
                ).encode()
            )

        with mock.patch.object(
            perf_recovery,
            "_snapshot_helper_module",
            return_value=SimpleNamespace(snapshot_family=copy_family),
        ), mock.patch.object(perf_recovery, "_run_process", side_effect=run_process):
            perf_recovery.run_workload(request, workload)

        self.assertEqual("import", calls[0][2])
        staged_change_root = Path(calls[0][calls[0].index("--root") + 1])
        self.assertNotEqual(source_root, staged_change_root)
        self.assertEqual("original\n", source_file.read_text(encoding="utf-8"))
        self.assertEqual(source_pointer_before, source_pointer.read_bytes())

    def test_workspace_open_fails_if_setup_mints_a_new_binding(self) -> None:
        request = self._request()
        workload = perf_recovery.Workload(
            workload_id="workspace.open.no_change",
            command=("workspace", "open", "--path", "{workspace}", "--json"),
            warmups=0,
            runs=1,
            hard_budget_ms={"development": 5_000, "windows": 10_000},
            timeout_ms=60_000,
        )

        def run_command(_request, _command, **_kwargs):
            pointer_path = self.workspace / ".miller" / "store.json"
            pointer = json.loads(pointer_path.read_text(encoding="utf-8"))
            pointer["family_id"] = "22222222-2222-2222-2222-222222222222"
            pointer["view_id"] = "view-2"
            pointer_path.write_text(json.dumps(pointer), encoding="utf-8")
            return self._result()

        with mock.patch.object(perf_recovery, "run_command", side_effect=run_command):
            with self.assertRaisesRegex(RuntimeError, "binding|family|view"):
                perf_recovery.run_workload(request, workload)

    def test_producer_view_change_requires_explicit_staged_adoption(self) -> None:
        request = self._request()
        workload = perf_recovery.Workload(
            workload_id="producer.resolve.full",
            command=(
                "store",
                "resolve",
                "--store",
                "{store_copy}",
                "--view",
                "{view}",
                "--request-id",
                "resolve",
                "--idempotency-key",
                "resolve-key",
                "--request-timeout-seconds",
                "30",
                "--json",
            ),
            warmups=0,
            runs=1,
            hard_budget_ms={"development": 1_000, "windows": 2_000},
            timeout_ms=3_000,
            execution_kind="julie_store",
        )
        result = self._result(stdout=json.dumps({"view_id": "view-2"}).encode())
        with mock.patch.object(perf_recovery, "_run_process", return_value=result):
            with self.assertRaisesRegex(RuntimeError, "adopt|view"):
                perf_recovery.run_workload(request, workload)

    def test_producer_generation_must_match_the_setup_manifest_generation(self) -> None:
        request = self._request()
        (self.workspace / "README.md").write_text("staged\n", encoding="utf-8")
        item = self._resolve_item(resolution_scope="one_file")
        item["timeout_ms"] = 121_000
        workload = perf_recovery._workload_from_mapping(item)
        calls: list[list[str]] = []

        def run_process(process_request, argv, _timeout, _environment):
            calls.append(list(argv))
            view = argv[argv.index("--view") + 1]
            family = perf_recovery.resolve_active_store(process_request).family_id
            generation = 6 if len(calls) == 4 else 5
            return self._result(
                stdout=json.dumps(
                    {"family_id": family, "view": view, "manifest": {"generation": generation}}
                ).encode()
            )

        with mock.patch.object(perf_recovery, "_run_process", side_effect=run_process):
            with self.assertRaisesRegex(RuntimeError, "generation|adopt"):
                perf_recovery.run_workload(request, workload)

    def test_resolve_report_treats_blank_roots_as_unavailable(self) -> None:
        request = self._request()
        active = perf_recovery.resolve_active_store(request)
        payload = {
            "family_id": str(active.family_id),
            "view_id": active.view_id,
            "manifest": {"generation": 5},
            "root": "",
        }

        for root in ("", " \t\n"):
            with self.subTest(root=repr(root)):
                payload["root"] = root
                self.assertEqual(
                    payload,
                    perf_recovery._validate_setup_result(
                        request,
                        self._result(stdout=json.dumps(payload).encode()),
                        expected_view=active.view_id,
                        label="resolve",
                        require_pointer_view=True,
                    ),
                )

        payload["root"] = str(self.root / "other-workspace")
        with self.assertRaisesRegex(RuntimeError, "JSON root does not match"):
            perf_recovery._validate_setup_result(
                request,
                self._result(stdout=json.dumps(payload).encode()),
                expected_view=active.view_id,
                label="resolve",
                require_pointer_view=True,
            )

    def test_nested_manifest_generation_matches_the_selected_view_not_current_directory(self) -> None:
        database = self.store_copy / "gen-001" / "store.db"
        database.unlink()
        connection = sqlite3.connect(database)
        try:
            connection.execute("CREATE TABLE views(view_id TEXT PRIMARY KEY, current_generation INTEGER)")
            connection.execute("INSERT INTO views VALUES (?, ?)", ("view-1", 5))
            connection.commit()
        finally:
            connection.close()
        request = self._request()
        active = perf_recovery.resolve_active_store(request)
        payload = {
            "family_id": active.family_id,
            "view_id": active.view_id,
            "manifest": {"generation": 5},
        }
        perf_recovery._require_producer_binding(
            request,
            active,
            payload,
            label="nested-generation",
        )

    def test_producer_paths_require_operation_specific_runtime_placeholders(self) -> None:
        item = self._resolve_item()
        item["id"] = "producer.resolve.invalid"
        command = list(item["command"])
        command[command.index("{store_copy}")] = "/tmp/store.db"
        item["command"] = command
        with self.assertRaisesRegex(ValueError, "placeholder"):
            perf_recovery._workload_from_mapping(item)

        item = self._resolve_item()
        item["id"] = "producer.resolve.family"
        item["command"] = list(item["command"]) + ["--family", "{family}"]
        workload = perf_recovery._workload_from_mapping(item)
        argv = perf_recovery._producer_argv(self._request(), workload.command)
        self.assertIn("11111111-1111-1111-1111-111111111111", argv)

        item = self._resolve_item()
        item["id"] = "producer.resolve.family_path"
        item["command"] = list(item["command"]) + ["--family", "/tmp/family"]
        with self.assertRaisesRegex(ValueError, "placeholder"):
            perf_recovery._workload_from_mapping(item)

        item = self._resolve_item()
        item["id"] = "producer.resolve.root"
        item["command"] = list(item["command"]) + ["--root", "{workspace}"]
        with self.assertRaisesRegex(ValueError, "root"):
            perf_recovery._workload_from_mapping(item)

    def test_producer_command_override_cannot_bypass_placeholder_validation(self) -> None:
        request = self._request()
        workload = perf_recovery._workload_from_mapping(self._resolve_item())
        with mock.patch.object(perf_recovery, "_run_process", return_value=self._result()):
            with self.assertRaisesRegex(ValueError, "placeholder"):
                perf_recovery.run_workload(
                    request,
                    workload,
                    command=(
                        "store",
                        "resolve",
                        "--store",
                        "/tmp/store.db",
                        "--view",
                        "{view}",
                        "--request-id",
                        "override",
                        "--idempotency-key",
                        "override-key",
                        "--request-timeout-seconds",
                        "30",
                        "--json",
                    ),
                )

    def test_producer_commands_reject_unknown_operation_flags(self) -> None:
        item = self._resolve_item()
        item["id"] = "producer.resolve.unknown_flag"
        item["command"] = list(item["command"]) + ["--family-id", "unexpected"]
        with self.assertRaisesRegex(ValueError, "unknown.*flag|operation"):
            perf_recovery._workload_from_mapping(item)

    def test_resolve_rows_prepare_a_fresh_full_import_before_resolve(self) -> None:
        request = self._request()
        (self.workspace / "README.md").write_text("staged\n", encoding="utf-8")
        workload = perf_recovery._workload_from_mapping(self._resolve_item())
        calls: list[list[str]] = []

        def run_process(process_request, argv, _timeout, _environment):
            calls.append(list(argv))
            view = argv[argv.index("--view") + 1]
            family = perf_recovery.resolve_active_store(process_request).family_id
            return self._result(
                stdout=json.dumps({"family_id": family, "view": view, "generation": "gen-001"}).encode()
            )

        with mock.patch.object(perf_recovery, "_run_process", side_effect=run_process):
            records = perf_recovery.run_workload(request, workload)
        self.assertEqual(4, len(calls))
        self.assertEqual("import", calls[0][2])
        self.assertEqual("resolve", calls[1][2])
        self.assertEqual("import", calls[2][2])
        self.assertEqual("resolve", calls[3][2])
        self.assertNotEqual(calls[0][calls[0].index("--request-id") + 1], calls[1][calls[1].index("--request-id") + 1])
        self.assertEqual(1, len(records))

    def test_resolution_scope_gate_preserves_mismatch_evidence_and_fails_row(self) -> None:
        request = self._request()
        (self.workspace / "README.md").write_text("staged\n", encoding="utf-8")
        workload = perf_recovery._workload_from_mapping(self._resolve_item())
        mismatch_result: perf_recovery.CommandResult | None = None
        def run_process(process_request, argv, _timeout, _environment):
            nonlocal mismatch_result
            if len(calls) < 3:
                calls.append(list(argv))
                view = argv[argv.index("--view") + 1]
                family = perf_recovery.resolve_active_store(process_request).family_id
                return self._result(
                    stdout=json.dumps({"family_id": family, "view": view, "generation": "gen-001"}).encode()
                )
            calls.append(list(argv))
            view = argv[argv.index("--view") + 1]
            family = perf_recovery.resolve_active_store(process_request).family_id
            mismatch_result = self._result(
                stdout=json.dumps(
                    {
                        "family_id": family,
                        "view": view,
                        "generation": "gen-001",
                        "resolution": {"resolution_mode": "scoped", "scope_file_count": 0},
                    }
                ).encode()
            )
            return mismatch_result

        calls: list[list[str]] = []
        with mock.patch.object(perf_recovery, "_run_process", side_effect=run_process):
            record = perf_recovery.run_workload(request, workload)[0]
        self.assertFalse(record.hard_gate_passed)
        self.assertEqual("scoped", record.metadata["resolution_gate"]["actual_mode"])
        self.assertIsNotNone(mismatch_result)
        self.assertEqual(mismatch_result.output_sha256, record.output_sha256)

    def test_workspace_open_converges_before_measured_attempts(self) -> None:
        request = self._request()
        workload = perf_recovery.Workload(
            workload_id="workspace.open.no_change",
            command=("workspace", "open", "--path", "{workspace}", "--json"),
            warmups=0,
            runs=1,
            hard_budget_ms={"development": 5_000, "windows": 10_000},
            timeout_ms=60_000,
        )
        calls: list[list[str]] = []

        def run_command(_request, command, **_kwargs):
            calls.append(list(command))
            return self._result()

        with mock.patch.object(perf_recovery, "run_command", side_effect=run_command):
            records = perf_recovery.run_workload(request, workload)
        self.assertEqual(2, len(calls))
        self.assertEqual(1, len(records))

    def test_phase_evidence_is_same_pid_completed_and_bounded(self) -> None:
        logs = self.workspace / ".miller" / "logs"
        logs.mkdir()
        path = logs / "miller-test.jsonl"
        lines = [
            json.dumps({"Phase": "startup_total", "pid": 999, "Outcome": "completed"}),
            json.dumps({"Phase": "startup_total", "pid": 123, "Outcome": "failed"}),
        ]
        lines.extend(json.dumps({"Phase": "startup_total", "pid": 123, "Outcome": "completed"}) for _ in range(300))
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        records = perf_recovery._phase_records(self.workspace, "startup_total", pid=123)
        self.assertLessEqual(len(records), perf_recovery.MAX_PHASE_RECORDS)
        self.assertTrue(all(record["pid"] == 123 for record in records))
        self.assertEqual("completed", records[-1]["Outcome"])

    def test_mcp_startup_result_projects_child_phases_through_first_startup_total(self) -> None:
        logs = self.workspace / ".miller" / "logs"
        logs.mkdir()
        path = logs / "miller-test.jsonl"
        path.write_text(
            json.dumps({"Phase": "bind", "pid": 123, "ElapsedMilliseconds": 99}) + "\n",
            encoding="utf-8",
        )
        offsets = {path: path.stat().st_size}
        phase_names = [
            "bind",
            "import",
            "resolve",
            "coordinator_total",
            "content",
            "search",
            "metrics",
            "vector",
            "sidecar_total",
            "startup_total",
        ]
        lines = [
            {"Phase": "bind", "pid": 999, "ElapsedMilliseconds": 77},
            *[
                {
                    "Phase": phase,
                    "pid": 123,
                    "Outcome": "completed",
                    "ElapsedMilliseconds": index + 1,
                }
                for index, phase in enumerate(phase_names)
            ],
            {"Phase": "bind", "pid": 123, "ElapsedMilliseconds": 999},
        ]
        with path.open("a", encoding="utf-8") as handle:
            handle.write("\n".join(json.dumps(line) for line in lines) + "\n")

        session = object.__new__(perf_recovery._McpSession)
        session.request = self._request()
        session.process = SimpleNamespace(pid=123, returncode=0)
        session._log_offsets = offsets
        session._peaks = {}
        session._stderr = []
        phase = session.wait_for_phase("startup_total", time.monotonic() + 1)
        result = session.result(
            10.0,
            {"phase": phase, "completed": True, "ready_at": 10.1},
            False,
            close=False,
        )

        payload = json.loads(result.stdout)
        self.assertEqual(set(phase_names), set(payload["phases"]))
        self.assertEqual(1, payload["phases"]["bind"]["ElapsedMilliseconds"])
        self.assertEqual(10, payload["phases"]["startup_total"]["ElapsedMilliseconds"])
        record = perf_recovery._record_from_result(
            self._request(),
            self._mcp_workload("startup.leader.no_change"),
            result,
            attempt=1,
            warmup=False,
            timeout_ms=60_000,
            environment={},
            commit=None,
        )
        self.assertEqual(set(phase_names), set(record.phase_timings))

    def test_mcp_startup_result_tolerates_absent_optional_phases(self) -> None:
        logs = self.workspace / ".miller" / "logs"
        logs.mkdir()
        path = logs / "miller-test.jsonl"
        path.write_text(
            json.dumps({"Phase": "startup_total", "pid": 123, "Outcome": "completed"}) + "\n",
            encoding="utf-8",
        )

        session = object.__new__(perf_recovery._McpSession)
        session.request = self._request()
        session.process = SimpleNamespace(pid=123, returncode=0)
        session._log_offsets = {path: 0}
        session._peaks = {}
        session._stderr = []
        phase = session.wait_for_phase("startup_total", time.monotonic() + 1)
        result = session.result(
            10.0,
            {"phase": phase, "completed": True, "ready_at": 10.1},
            False,
            close=False,
        )

        self.assertEqual(["startup_total"], list(json.loads(result.stdout)["phases"]))

    def test_phase_poll_preserves_incomplete_json_until_newline(self) -> None:
        logs = self.workspace / ".miller" / "logs"
        logs.mkdir()
        path = logs / "miller-test.jsonl"
        partial = b'{"Phase":"startup_total","pid":123,"Outcome":"completed"'
        path.write_bytes(partial)
        offsets: dict[Path, int] = {}
        self.assertEqual([], perf_recovery._phase_records(self.workspace, "startup_total", offsets, pid=123))
        self.assertEqual(0, offsets[path])
        path.write_bytes(partial + b"}\n")
        records = perf_recovery._phase_records(self.workspace, "startup_total", offsets, pid=123)
        self.assertEqual("completed", records[-1]["Outcome"])
        self.assertEqual(path.stat().st_size, offsets[path])

    def test_phase_poll_bounds_oversized_partial_lines(self) -> None:
        logs = self.workspace / ".miller" / "logs"
        logs.mkdir()
        path = logs / "miller-test.jsonl"
        first = b'{"Phase":"startup_total","pid":123,"Outcome":"completed"}\n'
        partial = b'{"Phase":"startup_total","pid":123,"Outcome":"completed","payload":"' + (
            b"x" * (perf_recovery.MAX_PHASE_LINE_BYTES * 2)
        )
        path.write_bytes(first + partial)
        offsets: dict[Path, int] = {}
        records = perf_recovery._phase_records(self.workspace, "startup_total", offsets, pid=123)
        self.assertEqual(1, len(records))
        self.assertEqual(len(first), offsets[path])
        path.write_bytes(first + partial + b'"}\n')
        self.assertEqual([], perf_recovery._phase_records(self.workspace, "startup_total", offsets, pid=123))
        self.assertEqual(path.stat().st_size, offsets[path])

    def test_phase_wait_returns_failed_outcome_without_promoting_it(self) -> None:
        logs = self.workspace / ".miller" / "logs"
        logs.mkdir()
        (logs / "miller-test.jsonl").write_text(
            "\n".join(
                [
                    json.dumps({"Phase": "startup_total", "pid": 123, "Outcome": "completed"}),
                    json.dumps({"Phase": "startup_total", "pid": 123, "Outcome": "failed"}),
                ]
            )
            + "\n",
            encoding="utf-8",
        )
        session = object.__new__(perf_recovery._McpSession)
        session.request = self._request()
        session.process = SimpleNamespace(pid=123, poll=lambda: 1)
        session._log_offsets = {}
        phase = session.wait_for_phase("startup_total", time.monotonic() + 1)
        self.assertIsNotNone(phase)
        self.assertEqual("failed", phase["Outcome"])

    def test_phase_wait_returns_failed_outcome_while_process_is_alive(self) -> None:
        logs = self.workspace / ".miller" / "logs"
        logs.mkdir()
        (logs / "miller-test.jsonl").write_text(
            json.dumps({"Phase": "startup_total", "pid": 123, "Outcome": "failed"}) + "\n",
            encoding="utf-8",
        )
        session = object.__new__(perf_recovery._McpSession)
        session.request = self._request()
        session.process = SimpleNamespace(pid=123, poll=lambda: None)
        session._log_offsets = {}
        with mock.patch.object(perf_recovery.time, "sleep") as sleep:
            phase = session.wait_for_phase("startup_total", time.monotonic() + 0.05)
        self.assertIsNotNone(phase)
        self.assertEqual("failed", phase["Outcome"])
        sleep.assert_not_called()

    def test_phase_wait_returns_skipped_outcome_while_process_is_alive(self) -> None:
        logs = self.workspace / ".miller" / "logs"
        logs.mkdir()
        (logs / "miller-test.jsonl").write_text(
            json.dumps({"Phase": "startup_total", "pid": 123, "Outcome": "skipped"}) + "\n",
            encoding="utf-8",
        )
        session = object.__new__(perf_recovery._McpSession)
        session.request = self._request()
        session.process = SimpleNamespace(pid=123, poll=lambda: None)
        session._log_offsets = {}
        with mock.patch.object(perf_recovery.time, "sleep") as sleep:
            phase = session.wait_for_phase("startup_total", time.monotonic() + 0.05)
        self.assertIsNotNone(phase)
        self.assertEqual("skipped", phase["Outcome"])
        sleep.assert_not_called()

    def test_mcp_capture_is_bounded_by_bytes(self) -> None:
        captured: list[str] = []
        perf_recovery._append_bounded(captured, "x" * 20, max_bytes=8)
        self.assertEqual(8, len("".join(captured).encode("utf-8")))

    def test_mcp_completed_signal_does_not_hide_nonzero_exit(self) -> None:
        session = object.__new__(perf_recovery._McpSession)
        session.process = SimpleNamespace(returncode=9)
        session._peaks = {}
        session._stderr = []
        session.close = mock.Mock()
        result = session.result(
            10.0,
            {"phase": {"Outcome": "completed"}, "completed": True, "ready_at": 10.1},
            False,
        )
        self.assertEqual(9, result.exit_code)
        self.assertFalse(result.completed)

    def test_mcp_close_failure_cannot_synthesize_success(self) -> None:
        for returncode in (None, 0):
            with self.subTest(returncode=returncode):
                session = object.__new__(perf_recovery._McpSession)
                session.process = SimpleNamespace(returncode=returncode)
                session._peaks = {}
                session._stderr = []
                session.close = mock.Mock(return_value=False)
                result = session.result(
                    10.0,
                    {"phase": {"Outcome": "completed"}, "completed": True, "ready_at": 10.1},
                    False,
                )
                self.assertEqual(returncode, result.exit_code)
                self.assertFalse(result.completed)

    def test_mcp_pipe_setup_is_utf8_with_replacement(self) -> None:
        class FakeProcess:
            pid = 123
            returncode = 0
            stdin = io.StringIO()
            stdout = io.StringIO("")
            stderr = io.StringIO("")

            def poll(self):
                return self.returncode

            def wait(self, timeout=None):
                return self.returncode

        process = FakeProcess()
        with mock.patch.object(perf_recovery.subprocess, "Popen", return_value=process) as popen:
            with mock.patch.object(perf_recovery, "_monitor_process"):
                session = perf_recovery._McpSession(self._request(), {})
                session.close()
        kwargs = popen.call_args.kwargs
        self.assertEqual("utf-8", kwargs["encoding"])
        self.assertEqual("replace", kwargs["errors"])

    def test_mcp_queue_evicts_oldest_noise_and_keeps_newest_response(self) -> None:
        class Stream:
            def __init__(self):
                self.lines = ["noise\n", "response\n"]
                self.sizes: list[int] = []

            def readline(self, size):
                self.sizes.append(size)
                return self.lines.pop(0) if self.lines else ""

        stream = Stream()
        output = perf_recovery.queue.Queue(maxsize=1)
        output.put("old\n")
        captured: list[str] = []
        perf_recovery._McpSession._read_lines(stream, output, captured)
        self.assertEqual("response\n", output.get_nowait())
        self.assertTrue(all(size == perf_recovery.MAX_MCP_LINE_BYTES for size in stream.sizes))

    def test_run_process_capture_is_bounded(self) -> None:
        result = perf_recovery.run_command(
            self._request(),
            _python_command("import sys; sys.stdout.write('x' * 2200000)"),
            timeout_ms=5_000,
        )
        self.assertEqual(0, result.exit_code)
        self.assertLessEqual(len(result.stdout), perf_recovery.MAX_CAPTURE_BYTES)

    def test_run_process_terminates_inherited_pipe_child_after_parent_exit(self) -> None:
        if os.name == "nt":
            self.skipTest("POSIX process-group teardown")
        child_path = self.root / "child.pid"
        source = """
            import pathlib
            import subprocess
            import sys

            child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
            pathlib.Path(sys.argv[1]).write_text(str(child.pid), encoding="ascii")
        """
        child_pid: int | None = None
        try:
            result = perf_recovery._run_process(
                self._request(),
                _python_command(source) + [str(child_path)],
                5_000,
                dict(os.environ),
            )
            for _ in range(50):
                if child_path.exists():
                    break
                time.sleep(0.02)
            child_pid = int(child_path.read_text(encoding="ascii"))
            self.assertEqual(0, result.exit_code)
            deadline = time.monotonic() + 2
            while time.monotonic() < deadline:
                try:
                    os.kill(child_pid, 0)
                except ProcessLookupError:
                    break
                stat_path = Path(f"/proc/{child_pid}/stat")
                try:
                    if stat_path.read_text(encoding="ascii").split()[2] == "Z":
                        break
                except (FileNotFoundError, OSError, IndexError):
                    break
                time.sleep(0.02)
            else:
                self.fail("inherited-pipe child survived process-group teardown")
        finally:
            if child_pid is None and child_path.exists():
                try:
                    child_pid = int(child_path.read_text(encoding="ascii"))
                except (OSError, ValueError):
                    child_pid = None
            if child_pid is not None:
                try:
                    os.kill(child_pid, perf_recovery.signal.SIGKILL)
                except (ProcessLookupError, PermissionError):
                    pass

    def test_posix_termination_waits_after_sigkill(self) -> None:
        if os.name == "nt":
            self.skipTest("POSIX process-group termination")
        process = SimpleNamespace(
            pid=123,
            poll=lambda: None,
            wait=mock.Mock(side_effect=[subprocess.TimeoutExpired("test", 1), None]),
            kill=mock.Mock(),
        )
        with mock.patch.object(perf_recovery.os, "killpg") as killpg:
            perf_recovery._terminate_process(process)
        killpg.assert_any_call(123, perf_recovery.signal.SIGTERM)
        killpg.assert_any_call(123, perf_recovery.signal.SIGKILL)
        self.assertGreaterEqual(process.wait.call_count, 2)

    def test_posix_sigkill_race_still_reaps_process(self) -> None:
        if os.name == "nt":
            self.skipTest("POSIX process-group termination")
        process = SimpleNamespace(
            pid=123,
            poll=lambda: None,
            wait=mock.Mock(side_effect=[subprocess.TimeoutExpired("test", 1), None]),
            kill=mock.Mock(),
        )
        with mock.patch.object(
            perf_recovery.os,
            "killpg",
            side_effect=[None, ProcessLookupError],
        ):
            perf_recovery._terminate_process(process)
        self.assertEqual(2, process.wait.call_count)

    def test_windows_taskkill_failure_retries_tree_and_reaps(self) -> None:
        for first_failure in (
            subprocess.TimeoutExpired("taskkill", 1),
            OSError("taskkill unavailable"),
        ):
            with self.subTest(first_failure=type(first_failure).__name__):
                process = SimpleNamespace(
                    pid=123,
                    poll=lambda: None,
                    wait=mock.Mock(
                        side_effect=[subprocess.TimeoutExpired("test", 1), subprocess.TimeoutExpired("test", 1), None]
                    ),
                    kill=mock.Mock(),
                )
                with mock.patch.object(perf_recovery.os, "name", "nt"):
                    with mock.patch.object(
                        perf_recovery.subprocess,
                        "run",
                        side_effect=[first_failure, SimpleNamespace(returncode=1)],
                    ) as run:
                        perf_recovery._terminate_process(process)
                self.assertEqual(2, run.call_count)
                for call in run.call_args_list:
                    self.assertEqual(["taskkill", "/PID", "123", "/T", "/F"], call.args[0])
                    self.assertLessEqual(call.kwargs["timeout"], perf_recovery.PROCESS_TEARDOWN_TIMEOUT_SECONDS)
                process.kill.assert_called_once()
                self.assertEqual(3, process.wait.call_count)

    def test_windows_taskkill_has_a_bounded_timeout(self) -> None:
        process = SimpleNamespace(pid=123, poll=lambda: None, wait=mock.Mock(return_value=0), kill=mock.Mock())
        with mock.patch.object(perf_recovery.os, "name", "nt"):
            with mock.patch.object(perf_recovery.subprocess, "run") as run:
                perf_recovery._terminate_process(process)
        self.assertIn("timeout", run.call_args.kwargs)
        self.assertLessEqual(run.call_args.kwargs["timeout"], perf_recovery.PROCESS_TEARDOWN_TIMEOUT_SECONDS)

    def test_windows_normal_parent_exit_still_runs_tree_cleanup(self) -> None:
        process = SimpleNamespace(pid=123, poll=lambda: 0, wait=mock.Mock(return_value=0), kill=mock.Mock())
        with mock.patch.object(perf_recovery.os, "name", "nt"):
            with mock.patch.object(
                perf_recovery.subprocess,
                "run",
                return_value=SimpleNamespace(returncode=0),
            ) as run:
                perf_recovery._terminate_process(process)
        run.assert_called_once()
        self.assertEqual(["taskkill", "/PID", "123", "/T", "/F"], run.call_args.args[0])

    def test_windows_normal_parent_exit_retries_failed_tree_cleanup(self) -> None:
        process = SimpleNamespace(pid=123, poll=lambda: 0, wait=mock.Mock(return_value=0), kill=mock.Mock())
        with mock.patch.object(perf_recovery.os, "name", "nt"):
            with mock.patch.object(
                perf_recovery.subprocess,
                "run",
                side_effect=[subprocess.TimeoutExpired("taskkill", 1), SimpleNamespace(returncode=0)],
            ) as run:
                perf_recovery._terminate_process(process)
        self.assertEqual(2, run.call_count)
        self.assertTrue(all(call.kwargs["timeout"] <= perf_recovery.PROCESS_TEARDOWN_TIMEOUT_SECONDS for call in run.call_args_list))

    def test_windows_alive_parent_retries_failed_tree_cleanup_after_wait(self) -> None:
        process = SimpleNamespace(pid=123, poll=lambda: None, wait=mock.Mock(return_value=0), kill=mock.Mock())
        with mock.patch.object(perf_recovery.os, "name", "nt"):
            with mock.patch.object(
                perf_recovery.subprocess,
                "run",
                side_effect=[SimpleNamespace(returncode=1), SimpleNamespace(returncode=0)],
            ) as run:
                perf_recovery._terminate_process(process)
        self.assertEqual(2, run.call_count)
        self.assertTrue(all(call.args[0][-2:] == ["/T", "/F"] for call in run.call_args_list))

    def test_context_facts_capture_pivots_order_and_truncation(self) -> None:
        facts = perf_recovery._context_facts(
            {
                "bundle": [
                    {"symbol_id": "pivot-a", "role": "pivot"},
                    {"symbol_id": "neighbour-b", "role": "neighbour", "body_truncated": True},
                ],
                "disposition": {"status": "partial", "reason": "truncated"},
            },
            42,
        )
        self.assertEqual(["pivot-a"], facts["pivot_ids"])
        self.assertEqual(["pivot-a", "neighbour-b"], facts["order"])
        self.assertEqual(["neighbour-b"], facts["truncation"]["body_truncated"])

    def test_mcp_bootstrap_uses_one_absolute_deadline_and_status_readiness(self) -> None:
        calls: list[tuple[str, object]] = []

        class FakeSession:
            process = SimpleNamespace(poll=lambda: None)

            def __init__(self, _request, _environment):
                pass

            def request_json(self, method, _params, deadline):
                calls.append((method, deadline))
                return {"result": {}}

            def notify(self, method, _params, deadline):
                calls.append((method, deadline))

            def workspace_status(self, deadline):
                calls.append(("workspace_status", deadline))
                return {"isError": False, "result": {"ready": True}}

            def wait_for_phase(self, phase, deadline):
                calls.append((phase, deadline))
                return {"Phase": phase, "pid": 1, "Outcome": "completed"}

        request = self._request()
        with mock.patch.object(perf_recovery, "_McpSession", FakeSession):
            _, _, evidence, timed_out = perf_recovery._bootstrap_session(
                request,
                timeout_ms=1_000,
                environment={},
                require_phase=True,
            )
        self.assertFalse(timed_out)
        deadlines = [value for _, value in calls]
        self.assertGreaterEqual(len(deadlines), 4)
        self.assertEqual(1, len({id(value) for value in deadlines}))
        self.assertIn("workspace_status", evidence)

    def test_mcp_bootstrap_retries_running_status_under_one_deadline(self) -> None:
        statuses = [
            {"result": {"isError": False, "structuredContent": {"bootstrap": "running"}}},
            {"result": {"isError": False, "structuredContent": {"bootstrap": "ready"}}},
        ]
        deadlines: list[object] = []

        class FakeSession:
            process = SimpleNamespace(pid=1, poll=lambda: None)

            def __init__(self, _request, _environment):
                self.status_calls = 0

            def request_json(self, _method, _params, deadline):
                deadlines.append(deadline)
                return {"result": {}}

            def notify(self, _method, _params, deadline):
                deadlines.append(deadline)

            def workspace_status(self, deadline):
                deadlines.append(deadline)
                self.status_calls += 1
                return statuses.pop(0)

        request = self._request()
        with mock.patch.object(perf_recovery, "_McpSession", FakeSession):
            with mock.patch.object(perf_recovery.time, "sleep"):
                _, _, evidence, timed_out = perf_recovery._bootstrap_session(
                    request,
                    timeout_ms=1_000,
                    environment={},
                )
        self.assertFalse(timed_out)
        self.assertTrue(evidence["completed"])
        self.assertEqual(2, len(deadlines) - 2)
        self.assertEqual(1, len({id(value) for value in deadlines}))

    def test_mcp_bootstrap_parses_workspace_binding_call_tool_text(self) -> None:
        statuses = [
            {
                "jsonrpc": "2.0",
                "id": 3,
                "result": {
                    "content": [{"type": "text", "text": "BOOTSTRAP: RUNNING /tmp/workspace"}],
                    "isError": False,
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 4,
                "result": {
                    "content": [{"type": "text", "text": "bootstrap: idle"}],
                    "isError": False,
                },
            },
            {
                "jsonrpc": "2.0",
                "id": 5,
                "result": {
                    "content": [{"type": "text", "text": "workspace bound: /tmp/workspace"}],
                    "isError": False,
                },
            },
        ]
        deadlines: list[object] = []

        class FakeSession:
            process = SimpleNamespace(pid=1, poll=lambda: None)

            def __init__(self, _request, _environment):
                self.status_calls = 0

            def request_json(self, _method, _params, deadline):
                deadlines.append(deadline)
                return {"result": {}}

            def notify(self, _method, _params, deadline):
                deadlines.append(deadline)

            def workspace_status(self, deadline):
                deadlines.append(deadline)
                self.status_calls += 1
                return statuses.pop(0)

        for text, expected in (
            ("BOOTSTRAP: FAILED — unable to bind", "failed"),
            ("bootstrap: unavailable", "failed"),
        ):
            self.assertEqual(
                expected,
                perf_recovery._status_probe_state(
                    {
                        "jsonrpc": "2.0",
                        "id": 6,
                        "result": {
                            "content": [{"type": "text", "text": text}],
                            "isError": False,
                        },
                    }
                ),
            )

        request = self._request()
        with mock.patch.object(perf_recovery, "_McpSession", FakeSession):
            with mock.patch.object(perf_recovery.time, "sleep"):
                _, _, evidence, timed_out = perf_recovery._bootstrap_session(
                    request,
                    timeout_ms=1_000,
                    environment={},
                )
        self.assertFalse(timed_out)
        self.assertTrue(evidence["completed"])
        self.assertEqual(3, len(deadlines) - 2)
        self.assertEqual(1, len({id(value) for value in deadlines}))

    def test_mcp_bootstrap_stops_on_status_hard_failure(self) -> None:
        class FakeSession:
            process = SimpleNamespace(pid=1, poll=lambda: None)

            def __init__(self, _request, _environment):
                self.status_calls = 0

            def request_json(self, _method, _params, _deadline):
                return {"result": {}}

            def notify(self, _method, _params, _deadline):
                return None

            def workspace_status(self, _deadline):
                self.status_calls += 1
                return {"result": {"isError": True}}

        request = self._request()
        with mock.patch.object(perf_recovery, "_McpSession", FakeSession):
            _, _, evidence, timed_out = perf_recovery._bootstrap_session(
                request,
                timeout_ms=1_000,
                environment={},
            )
        self.assertFalse(timed_out)
        self.assertFalse(evidence["completed"])

    def test_mcp_bootstrap_stops_before_initialized_on_initialize_error(self) -> None:
        class FakeSession:
            process = SimpleNamespace(pid=1, poll=lambda: None)

            def __init__(self, _request, _environment):
                self.notify_calls = 0
                self.status_calls = 0

            def request_json(self, _method, _params, _deadline):
                return {"jsonrpc": "2.0", "id": 1, "error": {"code": -32000, "message": "no"}}

            def notify(self, _method, _params, _deadline):
                self.notify_calls += 1

            def workspace_status(self, _deadline):
                self.status_calls += 1
                return {"result": {"ready": True}}

        request = self._request()
        with mock.patch.object(perf_recovery, "_McpSession", FakeSession):
            session, _, evidence, timed_out = perf_recovery._bootstrap_session(
                request,
                timeout_ms=1_000,
                environment={},
            )
        self.assertFalse(timed_out)
        self.assertFalse(evidence["completed"])
        self.assertEqual(0, session.notify_calls)
        self.assertEqual(0, session.status_calls)

    def test_mcp_leader_attempts_are_real_and_only_final_session_is_retained(self) -> None:
        class FakeSession:
            def __init__(self, number):
                self.number = number
                self.closed = 0
                self.results = 0
                self.process = SimpleNamespace(poll=lambda: None)

            def result(self, *_args, **_kwargs):
                self.results += 1
                if _kwargs.get("close", True):
                    self.close()
                return self_result

            def close(self):
                self.closed += 1

        request = self._request()
        workload = perf_recovery.Workload(
            workload_id="startup.leader.no_change",
            command=("serve",),
            warmups=1,
            runs=3,
            hard_budget_ms={"development": 2_000, "windows": 5_000},
            timeout_ms=60_000,
            execution_kind="mcp_bootstrap",
        )
        sessions: list[FakeSession] = []
        self_result = self._result(stdout=b'{"phases":{"startup_total":{"Outcome":"completed"}}}')

        def bootstrap(*_args, **_kwargs):
            session = FakeSession(len(sessions) + 1)
            sessions.append(session)
            return session, 1.0, {"phase": {"Outcome": "completed"}, "ready_at": 1.1, "completed": True}, False

        with mock.patch.object(perf_recovery, "_bootstrap_session", side_effect=bootstrap):
            records, retained = perf_recovery._run_mcp_workload(request, workload, keep_alive=True)
        self.assertEqual(4, len(records))
        self.assertEqual(4, len(sessions))
        self.assertIs(retained, sessions[-1])
        self.assertEqual([1, 1, 1, 0], [session.closed for session in sessions])
        self.assertEqual([1, 1, 1, 1], [session.results for session in sessions])

    def test_mcp_leader_is_retained_only_after_a_successful_result(self) -> None:
        class FakeSession:
            def __init__(self):
                self.closed = 0
                self.results = 0
                self.process = SimpleNamespace(poll=lambda: None)

            def result(self, *_args, **_kwargs):
                self.results += 1
                if _kwargs.get("close", True):
                    self.close()
                return failed_result

            def close(self):
                self.closed += 1

        request = self._request()
        workload = self._mcp_workload("startup.leader.no_change")
        failed_result = dataclasses.replace(self._result(), completed=False)
        session = FakeSession()
        with mock.patch.object(
            perf_recovery,
            "_bootstrap_session",
            return_value=(session, 1.0, {"completed": True, "ready_at": 1.1}, False),
        ):
            records, retained = perf_recovery._run_mcp_workload(request, workload, keep_alive=True)
        self.assertIsNone(retained)
        self.assertEqual(2, session.results)
        self.assertEqual(1, session.closed)
        self.assertFalse(records[0].hard_gate_passed)

    def test_mcp_cleanup_closes_stdin_and_waits_before_fallback_termination(self) -> None:
        class Pipe:
            def __init__(self):
                self.closed = False

            def close(self):
                self.closed = True

        class Process:
            def __init__(self):
                self.stdin = Pipe()
                self.returncode = None
                self.wait_calls = 0

            def poll(self):
                return self.returncode

            def wait(self, timeout=None):
                self.wait_calls += 1
                self.returncode = 0

        process = Process()
        session = object.__new__(perf_recovery._McpSession)
        session.process = process
        session._closed = False
        session._stop = __import__("threading").Event()
        session._monitor = SimpleNamespace(join=lambda timeout=None: None)
        session._stdout_thread = SimpleNamespace(join=lambda timeout=None: None)
        session._stderr_thread = SimpleNamespace(join=lambda timeout=None: None)
        with mock.patch.object(perf_recovery, "_terminate_process") as terminate:
            session.close()
        self.assertTrue(process.stdin.closed)
        self.assertEqual(1, process.wait_calls)
        terminate.assert_called_once_with(process)

    def test_run_replay_rejects_reader_without_selected_leader(self) -> None:
        reader = self._mcp_workload("startup.reader.warm")
        with mock.patch.object(perf_recovery, "run_workload") as run_workload:
            with self.assertRaisesRegex(ValueError, "leader"):
                perf_recovery.run_replay(self._request(), {reader.workload_id: reader})
        run_workload.assert_not_called()

    def test_run_replay_rejects_reader_before_selected_leader(self) -> None:
        reader = self._mcp_workload("startup.reader.warm")
        leader = self._mcp_workload("startup.leader.no_change")
        workloads = {reader.workload_id: reader, leader.workload_id: leader}
        with mock.patch.object(perf_recovery, "run_workload") as run_workload:
            with mock.patch.object(perf_recovery, "_run_mcp_workload") as mcp_workload:
                with self.assertRaisesRegex(ValueError, "ordered"):
                    perf_recovery.run_replay(self._request(), workloads)
        run_workload.assert_not_called()
        mcp_workload.assert_not_called()

    def test_run_replay_rejects_reader_when_leader_did_not_stay_alive(self) -> None:
        reader = self._mcp_workload("startup.reader.warm")
        leader = self._mcp_workload("startup.leader.no_change")
        workloads = {leader.workload_id: leader, reader.workload_id: reader}
        with mock.patch.object(
            perf_recovery,
            "_run_mcp_workload",
            return_value=([self._record_for_pair(leader.workload_id)], None),
        ):
            with mock.patch.object(perf_recovery, "run_workload") as run_workload:
                with self.assertRaisesRegex(RuntimeError, "established leader"):
                    perf_recovery.run_replay(self._request(), workloads)
        run_workload.assert_not_called()

    def test_run_replay_checks_retained_leader_liveness_before_reader(self) -> None:
        reader = self._mcp_workload("startup.reader.warm")
        leader = self._mcp_workload("startup.leader.no_change")
        workloads = {leader.workload_id: leader, reader.workload_id: reader}
        retained = SimpleNamespace(
            process=SimpleNamespace(poll=lambda: 17),
            close=mock.Mock(),
        )
        calls: list[str] = []

        def run_mcp(*_args, **_kwargs):
            calls.append("mcp")
            if len(calls) == 1:
                return [self._record_for_pair(leader.workload_id)], retained
            raise AssertionError("reader dispatched after leader exited")

        with mock.patch.object(perf_recovery, "_run_mcp_workload", side_effect=run_mcp):
            with self.assertRaisesRegex(RuntimeError, "leader session.*alive"):
                perf_recovery.run_replay(self._request(), workloads)
        self.assertEqual(["mcp"], calls)
        retained.close.assert_called_once()

    def test_depth_pair_records_semantic_delta_without_cross_depth_byte_gate(self) -> None:
        facts = {"pivot_ids": ["pivot-a"], "order": ["pivot-a"], "truncation": False, "bytes": 100}
        depth0 = self._result(stdout=b"depth-0")
        depth1 = self._result(stdout=b"depth-1-longer")
        left = perf_recovery.ReplayRecord(
            workload_id="tool.context.references.depth0",
            platform="linux",
            commit="abc",
            producer_version=None,
            wall_ms=1,
            cpu_ms=1,
            peak_rss_bytes=None,
            peak_pss_bytes=None,
            output_sha256=depth0.output_sha256,
            exit_code=0,
            timed_out=False,
            hard_gate_passed=True,
            metadata={"context_facts": facts},
        )
        right = dataclasses.replace(
            left,
            workload_id="tool.context.references.depth1",
            output_sha256=depth1.output_sha256,
            metadata={"context_facts": {**facts, "bytes": 130}},
        )
        comparison = perf_recovery.compare_pair(left, right)
        self.assertFalse(comparison.output_digest_match)
        self.assertTrue(comparison.stable_pivot_match)
        self.assertTrue(comparison.ordering_match)
        self.assertTrue(comparison.truncation_match)
        self.assertEqual(30, comparison.added_bytes)
        updated = perf_recovery._attach_depth_pair([left, right])
        self.assertEqual(30, updated[1].metadata["depth_pair"]["added_bytes"])

    def test_depth_pair_allows_extra_identifier_rows_and_valid_truncation_change(self) -> None:
        depth0_facts = perf_recovery._context_facts(
            {
                "bundle": [
                    {"item_type": "symbol", "symbol_id": "pivot-a", "role": "pivot"},
                    {"item_type": "symbol", "symbol_id": "neighbour-b", "role": "neighbour"},
                ],
                "disposition": {"status": "sufficient", "reason": "complete"},
            },
            100,
        )
        depth1_facts = perf_recovery._context_facts(
            {
                "bundle": [
                    {"item_type": "symbol", "symbol_id": "pivot-a", "role": "pivot"},
                    {"item_type": "symbol", "symbol_id": "neighbour-b", "role": "neighbour"},
                    {"item_type": "identifier", "name": "Call", "file": "src/Call.cs", "line": 4},
                ],
                "disposition": {"status": "partial", "reason": "truncated"},
            },
            140,
        )
        depth0 = dataclasses.replace(self._record_for_pair("tool.context.references.depth0"), metadata={"context_facts": depth0_facts})
        depth1 = dataclasses.replace(self._record_for_pair("tool.context.references.depth1"), metadata={"context_facts": depth1_facts})
        comparison = perf_recovery.compare_pair(depth0, depth1)
        self.assertTrue(comparison.stable_pivot_match)
        self.assertTrue(comparison.symbol_neighbour_match)
        self.assertTrue(comparison.ordering_match)
        self.assertEqual(1, comparison.extra_reference_rows)
        self.assertTrue(comparison.truncation_changed)
        updated = perf_recovery._attach_depth_pair([depth0, depth1])
        self.assertTrue(updated[1].hard_gate_passed)

    def test_depth_pair_allows_new_neighbours_when_common_symbol_order_is_stable(self) -> None:
        left_facts = {
            "available": True,
            "symbol_pivot_ids": ["pivot-a"],
            "symbol_neighbour_ids": ["neighbour-a", "neighbour-b"],
            "symbol_order": ["pivot-a", "neighbour-a", "neighbour-b"],
            "truncation": None,
        }
        right_facts = {
            "available": True,
            "symbol_pivot_ids": ["pivot-a"],
            "symbol_neighbour_ids": ["neighbour-a", "new-neighbour", "neighbour-b"],
            "symbol_order": ["pivot-a", "neighbour-a", "new-neighbour", "neighbour-b"],
            "truncation": None,
        }
        depth0 = dataclasses.replace(
            self._record_for_pair("tool.context.references.depth0"),
            metadata={"context_facts": left_facts},
        )
        depth1 = dataclasses.replace(
            self._record_for_pair("tool.context.references.depth1"),
            metadata={"context_facts": right_facts},
        )
        comparison = perf_recovery.compare_pair(depth0, depth1)
        self.assertTrue(comparison.symbol_neighbour_match)
        self.assertTrue(comparison.ordering_match)
        self.assertTrue(perf_recovery._attach_depth_pair([depth0, depth1])[1].hard_gate_passed)

    def test_depth_pair_rejects_reordered_common_symbols(self) -> None:
        left_facts = {
            "available": True,
            "symbol_pivot_ids": ["pivot-a"],
            "symbol_neighbour_ids": ["neighbour-a", "neighbour-b"],
            "symbol_order": ["pivot-a", "neighbour-a", "neighbour-b"],
            "truncation": None,
        }
        right_facts = {
            "available": True,
            "symbol_pivot_ids": ["pivot-a"],
            "symbol_neighbour_ids": ["neighbour-b", "new-neighbour", "neighbour-a"],
            "symbol_order": ["pivot-a", "neighbour-b", "new-neighbour", "neighbour-a"],
            "truncation": None,
        }
        depth0 = dataclasses.replace(
            self._record_for_pair("tool.context.references.depth0"),
            metadata={"context_facts": left_facts},
        )
        depth1 = dataclasses.replace(
            self._record_for_pair("tool.context.references.depth1"),
            metadata={"context_facts": right_facts},
        )
        comparison = perf_recovery.compare_pair(depth0, depth1)
        self.assertFalse(comparison.ordering_match)
        self.assertFalse(perf_recovery._attach_depth_pair([depth0, depth1])[1].hard_gate_passed)

    def test_output_is_atomic_and_rejects_store_aliases(self) -> None:
        with self.assertRaisesRegex(ValueError, "output|alias"):
            perf_recovery.validate_request(self._request(out=self.store_copy))
        output = self.root / "records.jsonl"
        with mock.patch.object(perf_recovery.os, "replace", wraps=os.replace) as replace:
            perf_recovery.write_jsonl(output, [])
        self.assertTrue(replace.called)

    def test_windows_memory_configures_pointer_safe_handle_apis(self) -> None:
        class Function:
            def __init__(self, value=1):
                self.argtypes = None
                self.restype = None
                self.value = value

            def __call__(self, *_args):
                return self.value

        kernel32 = SimpleNamespace(OpenProcess=Function(), CloseHandle=Function())
        psapi = SimpleNamespace(GetProcessMemoryInfo=Function(value=False))
        with mock.patch.object(perf_recovery.ctypes, "windll", SimpleNamespace(kernel32=kernel32, psapi=psapi), create=True):
            perf_recovery._read_windows_memory(123)
        self.assertEqual([perf_recovery.ctypes.c_uint32, perf_recovery.ctypes.c_int, perf_recovery.ctypes.c_uint32], kernel32.OpenProcess.argtypes)
        self.assertEqual(perf_recovery.ctypes.c_void_p, kernel32.OpenProcess.restype)
        self.assertEqual([perf_recovery.ctypes.c_void_p], kernel32.CloseHandle.argtypes)

    def test_mutating_workloads_require_isolated_snapshot_metadata(self) -> None:
        item = {
            "id": "producer.resolve.full",
            "execution_kind": "julie_store",
            "command": [
                "store",
                "resolve",
                "--store",
                "{store_copy}",
                "--view",
                "{view}",
                "--request-id",
                "id",
                "--idempotency-key",
                "idempotency",
                "--request-timeout-seconds",
                "30",
                "--json",
            ],
            "warmups": 0,
            "runs": 1,
            "timeout_ms": 60_000,
            "hard_budget_ms": {"development": 1_000, "windows": 2_000},
            "mutates_store": True,
        }
        with self.assertRaisesRegex(ValueError, "isolated_snapshot"):
            perf_recovery._workload_from_mapping(item)
        item["isolated_snapshot"] = True
        self.assertTrue(perf_recovery._workload_from_mapping(item).isolated_snapshot)

    def test_manifest_rejects_missing_contract_and_short_timeout(self) -> None:
        value = {
            "schema_version": 1,
            "workloads": [
                {
                    "id": "bad",
                    "command": ["search", "--json"],
                    "warmups": 0,
                    "runs": 1,
                    "hard_budget_ms": {"development": 1, "windows": 1},
                    "timeout_ms": 10,
                }
            ],
        }
        path = self.root / "bad.json"
        path.write_text(json.dumps(value), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "workload id|timeout_ms"):
            perf_recovery.load_manifest(path)

    def test_manifest_rejects_invalid_or_unsupported_setup_timeout(self) -> None:
        item = self._resolve_item(resolution_scope="full")
        item["setup_timeout_ms"] = perf_recovery.FIRST_ATTEMPT_TIMEOUT_MS - 1
        with self.assertRaisesRegex(ValueError, "setup_timeout_ms"):
            perf_recovery._workload_from_mapping(item)

        item = {
            "id": "tool.setup-timeout",
            "command": ["version"],
            "warmups": 0,
            "runs": 1,
            "timeout_ms": 61_000,
            "setup_timeout_ms": 61_000,
            "hard_budget_ms": {"development": 1_000, "windows": 2_000},
        }
        with self.assertRaisesRegex(ValueError, "isolated mutating Julie resolution"):
            perf_recovery._workload_from_mapping(item)

    def test_manifest_rejects_unvalidated_runtime_placeholder(self) -> None:
        manifest_path = Path(__file__).resolve().parents[1] / "benchmarks" / "perf-recovery-workloads.json"
        value = json.loads(manifest_path.read_text(encoding="utf-8"))
        next(item for item in value["workloads"] if item["id"] == "tool.inspect.warm")["command"] = [
            "version",
            "{machine_specific_target}",
        ]
        path = self.root / "bad-placeholder.json"
        path.write_text(json.dumps(value), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "placeholder"):
            perf_recovery.load_manifest(path)

    def test_manifest_limits_idle_memory_budget_to_startup_workloads(self) -> None:
        workloads = self._verification_workloads()
        self.assertEqual(367_001_600, workloads["startup.leader.no_change"].hard_idle_memory_bytes)
        self.assertEqual(367_001_600, workloads["startup.reader.warm"].hard_idle_memory_bytes)
        self.assertTrue(all(
            workload.hard_idle_memory_bytes is None
            for workload_id, workload in workloads.items()
            if workload_id not in {"startup.leader.no_change", "startup.reader.warm"}
        ))
        item = dict(self._resolve_item())
        item["id"] = "tool.idle-memory"
        item["hard_idle_memory_bytes"] = 1_000
        with self.assertRaisesRegex(ValueError, "hard_idle_memory_bytes.*startup"):
            perf_recovery._workload_from_mapping(item)

    def test_linux_memory_sample_sums_process_tree_and_invalidates_missing_descendants(self) -> None:
        samples = {
            101: {"peak_rss_bytes": 10, "peak_pss_bytes": 7, "read_bytes": 2, "write_bytes": 3, "read_syscalls": 4, "write_syscalls": 5},
            102: {"peak_rss_bytes": 20, "peak_pss_bytes": 13, "read_bytes": 11, "write_bytes": 17, "read_syscalls": 19, "write_syscalls": 23},
        }
        with mock.patch.object(perf_recovery, "_linux_process_tree", return_value=(101, 102)), mock.patch.object(
            perf_recovery, "_read_linux_memory_one", side_effect=lambda pid: samples[pid]
        ):
            result = perf_recovery._read_linux_memory(101)
        self.assertEqual(30, result["peak_rss_bytes"])
        self.assertEqual(20, result["peak_pss_bytes"])
        self.assertEqual(2, result["process_count"])
        self.assertEqual(20, result["write_bytes"])

        with mock.patch.object(perf_recovery, "_linux_process_tree", return_value=(101, 102)), mock.patch.object(
            perf_recovery, "_read_linux_memory_one", side_effect=[samples[101], None]
        ):
            invalid = perf_recovery._read_linux_memory(101)
        self.assertIsNone(invalid["peak_pss_bytes"])
        self.assertIsNone(invalid["process_count"])

    def test_linux_process_tree_reads_children_from_every_thread(self) -> None:
        task_paths = [Path("/proc/100/task/100/children"), Path("/proc/100/task/222/children")]
        contents = {
            str(task_paths[0]): "101\n",
            str(task_paths[1]): "202\n",
        }

        def read_text(path, *args, **kwargs):
            return contents[str(path)]

        with mock.patch.object(perf_recovery.Path, "glob", return_value=task_paths), mock.patch.object(
            perf_recovery.Path, "read_text", autospec=True, side_effect=read_text
        ):
            self.assertEqual((101, 202), perf_recovery._linux_process_children(100))

    def test_linux_process_tree_invalidates_when_any_thread_child_read_fails(self) -> None:
        task_paths = [Path("/proc/100/task/100/children"), Path("/proc/100/task/222/children")]

        def read_text(path, *args, **kwargs):
            if str(path).endswith("/222/children"):
                raise OSError("transient read failure")
            return "101\n"

        with mock.patch.object(perf_recovery.Path, "glob", return_value=task_paths), mock.patch.object(
            perf_recovery.Path, "read_text", autospec=True, side_effect=read_text
        ):
            self.assertIsNone(perf_recovery._linux_process_children(100))

    def test_windows_tree_memory_uses_mocked_descendants_without_root_only_hard_metric(self) -> None:
        samples = {
            201: {"peak_rss_bytes": 10, "private_usage_bytes": 7},
            202: {"peak_rss_bytes": 20, "private_usage_bytes": 13},
        }
        with mock.patch.object(perf_recovery, "_windows_process_tree", return_value=(201, 202)), mock.patch.object(
            perf_recovery, "_read_windows_memory_one", side_effect=lambda pid: samples[pid]
        ):
            result = perf_recovery._read_windows_memory(201)
        self.assertEqual(30, result["peak_rss_bytes"])
        self.assertEqual(20, result["private_usage_bytes"])
        self.assertEqual(2, result["process_count"])

        with mock.patch.object(perf_recovery, "_windows_process_tree", return_value=None), mock.patch.object(
            perf_recovery, "_read_windows_memory_one", return_value=samples[201]
        ):
            invalid = perf_recovery._read_windows_memory(201)
        self.assertEqual(10, invalid["peak_rss_bytes"])
        self.assertIsNone(invalid["private_usage_bytes"])

    def test_windows_toolhelp_accepts_normal_end_of_snapshot(self) -> None:
        class Function:
            def __init__(self, callback):
                self.callback = callback
                self.argtypes = None
                self.restype = None

            def __call__(self, *args):
                return self.callback(*args)

        rows = iter([(100, 1), (200, 100)])
        last_error = [0]

        def write_row(_snapshot, pointer):
            pid, parent = next(rows)
            entry = perf_recovery.ctypes.cast(
                pointer, perf_recovery.ctypes.POINTER(perf_recovery._PROCESSENTRY32W)
            ).contents
            entry.th32ProcessID = pid
            entry.th32ParentProcessID = parent
            return True

        def next_row(snapshot, pointer):
            try:
                return write_row(snapshot, pointer)
            except StopIteration:
                last_error[0] = 18
                return False

        kernel32 = SimpleNamespace(
            CreateToolhelp32Snapshot=Function(lambda *_args: 1),
            Process32FirstW=Function(write_row),
            Process32NextW=Function(next_row),
            GetLastError=Function(lambda: last_error[0]),
            CloseHandle=Function(lambda *_args: 1),
        )
        with mock.patch.object(perf_recovery.ctypes, "windll", SimpleNamespace(kernel32=kernel32), create=True):
            self.assertEqual((100, 200), perf_recovery._windows_process_tree(100))

    def test_windows_toolhelp_mid_enumeration_error_is_not_treated_as_eof(self) -> None:
        class Function:
            def __init__(self, callback):
                self.callback = callback
                self.argtypes = None
                self.restype = None

            def __call__(self, *args):
                return self.callback(*args)

        last_error = [0]

        def first_row(_snapshot, pointer):
            entry = perf_recovery.ctypes.cast(
                pointer, perf_recovery.ctypes.POINTER(perf_recovery._PROCESSENTRY32W)
            ).contents
            entry.th32ProcessID = 100
            entry.th32ParentProcessID = 1
            return True

        def next_row(_snapshot, _pointer):
            last_error[0] = 5
            return False

        kernel32 = SimpleNamespace(
            CreateToolhelp32Snapshot=Function(lambda *_args: 1),
            Process32FirstW=Function(first_row),
            Process32NextW=Function(next_row),
            GetLastError=Function(lambda: last_error[0]),
            CloseHandle=Function(lambda *_args: 1),
        )
        with mock.patch.object(perf_recovery.ctypes, "windll", SimpleNamespace(kernel32=kernel32), create=True):
            self.assertIsNone(perf_recovery._windows_process_tree(100))

    def test_windows_memory_uses_query_only_access(self) -> None:
        class Function:
            def __init__(self, value=1):
                self.argtypes = None
                self.restype = None
                self.value = value
                self.calls = []

            def __call__(self, *args):
                self.calls.append(args)
                return self.value

        open_process = Function(value=1)
        kernel32 = SimpleNamespace(OpenProcess=open_process, CloseHandle=Function())
        psapi = SimpleNamespace(GetProcessMemoryInfo=Function(value=False))
        with mock.patch.object(perf_recovery.ctypes, "windll", SimpleNamespace(kernel32=kernel32, psapi=psapi), create=True):
            perf_recovery._read_windows_memory_one(123)
        requested_access = open_process.calls[0][0]
        self.assertIn(requested_access, {0x0400, 0x1000})
        self.assertEqual(0, requested_access & 0x0010)

    def test_idle_gate_requires_platform_metric_and_budget(self) -> None:
        workload = self._verification_workloads()["startup.leader.no_change"]
        common = {
            "workload": workload,
            "wall_ms": 1,
            "exit_code": 0,
            "timed_out": False,
            "hard_memory_bytes": 1,
            "hard_memory_metric": "linux_pss",
            "platform_name": "linux",
        }
        self.assertFalse(perf_recovery.hard_gate_passed(**common))
        self.assertTrue(
            perf_recovery.hard_gate_passed(
                **common,
                hard_idle_memory_bytes=367_001_599,
                hard_idle_memory_metric="linux_pss",
            )
        )
        self.assertFalse(
            perf_recovery.hard_gate_passed(
                **common,
                hard_idle_memory_bytes=367_001_599,
                hard_idle_memory_metric="windows_private_usage",
            )
        )
        self.assertFalse(
            perf_recovery.hard_gate_passed(
                **common,
                hard_idle_memory_bytes=367_001_601,
                hard_idle_memory_metric="linux_pss",
            )
        )

    def test_idle_sampling_does_not_change_readiness_wall_time(self) -> None:
        evidence = {"completed": True, "ready_at": 10.042, "observed_at": 13.042}
        result = perf_recovery._command_result_from_idle_evidence(
            wall_ms=42,
            evidence=evidence,
            idle={
                "hard_idle_memory_bytes": 11,
                "hard_idle_memory_metric": "linux_pss",
                "idle_pss_bytes": 11,
                "idle_sample_ms": 3_000,
                "idle_process_count": 2,
            },
        )
        self.assertEqual(42, result.wall_ms)
        self.assertEqual(3_000, result.idle_sample_ms)

    def test_idle_sampling_policy_excludes_warmups_and_non_idle_workloads(self) -> None:
        startup = self._verification_workloads()["startup.leader.no_change"]
        ordinary = self._verification_workloads()["tool.inspect.warm"]
        self.assertFalse(perf_recovery._should_sample_idle_memory(startup, warmup=True))
        self.assertTrue(perf_recovery._should_sample_idle_memory(startup, warmup=False))
        self.assertFalse(perf_recovery._should_sample_idle_memory(ordinary, warmup=False))

    def test_mcp_bootstrap_samples_idle_only_for_measured_ready_attempts(self) -> None:
        workload = dataclasses.replace(
            self._mcp_workload("startup.leader.no_change"),
            hard_idle_memory_bytes=100,
        )
        session = mock.Mock()
        session.process.poll.return_value = None
        session.sample_idle_window.return_value = {
            "hard_idle_memory_bytes": 10,
            "hard_idle_memory_metric": "linux_pss",
        }
        session.result.return_value = self._result()
        evidence = {"completed": True, "ready_at": 1.0}
        with mock.patch.object(perf_recovery, "_bootstrap_session", return_value=(session, 0.0, evidence, False)):
            perf_recovery._run_mcp_bootstrap(
                self._request(), workload, timeout_ms=60_000, environment={}, warmup=True
            )
            session.sample_idle_window.assert_not_called()
            perf_recovery._run_mcp_bootstrap(
                self._request(), workload, timeout_ms=60_000, environment={}, warmup=False
            )
        session.sample_idle_window.assert_called_once_with()

    def test_idle_window_clean_exit_invalidates_bootstrap_evidence(self) -> None:
        workload = dataclasses.replace(
            self._mcp_workload("startup.leader.no_change"),
            hard_budget_memory_bytes=100,
            hard_idle_memory_bytes=100,
        )
        session = mock.Mock()
        session.process.poll.side_effect = [None, 0]
        session.sample_idle_window.return_value = {
            "idle_pss_bytes": 10,
            "hard_idle_memory_bytes": 10,
            "hard_idle_memory_metric": "linux_pss",
            "idle_sample_ms": 3_000,
            "idle_process_count": 1,
        }

        def result(*_args, **kwargs):
            base = dataclasses.replace(
                self._result(),
                peak_pss_bytes=1,
                hard_memory_bytes=1,
                hard_memory_metric="linux_pss",
            )
            idle = kwargs.get("idle")
            if idle is None:
                return base
            return dataclasses.replace(
                base,
                idle_pss_bytes=idle["idle_pss_bytes"],
                hard_idle_memory_bytes=idle["hard_idle_memory_bytes"],
                hard_idle_memory_metric=idle["hard_idle_memory_metric"],
                idle_sample_ms=idle["idle_sample_ms"],
                idle_process_count=idle["idle_process_count"],
            )

        session.result.side_effect = result
        evidence = {"completed": True, "ready_at": 1.0}
        with mock.patch.object(
            perf_recovery,
            "_bootstrap_session",
            return_value=(session, 0.0, evidence, False),
        ):
            command_result = perf_recovery._run_mcp_bootstrap(
                self._request(), workload, timeout_ms=60_000, environment={}
            )
        record = perf_recovery._record_from_result(
            self._request(),
            workload,
            command_result,
            attempt=1,
            warmup=False,
            timeout_ms=60_000,
            environment={},
            commit=None,
        )
        self.assertIsNone(command_result.hard_idle_memory_bytes)
        self.assertFalse(record.hard_gate_passed)

    def test_retained_leader_idle_resample_merges_before_close(self) -> None:
        leader = self._record_for_pair("startup.leader.no_change")
        leader = dataclasses.replace(
            leader,
            idle_pss_bytes=10,
            hard_idle_memory_bytes=10,
            hard_idle_memory_metric="linux_pss",
            idle_sample_ms=3_000,
            idle_process_count=2,
        )
        session = mock.Mock()
        session.process.poll.return_value = None
        session.sample_idle_window.return_value = {
            "idle_pss_bytes": 20,
            "hard_idle_memory_bytes": 20,
            "hard_idle_memory_metric": "linux_pss",
            "idle_sample_ms": 3_000,
            "idle_process_count": 3,
        }
        merged = perf_recovery._merge_idle_evidence(leader, session.sample_idle_window())
        session.close.assert_not_called()
        self.assertEqual(20, merged.hard_idle_memory_bytes)
        self.assertEqual(3, merged.idle_process_count)

    def test_run_replay_resamples_retained_leader_after_reader_before_close(self) -> None:
        leader = dataclasses.replace(
            self._mcp_workload("startup.leader.no_change"),
            hard_idle_memory_bytes=100,
        )
        reader = dataclasses.replace(
            self._mcp_workload("startup.reader.warm"),
            hard_idle_memory_bytes=100,
        )
        leader_record = self._record_for_pair(leader.workload_id)
        reader_record = self._record_for_pair(reader.workload_id)
        events: list[str] = []
        session = mock.Mock()
        session.process.poll.return_value = None
        session.sample_idle_window.side_effect = lambda: events.append("idle") or {
            "hard_idle_memory_bytes": 20,
            "hard_idle_memory_metric": "linux_pss",
            "idle_pss_bytes": 20,
            "idle_sample_ms": 3_000,
            "idle_process_count": 2,
        }
        session.close.side_effect = lambda: events.append("close")

        def run_mcp(_request, workload, *, keep_alive):
            if workload.workload_id == leader.workload_id:
                return [leader_record], session
            events.append("reader")
            return [reader_record], None

        with mock.patch.object(perf_recovery, "_run_mcp_workload", side_effect=run_mcp):
            records = perf_recovery.run_replay(self._request(), {leader.workload_id: leader, reader.workload_id: reader})
        self.assertEqual(["reader", "idle", "close"], events)
        self.assertEqual(20, records[0].hard_idle_memory_bytes)

    def _run_retained_replay_with_post_reader_evidence(
        self, *, idle, metrics=None, poll=None, close=True, returncode=None
    ):
        leader = dataclasses.replace(
            self._mcp_workload("startup.leader.no_change"),
            hard_budget_memory_bytes=100,
            hard_idle_memory_bytes=100,
        )
        reader = dataclasses.replace(
            self._mcp_workload("startup.reader.warm"),
            hard_budget_memory_bytes=100,
            hard_idle_memory_bytes=100,
        )
        leader_record = dataclasses.replace(
            self._record_for_pair(leader.workload_id),
            peak_pss_bytes=10,
            hard_memory_bytes=10,
            hard_memory_metric="linux_pss",
            idle_pss_bytes=50,
            hard_idle_memory_bytes=50,
            hard_idle_memory_metric="linux_pss",
            idle_sample_ms=3_000,
            idle_process_count=1,
        )
        session = mock.Mock()
        session.process.poll.side_effect = poll or [None, None, None]
        session.process.returncode = returncode
        session.sample_idle_window.return_value = idle
        session.memory_metrics.return_value = metrics or {
            "peak_rss_bytes": 20,
            "peak_pss_bytes": 20,
            "private_usage_bytes": None,
            "hard_memory_bytes": 20,
            "hard_memory_metric": "linux_pss",
            "process_count": 2,
        }
        session.close.return_value = close

        def run_mcp(_request, workload, *, keep_alive):
            if workload.workload_id == leader.workload_id:
                return [leader_record], session
            return [self._record_for_pair(reader.workload_id)], None

        with mock.patch.object(perf_recovery, "_run_mcp_workload", side_effect=run_mcp):
            records = perf_recovery.run_replay(
                self._request(),
                {leader.workload_id: leader, reader.workload_id: reader},
            )
        return records[0]

    def test_retained_leader_missing_second_idle_sample_fails_closed(self) -> None:
        record = self._run_retained_replay_with_post_reader_evidence(
            idle={
                "idle_pss_bytes": None,
                "hard_idle_memory_bytes": None,
                "hard_idle_memory_metric": None,
                "idle_sample_ms": 2_999,
                "idle_process_count": None,
            }
        )
        self.assertIsNone(record.hard_idle_memory_bytes)
        self.assertFalse(record.hard_gate_passed)

    def test_retained_leader_merges_peak_growth_and_recomputes_gate(self) -> None:
        record = self._run_retained_replay_with_post_reader_evidence(
            idle={
                "idle_pss_bytes": 60,
                "hard_idle_memory_bytes": 60,
                "hard_idle_memory_metric": "linux_pss",
                "idle_sample_ms": 3_000,
                "idle_process_count": 2,
            }
        )
        self.assertEqual(20, record.peak_pss_bytes)
        self.assertEqual(20, record.hard_memory_bytes)
        self.assertTrue(record.hard_gate_passed)

    def test_retained_leader_over_budget_second_idle_sample_fails_gate(self) -> None:
        record = self._run_retained_replay_with_post_reader_evidence(
            idle={
                "idle_pss_bytes": 101,
                "hard_idle_memory_bytes": 101,
                "hard_idle_memory_metric": "linux_pss",
                "idle_sample_ms": 3_000,
                "idle_process_count": 2,
            }
        )
        self.assertEqual(101, record.hard_idle_memory_bytes)
        self.assertFalse(record.hard_gate_passed)

    def test_retained_leader_exit_after_reader_fails_gate(self) -> None:
        record = self._run_retained_replay_with_post_reader_evidence(
            idle={
                "idle_pss_bytes": 60,
                "hard_idle_memory_bytes": 60,
                "hard_idle_memory_metric": "linux_pss",
                "idle_sample_ms": 3_000,
                "idle_process_count": 2,
            },
            poll=[None, 17],
        )
        self.assertFalse(record.hard_gate_passed)

    def test_retained_leader_nonzero_exit_code_is_preserved(self) -> None:
        record = self._run_retained_replay_with_post_reader_evidence(
            idle={
                "idle_pss_bytes": 60,
                "hard_idle_memory_bytes": 60,
                "hard_idle_memory_metric": "linux_pss",
                "idle_sample_ms": 3_000,
                "idle_process_count": 2,
            },
            poll=[None, -9],
            returncode=-9,
        )
        self.assertEqual(-9, record.exit_code)
        self.assertFalse(record.hard_gate_passed)

    def test_retained_leader_exit_during_second_idle_sample_fails_gate(self) -> None:
        record = self._run_retained_replay_with_post_reader_evidence(
            idle={
                "idle_pss_bytes": 60,
                "hard_idle_memory_bytes": 60,
                "hard_idle_memory_metric": "linux_pss",
                "idle_sample_ms": 3_000,
                "idle_process_count": 2,
            },
            poll=[None, None, 17],
        )
        self.assertFalse(record.hard_gate_passed)

    def test_retained_leader_close_failure_fails_gate(self) -> None:
        record = self._run_retained_replay_with_post_reader_evidence(
            idle={
                "idle_pss_bytes": 60,
                "hard_idle_memory_bytes": 60,
                "hard_idle_memory_metric": "linux_pss",
                "idle_sample_ms": 3_000,
                "idle_process_count": 2,
            },
            close=False,
        )
        self.assertFalse(record.hard_gate_passed)

    def test_hard_gate_uses_platform_budget_and_nullable_metrics(self) -> None:
        workload = perf_recovery.Workload(
            workload_id="test",
            command=("version",),
            warmups=0,
            runs=1,
            hard_budget_ms={"development": 100, "windows": 200},
            hard_budget_memory_bytes=1_000,
        )
        self.assertTrue(
            perf_recovery.hard_gate_passed(
                workload,
                wall_ms=99,
                exit_code=0,
                timed_out=False,
                hard_memory_bytes=None,
                platform_name="darwin",
            )
        )
        self.assertFalse(
            perf_recovery.hard_gate_passed(
                workload,
                wall_ms=99,
                exit_code=0,
                timed_out=False,
                hard_memory_bytes=None,
                platform_name="win32",
            )
        )
        self.assertFalse(
            perf_recovery.hard_gate_passed(
                workload,
                wall_ms=101,
                exit_code=0,
                timed_out=False,
                hard_memory_bytes=500,
                platform_name="linux",
            )
        )
        metrics = perf_recovery.normalise_memory_metrics("darwin", {"rss": 12})
        self.assertIsNone(metrics["peak_pss_bytes"])
        self.assertIsNone(metrics["private_usage_bytes"])
        windows = perf_recovery.normalise_memory_metrics("win32", {"private_usage_bytes": 12})
        self.assertEqual(12, windows["private_usage_bytes"])
        self.assertIsNone(windows["peak_pss_bytes"])

    def test_peak_memory_gate_fails_closed_for_missing_or_mismatched_platform_metric(self) -> None:
        workload = perf_recovery.Workload(
            workload_id="test",
            command=("version",),
            warmups=0,
            runs=1,
            hard_budget_ms={"development": 100, "windows": 200},
            hard_budget_memory_bytes=1_000,
        )
        common = {
            "workload": workload,
            "wall_ms": 1,
            "exit_code": 0,
            "timed_out": False,
            "hard_memory_bytes": 500,
        }
        self.assertFalse(perf_recovery.hard_gate_passed(**common, hard_memory_metric=None, platform_name="linux"))
        self.assertFalse(
            perf_recovery.hard_gate_passed(**common, hard_memory_metric="windows_private_usage", platform_name="linux")
        )
        self.assertFalse(
            perf_recovery.hard_gate_passed(
                **{**common, "hard_memory_bytes": None},
                hard_memory_metric=None,
                platform_name="windows",
            )
        )
        self.assertFalse(
            perf_recovery.hard_gate_passed(
                **common,
                hard_memory_metric="linux_pss",
                platform_name="windows",
            )
        )
        self.assertTrue(
            perf_recovery.hard_gate_passed(**common, hard_memory_metric="linux_pss", platform_name="linux")
        )
        self.assertTrue(
            perf_recovery.hard_gate_passed(**common, hard_memory_metric="windows_private_usage", platform_name="windows")
        )

    def _verification_workloads(self) -> dict[str, perf_recovery.Workload]:
        manifest_path = Path(__file__).resolve().parents[1] / "benchmarks" / "perf-recovery-workloads.json"
        return perf_recovery.load_manifest(manifest_path)

    def _verification_records(
        self,
        *,
        platform: str = "linux",
        workloads: dict[str, perf_recovery.Workload] | None = None,
    ) -> list[perf_recovery.ReplayRecord]:
        workloads = workloads or self._verification_workloads()
        budget_key = "windows" if platform.startswith("win") else "development"
        records: list[perf_recovery.ReplayRecord] = []
        for workload in workloads.values():
            timing_budget = workload.hard_budget_ms[budget_key]
            memory_budget = workload.hard_budget_memory_bytes
            hard_memory = memory_budget if memory_budget is not None else None
            idle_memory_budget = workload.hard_idle_memory_bytes
            hard_idle_memory = idle_memory_budget if idle_memory_budget is not None else None
            metadata: dict[str, object] = {}
            parity: dict[str, object] | None = None
            if workload.parity_with is not None:
                parity = {
                    "paired_workload_id": workload.parity_with,
                    "output_digest_match": True,
                    "exit_code_match": True,
                    "timeout_match": True,
                }
            if workload.workload_id == "tool.context.references.depth1":
                metadata["depth_pair"] = {
                    "stable_pivot_match": True,
                    "symbol_neighbour_match": True,
                    "ordering_match": True,
                    "truncation_match": True,
                    "truncation_changed": False,
                    "truncation_shape_valid": True,
                }
            if workload.metadata.get("resolution_scope") in {"one_file", "full"}:
                metadata["resolution_gate"] = {"passed": True}
                metadata["resolution_report"] = {"state": "exact", "exact_at_matches": True}
            for attempt in range(1, 4):
                records.append(
                    perf_recovery.ReplayRecord(
                        workload_id=workload.workload_id,
                        platform=platform,
                        commit="abc",
                        producer_version=None,
                        wall_ms=timing_budget,
                        cpu_ms=1,
                        peak_rss_bytes=None,
                        peak_pss_bytes=hard_memory if platform.startswith("linux") else None,
                        output_sha256=f"{workload.workload_id}-{attempt}",
                        exit_code=0,
                        timed_out=False,
                        hard_gate_passed=True,
                        attempt=attempt,
                        warmup=False,
                        hard_memory_bytes=hard_memory,
                        hard_memory_metric=(
                            "linux_pss"
                            if platform.startswith("linux") and hard_memory is not None
                            else "windows_private_usage"
                            if platform.startswith("win") and hard_memory is not None
                            else None
                        ),
                        private_usage_bytes=hard_memory if platform.startswith("win") else None,
                        idle_pss_bytes=hard_idle_memory if platform.startswith("linux") else None,
                        idle_private_usage_bytes=hard_idle_memory if platform.startswith("win") else None,
                        hard_idle_memory_bytes=hard_idle_memory,
                        hard_idle_memory_metric=(
                            "linux_pss"
                            if platform.startswith("linux") and hard_idle_memory is not None
                            else "windows_private_usage"
                            if platform.startswith("win") and hard_idle_memory is not None
                            else None
                        ),
                        idle_sample_ms=3_000 if hard_idle_memory is not None else None,
                        idle_process_count=1 if hard_idle_memory is not None else None,
                        metadata=metadata,
                        parity=parity,
                    )
                )
        return records

    def test_verification_report_rejects_missing_hard_gate(self) -> None:
        workloads = self._verification_workloads()
        records = [
            record
            for record in self._verification_records(workloads=workloads)
            if record.workload_id != "tool.context.references.depth1"
        ]
        with self.assertRaisesRegex(ValueError, "missing hard gate: tool.context.references.depth1"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_fewer_than_three_measured_attempts(self) -> None:
        workloads = self._verification_workloads()
        records = self._verification_records(workloads=workloads)
        records = [
            record
            for record in records
            if record.workload_id != "tool.inspect.warm" or record.attempt != 3
        ]
        with self.assertRaisesRegex(ValueError, "requires at least 3 measured attempts: tool.inspect.warm"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_missing_platform_identity(self) -> None:
        workloads = self._verification_workloads()
        records = self._verification_records(workloads=workloads)
        records[0] = dataclasses.replace(records[0], platform="")
        with self.assertRaisesRegex(ValueError, "missing platform identity"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_failed_parity_invariant(self) -> None:
        workloads = self._verification_workloads()
        records = self._verification_records(workloads=workloads)
        index = next(
            index
            for index, record in enumerate(records)
            if record.workload_id == "tool.context.references.depth1.batch_on" and not record.warmup
        )
        records[index] = dataclasses.replace(
            records[index],
            parity={
                "paired_workload_id": "tool.context.references.depth1.batch_off",
                "output_digest_match": False,
                "exit_code_match": True,
                "timeout_match": True,
            },
        )
        with self.assertRaisesRegex(ValueError, "parity invariant failed: tool.context.references.depth1.batch_on"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_failed_depth_pair_invariant(self) -> None:
        workloads = self._verification_workloads()
        records = self._verification_records(workloads=workloads)
        index = next(
            index
            for index, record in enumerate(records)
            if record.workload_id == "tool.context.references.depth1" and not record.warmup
        )
        metadata = dict(records[index].metadata)
        metadata["depth_pair"] = {
            "stable_pivot_match": True,
            "symbol_neighbour_match": True,
            "ordering_match": False,
            "truncation_match": True,
            "truncation_changed": False,
            "truncation_shape_valid": True,
        }
        records[index] = dataclasses.replace(records[index], metadata=metadata)
        with self.assertRaisesRegex(ValueError, "semantic invariant failed: tool.context.references.depth1"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_allows_valid_truncation_change(self) -> None:
        records = self._verification_records()
        index = next(
            index
            for index, record in enumerate(records)
            if record.workload_id == "tool.context.references.depth1" and not record.warmup
        )
        metadata = dict(records[index].metadata)
        metadata["depth_pair"] = {
            **metadata["depth_pair"],
            "truncation_match": False,
            "truncation_changed": True,
            "truncation_shape_valid": True,
        }
        records[index] = dataclasses.replace(records[index], metadata=metadata)
        report = perf_recovery.build_verification_report(records, platform="linux")
        self.assertTrue(report["passed"])

    def test_verification_report_rejects_missing_truncation_evidence(self) -> None:
        records = self._verification_records()
        index = next(
            index
            for index, record in enumerate(records)
            if record.workload_id == "tool.context.references.depth1" and not record.warmup
        )
        metadata = dict(records[index].metadata)
        depth_pair = dict(metadata["depth_pair"])
        depth_pair.pop("truncation_match")
        metadata["depth_pair"] = depth_pair
        records[index] = dataclasses.replace(records[index], metadata=metadata)
        with self.assertRaisesRegex(ValueError, "missing semantic invariant evidence: tool.context.references.depth1"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_invalid_truncation_relation(self) -> None:
        records = self._verification_records()
        index = next(
            index
            for index, record in enumerate(records)
            if record.workload_id == "tool.context.references.depth1" and not record.warmup
        )
        metadata = dict(records[index].metadata)
        metadata["depth_pair"] = {
            **metadata["depth_pair"],
            "truncation_match": False,
            "truncation_changed": False,
        }
        records[index] = dataclasses.replace(records[index], metadata=metadata)
        with self.assertRaisesRegex(ValueError, "invalid truncation relation: tool.context.references.depth1"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_missing_parity_evidence(self) -> None:
        records = self._verification_records()
        index = next(
            index
            for index, record in enumerate(records)
            if record.workload_id == "tool.context.references.depth1.batch_on" and not record.warmup
        )
        records[index] = dataclasses.replace(records[index], parity=None)
        with self.assertRaisesRegex(ValueError, "missing parity evidence: tool.context.references.depth1.batch_on"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_missing_depth_pair_evidence(self) -> None:
        records = self._verification_records()
        index = next(
            index
            for index, record in enumerate(records)
            if record.workload_id == "tool.context.references.depth1" and not record.warmup
        )
        metadata = dict(records[index].metadata)
        metadata.pop("depth_pair")
        records[index] = dataclasses.replace(records[index], metadata=metadata)
        with self.assertRaisesRegex(ValueError, "missing semantic invariant evidence: tool.context.references.depth1"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_missing_resolution_exact_evidence(self) -> None:
        records = self._verification_records()
        index = next(
            index
            for index, record in enumerate(records)
            if record.workload_id == "producer.resolve.full" and not record.warmup
        )
        metadata = dict(records[index].metadata)
        metadata["resolution_report"] = {"state": "exact"}
        records[index] = dataclasses.replace(records[index], metadata=metadata)
        with self.assertRaisesRegex(ValueError, "missing resolution exact evidence: producer.resolve.full"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_failed_resolution_exact_parity(self) -> None:
        records = self._verification_records()
        index = next(
            index
            for index, record in enumerate(records)
            if record.workload_id == "producer.resolve.one_file" and not record.warmup
        )
        metadata = dict(records[index].metadata)
        metadata["resolution_report"] = {"state": "converging", "exact_at_matches": False}
        records[index] = dataclasses.replace(records[index], metadata=metadata)
        with self.assertRaisesRegex(ValueError, "resolution exact parity failed: producer.resolve.one_file"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_over_budget_median(self) -> None:
        workloads = self._verification_workloads()
        records = self._verification_records(workloads=workloads)
        workload = workloads["tool.inspect.warm"]
        budget = workload.hard_budget_ms["development"]
        records = [
            dataclasses.replace(record, wall_ms=budget + 1)
            if record.workload_id == workload.workload_id
            else record
            for record in records
        ]
        with self.assertRaisesRegex(ValueError, "budget exceeded: tool.inspect.warm"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_windows_missing_private_usage(self) -> None:
        workloads = self._verification_workloads()
        records = self._verification_records(platform="windows", workloads=workloads)
        index = next(index for index, record in enumerate(records) if not record.warmup)
        records[index] = dataclasses.replace(
            records[index],
            private_usage_bytes=None,
            hard_memory_bytes=None,
            hard_memory_metric=None,
        )
        with self.assertRaisesRegex(ValueError, "missing Windows PrivateUsage"):
            perf_recovery.build_verification_report(records, platform="windows")

    def test_verification_report_rejects_memory_overage(self) -> None:
        workloads = self._verification_workloads()
        records = self._verification_records(workloads=workloads)
        workload = workloads["startup.leader.no_change"]
        memory_budget = workload.hard_budget_memory_bytes
        assert memory_budget is not None
        index = next(index for index, record in enumerate(records) if not record.warmup)
        records[index] = dataclasses.replace(
            records[index],
            peak_pss_bytes=memory_budget + 1,
            hard_memory_bytes=memory_budget + 1,
        )
        with self.assertRaisesRegex(ValueError, "memory budget exceeded: startup.leader.no_change"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rechecks_exit_code_independently_of_hard_gate(self) -> None:
        records = self._verification_records()
        index = next(index for index, record in enumerate(records) if not record.warmup)
        records[index] = dataclasses.replace(records[index], exit_code=9, hard_gate_passed=True)
        with self.assertRaisesRegex(ValueError, "exit code: startup.leader.no_change"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rechecks_timeout_independently_of_hard_gate(self) -> None:
        records = self._verification_records()
        index = next(index for index, record in enumerate(records) if not record.warmup)
        records[index] = dataclasses.replace(records[index], timed_out=True, hard_gate_passed=True)
        with self.assertRaisesRegex(ValueError, "timed out: startup.leader.no_change"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_missing_idle_metric(self) -> None:
        records = self._verification_records()
        index = next(index for index, record in enumerate(records) if record.workload_id == "startup.leader.no_change" and not record.warmup)
        records[index] = dataclasses.replace(
            records[index],
            idle_pss_bytes=None,
            hard_idle_memory_bytes=None,
            hard_idle_memory_metric=None,
        )
        with self.assertRaisesRegex(ValueError, "missing idle memory metric: startup.leader.no_change"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_requires_the_complete_idle_window(self) -> None:
        records = self._verification_records()
        index = next(index for index, record in enumerate(records) if record.workload_id == "startup.leader.no_change" and not record.warmup)
        records[index] = dataclasses.replace(records[index], idle_sample_ms=2_999)
        with self.assertRaisesRegex(ValueError, "missing idle memory sample: startup.leader.no_change"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_fractional_idle_process_count(self) -> None:
        records = self._verification_records()
        index = next(
            index
            for index, record in enumerate(records)
            if record.workload_id == "startup.leader.no_change" and not record.warmup
        )
        records[index] = dataclasses.replace(records[index], idle_process_count=1.5)
        with self.assertRaisesRegex(ValueError, "missing idle memory sample: startup.leader.no_change"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_rejects_idle_metric_mismatch_and_overage(self) -> None:
        records = self._verification_records()
        index = next(index for index, record in enumerate(records) if record.workload_id == "startup.reader.warm" and not record.warmup)
        records[index] = dataclasses.replace(records[index], hard_idle_memory_metric="windows_private_usage")
        with self.assertRaisesRegex(ValueError, "missing idle memory metric: startup.reader.warm"):
            perf_recovery.build_verification_report(records, platform="linux")
        records[index] = dataclasses.replace(
            records[index],
            hard_idle_memory_metric="linux_pss",
            idle_pss_bytes=367_001_601,
            hard_idle_memory_bytes=367_001_601,
        )
        with self.assertRaisesRegex(ValueError, "idle memory budget exceeded: startup.reader.warm"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_keeps_peak_and_idle_memory_independent(self) -> None:
        records = self._verification_records()
        index = next(index for index, record in enumerate(records) if record.workload_id == "startup.reader.warm" and not record.warmup)
        records[index] = dataclasses.replace(records[index], peak_pss_bytes=629_145_599, hard_memory_bytes=629_145_599)
        report = perf_recovery.build_verification_report(records, platform="linux")
        item = report["workloads"]["startup.reader.warm"]
        self.assertEqual(367_001_600, item["max_idle_memory_bytes"])
        self.assertEqual(367_001_600, item["idle_memory_budget_bytes"])
        self.assertEqual(629_145_600, item["max_hard_memory_bytes"])

    def test_idle_sampler_uses_fixed_window_and_ignores_invalid_samples(self) -> None:
        ticks = [0.0]
        samples = iter([
            {"peak_pss_bytes": 10, "private_usage_bytes": None, "process_count": 2},
            {"peak_pss_bytes": None, "private_usage_bytes": None, "process_count": None},
            {"peak_pss_bytes": 20, "private_usage_bytes": None, "process_count": 3},
            {"peak_pss_bytes": 20, "private_usage_bytes": None, "process_count": 3},
        ])
        result = perf_recovery._sample_idle_memory(
            123,
            "linux",
            duration_seconds=3,
            interval_seconds=1,
            sampler=lambda _pid, _platform: next(samples),
            clock=lambda: ticks[0],
            sleeper=lambda seconds: ticks.__setitem__(0, ticks[0] + seconds),
        )
        self.assertEqual(20, result["hard_idle_memory_bytes"])
        self.assertEqual("linux_pss", result["hard_idle_memory_metric"])
        self.assertEqual(3, result["idle_process_count"])

    def test_verification_report_accepts_complete_three_run_pass(self) -> None:
        workloads = self._verification_workloads()
        records = self._verification_records(workloads=workloads)
        report = perf_recovery.build_verification_report(records, platform="linux")
        self.assertTrue(report["passed"])
        self.assertEqual("linux", report["platform"])
        self.assertEqual(len(workloads), len(report["workloads"]))
        self.assertTrue(all(item["measured_runs"] == 3 for item in report["workloads"].values()))

    def test_verification_report_rejects_duplicate_measured_attempt_identity(self) -> None:
        records = self._verification_records()
        index = next(
            index
            for index, record in enumerate(records)
            if record.workload_id == "tool.inspect.warm" and record.attempt == 3
        )
        records[index] = dataclasses.replace(records[index], attempt=2)
        with self.assertRaisesRegex(ValueError, "duplicate measured attempt: tool.inspect.warm"):
            perf_recovery.build_verification_report(records, platform="linux")

    def test_verification_report_cannot_override_required_manifest_workloads(self) -> None:
        workloads = self._verification_workloads()
        omitted_id = "tool.context.references.depth1"
        subset = {
            workload_id: workload
            for workload_id, workload in workloads.items()
            if workload_id != omitted_id
        }
        records = [
            record
            for record in self._verification_records(workloads=workloads)
            if record.workload_id != omitted_id
        ]
        with self.assertRaisesRegex(ValueError, f"missing hard gate: {omitted_id}"):
            perf_recovery.build_verification_report(records, platform="linux")
        with self.assertRaises(TypeError):
            perf_recovery.build_verification_report(records, workloads=subset, platform="linux")

    def test_jsonl_records_are_one_line_per_attempt_and_sorted(self) -> None:
        request = self._request()
        workload = perf_recovery.Workload(
            workload_id="test",
            command=("version",),
            warmups=1,
            runs=2,
            hard_budget_ms={"development": 2_000, "windows": 5_000},
        )
        command = _python_command("print('{\\\"revision\\\": 4, \\\"view_id\\\": \\\"v1\\\"}')")
        records = perf_recovery.run_workload(request, workload, command=command)
        self.assertEqual(3, len(records))
        out = self.root / "records.jsonl"
        perf_recovery.write_jsonl(out, records)
        lines = out.read_text(encoding="utf-8").splitlines()
        self.assertEqual(3, len(lines))
        for line in lines:
            self.assertEqual(line, json.dumps(json.loads(line), sort_keys=True, separators=(",", ":")))
            self.assertNotIn("\n", line)
        self.assertTrue(all(record.view == "v1" for record in records))
        self.assertTrue(all(record.generation == 4 for record in records))


if __name__ == "__main__":
    unittest.main()
