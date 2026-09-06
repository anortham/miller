#!/usr/bin/env python3
import subprocess
import sys
import time
from pathlib import Path

script = Path(__file__)
state = script.with_suffix(".container")
container_id = "e" * 64
operation = sys.argv[1]
if operation == "stop":
    raise SystemExit(0)
if operation == "rm":
    state.unlink(missing_ok=True)
    raise SystemExit(0)
if sys.argv[1:3] == ["container", "exists"]:
    raise SystemExit(0 if state.exists() else 1)
cidfile = Path(sys.argv[sys.argv.index("--cidfile") + 1])
cidfile.write_text(container_id, encoding="ascii")
state.write_text(container_id, encoding="ascii")
if "timeout" in script.name:
    time.sleep(60)
if "orphan" in script.name:
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    script.with_suffix(".child").write_text(str(child.pid), encoding="ascii")
raise SystemExit(0)
