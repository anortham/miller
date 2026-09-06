from __future__ import annotations

import hashlib
import json
import math
import os
import re
import shutil
import stat
import tempfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath, PureWindowsPath
from types import MappingProxyType
from typing import Any, Mapping, Protocol, Sequence


CONTRACT_ID = "agent-outcomes-v1"
_HEX_256 = re.compile(r"^[0-9a-f]{64}$")
_COMMIT = re.compile(r"^(?:[0-9a-f]{40}|[0-9a-f]{64})$")
_IDENTIFIER = re.compile(r"^[a-z][a-z0-9-]{0,127}$")
_WORKFLOWS = {"location", "concept", "references", "safe_edit", "repair", "test_selection"}
_TASK_FIELDS = {
    "contract_id", "task_id", "repo_id", "source_commit", "snapshot_sha256",
    "language", "workflow", "prompt", "verifier_id", "allowed_write_paths",
    "max_wall_seconds", "max_model_tokens",
}
_CAMPAIGN_FIELDS = {
    "contract_id", "campaign_id", "task_set_sha256", "host", "model", "arms",
    "repetition_count", "order_seed", "platform_toolchain_image_sha256",
    "network_policy", "resource_limits", "approved_total_run_count", "pricing",
    "approved_money_ceiling",
}
_RUN_FIELDS = {
    "contract_id", "campaign_sha256", "run_id", "task_id", "arm_id", "repetition",
    "order", "outcome", "verifier_evidence_sha256", "wall_time_seconds",
    "native_tool_counts", "miller_calls", "total_model_input_tokens",
    "total_model_cached_tokens", "total_model_output_tokens", "raw_event_sha256",
    "price_derived_cost",
}


@dataclass(frozen=True)
class OutcomeTask:
    contract_id: str
    task_id: str
    repo_id: str
    source_commit: str
    snapshot_sha256: str
    language: str
    workflow: str
    prompt: str
    verifier_id: str
    allowed_write_paths: tuple[str, ...]
    max_wall_seconds: int
    max_model_tokens: int


@dataclass(frozen=True)
class Campaign:
    campaign_id: str
    repetition_count: int
    approved_total_run_count: int
    value: Mapping[str, Any]


@dataclass(frozen=True)
class RunRecord:
    outcome: str
    price_derived_cost: float | None
    value: Mapping[str, Any]


@dataclass(frozen=True)
class FrozenVerifier:
    verifier_id: str
    kind: str
    value: Mapping[str, Any]


@dataclass(frozen=True)
class VerifiableTask:
    task: OutcomeTask
    verifier: FrozenVerifier


@dataclass(frozen=True)
class VerificationExecution:
    ran: bool
    returncode: int | None
    stdout: str = ""
    stderr: str = ""


class VerificationExecutor(Protocol):
    def execute(
        self, argv: Sequence[str], candidate_root: Path, timeout_seconds: int
    ) -> VerificationExecution: ...


@dataclass(frozen=True)
class Verification:
    correct: bool
    failures: tuple[str, ...]
    evidence: Mapping[str, Any]


def load_json(path: str | Path) -> Any:
    try:
        return json.loads(
            Path(path).read_text(encoding="utf-8"),
            object_pairs_hook=_unique_object,
            parse_constant=_reject_json_constant,
        )
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"JSON document: {exc}") from exc


def validate_task(mapping: Mapping[str, Any]) -> OutcomeTask:
    _reject_nonfinite(mapping, "task")
    value = _mapping(mapping, "task")
    _exact_fields(value, _TASK_FIELDS, "task")
    _equal(value["contract_id"], CONTRACT_ID, "task contract_id")
    for field in ("task_id", "repo_id", "language", "verifier_id"):
        _identity(value[field], f"task {field}")
    if not isinstance(value["source_commit"], str) or not _COMMIT.fullmatch(value["source_commit"]):
        raise ValueError("task source_commit must be a full lowercase Git object id")
    _sha256(value["snapshot_sha256"], "task snapshot_sha256")
    if value["workflow"] not in _WORKFLOWS:
        raise ValueError(f"task workflow is unsupported: {value['workflow']}")
    if not isinstance(value["prompt"], str) or not value["prompt"].strip():
        raise ValueError("task prompt must be non-empty")
    if len(value["prompt"]) > 20_000:
        raise ValueError("task prompt must be at most 20000 characters")
    paths = _string_list(value["allowed_write_paths"], "task allowed_write_paths", unique=True)
    for path in paths:
        _repo_path(path, "task allowed_write_paths")
    _positive_int(value["max_wall_seconds"], "task max_wall_seconds")
    _positive_int(value["max_model_tokens"], "task max_model_tokens")
    return OutcomeTask(
        contract_id=CONTRACT_ID,
        task_id=value["task_id"], repo_id=value["repo_id"],
        source_commit=value["source_commit"], snapshot_sha256=value["snapshot_sha256"],
        language=value["language"], workflow=value["workflow"], prompt=value["prompt"],
        verifier_id=value["verifier_id"], allowed_write_paths=tuple(paths),
        max_wall_seconds=value["max_wall_seconds"], max_model_tokens=value["max_model_tokens"],
    )


