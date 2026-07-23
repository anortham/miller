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
_TAKEOVER_CONTRACT_ID = "takeover-evaluation-v1"
_ACTION_TARGET_FIELDS = {
    "inspect_symbol": (frozenset({"path", "symbol_id"}), (frozenset({"symbol_id"}),)),
    "inspect_file": (frozenset({"path"}), (frozenset({"path"}),)),
    "assemble_context": (
        frozenset({"path", "symbol_id"}),
        (frozenset({"path"}), frozenset({"symbol_id"})),
    ),
    "trace_callers": (frozenset({"symbol_id"}), (frozenset({"symbol_id"}),)),
    "trace_callees": (frozenset({"symbol_id"}), (frozenset({"symbol_id"}),)),
    "trace_call_path": (
        frozenset({"symbol_id", "target_symbol_id"}),
        (frozenset({"symbol_id", "target_symbol_id"}),),
    ),
    "cite_reference_site": (
        frozenset({"reference_site"}),
        (frozenset({"reference_site"}),),
    ),
    "select_tests": (frozenset({"test_path"}), (frozenset({"test_path"}),)),
    "propose_edit": (
        frozenset({"path", "symbol_id"}),
        (frozenset({"path"}), frozenset({"symbol_id"})),
    ),
    "propose_rename": (frozenset({"symbol_id"}), (frozenset({"symbol_id"}),)),
    "read_log": (frozenset({"path"}), (frozenset({"path"}),)),
    "query_pattern": (frozenset({"pattern_id"}), (frozenset({"pattern_id"}),)),
    "recover_workspace": (
        frozenset({"workspace_selector"}),
        (frozenset({"workspace_selector"}),),
    ),
    "report_empty": (
        frozenset({"workspace_selector"}),
        (frozenset({"workspace_selector"}),),
    ),
    "refuse_unsafe": (
        frozenset({"symbol_id", "path", "workspace_selector"}),
        (
            frozenset({"symbol_id"}),
            frozenset({"path"}),
            frozenset({"workspace_selector"}),
        ),
    ),
}
_EXACT_ACTION_TARGET_ALTERNATIVES = frozenset({"refuse_unsafe"})


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
    relevance_grade: int | None = None


@dataclass(frozen=True)
class _AnswerEvidence:
    path: str
    claim: str
    symbol: str | None
    line: int | None


@dataclass(frozen=True)
class _ReferenceSiteIdentity:
    path: str
    line_start: int
    line_end: int
    column_start: int | None
    column_end: int | None
    reference_kind: str
    containing_symbol_id: str | None
    source_symbol_id: str | None
    target_symbol_id: str | None
    resolution: str


@dataclass(frozen=True)
class _ReferenceSite:
    site_id: str
    identity: _ReferenceSiteIdentity


@dataclass(frozen=True)
class _ActionTarget:
    path: str | None = None
    symbol_id: str | None = None
    target_symbol_id: str | None = None
    reference_site: _ReferenceSiteIdentity | None = None
    test_path: str | None = None
    pattern_id: str | None = None
    workspace_selector: str | None = None


@dataclass(frozen=True)
class _AcceptableAction:
    action_id: str
    kind: str
    target: _ActionTarget
    requirement_group: str
    evidence_anchor_ids: tuple[str, ...]
    reference_site_ids: tuple[str, ...]


@dataclass(frozen=True)
class _ForbiddenAction:
    action_id: str
    kind: str
    target: _ActionTarget
    reason: str


@dataclass(frozen=True)
class _SubmittedAction:
    kind: str
    target: _ActionTarget


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
    contract_id: str | None = None
    capabilities: tuple[str, ...] = ()
    expected_outcome: str | None = None
    acceptable_actions: tuple[_AcceptableAction, ...] = ()
    forbidden_actions: tuple[_ForbiddenAction, ...] = ()
    reference_sites: tuple[_ReferenceSite, ...] = ()
    uncertainty_expectation: str | None = None


