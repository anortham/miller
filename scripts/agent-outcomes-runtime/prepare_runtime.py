#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
from collections.abc import Sequence
from pathlib import Path

ASSET_ROOT = Path(__file__).resolve().parent
REPOSITORY_ROOT = ASSET_ROOT.parents[1]
SHA256 = re.compile(r"^[0-9a-f]{64}$")
TAG = re.compile(r"^[a-z0-9][a-z0-9._/-]*:[a-z0-9][a-z0-9._-]*$")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def tree_manifest(root: Path) -> list[dict[str, object]]:
    entries = []
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise ValueError(f"runtime input cannot contain symlinks: {path}")
        if path.is_file():
            entries.append(
                {
                    "path": path.relative_to(root).as_posix(),
                    "sha256": sha256_file(path),
                    "size_bytes": path.stat().st_size,
                }
            )
    return entries


def run_checked(argv: Sequence[str]) -> str:
    completed = subprocess.run(
        list(argv),
        capture_output=True,
        text=True,
        check=False,
        timeout=1800,
        env={"PATH": os.defpath},
    )
    if completed.returncode != 0:
        raise RuntimeError(
            f"command failed with {completed.returncode}: {completed.stderr[-4096:]}"
        )
    return completed.stdout


def prepare(
    codex_binary: Path, miller_directory: Path, evidence_directory: Path, tag: str
) -> dict[str, object]:
    codex = codex_binary.resolve(strict=True)
    miller = miller_directory.resolve(strict=True)
    evidence = evidence_directory.resolve()
    if not codex.is_file() or codex.is_symlink() or not os.access(codex, os.X_OK):
        raise ValueError("Codex input must be a non-symlink executable")
    if not miller.is_dir() or miller.is_symlink() or not (miller / "miller").is_file():
        raise ValueError("Miller input must be a release directory")
    if evidence == REPOSITORY_ROOT or REPOSITORY_ROOT in evidence.parents:
        raise ValueError("raw runtime evidence must be outside the repository")
    if evidence.exists():
        raise ValueError("evidence directory must not already exist")
    if not TAG.fullmatch(tag):
        raise ValueError("local image tag is invalid")
    codex_version = run_checked([str(codex), "--version"]).strip()
    if codex_version != "codex-cli 0.153.4":
        raise ValueError(f"unsupported Codex runtime: {codex_version}")
    miller_version = run_checked([str(miller / "miller"), "--version"]).strip()
    miller_files = tree_manifest(miller)
    evidence.mkdir(mode=0o700, parents=True)
    started = time.monotonic()
    with tempfile.TemporaryDirectory(prefix="agent-outcomes-image-") as directory:
        context = Path(directory)
        shutil.copy2(ASSET_ROOT / "Containerfile", context / "Containerfile")
        shutil.copy2(codex, context / "codex")
        shutil.copytree(miller, context / "miller")
        build_output = run_checked(
            [
                "podman",
                "build",
                "--quiet",
                "--pull=never",
                "--network=pasta",
                "--format=oci",
                "--tag",
                tag,
                str(context),
            ]
        )
    build_seconds = time.monotonic() - started
    (evidence / "podman-build.stdout").write_text(build_output, encoding="utf-8")
    identity = (
        run_checked(
            ["podman", "image", "inspect", tag, "--format", "{{.Digest}} {{.Id}}"]
        )
        .strip()
        .split()
    )
    candidates = [value.removeprefix("sha256:") for value in identity]
    image_digest = next(
        (value for value in candidates if SHA256.fullmatch(value)), None
    )
    if image_digest is None:
        raise RuntimeError("built image has no immutable SHA-256 identity")
    package_inventory = run_checked(
        [
            "podman",
            "run",
            "--rm",
            "--network=none",
            tag,
            "rpm",
            "-qa",
            "--qf",
            "%{NAME} %{EVR} %{ARCH}\\n",
        ]
    )
    package_inventory = (
        "\n".join(sorted(line for line in package_inventory.splitlines() if line))
        + "\n"
    )
    package_inventory_path = evidence / "rpm-packages.txt"
    package_inventory_path.write_text(package_inventory, encoding="utf-8")
    manifest = {
        "schema": "agent-outcomes-runtime-v1",
        "base_image_digest": "b013b98e4f4c43b46fb59b71b9d3d1f4f33df503ff84b1ea3415cafc32ead87c",
        "codex": {
            "version": codex_version,
            "sha256": sha256_file(codex),
            "size_bytes": codex.stat().st_size,
        },
        "miller": {
            "version": miller_version,
            "files": miller_files,
            "tree_sha256": hashlib.sha256(
                json.dumps(miller_files, sort_keys=True, separators=(",", ":")).encode()
            ).hexdigest(),
        },
        "image": {"tag": tag, "digest": image_digest},
        "packages": {
            "count": len(package_inventory.splitlines()),
            "sha256": sha256_file(package_inventory_path),
            "inventory_file": package_inventory_path.name,
        },
        "build": {
            "network_policy": "pasta-package-setup-only",
            "containerfile_sha256": sha256_file(ASSET_ROOT / "Containerfile"),
        },
    }
    canonical = json.dumps(manifest, sort_keys=True, separators=(",", ":"))
    manifest_digest = hashlib.sha256(canonical.encode()).hexdigest()
    manifest_path = evidence / f"runtime-manifest-{manifest_digest}.json"
    manifest_path.write_text(canonical, encoding="utf-8")
    setup = {
        "schema": "agent-outcomes-runtime-setup-v1",
        "runtime_manifest_sha256": manifest_digest,
        "build_seconds": build_seconds,
        "download_bytes": None,
        "download_seconds": None,
    }
    setup_json = json.dumps(setup, sort_keys=True, separators=(",", ":"))
    setup_digest = hashlib.sha256(setup_json.encode()).hexdigest()
    setup_path = evidence / f"setup-evidence-{setup_digest}.json"
    setup_path.write_text(setup_json, encoding="utf-8")
    return {
        **manifest,
        "manifest_sha256": manifest_digest,
        "manifest_path": str(manifest_path),
        "setup_evidence_sha256": setup_digest,
        "setup_evidence_path": str(setup_path),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--codex-binary", type=Path, required=True)
    parser.add_argument("--miller-directory", type=Path, required=True)
    parser.add_argument("--evidence-directory", type=Path, required=True)
    parser.add_argument(
        "--tag", default="localhost/miller-agent-outcomes:prequalification"
    )
    arguments = parser.parse_args()
    result = prepare(
        arguments.codex_binary,
        arguments.miller_directory,
        arguments.evidence_directory,
        arguments.tag,
    )
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
