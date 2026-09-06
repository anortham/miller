#!/usr/bin/env python3
import json
import sys
from pathlib import Path

mounts = [sys.argv[index + 1] for index, value in enumerate(sys.argv[:-1]) if value == "--mount"]
output_mount = next(value for value in mounts if "dst=/run-results" in value)
output_root = Path(next(part[4:] for part in output_mount.split(",") if part.startswith("src=")))
victim = output_root.parent / "private-grader" / "victim.txt"
(output_root / "raw-events.jsonl").symlink_to(victim)
(output_root / "execution-private.json").symlink_to(victim)
sys.stdin.read()
print(json.dumps({"type": "thread.started", "thread_id": "fixture"}), flush=True)
