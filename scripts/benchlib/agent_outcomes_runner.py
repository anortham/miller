from __future__ import annotations

import hashlib
import json
import os
import re
import secrets
import shlex
import shutil
import signal
import subprocess
import tempfile
import time
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from types import MappingProxyType
from typing import Protocol
from urllib.parse import urlsplit

from .agent_outcomes_contract import (
    Campaign,
    OutcomeTask,
    VerifiableTask,
    VerificationExecution,
    VerificationExecutor,
    public_response_schema,
    source_snapshot_sha256,
    validate_run_record,
    verify_result,
)

_SUPPORTED_HOST = ("codex-cli", "0.153.4")
_ANSWER_WORKFLOWS = {"location", "concept", "references", "test_selection"}
_EVENT_TYPES = {
    "thread.started",
    "turn.started",
    "turn.completed",
    "turn.failed",
    "item.started",
    "item.updated",
    "item.completed",
    "error",
}
_ID = re.compile(r"^[a-z][a-z0-9-]{0,127}$")
_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_PODMAN_DEFAULT_CAPABILITIES = {
    "CAP_CHOWN",
    "CAP_DAC_OVERRIDE",
    "CAP_FOWNER",
    "CAP_FSETID",
    "CAP_KILL",
    "CAP_NET_BIND_SERVICE",
    "CAP_SETFCAP",
    "CAP_SETGID",
    "CAP_SETPCAP",
    "CAP_SETUID",
    "CAP_SYS_CHROOT",
}
_PREPARED_ENVIRONMENT_NAMES = {
    "BUNDLE_FROZEN",
    "BUNDLE_PATH",
    "CARGO_HOME",
    "CARGO_NET_OFFLINE",
    "CARGO_TARGET_DIR",
    "GOCACHE",
    "GOMODCACHE",
    "GOPROXY",
    "GOSUMDB",
    "NUGET_PACKAGES",
    "NODE_PATH",
    "PATH",
    "PYTHONPATH",
    "DOTNET_ROLL_FORWARD",
    "RestoreIgnoreFailedSources",
    "UV_CACHE_DIR",
    "UV_OFFLINE",
    "UV_PROJECT_ENVIRONMENT",
    "npm_config_cache",
    "npm_config_offline",
}
_VERIFY_PREPARED_SCRIPT = r"""
import hashlib,json,os,stat,sys
root="/opt/agent-deps"
data=open(root+"/manifest.json","rb").read()
manifest=json.loads(data)
for repository in manifest["repositories"]:
    repo_root=root+"/"+repository["repo_id"]
    expected=set()
    for artifact in repository["artifacts"]:
        path=repo_root+"/"+artifact["path"]
        mode=os.lstat(path).st_mode
        if artifact["kind"]=="symlink":
            if not stat.S_ISLNK(mode): raise SystemExit(21)
            value=os.fsencode(os.readlink(path))
        else:
            if not stat.S_ISREG(mode): raise SystemExit(22)
            value=open(path,"rb").read()
        if len(value)!=artifact["size_bytes"] or hashlib.sha256(value).hexdigest()!=artifact["sha256"]: raise SystemExit(23)
        expected.add(artifact["path"])
    actual=set()
    for directory,_,files in os.walk(repo_root,followlinks=False):
        for name in files:
            actual.add(os.path.relpath(os.path.join(directory,name),repo_root))
        for name in list(os.listdir(directory)):
            path=os.path.join(directory,name)
            if os.path.islink(path): actual.add(os.path.relpath(path,repo_root))
    if actual!=expected: raise SystemExit(24)
sys.stdout.write(hashlib.sha256(data).hexdigest())
""".strip()
_MATERIALIZE_PREPARED_SCRIPT = r"""
import json,os,shutil,sys
repo=sys.argv[1]
root="/opt/agent-deps"
manifest=json.load(open(root+"/manifest.json"))
record=next((item for item in manifest["repositories"] if item["repo_id"]==repo),None)
if record is None: raise SystemExit(31)
for mount in record["workspace_mounts"]:
    source=os.path.join(root,repo,mount["seed_path"])
    target=os.path.join("/runtime/prepared-workspace",mount["path"])
    os.makedirs(os.path.dirname(target),exist_ok=True)
    if os.path.isdir(source) and not os.path.islink(source): shutil.copytree(source,target,symlinks=True)
    else: shutil.copy2(source,target,follow_symlinks=False)
""".strip()


@dataclass(frozen=True)
class ParsedAgentEvents:
    native_tool_counts: Mapping[str, int]
    miller_calls: int
    model_tokens: tuple[int | None, int | None, int | None]
    answer: str | None
    unsupported_reason: str | None
    raw_lines: tuple[str, ...]
    reasoning_output_tokens: int | None = None
    failed: bool = False


@dataclass(frozen=True)
class RunnerQualification:
    host_name: str
    host_version: str
    host_binary_sha256: str
    image_reference: str
    network_policy: str
    environment_allowlist: tuple[str, ...]
    auth_mounted: bool
    configuration_sha256: str
    experiment_root_sha256: str | None
    workspace_mode: str | None
    arm_id: str | None
    prepared_repo_id: str | None
    passed: bool
    kind: str

    def __post_init__(self) -> None:
        if not _SHA256.fullmatch(self.host_binary_sha256) or not _SHA256.fullmatch(
            self.configuration_sha256
        ):
            raise ValueError("runner qualification digest is invalid")
        if self.experiment_root_sha256 is not None and not _SHA256.fullmatch(
            self.experiment_root_sha256
        ):
            raise ValueError("runner qualification experiment root digest is invalid")
        if self.workspace_mode not in {None, "ro", "rw"}:
            raise ValueError("runner qualification workspace mode is invalid")
        if self.prepared_repo_id is not None and not _ID.fullmatch(
            self.prepared_repo_id
        ):
            raise ValueError("runner qualification prepared repository is invalid")
        if self.kind not in {"fake", "os"}:
            raise ValueError("runner qualification kind is invalid")
        if self.network_policy not in {"denied", "unrestricted"} or self.auth_mounted:
            raise ValueError(
                "runner qualification cannot mount auth or substitute network policy"
            )

    @classmethod
    def fake(
        cls,
        runner: NativeAgentRunner,
        experiment_root: Path | None = None,
        *,
        mutation: bool | None = None,
        arm_id: str | None = None,
        prepared_repo_id: str | None = None,
    ) -> RunnerQualification:
        workspace_mode = None if mutation is None else ("rw" if mutation else "ro")
        value = cls(
            runner.host_name,
            runner.host_version,
            runner.host_binary_sha256,
            runner.image_reference,
            "denied",
            (),
            False,
            runner.qualification_configuration_sha256(
                experiment_root,
                workspace_mode,
                arm_id,
                prepared_repo_id,
            ),
            None
            if experiment_root is None
            else _path_sha256(experiment_root.resolve()),
            workspace_mode,
            arm_id,
            prepared_repo_id,
            True,
            "fake",
        )
        return value


@dataclass(frozen=True)
class IsolationProbeResult:
    passed: bool
    argv: tuple[str, ...]
    returncode: int
    stdout: str
    stderr: str
    qualification_sha256: str | None = None
    inspect_json: str = ""
    qualification: RunnerQualification | None = None
    evidence_path: str | None = None
    prepared_setup: Mapping[str, object] | None = None


@dataclass(frozen=True)
class ZeroWorkObservation:
    process_names: tuple[str, ...]
    accessed_paths: tuple[str, ...]


class RuntimeObserver(Protocol):
    def snapshot(self) -> ZeroWorkObservation: ...


class UnsafeLiveExecution(RuntimeError):
    pass


@dataclass(frozen=True)
class ProviderTransport:
    provider_id: str
    base_url: str
    qualification_sha256: str
    network_policy: str

    def __post_init__(self) -> None:
        if not _ID.fullmatch(self.provider_id):
            raise ValueError("provider transport id is invalid")
        if not _SHA256.fullmatch(self.qualification_sha256):
            raise ValueError("provider transport qualification digest is invalid")
        parsed = urlsplit(self.base_url)
        loopback = parsed.hostname in {"127.0.0.1", "::1", "localhost"}
        if parsed.scheme != "https" and not (parsed.scheme == "http" and loopback):
            raise ValueError("provider transport URL must use HTTPS or loopback HTTP")
        if (
            not parsed.hostname
            or parsed.username
            or parsed.password
            or parsed.query
            or parsed.fragment
            or any(
                ord(character) <= 32 or character in {'"', "'", "\\"}
                for character in self.base_url
            )
        ):
            raise ValueError(
                "provider transport URL cannot contain credentials, query, or fragment"
            )
        if self.network_policy not in {"denied", "unrestricted"}:
            raise ValueError("provider transport network policy is not enforceable")