def validate_campaign(mapping: Mapping[str, Any]) -> Campaign:
    _reject_nonfinite(mapping, "campaign")
    value = _mapping(mapping, "campaign")
    _exact_fields(value, _CAMPAIGN_FIELDS, "campaign")
    _equal(value["contract_id"], CONTRACT_ID, "campaign contract_id")
    _identity(value["campaign_id"], "campaign campaign_id")
    _sha256(value["task_set_sha256"], "campaign task_set_sha256")
    _sha256(value["platform_toolchain_image_sha256"], "campaign platform_toolchain_image_sha256")
    _validate_host(value["host"])
    _validate_model(value["model"])
    arms = value["arms"]
    if not isinstance(arms, list) or len(arms) < 2:
        raise ValueError("campaign arms must contain at least two arms")
    seen: set[str] = set()
    for arm in arms:
        _validate_arm(arm)
        if arm["arm_id"] in seen:
            raise ValueError(f"duplicate campaign arm_id: {arm['arm_id']}")
        seen.add(arm["arm_id"])
    _positive_int(value["repetition_count"], "campaign repetition_count")
    if not isinstance(value["order_seed"], int) or isinstance(value["order_seed"], bool):
        raise ValueError("campaign order_seed must be an integer")
    if value["network_policy"] not in {"denied", "allowlist", "unrestricted"}:
        raise ValueError("campaign network_policy is unsupported")
    limits = _mapping(value["resource_limits"], "campaign resource_limits")
    _exact_fields(limits, {"max_parallel_runs", "memory_bytes"}, "campaign resource_limits")
    _positive_int(limits["max_parallel_runs"], "campaign resource_limits.max_parallel_runs")
    _positive_int(limits["memory_bytes"], "campaign resource_limits.memory_bytes")
    _positive_int(value["approved_total_run_count"], "campaign approved_total_run_count")
    expected_runs = len(arms) * value["repetition_count"]
    if value["approved_total_run_count"] < expected_runs:
        raise ValueError("campaign approved_total_run_count is below one run per arm and repetition")
    pricing = value["pricing"]
    ceiling = value["approved_money_ceiling"]
    if pricing is None:
        if ceiling is not None:
            raise ValueError("campaign approved_money_ceiling must be null without pricing")
    else:
        _validate_pricing(pricing)
        if not _positive_number(ceiling):
            raise ValueError("campaign approved_money_ceiling must be positive when pricing exists")
    return Campaign(
        value["campaign_id"],
        value["repetition_count"],
        value["approved_total_run_count"],
        _freeze(value),
    )


def validate_run_record(mapping: Mapping[str, Any]) -> RunRecord:
    _reject_nonfinite(mapping, "run record")
    value = _mapping(mapping, "run record")
    _exact_fields(value, _RUN_FIELDS, "run record")
    _equal(value["contract_id"], CONTRACT_ID, "run record contract_id")
    for field in ("run_id", "task_id"):
        _identity(value[field], f"run record {field}")
    _arm_id(value["arm_id"], "run record arm_id")
    for field in ("campaign_sha256", "verifier_evidence_sha256", "raw_event_sha256"):
        _sha256(value[field], f"run record {field}")
    for field in ("repetition", "order"):
        _positive_int(value[field], f"run record {field}")
    if value["outcome"] not in {"correct", "incorrect", "timeout", "product_error", "infrastructure_void", "unsupported"}:
        raise ValueError("run record outcome is unsupported")
    if not _nonnegative_number(value["wall_time_seconds"]):
        raise ValueError("run record wall_time_seconds must be nonnegative")
    counts = _mapping(value["native_tool_counts"], "run record native_tool_counts")
    for key, count in counts.items():
        _identity(key, "native tool name")
        _nonnegative_int(count, f"native tool count {key}")
    _nonnegative_int(value["miller_calls"], "run record miller_calls")
    tokens = [value[name] for name in ("total_model_input_tokens", "total_model_cached_tokens", "total_model_output_tokens")]
    if any(item is None for item in tokens) and not all(item is None for item in tokens):
        raise ValueError("run record token counts must all be null or all be measured")
    for item in tokens:
        if item is not None:
            _nonnegative_int(item, "run record token count")
    cost = value["price_derived_cost"]
    if cost is not None and not _nonnegative_number(cost):
        raise ValueError("run record price_derived_cost must be null or nonnegative")
    return RunRecord(value["outcome"], cost, _freeze(value))


