#!/usr/bin/env python3
"""Run the local-RID package publish and native release smokes before GitHub Actions."""

from __future__ import annotations

import argparse
import json
import os
import platform
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

REQUIRED_TOOLS = {
    "search", "inspect", "context", "impact", "trace",
    "edit", "content", "workspace", "patterns", "tests",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Publish the local release RID and smoke the packaged native Miller binaries."
    )
    parser.add_argument("--version", help="Release version; defaults to Directory.Build.props.")
    parser.add_argument("--rid", help="Local release RID; defaults from the current OS and architecture.")
    parser.add_argument("--output-dir", type=Path, help="Artifact directory outside the source tree.")
    parser.add_argument("--skip-restore", action="store_true", help="Use already restored, verified pinned tools.")
    parser.add_argument(
        "--skip-semantic-smoke",
        action="store_true",
        help="Skip the prepared-model semantic payload smoke for an offline diagnostic run.",
    )
    parser.add_argument("--self-test", action="store_true", help=argparse.SUPPRESS)
    return parser.parse_args()


def release_version(repo: Path, requested: str | None) -> str:
    if requested:
        return requested.removeprefix("v")
    value = ET.parse(repo / "Directory.Build.props").findtext(".//Version")
    if not value:
        raise RuntimeError("Directory.Build.props has no Version value")
    return value.strip()


def local_rid(requested: str | None) -> str:
    system = platform.system()
    machine = platform.machine().lower()
    arch = "arm64" if machine in {"arm64", "aarch64"} else "x64" if machine in {"x86_64", "amd64"} else None
    supported = {
        ("Linux", "x64"): "linux-x64",
        ("Darwin", "x64"): "osx-x64",
        ("Darwin", "arm64"): "osx-arm64",
        ("Windows", "x64"): "win-x64",
    }
    if arch is None or (system, arch) not in supported:
        raise RuntimeError(f"the release matrix has no local RID for {system}/{machine}")
    detected = supported[(system, arch)]
    if requested and requested != detected:
        raise RuntimeError(f"--rid {requested} cannot be executed on this {detected} host")
    return detected


def default_output(version: str, rid: str) -> Path:
    if platform.system() == "Windows":
        base = Path(os.environ.get("LOCALAPPDATA", tempfile.gettempdir()))
    else:
        base = Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache"))
    return base / "miller-release-preflight" / f"{version}-{rid}"


def run_logged(label: str, command: list[str], repo: Path, output: Path) -> None:
    log = output / f"{label}.log"
    print(f"==> {label}")
    with log.open("w", encoding="utf-8") as stream:
        completed = subprocess.run(
            command, cwd=repo, stdout=stream, stderr=subprocess.STDOUT, text=True, check=False
        )
    if completed.returncode == 0:
        return
    tail = log.read_text(encoding="utf-8", errors="replace").splitlines()[-80:]
    if tail:
        print("\n".join(tail), file=sys.stderr)
    raise RuntimeError(f"{label} failed with exit code {completed.returncode}; see {log}")


def capture_logged(label: str, command: list[str], repo: Path, output: Path) -> str:
    log = output / f"{label}.log"
    print(f"==> {label}")
    completed = subprocess.run(
        command, cwd=repo, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, check=False
    )
    log.write_text(completed.stdout, encoding="utf-8")
    if completed.returncode != 0:
        tail = completed.stdout.splitlines()[-80:]
        if tail:
            print("\n".join(tail), file=sys.stderr)
        raise RuntimeError(f"{label} failed with exit code {completed.returncode}; see {log}")
    return completed.stdout.strip()


def require_version_output(label: str, output: str, expected: str, allow_build_metadata: bool = False) -> None:
    actual = output.strip()
    if allow_build_metadata:
        matched = actual == expected or actual.startswith(expected + "+")
    else:
        matched = bool(actual) and actual.split()[-1] == expected
    if not matched:
        raise RuntimeError(f"{label} version mismatch: expected {expected}, got {actual or '<empty>'}")


