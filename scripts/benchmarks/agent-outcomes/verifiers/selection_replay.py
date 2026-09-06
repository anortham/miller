"""Replay complete native test inventories before and after frozen changes."""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET
from pathlib import Path

from benchlib.agent_outcomes_contract import source_snapshot_sha256


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def substitute(value: str, paths: dict[str, Path]) -> str:
    for name, path in paths.items():
        value = value.replace("{" + name + "}", str(path))
    return value


def environment_for(config: dict, paths: dict[str, Path]) -> dict[str, str]:
    environment = os.environ.copy()
    for name, value in config["test_environment"].items():
        value = substitute(value, paths)
        if name == "PATH_PREPEND":
            environment["PATH"] = value + os.pathsep + environment["PATH"]
        else:
            environment[name] = value
    return environment


def apply_patch(root: Path, patch: Path) -> None:
    process = subprocess.run(["patch", "-p1", "-i", str(patch)], cwd=root, text=True, capture_output=True)
    if process.returncode != 0:
        raise RuntimeError(f"failed to apply {patch}: {process.stdout}{process.stderr}")


def native_command(repo_id: str, candidate: Path) -> list[str]:
    if repo_id == "flask":
        return ["python", "-m", "pytest", "-q"]
    if repo_id == "express":
        return ["mocha", "--require", "test/support/env", "--reporter", "json", "--check-leaks", "test/", "test/acceptance/"]
    if repo_id == "chi":
        return ["go", "test", "-json", "./..."]
    if repo_id == "ripgrep":
        return ["sh", "-c", "cargo test --workspace --no-fail-fast 2>&1"]
    if repo_id == "command-line-api":
        results = candidate / ".agent-outcomes-results"
        commands = []
        for name, project in (
            ("system", "src/System.CommandLine.Tests/System.CommandLine.Tests.csproj"),
            ("suggest", "src/System.CommandLine.Suggest.Tests/dotnet-suggest.Tests.csproj"),
            ("api", "src/System.CommandLine.ApiCompatibility.Tests/System.CommandLine.ApiCompatibility.Tests.csproj"),
        ):
            commands.append(f"dotnet test {project} -f net8.0 --no-restore --logger 'trx;LogFileName={name}.trx' --results-directory {results} --nologo || rc=1")
        return ["sh", "-c", "rc=0; " + "; ".join(commands) + "; exit $rc"]
    return ["bundle", "exec", "rake", "test", "TESTOPTS=--verbose"]


def find_source(root: Path, name: str, suffix: str, fallback: str) -> str:
    parent = name.split("/")[0].split("(")[0]
    pattern = re.compile(r"\b(?:def|fn|func)\s+(?:\([^)]*\)\s*)?" + re.escape(parent) + r"\b")
    for path in root.rglob("*" + suffix):
        try:
            if pattern.search(path.read_text(encoding="utf-8")):
                return path.relative_to(root).as_posix()
        except UnicodeError:
            pass
    return fallback


def flask_outcomes(root: Path, stdout: str, returncode: int, environment: dict[str, str]) -> dict[tuple[str, str], str]:
    collected = subprocess.run(["python", "-m", "pytest", "--collect-only", "-q"], cwd=root, env=environment, text=True, capture_output=True, timeout=300)
    if collected.returncode != 0:
        raise RuntimeError(f"Flask collection failed: {collected.stderr}")
    node_ids = [line.strip() for line in collected.stdout.splitlines() if "::" in line and not line.startswith("<")]
    failures = set(re.findall(r"^FAILED\s+(.+?)\s+-\s", stdout, re.MULTILINE))
    if returncode not in {0, 1}:
        raise RuntimeError("Flask runner failed outside test assertions")
    return {(node.split("::", 1)[0], node): "failed" if node in failures else "passed" for node in node_ids}


def express_outcomes(root: Path, stdout: str) -> dict[tuple[str, str], str]:
    report = json.loads(stdout)
    failed = {test["fullTitle"] for test in report["failures"]}
    pending = {test["fullTitle"] for test in report["pending"]}
    outcomes = {}
    for test in report["tests"]:
        path = Path(test["file"]).resolve().relative_to(root.resolve()).as_posix()
        outcomes[(path, test["fullTitle"])] = "failed" if test["fullTitle"] in failed else "skipped" if test["fullTitle"] in pending else "passed"
    return outcomes


