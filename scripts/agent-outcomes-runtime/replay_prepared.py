#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

SCRIPTS_ROOT = Path(__file__).resolve().parents[1]
CORPUS_ROOT = SCRIPTS_ROOT / "benchmarks" / "agent-outcomes"
sys.path.insert(0, str(SCRIPTS_ROOT))

from benchlib.agent_outcomes_contract import (
    VerificationExecution,
    bind_verifier,
    source_snapshot_sha256,
    validate_task,
    validate_verifier,
    verify_result,
)
from benchlib.agent_outcomes_runner import (
    PodmanVerificationExecutor,
    PreparedEnvironment,
)


class CapturedExecutor:
    def __init__(self, execution: VerificationExecution) -> None:
        self.execution = execution

    def execute(self, argv, candidate_root, timeout_seconds):
        return self.execution


def apply_patch(root: Path, patch_path: Path) -> None:
    completed = subprocess.run(
        ["patch", "-p1", "-i", str(patch_path)],
        cwd=root,
        capture_output=True,
        text=True,
        check=False,
        env={"PATH": os.defpath},
    )
    if completed.returncode != 0:
        raise RuntimeError(
            f"patch failed: {patch_path}: {completed.stdout}{completed.stderr}"
        )


def replay(
    binding_path: Path,
    image_reference: str,
    evidence_root: Path,
    selected_repo_id: str | None = None,
) -> dict[str, object]:
    prepared = PreparedEnvironment.from_manifest(binding_path, image_reference)
    prepared.verify_image("podman")
    evidence = evidence_root.resolve()
    evidence.mkdir(mode=0o700, parents=True)
    repositories = json.loads(
        (CORPUS_ROOT / "repositories.json").read_text(encoding="utf-8")
    )
    tasks = [
        validate_task(json.loads(line))
        for line in (CORPUS_ROOT / "tasks.jsonl")
        .read_text(encoding="utf-8")
        .splitlines()
        if line
    ]
    verifiers = {
        record["verifier_id"]: validate_verifier(record)
        for record in json.loads(
            (CORPUS_ROOT / "verifiers/verifiers.json").read_text(encoding="utf-8")
        )
    }
    records = []
    temporary = Path(tempfile.mkdtemp(prefix="agent-outcomes-offline-replay-"))
    try:
        for repository in repositories:
            if (
                selected_repo_id is not None
                and repository["repo_id"] != selected_repo_id
            ):
                continue
            repo_id = repository["repo_id"]
            source = temporary / "sources" / repo_id
            source.parent.mkdir(exist_ok=True)
            clone_started = time.monotonic()
            clone = subprocess.run(
                ["git", "clone", "--quiet", repository["upstream"], str(source)],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                check=False,
                timeout=600,
                env={"PATH": os.defpath},
            )
            clone_seconds = time.monotonic() - clone_started
            if clone.returncode != 0:
                raise RuntimeError(
                    f"clone failed for {repo_id}: {clone.stderr[-4096:].decode(errors='replace')}"
                )
            subprocess.run(
                [
                    "git",
                    "-C",
                    str(source),
                    "checkout",
                    "--quiet",
                    "--detach",
                    repository["commit"],
                ],
                check=True,
                env={"PATH": os.defpath},
            )
            if source_snapshot_sha256(source) != repository["source_snapshot_sha256"]:
                raise RuntimeError(f"source identity differs for {repo_id}")
            records.append(
                {
                    "repo_id": repo_id,
                    "clone_seconds": clone_seconds,
                    "download_bytes": None,
                    "download_seconds": None,
                }
            )
            for task in (
                value
                for value in tasks
                if value.repo_id == repo_id
                and value.workflow in {"safe_edit", "repair"}
            ):
                bound = bind_verifier(task, verifiers[task.verifier_id])
                for state in ("baseline", "reference", "plausible_wrong"):
                    candidate = temporary / "candidates" / task.verifier_id / state
                    shutil.copytree(
                        source,
                        candidate,
                        ignore=shutil.ignore_patterns(".git"),
                        symlinks=True,
                    )
                    patch_root = CORPUS_ROOT / "verifiers" / repo_id / task.verifier_id
                    if task.workflow == "repair":
                        apply_patch(candidate, patch_root / "seed.patch")
                    if source_snapshot_sha256(candidate) != task.snapshot_sha256:
                        raise RuntimeError(
                            f"task snapshot differs for {task.verifier_id}"
                        )
                    if state != "baseline":
                        apply_patch(
                            candidate,
                            patch_root
                            / (
                                "reference.patch"
                                if state == "reference"
                                else "plausible-wrong.patch"
                            ),
                        )
                    runtime = temporary / "runtime" / task.verifier_id / state
                    (runtime / "miller").mkdir(parents=True)
                    (runtime / "native-miller-mask").mkdir()
                    (runtime / "public-response-schema.json").write_text(
                        "{}", encoding="utf-8"
                    )
                    prepared_repository, setup = prepared.materialize(
                        repo_id, "podman", runtime
                    )
                    executor = PodmanVerificationExecutor(
                        image_reference,
                        prepared_repository=prepared_repository,
                        prepared_runtime_root=runtime,
                    )
                    execution_candidate = (
                        temporary / "execution" / task.verifier_id / state
                    )
                    shutil.copytree(candidate, execution_candidate, symlinks=True)
                    execution = executor.execute(
                        bound.verifier.value["test_argv"],
                        execution_candidate,
                        task.max_wall_seconds,
                    )
                    raw = evidence / f"{task.verifier_id}-{state}.json"
                    row = {
                        "verifier_id": task.verifier_id,
                        "repo_id": repo_id,
                        "state": state,
                        "ran": execution.ran,
                        "returncode": execution.returncode,
                        "prepared_setup": setup,
                        "stdout_sha256": hashlib.sha256(
                            execution.stdout.encode()
                        ).hexdigest(),
                        "stdout_tail": execution.stdout[-4096:],
                        "stderr_sha256": hashlib.sha256(
                            execution.stderr.encode()
                        ).hexdigest(),
                        "stderr_tail": execution.stderr[-4096:],
                    }
                    raw.write_text(json.dumps(row, sort_keys=True), encoding="utf-8")
                    if not execution.ran:
                        raise RuntimeError(
                            f"offline verifier did not run for {task.verifier_id} {state}: {execution.stderr}"
                        )
                    verification = verify_result(
                        bound, {}, candidate, executor=CapturedExecutor(execution)
                    )
                    correct = (
                        verification.correct
                        if state != "baseline"
                        else execution.returncode == 0
                    )
                    expected = state == "reference"
                    if correct != expected:
                        raise RuntimeError(
                            f"unexpected offline replay for {task.verifier_id} {state}: "
                            f"returncode={execution.returncode}, failures={verification.failures}"
                        )
                    row["correct"] = correct
                    raw.write_text(json.dumps(row, sort_keys=True), encoding="utf-8")
                    records.append(
                        {
                            **row,
                            "evidence_path": str(raw),
                            "evidence_sha256": hashlib.sha256(
                                raw.read_bytes()
                            ).hexdigest(),
                        }
                    )
    finally:
        pass
    summary = {
        "schema": "agent-outcomes-prepared-offline-replay-v1",
        "image_reference": image_reference,
        "prepared_binding_sha256": prepared.binding_sha256,
        "network_policy": "none",
        "private_work_root": str(temporary),
        "records": records,
    }
    encoded = json.dumps(summary, sort_keys=True, separators=(",", ":"))
    digest = hashlib.sha256(encoded.encode()).hexdigest()
    path = evidence / f"offline-replay-summary-{digest}.json"
    path.write_text(encoded, encoding="utf-8")
    return {**summary, "summary_path": str(path), "summary_sha256": digest}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--prepared-binding", type=Path, required=True)
    parser.add_argument("--image-reference", required=True)
    parser.add_argument("--evidence-root", type=Path, required=True)
    parser.add_argument("--repo-id")
    arguments = parser.parse_args()
    print(
        json.dumps(
            replay(
                arguments.prepared_binding,
                arguments.image_reference,
                arguments.evidence_root,
                arguments.repo_id,
            ),
            sort_keys=True,
        )
    )


if __name__ == "__main__":
    main()