def validate_verifier(mapping: Mapping[str, Any]) -> FrozenVerifier:
    _reject_nonfinite(mapping, "verifier")
    value = _mapping(mapping, "verifier")
    common = {"verifier_id", "kind", "expected_status"}
    _require_fields(value, {"verifier_id", "kind"}, "verifier")
    _identity(value["verifier_id"], "verifier verifier_id")
    kind = value["kind"]
    expected_status = value.get("expected_status", "answered")
    if expected_status not in {"answered", "empty", "refused"}:
        raise ValueError("verifier expected_status is unsupported")
    if kind in {"location", "references"}:
        allowed = common | {"locations"}
        _exact_fields(value, allowed, "verifier", optional={"expected_status"})
        locations = value["locations"]
        if not isinstance(locations, list):
            raise ValueError("verifier locations must be an array")
        if kind == "location" and expected_status != "answered":
            raise ValueError("location verifier supports answered results only")
        if expected_status == "answered" and not locations:
            raise ValueError("verifier locations must be non-empty for an answered result")
        if expected_status != "answered" and locations:
            raise ValueError("empty or refused reference verifiers cannot contain locations")
        for location in locations:
            _validate_location_label(location)
    elif kind == "concept":
        has_claims = "claims" in value
        has_facts = "facts" in value
        if has_claims == has_facts:
            raise ValueError("concept verifier must contain exactly one of claims or facts")
        _exact_fields(value, common | ({"claims"} if has_claims else {"facts"}), "verifier", optional={"expected_status"})
        if expected_status == "empty":
            raise ValueError("concept verifier uses refused rather than empty")
        records = value["claims"] if has_claims else value["facts"]
        if not isinstance(records, list) or not records:
            raise ValueError("verifier concept records must be non-empty")
        record_ids: set[str] = set()
        for record in records:
            record = _mapping(record, "concept record")
            if has_claims:
                _exact_fields(record, {"claim_id", "acceptable_alternatives", "evidence"}, "concept claim")
                record_id = record["claim_id"]
                alternatives = record["acceptable_alternatives"]
                if not isinstance(alternatives, list) or not alternatives:
                    raise ValueError("concept acceptable_alternatives must be non-empty")
                for alternative in alternatives:
                    if not isinstance(alternative, str) or not _normalize_statement(alternative):
                        raise ValueError("concept acceptable alternatives must be non-empty strings")
            else:
                if expected_status != "answered":
                    raise ValueError("fact concept verifier supports answered results only")
                _exact_fields(record, {"fact_id", "expected", "evidence"}, "concept fact")
                record_id = record["fact_id"]
                _fact_value(record["expected"], "concept fact expected")
            _identity(record_id, "concept record id")
            if record_id in record_ids:
                raise ValueError(f"duplicate concept record id: {record_id}")
            record_ids.add(record_id)
            evidence = record["evidence"]
            if not isinstance(evidence, list) or not evidence:
                raise ValueError("concept evidence must be non-empty")
            for location in evidence:
                _validate_location_label(location)
    elif kind == "test_selection":
        _exact_fields(value, common | {"test_cases"}, "verifier", optional={"expected_status"})
        cases = value["test_cases"]
        if not isinstance(cases, list):
            raise ValueError("verifier test_cases must be an array")
        if expected_status == "answered" and not cases:
            raise ValueError("verifier test_cases must be non-empty for an answered result")
        if expected_status != "answered" and cases:
            raise ValueError("empty or refused test selection verifiers cannot contain test_cases")
        seen_cases: set[tuple[str, str]] = set()
        for case in cases:
            case = _validate_test_case(case, "verifier test case")
            identity = (case["path"], case["test_id"])
            if identity in seen_cases:
                raise ValueError(f"duplicate verifier test case: {case['path']}:{case['test_id']}")
            seen_cases.add(identity)
    elif kind == "mutation":
        if expected_status != "answered":
            raise ValueError("mutation verifier supports answered results only")
        fields = common | {"expected_changed_paths", "acceptance_test_paths", "forbidden_public_paths", "required_source_fragments", "baseline_files", "test_argv"}
        _exact_fields(value, fields, "verifier", optional={"expected_status"})
        for field in ("expected_changed_paths", "acceptance_test_paths", "forbidden_public_paths"):
            paths = _string_list(value[field], f"verifier {field}", unique=True)
            for path in paths:
                _repo_path(path, f"verifier {field}")
        fragments = value["required_source_fragments"]
        if not isinstance(fragments, list) or not fragments:
            raise ValueError("verifier required_source_fragments must be non-empty")
        for fragment in fragments:
            _exact_fields(_mapping(fragment, "source fragment"), {"path", "text"}, "source fragment")
            _repo_path(fragment["path"], "source fragment path")
            if not isinstance(fragment["text"], str) or not fragment["text"]:
                raise ValueError("source fragment text must be non-empty")
        baseline = value["baseline_files"]
        if not isinstance(baseline, list) or not baseline:
            raise ValueError("verifier baseline_files must be non-empty")
        baseline_paths: set[str] = set()
        for item in baseline:
            item = _mapping(item, "baseline file")
            _exact_fields(
                item,
                {"path", "sha256", "link_target"},
                "baseline file",
                optional={"link_target"},
            )
            _repo_path(item["path"], "baseline file path")
            _sha256(item["sha256"], "baseline file sha256")
            if "link_target" in item:
                _relative_link_target(item["link_target"], "baseline link_target")
                expected = hashlib.sha256(os.fsencode(item["link_target"])).hexdigest()
                if item["sha256"] != expected:
                    raise ValueError("baseline link sha256 must hash link_target bytes")
            if item["path"] in baseline_paths:
                raise ValueError(f"duplicate baseline file path: {item['path']}")
            baseline_paths.add(item["path"])
        argv = _string_list(value["test_argv"], "verifier test_argv", unique=False)
        if not argv or any(not part for part in argv):
            raise ValueError("verifier test_argv must be non-empty")
        if Path(argv[0]).name.casefold() in {"codex", "claude", "curl", "wget"}:
            raise ValueError("verifier test_argv cannot invoke model or network clients")
    else:
        raise ValueError(f"verifier kind is unsupported: {kind}")
    return FrozenVerifier(value["verifier_id"], kind, _freeze(value))