@dataclass(frozen=True)
class SemanticRuntimeBinding:
    binding_path: Path
    binding_sha256: str
    image_reference: str
    runtime_identity: Mapping[str, object]
    runtime_qualification_sha256: str
    observation_evidence_sha256: str

    @classmethod
    def from_manifest(cls, path: Path, image_reference: str) -> SemanticRuntimeBinding:
        binding_path = path.resolve(strict=True)
        raw = binding_path.read_bytes()
        if len(raw) > 1024 * 1024:
            raise ValueError("semantic runtime binding exceeds 1 MiB")
        try:
            value = json.loads(
                raw,
                object_pairs_hook=_unique_json_object,
                parse_constant=lambda constant: (_ for _ in ()).throw(
                    ValueError(constant)
                ),
            )
        except (json.JSONDecodeError, ValueError) as exc:
            raise ValueError("semantic runtime binding is invalid JSON") from exc
        fields = {
            "schema",
            "image_digest",
            "runtime_identity",
            "runtime_qualification_sha256",
            "observation_evidence_sha256",
            "passed",
        }
        if not isinstance(value, dict) or set(value) != fields:
            raise ValueError("semantic runtime binding fields are invalid")
        image_digest = image_reference.rsplit("@sha256:", 1)
        identity = value["runtime_identity"]
        identity_fields = {
            "sidecar_commit",
            "binary_sha256",
            "runtime_payload_sha256",
            "model_id",
            "model_sha256",
            "model_manifest_sha256",
            "miller_fixture_commit",
            "resolved_backend",
            "process_mode",
            "served_dimensions",
            "conformance_harness_sha256",
            "throughput_harness_sha256",
            "concurrency_harness_sha256",
        }
        if (
            len(image_digest) != 2
            or not _SHA256.fullmatch(image_digest[1])
            or value["schema"] != "agent-outcomes-semantic-image-binding-v1"
            or value["passed"] is not True
            or value["image_digest"] != image_digest[1]
            or not isinstance(identity, dict)
            or set(identity) != identity_fields
            or identity["process_mode"] not in {"stdio", "broker"}
            or not isinstance(identity["served_dimensions"], int)
            or isinstance(identity["served_dimensions"], bool)
            or identity["served_dimensions"] <= 0
            or any(
                not isinstance(identity[name], str) or not identity[name]
                for name in identity_fields - {"served_dimensions"}
            )
            or any(
                not _SHA256.fullmatch(identity[name])
                for name in (
                    "binary_sha256",
                    "runtime_payload_sha256",
                    "model_sha256",
                    "model_manifest_sha256",
                    "conformance_harness_sha256",
                    "throughput_harness_sha256",
                    "concurrency_harness_sha256",
                )
            )
            or not _SHA256.fullmatch(value["runtime_qualification_sha256"])
            or not _SHA256.fullmatch(value["observation_evidence_sha256"])
        ):
            raise ValueError(
                "semantic runtime binding does not match an immutable image runtime"
            )
        return cls(
            binding_path,
            hashlib.sha256(raw).hexdigest(),
            image_reference,
            MappingProxyType(dict(identity)),
            value["runtime_qualification_sha256"],
            value["observation_evidence_sha256"],
        )

    def verify_image(self, podman_path: str) -> Mapping[str, object]:
        script = (
            "import hashlib,json,pathlib;"
            "root=pathlib.Path('/opt/miller/.tools/julie-semantic-sidecar-runtime');"
            "obs=pathlib.Path('/opt/miller-semantic/runtime-observation.json');"
            "model=pathlib.Path('/opt/miller-semantic/model.bin');"
            "files=[{'path':str(p.relative_to(root)).replace('\\\\','/'),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'size':p.stat().st_size} for p in sorted(root.rglob('*')) if p.is_file()];"
            "print(json.dumps({'binary_sha256':hashlib.sha256((root/'julie-semantic-sidecar').read_bytes()).hexdigest(),'runtime_payload_sha256':hashlib.sha256(json.dumps(files,sort_keys=True,separators=(',',':')).encode()).hexdigest(),'model_sha256':hashlib.sha256(model.read_bytes()).hexdigest(),'observation_evidence_sha256':hashlib.sha256(obs.read_bytes()).hexdigest(),'observation':json.loads(obs.read_text())},sort_keys=True,separators=(',',':')))"
        )
        completed = subprocess.run(
            [
                podman_path,
                "run",
                "--rm",
                "--network=none",
                self.image_reference,
                "python3",
                "-B",
                "-c",
                script,
            ],
            capture_output=True,
            check=False,
            timeout=60,
            env={"PATH": os.defpath},
        )
        if completed.returncode != 0 or len(completed.stdout) > 1024 * 1024:
            raise UnsafeLiveExecution("semantic runtime image observation failed")
        try:
            observed = json.loads(
                completed.stdout, object_pairs_hook=_unique_json_object
            )
        except (json.JSONDecodeError, ValueError) as exc:
            raise UnsafeLiveExecution(
                "semantic runtime image observation is invalid"
            ) from exc
        identity = self.runtime_identity
        observation = (
            observed.get("observation") if isinstance(observed, dict) else None
        )
        expected_observation = {
            "schema": "agent-outcomes-semantic-runtime-observation-v1",
            "image_digest": self.image_reference.rsplit("@sha256:", 1)[1],
            "runtime_identity": dict(identity),
            "passed": True,
        }
        if (
            not isinstance(observed, dict)
            or observed.get("binary_sha256") != identity["binary_sha256"]
            or observed.get("runtime_payload_sha256")
            != identity["runtime_payload_sha256"]
            or observed.get("model_sha256") != identity["model_sha256"]
            or observed.get("observation_evidence_sha256")
            != self.observation_evidence_sha256
            or observation != expected_observation
        ):
            raise UnsafeLiveExecution(
                "semantic runtime image bytes or execution mode differ from binding"
            )
        return MappingProxyType(dict(observed))


@dataclass(frozen=True)
class PreparedRepositoryEnvironment:
    repo_id: str
    environment: Mapping[str, str]
    workspace_mounts: tuple[Mapping[str, str], ...]


