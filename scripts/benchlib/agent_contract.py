"""Strict data contracts for the agent-efficiency benchmark."""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any


_BENCHMARK_ROOT = Path(__file__).resolve().parents[1] / "benchmarks" / "agent-efficiency"
_CRITICAL_WORKFLOWS = frozenset({"exact_lookup", "references_trace", "impact_tests"})
_ARTIFACT_DIRECTORIES = frozenset({".miller", ".julie", ".eros", ".razorback"})
_PREPARED_ARTIFACT_DIRECTORIES = frozenset({".miller", ".julie"})


@dataclass(frozen=True)
class _FactPredicate:
    predicate_id: str
    source: str
    all_terms: tuple[str, ...]
    any_terms: tuple[str, ...]
    evidence_anchor_ids: tuple[str, ...]


@dataclass(frozen=True)
class _CitationPredicate:
    predicate_id: str
    value: str
    evidence_anchor_ids: tuple[str, ...]


@dataclass(frozen=True)
class _EvidenceAnchor:
    anchor_id: str
    path: str
    symbol: str | None
    line_start: int | None
    line_end: int | None


@dataclass(frozen=True)
class _AnswerEvidence:
    path: str
    claim: str
    symbol: str | None
    line: int | None


@dataclass(frozen=True)
class BenchmarkTask:
    task_id: str
    repo_id: str
    snapshot_id: str
    language: str
    workflow_class: str
    evidence_critical: bool
    prompt: str
    fact_predicates: tuple[_FactPredicate, ...]
    path_cited: tuple[_CitationPredicate, ...]
    symbol_cited: tuple[_CitationPredicate, ...]
    evidence_anchors: tuple[_EvidenceAnchor, ...]
    forbidden_claims: tuple[str, ...]


@dataclass(frozen=True)
class VerificationResult:
    passed: bool
    failures: tuple[str, ...]
    matched_anchor_ids: tuple[str, ...]


@dataclass(frozen=True)
class SnapshotIdentity:
    snapshot_id: str
    repo_id: str
    commit: str
    content_sha256: str
    languages: tuple[str, ...]

    @classmethod
    def capture(
        cls,
        snapshot_id: str,
        repo_id: str,
        languages: Sequence[str],
        root: str | Path,
    ) -> SnapshotIdentity:
        commit, content_sha256, failures = _snapshot_facts(Path(root))
        if failures:
            raise ValueError("; ".join(failures))
        return cls(snapshot_id, repo_id, commit, content_sha256, tuple(languages))

    def verify_root(self, root: str | Path) -> VerificationResult:
        commit, content_sha256, failures = _snapshot_facts(Path(root))
        collected = list(failures)
        if commit and commit != self.commit:
            collected.append("snapshot: commit mismatch")
        if content_sha256 and content_sha256 != self.content_sha256:
            collected.append("snapshot: content SHA-256 mismatch")
        ordered = tuple(dict.fromkeys(collected))
        return VerificationResult(not ordered, ordered, ())

    def verify_prepared_root(self, root: str | Path) -> VerificationResult:
        commit, content_sha256, failures = _snapshot_facts(Path(root), prepared=True)
        collected = list(failures)
        if commit and commit != self.commit:
            collected.append("snapshot: commit mismatch")
        if content_sha256 and content_sha256 != self.content_sha256:
            collected.append("snapshot: content SHA-256 mismatch")
        ordered = tuple(dict.fromkeys(collected))
        return VerificationResult(not ordered, ordered, ())


@dataclass(frozen=True)
class StructuredAnswer:
    status: str
    answer: str
    evidence: tuple[_AnswerEvidence, ...]

    @classmethod
    def from_mapping(cls, value: Mapping[str, Any]) -> StructuredAnswer:
        _validate_value(value, "answer-schema.json")
        return cls(
            status=value["status"],
            answer=value["answer"],
            evidence=tuple(
                _AnswerEvidence(
                    path=item["path"],
                    claim=item["claim"],
                    symbol=item.get("symbol"),
                    line=item.get("line"),
                )
                for item in value["evidence"]
            ),
        )


