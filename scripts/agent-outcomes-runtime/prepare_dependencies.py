#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import os
import posixpath
import shutil
import stat
import subprocess
import tempfile
import time
from pathlib import Path

ASSET_ROOT = Path(__file__).resolve().parent


PREPARE_COMMANDS = {
    "flask": (
        {
            "UV_PROJECT_ENVIRONMENT": "/opt/agent-deps/flask/workspace/.venv",
            "UV_CACHE_DIR": "/opt/agent-deps/flask/uv-cache",
        },
        "uv sync --group tests --no-install-project",
        [],
    ),
    "express": (
        {"npm_config_cache": "/opt/agent-deps/express/npm-cache"},
        "npm install --no-package-lock --ignore-scripts && mkdir -p /opt/agent-deps/express/workspace && mv node_modules /opt/agent-deps/express/workspace/node_modules",
        [{"path": "node_modules", "seed_path": "workspace/node_modules"}],
    ),
    "chi": (
        {
            "GOMODCACHE": "/opt/agent-deps/chi/go-mod",
            "GOCACHE": "/opt/agent-deps/chi/go-build",
        },
        "go mod download",
        [],
    ),
    "ripgrep": (
        {"CARGO_HOME": "/opt/agent-deps/ripgrep/cargo-home"},
        "cargo fetch --locked",
        [],
    ),
    "command-line-api": (
        {"NUGET_PACKAGES": "/opt/agent-deps/command-line-api/nuget"},
        (
            "dotnet restore src/System.CommandLine.Tests/System.CommandLine.Tests.csproj --nologo && "
            "mkdir -p /opt/agent-deps/command-line-api/workspace/artifacts && "
            "cp -a artifacts/obj /opt/agent-deps/command-line-api/workspace/artifacts/obj && "
            "mkdir -p /opt/agent-deps/command-line-api/workspace/artifacts/bin"
        ),
        [
            {"path": "artifacts/obj", "seed_path": "workspace/artifacts/obj"},
            {"path": "artifacts/bin", "seed_path": "workspace/artifacts/bin"},
        ],
    ),
    "rake": (
        {"BUNDLE_PATH": "/opt/agent-deps/rake/bundle"},
        "bundle install && mkdir -p /opt/agent-deps/rake/workspace && cp Gemfile.lock /opt/agent-deps/rake/workspace/Gemfile.lock",
        [{"path": "Gemfile.lock", "seed_path": "workspace/Gemfile.lock"}],
    ),
}


RUNTIME_ENVIRONMENT = {
    "flask": {
        "PATH": "/opt/agent-deps/flask/workspace/.venv/bin:/usr/bin:/bin",
        "PYTHONPATH": "/workspace/src",
        "UV_PROJECT_ENVIRONMENT": "/opt/agent-deps/flask/workspace/.venv",
        "UV_CACHE_DIR": "/opt/agent-deps/flask/uv-cache",
        "UV_OFFLINE": "1",
    },
    "express": {
        "PATH": "/workspace/node_modules/.bin:/usr/bin:/bin",
        "NODE_PATH": "/workspace/node_modules",
        "npm_config_cache": "/opt/agent-deps/express/npm-cache",
        "npm_config_offline": "true",
    },
    "chi": {
        "GOMODCACHE": "/opt/agent-deps/chi/go-mod",
        "GOCACHE": "/runtime/go-build",
        "GOPROXY": "off",
        "GOSUMDB": "off",
    },
    "ripgrep": {
        "CARGO_HOME": "/opt/agent-deps/ripgrep/cargo-home",
        "CARGO_TARGET_DIR": "/runtime/cargo-target",
        "CARGO_NET_OFFLINE": "true",
    },
    "command-line-api": {
        "DOTNET_ROLL_FORWARD": "Major",
        "NUGET_PACKAGES": "/opt/agent-deps/command-line-api/nuget",
        "RestoreIgnoreFailedSources": "true",
    },
    "rake": {"BUNDLE_PATH": "/opt/agent-deps/rake/bundle", "BUNDLE_FROZEN": "true"},
}


