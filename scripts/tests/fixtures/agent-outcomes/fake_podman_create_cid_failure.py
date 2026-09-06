#!/usr/bin/env python3
import sys
import time
from pathlib import Path

script = Path(__file__)
log = script.with_suffix(".log")
state = script.with_suffix(".container")
container_id = "d" * 64
operation = sys.argv[1]
with log.open("a", encoding="utf-8") as stream:
    stream.write(operation + "\n")
if operation == "create":
    cidfile = Path(sys.argv[sys.argv.index("--cidfile") + 1])
    cidfile.write_text(container_id, encoding="ascii")
    state.write_text(container_id, encoding="ascii")
    if "timeout" in script.name:
        time.sleep(60)
    raise SystemExit(7)
if operation == "stop":
    raise SystemExit(0)
if operation == "rm":
    state.unlink(missing_ok=True)
    raise SystemExit(0)
if sys.argv[1:3] == ["container", "exists"]:
    raise SystemExit(0 if state.exists() else 1)
raise SystemExit(125)