def load_task_manifest(path: str | Path) -> tuple[BenchmarkTask, ...]:
    value = _load_json(path)
    _validate_value(value, "task-manifest.schema.json")
    tasks = tuple(_task_from_mapping(item) for item in value["tasks"])
    _require_unique((task.task_id for task in tasks), "task_id")
    for task in tasks:
        expected_critical = task.workflow_class in _CRITICAL_WORKFLOWS
        if task.evidence_critical != expected_critical:
            raise ValueError(
                f"task {task.task_id}: evidence_critical must be {str(expected_critical).lower()} "
                f"for {task.workflow_class}"
            )
        anchor_id_values = tuple(anchor.anchor_id for anchor in task.evidence_anchors)
        _require_unique(anchor_id_values, f"task {task.task_id} evidence anchor")
        anchor_ids = set(anchor_id_values)
        for anchor in task.evidence_anchors:
            if (
                anchor.line_start is not None
                and anchor.line_end is not None
                and anchor.line_end < anchor.line_start
            ):
                raise ValueError(
                    f"task {task.task_id} anchor {anchor.anchor_id}: line_end must be at least line_start"
                )
        predicates = (*task.fact_predicates, *task.path_cited, *task.symbol_cited)
        _require_unique((predicate.predicate_id for predicate in predicates), f"task {task.task_id} predicate")
        for predicate in predicates:
            missing = set(predicate.evidence_anchor_ids) - anchor_ids
            if missing:
                raise ValueError(
                    f"task {task.task_id} predicate {predicate.predicate_id}: missing anchor "
                    f"{sorted(missing)[0]}"
                )
    return tasks


def load_snapshot_manifest(path: str | Path) -> tuple[SnapshotIdentity, ...]:
    value = _load_json(path)
    _validate_value(value, "snapshot-manifest.schema.json")
    snapshots = tuple(
        SnapshotIdentity(
            snapshot_id=item["snapshot_id"],
            repo_id=item["repo_id"],
            commit=item["commit"],
            content_sha256=item["content_sha256"],
            languages=tuple(item["languages"]),
        )
        for item in value["snapshots"]
    )
    _require_unique((snapshot.snapshot_id for snapshot in snapshots), "snapshot_id")
    _require_unique((snapshot.repo_id for snapshot in snapshots), "repo_id")
    return snapshots


def verify_answer(
    task: BenchmarkTask,
    answer: StructuredAnswer | Mapping[str, Any],
    snapshot_root: str | Path,
) -> VerificationResult:
    structured = answer if isinstance(answer, StructuredAnswer) else StructuredAnswer.from_mapping(answer)
    root = Path(snapshot_root).resolve()
    failures: list[str] = []
    if structured.status != "answered":
        failures.append(f"answer: status {structured.status} does not satisfy an answerable task")

    matched_by_evidence: list[tuple[_AnswerEvidence, tuple[str, ...]]] = []
    matched_anchor_ids: set[str] = set()
    for evidence in structured.evidence:
        path_failure = _evidence_path_failure(root, evidence.path)
        if path_failure:
            failures.append(path_failure)
            matched_by_evidence.append((evidence, ()))
            continue
        matched = tuple(
            anchor.anchor_id
            for anchor in task.evidence_anchors
            if _evidence_matches_anchor(evidence, anchor)
        )
        if not matched:
            failures.append(f"evidence: no accepted anchor for {evidence.path}")
        matched_anchor_ids.update(matched)
        matched_by_evidence.append((evidence, matched))

    for predicate in task.fact_predicates:
        required = set(predicate.evidence_anchor_ids)
        missing = required - matched_anchor_ids
        if missing:
            failures.append(
                f"predicate {predicate.predicate_id}: missing evidence anchor {sorted(missing)[0]}"
            )
        if predicate.source == "answer":
            source_text = structured.answer
        else:
            source_text = "\n".join(
                evidence.claim
                for evidence, matched in matched_by_evidence
                if required.intersection(matched)
            )
        lowered = source_text.casefold()
        missing_terms = [term for term in predicate.all_terms if term.casefold() not in lowered]
        if missing_terms:
            failures.append(
                f"predicate {predicate.predicate_id}: missing all_terms value {missing_terms[0]}"
            )
        if predicate.any_terms and not any(term.casefold() in lowered for term in predicate.any_terms):
            failures.append(f"predicate {predicate.predicate_id}: no any_terms value matched")

    for predicate in task.path_cited:
        required = set(predicate.evidence_anchor_ids)
        cited_anchor_ids = {
            anchor_id
            for evidence, matched in matched_by_evidence
            if evidence.path == predicate.value
            for anchor_id in matched
        }
        if not required.issubset(cited_anchor_ids):
            failures.append(f"predicate {predicate.predicate_id}: required path was not cited")

    for predicate in task.symbol_cited:
        required = set(predicate.evidence_anchor_ids)
        cited_anchor_ids = {
            anchor_id
            for evidence, matched in matched_by_evidence
            if evidence.symbol == predicate.value
            for anchor_id in matched
        }
        if not required.issubset(cited_anchor_ids):
            failures.append(f"predicate {predicate.predicate_id}: required symbol was not cited")

    combined_claims = "\n".join(
        (structured.answer, *(evidence.claim for evidence in structured.evidence))
    ).casefold()
    for forbidden in task.forbidden_claims:
        if forbidden.casefold() in combined_claims:
            failures.append(f"forbidden claim matched: {forbidden}")

    ordered_failures = tuple(dict.fromkeys(failures))
    return VerificationResult(
        passed=not ordered_failures,
        failures=ordered_failures,
        matched_anchor_ids=tuple(sorted(matched_anchor_ids)),
    )