@dataclass(frozen=True)
class VerificationResult:
    passed: bool
    failures: tuple[str, ...]
    matched_anchor_ids: tuple[str, ...]
    ordered_evidence_matches: tuple[str | None, ...] = ()
    observed_outcome: str | None = None
    wrong_action_count: int = 0


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
    actions: tuple[_SubmittedAction, ...] = ()
    contract_id: str | None = None

    @classmethod
    def from_mapping(cls, value: Mapping[str, Any]) -> StructuredAnswer:
        _validate_value(value, "answer-schema.json")
        for item in value["evidence"]:
            path = item["path"]
            relative = PurePosixPath(path)
            if relative.is_absolute() or "\\" in path or ".." in relative.parts:
                raise ValueError("answer-schema.json: evidence path does not match repo-relative path contract")
        actions = tuple(_submitted_action(item) for item in value.get("actions", ()))
        for action in actions:
            _validate_action_target("answer", action.kind, action.kind, action.target)
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
            actions=actions,
            contract_id=value.get("contract_id"),
        )


def load_task_manifest(path: str | Path) -> tuple[BenchmarkTask, ...]:
    value = _load_json(path)
    _validate_value(value, "task-manifest.schema.json")
    contract_id = value.get("contract_id")
    tasks = tuple(_task_from_mapping(item, contract_id=contract_id) for item in value["tasks"])
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
        if task.contract_id == _TAKEOVER_CONTRACT_ID:
            _validate_takeover_task(task)
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
    if task.contract_id == _TAKEOVER_CONTRACT_ID:
        return _verify_takeover_answer(task, structured, root)
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