def go_outcomes(root: Path, stdout: str) -> dict[tuple[str, str], str]:
    outcomes = {}
    module = re.search(r"^module\s+(\S+)", (root / "go.mod").read_text(encoding="utf-8"), re.MULTILINE).group(1)
    for line in stdout.splitlines():
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            continue
        if "Test" not in event or event.get("Action") not in {"pass", "fail", "skip"}:
            continue
        package_suffix = event["Package"].removeprefix(module).lstrip("/")
        relative_package = Path(package_suffix) if package_suffix else Path()
        package_root = root / relative_package
        local_path = find_source(package_root, event["Test"], "_test.go", next((path.name for path in package_root.glob("*_test.go")), "go.mod"))
        path = (relative_package / local_path).as_posix() if relative_package.parts else local_path
        test_id = event["Package"] + "::" + event["Test"]
        outcomes[(path, test_id)] = {"pass": "passed", "fail": "failed", "skip": "skipped"}[event["Action"]]
    return outcomes


def rust_outcomes(root: Path, stdout: str) -> dict[tuple[str, str], str]:
    outcomes = {}
    target = "Cargo.toml"
    for line in stdout.splitlines():
        running = re.search(r"Running (?:unittests|tests/\S+) (\S+\.rs)", line)
        if running:
            target = running.group(1).lstrip("./")
        result = re.match(r"test (.+) \.\.\. (ok|FAILED|ignored)$", line.strip())
        if result:
            fallback = target if (root / target).is_file() else "Cargo.toml"
            path = find_source(root, result.group(1).rsplit("::", 1)[-1], ".rs", fallback)
            outcomes[(path, result.group(1))] = {"ok": "passed", "FAILED": "failed", "ignored": "skipped"}[result.group(2)]
    return outcomes


def csharp_path(root: Path, test_id: str) -> str:
    identity = test_id.split("(", 1)[0]
    method = identity.rsplit(".", 1)[-1]
    class_name = identity.rsplit(".", 2)[-2].split("+")[-1]
    method_paths = []
    for path in (root / "src").rglob("*.cs"):
        source = path.read_text(encoding="utf-8")
        if re.search(r"\b" + re.escape(method) + r"\s*\(", source):
            method_paths.append(path)
            if re.search(r"\bclass\s+" + re.escape(class_name) + r"\b", source):
                return path.relative_to(root).as_posix()
    if method_paths:
        return method_paths[0].relative_to(root).as_posix()
    return "System.CommandLine.sln"


def csharp_outcomes(root: Path) -> dict[tuple[str, str], str]:
    outcomes = {}
    for path in (root / ".agent-outcomes-results").glob("*.trx"):
        for result in ET.parse(path).findall(".//{*}UnitTestResult"):
            test_id = result.attrib["testName"]
            outcome = result.attrib["outcome"].casefold()
            outcomes[(csharp_path(root, test_id), test_id)] = "passed" if outcome == "passed" else "skipped" if outcome in {"notexecuted", "skipped"} else "failed"
    return outcomes


def rake_outcomes(root: Path, stdout: str) -> dict[tuple[str, str], str]:
    outcomes = {}
    current_class = None
    for line in stdout.splitlines():
        class_match = re.match(r"^  ([A-Za-z][A-Za-z0-9_:]+):\s*$", line)
        if class_match:
            current_class = class_match.group(1)
            continue
        test_match = re.match(r"^    (.+):\s+([.FEO])(?::|$)", line)
        if not current_class or not test_match:
            continue
        method, result = test_match.groups()
        test_id = current_class + "#" + method
        candidates = []
        local_path = None
        for path in (root / "test").rglob("*.rb"):
            source = path.read_text(encoding="utf-8")
            if re.search(r"\bdef\s+" + re.escape(method) + r"\b", source):
                candidates.append(path)
                if re.search(r"\bclass\s+" + re.escape(current_class.split("::")[-1]) + r"\b", source):
                    local_path = path.relative_to(root / "test").as_posix()
                    break
        if local_path is None:
            local_path = candidates[0].relative_to(root / "test").as_posix() if candidates else "test_rake_task.rb"
        outcomes[("test/" + local_path, test_id)] = "passed" if result == "." else "skipped" if result == "O" else "failed"
    return outcomes


def parse_outcomes(repo_id: str, root: Path, stdout: str, returncode: int, environment: dict[str, str]) -> dict[tuple[str, str], str]:
    if repo_id == "flask":
        return flask_outcomes(root, stdout, returncode, environment)
    if repo_id == "express":
        return express_outcomes(root, stdout)
    if repo_id == "chi":
        return go_outcomes(root, stdout)
    if repo_id == "ripgrep":
        return rust_outcomes(root, stdout)
    if repo_id == "command-line-api":
        return csharp_outcomes(root)
    return rake_outcomes(root, stdout)


def execution_count(repo_id: str, root: Path, stdout: str, outcomes: dict) -> int:
    if repo_id == "express":
        return json.loads(stdout)["stats"]["tests"]
    if repo_id == "command-line-api":
        return sum(len(ET.parse(path).findall(".//{*}UnitTestResult")) for path in (root / ".agent-outcomes-results").glob("*.trx"))
    if repo_id == "rake":
        match = re.search(r"^(\d+) tests,", stdout, re.MULTILINE)
        if match:
            return int(match.group(1))
    return len(outcomes)