def run(argv, *, log: Path, timeout=1800):
    started = time.monotonic()
    with log.open("wb") as output:
        completed = subprocess.run(
            argv,
            stdout=output,
            stderr=subprocess.STDOUT,
            check=False,
            timeout=timeout,
            env={"PATH": os.defpath},
        )
    if completed.returncode != 0:
        raise RuntimeError(
            f"dependency preparation failed with {completed.returncode}; see {log}"
        )
    return time.monotonic() - started


def artifact_manifest(root: Path, repo_id: str):
    entries = []
    for path in sorted(root.rglob("*")):
        relative = path.relative_to(root).as_posix()
        mode = path.lstat().st_mode
        if stat.S_ISLNK(mode):
            target = os.readlink(path)
            if Path(target).is_absolute():
                virtual_target = posixpath.normpath(target)
            else:
                virtual_parent = (
                    "/opt/agent-deps/"
                    + repo_id
                    + "/"
                    + path.parent.relative_to(root).as_posix()
                )
                virtual_target = posixpath.normpath(
                    posixpath.join(virtual_parent, target)
                )
            if not any(
                virtual_target == prefix or virtual_target.startswith(prefix + "/")
                for prefix in (
                    f"/opt/agent-deps/{repo_id}",
                    "/usr/bin",
                    "/usr/lib",
                    "/usr/lib64",
                )
            ):
                raise ValueError(f"prepared dependency symlink is unsafe: {relative}")
            data = os.fsencode(target)
            kind = "symlink"
        elif stat.S_ISREG(mode):
            data = path.read_bytes()
            kind = "file"
        elif stat.S_ISDIR(mode):
            continue
        else:
            raise ValueError(f"unsupported prepared dependency entry: {relative}")
        entries.append(
            {
                "path": relative,
                "kind": kind,
                "sha256": hashlib.sha256(data).hexdigest(),
                "size_bytes": len(data),
            }
        )
    return entries


def workspace_mount_manifest(destination: Path, configured):
    if configured != "auto":
        return configured
    workspace = destination / "workspace"
    mounts = []
    for path in sorted(workspace.rglob("*")):
        if path.is_dir() and path.name in {"bin", "obj"}:
            relative = path.relative_to(workspace).as_posix()
            mounts.append({"path": relative, "seed_path": "workspace/" + relative})
    return mounts