@dataclass(frozen=True)
class PreparedEnvironment:
    manifest_path: Path
    manifest_sha256: str
    binding_sha256: str
    image_reference: str
    repositories: Mapping[str, PreparedRepositoryEnvironment]
    manifest_bytes: bytes
    _verified_images: set[str]

    @classmethod
    def from_manifest(cls, path: Path, image_reference: str) -> PreparedEnvironment:
        manifest_path = path.resolve(strict=True)
        binding_bytes = manifest_path.read_bytes()
        if len(binding_bytes) > 64 * 1024 * 1024:
            raise ValueError("prepared environment manifest exceeds 64 MiB")
        try:
            value = json.loads(
                binding_bytes,
                object_pairs_hook=_unique_json_object,
                parse_constant=lambda constant: (_ for _ in ()).throw(
                    ValueError(constant)
                ),
            )
        except (json.JSONDecodeError, ValueError) as exc:
            raise ValueError("prepared environment manifest is invalid JSON") from exc
        binding_sha256 = hashlib.sha256(binding_bytes).hexdigest()
        if (
            isinstance(value, dict)
            and value.get("schema") == "agent-outcomes-prepared-image-binding-v1"
        ):
            if set(value) != {
                "schema",
                "image_digest",
                "content_manifest_sha256",
                "content_manifest",
            }:
                raise ValueError("prepared environment binding fields are invalid")
            image_digest = image_reference.rsplit("@sha256:", 1)[-1]
            if value["image_digest"] != image_digest:
                raise ValueError("prepared environment binding image digest differs")
            content_manifest_sha256 = value["content_manifest_sha256"]
            if not isinstance(content_manifest_sha256, str) or not _SHA256.fullmatch(
                content_manifest_sha256
            ):
                raise ValueError(
                    "prepared environment content manifest digest is invalid"
                )
            value = value["content_manifest"]
            data = json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
            if hashlib.sha256(data).hexdigest() != content_manifest_sha256:
                raise ValueError("prepared environment content manifest digest differs")
        else:
            data = binding_bytes
        if not isinstance(value, dict) or set(value) != {
            "schema",
            "base_image_digest",
            "repositories",
        }:
            raise ValueError("prepared environment manifest fields are invalid")
        if value[
            "schema"
        ] != "agent-outcomes-prepared-environments-v1" or not _SHA256.fullmatch(
            value["base_image_digest"]
        ):
            raise ValueError("prepared environment manifest identity is invalid")
        if "@sha256:" not in image_reference or not _SHA256.fullmatch(
            image_reference.rsplit("@sha256:", 1)[-1]
        ):
            raise ValueError("prepared environment image reference is not immutable")
        records = value["repositories"]
        if not isinstance(records, list) or not records:
            raise ValueError(
                "prepared environment repositories must be a non-empty array"
            )
        repositories = {}
        for record in records:
            if not isinstance(record, dict) or set(record) != {
                "repo_id",
                "environment",
                "workspace_mounts",
                "artifacts",
            }:
                raise ValueError("prepared repository fields are invalid")
            repo_id = record["repo_id"]
            if (
                not isinstance(repo_id, str)
                or not _ID.fullmatch(repo_id)
                or repo_id in repositories
            ):
                raise ValueError("prepared repository id is invalid or duplicated")
            environment = record["environment"]
            if not isinstance(environment, dict) or any(
                name not in _PREPARED_ENVIRONMENT_NAMES
                or not isinstance(setting, str)
                or not _prepared_setting_is_safe(setting, repo_id)
                for name, setting in environment.items()
            ):
                raise ValueError("prepared repository environment is invalid")
            mounts = record["workspace_mounts"]
            if not isinstance(mounts, list):
                raise TypeError("prepared repository workspace mounts must be an array")
            validated_mounts = []
            seen_mounts = set()
            for mount in mounts:
                if not isinstance(mount, dict) or set(mount) != {"path", "seed_path"}:
                    raise ValueError(
                        "prepared repository workspace mount fields are invalid"
                    )
                path_value = _safe_relative_path(
                    mount["path"], "prepared workspace mount"
                )
                seed_value = _safe_relative_path(
                    mount["seed_path"], "prepared workspace seed"
                )
                if not seed_value.startswith("workspace/") or path_value in seen_mounts:
                    raise ValueError(
                        "prepared repository workspace mount is invalid or duplicated"
                    )
                seen_mounts.add(path_value)
                validated_mounts.append(
                    MappingProxyType({"path": path_value, "seed_path": seed_value})
                )
            artifacts = record["artifacts"]
            if not isinstance(artifacts, list):
                raise TypeError("prepared repository artifacts must be an array")
            seen_artifacts = set()
            for artifact in artifacts:
                if not isinstance(artifact, dict) or set(artifact) != {
                    "path",
                    "kind",
                    "sha256",
                    "size_bytes",
                }:
                    raise ValueError("prepared repository artifact fields are invalid")
                artifact_path = _safe_relative_path(
                    artifact["path"], "prepared artifact"
                )
                if (
                    artifact_path in seen_artifacts
                    or artifact["kind"] not in {"file", "symlink"}
                    or not _SHA256.fullmatch(artifact["sha256"])
                    or not _nonnegative_int(artifact["size_bytes"])
                ):
                    raise ValueError(
                        "prepared repository artifact is invalid or duplicated"
                    )
                seen_artifacts.add(artifact_path)
            repositories[repo_id] = PreparedRepositoryEnvironment(
                repo_id,
                MappingProxyType(dict(environment)),
                tuple(validated_mounts),
            )
        return cls(
            manifest_path,
            hashlib.sha256(data).hexdigest(),
            binding_sha256,
            image_reference,
            MappingProxyType(repositories),
            data,
            set(),
        )

    def for_repo(self, repo_id: str) -> PreparedRepositoryEnvironment:
        try:
            return self.repositories[repo_id]
        except KeyError as exc:
            raise ValueError(
                f"prepared environment is missing repository: {repo_id}"
            ) from exc

    def verify_image(self, podman_path: str) -> None:
        if self.image_reference in self._verified_images:
            return
        embedded = _run_bounded(
            [
                podman_path,
                "run",
                "--rm",
                "--network=none",
                self.image_reference,
                "cat",
                "/opt/agent-deps/manifest.json",
            ],
            64 * 1024 * 1024,
            300,
        )
        if embedded != self.manifest_bytes:
            raise ValueError(
                "prepared environment manifest bytes differ from the embedded image"
            )
        verified = (
            _run_bounded(
                [
                    podman_path,
                    "run",
                    "--rm",
                    "--network=none",
                    self.image_reference,
                    "python3",
                    "-c",
                    _VERIFY_PREPARED_SCRIPT,
                ],
                1024,
                900,
            )
            .decode("ascii")
            .strip()
        )
        if verified != self.manifest_sha256:
            raise ValueError("prepared environment artifact verification failed")
        self._verified_images.add(self.image_reference)

    def materialize(
        self,
        repo_id: str,
        podman_path: str,
        runtime_root: Path,
    ) -> tuple[PreparedRepositoryEnvironment, Mapping[str, object]]:
        repository = self.for_repo(repo_id)
        verification_started = time.monotonic()
        self.verify_image(podman_path)
        verification_seconds = time.monotonic() - verification_started
        started = time.monotonic()
        completed = subprocess.run(
            [
                podman_path,
                "run",
                "--rm",
                "--network=none",
                "--userns=keep-id",
                "--security-opt=no-new-privileges",
                "--cap-drop=all",
                "--mount",
                f"type=bind,src={runtime_root.resolve()},dst=/runtime,rw,Z",
                self.image_reference,
                "python3",
                "-c",
                _MATERIALIZE_PREPARED_SCRIPT,
                repo_id,
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            check=False,
            timeout=900,
            env={"PATH": os.defpath},
        )
        seconds = time.monotonic() - started
        if completed.returncode != 0:
            raise RuntimeError(
                "prepared dependency materialization failed: "
                + completed.stderr[-4096:].decode("utf-8", errors="replace")
            )
        return repository, {
            "manifest_sha256": self.manifest_sha256,
            "binding_sha256": self.binding_sha256,
            "image_digest": self.image_reference.rsplit("@sha256:", 1)[1],
            "repo_id": repo_id,
            "materialization_seconds": seconds,
            "image_verification_seconds": verification_seconds,
            "download_bytes": None,
            "download_seconds": None,
        }


@dataclass(frozen=True)
class ExecutionResultEnvelope:
    run_record: Mapping[str, object]
    execution: Mapping[str, object]


class PodmanVerificationExecutor:
    def __init__(
        self,
        image_reference: str,
        *,
        podman_path: str = "podman",
        prepared_repository: PreparedRepositoryEnvironment | None = None,
        prepared_runtime_root: Path | None = None,
    ) -> None:
        self.image_reference = image_reference
        self.podman_path = podman_path
        self.prepared_repository = prepared_repository
        self.prepared_runtime_root = prepared_runtime_root

    def execute(
        self, argv: Sequence[str], candidate_root: Path, timeout_seconds: int
    ) -> VerificationExecution:
        with tempfile.TemporaryDirectory(
            prefix="agent-outcomes-container-"
        ) as control_directory:
            cidfile = Path(control_directory) / "container.cid"
            command = [
                self.podman_path,
                "run",
                "--cidfile",
                str(cidfile),
                "--network=none",
                "--userns=keep-id",
                "--security-opt=no-new-privileges",
                "--cap-drop=all",
                "--mount",
                f"type=bind,src={candidate_root.resolve()},dst=/workspace,rw,Z",
            ]
            if self.prepared_repository is not None:
                if self.prepared_runtime_root is None:
                    raise ValueError("prepared verifier runtime root is missing")
                command.extend(
                    [
                        "--mount",
                        f"type=bind,src={self.prepared_runtime_root.resolve()},dst=/runtime,rw,Z",
                    ]
                )
                command.extend(
                    _prepared_container_arguments(
                        self.prepared_repository,
                        self.prepared_runtime_root,
                        candidate_root,
                    )
                )
            command.extend(["--workdir", "/workspace", self.image_reference, *argv])
            return self._execute_owned(command, cidfile, timeout_seconds)

    def _execute_owned(
        self,
        command: Sequence[str],
        cidfile: Path,
        timeout_seconds: int,
    ) -> VerificationExecution:
        with (
            tempfile.TemporaryFile() as stdout_file,
            tempfile.TemporaryFile() as stderr_file,
        ):
            try:
                process = subprocess.Popen(
                    command,
                    stdout=stdout_file,
                    stderr=stderr_file,
                    start_new_session=True,
                    env={"PATH": os.defpath},
                )
            except OSError as exc:
                return VerificationExecution(False, None, "", str(exc))
            supervision_error = None
            try:
                try:
                    process.wait(timeout=timeout_seconds)
                    returncode = process.returncode
                    _terminate_process_group(process.pid, None)
                except subprocess.TimeoutExpired:
                    returncode = 124
                    _terminate_process_group(process.pid, process)
            except (OSError, subprocess.SubprocessError) as exc:
                supervision_error = str(exc)
                returncode = 124
            finally:
                cleanup_confirmed, cleanup_error = _cleanup_container(
                    self.podman_path, cidfile
                )
            stdout_file.seek(0)
            stderr_file.seek(0)
            stdout = stdout_file.read(1024 * 1024).decode("utf-8", errors="replace")
            stderr = stderr_file.read(1024 * 1024).decode("utf-8", errors="replace")
            if supervision_error is not None or not cleanup_confirmed:
                detail = "; ".join(
                    value for value in (supervision_error, cleanup_error) if value
                )
                return VerificationExecution(False, None, stdout, stderr + detail)
            return VerificationExecution(True, returncode, stdout, stderr)


class NativeAgentRunner:
    def __init__(
        self,
        campaign: Campaign,
        *,
        image_reference: str,
        codex_path: str,
        miller_path: str,
        podman_path: str = "podman",
        environment_allowlist: Sequence[str] = (),
        provider_transport: ProviderTransport | None = None,
        qualification: RunnerQualification | None = None,
        verification_executor: VerificationExecutor | None = None,
        runtime_observer: RuntimeObserver | None = None,
        probe_timeout_seconds: int = 30,
        prepared_environment: PreparedEnvironment | None = None,
        semantic_runtime_binding: SemanticRuntimeBinding | None = None,
    ) -> None:
        host = campaign.value["host"]
        model = campaign.value["model"]
        self.campaign = campaign
        self.host_name = host["name"]
        self.host_version = host["version"]
        self.host_binary_sha256 = host["binary_sha256"]
        self.model_id = model["model_id"]
        self.reasoning = model["reasoning"]
        self.image_reference = image_reference
        self.codex_path = codex_path
        self.miller_path = miller_path
        self.podman_path = podman_path
        self.environment_allowlist = tuple(environment_allowlist)
        self.provider_transport = provider_transport
        self.qualification = qualification
        self._uses_default_verification_executor = verification_executor is None
        self.verification_executor = (
            verification_executor
            or PodmanVerificationExecutor(
                image_reference,
                podman_path=podman_path,
            )
        )
        self.runtime_observer = runtime_observer
        self.probe_timeout_seconds = probe_timeout_seconds
        self.prepared_environment = prepared_environment
        self.semantic_runtime_binding = semantic_runtime_binding
        self._os_qualifications: set[str] = set()
        self._ct_baseline_copies: dict[str, Path] = {}
        self._ct_prepared_setups: dict[str, Mapping[str, object] | None] = {}
        self._semantic_observation: Mapping[str, object] | None = None
        expected_digest = campaign.value["platform_toolchain_image_sha256"]
        if not image_reference.endswith("@sha256:" + expected_digest):
            raise ValueError("image reference must use the frozen campaign digest")
        if (
            prepared_environment is not None
            and prepared_environment.image_reference != image_reference
        ):
            raise ValueError("prepared environment image differs from runner image")
        if (
            semantic_runtime_binding is not None
            and semantic_runtime_binding.image_reference != image_reference
        ):
            raise ValueError("semantic runtime binding image differs from runner image")
        if (self.host_name, self.host_version) != _SUPPORTED_HOST:
            raise ValueError(
                f"unsupported native adapter host: {self.host_name} {self.host_version}"
            )
        forbidden = {"HOME", "CODEX_HOME", "OPENAI_API_KEY", "ANTHROPIC_API_KEY"}
        if forbidden.intersection(self.environment_allowlist):
            raise ValueError(
                "environment allowlist contains auth or host-config variables"
            )
        if (
            provider_transport is not None
            and provider_transport.network_policy != campaign.value["network_policy"]
        ):
            raise ValueError("provider transport network policy differs from campaign")
        if campaign.value["network_policy"] == "allowlist":
            raise ValueError(
                "allowlist network policy has no qualified enforcement adapter"
            )

    @staticmethod
    def option(command: Sequence[str], name: str) -> str | None:
        try:
            return command[command.index(name) + 1]
        except (ValueError, IndexError):
            return None

    def build_agent_command(
        self, task: OutcomeTask, arm_id: str
    ) -> tuple[list[str], str]:
        self._arm(arm_id)
        sandbox = (
            "read-only" if task.workflow in _ANSWER_WORKFLOWS else "workspace-write"
        )
        command = [
            self.codex_path,
            "exec",
            "--json",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "--strict-config",
            "--model",
            self.model_id,
            "--sandbox",
            sandbox,
            "--cd",
            "/workspace",
            "--output-schema",
            "/run-config/response-schema.json",
            "--config",
            "model_reasoning_effort=" + _toml_string(self.reasoning),
            "--config",
            'approval_policy="never"',
        ]
        if self.provider_transport is not None:
            transport = self.provider_transport
            command.extend(
                [
                    "--config",
                    "model_provider=" + _toml_string(transport.provider_id),
                    "--config",
                    f'model_providers.{transport.provider_id}.name="qualified-gateway"',
                    "--config",
                    f"model_providers.{transport.provider_id}.base_url="
                    + _toml_string(transport.base_url),
                    "--config",
                    f'model_providers.{transport.provider_id}.wire_api="responses"',
                    "--config",
                    f"model_providers.{transport.provider_id}.requires_openai_auth=false",
                ]
            )
        if arm_id != "native":
            command.append(
                "--config=mcp_servers.miller.command=" + _toml_string(self.miller_path)
            )
            command.append('--config=mcp_servers.miller.args=["serve"]')
            if arm_id == "native+miller-lexical":
                command.append('--config=mcp_servers.miller.env.MILLER_SEMANTIC="off"')
            command.append('--config=mcp_servers.miller.env.MILLER_CT="off"')
        prompt = (
            task.prompt
            + "\n\nUse your native read, search, edit, command, and test tools as appropriate. For mutation work, run the repository's focused tests. Return only the requested structured result."
        )
        return command, prompt

    def build_ct_agent_command(self, task: OutcomeTask) -> tuple[list[str], str]:
        command, prompt = self.build_agent_command(task, "native+miller-lexical")
        ct_off = '--config=mcp_servers.miller.env.MILLER_CT="off"'
        return [part for part in command if part != ct_off], prompt

    def qualification_configuration_sha256(
        self,
        experiment_root: Path | None = None,
        workspace_mode: str | None = None,
        arm_id: str | None = None,
        prepared_repo_id: str | None = None,
    ) -> str:
        value = {
            "host": [self.host_name, self.host_version, self.host_binary_sha256],
            "image": self.image_reference,
            "network": self.campaign.value["network_policy"],
            "memory": self.campaign.value["resource_limits"]["memory_bytes"],
            "transport": None
            if self.provider_transport is None
            else {
                "provider_id": self.provider_transport.provider_id,
                "base_url": self.provider_transport.base_url,
                "qualification_sha256": self.provider_transport.qualification_sha256,
                "network_policy": self.provider_transport.network_policy,
            },
            "experiment_root_sha256": None
            if experiment_root is None
            else _path_sha256(experiment_root.resolve()),
            "mounts": [
                f"task-input:/workspace:{workspace_mode}",
                "agent-output:/run-results:rw",
                "runtime:/runtime:rw",
                "miller-runtime:/workspace/.miller:rw",
                "public-schema:/run-config/response-schema.json:ro",
                "native-mask:/opt/miller:ro"
                if arm_id == "native"
                else "miller-visible:/opt/miller:ro",
            ],
            "prepared_environment_sha256": None
            if self.prepared_environment is None
            else self.prepared_environment.manifest_sha256,
            "prepared_environment_binding_sha256": None
            if self.prepared_environment is None
            else self.prepared_environment.binding_sha256,
            "prepared_repo_id": prepared_repo_id,
            "semantic_runtime_binding_sha256": None
            if self.semantic_runtime_binding is None
            else self.semantic_runtime_binding.binding_sha256,
        }
        return hashlib.sha256(
            json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
        ).hexdigest()

    def build_run(
        self,
        task: OutcomeTask,
        arm_id: str,
        snapshot: Path,
        output_dir: Path,
        runtime_dir: Path | None = None,
        cidfile: Path | None = None,
        prepared_repository: PreparedRepositoryEnvironment | None = None,
    ) -> tuple[list[str], str]:
        agent, prompt = self.build_agent_command(task, arm_id)
        mode = "ro" if task.workflow in _ANSWER_WORKFLOWS else "rw"
        command = [
            self.podman_path,
            "run",
            "--rm",
            *([] if cidfile is None else ["--cidfile", str(cidfile)]),
            *self._runtime_arguments(
                snapshot,
                output_dir,
                mode,
                runtime_dir
                or output_dir.parent / "runtime-artifacts" / "command-preview",
                arm_id,
                prepared_repository,
            ),
            self.image_reference,
            *agent,
        ]
        return command, prompt

    def build_ct_container_spec(
        self,
        task: VerifiableTask,
        arm_id: str,
        snapshot: Path,
        output_dir: Path,
        runtime_dir: Path,
        cidfile: Path,
        known_change,
    ):
        from .agent_outcomes_ct import CtContainerSpec

        candidate = snapshot.resolve(strict=True)
        private_output = output_dir.resolve()
        runtime = runtime_dir.resolve()
        if (
            not candidate.is_dir()
            or private_output == candidate
            or runtime == candidate
        ):
            raise ValueError("CT attempt paths are invalid")
        private_output.mkdir(mode=0o700, parents=True, exist_ok=False)
        baseline_copy = private_output / "baseline-verification"
        shutil.copytree(candidate, baseline_copy, symlinks=True)
        runtime.mkdir(mode=0o700, parents=True, exist_ok=False)
        (runtime / "miller").mkdir()
        (runtime / "native-miller-mask").mkdir()
        (runtime / "home").mkdir()
        untrusted_output = runtime / "agent-output"
        untrusted_output.mkdir()
        response_schema = public_response_schema(task)
        _write_exclusive(
            runtime / "public-response-schema.json",
            json.dumps(_plain(response_schema), sort_keys=True, separators=(",", ":")),
        )
        self._arm(arm_id)
        prepared_repository = None
        prepared_setup = None
        if self.prepared_environment is not None:
            prepared_repository, prepared_setup = self.prepared_environment.materialize(
                task.task.repo_id,
                self.podman_path,
                runtime,
            )
        if arm_id == "native":
            agent_argv, prompt = self.build_agent_command(task.task, arm_id)
        else:
            agent_argv, prompt = self.build_ct_agent_command(task.task)
        prompt_path = private_output / "prompt.txt"
        _write_exclusive(prompt_path, prompt)
        if cidfile.resolve().parent != private_output or cidfile.exists():
            raise ValueError("CT cidfile must be a new private output child")
        create_argv = (
            self.podman_path,
            "create",
            "--init",
            "--cidfile",
            str(cidfile.resolve()),
            *self._runtime_arguments(
                candidate,
                untrusted_output,
                "rw",
                runtime,
                arm_id,
                prepared_repository,
            ),
            self.image_reference,
            "sleep",
            "infinity",
        )
        spec = CtContainerSpec(
            self.podman_path,
            self.image_reference,
            tuple(create_argv),
            tuple(agent_argv),
            prompt_path,
            private_output / "raw-events.jsonl",
            private_output / "stderr.txt",
            cidfile.resolve(),
            task.task.max_wall_seconds,
            candidate,
            arm_id,
            known_change,
        )
        self._ct_baseline_copies[str(spec.cidfile)] = baseline_copy
        self._ct_prepared_setups[str(spec.cidfile)] = prepared_setup
        return spec

    def run_ct(
        self,
        supervisor,
        lifecycle,
        spec,
        task: VerifiableTask,
        arm_id: str,
        *,
        repetition: int = 1,
        order: int = 1,
    ) -> ExecutionResultEnvelope:
        outcome = supervisor.run(spec, lifecycle, task, arm_id)
        return self.finalize_ct_attempt(
            task, arm_id, spec, outcome, repetition=repetition, order=order
        )

    def finalize_ct_attempt(
        self,
        task: VerifiableTask,
        arm_id: str,
        spec,
        outcome,
        *,
        repetition: int = 1,
        order: int = 1,
    ) -> ExecutionResultEnvelope:
        baseline = self._ct_baseline_copies.pop(str(spec.cidfile), None)
        prepared_setup = self._ct_prepared_setups.pop(str(spec.cidfile), None)
        if (
            baseline is None
            or source_snapshot_sha256(baseline) != task.task.snapshot_sha256
        ):
            raise ValueError(
                "CT private baseline verification copy is unavailable or changed"
            )
        raw_path = Path(spec.raw_events_path)
        if not raw_path.is_file():
            _write_exclusive(raw_path, "")
        if raw_path.stat().st_size > 16 * 1024 * 1024:
            parsed = ParsedAgentEvents(
                {}, 0, (None, None, None), None, "raw JSONL exceeds parse bound", ()
            )
        else:
            parsed = self.parse_events(
                raw_path.read_text(encoding="utf-8", errors="replace")
            )
        if not outcome.container_removed:
            result_kind = "infrastructure_void"
            evidence = {"reason": "CT container cleanup was not confirmed"}
        elif outcome.returncode == 124:
            result_kind = "timeout"
            evidence = {"reason": "CT agent timeout"}
        elif not outcome.success:
            result_kind = (
                "product_error"
                if str(outcome.disposition).startswith("agent_")
                else "infrastructure_void"
            )
            evidence = {"reason": str(outcome.disposition)}
        elif parsed.unsupported_reason:
            result_kind = "unsupported"
            evidence = {"reason": parsed.unsupported_reason}
        elif parsed.failed or outcome.returncode not in {None, 0}:
            result_kind = "product_error"
            evidence = {"reason": "CT agent failed", "returncode": outcome.returncode}
        else:
            verification = verify_result(task, _parse_result(parsed.answer), baseline)
            result_kind = "correct" if verification.correct else "incorrect"
            evidence = dict(verification.evidence)
            evidence["failures"] = list(verification.failures)
        campaign_value = json.dumps(
            _plain(self.campaign.value), sort_keys=True, separators=(",", ":")
        ).encode()
        evidence["ct_attempt_evidence_sha256"] = outcome.evidence_sha256
        record = {
            "contract_id": "agent-outcomes-v1",
            "campaign_sha256": hashlib.sha256(campaign_value).hexdigest(),
            "run_id": f"{task.task.task_id}-{arm_id.replace('+', '-')}-r{repetition}",
            "task_id": task.task.task_id,
            "arm_id": arm_id,
            "repetition": repetition,
            "order": order,
            "outcome": result_kind,
            "verifier_evidence_sha256": hashlib.sha256(
                json.dumps(evidence, sort_keys=True, separators=(",", ":")).encode()
            ).hexdigest(),
            "wall_time_seconds": outcome.wall_time_seconds,
            "native_tool_counts": dict(parsed.native_tool_counts),
            "miller_calls": parsed.miller_calls,
            "total_model_input_tokens": parsed.model_tokens[0],
            "total_model_cached_tokens": parsed.model_tokens[1],
            "total_model_output_tokens": parsed.model_tokens[2],
            "raw_event_sha256": _file_sha256(raw_path),
            "price_derived_cost": None,
        }
        validated = validate_run_record(record)
        execution = {
            "ct_attempt_evidence_sha256": outcome.evidence_sha256,
            "ct_lifecycle_evidence_sha256": outcome.lifecycle.evidence_sha256,
            "ct_transition_evidence_sha256": outcome.transition.evidence_sha256,
            "ct_cleanup_evidence_sha256": outcome.cleanup.evidence_sha256,
            "container_removed": outcome.container_removed,
            "setup_wall_time_seconds": outcome.setup_wall_time_seconds,
            "agent_wall_time_seconds": outcome.agent_wall_time_seconds,
            "reasoning_output_tokens": parsed.reasoning_output_tokens,
            "raw_events_path": str(raw_path),
            "stderr_path": str(spec.stderr_path),
            "candidate_root": str(spec.candidate_root),
            "baseline_snapshot_sha256": source_snapshot_sha256(baseline),
            "changed_snapshot_sha256": source_snapshot_sha256(spec.candidate_root),
            "measured_snapshot_sha256": outcome.measured_snapshot_sha256,
            "prepared_environment": prepared_setup,
        }
        private_path = raw_path.parent / "ct-execution-private.json"
        execution["private_envelope_path"] = str(private_path)
        _write_exclusive(private_path, json.dumps(execution, sort_keys=True))
        return ExecutionResultEnvelope(_plain(validated.value), execution)

    def build_isolation_probe(
        self,
        experiment_root: Path,
        sentinel: str,
        sentinel_secret: str,
        *,
        mutation: bool,
        arm_id: str,
        prepared_repository: PreparedRepositoryEnvironment | None = None,
    ) -> list[str]:
        root = experiment_root.resolve()
        workspace_mode = "rw" if mutation else "ro"
        absolute_sentinel = root / "private-grader" / sentinel
        workspace_probe = "/workspace/.agent-outcomes-probe-" + sentinel
        script = (
            "set -eu; "
            f'test "$({shlex.quote(self.codex_path)} --version)" = {shlex.quote(self.host_name + " " + self.host_version)}; '
            f"test \"$(sha256sum {shlex.quote(self.codex_path)} | cut -d ' ' -f 1)\" = {shlex.quote(self.host_binary_sha256)}; "
            f"if cat {shlex.quote(str(absolute_sentinel))} >/dev/null 2>&1; then exit 41; fi; "
            f"if cat {shlex.quote('/private-grader/' + sentinel)} >/dev/null 2>&1; then exit 42; fi; "
            + (
                f"printf %s {shlex.quote(sentinel_secret)} > {shlex.quote(workspace_probe)}; "
                f'test "$(cat {shlex.quote(workspace_probe)})" = {shlex.quote(sentinel_secret)}; '
                f"rm {shlex.quote(workspace_probe)}; "
                if mutation
                else f"if printf %s {shlex.quote(sentinel_secret)} > {shlex.quote(workspace_probe)} 2>/dev/null; then exit 43; fi; "
            )
            + "test ! -S /run/podman/podman.sock; "
            + (
                "test ! -e /opt/miller/miller"
                if arm_id == "native"
                else f"test -x {shlex.quote(self.miller_path)}"
            )
        )
        return [
            self.podman_path,
            "create",
            "--cidfile",
            str(root / "private-grader" / f"{sentinel}.cid"),
            *self._runtime_arguments(
                root / "task-input",
                root / "agent-output",
                workspace_mode,
                root / "qualification-runtime" / workspace_mode,
                arm_id,
                prepared_repository,
            ),
            self.image_reference,
            "/bin/sh",
            "-c",
            script,
        ]

    def qualify_isolation(
        self,
        experiment_root: Path,
        *,
        mutation: bool,
        arm_id: str,
        repo_id: str | None = None,
    ) -> IsolationProbeResult:
        self._arm(arm_id)
        root = experiment_root.resolve(strict=True)
        snapshot = root / "task-input"
        grader = root / "private-grader"
        output = root / "agent-output"
        if not snapshot.is_dir() or not grader.is_dir():
            raise ValueError(
                "isolation qualification requires task-input and private-grader"
            )
        output.mkdir(mode=0o700, exist_ok=False)
        qualification_runtime = (
            root / "qualification-runtime" / ("rw" if mutation else "ro")
        )
        (qualification_runtime / "miller").mkdir(parents=True)
        (qualification_runtime / "native-miller-mask").mkdir()
        (qualification_runtime / "home").mkdir()
        _write_exclusive(qualification_runtime / "public-response-schema.json", "{}")
        prepared_repository = None
        prepared_setup = None
        if self.prepared_environment is not None:
            if repo_id is None:
                raise ValueError("prepared isolation qualification requires repo_id")
            prepared_repository, prepared_setup = self.prepared_environment.materialize(
                repo_id,
                self.podman_path,
                qualification_runtime,
            )
        sentinel = "sentinel-" + secrets.token_hex(16)
        sentinel_secret = secrets.token_hex(32)
        (grader / sentinel).write_text(sentinel_secret, encoding="utf-8")
        command = self.build_isolation_probe(
            root,
            sentinel,
            sentinel_secret,
            mutation=mutation,
            arm_id=arm_id,
            prepared_repository=prepared_repository,
        )
        safe_env = {"PATH": os.defpath}
        try:
            created = subprocess.run(
                command,
                capture_output=True,
                text=True,
                check=False,
                timeout=self.probe_timeout_seconds,
                env=safe_env,
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            created = subprocess.CompletedProcess(command, 124, "", str(exc))
        cidfile = grader / f"{sentinel}.cid"
        container_id = created.stdout.strip()
        if cidfile.is_file() and not cidfile.is_symlink():
            try:
                container_id = cidfile.read_text(encoding="ascii").strip()
            except (OSError, UnicodeError):
                container_id = ""
        inspect_stdout = ""
        start_stdout = ""
        stderr = created.stderr
        returncode = created.returncode
        passed = False
        qualification = None
        if created.returncode == 0 and container_id and "\n" not in container_id:
            try:
                inspected = subprocess.run(
                    [self.podman_path, "inspect", container_id],
                    capture_output=True,
                    text=True,
                    check=False,
                    timeout=30,
                    env=safe_env,
                )
                inspect_stdout = inspected.stdout
                stderr += inspected.stderr
                mode = "rw" if mutation else "ro"
                if inspected.returncode == 0 and self._inspect_matches_runtime(
                    inspect_stdout,
                    snapshot,
                    output,
                    mode,
                    qualification_runtime,
                    arm_id,
                    prepared_repository,
                ):
                    started = subprocess.run(
                        [self.podman_path, "start", "--attach", container_id],
                        capture_output=True,
                        text=True,
                        check=False,
                        timeout=30,
                        env=safe_env,
                    )
                    start_stdout = started.stdout
                    stderr += started.stderr
                    returncode = started.returncode
                    passed = started.returncode == 0
            except (OSError, subprocess.TimeoutExpired) as exc:
                stderr += str(exc)
                passed = False
        if cidfile.exists():
            cleanup_confirmed, cleanup_error = _cleanup_container(
                self.podman_path, cidfile
            )
            passed = passed and cleanup_confirmed
            stderr += cleanup_error
        if passed:
            configuration_sha = self.qualification_configuration_sha256(
                root, mode, arm_id, repo_id
            )
            qualification = RunnerQualification(
                self.host_name,
                self.host_version,
                self.host_binary_sha256,
                self.image_reference,
                self.campaign.value["network_policy"],
                (),
                False,
                configuration_sha,
                _path_sha256(root),
                mode,
                arm_id,
                repo_id,
                True,
                "os",
            )
        try:
            output.rmdir()
        except OSError:
            passed = False
            qualification = None
        evidence_dir = grader / "qualification-evidence"
        evidence_dir.mkdir(mode=0o700, exist_ok=True)
        evidence_path = evidence_dir / f"{sentinel}.json"
        _write_exclusive(
            evidence_path,
            json.dumps(
                {
                    "argv": command,
                    "returncode": returncode,
                    "stdout": created.stdout + start_stdout,
                    "stderr": stderr,
                    "inspect_json": inspect_stdout,
                    "passed": passed,
                    "configuration_sha256": None
                    if qualification is None
                    else qualification.configuration_sha256,
                    "prepared_setup": prepared_setup,
                },
                sort_keys=True,
            ),
        )
        if qualification is not None:
            self._os_qualifications.add(qualification.configuration_sha256)
        return IsolationProbeResult(
            passed,
            tuple(command),
            returncode,
            created.stdout + start_stdout,
            stderr,
            None if qualification is None else qualification.configuration_sha256,
            inspect_stdout,
            qualification,
            str(evidence_path),
            prepared_setup,
        )

    def run(
        self,
        task: OutcomeTask | VerifiableTask,
        arm_id: str,
        snapshot: Path,
        output_dir: Path,
        *,
        repetition: int = 1,
        order: int = 1,
    ) -> ExecutionResultEnvelope:
        if self.provider_transport is None:
            raise UnsafeLiveExecution(
                "live execution requires a qualified credential transport with no provider secret in the agent container"
            )
        if self.qualification is None or not self.qualification.passed:
            raise UnsafeLiveExecution(
                "live execution requires a passing frozen OS qualification"
            )
        outcome_task = task.task if isinstance(task, VerifiableTask) else task
        arm = self._arm(arm_id)
        semantic_identity = arm["runtime_identity"]
        semantic_observation = None
        if semantic_identity is not None:
            binding = self.semantic_runtime_binding
            if (
                binding is None
                or dict(binding.runtime_identity) != dict(semantic_identity)
                or binding.runtime_qualification_sha256
                != arm["runtime_qualification_sha256"]
            ):
                raise UnsafeLiveExecution(
                    "semantic arm requires an exact observed image runtime binding"
                )
            if self._semantic_observation is None:
                self._semantic_observation = binding.verify_image(self.podman_path)
            semantic_observation = self._semantic_observation
        snapshot_input = Path(snapshot)
        if snapshot_input.is_symlink() or snapshot_input.parent.is_symlink():
            raise ValueError("experiment input and root cannot be symlinks")
        snapshot = snapshot_input.resolve(strict=True)
        experiment_root = self._validate_experiment_paths(snapshot, output_dir)
        workspace_mode = "ro" if outcome_task.workflow in _ANSWER_WORKFLOWS else "rw"
        prepared_repo_id = (
            outcome_task.repo_id if self.prepared_environment is not None else None
        )
        if (
            self.qualification.configuration_sha256
            != self.qualification_configuration_sha256(
                experiment_root,
                workspace_mode,
                arm_id,
                prepared_repo_id,
            )
            or self.qualification.experiment_root_sha256
            != _path_sha256(experiment_root)
            or self.qualification.workspace_mode != workspace_mode
            or self.qualification.arm_id != arm_id
            or self.qualification.prepared_repo_id != prepared_repo_id
        ):
            raise UnsafeLiveExecution(
                "OS qualification does not match the frozen execution configuration"
            )
        transport_host = urlsplit(self.provider_transport.base_url).hostname or ""
        if self.qualification.kind == "fake" and not (
            transport_host.endswith(".invalid")
            or transport_host in {"127.0.0.1", "::1", "localhost"}
        ):
            raise UnsafeLiveExecution(
                "fake qualification cannot authorize a live provider gateway"
            )
        if (
            self.qualification.kind == "os"
            and self.qualification.configuration_sha256 not in self._os_qualifications
        ):
            raise UnsafeLiveExecution(
                "OS qualification was not produced by this runner's direct isolation probe"
            )
        if self.qualification.kind == "os" and not isinstance(task, VerifiableTask):
            raise UnsafeLiveExecution(
                "OS-qualified execution requires a bound public response schema and verifier"
            )
        if source_snapshot_sha256(snapshot) != outcome_task.snapshot_sha256:
            raise ValueError(
                "task snapshot identity does not match frozen snapshot_sha256"
            )
        if (snapshot / ".miller").exists():
            raise ValueError(
                "frozen task input cannot contain the isolated Miller runtime mount path"
            )
        output_dir.mkdir(parents=True, exist_ok=False)
        run_name = (
            f"{outcome_task.task_id}-{arm_id.replace('+', '-')}-r{repetition}-o{order}"
        )
        candidate_root = experiment_root / "run-workspaces" / run_name
        candidate_root.parent.mkdir(mode=0o700, exist_ok=True)
        shutil.copytree(snapshot, candidate_root, symlinks=True)
        if source_snapshot_sha256(candidate_root) != outcome_task.snapshot_sha256:
            raise ValueError("disposable run copy differs from frozen snapshot")
        runtime_root = experiment_root / "runtime-artifacts" / run_name
        (runtime_root / "miller").mkdir(parents=True)
        (runtime_root / "native-miller-mask").mkdir()
        (runtime_root / "home").mkdir()
        prepared_repository = None
        prepared_setup = None
        if self.prepared_environment is not None:
            prepared_repository, prepared_setup = self.prepared_environment.materialize(
                outcome_task.repo_id,
                self.podman_path,
                runtime_root,
            )
        response_schema = (
            public_response_schema(task)
            if isinstance(task, VerifiableTask)
            else {"type": "object"}
        )
        response_schema_json = json.dumps(
            _plain(response_schema), sort_keys=True, separators=(",", ":")
        )
        _write_exclusive(
            runtime_root / "public-response-schema.json", response_schema_json
        )
        evidence_parent = experiment_root / "private-grader" / "run-evidence"
        evidence_parent.mkdir(mode=0o700, exist_ok=True)
        evidence_dir = evidence_parent / run_name
        evidence_dir.mkdir(mode=0o700)
        raw_path = evidence_dir / "raw-events.jsonl"
        stderr_path = evidence_dir / "stderr.txt"
        cidfile = evidence_dir / "container.cid"
        prompt_path = evidence_dir / "prompt.txt"
        command, prompt = self.build_run(
            outcome_task,
            arm_id,
            candidate_root,
            output_dir,
            runtime_root,
            cidfile,
            prepared_repository,
        )
        _write_exclusive(prompt_path, prompt)
        zero_work_before = (
            self.runtime_observer.snapshot() if self.runtime_observer else None
        )
        started = time.monotonic()
        timed_out = False
        launch_error = None
        descendant_cleanup_performed = False
        process = None
        with (
            _open_exclusive(raw_path) as raw_file,
            _open_exclusive(stderr_path) as stderr_file,
            prompt_path.open("rb") as prompt_file,
        ):
            try:
                process = subprocess.Popen(
                    command,
                    stdin=prompt_file,
                    stdout=raw_file,
                    stderr=stderr_file,
                    start_new_session=True,
                    env={"PATH": os.defpath},
                )
            except OSError as exc:
                launch_error = str(exc)
            if process is not None:
                try:
                    process.wait(timeout=outcome_task.max_wall_seconds)
                except subprocess.TimeoutExpired:
                    timed_out = True
                    descendant_cleanup_performed = _terminate_process_group(
                        process.pid, process
                    )
                else:
                    descendant_cleanup_performed = _terminate_process_group(
                        process.pid, None
                    )
        wall_time = time.monotonic() - started
        container_cleanup_confirmed = None
        container_cleanup_error = ""
        if cidfile.exists():
            container_cleanup_confirmed, container_cleanup_error = _cleanup_container(
                self.podman_path, cidfile
            )
        elif self.qualification.kind == "os":
            container_cleanup_confirmed = False
            container_cleanup_error = "owned container id was not captured"
        zero_work_error = None
        if self.runtime_observer and zero_work_before is not None:
            try:
                self.assert_zero_work(
                    arm_id, zero_work_before, self.runtime_observer.snapshot()
                )
            except RuntimeError as exc:
                zero_work_error = str(exc)
        raw_size = raw_path.stat().st_size
        if raw_size > 16 * 1024 * 1024:
            parsed = ParsedAgentEvents(
                {},
                0,
                (None, None, None),
                None,
                "raw JSONL exceeds parse bound",
                (),
                None,
                False,
            )
        else:
            parsed = self.parse_events(
                raw_path.read_text(encoding="utf-8", errors="replace")
            )
        if container_cleanup_confirmed is False:
            outcome = "infrastructure_void"
            evidence = {"reason": "owned container cleanup was not confirmed"}
        elif zero_work_error is not None:
            outcome = "infrastructure_void"
            evidence = {"reason": "off-mode observation failed"}
        elif timed_out:
            outcome = "timeout"
            evidence = {"reason": "process timeout"}
        elif launch_error is not None:
            outcome = "infrastructure_void"
            evidence = {"reason": "process launch failed", "detail": launch_error}
        elif parsed.unsupported_reason:
            outcome = "unsupported"
            evidence = {"reason": parsed.unsupported_reason}
        elif parsed.failed:
            outcome = "product_error"
            evidence = {"reason": "agent failure lifecycle event"}
        elif process is not None and process.returncode != 0:
            outcome = "product_error"
            evidence = {"returncode": process.returncode}
        elif not isinstance(task, VerifiableTask):
            outcome = "unsupported"
            evidence = {"reason": "bound verifier is required"}
        else:
            result = _parse_result(parsed.answer)
            verification_executor = self.verification_executor
            if (
                self._uses_default_verification_executor
                and prepared_repository is not None
            ):
                verification_executor = PodmanVerificationExecutor(
                    self.image_reference,
                    podman_path=self.podman_path,
                    prepared_repository=prepared_repository,
                    prepared_runtime_root=runtime_root,
                )
            verification = verify_result(
                task, result, candidate_root, executor=verification_executor
            )
            outcome = "correct" if verification.correct else "incorrect"
            evidence = dict(verification.evidence)
            evidence["failures"] = list(verification.failures)
        canonical_campaign = json.dumps(
            _plain(self.campaign.value), sort_keys=True, separators=(",", ":")
        ).encode()
        campaign_sha = hashlib.sha256(canonical_campaign).hexdigest()
        evidence_sha = hashlib.sha256(
            json.dumps(evidence, sort_keys=True, separators=(",", ":")).encode()
        ).hexdigest()
        raw_sha = _file_sha256(raw_path)
        input_tokens, cached_tokens, output_tokens = parsed.model_tokens
        record = {
            "contract_id": "agent-outcomes-v1",
            "campaign_sha256": campaign_sha,
            "run_id": f"{outcome_task.task_id}-{arm_id.replace('+', '-')}-r{repetition}",
            "task_id": outcome_task.task_id,
            "arm_id": arm_id,
            "repetition": repetition,
            "order": order,
            "outcome": outcome,
            "verifier_evidence_sha256": evidence_sha,
            "wall_time_seconds": wall_time,
            "native_tool_counts": dict(parsed.native_tool_counts),
            "miller_calls": parsed.miller_calls,
            "total_model_input_tokens": input_tokens,
            "total_model_cached_tokens": cached_tokens,
            "total_model_output_tokens": output_tokens,
            "raw_event_sha256": raw_sha,
            "price_derived_cost": None,
        }
        validated = validate_run_record(record)
        execution = {
            "host": {
                "name": self.host_name,
                "version": self.host_version,
                "binary_sha256": self.host_binary_sha256,
            },
            "image_reference": self.image_reference,
            "provider_transport_qualification_sha256": self.provider_transport.qualification_sha256,
            "public_response_schema_sha256": hashlib.sha256(
                response_schema_json.encode()
            ).hexdigest(),
            "verifier_sha256": None
            if not isinstance(task, VerifiableTask)
            else hashlib.sha256(
                json.dumps(
                    _plain(task.verifier.value), sort_keys=True, separators=(",", ":")
                ).encode()
            ).hexdigest(),
            "argv_sha256": hashlib.sha256(
                json.dumps(command, separators=(",", ":")).encode()
            ).hexdigest(),
            "prompt_sha256": hashlib.sha256(prompt.encode()).hexdigest(),
            "environment_allowlist_names": ["PATH"],
            "network_policy": self.campaign.value["network_policy"],
            "prepared_environment": prepared_setup,
            "semantic_runtime_observation": semantic_observation,
            "semantic_runtime_binding_sha256": None
            if self.semantic_runtime_binding is None
            else self.semantic_runtime_binding.binding_sha256,
            "reasoning_output_tokens": parsed.reasoning_output_tokens,
            "peak_process_memory_bytes": None,
            "setup": {
                "download_bytes": None,
                "download_seconds": None,
                "extraction_seconds": None,
                "sidecar_convergence_seconds": None,
                "model_load_seconds": None,
                "shared_broker_mode": None,
                "steady_state_ready": None,
                "measurement_scope": "agent-process-and-owned-descendants",
            },
            "wall_time_seconds": wall_time,
            "timed_out": timed_out,
            "launch_error": launch_error,
            "zero_work_error": zero_work_error,
            "descendant_cleanup_performed": descendant_cleanup_performed,
            "container_cleanup_confirmed": container_cleanup_confirmed,
            "container_cleanup_error": container_cleanup_error,
            "raw_events_path": str(raw_path),
            "stderr_path": str(stderr_path),
            "candidate_root": str(candidate_root),
            "baseline_snapshot_sha256_after": source_snapshot_sha256(snapshot),
        }
        private_envelope_path = evidence_dir / "execution-private.json"
        execution["private_envelope_path"] = str(private_envelope_path)
        _write_exclusive(private_envelope_path, json.dumps(execution, sort_keys=True))
        _write_exclusive(
            evidence_dir / "run-record.json",
            json.dumps(_plain(validated.value), sort_keys=True),
        )
        return ExecutionResultEnvelope(_plain(validated.value), execution)

    @staticmethod
    def assert_zero_work(
        arm_id: str,
        before: ZeroWorkObservation,
        after: ZeroWorkObservation,
    ) -> None:
        new_processes = set(after.process_names) - set(before.process_names)
        new_paths = set(after.accessed_paths) - set(before.accessed_paths)
        if arm_id == "native+miller-lexical":
            forbidden_processes = {
                name
                for name in new_processes
                if "semantic" in name.casefold() or "ct-daemon" in name.casefold()
            }
            forbidden_paths = {
                path for path in new_paths if "vectors.db" in path or "/ct" in path
            }
            if forbidden_processes or forbidden_paths:
                raise RuntimeError(
                    "lexical arm performed semantic or continuous-testing work"
                )

    @staticmethod
    def public_record(record: Mapping[str, object]) -> Mapping[str, object]:
        allowed = {
            "contract_id",
            "campaign_sha256",
            "run_id",
            "task_id",
            "arm_id",
            "repetition",
            "order",
            "outcome",
            "verifier_evidence_sha256",
            "wall_time_seconds",
            "native_tool_counts",
            "miller_calls",
            "total_model_input_tokens",
            "total_model_cached_tokens",
            "total_model_output_tokens",
            "raw_event_sha256",
            "price_derived_cost",
        }
        if set(record) != allowed:
            raise ValueError("public run record must contain exactly the frozen fields")
        return _plain(validate_run_record(record).value)

    def parse_events(self, text: str) -> ParsedAgentEvents:
        counts: dict[str, int] = {}
        miller_calls = 0
        usage_totals: list[int] | None = None
        usage_complete = True
        reasoning_total: int | None = None
        answer = None
        unsupported = None
        failed = False
        completed_ids: set[str] = set()
        raw_lines = tuple(line for line in text.splitlines() if line)
        for line in raw_lines:
            try:
                event = json.loads(
                    line,
                    object_pairs_hook=_unique_json_object,
                    parse_constant=lambda value: (_ for _ in ()).throw(
                        ValueError(value)
                    ),
                )
            except (json.JSONDecodeError, ValueError):
                unsupported = unsupported or "malformed JSONL event"
                continue
            if not isinstance(event, dict):
                unsupported = unsupported or "JSONL event must be an object"
                continue
            event_type = event.get("type")
            if not isinstance(event_type, str):
                unsupported = unsupported or "event type must be a string"
                continue
            if event_type not in _EVENT_TYPES:
                unsupported = unsupported or f"unknown event type: {event_type}"
                continue
            if event_type == "item.completed":
                item = event.get("item", {})
                if not isinstance(item, dict):
                    unsupported = unsupported or "completed item must be an object"
                    continue
                item_id = item.get("id")
                if item_id is not None and not isinstance(item_id, str):
                    unsupported = unsupported or "item id must be a string"
                    continue
                if item_id is not None and item_id in completed_ids:
                    continue
                if item_id is not None:
                    completed_ids.add(item_id)
                item_type = item.get("type")
                if item_type == "command_execution":
                    if not isinstance(item.get("command"), str):
                        unsupported = (
                            unsupported or "command item must contain a string command"
                        )
                    else:
                        counts["command"] = counts.get("command", 0) + 1
                elif item_type == "file_change":
                    if not isinstance(item.get("changes"), list):
                        unsupported = (
                            unsupported
                            or "file change item must contain an array of changes"
                        )
                    else:
                        counts["edit"] = counts.get("edit", 0) + 1
                elif item_type == "mcp_tool_call":
                    if not isinstance(item.get("server"), str) or not isinstance(
                        item.get("tool"), str
                    ):
                        unsupported = (
                            unsupported
                            or "MCP item must contain string server and tool names"
                        )
                    elif item.get("server") == "miller":
                        miller_calls += 1
                elif item_type == "agent_message":
                    if not isinstance(item.get("text"), str):
                        unsupported = (
                            unsupported or "agent message must contain string text"
                        )
                    else:
                        answer = item["text"]
                elif item_type not in {"reasoning", "todo_list", "web_search"}:
                    unsupported = unsupported or f"unknown item type: {item_type}"
            elif event_type == "turn.completed":
                measured = event.get("usage")
                if measured is None:
                    usage_complete = False
                else:
                    if not isinstance(measured, dict):
                        unsupported = unsupported or "unsupported usage event"
                        usage_complete = False
                        continue
                    required = ("input_tokens", "cached_input_tokens", "output_tokens")
                    values = [measured.get(key) for key in required]
                    reasoning = measured.get("reasoning_output_tokens")
                    if not all(_nonnegative_int(value) for value in values) or (
                        reasoning is not None and not _nonnegative_int(reasoning)
                    ):
                        unsupported = unsupported or "unsupported usage event"
                        usage_complete = False
                    else:
                        if usage_totals is None:
                            usage_totals = [0, 0, 0]
                        for index, value in enumerate(values):
                            usage_totals[index] += value
                        if reasoning is not None:
                            reasoning_total = (reasoning_total or 0) + reasoning
            elif event_type in {"turn.failed", "error"}:
                failed = True
                usage_complete = False
        usage = (
            (None, None, None)
            if usage_totals is None or not usage_complete
            else tuple(usage_totals)
        )
        if not usage_complete:
            reasoning_total = None
        return ParsedAgentEvents(
            counts,
            miller_calls,
            usage,
            answer,
            unsupported,
            raw_lines,
            reasoning_total,
            failed,
        )

    def _arm(self, arm_id: str) -> Mapping[str, object]:
        for arm in self.campaign.value["arms"]:
            if arm["arm_id"] == arm_id:
                return arm
        raise ValueError(f"arm is not frozen in campaign: {arm_id}")

    def _runtime_arguments(
        self,
        workspace: Path,
        output_dir: Path,
        workspace_mode: str,
        runtime_dir: Path | None = None,
        arm_id: str | None = None,
        prepared_repository: PreparedRepositoryEnvironment | None = None,
    ) -> list[str]:
        if workspace_mode not in {"ro", "rw"}:
            raise ValueError("workspace mount mode is invalid")
        network_args = (
            ["--network=none"]
            if self.campaign.value["network_policy"] == "denied"
            else []
        )
        arguments = [
            "--userns=keep-id",
            "--security-opt=no-new-privileges",
            "--cap-drop=all",
            *network_args,
            "--memory",
            str(self.campaign.value["resource_limits"]["memory_bytes"]),
            "--mount",
            f"type=bind,src={workspace.resolve()},dst=/workspace,{workspace_mode},Z",
            "--mount",
            f"type=bind,src={output_dir.resolve()},dst=/run-results,rw,Z",
        ]
        if runtime_dir is not None:
            arguments.extend(
                [
                    "--mount",
                    f"type=bind,src={runtime_dir.resolve()},dst=/runtime,rw,Z",
                    "--mount",
                    f"type=bind,src={(runtime_dir / 'miller').resolve()},dst=/workspace/.miller,rw,Z",
                    "--mount",
                    f"type=bind,src={(runtime_dir / 'public-response-schema.json').resolve()},dst=/run-config/response-schema.json,ro,Z",
                    "--env",
                    "PYTHONDONTWRITEBYTECODE=1",
                    "--env",
                    "HOME=/runtime/home",
                    "--env",
                    "DOTNET_CLI_HOME=/runtime/dotnet-home",
                    "--env",
                    "NUGET_PACKAGES=/runtime/nuget",
                    "--env",
                    "CARGO_TARGET_DIR=/runtime/cargo-target",
                    "--env",
                    "GOCACHE=/runtime/go-build",
                    "--env",
                    "GOMODCACHE=/runtime/go-mod",
                    "--env",
                    "GRADLE_USER_HOME=/runtime/gradle",
                ]
            )
            if arm_id == "native":
                arguments.extend(
                    [
                        "--mount",
                        f"type=bind,src={(runtime_dir / 'native-miller-mask').resolve()},dst=/opt/miller,ro,Z",
                    ]
                )
            if prepared_repository is not None:
                arguments.extend(
                    _prepared_container_arguments(
                        prepared_repository,
                        runtime_dir,
                        workspace,
                    )
                )
        return arguments

    def _inspect_matches_runtime(
        self,
        inspect_text: str,
        workspace: Path,
        output_dir: Path,
        workspace_mode: str,
        runtime_dir: Path,
        arm_id: str,
        prepared_repository: PreparedRepositoryEnvironment | None = None,
    ) -> bool:
        try:
            documents = json.loads(inspect_text, object_pairs_hook=_unique_json_object)
            if (
                not isinstance(documents, list)
                or len(documents) != 1
                or not isinstance(documents[0], dict)
            ):
                return False
            mounts = documents[0].get("Mounts")
            host_config = documents[0].get("HostConfig")
            if not isinstance(mounts, list) or not isinstance(host_config, dict):
                return False
            image_digest = documents[0].get("ImageDigest")
            if (
                image_digest
                != "sha256:" + self.campaign.value["platform_toolchain_image_sha256"]
            ):
                return False
            network_mode = host_config.get("NetworkMode")
            expected_network = self.campaign.value["network_policy"]
            if expected_network == "denied" and network_mode != "none":
                return False
            if expected_network == "unrestricted" and network_mode in {
                None,
                "none",
                "host",
            }:
                return False
            if (
                host_config.get("Privileged") is not False
                or host_config.get("PidMode") == "host"
                or host_config.get("Memory")
                != self.campaign.value["resource_limits"]["memory_bytes"]
                or host_config.get("CapAdd") not in (None, [])
                or not _PODMAN_DEFAULT_CAPABILITIES.issubset(
                    set(host_config.get("CapDrop", []))
                )
                or "no-new-privileges" not in host_config.get("SecurityOpt", [])
            ):
                return False
            observed = {}
            for mount in mounts:
                if not isinstance(mount, dict):
                    return False
                destination = mount.get("Destination")
                source = mount.get("Source")
                read_write = mount.get("RW")
                if (
                    not isinstance(destination, str)
                    or not isinstance(source, str)
                    or not isinstance(read_write, bool)
                ):
                    return False
                observed[destination] = (Path(source).resolve(), read_write)
            expected = {
                "/workspace": (workspace.resolve(), workspace_mode == "rw"),
                "/run-results": (output_dir.resolve(), True),
                "/runtime": (runtime_dir.resolve(), True),
                "/workspace/.miller": ((runtime_dir / "miller").resolve(), True),
                "/run-config/response-schema.json": (
                    (runtime_dir / "public-response-schema.json").resolve(),
                    False,
                ),
            }
            if arm_id == "native":
                expected["/opt/miller"] = (
                    (runtime_dir / "native-miller-mask").resolve(),
                    False,
                )
            if prepared_repository is not None:
                for mount in prepared_repository.workspace_mounts:
                    expected["/workspace/" + mount["path"]] = (
                        (runtime_dir / "prepared-workspace" / mount["path"]).resolve(),
                        True,
                    )
            return observed == expected
        except (json.JSONDecodeError, ValueError, OSError):
            return False

    @staticmethod
    def _validate_experiment_paths(snapshot: Path, output_dir: Path) -> Path:
        if snapshot.name != "task-input" or output_dir.name != "agent-output":
            raise ValueError(
                "runner paths must use task-input and agent-output topology"
            )
        experiment_root = snapshot.parent.resolve(strict=True)
        if output_dir.parent.resolve(strict=True) != experiment_root:
            raise ValueError(
                "task input and agent output must share one experiment root"
            )
        if experiment_root in {
            Path("/"),
            Path.home().resolve(),
        } or experiment_root.parent == Path("/"):
            raise ValueError("experiment root is unsafe")
        if (
            snapshot.is_symlink()
            or experiment_root.is_symlink()
            or output_dir.parent.is_symlink()
        ):
            raise ValueError("experiment input and root cannot be symlinks")
        grader = experiment_root / "private-grader"
        if not grader.is_dir():
            raise ValueError(
                "private-grader directory is required outside agent mounts"
            )
        if grader.is_symlink() or grader.resolve(strict=True).parent != experiment_root:
            raise ValueError("private-grader must be a direct non-symlink child")
        if (
            output_dir.exists()
            or snapshot == output_dir
            or grader in snapshot.parents
            or snapshot in grader.parents
        ):
            raise ValueError("experiment input, output, and grader paths overlap")
        return experiment_root


def _plain(value):
    if isinstance(value, Mapping):
        return {key: _plain(item) for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return [_plain(item) for item in value]
    return value


def _toml_string(value: str) -> str:
    return json.dumps(value, ensure_ascii=False)


def _file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _path_sha256(path: Path) -> str:
    return hashlib.sha256(os.fsencode(path)).hexdigest()


def _open_exclusive(path: Path):
    descriptor = os.open(
        path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600
    )
    return os.fdopen(descriptor, "wb")


def _write_exclusive(path: Path, value: str) -> None:
    with _open_exclusive(path) as stream:
        stream.write(value.encode("utf-8"))


def _terminate_process_group(
    process_group_id: int, leader: subprocess.Popen | None
) -> bool:
    try:
        os.killpg(process_group_id, 0)
    except ProcessLookupError:
        return False
    try:
        os.killpg(process_group_id, signal.SIGTERM)
    except ProcessLookupError:
        return False
    if leader is not None:
        try:
            leader.wait(timeout=2)
        except subprocess.TimeoutExpired:
            pass
    deadline = time.monotonic() + 2
    while time.monotonic() < deadline:
        try:
            os.killpg(process_group_id, 0)
        except ProcessLookupError:
            return True
        time.sleep(0.02)
    try:
        os.killpg(process_group_id, signal.SIGKILL)
    except ProcessLookupError:
        pass
    if leader is not None:
        leader.wait()
    return True


def _cleanup_container(podman_path: str, cidfile: Path) -> tuple[bool, str]:
    try:
        if (
            cidfile.is_symlink()
            or not cidfile.is_file()
            or cidfile.stat().st_size > 128
        ):
            return False, "owned container id file is unsafe"
        container_id = cidfile.read_text(encoding="ascii").strip()
    except (OSError, UnicodeError):
        return False, "owned container id file is unreadable"
    if not re.fullmatch(r"[0-9a-f]{12,64}", container_id):
        return False, "owned container id is invalid"
    safe_env = {"PATH": os.defpath}
    errors = []
    for operation in (["stop", "--time", "2"], ["rm", "--force"]):
        try:
            completed = subprocess.run(
                [podman_path, *operation, container_id],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                check=False,
                timeout=10,
                env=safe_env,
            )
            if completed.returncode != 0:
                errors.append(completed.stderr[:4096].decode("utf-8", errors="replace"))
        except (OSError, subprocess.TimeoutExpired) as exc:
            errors.append(str(exc))
    try:
        exists = subprocess.run(
            [podman_path, "container", "exists", container_id],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            check=False,
            timeout=10,
            env=safe_env,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return False, "; ".join([*errors, str(exc)])
    if exists.returncode == 0:
        return False, "; ".join([*errors, "owned container still exists after cleanup"])
    if exists.returncode != 1:
        detail = exists.stderr[:4096].decode("utf-8", errors="replace")
        return False, "; ".join(
            [*errors, detail or f"container exists failed with {exists.returncode}"]
        )
    return True, "; ".join(error for error in errors if error)


def _run_bounded(argv: Sequence[str], limit: int, timeout_seconds: int) -> bytes:
    with (
        tempfile.TemporaryFile() as stdout_file,
        tempfile.TemporaryFile() as stderr_file,
    ):
        completed = subprocess.run(
            list(argv),
            stdout=stdout_file,
            stderr=stderr_file,
            check=False,
            timeout=timeout_seconds,
            env={"PATH": os.defpath},
        )
        stdout_file.seek(0, os.SEEK_END)
        size = stdout_file.tell()
        if size > limit:
            raise ValueError("bounded command output exceeds limit")
        stdout_file.seek(0)
        stderr_file.seek(0)
        output = stdout_file.read()
        if completed.returncode != 0:
            detail = stderr_file.read(4096).decode("utf-8", errors="replace")
            raise RuntimeError(
                f"bounded command failed with {completed.returncode}: {detail}"
            )
        return output


def _unique_json_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"duplicate JSON key: {key}")
        value[key] = item
    return value


def _nonnegative_int(value) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def _safe_relative_path(value, label: str) -> str:
    if not isinstance(value, str) or not value or "\\" in value:
        raise ValueError(f"{label} path is invalid")
    path = PurePosixPath(value)
    if path.is_absolute() or ".." in path.parts or "." in path.parts:
        raise ValueError(f"{label} path is invalid")
    return path.as_posix()


def _prepared_setting_is_safe(value: str, repo_id: str) -> bool:
    if any(ord(character) < 32 for character in value):
        return False
    if value.startswith("/"):
        return value.startswith(
            (f"/opt/agent-deps/{repo_id}/", "/runtime/", "/workspace/")
        )
    return value in {"1", "true", "off", "Major"}


def _prepared_container_arguments(
    repository: PreparedRepositoryEnvironment,
    runtime_root: Path,
    workspace_root: Path,
) -> list[str]:
    arguments = []
    for name, value in sorted(repository.environment.items()):
        arguments.extend(["--env", f"{name}={value}"])
    for mount in repository.workspace_mounts:
        workspace_path = workspace_root / mount["path"]
        if workspace_path.exists() or workspace_path.is_symlink():
            raise ValueError(
                f"prepared dependency mount would mask frozen source: {mount['path']}"
            )
        source = runtime_root / "prepared-workspace" / mount["path"]
        if not source.exists():
            raise ValueError(
                f"prepared dependency materialization is missing: {mount['path']}"
            )
        arguments.extend(
            [
                "--mount",
                f"type=bind,src={source.resolve()},dst=/workspace/{mount['path']},rw,Z",
            ]
        )
    return arguments


def _parse_result(answer: str | None) -> Mapping[str, object]:
    try:
        value = json.loads(
            answer or "",
            object_pairs_hook=_unique_json_object,
            parse_constant=lambda constant: (_ for _ in ()).throw(ValueError(constant)),
        )
    except (json.JSONDecodeError, ValueError):
        return {}
    return value if isinstance(value, dict) else {}