def require_ct_lifecycle(
    enabled: dict[str, Any],
    started: dict[str, Any],
    running: dict[str, Any],
    stopped: dict[str, Any],
    expected_miller_version: str,
) -> None:
    if enabled.get("changed_count") != 1 or enabled.get("enabled_count", 0) < 1 or enabled.get("unsupported_count") != 0:
        raise RuntimeError("packaged CT enable did not enable exactly the smoke project")
    if started.get("status") not in {"started", "replaced"} or started.get("publication", {}).get("readiness") != "ready":
        raise RuntimeError("packaged CT daemon did not start and publish ready state")
    daemon = running.get("daemon", {})
    if (
        running.get("enabled") is not True
        or daemon.get("running") is not True
        or daemon.get("state") != "running"
        or daemon.get("miller_version") != expected_miller_version
    ):
        raise RuntimeError("packaged CT status did not observe the running daemon")
    if stopped.get("status") != "stopped":
        raise RuntimeError("packaged CT daemon did not stop cleanly")


def self_test() -> None:
    require_version_output("Miller", "1.2.3+abc", "1.2.3", allow_build_metadata=True)
    require_version_output("julie-extract", "julie-extract 2.0.0", "2.0.0")
    require_ct_lifecycle(
        {"changed_count": 1, "enabled_count": 1, "unsupported_count": 0},
        {"status": "started", "publication": {"readiness": "ready"}},
        {"enabled": True, "daemon": {"running": True, "state": "running", "miller_version": "1.2.3+abc"}},
        {"status": "stopped"},
        "1.2.3+abc",
    )
    failures = [
        lambda: require_version_output("Miller", "1.2.2+abc", "1.2.3", allow_build_metadata=True),
        lambda: require_version_output("julie-extract", "julie-extract 1.9.9", "2.0.0"),
        lambda: require_ct_lifecycle(
            {"changed_count": 0, "enabled_count": 0, "unsupported_count": 1},
            {"status": "started", "publication": {"readiness": "ready"}},
            {"enabled": True, "daemon": {"running": True, "state": "running", "miller_version": "1.2.3+abc"}},
            {"status": "stopped"},
            "1.2.3+abc",
        ),
        lambda: require_ct_lifecycle(
            {"changed_count": 1, "enabled_count": 1, "unsupported_count": 0},
            {"status": "failed", "publication": {"readiness": "not_observed"}},
            {"enabled": True, "daemon": {"running": False, "state": "stopped", "miller_version": "1.2.3+abc"}},
            {"status": "failed"},
            "1.2.3+abc",
        ),
        lambda: require_ct_lifecycle(
            {"changed_count": 1, "enabled_count": 1, "unsupported_count": 0},
            {"status": "started", "publication": {"readiness": "ready"}},
            {"enabled": True, "daemon": {"running": True, "state": "running", "miller_version": "1.2.2+old"}},
            {"status": "stopped"},
            "1.2.3+abc",
        ),
    ]
    for guard in failures:
        try:
            guard()
        except RuntimeError:
            continue
        raise RuntimeError("release preflight negative self-test accepted an invalid result")
    incomplete = preflight_summary("1.2.3", "linux-x64", Path("/tmp/package"), complete=False)
    if incomplete["status"] != "incomplete_diagnostic" or incomplete["complete"] is not False:
        raise RuntimeError("skipped semantic smoke was reported as a complete preflight")


def preflight_summary(version: str, rid: str, package: Path, complete: bool) -> dict[str, Any]:
    return {
        "status": "passed" if complete else "incomplete_diagnostic",
        "complete": complete,
        "version": version,
        "rid": rid,
        "package_root": str(package),
        "skipped_checks": [] if complete else ["packaged_semantic_smoke"],
    }


def restore(repo: Path, output: Path) -> None:
    run_logged("restore-solution", ["dotnet", "restore", "Miller.slnx"], repo, output)
    run_logged(
        "restore-semantic-smoke-project",
        ["dotnet", "restore", "scripts/Miller.PackageSemanticSmoke/Miller.PackageSemanticSmoke.csproj"],
        repo,
        output,
    )
    if platform.system() == "Windows":
        run_logged("restore-julie", ["pwsh", "-File", "scripts/restore-julie-extract.ps1"], repo, output)
        run_logged("restore-semantic", ["pwsh", "-File", "scripts/restore-semantic-sidecar.ps1"], repo, output)
    else:
        run_logged("restore-julie", ["bash", "scripts/restore-julie-extract.sh"], repo, output)
        run_logged("restore-semantic", ["bash", "scripts/restore-semantic-sidecar.sh"], repo, output)