def count_tool_output_tokens(text: str) -> int:
    import tiktoken

    encoding = tiktoken.get_encoding("o200k_base")
    return len(encoding.encode(text))


def validate_run_result(value: Mapping[str, Any]) -> None:
    _validate_value(value, "run-result.schema.json")
    if value["uncited_tool_output_tokens"] > value["tool_output_tokens"]:
        raise ValueError(
            "run-result: uncited_tool_output_tokens must not exceed tool_output_tokens"
        )


def _task_from_mapping(item: Mapping[str, Any]) -> BenchmarkTask:
    return BenchmarkTask(
        task_id=item["task_id"],
        repo_id=item["repo_id"],
        snapshot_id=item["snapshot_id"],
        language=item["language"],
        workflow_class=item["workflow_class"],
        evidence_critical=item["evidence_critical"],
        prompt=item["prompt"],
        fact_predicates=tuple(
            _FactPredicate(
                predicate_id=predicate["predicate_id"],
                source=predicate["source"],
                all_terms=tuple(predicate["all_terms"]),
                any_terms=tuple(predicate.get("any_terms", ())),
                evidence_anchor_ids=tuple(predicate["evidence_anchor_ids"]),
            )
            for predicate in item["fact_predicates"]
        ),
        path_cited=tuple(
            _CitationPredicate(
                predicate_id=predicate["predicate_id"],
                value=predicate["path"],
                evidence_anchor_ids=tuple(predicate["evidence_anchor_ids"]),
            )
            for predicate in item["path_cited"]
        ),
        symbol_cited=tuple(
            _CitationPredicate(
                predicate_id=predicate["predicate_id"],
                value=predicate["symbol"],
                evidence_anchor_ids=tuple(predicate["evidence_anchor_ids"]),
            )
            for predicate in item["symbol_cited"]
        ),
        evidence_anchors=tuple(
            _EvidenceAnchor(
                anchor_id=anchor["anchor_id"],
                path=anchor["path"],
                symbol=anchor.get("symbol"),
                line_start=anchor.get("line_start"),
                line_end=anchor.get("line_end"),
            )
            for anchor in item["evidence_anchors"]
        ),
        forbidden_claims=tuple(item["forbidden_claims"]),
    )


def _load_json(path: str | Path) -> Any:
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"manifest: {exc}") from exc


def _validate_value(value: Any, schema_name: str) -> None:
    from jsonschema import Draft202012Validator

    schema = _load_json(_BENCHMARK_ROOT / schema_name)
    errors = sorted(
        Draft202012Validator(schema).iter_errors(value),
        key=lambda error: tuple(str(part) for part in error.absolute_path),
    )
    if not errors:
        return
    error = errors[0]
    location = ".".join(str(part) for part in error.absolute_path) or "$"
    raise ValueError(f"{schema_name} {location}: {error.message}")


def _require_unique(values: Iterable[str], label: str) -> None:
    seen: set[str] = set()
    for value in values:
        if value in seen:
            raise ValueError(f"duplicate {label}: {value}")
        seen.add(value)


def _evidence_matches_anchor(evidence: _AnswerEvidence, anchor: _EvidenceAnchor) -> bool:
    if evidence.path != anchor.path:
        return False
    if anchor.symbol is not None and evidence.symbol != anchor.symbol:
        return False
    if anchor.line_start is not None:
        if evidence.line is None or evidence.line < anchor.line_start:
            return False
    if anchor.line_end is not None:
        if evidence.line is None or evidence.line > anchor.line_end:
            return False
    return True


def _evidence_path_failure(root: Path, relative: str) -> str | None:
    candidate = (root / relative).resolve()
    try:
        candidate.relative_to(root)
    except ValueError:
        return f"evidence: path escapes snapshot root: {relative}"
    if not candidate.is_file():
        return f"evidence: path does not exist: {relative}"
    return None