def bind_verifier(task: OutcomeTask, verifier: FrozenVerifier) -> VerifiableTask:
    if task.verifier_id != verifier.verifier_id:
        raise ValueError("task verifier_id does not match frozen verifier")
    permitted = {
        "location": {"location"}, "concept": {"concept"}, "references": {"references"},
        "safe_edit": {"mutation"}, "repair": {"mutation"},
        "test_selection": {"test_selection"},
    }
    if verifier.kind not in permitted[task.workflow]:
        raise ValueError(f"verifier kind {verifier.kind} cannot grade workflow {task.workflow}")
    return VerifiableTask(task, verifier)


def verify_result(
    task: VerifiableTask,
    result: Mapping[str, Any],
    artifact_root: str | Path,
    *,
    executor: VerificationExecutor | None = None,
) -> Verification:
    try:
        _reject_nonfinite(result, "result")
    except ValueError as exc:
        return Verification(False, (str(exc),), {})
    root = Path(artifact_root)
    failures: list[str] = []
    evidence: dict[str, Any] = {"verifier_id": task.verifier.verifier_id, "kind": task.verifier.kind}
    if not root.is_absolute():
        failures.append("artifact_root must be absolute")
        return Verification(False, tuple(failures), evidence)
    try:
        root = root.resolve(strict=True)
    except OSError as exc:
        return Verification(False, (f"artifact_root is unavailable: {exc}",), evidence)
    if not root.is_dir():
        return Verification(False, ("artifact_root must be a directory",), evidence)
    kind = task.verifier.kind
    if kind != "mutation":
        try:
            observed_snapshot = source_snapshot_sha256(root)
        except ValueError as exc:
            return Verification(False, (str(exc),), evidence)
        evidence["source_snapshot_sha256"] = observed_snapshot
        if observed_snapshot != task.task.snapshot_sha256:
            return Verification(
                False,
                ("artifact source does not match task snapshot_sha256",),
                evidence,
            )
    if kind == "location":
        failures.extend(_verify_one_location(task.verifier.value["locations"], result, root))
    elif kind == "concept":
        if "facts" in task.verifier.value:
            failures.extend(_verify_concept_facts(task.verifier.value["facts"], result, root))
        else:
            failures.extend(
                _verify_concept(
                    task.verifier.value["claims"],
                    task.verifier.value.get("expected_status", "answered"),
                    result,
                    root,
                )
            )
    elif kind == "references":
        failures.extend(
            _verify_references(
                task.verifier.value["locations"],
                task.verifier.value.get("expected_status", "answered"),
                result,
                root,
            )
        )
    elif kind == "test_selection":
        failures.extend(
            _verify_test_selection(
                task.verifier.value["test_cases"],
                task.verifier.value.get("expected_status", "answered"),
                result,
                root,
            )
        )
    else:
        failures.extend(_verify_mutation(task, result, root, executor, evidence))
    return Verification(not failures, tuple(dict.fromkeys(failures)), evidence)


def _verify_one_location(labels: Sequence[Mapping[str, Any]], result: Mapping[str, Any], root: Path) -> list[str]:
    try:
        value = _mapping(result, "result")
        _exact_fields(value, {"path", "name", "signature", "line"}, "result", optional={"name", "signature"})
        name = value.get("name")
        signature = value.get("signature")
        if (name is None) == (signature is None):
            raise ValueError("result must contain exactly one of name or signature")
        if name is not None and (not isinstance(name, str) or not name):
            raise ValueError("result name must be null or a non-empty string")
        if signature is not None and (not isinstance(signature, str) or not signature):
            raise ValueError("result signature must be null or a non-empty string")
        _positive_int(value["line"], "result line")
        _safe_artifact_path(root, value["path"])
    except ValueError as exc:
        return [str(exc)]
    for label in labels:
        if value["path"] != label["path"]:
            continue
        identity_matches = value.get("name") == label.get("name") or value.get("signature") in label["signatures"]
        span_matches = any(span["line_start"] <= value["line"] <= span["line_end"] for span in label["spans"])
        if identity_matches and span_matches:
            return []
    return ["result location does not match frozen path, identity, and span"]


def _verify_references(labels, expected_status, result, root):
    try:
        value = _mapping(result, "result")
        _exact_fields(value, {"status", "references"}, "result", optional={"status"})
        references = value["references"]
        if not isinstance(references, list):
            raise ValueError("result references must be an array")
    except ValueError as exc:
        return [str(exc)]
    if value.get("status", "answered") != expected_status:
        return [f"result status must be {expected_status}"]
    if expected_status != "answered":
        return [] if not references else [f"{expected_status} result must not contain references"]
    unmatched = list(labels)
    failures: list[str] = []
    for reference in references:
        matched = next((label for label in unmatched if not _verify_one_location([label], reference, root)), None)
        if matched is None:
            failures.append("result contains an unexpected reference")
        else:
            unmatched.remove(matched)
    if unmatched:
        failures.append("result omits required references")
    return failures