def _verify_takeover_answer(
    task: BenchmarkTask,
    structured: StructuredAnswer,
    root: Path,
) -> VerificationResult:
    failures: list[str] = []
    if structured.contract_id != _TAKEOVER_CONTRACT_ID:
        failures.append(
            f"answer: contract_id must be {_TAKEOVER_CONTRACT_ID}"
        )

    expected_status = {
        "success": "answered",
        "empty": "not_found",
        "refusal": "blocked",
    }[task.expected_outcome]
    if structured.status != expected_status:
        failures.append(
            f"outcome: expected {task.expected_outcome} status {expected_status}, "
            f"got {structured.status}"
        )

    matched_by_evidence: list[tuple[_AnswerEvidence, tuple[str, ...]]] = []
    ordered_evidence_matches: list[str | None] = []
    matched_anchor_ids: set[str] = set()
    for evidence in structured.evidence:
        path_failure = _evidence_path_failure(root, evidence.path)
        if path_failure:
            failures.append(path_failure)
            ordered_evidence_matches.append(None)
            matched_by_evidence.append((evidence, ()))
            continue
        matched = tuple(
            anchor.anchor_id
            for anchor in task.evidence_anchors
            if _evidence_matches_anchor(evidence, anchor)
        )
        if len(matched) > 1:
            failures.append(f"evidence: multiple accepted anchors for {evidence.path}")
            ordered_evidence_matches.append(None)
            matched_by_evidence.append((evidence, ()))
            continue
        matched_anchor_id = matched[0] if matched else None
        ordered_evidence_matches.append(
            None
            if matched_anchor_id in matched_anchor_ids
            else matched_anchor_id
        )
        if matched_anchor_id is not None:
            matched_anchor_ids.add(matched_anchor_id)
        matched_by_evidence.append((evidence, matched))

    _append_semantic_failures(
        task,
        structured,
        matched_by_evidence,
        matched_anchor_ids,
        failures,
    )

    satisfied_groups: set[str] = set()
    wrong_action_identities: set[str] = set()
    forbidden_by_identity = {
        _action_identity_key(action.kind, action.target): action
        for action in task.forbidden_actions
    }
    reference_sites = {
        site.site_id: site.identity
        for site in task.reference_sites
    }
    submitted_reference_sites = {
        action.target.reference_site
        for action in structured.actions
        if action.target.reference_site is not None
    }
    for action in structured.actions:
        identity = _action_identity_key(action.kind, action.target)
        forbidden = forbidden_by_identity.get(identity)
        if forbidden is not None:
            wrong_action_identities.add(identity)
            failures.append(
                f"action: forbidden {action.kind} matched {forbidden.action_id}: "
                f"{forbidden.reason}"
            )
            continue
        acceptable = [
            label
            for label in task.acceptable_actions
            if label.kind == action.kind and label.target == action.target
        ]
        if not acceptable:
            wrong_action_identities.add(identity)
            failures.append(f"action: unrecognized {action.kind} target")
            continue
        for label in acceptable:
            if not set(label.evidence_anchor_ids).issubset(matched_anchor_ids):
                continue
            if label.reference_site_ids:
                required_sites = {
                    reference_sites[site_id]
                    for site_id in label.reference_site_ids
                }
                if not required_sites.issubset(submitted_reference_sites):
                    continue
            satisfied_groups.add(label.requirement_group)

    required_groups = {
        action.requirement_group
        for action in task.acceptable_actions
    }
    for group in sorted(required_groups - satisfied_groups):
        failures.append(f"action: missing requirement group {group}")

    if task.uncertainty_expectation == "must_resolve":
        if (
            task.expected_outcome == "success"
            and not _grounded_exact_requirement_groups(task).intersection(satisfied_groups)
        ):
            failures.append(
                "uncertainty: must_resolve requires a grounded exact identity action"
            )
        if any(
            action.target.reference_site is not None
            and action.target.reference_site.resolution != "exact"
            for action in structured.actions
        ):
            failures.append("uncertainty: must_resolve does not permit fallback or unresolved sites")
    elif task.uncertainty_expectation == "must_disclose":
        if not any(
            action.target.reference_site is not None
            and action.target.reference_site.resolution in {"fallback", "unresolved"}
            for action in structured.actions
        ):
            failures.append(
                "uncertainty: must_disclose requires a typed fallback or unresolved site"
            )
    elif task.uncertainty_expectation == "must_refuse":
        if not any(action.kind == "refuse_unsafe" for action in structured.actions):
            failures.append("uncertainty: must_refuse requires refuse_unsafe")
        if any(action.kind != "refuse_unsafe" for action in structured.actions):
            failures.append("uncertainty: must_refuse does not permit conflicting actions")

    ordered_failures = tuple(dict.fromkeys(failures))
    wrong_action_count = len(wrong_action_identities)
    passed = not ordered_failures and wrong_action_count == 0
    return VerificationResult(
        passed=passed,
        failures=ordered_failures,
        matched_anchor_ids=tuple(sorted(matched_anchor_ids)),
        ordered_evidence_matches=tuple(ordered_evidence_matches),
        observed_outcome=task.expected_outcome if passed else "wrong_answer",
        wrong_action_count=wrong_action_count,
    )


def _append_semantic_failures(
    task: BenchmarkTask,
    structured: StructuredAnswer,
    matched_by_evidence: Sequence[tuple[_AnswerEvidence, tuple[str, ...]]],
    matched_anchor_ids: set[str],
    failures: list[str],
) -> None:
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


def _task_from_mapping(
    item: Mapping[str, Any],
    *,
    contract_id: str | None = None,
) -> BenchmarkTask:
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
                relevance_grade=anchor.get("relevance_grade"),
            )
            for anchor in item["evidence_anchors"]
        ),
        forbidden_claims=tuple(item["forbidden_claims"]),
        contract_id=contract_id,
        capabilities=tuple(item.get("capabilities", ())),
        expected_outcome=item.get("expected_outcome"),
        acceptable_actions=tuple(
            _acceptable_action(action) for action in item.get("acceptable_actions", ())
        ),
        forbidden_actions=tuple(
            _forbidden_action(action) for action in item.get("forbidden_actions", ())
        ),
        reference_sites=tuple(
            _reference_site(site) for site in item.get("reference_sites", ())
        ),
        uncertainty_expectation=item.get("uncertainty_expectation"),
    )


def _reference_site(value: Mapping[str, Any]) -> _ReferenceSite:
    return _ReferenceSite(
        site_id=value["site_id"],
        identity=_reference_site_identity(value),
    )


