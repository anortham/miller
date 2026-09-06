#!/usr/bin/env python3
"""Replay the frozen mutation corpus against externally stored upstream sources."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path


CORPUS = Path(__file__).resolve().parents[1]
SCRIPTS = CORPUS.parents[1]
sys.path.insert(0, str(SCRIPTS))

from benchlib.agent_outcomes_contract import (
    VerificationExecution,
    bind_verifier,
    source_snapshot_sha256,
    validate_task,
    validate_verifier,
    verify_result,
)
from selection_replay import replay_selection


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def expand(value: str, paths: dict[str, Path]) -> str:
    for name, path in paths.items():
        value = value.replace("{" + name + "}", str(path))
    return value


def run(argv: list[str], root: Path, environment: dict[str, str], timeout: int) -> tuple[subprocess.CompletedProcess[str], float]:
    started = time.monotonic()
    process = subprocess.run(argv, cwd=root, env=environment, text=True, capture_output=True, timeout=timeout)
    return process, time.monotonic() - started


class PreparedExecutor:
    def __init__(self, repo_id: str, state: str, setup_source: Path, prepared: Path, evidence_dir: Path, config: dict, pattern: str):
        self.repo_id = repo_id
        self.state = state
        self.setup_source = setup_source
        self.prepared = prepared
        self.evidence_dir = evidence_dir
        self.config = config
        self.pattern = pattern
        self.record: dict = {}

    def execute(self, argv, candidate_root, timeout_seconds):
        candidate = Path(candidate_root)
        paths = {"candidate": candidate, "prepared": self.prepared, "setup_source": self.setup_source}
        environment = os.environ.copy()
        for name, value in self.config["test_environment"].items():
            value = expand(value, paths)
            if name == "PATH_PREPEND":
                environment["PATH"] = value + os.pathsep + environment["PATH"]
            else:
                environment[name] = value
        pretest_argv = [expand(part, paths) for part in self.config["pre_test_argv"]]
        pretest_seconds = 0.0
        if pretest_argv:
            pretest, pretest_seconds = run(pretest_argv, candidate, environment, timeout_seconds)
            (self.evidence_dir / f"{self.state}.pretest.stdout").write_text(pretest.stdout, encoding="utf-8")
            (self.evidence_dir / f"{self.state}.pretest.stderr").write_text(pretest.stderr, encoding="utf-8")
            if pretest.returncode != 0:
                self.record = {"expected_test_executed": False, "pre_test_returncode": pretest.returncode, "pre_test_seconds": round(pretest_seconds, 3)}
                return VerificationExecution(False, None, pretest.stdout, pretest.stderr)
        command = [expand(part, paths) for part in argv]
        process, seconds = run(command, candidate, environment, timeout_seconds)
        stdout_path = self.evidence_dir / f"{self.state}.stdout"
        stderr_path = self.evidence_dir / f"{self.state}.stderr"
        stdout_path.write_text(process.stdout, encoding="utf-8")
        stderr_path.write_text(process.stderr, encoding="utf-8")
        combined = process.stdout + process.stderr
        self.record = {
            "argv": command,
            "returncode": process.returncode,
            "wall_seconds": round(seconds, 3),
            "pre_test_seconds": round(pretest_seconds, 3),
            "expected_test_executed": re.search(self.pattern, combined) is not None,
            "raw_stdout_path": str(stdout_path),
            "raw_stdout_sha256": sha256(process.stdout.encode()),
            "raw_stderr_path": str(stderr_path),
            "raw_stderr_sha256": sha256(process.stderr.encode()),
        }
        return VerificationExecution(True, process.returncode, process.stdout, process.stderr)


def apply_patch(root: Path, patch: Path) -> None:
    process = subprocess.run(["patch", "-p1", "-i", str(patch)], cwd=root, text=True, capture_output=True)
    if process.returncode != 0:
        raise RuntimeError(f"failed to apply {patch}: {process.stdout}{process.stderr}")


def prepare(repo: dict, source: Path, evidence_root: Path, config: dict) -> tuple[Path, Path, dict]:
    prepared = evidence_root / "prepared" / repo["repo_id"]
    setup_source = prepared / "setup-source"
    if prepared.exists():
        shutil.rmtree(prepared)
    shutil.copytree(source, setup_source, ignore=shutil.ignore_patterns(".git"), symlinks=True)
    paths = {"candidate": setup_source, "prepared": prepared, "setup_source": setup_source}
    environment = os.environ.copy()
    for name, value in config["test_environment"].items():
        value = expand(value, paths)
        if name == "PATH_PREPEND":
            environment["PATH"] = value + os.pathsep + environment["PATH"]
        else:
            environment[name] = value
    argv = [expand(part, paths) for part in config["prepare_once_argv"]]
    process, seconds = run(argv, setup_source, environment, 600)
    directory = evidence_root / "setup" / repo["repo_id"]
    directory.mkdir(parents=True, exist_ok=True)
    record = {
        "argv": argv,
        "returncode": process.returncode,
        "wall_seconds": round(seconds, 3),
        "stdout_path": str(directory / "stdout"),
        "stderr_path": str(directory / "stderr"),
        "stdout_sha256": sha256(process.stdout.encode()),
        "stderr_sha256": sha256(process.stderr.encode()),
    }
    (directory / "stdout").write_text(process.stdout, encoding="utf-8")
    (directory / "stderr").write_text(process.stderr, encoding="utf-8")
    if process.returncode != 0:
        raise RuntimeError(f"dependency preparation failed for {repo['repo_id']}")
    return prepared, setup_source, record


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sources-root", type=Path, required=True)
    parser.add_argument("--evidence-root", type=Path, required=True)
    parser.add_argument("--update-checked-evidence", action="store_true")
    parser.add_argument("--repo", action="append")
    parser.add_argument("--skip-selection", action="store_true")
    args = parser.parse_args()
    sources_root = args.sources_root.resolve(strict=True)
    evidence_root = args.evidence_root.resolve()
    evidence_root.mkdir(parents=True, exist_ok=True)
    repositories = json.loads((CORPUS / "repositories.json").read_text(encoding="utf-8"))
    if args.repo:
        repositories = [repo for repo in repositories if repo["repo_id"] in set(args.repo)]
    tasks = [validate_task(json.loads(line)) for line in (CORPUS / "tasks.jsonl").read_text(encoding="utf-8").splitlines() if line]
    verifier_records = json.loads((CORPUS / "verifiers/verifiers.json").read_text(encoding="utf-8"))
    verifiers = {value["verifier_id"]: validate_verifier(value) for value in verifier_records}
    raw_verifiers = {value["verifier_id"]: value for value in verifier_records}
    environments = {value["repo_id"]: value for value in json.loads((CORPUS / "verifiers/prepared-environments.json").read_text(encoding="utf-8"))}
    contracts = json.loads((CORPUS / "verifiers/execution-contracts.json").read_text(encoding="utf-8"))
    checked_evidence = json.loads((CORPUS / "verifiers/evidence.json").read_text(encoding="utf-8"))
    record_path = evidence_root / "replay-record.json"
    replay_record = json.loads(record_path.read_text(encoding="utf-8")) if args.repo and record_path.is_file() else {"sources_root": str(sources_root), "evidence_root": str(evidence_root), "repositories": {}, "verifiers": {}}
    for repo in repositories:
        repo_id = repo["repo_id"]
        source = (sources_root / repo_id).resolve(strict=True)
        commit = subprocess.run(["git", "rev-parse", "HEAD"], cwd=source, text=True, capture_output=True, check=True).stdout.strip()
        snapshot = source_snapshot_sha256(source)
        if commit != repo["commit"] or snapshot != repo["source_snapshot_sha256"]:
            raise RuntimeError(f"source identity mismatch for {repo_id}")
        prepared, setup_source, setup_record = prepare(repo, source, evidence_root, environments[repo_id])
        replay_record["repositories"][repo_id] = {"commit": commit, "snapshot_sha256": snapshot, "setup": setup_record}
        for task in (item for item in tasks if item.repo_id == repo_id and item.workflow in {"safe_edit", "repair"}):
            verifier = verifiers[task.verifier_id]
            bound = bind_verifier(task, verifier)
            directory = evidence_root / "mutation" / task.verifier_id
            directory.mkdir(parents=True, exist_ok=True)
            states = {}
            for state in ("baseline", "reference", "plausible_wrong"):
                with tempfile.TemporaryDirectory(prefix=f"agent-outcomes-{repo_id}-") as temporary:
                    candidate = Path(temporary) / "candidate"
                    shutil.copytree(source, candidate, ignore=shutil.ignore_patterns(".git"), symlinks=True)
                    if task.workflow == "repair":
                        apply_patch(candidate, CORPUS / f"verifiers/{repo_id}/{task.verifier_id}/seed.patch")
                    observed = source_snapshot_sha256(candidate)
                    if observed != task.snapshot_sha256:
                        raise RuntimeError(f"task snapshot mismatch for {task.verifier_id}")
                    if state != "baseline":
                        filename = "reference.patch" if state == "reference" else "plausible-wrong.patch"
                        apply_patch(candidate, CORPUS / f"verifiers/{repo_id}/{task.verifier_id}/{filename}")
                    executor = PreparedExecutor(repo_id, state, setup_source, prepared, directory, environments[repo_id], contracts[task.verifier_id]["executed_patterns"][state])
                    if state == "baseline":
                        execution = executor.execute(verifier.value["test_argv"], candidate, task.max_wall_seconds)
                        correct = execution.returncode == 0
                        failures = []
                    else:
                        verification = verify_result(bound, {}, candidate, executor=executor)
                        correct = verification.correct
                        failures = list(verification.failures)
                    expected_pass = state == "reference"
                    if correct != expected_pass or not executor.record.get("expected_test_executed"):
                        raise RuntimeError(f"unexpected {state} result for {task.verifier_id}: {executor.record}, {failures}")
                    states[state] = {**executor.record, "outcome": "passed" if correct else "failed", "snapshot_sha256": observed, "verification_failures": failures}
            artifact_paths = {
                "reference_patch": CORPUS / f"verifiers/{repo_id}/{task.verifier_id}/reference.patch",
                "plausible_wrong_patch": CORPUS / f"verifiers/{repo_id}/{task.verifier_id}/plausible-wrong.patch",
            }
            if task.workflow == "repair":
                artifact_paths["seed_patch"] = CORPUS / f"verifiers/{repo_id}/{task.verifier_id}/seed.patch"
            replay_record["verifiers"][task.verifier_id] = {
                "task_snapshot_sha256": task.snapshot_sha256,
                "verifier_sha256": sha256(json.dumps(raw_verifiers[task.verifier_id], sort_keys=True, separators=(",", ":")).encode()),
                "artifact_sha256": {name: sha256(path.read_bytes()) for name, path in artifact_paths.items()},
                "states": states,
            }
            if args.update_checked_evidence:
                checked_evidence[task.verifier_id].update(states)
    record_path.write_text(json.dumps(replay_record, indent=2) + "\n", encoding="utf-8")
    if not args.skip_selection:
        selection = replay_selection(CORPUS, sources_root, evidence_root, args.update_checked_evidence, set(args.repo) if args.repo else None)
        replay_record["selection"] = selection
        record_path.write_text(json.dumps(replay_record, indent=2) + "\n", encoding="utf-8")
    if args.update_checked_evidence:
        (CORPUS / "verifiers/evidence.json").write_text(json.dumps(checked_evidence, indent=2) + "\n", encoding="utf-8")
    print(record_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
