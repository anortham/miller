import importlib.util
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parent


def load_prepare_module():
    spec = importlib.util.spec_from_file_location(
        "prepare_runtime", ROOT / "prepare_runtime.py"
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_qualify_module():
    spec = importlib.util.spec_from_file_location(
        "qualify_runtime", ROOT / "qualify_runtime.py"
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class RuntimeAssetsTests(unittest.TestCase):
    def test_containerfile_pins_base_and_contains_every_corpus_toolchain(self):
        source = (ROOT / "Containerfile").read_text(encoding="utf-8")

        self.assertIn("fedora@sha256:b013b98e", source)
        for package in (
            "uv",
            "nodejs20-npm",
            "golang",
            "rust",
            "cargo",
            "dotnet-sdk-8.0",
            "dotnet-sdk-10.0",
            "ruby",
            "rubygem-bundler",
        ):
            self.assertIn(package, source)
        self.assertIn("COPY --chmod=0755 codex /usr/local/bin/codex", source)
        self.assertIn("COPY miller/ /opt/miller/", source)
        self.assertNotIn("API_KEY", source)
        self.assertNotIn("auth", source.casefold())

    def test_prepare_command_uses_immutable_inputs_and_writes_outside_source_tree(self):
        module = load_prepare_module()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            codex = root / "codex"
            codex.write_bytes(b"codex")
            codex.chmod(0o755)
            miller = root / "miller"
            miller.mkdir()
            (miller / "miller").write_bytes(b"miller")
            (miller / "miller").chmod(0o755)
            evidence = root / "evidence"
            with mock.patch.object(module, "run_checked") as run_checked:
                run_checked.side_effect = [
                    "codex-cli 0.153.4\n",
                    "1.27.2+fixture\n",
                    "sha256:" + "b" * 64 + "\n",
                    "sha256:" + "a" * 64 + " sha256:" + "b" * 64 + "\n",
                    "package-b 2 x86_64\npackage-a 1 noarch\n",
                ]

                result = module.prepare(
                    codex,
                    miller,
                    evidence,
                    "localhost/miller-agent-outcomes:prequalification",
                )

            build_command = run_checked.call_args_list[2].args[0]
            self.assertIn("--pull=never", build_command)
            self.assertIn("--network=pasta", build_command)
            self.assertEqual("a" * 64, result["image"]["digest"])
            self.assertTrue(Path(result["manifest_path"]).is_file())
            self.assertNotIn("seconds", result["build"])
            self.assertTrue(Path(result["setup_evidence_path"]).is_file())

    def test_prequalification_campaign_is_network_denied_and_has_no_model_or_runtime_identity(
        self,
    ):
        module = load_qualify_module()

        campaign = module.prequalification_campaign("a" * 64, "b" * 64)

        self.assertEqual("denied", campaign.value["network_policy"])
        self.assertEqual("no-model-invocation", campaign.value["model"]["model_id"])
        self.assertTrue(
            all(arm["runtime_identity"] is None for arm in campaign.value["arms"])
        )

    def test_dependency_recipe_has_six_repo_specific_offline_environments(self):
        source = (ROOT / "prepare_dependencies.py").read_text(encoding="utf-8")

        for repo_id in (
            "flask",
            "express",
            "chi",
            "ripgrep",
            "command-line-api",
            "rake",
        ):
            self.assertIn(f'"{repo_id}"', source)
        self.assertIn('"npm_config_offline": "true"', source)
        self.assertIn('"GOPROXY": "off"', source)
        self.assertIn('"CARGO_NET_OFFLINE": "true"', source)
        self.assertNotIn("API_KEY", source)


if __name__ == "__main__":
    unittest.main()
