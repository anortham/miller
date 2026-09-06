#!/usr/bin/env python3
import json
import os
import subprocess
import sys
from pathlib import Path


def mount_source(destination):
    mounts = [sys.argv[index + 1] for index, value in enumerate(sys.argv[:-1]) if value == "--mount"]
    mount = next(value for value in mounts if f"dst={destination}" in value)
    return Path(next(part[4:] for part in mount.split(",") if part.startswith("src=")))


script_path = Path(__file__)
marker = script_path.with_suffix(".agent-done")
container_state = script_path.with_suffix(".container")
container_id = "c" * 64
if len(sys.argv) > 1 and sys.argv[1] == "stop":
    raise SystemExit(0)
if len(sys.argv) > 1 and sys.argv[1] == "rm":
    container_state.unlink(missing_ok=True)
    raise SystemExit(0)
if len(sys.argv) > 1 and sys.argv[1] == "inspect":
    raise SystemExit(0 if container_state.exists() else 1)
if "--cidfile" in sys.argv:
    cidfile = Path(sys.argv[sys.argv.index("--cidfile") + 1])
    cidfile.write_text(container_id, encoding="ascii")
    container_state.write_text(container_id, encoding="ascii")
workspace = mount_source("/workspace")
if "--workdir" in sys.argv:
    if not marker.exists():
        raise SystemExit(91)
    image_index = next(index for index, value in enumerate(sys.argv) if "@sha256:" in value)
    argv = sys.argv[image_index + 1:]
    completed = subprocess.run(
        argv,
        cwd=workspace,
        env={"PATH": os.defpath, "PYTHONDONTWRITEBYTECODE": "1"},
        check=False,
    )
    raise SystemExit(completed.returncode)

output = mount_source("/run-results")
runtime = mount_source("/runtime")
source = workspace / "src" / "fixture.py"
if "value = 1" not in source.read_text(encoding="utf-8"):
    raise SystemExit(92)
if not any("value = 1" in line for line in source.read_text(encoding="utf-8").splitlines()):
    raise SystemExit(93)
source.write_text("value = 2\n", encoding="utf-8")
if "extra" in script_path.name:
    (workspace / "src" / "unexpected.py").write_text("unexpected = True\n", encoding="utf-8")
if "delete" in script_path.name:
    (workspace / "tests" / "test_fixture.py").unlink()
(runtime / "native-test-output.txt").write_text("passed", encoding="utf-8")
miller_runtime = mount_source("/workspace/.miller")
(miller_runtime / "vectors.db").write_text("sidecar", encoding="utf-8")
completed = subprocess.CompletedProcess([], 0, "", "") if "delete" in script_path.name else subprocess.run(
    [sys.executable, "-B", "-m", "unittest", "discover", "-s", "tests"],
    cwd=workspace,
    env={"PATH": os.defpath, "PYTHONDONTWRITEBYTECODE": "1"},
    capture_output=True,
    text=True,
    check=False,
)
if completed.returncode != 0:
    (output / "native-test-failure.txt").write_text(completed.stdout + completed.stderr, encoding="utf-8")
    raise SystemExit(completed.returncode)
marker.write_text("done", encoding="utf-8")
events = [
    {"type": "item.completed", "item": {"id": "read", "type": "command_execution", "command": "cat src/fixture.py"}},
    {"type": "item.completed", "item": {"id": "search", "type": "command_execution", "command": "rg 'value = 1' src"}},
    {"type": "item.completed", "item": {"id": "edit", "type": "file_change", "changes": ["src/fixture.py"]}},
    {"type": "item.completed", "item": {"id": "test", "type": "command_execution", "command": "python -m unittest"}},
    {"type": "item.completed", "item": {"id": "answer", "type": "agent_message", "text": "{}"}},
    {"type": "turn.completed", "usage": {"input_tokens": 5, "cached_input_tokens": 1, "output_tokens": 2, "reasoning_output_tokens": 1}},
]
sys.stdin.read()
for event in events:
    print(json.dumps(event, separators=(",", ":")), flush=True)