def _snapshot_facts(
    root: Path,
    *,
    prepared: bool = False,
) -> tuple[str, str, tuple[str, ...]]:
    resolved = root.resolve()
    allowed_artifacts = _PREPARED_ARTIFACT_DIRECTORIES if prepared else frozenset()
    failures = list(_snapshot_structure_failures(resolved, allowed_artifacts))
    if prepared:
        failures.extend(_prepared_artifact_failures(resolved))
    try:
        top_level = Path(_run_git(resolved, "rev-parse", "--show-toplevel")).resolve()
        if top_level != resolved:
            failures.append("snapshot: root is not the repository top level")
        commit = _run_git(resolved, "rev-parse", "HEAD")
        status_args = ["status", "--porcelain=v1", "--untracked-files=all"]
        if prepared:
            status_args.extend(
                [
                    "--",
                    ".",
                    ":(top,exclude,glob).miller/**",
                    ":(top,exclude,glob).julie/**",
                ]
            )
        status = _run_git(resolved, *status_args)
        if status:
            failures.append("snapshot: working tree is dirty")
        content_sha256 = _committed_content_sha256(resolved)
    except (OSError, ValueError, subprocess.CalledProcessError) as exc:
        detail = exc.stderr.strip() if isinstance(exc, subprocess.CalledProcessError) and exc.stderr else str(exc)
        failures.append(f"snapshot: Git inspection failed: {detail}")
        commit = ""
        content_sha256 = ""
    return commit, content_sha256, tuple(dict.fromkeys(failures))


def _snapshot_structure_failures(
    root: Path,
    allowed_top_level_artifacts: frozenset[str] = frozenset(),
) -> tuple[str, ...]:
    failures: list[str] = []
    for current, directories, files in os.walk(root):
        current_path = Path(current)
        relative = current_path.relative_to(root)
        if relative != Path(".") and ".git" in directories + files:
            failures.append(f"snapshot: nested Git worktree content at {relative.as_posix()}/.git")
        artifact_names = _ARTIFACT_DIRECTORIES.intersection(directories)
        if relative == Path("."):
            artifact_names -= allowed_top_level_artifacts
        artifact_names = sorted(artifact_names)
        for artifact in artifact_names:
            artifact_path = (relative / artifact).as_posix()
            failures.append(f"snapshot: product or benchmark artifact at {artifact_path}")
        directories[:] = [
            name
            for name in directories
            if name != ".git" and name not in _ARTIFACT_DIRECTORIES
        ]
    return tuple(failures)


def _prepared_artifact_failures(root: Path) -> tuple[str, ...]:
    failures: list[str] = []
    for artifact in sorted(_PREPARED_ARTIFACT_DIRECTORIES):
        artifact_root = root / artifact
        if artifact_root.is_symlink():
            failures.append(f"snapshot: prepared artifact path is a symbolic link: {artifact}")
            continue
        if not artifact_root.is_dir():
            failures.append(f"snapshot: required prepared directory is missing: {artifact}")
            continue
        for current, directories, files in os.walk(artifact_root):
            directories.sort()
            files.sort()
            current_path = Path(current)
            for name in directories + files:
                candidate = current_path / name
                relative = candidate.relative_to(root).as_posix()
                if name == ".git":
                    failures.append(f"snapshot: nested Git worktree content at {relative}")
                if candidate.is_symlink():
                    failures.append(f"snapshot: prepared artifact path is a symbolic link: {relative}")
            directories[:] = [
                name
                for name in directories
                if name != ".git" and not (current_path / name).is_symlink()
            ]
    return tuple(failures)


def _committed_content_sha256(root: Path) -> str:
    tree = _run_git_bytes(root, "ls-tree", "-r", "-z", "--full-tree", "HEAD")
    digest = hashlib.sha256()
    for record in tree.split(b"\0"):
        if not record:
            continue
        metadata, path_bytes = record.split(b"\t", 1)
        mode, object_type, object_id = metadata.split(b" ", 2)
        if object_type != b"blob":
            raise ValueError(f"snapshot: nested Git worktree content at {path_bytes.decode('utf-8')}")
        blob = _run_git_bytes(root, "cat-file", "blob", object_id.decode("ascii"))
        digest.update(mode)
        digest.update(b"\0")
        digest.update(path_bytes)
        digest.update(b"\0")
        digest.update(len(blob).to_bytes(8, "big"))
        digest.update(blob)
    return digest.hexdigest()


def _run_git(root: Path, *args: str) -> str:
    return subprocess.run(
        ["git", "-C", str(root), *args],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def _run_git_bytes(root: Path, *args: str) -> bytes:
    return subprocess.run(
        ["git", "-C", str(root), *args],
        check=True,
        capture_output=True,
    ).stdout
