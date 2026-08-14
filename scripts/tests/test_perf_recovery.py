from __future__ import annotations

import contextlib
import hashlib
import importlib.util
import io
import json
import os
import sys
import tempfile
import textwrap
import unittest
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
        pointer = self.workspace / ".miller" / "store.json"
        pointer.write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "family_id": "11111111-1111-1111-1111-111111111111",
                    "store_root": str(store_root),
                    "view_id": "view-1",
                    "workspace_root": str(self.workspace),
                }
            ),
            encoding="utf-8",
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
        marker = self.root / "cli-launched"

        def fake_replay(request, workloads):
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
            "('MILLER_HOME', 'MILLER_SEMANTIC', 'MILLER_PERF_STORE_COPY')}))"
        )
        record = perf_recovery.run_workload(request, workload, command=command)[0]
        self.assertEqual(str(self.miller_home), record.environment["MILLER_HOME"])
        self.assertEqual("off", record.environment["MILLER_SEMANTIC"])
        self.assertEqual("on", record.environment["MILLER_INDEX_STORE"])
        self.assertEqual(str(self.store_copy), record.environment["MILLER_PERF_STORE_COPY"])
        self.assertNotEqual(str(self.live_store), record.environment["MILLER_PERF_STORE_COPY"])

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
        self.assertEqual(
            ("refresh", "--json", "--wait", "--full"),
            manifest["producer.resolve.full"].command,
        )
        self.assertEqual(
            "tool.context.references.depth0",
            manifest["tool.context.references.depth1"].parity_with,
        )

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

    def test_manifest_rejects_unvalidated_runtime_placeholder(self) -> None:
        manifest_path = Path(__file__).resolve().parents[1] / "benchmarks" / "perf-recovery-workloads.json"
        value = json.loads(manifest_path.read_text(encoding="utf-8"))
        value["workloads"][0]["command"] = ["version", "{machine_specific_target}"]
        path = self.root / "bad-placeholder.json"
        path.write_text(json.dumps(value), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "placeholder"):
            perf_recovery.load_manifest(path)

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