def _verify_concept(claims, expected_status, result, root):
    try:
        value = _mapping(result, "result")
        _exact_fields(value, {"status", "claims", "evidence"}, "result", optional={"status"})
        submitted_claims = _string_list(value["claims"], "result claims", unique=True)
        if not submitted_claims:
            raise ValueError("result claims must be non-empty")
        evidence = value["evidence"]
        if not isinstance(evidence, list) or not evidence:
            raise ValueError("result evidence must be non-empty")
    except ValueError as exc:
        return [str(exc)]
    failures: list[str] = []
    if value.get("status", "answered") != expected_status:
        failures.append(f"result status must be {expected_status}")
    accepted = {
        _normalize_statement(alternative)
        for claim in claims
        for alternative in claim["acceptable_alternatives"]
    }
    submitted = {_normalize_statement(claim) for claim in submitted_claims}
    for statement in sorted(submitted - accepted):
        failures.append(f"concept claim is not a frozen acceptable alternative: {statement}")
    for claim in claims:
        if not any(
            _normalize_statement(alternative) in submitted
            for alternative in claim["acceptable_alternatives"]
        ):
            failures.append(f"concept claim is not satisfied: {claim['claim_id']}")
            continue
        if not any(
            any(not _verify_one_location([label], submitted, root) for label in claim["evidence"])
            for submitted in evidence
        ):
            failures.append(f"concept claim lacks frozen evidence: {claim['claim_id']}")
    all_labels = [label for claim in claims for label in claim["evidence"]]
    for submitted_evidence in evidence:
        if not any(
            not _verify_one_location([label], submitted_evidence, root)
            for label in all_labels
        ):
            failures.append("concept result contains evidence outside the frozen labels")
    return failures


def _verify_concept_facts(facts, result, root):
    try:
        value = _mapping(result, "result")
        _exact_fields(value, {"facts", "evidence"}, "result")
        submitted = _mapping(value["facts"], "result facts")
        for fact_id, fact_value in submitted.items():
            _identity(fact_id, "result fact id")
            _fact_value(fact_value, f"result fact {fact_id}")
        evidence = value["evidence"]
        if not isinstance(evidence, list) or not evidence:
            raise ValueError("result evidence must be non-empty")
    except ValueError as exc:
        return [str(exc)]
    expected = {fact["fact_id"]: _thaw(fact["expected"]) for fact in facts}
    failures = []
    for fact_id in sorted(set(submitted) - set(expected)):
        failures.append(f"result contains unknown concept fact: {fact_id}")
    for fact_id in sorted(set(expected) - set(submitted)):
        failures.append(f"result omits concept fact: {fact_id}")
    for fact in facts:
        fact_id = fact["fact_id"]
        if fact_id in submitted and not _fact_values_equal(submitted[fact_id], expected[fact_id]):
            failures.append(f"concept fact is incorrect: {fact_id}")
        if fact_id in submitted and not any(
            any(not _verify_one_location([label], item, root) for label in fact["evidence"])
            for item in evidence
        ):
            failures.append(f"concept fact lacks frozen evidence: {fact_id}")
    all_labels = [label for fact in facts for label in fact["evidence"]]
    for item in evidence:
        if not any(not _verify_one_location([label], item, root) for label in all_labels):
            failures.append("concept result contains evidence outside the frozen labels")
    return failures


def public_response_schema(task: VerifiableTask) -> Mapping[str, Any]:
    location = {
        "type": "object",
        "additionalProperties": False,
        "required": ["path", "line", "name", "signature"],
        "properties": {
            "path": {"type": "string", "description": "Repository-relative source path."},
            "line": {"type": "integer", "minimum": 1},
            "name": {"type": ["string", "null"], "description": "Native symbol name, or null when signature identifies the location."},
            "signature": {"type": ["string", "null"], "description": "Native signature, or null when name identifies the location."},
        },
    }
    workflow = task.task.workflow
    if workflow == "location":
        return location
    if workflow == "concept":
        if "facts" not in task.verifier.value:
            return {
                "type": "object",
                "additionalProperties": False,
                "required": ["status", "claims", "evidence"],
                "properties": {
                    "status": {"type": "string", "enum": ["answered", "refused"]},
                    "claims": {"type": "array", "minItems": 1, "items": {"type": "string"}},
                    "evidence": {"type": "array", "minItems": 1, "items": location},
                },
            }
        fact_properties = {}
        for fact in sorted(task.verifier.value["facts"], key=lambda item: item["fact_id"]):
            expected = fact["expected"]
            if isinstance(expected, bool):
                value_schema = {"type": "boolean"}
            elif isinstance(expected, str):
                value_schema = {"type": "string"}
            else:
                value_schema = {"type": "array", "items": {"type": "string"}}
            fact_properties[fact["fact_id"]] = value_schema
        return {
            "type": "object",
            "additionalProperties": False,
            "required": ["facts", "evidence"],
            "properties": {
                "facts": {
                    "type": "object",
                    "description": "Values for every behavior facet named in the task prompt. String arrays are unique unordered sets.",
                    "additionalProperties": False,
                    "required": list(fact_properties),
                    "properties": fact_properties,
                },
                "evidence": {"type": "array", "minItems": 1, "items": location},
            },
        }
    if workflow == "references":
        return {"type": "object", "additionalProperties": False, "required": ["status", "references"], "properties": {"status": {"type": "string", "enum": ["answered", "empty", "refused"]}, "references": {"type": "array", "items": location}}}
    if workflow == "test_selection":
        return {
            "type": "object",
            "additionalProperties": False,
            "required": ["status", "tests"],
            "properties": {
                "status": {"type": "string", "enum": ["answered", "empty", "refused"]},
                "tests": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "additionalProperties": False,
                        "required": ["path", "test_id"],
                        "properties": {
                            "path": {"type": "string"},
                            "test_id": {"type": "string"},
                        },
                    },
                },
            },
        }
    return {"type": "object", "additionalProperties": False, "required": [], "properties": {}}


