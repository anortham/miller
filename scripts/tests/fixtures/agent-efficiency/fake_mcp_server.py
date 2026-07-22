import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path


def _write(message: dict) -> None:
    sys.stdout.buffer.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode() + b"\n")
    sys.stdout.buffer.flush()


parser = argparse.ArgumentParser()
parser.add_argument("--received", type=Path)
parser.add_argument("--mode", choices=["normal", "crash", "malformed", "hang"], default="normal")
parser.add_argument("--pid-file", type=Path)
parser.add_argument("--child-pid-file", type=Path)
args = parser.parse_args()

if args.pid_file:
    args.pid_file.write_text(str(os.getpid()), encoding="utf-8")
if args.child_pid_file:
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    args.child_pid_file.write_text(str(child.pid), encoding="utf-8")
if args.mode == "crash":
    sys.stderr.buffer.write(b"fake crash\n")
    sys.stderr.buffer.flush()
    raise SystemExit(23)

for line in sys.stdin.buffer:
    if args.received:
        with args.received.open("ab") as stream:
            stream.write(line)
    message = json.loads(line)
    if args.mode == "malformed":
        sys.stdout.buffer.write(b"not-json\n")
        sys.stdout.buffer.flush()
        time.sleep(60)
    method = message.get("method")
    if method == "initialize":
        _write(
            {
                "jsonrpc": "2.0",
                "id": message["id"],
                "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "fake", "version": "1"},
                    "instructions": "fake instructions",
                },
            }
        )
    elif method == "notifications/initialized":
        _write({"jsonrpc": "2.0", "method": "notifications/fake", "params": {"ready": True}})
        _write({"jsonrpc": "2.0", "id": "server-1", "method": "sampling/createMessage", "params": {}})
    elif method == "tools/list":
        _write(
            {
                "jsonrpc": "2.0",
                "id": message["id"],
                "result": {
                    "tools": [
                        {
                            "name": "echo",
                            "description": "Echo text",
                            "inputSchema": {
                                "type": "object",
                                "properties": {"text": {"type": "string"}},
                                "required": ["text"],
                            },
                        }
                    ]
                },
            }
        )
    elif method == "tools/call":
        sys.stderr.buffer.write(b"fake diagnostic\n")
        sys.stderr.buffer.flush()
        arguments = message["params"]["arguments"]
        if arguments.get("error"):
            _write({"jsonrpc": "2.0", "id": message["id"], "error": {"code": -32602, "message": "fake error"}})
        else:
            _write(
                {
                    "jsonrpc": "2.0",
                    "id": message["id"],
                    "result": {
                        "content": [{"type": "text", "text": f"result {arguments['text']}\nline"}],
                        "isError": False,
                    },
                }
            )

if args.mode == "hang":
    time.sleep(60)
