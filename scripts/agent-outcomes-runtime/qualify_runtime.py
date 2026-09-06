#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

SCRIPTS_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS_ROOT))

from benchlib.agent_outcomes_contract import validate_campaign
from benchlib.agent_outcomes_runner import NativeAgentRunner, PreparedEnvironment


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def prequalification_campaign(image_digest: str, codex_sha256: str):
    return validate_campaign(
        {
            "contract_id": "agent-outcomes-v1",
            "campaign_id": "physical-isolation-prequalification",
            "task_set_sha256": "0" * 64,
            "host": {
                "name": "codex-cli",
                "version": "0.153.4",
                "binary_sha256": codex_sha256,
            },
            "model": {"model_id": "no-model-invocation", "reasoning": "medium"},
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
            "repetition_count": 1,
            "order_seed": 0,
            "platform_toolchain_image_sha256": image_digest,
            "network_policy": "denied",
            "resource_limits": {"max_parallel_runs": 1, "memory_bytes": 1073741824},
            "approved_total_run_count": 2,
            "pricing": None,
            "approved_money_ceiling": None,
        }
    )


def qualify(
    manifest_path: Path,
    evidence_root: Path,
    prepared_binding_path: Path | None = None,
) -> dict[str, object]:
    manifest_file = manifest_path.resolve(strict=True)
    manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
    codex_sha = manifest["codex"]["sha256"]
    prepared_environment = None
    if prepared_binding_path is None:
        image_digest = manifest["image"]["digest"]
    else:
        binding = json.loads(prepared_binding_path.read_text(encoding="utf-8"))
        image_digest = binding["image_digest"]
    image_reference = f"localhost/miller-agent-outcomes@sha256:{image_digest}"
    if prepared_binding_path is not None:
        prepared_environment = PreparedEnvironment.from_manifest(
            prepared_binding_path, image_reference
        )
    qualification_root = (
        evidence_root.resolve() / f"physical-qualification-{image_digest[:16]}"
    )
    qualification_root.mkdir(mode=0o700, parents=True)
    campaign = prequalification_campaign(image_digest, codex_sha)
    checks = []
    repositories = (
        [None]
        if prepared_environment is None
        else sorted(prepared_environment.repositories)
    )
    for repo_id in repositories:
        for arm_id in ("native", "native+miller-lexical"):
            for mutation in (False, True):
                mode = "rw" if mutation else "ro"
                prefix = "base" if repo_id is None else repo_id
                experiment = (
                    qualification_root / f"{prefix}-{arm_id.replace('+', '-')}-{mode}"
                )
                (experiment / "task-input").mkdir(parents=True)
                (experiment / "private-grader").mkdir()
                (experiment / "task-input" / "source.txt").write_text(
                    "frozen\n", encoding="utf-8"
                )
                runner = NativeAgentRunner(
                    campaign,
                    image_reference=image_reference,
                    codex_path="/usr/local/bin/codex",
                    miller_path="/opt/miller/miller",
                    prepared_environment=prepared_environment,
                )
                result = runner.qualify_isolation(
                    experiment,
                    mutation=mutation,
                    arm_id=arm_id,
                    repo_id=repo_id,
                )
                raw_evidence = Path(result.evidence_path)
                checks.append(
                    {
                        "arm_id": arm_id,
                        "repo_id": repo_id,
                        "workspace_mode": mode,
                        "passed": result.passed,
                        "returncode": result.returncode,
                        "configuration_sha256": result.qualification_sha256,
                        "raw_evidence_path": str(raw_evidence),
                        "raw_evidence_sha256": sha256_file(raw_evidence),
                    }
                )
    summary = {
        "schema": "agent-outcomes-physical-qualification-v1",
        "runtime_manifest_path": str(manifest_file),
        "runtime_manifest_sha256": sha256_file(manifest_file),
        "image_reference": image_reference,
        "codex_sha256": codex_sha,
        "network_policy": "denied",
        "model_invoked": False,
        "provider_configured": False,
        "prepared_binding_path": None
        if prepared_binding_path is None
        else str(prepared_binding_path.resolve()),
        "prepared_binding_sha256": None
        if prepared_binding_path is None
        else sha256_file(prepared_binding_path),
        "checks": checks,
    }
    canonical = json.dumps(summary, sort_keys=True, separators=(",", ":"))
    digest = hashlib.sha256(canonical.encode()).hexdigest()
    summary_path = qualification_root / f"qualification-summary-{digest}.json"
    summary_path.write_text(canonical, encoding="utf-8")
    return {**summary, "summary_sha256": digest, "summary_path": str(summary_path)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime-manifest", type=Path, required=True)
    parser.add_argument("--evidence-root", type=Path, required=True)
    parser.add_argument("--prepared-binding", type=Path)
    arguments = parser.parse_args()
    result = qualify(
        arguments.runtime_manifest, arguments.evidence_root, arguments.prepared_binding
    )
    print(json.dumps(result, sort_keys=True))
    return 0 if all(check["passed"] for check in result["checks"]) else 2


if __name__ == "__main__":
    raise SystemExit(main())