def _reference_site_identity(value: Mapping[str, Any]) -> _ReferenceSiteIdentity:
    return _ReferenceSiteIdentity(
        path=value["path"],
        line_start=value["line_start"],
        line_end=value["line_end"],
        column_start=value.get("column_start"),
        column_end=value.get("column_end"),
        reference_kind=value["reference_kind"],
        containing_symbol_id=value["containing_symbol_id"],
        source_symbol_id=value["source_symbol_id"],
        target_symbol_id=value["target_symbol_id"],
        resolution=value["resolution"],
    )


def _action_target(value: Mapping[str, Any]) -> _ActionTarget:
    reference_site = value.get("reference_site")
    return _ActionTarget(
        path=value.get("path"),
        symbol_id=value.get("symbol_id"),
        target_symbol_id=value.get("target_symbol_id"),
        reference_site=(
            _reference_site_identity(reference_site)
            if isinstance(reference_site, Mapping)
            else None
        ),
        test_path=value.get("test_path"),
        pattern_id=value.get("pattern_id"),
        workspace_selector=value.get("workspace_selector"),
    )


def _acceptable_action(value: Mapping[str, Any]) -> _AcceptableAction:
    return _AcceptableAction(
        action_id=value["action_id"],
        kind=value["kind"],
        target=_action_target(value["target"]),
        requirement_group=value["requirement_group"],
        evidence_anchor_ids=tuple(value.get("evidence_anchor_ids", ())),
        reference_site_ids=tuple(value.get("reference_site_ids", ())),
    )


def _forbidden_action(value: Mapping[str, Any]) -> _ForbiddenAction:
    return _ForbiddenAction(
        action_id=value["action_id"],
        kind=value["kind"],
        target=_action_target(value["target"]),
        reason=value["reason"],
    )


def _submitted_action(value: Mapping[str, Any]) -> _SubmittedAction:
    return _SubmittedAction(
        kind=value["kind"],
        target=_action_target(value["target"]),
    )


def _validate_takeover_task(task: BenchmarkTask) -> None:
    anchor_ids = {anchor.anchor_id for anchor in task.evidence_anchors}
    if task.expected_outcome == "success" and not any(
        anchor.relevance_grade is not None for anchor in task.evidence_anchors
    ):
        raise ValueError(
            f"task {task.task_id}: success requires at least one graded evidence anchor"
        )
    _require_unique(
        (
            f"{anchor.path}\0{anchor.symbol}\0{anchor.line_start}\0{anchor.line_end}"
            for anchor in task.evidence_anchors
        ),
        f"task {task.task_id} evidence anchor identity",
    )
    for index, anchor in enumerate(task.evidence_anchors):
        for other in task.evidence_anchors[index + 1 :]:
            if _evidence_anchors_overlap(anchor, other):
                raise ValueError(
                    f"task {task.task_id}: overlapping evidence anchors "
                    f"{anchor.anchor_id} and {other.anchor_id}"
                )
    site_ids = tuple(site.site_id for site in task.reference_sites)
    _require_unique(site_ids, f"task {task.task_id} reference site")
    _require_unique(
        (_reference_identity_key(site.identity) for site in task.reference_sites),
        f"task {task.task_id} reference site identity",
    )
    for site in task.reference_sites:
        _validate_reference_site_identity(task.task_id, site.site_id, site.identity)

    all_actions = (*task.acceptable_actions, *task.forbidden_actions)
    _require_unique(
        (action.action_id for action in all_actions),
        f"task {task.task_id} action id",
    )
    _require_unique(
        (_action_identity_key(action.kind, action.target) for action in all_actions),
        f"task {task.task_id} action identity",
    )
    for action in task.acceptable_actions:
        missing_anchors = set(action.evidence_anchor_ids) - anchor_ids
        if missing_anchors:
            raise ValueError(
                f"task {task.task_id} action {action.action_id}: missing anchor "
                f"{sorted(missing_anchors)[0]}"
            )
        missing_sites = set(action.reference_site_ids) - set(site_ids)
        if missing_sites:
            raise ValueError(
                f"task {task.task_id} action {action.action_id}: missing reference site "
                f"{sorted(missing_sites)[0]}"
            )
        _validate_action_target(task.task_id, action.action_id, action.kind, action.target)
    for action in task.forbidden_actions:
        _validate_action_target(task.task_id, action.action_id, action.kind, action.target)

    if (
        task.expected_outcome == "success"
        and task.uncertainty_expectation == "must_resolve"
        and not _grounded_exact_requirement_groups(task)
    ):
        raise ValueError(
            f"task {task.task_id}: must_resolve requires a mandatory exact identity "
            "requirement group"
        )

    acceptable_kinds = {action.kind for action in task.acceptable_actions}
    if task.expected_outcome == "empty" and "report_empty" not in acceptable_kinds:
        raise ValueError(f"task {task.task_id}: empty outcome requires report_empty")
    if task.expected_outcome == "refusal":
        if task.uncertainty_expectation != "must_refuse":
            raise ValueError(
                f"task {task.task_id}: refusal outcome requires must_refuse"
            )
        if "refuse_unsafe" not in acceptable_kinds:
            raise ValueError(f"task {task.task_id}: refusal outcome requires refuse_unsafe")
    elif task.uncertainty_expectation == "must_refuse":
        raise ValueError(
            f"task {task.task_id}: must_refuse requires expected_outcome refusal"
        )