def publish(repo: Path, output: Path, version: str, rid: str) -> Path:
    package = output / "package"
    dashboard = package / "dashboard"
    common = ["-c", "Release", "-r", rid, "--self-contained", "true", f"-p:Version={version}"]
    run_logged(
        "publish-server-aot",
        [
            "dotnet", "publish", "src/Miller.Server/Miller.Server.csproj", *common,
            "-p:PublishAot=true", "-p:JsonSerializerIsReflectionEnabledByDefault=false", "-o", str(package),
        ],
        repo,
        output,
    )
    run_logged(
        "publish-dashboard-single-file",
        [
            "dotnet", "publish", "src/Miller.Dashboard/Miller.Dashboard.csproj", *common,
            "-p:PublishSingleFile=true", "-p:PublishTrimmed=false", "-o", str(dashboard),
        ],
        repo,
        output,
    )
    shutil.rmtree(dashboard / ".tools", ignore_errors=True)
    return package


def require_payload(repo: Path, package: Path, rid: str) -> tuple[Path, Path, Path]:
    windows = rid.startswith("win-")
    miller = package / ("miller.exe" if windows else "miller")
    extractor = package / ".tools" / ("julie-extract.exe" if windows else "julie-extract")
    sidecar = package / ".tools" / "julie-semantic-sidecar-runtime" / (
        "julie-semantic-sidecar.exe" if windows else "julie-semantic-sidecar"
    )
    dashboard = package / "dashboard" / ("Miller.Dashboard.exe" if windows else "Miller.Dashboard")
    semantic_pins = json.loads((repo / "scripts/semantic-pins.json").read_text(encoding="utf-8"))
    vec_member = semantic_pins["sqliteVec"]["assets"][rid]["member"]
    required = [
        miller,
        extractor,
        sidecar,
        dashboard,
        package / "dashboard/wwwroot/dashboard.css",
        package / "dashboard/wwwroot/fonts/archivo-latin.woff2",
        package / "dashboard/wwwroot/fonts/jetbrains-mono-latin.woff2",
        package / "dashboard/wwwroot/js/theme-init.js",
        package / "dashboard/wwwroot/js/dashboard-site.js",
        package / "dashboard/wwwroot/lib/htmx/htmx.min.js",
        package / "dashboard/wwwroot/lib/idiomorph/idiomorph-ext.min.js",
        package / ".tools" / vec_member,
    ]
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise RuntimeError("packaged payload is incomplete:\n" + "\n".join(missing))
    if (package / "dashboard/.tools").exists():
        raise RuntimeError("dashboard package contains a duplicate .tools directory")
    wrong_extractor = package / ".tools" / ("julie-extract" if windows else "julie-extract.exe")
    wrong_sidecar = package / ".tools/julie-semantic-sidecar-runtime" / (
        "julie-semantic-sidecar" if windows else "julie-semantic-sidecar.exe"
    )
    if wrong_extractor.exists() or wrong_sidecar.exists():
        raise RuntimeError("packaged tools contain executables for the wrong operating system")
    return miller, extractor, sidecar