def _verify_test_selection(labels, expected_status, result, root):
    try:
        value = _mapping(result, "result")
        _exact_fields(value, {"status", "tests"}, "result", optional={"status"})
        cases = value["tests"]
        if not isinstance(cases, list):
            raise ValueError("result tests must be an array")
        identities: list[tuple[str, str]] = []
        for case in cases:
            case = _validate_test_case(case, "result test case")
            _safe_artifact_path(root, case["path"])
            identities.append((case["path"], case["test_id"]))
        if len(identities) != len(set(identities)):
            raise ValueError("result tests contain duplicate cases")
    except ValueError as exc:
        return [str(exc)]
    if value.get("status", "answered") != expected_status:
        return [f"result status must be {expected_status}"]
    if expected_status != "answered":
        return [] if not identities else [f"{expected_status} result must not contain tests"]
    expected = {(case["path"], case["test_id"]) for case in labels}
    return [] if set(identities) == expected else ["result test selection does not match frozen cases"]


def _verify_mutation(task, result, root, executor, evidence):
    failures: list[str] = []
    try:
        _exact_fields(_mapping(result, "result"), set(), "result")
        baseline_entries = _thaw(task.verifier.value["baseline_files"])
        baseline = {item["path"]: item for item in baseline_entries}
        digest = _inventory_sha256(baseline_entries)
        if digest != task.task.snapshot_sha256:
            raise ValueError("frozen baseline inventory does not match task snapshot_sha256")
        current = {item["path"]: item for item in source_inventory(root)}
        changed = {path for path in baseline | current if baseline.get(path) != current.get(path)}
        deleted = set(baseline) - set(current)
    except ValueError as exc:
        return [str(exc)]
    expected = set(task.verifier.value["expected_changed_paths"])
    allowed = set(task.task.allowed_write_paths)
    for path in sorted(changed - expected):
        failures.append(f"unexpected changed path: {path}")
    for path in sorted(expected - changed):
        failures.append(f"missing changed path: {path}")
    for path in sorted(changed - allowed):
        failures.append(f"changed path is outside allowed_write_paths: {path}")
    for path in task.verifier.value["acceptance_test_paths"]:
        if path in deleted:
            failures.append(f"acceptance test was deleted: {path}")
    for path in task.verifier.value["forbidden_public_paths"]:
        if path in changed:
            failures.append(f"public behavior path changed: {path}")
    for fragment in task.verifier.value["required_source_fragments"]:
        try:
            source = _safe_artifact_path(root, fragment["path"]).read_text(encoding="utf-8")
        except (OSError, UnicodeError, ValueError):
            failures.append(f"required reference is unreadable: {fragment['path']}")
            continue
        if fragment["text"] not in source:
            failures.append(f"required reference is missing: {fragment['path']}")
    if failures:
        return failures
    if executor is None:
        return ["isolated verification executor is required"]
    with tempfile.TemporaryDirectory(prefix="agent-outcomes-verify-") as directory:
        candidate = Path(directory) / "candidate"
        shutil.copytree(root, candidate, symlinks=True, ignore=shutil.ignore_patterns(".git"))
        execution = executor.execute(task.verifier.value["test_argv"], candidate, task.task.max_wall_seconds)
        evidence.update({"test_ran": execution.ran, "test_returncode": execution.returncode})
    if not execution.ran:
        failures.append("frozen test command did not run")
    elif execution.returncode != 0:
        failures.append(f"frozen test command failed with exit {execution.returncode}")
    return failures


def source_inventory(root: str | Path) -> tuple[Mapping[str, Any], ...]:
    path = Path(root)
    if not path.is_absolute():
        raise ValueError("snapshot root must be absolute")
    try:
        resolved = path.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise ValueError(f"snapshot root is unavailable: {exc}") from exc
    if not resolved.is_dir():
        raise ValueError("snapshot root must be a directory")
    entries: list[dict[str, Any]] = []
    for directory, names, files in os.walk(resolved, followlinks=False):
        directory_path = Path(directory)
        names.sort()
        files.sort()
        if ".git" in names:
            git_path = directory_path / ".git"
            if git_path.is_symlink():
                raise ValueError("snapshot .git entry cannot be a symlink")
            names.remove(".git")
        if ".git" in files:
            files.remove(".git")
        for name in tuple(names):
            candidate = directory_path / name
            if candidate.is_symlink():
                entries.append(_link_inventory_entry(resolved, candidate))
                names.remove(name)
        for name in files:
            candidate = directory_path / name
            if candidate.is_symlink():
                entries.append(_link_inventory_entry(resolved, candidate))
                continue
            relative = candidate.relative_to(resolved).as_posix()
            _repo_path(relative, "snapshot file path")
            if not stat.S_ISREG(candidate.stat(follow_symlinks=False).st_mode):
                raise ValueError(f"snapshot contains a non-regular file: {relative}")
            entries.append(
                {"path": relative, "sha256": hashlib.sha256(candidate.read_bytes()).hexdigest()}
            )
    entries.sort(key=lambda item: item["path"])
    return tuple(_freeze(entry) for entry in entries)