def _grounded_exact_requirement_groups(task: BenchmarkTask) -> frozenset[str]:
    anchors = {anchor.anchor_id: anchor for anchor in task.evidence_anchors}
    reference_sites = {
        site.site_id: site.identity
        for site in task.reference_sites
    }
    actions_by_group: dict[str, list[_AcceptableAction]] = {}
    for action in task.acceptable_actions:
        actions_by_group.setdefault(action.requirement_group, []).append(action)
    return frozenset(
        group
        for group, actions in actions_by_group.items()
        if all(
            _action_has_grounded_exact_identity(action, anchors, reference_sites)
            for action in actions
        )
    )


def _action_has_grounded_exact_identity(
    action: _AcceptableAction,
    anchors: Mapping[str, _EvidenceAnchor],
    reference_sites: Mapping[str, _ReferenceSiteIdentity],
) -> bool:
    target = action.target
    if target.symbol_id is not None:
        return True
    if (
        target.reference_site is not None
        and target.reference_site.resolution == "exact"
        and target.reference_site.target_symbol_id is not None
    ):
        return True
    if any(
        site_id in reference_sites
        and reference_sites[site_id].resolution == "exact"
        and reference_sites[site_id].target_symbol_id is not None
        for site_id in action.reference_site_ids
    ):
        return True

    required_anchors = tuple(
        anchors[anchor_id]
        for anchor_id in action.evidence_anchor_ids
        if anchor_id in anchors and anchors[anchor_id].relevance_grade is not None
    )
    if target.path is not None:
        return any(anchor.path == target.path for anchor in required_anchors)
    if target.test_path is not None:
        return any(anchor.path == target.test_path for anchor in required_anchors)
    if target.pattern_id is not None or target.workspace_selector is not None:
        return bool(required_anchors)
    return False


def _validate_action_target(
    task_id: str,
    action_id: str,
    kind: str,
    target: _ActionTarget,
) -> None:
    present = {
        field
        for field in (
            "path",
            "symbol_id",
            "target_symbol_id",
            "reference_site",
            "test_path",
            "pattern_id",
            "workspace_selector",
        )
        if getattr(target, field) is not None
    }
    allowed, required_alternatives = _ACTION_TARGET_FIELDS[kind]
    target_matches = (
        present in required_alternatives
        if kind in _EXACT_ACTION_TARGET_ALTERNATIVES
        else (
            present.issubset(allowed)
            and any(
                required.issubset(present)
                for required in required_alternatives
            )
        )
    )
    if not target_matches:
        raise ValueError(
            f"task {task_id} action {action_id}: typed target does not match {kind}"
        )
    if target.reference_site is not None:
        _validate_reference_site_identity(task_id, action_id, target.reference_site)


