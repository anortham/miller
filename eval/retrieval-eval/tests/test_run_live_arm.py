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