class McpClient:
    def __init__(self, executable: Path, cwd: Path, home: Path) -> None:
        environment = os.environ.copy()
        environment["MILLER_HOME"] = str(home)
        environment["MILLER_SEMANTIC"] = "off"
        self.process = subprocess.Popen(
            [str(executable), "serve"],
            cwd=cwd,
            env=environment,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
        )
        self.responses: queue.Queue[dict[str, Any]] = queue.Queue()
        self.stderr: list[str] = []
        self.next_id = 1
        threading.Thread(target=self._read_stdout, daemon=True).start()
        threading.Thread(target=self._read_stderr, daemon=True).start()

    def _read_stdout(self) -> None:
        assert self.process.stdout is not None
        for line in self.process.stdout:
            try:
                self.responses.put(json.loads(line))
            except json.JSONDecodeError:
                continue

    def _read_stderr(self) -> None:
        assert self.process.stderr is not None
        self.stderr.extend(line.rstrip() for line in self.process.stderr)

    def send(self, method: str, params: dict[str, Any], timeout: float = 120) -> dict[str, Any]:
        request_id = self.next_id
        self.next_id += 1
        self._write({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
        deadline = time.monotonic() + timeout
        deferred: list[dict[str, Any]] = []
        try:
            while time.monotonic() < deadline:
                try:
                    message = self.responses.get(timeout=max(0.001, min(0.2, deadline - time.monotonic())))
                except queue.Empty:
                    if self.process.poll() is not None:
                        raise RuntimeError(f"Miller exited early ({self.process.returncode}): {' '.join(self.stderr[-30:])}")
                    continue
                if message.get("id") == request_id:
                    if "error" in message:
                        raise RuntimeError(f"MCP {method} failed: {message['error']}")
                    return message
                deferred.append(message)
        finally:
            for message in deferred:
                self.responses.put(message)
        raise TimeoutError(f"timed out waiting for MCP {method}: {' '.join(self.stderr[-30:])}")

    def notify(self, method: str) -> None:
        self._write({"jsonrpc": "2.0", "method": method, "params": {}})

    def _write(self, message: dict[str, Any]) -> None:
        assert self.process.stdin is not None
        self.process.stdin.write(json.dumps(message, separators=(",", ":")) + "\n")
        self.process.stdin.flush()

    def tool(self, name: str, arguments: dict[str, Any], timeout: float = 120) -> dict[str, Any]:
        response = self.send("tools/call", {"name": name, "arguments": arguments}, timeout)
        result = response.get("result", {})
        if result.get("isError"):
            raise RuntimeError(f"MCP tool {name} failed: {json.dumps(result, sort_keys=True)}")
        return result

    def close(self) -> None:
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)


def content_json(result: dict[str, Any]) -> dict[str, Any]:
    for item in result.get("content", []):
        if item.get("type") == "text":
            try:
                value = json.loads(item.get("text", ""))
            except json.JSONDecodeError:
                continue
            if isinstance(value, dict):
                return value
    raise RuntimeError(f"MCP tool result had no JSON object content: {json.dumps(result, sort_keys=True)}")


def native_mcp_smoke(miller: Path, expected_miller_version: str, output: Path) -> None:
    print("==> native-mcp-ct-smoke")
    with tempfile.TemporaryDirectory(prefix="miller-release-preflight-") as temp:
        root = Path(temp) / "workspace"
        home = Path(temp) / "home"
        root.mkdir()
        home.mkdir()
        project = root / "Smoke.Tests.csproj"
        project.write_text(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
            "</PropertyGroup><ItemGroup><PackageReference Include=\"xunit.v3\" Version=\"3.2.1\" />"
            "</ItemGroup></Project>",
            encoding="utf-8",
        )
        client = McpClient(miller, root, home)
        transcript: dict[str, Any] = {}
        workspace_id: str | None = None
        daemon_started = False
        try:
            transcript["initialize"] = client.send(
                "initialize",
                {
                    "protocolVersion": "2025-03-26",
                    "capabilities": {},
                    "clientInfo": {"name": "release-preflight", "version": "1"},
                },
            )
            client.notify("notifications/initialized")
            listed = client.send("tools/list", {})
            names = {tool.get("name") for tool in listed.get("result", {}).get("tools", [])}
            missing = sorted(REQUIRED_TOOLS - names)
            if missing:
                raise RuntimeError("packaged MCP tools/list omitted: " + ", ".join(missing))
            transcript["tools"] = sorted(names)
            opened = content_json(client.tool(
                "workspace", {"operation": "open", "path": str(root), "format": "json"}
            ))
            workspace_id = opened.get("workspace_id")
            if not workspace_id:
                raise RuntimeError(f"workspace open returned no workspace_id: {opened}")
            transcript["workspace_id"] = workspace_id
            transcript["status_before"] = content_json(client.tool(
                "tests", {"operation": "status", "workspace_id": workspace_id, "format": "json"}, timeout=180
            ))
            enabled = content_json(client.tool(
                "tests",
                {"operation": "enable", "workspace_id": workspace_id, "project": project.name, "format": "json"},
                timeout=180,
            ))
            start_result = client.tool(
                "tests", {"operation": "start", "workspace_id": workspace_id, "format": "json"}, timeout=180
            )
            daemon_started = True
            started = content_json(start_result)
            running = content_json(client.tool(
                "tests", {"operation": "status", "workspace_id": workspace_id, "format": "json"}, timeout=180
            ))
            stopped = content_json(client.tool(
                "tests", {"operation": "stop", "workspace_id": workspace_id, "format": "json"}, timeout=180
            ))
            require_ct_lifecycle(enabled, started, running, stopped, expected_miller_version)
            transcript["enable"] = enabled
            transcript["start"] = started
            transcript["status_started"] = running
            transcript["stop"] = stopped
            daemon_started = False
        finally:
            if daemon_started and workspace_id:
                try:
                    client.tool(
                        "tests", {"operation": "stop", "workspace_id": workspace_id, "format": "json"}, timeout=30
                    )
                except (OSError, RuntimeError, TimeoutError):
                    pass
            client.close()
            transcript["stderr"] = client.stderr
            (output / "native-mcp-ct-smoke.json").write_text(
                json.dumps(transcript, indent=2, sort_keys=True) + "\n", encoding="utf-8"
            )


def main() -> int:
    args = parse_args()
    if args.self_test:
        self_test()
        print("PASS: release preflight guard self-test")
        return 0
    repo = Path(__file__).resolve().parent.parent
    version = release_version(repo, args.version)
    rid = local_rid(args.rid)
    output = (args.output_dir or default_output(version, rid)).expanduser().resolve()
    if output == repo or repo in output.parents:
        raise RuntimeError("--output-dir must be outside the source tree")
    if output.exists():
        if not (output / ".miller-release-preflight").is_file():
            raise RuntimeError(f"refusing to replace unowned output directory: {output}")
        shutil.rmtree(output)
    output.mkdir(parents=True)
    (output / ".miller-release-preflight").write_text("owned\n", encoding="utf-8")
    print(f"release preflight {version} ({rid}) -> {output}")
    if not args.skip_restore:
        restore(repo, output)
    package = publish(repo, output, version, rid)
    miller, extractor, sidecar = require_payload(repo, package, rid)
    julie_version = json.loads((repo / "scripts/julie-pins.json").read_text(encoding="utf-8"))["version"]
    sidecar_version = json.loads((repo / "scripts/semantic-pins.json").read_text(encoding="utf-8"))["sidecar"]["version"]
    miller_output = capture_logged("packaged-miller-version", [str(miller), "version"], repo, output)
    julie_output = capture_logged("packaged-julie-version", [str(extractor), "--version"], repo, output)
    sidecar_output = capture_logged("packaged-sidecar-version", [str(sidecar), "--version"], repo, output)
    require_version_output("Miller", miller_output, version, allow_build_metadata=True)
    require_version_output("julie-extract", julie_output, julie_version)
    require_version_output("semantic sidecar", sidecar_output, sidecar_version)
    native_mcp_smoke(miller, miller_output, output)
    if not args.skip_semantic_smoke:
        try:
            run_logged(
                "packaged-semantic-smoke",
                [
                    "dotnet", "run", "--project", "scripts/Miller.PackageSemanticSmoke/Miller.PackageSemanticSmoke.csproj",
                    "-c", "Release", "--no-restore", "--", "--package-root", str(package),
                ],
                repo,
                output,
            )
        except RuntimeError as error:
            raise RuntimeError(
                f"{error}. Prepare Miller's active semantic model before the release preflight."
            ) from error
    complete = not args.skip_semantic_smoke
    summary = preflight_summary(version, rid, package, complete)
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    verdict = "PASS: local release preflight" if complete else "INCOMPLETE: semantic smoke skipped"
    print(f"{verdict} ({output / 'summary.json'})")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, subprocess.SubprocessError, TimeoutError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        raise SystemExit(1)