def _validate_reference_site_identity(
    task_id: str,
    label_id: str,
    site: _ReferenceSiteIdentity,
) -> None:
    if site.line_end < site.line_start:
        raise ValueError(
            f"task {task_id} reference {label_id}: line_end must be at least line_start"
        )
    if (
        site.line_start == site.line_end
        and site.column_start is not None
        and site.column_end is not None
        and site.column_end <= site.column_start
    ):
        raise ValueError(
            f"task {task_id} reference {label_id}: column_end must be at least column_start"
        )
    if site.resolution == "unresolved":
        if site.target_symbol_id is not None:
            raise ValueError(
                f"task {task_id} reference {label_id}: unresolved target_symbol_id must be null"
            )
    elif site.target_symbol_id is None:
        raise ValueError(
            f"task {task_id} reference {label_id}: {site.resolution} target_symbol_id is required"
        )


def _reference_identity_key(site: _ReferenceSiteIdentity) -> str:
    return json.dumps(
        {
            "column_end": site.column_end,
            "column_start": site.column_start,
            "containing_symbol_id": site.containing_symbol_id,
            "line_end": site.line_end,
            "line_start": site.line_start,
            "path": site.path,
            "reference_kind": site.reference_kind,
            "resolution": site.resolution,
            "source_symbol_id": site.source_symbol_id,
            "target_symbol_id": site.target_symbol_id,
        },
        sort_keys=True,
        separators=(",", ":"),
    )


def _evidence_anchors_overlap(first: _EvidenceAnchor, second: _EvidenceAnchor) -> bool:
    if first.path != second.path:
        return False
    if (
        first.symbol is not None
        and second.symbol is not None
        and first.symbol != second.symbol
    ):
        return False
    first_start = first.line_start if first.line_start is not None else 1
    second_start = second.line_start if second.line_start is not None else 1
    first_end = first.line_end if first.line_end is not None else 2**63 - 1
    second_end = second.line_end if second.line_end is not None else 2**63 - 1
    return max(first_start, second_start) <= min(first_end, second_end)


def _action_identity_key(kind: str, target: _ActionTarget) -> str:
    return json.dumps(
        {
            "kind": kind,
            "target": {
                "path": target.path,
                "symbol_id": target.symbol_id,
                "target_symbol_id": target.target_symbol_id,
                "reference_site": (
                    _reference_identity_key(target.reference_site)
                    if target.reference_site is not None
                    else None
                ),
                "test_path": target.test_path,
                "pattern_id": target.pattern_id,
                "workspace_selector": target.workspace_selector,
            },
        },
        sort_keys=True,
        separators=(",", ":"),
    )


def _unique_json_object(pairs: Sequence[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"duplicate JSON object key: {key}")
        value[key] = item
    return value


def _load_json(path: str | Path) -> Any:
    try:
        return json.loads(
            Path(path).read_text(encoding="utf-8"),
            object_pairs_hook=_unique_json_object,
        )
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
        if prepared and _has_ignored_untracked_source(resolved):
            status = status or "ignored untracked source"
        if status:
            failures.append("snapshot: working tree is dirty")
        content_sha256 = _committed_content_sha256(resolved)
    except (OSError, ValueError, subprocess.CalledProcessError) as exc:
        detail = exc.stderr.strip() if isinstance(exc, subprocess.CalledProcessError) and exc.stderr else str(exc)
        failures.append(f"snapshot: Git inspection failed: {detail}")
        commit = ""
        content_sha256 = ""
    return commit, content_sha256, tuple(dict.fromkeys(failures))


def _has_ignored_untracked_source(root: Path) -> bool:
    ignored = _run_git_bytes(root, "ls-files", "--others", "--ignored", "--exclude-standard", "-z")
    for path_bytes in ignored.split(b"\0"):
        if not path_bytes:
            continue
        path = PurePosixPath(os.fsdecode(path_bytes))
        if not path.parts or path.parts[0] not in _PREPARED_ARTIFACT_DIRECTORIES:
            return True
    return False


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