def prepare(
    repositories_path: Path, base_image: str, evidence_directory: Path, output_tag: str
):
    repositories = json.loads(repositories_path.read_text(encoding="utf-8"))
    expected = set(PREPARE_COMMANDS)
    if {record["repo_id"] for record in repositories} != expected:
        raise ValueError("repository manifest does not match six prepared environments")
    evidence = evidence_directory.resolve()
    if evidence.exists():
        raise ValueError("dependency evidence directory must be new")
    evidence.mkdir(mode=0o700, parents=True)
    with tempfile.TemporaryDirectory(prefix="agent-outcomes-deps-") as directory:
        working = Path(directory)
        prepared = working / "prepared"
        prepared.mkdir()
        setup = []
        manifest_repositories = []
        for record in sorted(repositories, key=lambda value: value["repo_id"]):
            repo_id = record["repo_id"]
            source = working / "sources" / repo_id
            source.parent.mkdir(exist_ok=True)
            clone_log = evidence / f"{repo_id}-clone.log"
            clone_seconds = run(
                ["git", "clone", "--quiet", record["upstream"], str(source)],
                log=clone_log,
            )
            checkout_log = evidence / f"{repo_id}-checkout.log"
            checkout_seconds = run(
                [
                    "git",
                    "-C",
                    str(source),
                    "checkout",
                    "--quiet",
                    "--detach",
                    record["commit"],
                ],
                log=checkout_log,
            )
            environment, command, configured_mounts = PREPARE_COMMANDS[repo_id]
            destination = prepared / repo_id
            destination.mkdir()
            prepare_log = evidence / f"{repo_id}-prepare.log"
            podman = [
                "podman",
                "run",
                "--rm",
                "--network=pasta",
                "--userns=keep-id",
                "--security-opt=no-new-privileges",
                "--cap-drop=all",
                "--mount",
                f"type=bind,src={source.resolve()},dst=/source,rw,Z",
                "--mount",
                f"type=bind,src={destination.resolve()},dst=/opt/agent-deps/{repo_id},rw,Z",
                "--workdir",
                "/source",
            ]
            for name, value in environment.items():
                podman.extend(["--env", f"{name}={value}"])
            prepare_seconds = run(
                [*podman, base_image, "sh", "-c", command], log=prepare_log
            )
            artifacts = artifact_manifest(destination, repo_id)
            workspace_mounts = workspace_mount_manifest(destination, configured_mounts)
            manifest_repositories.append(
                {
                    "repo_id": repo_id,
                    "environment": RUNTIME_ENVIRONMENT[repo_id],
                    "workspace_mounts": workspace_mounts,
                    "artifacts": artifacts,
                }
            )
            setup.append(
                {
                    "repo_id": repo_id,
                    "clone_seconds": clone_seconds,
                    "checkout_seconds": checkout_seconds,
                    "prepare_seconds": prepare_seconds,
                    "download_bytes": None,
                    "download_seconds": None,
                }
            )
        base_digest = base_image.rsplit("sha256:", 1)[-1]
        manifest = {
            "schema": "agent-outcomes-prepared-environments-v1",
            "base_image_digest": base_digest,
            "repositories": manifest_repositories,
        }
        manifest_json = json.dumps(manifest, sort_keys=True, separators=(",", ":"))
        manifest_sha = hashlib.sha256(manifest_json.encode()).hexdigest()
        (prepared / "manifest.json").write_text(manifest_json, encoding="utf-8")
        context = working / "context"
        context.mkdir()
        shutil.copy2(
            ASSET_ROOT / "Containerfile.dependencies", context / "Containerfile"
        )
        shutil.copytree(prepared, context / "prepared", symlinks=True)
        build_log = evidence / "dependency-image-build.log"
        build_seconds = run(
            [
                "podman",
                "build",
                "--quiet",
                "--pull=never",
                "--network=none",
                "--format=oci",
                "--build-arg",
                f"BASE_IMAGE={base_image}",
                "--tag",
                output_tag,
                str(context),
            ],
            log=build_log,
        )
    image_identity = (
        subprocess.run(
            ["podman", "image", "inspect", output_tag, "--format", "{{.Digest}}"],
            capture_output=True,
            text=True,
            check=True,
            env={"PATH": os.defpath},
        )
        .stdout.strip()
        .removeprefix("sha256:")
    )
    binding = {
        "schema": "agent-outcomes-prepared-image-binding-v1",
        "image_digest": image_identity,
        "content_manifest_sha256": manifest_sha,
        "content_manifest": manifest,
    }
    binding_json = json.dumps(binding, sort_keys=True, separators=(",", ":"))
    binding_sha = hashlib.sha256(binding_json.encode()).hexdigest()
    binding_path = evidence / f"prepared-binding-{binding_sha}.json"
    binding_path.write_text(binding_json, encoding="utf-8")
    setup_record = {
        "schema": "agent-outcomes-prepared-setup-v1",
        "manifest_sha256": manifest_sha,
        "image_digest": image_identity,
        "build_seconds": build_seconds,
        "repositories": setup,
    }
    setup_json = json.dumps(setup_record, sort_keys=True, separators=(",", ":"))
    setup_sha = hashlib.sha256(setup_json.encode()).hexdigest()
    setup_path = evidence / f"prepared-setup-{setup_sha}.json"
    setup_path.write_text(setup_json, encoding="utf-8")
    manifest_path = evidence / f"prepared-manifest-{manifest_sha}.json"
    manifest_path.write_text(manifest_json, encoding="utf-8")
    return {
        "image_digest": image_identity,
        "manifest_sha256": manifest_sha,
        "manifest_path": str(manifest_path),
        "binding_sha256": binding_sha,
        "binding_path": str(binding_path),
        "setup_sha256": setup_sha,
        "setup_path": str(setup_path),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--repositories", type=Path, required=True)
    parser.add_argument("--base-image", required=True)
    parser.add_argument("--evidence-directory", type=Path, required=True)
    parser.add_argument("--tag", default="localhost/miller-agent-outcomes:prepared")
    arguments = parser.parse_args()
    print(
        json.dumps(
            prepare(
                arguments.repositories,
                arguments.base_image,
                arguments.evidence_directory,
                arguments.tag,
            ),
            sort_keys=True,
        )
    )


if __name__ == "__main__":
    main()