def _link_inventory_entry(root: Path, link: Path) -> dict[str, str]:
    relative = link.relative_to(root).as_posix()
    _repo_path(relative, "snapshot link path")
    target = os.readlink(link)
    _relative_link_target(target, f"snapshot link target for {relative}")
    try:
        resolved_target = link.resolve(strict=True)
        target_relative = resolved_target.relative_to(root)
    except (OSError, RuntimeError, ValueError) as exc:
        raise ValueError(f"snapshot link is dangling, cyclic, or escaping: {relative}") from exc
    if ".git" in target_relative.parts:
        raise ValueError(f"snapshot link targets excluded .git content: {relative}")
    return {
        "path": relative,
        "sha256": hashlib.sha256(os.fsencode(target)).hexdigest(),
        "link_target": target,
    }


def source_snapshot_sha256(root: str | Path) -> str:
    return _inventory_sha256(source_inventory(root))


def _inventory_sha256(inventory: Sequence[Mapping[str, Any]]) -> str:
    canonical = sorted((_thaw(item) for item in inventory), key=lambda item: item["path"])
    return hashlib.sha256(
        json.dumps(canonical, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()


def _validate_location_label(value):
    location = _mapping(value, "location label")
    _exact_fields(location, {"path", "name", "signatures", "spans"}, "location label")
    _repo_path(location["path"], "location path")
    if not isinstance(location["name"], str) or not location["name"]:
        raise ValueError("location name must be non-empty")
    signatures = _string_list(location["signatures"], "location signatures", unique=True)
    if not signatures:
        raise ValueError("location signatures must be non-empty")
    spans = location["spans"]
    if not isinstance(spans, list) or not spans:
        raise ValueError("location spans must be non-empty")
    for span in spans:
        _exact_fields(_mapping(span, "location span"), {"line_start", "line_end"}, "location span")
        _positive_int(span["line_start"], "location line_start")
        _positive_int(span["line_end"], "location line_end")
        if span["line_end"] < span["line_start"]:
            raise ValueError("location line_end must be at least line_start")


def _validate_test_case(value, label):
    case = _mapping(value, label)
    _exact_fields(case, {"path", "test_id"}, label)
    _repo_path(case["path"], f"{label} path")
    _opaque_id(case["test_id"], f"{label} test_id")
    return case


def _validate_host(value):
    host = _mapping(value, "campaign host")
    _exact_fields(host, {"name", "version", "binary_sha256"}, "campaign host")
    _identity(host["name"], "campaign host name")
    if not isinstance(host["version"], str) or not host["version"]:
        raise ValueError("campaign host version must be non-empty")
    _sha256(host["binary_sha256"], "campaign host binary_sha256")


def _validate_model(value):
    model = _mapping(value, "campaign model")
    _exact_fields(model, {"model_id", "reasoning"}, "campaign model")
    if (
        not isinstance(model["model_id"], str)
        or not model["model_id"].strip()
        or len(model["model_id"]) > 512
        or any(ord(character) < 32 for character in model["model_id"])
    ):
        raise ValueError("campaign model_id must be a non-empty opaque identifier")
    if not isinstance(model["reasoning"], str) or not model["reasoning"]:
        raise ValueError("campaign reasoning must be non-empty")


def _validate_arm(value):
    arm = _mapping(value, "campaign arm")
    _exact_fields(arm, {"arm_id", "runtime_identity", "runtime_qualification_sha256"}, "campaign arm")
    _arm_id(arm["arm_id"], "campaign arm_id")
    semantic = arm["arm_id"] == "native+miller-semantic"
    if semantic:
        if arm["runtime_identity"] is None:
            raise ValueError("semantic arm requires runtime_identity")
        _validate_runtime_identity(arm["runtime_identity"])
        _sha256(arm["runtime_qualification_sha256"], "semantic arm runtime_qualification_sha256")
    elif arm["runtime_identity"] is not None or arm["runtime_qualification_sha256"] is not None:
        raise ValueError("native and lexical arms require null runtime qualification fields")


def _validate_runtime_identity(value):
    fields = {"sidecar_commit", "binary_sha256", "runtime_payload_sha256", "model_id", "model_sha256", "model_manifest_sha256", "miller_fixture_commit", "resolved_backend", "process_mode", "served_dimensions", "conformance_harness_sha256", "throughput_harness_sha256", "concurrency_harness_sha256"}
    identity = _mapping(value, "runtime_identity")
    _exact_fields(identity, fields, "runtime_identity")
    for field in ("sidecar_commit", "miller_fixture_commit"):
        if not isinstance(identity[field], str) or not _COMMIT.fullmatch(identity[field]):
            raise ValueError(f"runtime_identity {field} must be a full lowercase Git object id")
    for field in (
        "binary_sha256", "runtime_payload_sha256", "model_sha256",
        "model_manifest_sha256", "conformance_harness_sha256",
        "throughput_harness_sha256", "concurrency_harness_sha256",
    ):
        _sha256(identity[field], f"runtime_identity {field}")
    for field in ("model_id", "resolved_backend"):
        if not isinstance(identity[field], str) or not identity[field]:
            raise ValueError(f"runtime_identity {field} must be non-empty")
    if identity["process_mode"] not in {"stdio", "broker"}:
        raise ValueError("runtime_identity process_mode must be stdio or broker")
    _positive_int(identity["served_dimensions"], "runtime_identity served_dimensions")


def _validate_pricing(value):
    pricing = _mapping(value, "campaign pricing")
    fields = {"currency", "input_per_million", "cached_input_per_million", "output_per_million"}
    _exact_fields(pricing, fields, "campaign pricing")
    if not isinstance(pricing["currency"], str) or not pricing["currency"]:
        raise ValueError("campaign pricing currency must be non-empty")
    for field in fields - {"currency"}:
        if not _nonnegative_number(pricing[field]):
            raise ValueError(f"campaign pricing {field} must be nonnegative")


def _safe_artifact_path(root: Path, value: Any) -> Path:
    path = _repo_path(value, "result path")
    try:
        candidate = (root / path).resolve(strict=True)
    except OSError as exc:
        raise ValueError(f"result path is unavailable: {path}") from exc
    if os.path.commonpath((root, candidate)) != str(root):
        raise ValueError(f"result path escapes artifact_root: {path}")
    if not candidate.is_file():
        raise ValueError(f"result path is not a file: {path}")
    return candidate


def _repo_path(value: Any, label: str) -> str:
    if (
        not isinstance(value, str)
        or not value
        or len(value) > 1024
        or "\\" in value
        or "\0" in value
    ):
        raise ValueError(f"{label} must be a repository-relative POSIX path")
    path = PurePosixPath(value)
    if (
        path.is_absolute()
        or path.as_posix() != value
        or ":" in path.parts[0]
        or any(part in {"", ".", ".."} for part in path.parts)
    ):
        raise ValueError(f"{label} must be a safe repository-relative path: {value}")
    return value


def _relative_link_target(value: Any, label: str) -> str:
    if (
        not isinstance(value, str)
        or not value
        or "\0" in value
        or Path(value).is_absolute()
        or PureWindowsPath(value).is_absolute()
    ):
        raise ValueError(f"{label} must be a non-empty relative path")
    return value


def _mapping(value, label):
    if not isinstance(value, Mapping):
        raise ValueError(f"{label} must be an object")
    if any(not isinstance(key, str) for key in value):
        raise ValueError(f"{label} keys must be strings")
    return value


def _exact_fields(value, allowed, label, optional=frozenset()):
    unknown = set(value) - allowed
    if unknown:
        raise ValueError(f"unknown field in {label}: {sorted(unknown)[0]}")
    _require_fields(value, allowed - set(optional), label)


def _require_fields(value, required, label):
    missing = required - set(value)
    if missing:
        raise ValueError(f"missing field in {label}: {sorted(missing)[0]}")


def _identity(value, label):
    if not isinstance(value, str) or not _IDENTIFIER.fullmatch(value):
        raise ValueError(f"{label} is malformed")


def _arm_id(value, label):
    if value not in {
        "native",
        "native+miller-lexical",
        "native+miller-semantic",
    }:
        raise ValueError(f"{label} is unsupported")


def _opaque_id(value, label):
    if (
        not isinstance(value, str)
        or not value.strip()
        or len(value) > 1024
        or any(ord(character) < 32 for character in value)
    ):
        raise ValueError(f"{label} must be a non-empty opaque identifier")


def _sha256(value, label):
    if not isinstance(value, str) or not _HEX_256.fullmatch(value):
        raise ValueError(f"{label} must be 64 lowercase hexadecimal characters")


def _equal(value, expected, label):
    if value != expected:
        raise ValueError(f"{label} must be {expected}")


def _string_list(value, label, unique):
    if not isinstance(value, list) or any(not isinstance(item, str) for item in value):
        raise ValueError(f"{label} must be an array of strings")
    if unique and len(set(value)) != len(value):
        raise ValueError(f"{label} contains duplicates")
    return value


def _fact_value(value, label):
    if isinstance(value, bool):
        return value
    if isinstance(value, str) and value:
        return value
    if isinstance(value, list) and all(isinstance(item, str) and item for item in value) and len(value) == len(set(value)):
        return value
    raise ValueError(f"{label} must be a boolean, non-empty string, or unique string array")


def _fact_values_equal(left, right):
    if isinstance(left, list) and isinstance(right, list):
        return set(left) == set(right)
    return left == right


def _positive_int(value, label):
    if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
        raise ValueError(f"{label} must be a positive integer")


def _nonnegative_int(value, label):
    if not isinstance(value, int) or isinstance(value, bool) or value < 0:
        raise ValueError(f"{label} must be a nonnegative integer")


def _positive_number(value):
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(value)
        and value > 0
    )


def _nonnegative_number(value):
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(value)
        and value >= 0
    )


def _unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"duplicate JSON object key: {key}")
        value[key] = item
    return value


def _reject_json_constant(value):
    raise ValueError(f"non-finite JSON number is forbidden: {value}")


def _reject_nonfinite(value, label):
    if isinstance(value, float) and not math.isfinite(value):
        raise ValueError(f"{label} contains a non-finite number")
    if isinstance(value, Mapping):
        for item in value.values():
            _reject_nonfinite(item, label)
    elif isinstance(value, (list, tuple)):
        for item in value:
            _reject_nonfinite(item, label)


def _freeze(value):
    if isinstance(value, Mapping):
        return MappingProxyType({key: _freeze(item) for key, item in value.items()})
    if isinstance(value, list):
        return tuple(_freeze(item) for item in value)
    return value


def _thaw(value):
    if isinstance(value, Mapping):
        return {key: _thaw(item) for key, item in value.items()}
    if isinstance(value, tuple):
        return [_thaw(item) for item in value]
    return value


def _normalize_statement(value: str) -> str:
    return " ".join(value.casefold().strip().rstrip(".").split())