def replay_selection(corpus: Path, sources_root: Path, evidence_root: Path, update_checked: bool, repo_ids: set[str] | None = None) -> dict:
    repositories = json.loads((corpus / "repositories.json").read_text(encoding="utf-8"))
    if repo_ids is not None:
        repositories = [repo for repo in repositories if repo["repo_id"] in repo_ids]
    environments = {item["repo_id"]: item for item in json.loads((corpus / "verifiers/prepared-environments.json").read_text(encoding="utf-8"))}
    verifiers_path = corpus / "verifiers/verifiers.json"
    verifiers = json.loads(verifiers_path.read_text(encoding="utf-8"))
    verifier_by_id = {item["verifier_id"]: item for item in verifiers}
    summaries = {}
    record_path = evidence_root / "selection-record.json"
    full_record = json.loads(record_path.read_text(encoding="utf-8")) if repo_ids is not None and record_path.is_file() else {}
    for repo in repositories:
        repo_id = repo["repo_id"]
        verifier_id = f"{repo_id}-test-selection-v1"
        prepared = evidence_root / "prepared" / repo_id
        setup_source = prepared / "setup-source"
        config = environments[repo_id]
        states = {}
        state_names = ["baseline", "changed"]
        if repo_id == "command-line-api":
            state_names.extend(["baseline_repeat", "changed_repeat"])
        for state in state_names:
            with tempfile.TemporaryDirectory(prefix=f"agent-outcomes-selection-{repo_id}-") as temporary:
                candidate = Path(temporary) / "candidate"
                shutil.copytree(sources_root / repo_id, candidate, ignore=shutil.ignore_patterns(".git"), symlinks=True)
                if state.startswith("changed"):
                    apply_patch(candidate, corpus / f"verifiers/{repo_id}/{verifier_id}/known-change.patch")
                snapshot = source_snapshot_sha256(candidate)
                paths = {"candidate": candidate, "prepared": prepared, "setup_source": setup_source}
                environment = environment_for(config, paths)
                pretest = [substitute(part, paths) for part in config["pre_test_argv"]]
                pretest_record = None
                if pretest:
                    started = time.monotonic()
                    process = subprocess.run(pretest, cwd=candidate, env=environment, text=True, capture_output=True, timeout=300)
                    pretest_record = {"argv": pretest, "returncode": process.returncode, "wall_seconds": round(time.monotonic() - started, 3)}
                    if process.returncode != 0:
                        raise RuntimeError(f"selection pre-test failed for {repo_id}: {process.stderr}")
                argv = native_command(repo_id, candidate)
                started = time.monotonic()
                process = subprocess.run(argv, cwd=candidate, env=environment, text=True, capture_output=True, timeout=600)
                seconds = time.monotonic() - started
                directory = evidence_root / "selection" / verifier_id
                directory.mkdir(parents=True, exist_ok=True)
                output_path = directory / f"{state}.native-output"
                raw_output = process.stdout + process.stderr
                output_path.write_text(raw_output, encoding="utf-8")
                outcomes = parse_outcomes(repo_id, candidate, process.stdout, process.returncode, environment)
                result_artifacts = []
                if repo_id == "command-line-api":
                    trx_directory = directory / f"{state}.trx"
                    if trx_directory.exists():
                        shutil.rmtree(trx_directory)
                    shutil.copytree(candidate / ".agent-outcomes-results", trx_directory)
                    result_artifacts = [{"path": str(path), "sha256": digest(path.read_bytes())} for path in sorted(trx_directory.glob("*.trx"))]
                if not outcomes:
                    raise RuntimeError(f"native runner exposed no cases for {repo_id}")
                states[state] = {
                    "snapshot_sha256": snapshot,
                    "argv": argv,
                    "returncode": process.returncode,
                    "wall_seconds": round(seconds, 3),
                    "pre_test": pretest_record,
                    "raw_output_path": str(output_path),
                    "raw_output_sha256": digest(raw_output.encode()),
                    "result_artifacts": result_artifacts,
                    "runner_execution_count": execution_count(repo_id, candidate, process.stdout, outcomes),
                    "outcomes": [{"path": path, "test_id": test_id, "outcome": outcome} for (path, test_id), outcome in sorted(outcomes.items())],
                }
        baseline = {(item["path"], item["test_id"]): item["outcome"] for item in states["baseline"]["outcomes"]}
        changed = {(item["path"], item["test_id"]): item["outcome"] for item in states["changed"]["outcomes"]}
        if repo_id != "command-line-api" and set(baseline) != set(changed):
            raise RuntimeError(f"native case inventory changed for {repo_id}")
        unstable = []
        if repo_id == "command-line-api":
            baseline_repeat = {(item["path"], item["test_id"]): item["outcome"] for item in states["baseline_repeat"]["outcomes"]}
            changed_repeat = {(item["path"], item["test_id"]): item["outcome"] for item in states["changed_repeat"]["outcomes"]}
            inventory_keys = set(baseline) | set(baseline_repeat) | set(changed) | set(changed_repeat)
            unstable = [{"path": path, "test_id": test_id, "outcomes": [baseline.get((path, test_id), "not_run"), baseline_repeat.get((path, test_id), "not_run"), changed.get((path, test_id), "not_run"), changed_repeat.get((path, test_id), "not_run")]} for path, test_id in sorted(inventory_keys) if len({baseline.get((path, test_id), "not_run"), baseline_repeat.get((path, test_id), "not_run"), changed.get((path, test_id), "not_run"), changed_repeat.get((path, test_id), "not_run")}) != 1]
            impacted = [{"path": path, "test_id": test_id} for path, test_id in sorted(inventory_keys) if baseline.get((path, test_id)) == baseline_repeat.get((path, test_id)) == "passed" and changed.get((path, test_id)) == changed_repeat.get((path, test_id)) == "failed"]
        else:
            inventory_keys = set(baseline)
            impacted = [{"path": path, "test_id": test_id} for (path, test_id), outcome in sorted(baseline.items()) if outcome == "passed" and changed[(path, test_id)] == "failed"]
        if not impacted:
            raise RuntimeError(f"known change did not turn a passing case red for {repo_id}")
        preexisting = [{"path": path, "test_id": test_id, "outcome": outcome} for (path, test_id), outcome in sorted(baseline.items()) if outcome != "passed"]
        transitions = [{"path": path, "test_id": test_id, "before": baseline.get((path, test_id), "not_run"), "after": changed.get((path, test_id), "not_run")} for path, test_id in sorted(inventory_keys) if baseline.get((path, test_id), "not_run") != changed.get((path, test_id), "not_run")]
        inventory = {"inventory_scope": "complete case inventory from the repository native test runner", "native_test_argv": states["baseline"]["argv"], "cases": [{"path": path, "test_id": test_id} for path, test_id in sorted(inventory_keys)]}
        inventory_path = corpus / f"verifiers/{repo_id}/{verifier_id}/case-inventory.json"
        known_change = corpus / f"verifiers/{repo_id}/{verifier_id}/known-change.patch"
        summary = {
            "baseline": {"outcome": "completed", "returncode": states["baseline"]["returncode"], "case_count": len(inventory_keys), "runner_execution_count": max(state["runner_execution_count"] for state in states.values()), "preexisting_nonpass": preexisting},
            "changed": {"outcome": "completed", "returncode": states["changed"]["returncode"], "transition_count": len(transitions)},
            "derived_impacted_cases": impacted,
            "outcome_transitions": transitions,
            "unstable_cases": unstable,
            "unchanged_case_count": len(inventory_keys) - len({(item["path"], item["test_id"]) for item in transitions} | {(item["path"], item["test_id"]) for item in unstable}),
            "known_change_sha256": digest(known_change.read_bytes()),
            "external_record_path": str(evidence_root / "selection-record.json"),
        }
        verifier_by_id[verifier_id]["test_cases"] = impacted
        summaries[verifier_id] = summary
        full_record[verifier_id] = {"source_commit": repo["commit"], "known_change_sha256": summary["known_change_sha256"], "states": states}
        if update_checked:
            inventory_path.write_text(json.dumps(inventory, indent=2) + "\n", encoding="utf-8")
    record_path.write_text(json.dumps(full_record, indent=2) + "\n", encoding="utf-8")
    record_digest = digest(record_path.read_bytes())
    for summary in summaries.values():
        summary["external_record_sha256"] = record_digest
    if update_checked:
        for repo in repositories:
            verifier_id = f"{repo['repo_id']}-test-selection-v1"
            path = corpus / f"verifiers/{repo['repo_id']}/{verifier_id}/selection-evidence.json"
            path.write_text(json.dumps(summaries[verifier_id], indent=2) + "\n", encoding="utf-8")
        verifiers_path.write_text(json.dumps(verifiers, indent=2) + "\n", encoding="utf-8")
    else:
        for repo in repositories:
            verifier_id = f"{repo['repo_id']}-test-selection-v1"
            checked = json.loads((corpus / f"verifiers/{repo['repo_id']}/{verifier_id}/selection-evidence.json").read_text(encoding="utf-8"))
            if checked["derived_impacted_cases"] != summaries[verifier_id]["derived_impacted_cases"]:
                raise RuntimeError(f"checked selection differs from replay for {repo['repo_id']}")
    return {"record_path": str(record_path), "record_sha256": record_digest}
