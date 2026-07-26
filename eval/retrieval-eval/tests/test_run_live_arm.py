import importlib.util
import json
import pathlib
import subprocess
import tempfile
import unittest
from unittest import mock


SCRIPT_PATH = pathlib.Path(__file__).parents[1] / "run-live-arm.py"
SPEC = importlib.util.spec_from_file_location("run_live_arm", SCRIPT_PATH)
RUN_LIVE_ARM = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUN_LIVE_ARM)


class RunLiveArmTests(unittest.TestCase):
    def run_main(self, arm, query_rows, completed, corpus_names=("miller",), latency=False):
        with tempfile.TemporaryDirectory() as directory:
            base = pathlib.Path(directory)
            roots = {}
            for repo in corpus_names:
                root = base / f"corpus-{repo}"
                root.mkdir()
                roots[repo] = root
            queries = base / "queries.jsonl"
            queries.write_text(
                "\n".join(json.dumps(row) for row in query_rows) + "\n",
                encoding="utf-8",
            )
            out = base / "results.jsonl"
            latency_out = base / "latency.jsonl"
            argv = [
                "run-live-arm.py",
                "--queries",
                str(queries),
                "--binary",
                "miller",
                "--out",
                str(out),
                "--arm",
                arm,
            ]
            for repo, root in roots.items():
                argv.extend(["--corpus", f"{repo}={root}"])
            if latency:
                argv.extend(["--latency-out", str(latency_out)])
            responses = completed if isinstance(completed, list) else [completed]

            with mock.patch.object(RUN_LIVE_ARM.sys, "argv", argv), mock.patch.object(
                RUN_LIVE_ARM.subprocess, "run", side_effect=responses
            ) as run:
                result = RUN_LIVE_ARM.main()

            result_rows = [
                json.loads(line) for line in out.read_text(encoding="utf-8").splitlines()
            ]
            latency_rows = (
                [
                    json.loads(line)
                    for line in latency_out.read_text(encoding="utf-8").splitlines()
                ]
                if latency
                else []
            )
            return result, run, result_rows, latency_rows

    def test_production_missing_runtime_fails_before_reading_queries(self):
        self.assert_semantic_preflight_failure("semantic embedding runtime is unavailable")

    def test_production_missing_vectors_fails_before_reading_queries(self):
        self.assert_semantic_preflight_failure("no serving vector artifact")

    def assert_semantic_preflight_failure(self, error):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory) / "corpus"
            root.mkdir()
            queries = pathlib.Path(directory) / "sealed-queries.jsonl"
            out = pathlib.Path(directory) / "results.jsonl"
            completed = subprocess.CompletedProcess([], 1, stdout="", stderr=error)
            argv = [
                "run-live-arm.py",
                "--queries",
                str(queries),
                "--binary",
                "miller",
                "--corpus",
                f"miller={root}",
                "--out",
                str(out),
                "--arm",
                "production",
            ]

            with mock.patch.object(RUN_LIVE_ARM.sys, "argv", argv), mock.patch.object(
                RUN_LIVE_ARM, "read_queries"
            ) as read_queries, mock.patch.object(
                RUN_LIVE_ARM.subprocess, "run", return_value=completed
            ) as run, self.assertRaisesRegex(
                SystemExit, f"semantic preflight failed for corpus 'miller'.*{error}"
            ):
                RUN_LIVE_ARM.main()

            read_queries.assert_not_called()
            command = run.call_args.args[0]
            self.assertEqual("hybrid", command[command.index("--arm") + 1])
            self.assertNotIn("sealed-queries", " ".join(command))

    def test_valid_production_corpora_preflight_then_emit_policy_version(self):
        queries = [
            {
                "query_id": "q1",
                "query": "first production query",
                "repo": "a",
                "search_mode": "auto",
            },
            {
                "query_id": "q2",
                "query": "second production query",
                "repo": "b",
                "search_mode": "content",
            },
        ]
        completed = [
            subprocess.CompletedProcess([], 0, stdout="[]", stderr=""),
            subprocess.CompletedProcess([], 0, stdout="[]", stderr=""),
            subprocess.CompletedProcess(
                [], 0, stdout=json.dumps([{"file": "src/First.cs"}]), stderr=""
            ),
            subprocess.CompletedProcess(
                [], 0, stdout=json.dumps([{"path": "docs/Second.md"}]), stderr=""
            ),
        ]

        result, run, result_rows, latency_rows = self.run_main(
            "production", queries, completed, ("a", "b"), latency=True
        )

        self.assertEqual(0, result)
        self.assertEqual(4, run.call_count)
        for call in run.call_args_list[:2]:
            command = call.args[0]
            self.assertEqual("hybrid", command[command.index("--arm") + 1])
            self.assertNotIn("first production query", command)
            self.assertNotIn("second production query", command)
        for call in run.call_args_list[2:]:
            self.assertNotIn("--arm", call.args[0])
        self.assertEqual(
            [
                {"query_id": "q1", "policy_version": 2, "ranked": ["src/First.cs"]},
                {"query_id": "q2", "policy_version": 2, "ranked": ["docs/Second.md"]},
            ],
            result_rows,
        )
        self.assertEqual([2, 2], [row["policy_version"] for row in latency_rows])

    def test_lexical_arm_skips_semantic_preflight(self):
        queries = [
            {
                "query_id": "q1",
                "query": "lexical query",
                "repo": "miller",
                "search_mode": "auto",
            }
        ]
        completed = subprocess.CompletedProcess([], 0, stdout="[]", stderr="")

        result, run, result_rows, _ = self.run_main("lexical", queries, completed)

        self.assertEqual(0, result)
        run.assert_called_once()
        command = run.call_args.args[0]
        self.assertEqual("lexical", command[command.index("--arm") + 1])
        self.assertEqual("off", run.call_args.kwargs["env"]["MILLER_SEMANTIC"])
        self.assertEqual(2, result_rows[0]["policy_version"])

    def test_semantic_development_arms_preflight_before_queries(self):
        queries = [
            {
                "query_id": "q1",
                "query": "development query",
                "repo": "miller",
                "search_mode": "auto",
            }
        ]
        completed = subprocess.CompletedProcess([], 0, stdout="[]", stderr="")

        for arm in ("semantic", "hybrid"):
            with self.subTest(arm=arm):
                result, run, _, _ = self.run_main(arm, queries, [completed, completed])

                self.assertEqual(0, result)
                self.assertEqual(2, run.call_count)
                preflight = run.call_args_list[0].args[0]
                query = run.call_args_list[1].args[0]
                self.assertEqual("hybrid", preflight[preflight.index("--arm") + 1])
                self.assertEqual(arm, query[query.index("--arm") + 1])

    def test_read_queries_defaults_missing_search_mode_to_auto(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "queries.jsonl"
            path.write_text(
                json.dumps({"query_id": "q1", "query": "find it", "repo": "miller"}) + "\n",
                encoding="utf-8",
            )

            queries = RUN_LIVE_ARM.read_queries(path)

        self.assertEqual("auto", queries[0]["search_mode"])

    def test_read_queries_rejects_invalid_search_mode(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "queries.jsonl"
            path.write_text(
                json.dumps(
                    {
                        "query_id": "q1",
                        "query": "find it",
                        "repo": "miller",
                        "search_mode": "docs",
                    }
                )
                + "\n",
                encoding="utf-8",
            )

            with self.assertRaisesRegex(SystemExit, "search_mode 'docs' is not in the enum"):
                RUN_LIVE_ARM.read_queries(path)

    def test_content_queries_use_the_frozen_mode_without_an_invalid_forced_arm(self):
        completed = subprocess.CompletedProcess([], 0, stdout="[]", stderr="")

        for arm in ("production", "lexical", "semantic", "hybrid"):
            with self.subTest(arm=arm), mock.patch.object(
                RUN_LIVE_ARM.subprocess, "run", return_value=completed
            ) as run:
                RUN_LIVE_ARM.run_search(
                    "miller", "/repo", "find it", "content", 10, arm
                )

                command = run.call_args.args[0]
                self.assertEqual("content", command[command.index("--mode") + 1])
                if arm in ("production", "semantic", "hybrid"):
                    self.assertNotIn("--arm", command)
                else:
                    self.assertEqual(arm, command[command.index("--arm") + 1])
                self.assertEqual("off", run.call_args.kwargs["env"]["MILLER_SEMANTIC_CANARY"])
                expected_semantic = "off" if arm == "lexical" else "on"
                self.assertEqual(
                    expected_semantic, run.call_args.kwargs["env"]["MILLER_SEMANTIC"]
                )

    def test_symbol_routes_keep_explicit_development_arm_forcing(self):
        completed = subprocess.CompletedProcess([], 0, stdout="[]", stderr="")

        for arm in ("lexical", "semantic", "hybrid"):
            with self.subTest(arm=arm), mock.patch.object(
                RUN_LIVE_ARM.subprocess, "run", return_value=completed
            ) as run:
                RUN_LIVE_ARM.run_search("miller", "/repo", "find it", "auto", 10, arm)

                command = run.call_args.args[0]
                self.assertEqual("auto", command[command.index("--mode") + 1])
                self.assertEqual(arm, command[command.index("--arm") + 1])


if __name__ == "__main__":
    unittest.main()
