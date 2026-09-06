#!/usr/bin/env python3
import subprocess
import sys
import time
from pathlib import Path

mounts = [sys.argv[index + 1] for index, value in enumerate(sys.argv[:-1]) if value == "--mount"]
output_mount = next(value for value in mounts if "dst=/run-results" in value)
output_root = Path(next(part[4:] for part in output_mount.split(",") if part.startswith("src=")))
child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
(output_root / "child.pid").write_text(str(child.pid), encoding="utf-8")
time.sleep(60)
