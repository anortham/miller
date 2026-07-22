import argparse
import hashlib
import json
import os
import select
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Any

from agent_contract import count_tool_output_tokens


class EventWriter:
    def __init__(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        self._fd = os.open(path, os.O_APPEND | os.O_CREAT | os.O_WRONLY, 0o600)
        self._lock = threading.Lock()
        self._sequence = 0
        self._started_ns = time.monotonic_ns()

    def write(self, event: str, **fields: Any) -> None:
        with self._lock:
            self._sequence += 1
            value = {
                "event": event,
                "sequence": self._sequence,
                "monotonic_ns": time.monotonic_ns() - self._started_ns,
                **fields,
            }
            data = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode() + b"\n"
            written = os.write(self._fd, data)
            if written != len(data):
                raise OSError(f"partial event write: {written} of {len(data)} bytes")

    def close(self) -> None:
        os.close(self._fd)


class RecordingProxy:
    def __init__(
        self,
        events: EventWriter,
        command: list[str],
        cwd: Path,
        max_calls: int,
        max_output_tokens: int,
    ) -> None:
        self._events = events
        self._command = command
        self._cwd = cwd
        self._max_calls = max_calls
        self._max_output_tokens = max_output_tokens
        self._state_lock = threading.Lock()
        self._stdout_lock = threading.Lock()
        self._stop = threading.Event()
        self._controller_eof = threading.Event()
        self._last_activity_ns = time.monotonic_ns()
        self._failure_reason: str | None = None
        self._signal_number: int | None = None
        self._pending: dict[tuple[str, str], dict[str, Any]] = {}
        self._tool_call_count = 0
        self._output_tokens = 0
        self._call_budget_closed = False
        self._token_budget_closed = False
        self._process: subprocess.Popen[bytes] | None = None

    def run(self) -> int:
        popen_options: dict[str, Any] = {}
        if os.name == "nt":
            popen_options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
        else:
            popen_options["start_new_session"] = True
        self._process = subprocess.Popen(
            self._command,
            cwd=self._cwd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            **popen_options,
        )
        self._events.write("downstream_started", pid=self._process.pid, cwd=str(self._cwd), command=self._command)
        previous_handlers = self._install_signal_handlers()
        threads = [
            threading.Thread(target=self._pump_controller, name="mcp-controller", daemon=True),
            threading.Thread(target=self._pump_product, name="mcp-product", daemon=True),
            threading.Thread(target=self._pump_stderr, name="mcp-stderr", daemon=True),
        ]
        for thread in threads:
            thread.start()

        timeout_seconds = float(os.environ.get("MILLER_RECORDING_PROXY_TIMEOUT_SECONDS", "120"))
        eof_grace_seconds = float(os.environ.get("MILLER_RECORDING_PROXY_EOF_GRACE_SECONDS", "1"))
        eof_started_ns: int | None = None
        try:
            while self._process.poll() is None:
                now_ns = time.monotonic_ns()
                if self._stop.is_set():
                    self._terminate_process_group()
                    break
                if self._controller_eof.is_set():
                    if eof_started_ns is None:
                        eof_started_ns = now_ns
                    elif now_ns - eof_started_ns >= int(eof_grace_seconds * 1_000_000_000):
                        self._fail("controller_eof", direction="controller_to_product")
                        self._terminate_process_group()
                        break
                if now_ns - self._last_activity_ns >= int(timeout_seconds * 1_000_000_000):
                    self._fail("timeout", timeout_seconds=timeout_seconds)
                    self._terminate_process_group()
                    break
                time.sleep(0.01)
            returncode = self._wait_for_process()
            for thread in threads:
                thread.join(timeout=1)
            self._events.write(
                "downstream_exit",
                returncode=returncode,
                failure_reason=self._failure_reason,
                signal=self._signal_number,
            )
            if self._failure_reason is not None:
                return 128 + self._signal_number if self._signal_number is not None else 70
            return returncode
        finally:
            self._restore_signal_handlers(previous_handlers)

    def _install_signal_handlers(self) -> dict[int, Any]:
        previous: dict[int, Any] = {}
        for signum in (signal.SIGINT, signal.SIGTERM):
            previous[signum] = signal.getsignal(signum)
            signal.signal(signum, self._handle_signal)
        return previous

    def _restore_signal_handlers(self, previous: dict[int, Any]) -> None:
        for signum, handler in previous.items():
            signal.signal(signum, handler)

    def _handle_signal(self, signum: int, _frame: Any) -> None:
        self._signal_number = signum
        self._fail("signal", signal=signum)

    def _pump_controller(self) -> None:
        assert self._process is not None
        assert self._process.stdin is not None
        try:
            while not self._stop.is_set():
                if os.name != "nt":
                    readable, _, _ = select.select([sys.stdin.buffer], [], [], 0.05)
                    if not readable:
                        continue
                line = sys.stdin.buffer.readline()
                if not line:
                    self._controller_eof.set()
                    self._process.stdin.close()
                    return
                self._touch()
                message = self._parse_message(line, "controller_to_product")
                if message is None:
                    return
                if self._should_reject_tool_call(message):
                    continue
                self._record_controller_message(message, line)
                self._process.stdin.write(line)
                self._process.stdin.flush()
                self._record_rpc("controller_to_product", message, line, forwarded=True)
        except (BrokenPipeError, OSError) as error:
            if not self._stop.is_set() and self._process.poll() is None:
                self._fail("controller_relay_error", detail=str(error))
        except Exception as error:
            self._fail("controller_relay_error", detail=repr(error))

    def _pump_product(self) -> None:
        assert self._process is not None
        assert self._process.stdout is not None
        try:
            while not self._stop.is_set():
                line = self._process.stdout.readline()
                if not line:
                    return
                self._touch()
                message = self._parse_message(line, "product_to_controller")
                if message is None:
                    return
                self._record_product_message(message, line)
                with self._stdout_lock:
                    sys.stdout.buffer.write(line)
                    sys.stdout.buffer.flush()
                self._record_rpc("product_to_controller", message, line, forwarded=True)
        except (BrokenPipeError, OSError) as error:
            if not self._stop.is_set():
                self._fail("product_relay_error", detail=str(error))
        except Exception as error:
            self._fail("product_relay_error", detail=repr(error))

    def _pump_stderr(self) -> None:
        assert self._process is not None
        assert self._process.stderr is not None
        try:
            while True:
                line = self._process.stderr.readline()
                if not line:
                    return
                self._touch()
                sys.stderr.buffer.write(line)
                sys.stderr.buffer.flush()
                self._events.write(
                    "stderr",
                    byte_count=len(line),
                    sha256=hashlib.sha256(line).hexdigest(),
                    text=line.decode("utf-8", errors="replace").rstrip("\r\n"),
                )
        except Exception as error:
            if not self._stop.is_set():
                self._fail("stderr_relay_error", detail=repr(error))

    def _parse_message(self, line: bytes, direction: str) -> dict[str, Any] | None:
        try:
            message = json.loads(line)
        except (json.JSONDecodeError, UnicodeDecodeError) as error:
            self._events.write(
                "malformed_protocol",
                direction=direction,
                byte_count=len(line),
                sha256=hashlib.sha256(line).hexdigest(),
                detail=str(error),
            )
            self._fail("malformed_protocol", direction=direction)
            return None
        if not isinstance(message, dict) or message.get("jsonrpc") != "2.0":
            self._events.write(
                "malformed_protocol",
                direction=direction,
                byte_count=len(line),
                sha256=hashlib.sha256(line).hexdigest(),
                detail="expected a JSON-RPC 2.0 object",
            )
            self._fail("malformed_protocol", direction=direction)
            return None
        return message

    def _record_controller_message(self, message: dict[str, Any], line: bytes) -> None:
        method = message.get("method")
        if method is None or "id" not in message:
            return
        key = self._id_key(message["id"])
        pending = {"method": method, "started_ns": time.monotonic_ns(), "id": message["id"]}
        if method == "tools/call":
            params = message.get("params") if isinstance(message.get("params"), dict) else {}
            self._tool_call_count += 1
            pending["name"] = params.get("name")
            pending["arguments"] = params.get("arguments", {})
            pending["call_number"] = self._tool_call_count
            self._events.write(
                "tool_call",
                id=message["id"],
                call_number=self._tool_call_count,
                name=pending["name"],
                arguments=pending["arguments"],
                request_bytes=len(line),
            )
            if self._tool_call_count == self._max_calls:
                self._call_budget_closed = True
                self._events.write(
                    "budget_transition",
                    budget="tool_calls",
                    state="closed",
                    used=self._tool_call_count,
                    limit=self._max_calls,
                )
        with self._state_lock:
            self._pending[key] = pending

    def _record_product_message(self, message: dict[str, Any], line: bytes) -> None:
        if "id" not in message or "method" in message:
            return
        with self._state_lock:
            pending = self._pending.pop(self._id_key(message["id"]), None)
        if pending is None:
            return
        duration_ns = max(0, time.monotonic_ns() - pending["started_ns"])
        if pending["method"] == "initialize":
            result = message.get("result") if isinstance(message.get("result"), dict) else {}
            instructions = result.get("instructions", "")
            instructions_bytes = instructions.encode("utf-8") if isinstance(instructions, str) else b""
            self._events.write(
                "initialize_response",
                id=message["id"],
                instructions_sha256=hashlib.sha256(instructions_bytes).hexdigest(),
                instructions_bytes=len(instructions_bytes),
                error=message.get("error"),
                duration_ns=duration_ns,
            )
            return
        if pending["method"] == "tools/list":
            result = message.get("result") if isinstance(message.get("result"), dict) else {}
            tools = result.get("tools", [])
            tools_bytes = self._canonical_json(tools)
            self._events.write(
                "tools_list_response",
                id=message["id"],
                tools_sha256=hashlib.sha256(tools_bytes).hexdigest(),
                tools_bytes=len(tools_bytes),
                tool_count=len(tools) if isinstance(tools, list) else 0,
                error=message.get("error"),
                duration_ns=duration_ns,
            )
            return
        if pending["method"] != "tools/call":
            return
        result = message.get("result")
        error = message.get("error")
        output_text = self._tool_output_text(result, error)
        output_tokens = count_tool_output_tokens(output_text)
        with self._state_lock:
            self._output_tokens += output_tokens
            cumulative = self._output_tokens
        self._events.write(
            "tool_result" if error is None else "tool_error",
            id=message["id"],
            call_number=pending["call_number"],
            name=pending["name"],
            result=result,
            error=error,
            response_bytes=len(line),
            output_bytes=len(output_text.encode("utf-8")),
            output_tokens=output_tokens,
            cumulative_output_tokens=cumulative,
            duration_ns=duration_ns,
        )
        if cumulative >= self._max_output_tokens and not self._token_budget_closed:
            self._token_budget_closed = True
            self._events.write(
                "budget_transition",
                budget="tool_output_tokens",
                state="closed",
                used=cumulative,
                limit=self._max_output_tokens,
            )

    def _should_reject_tool_call(self, message: dict[str, Any]) -> bool:
        if message.get("method") != "tools/call" or "id" not in message:
            return False
        if not self._call_budget_closed and not self._token_budget_closed:
            return False
        budget = "tool_calls" if self._call_budget_closed else "tool_output_tokens"
        error = {
            "jsonrpc": "2.0",
            "id": message["id"],
            "error": {"code": -32001, "message": f"{budget} budget exhausted"},
        }
        line = json.dumps(error, ensure_ascii=False, separators=(",", ":")).encode() + b"\n"
        with self._stdout_lock:
            sys.stdout.buffer.write(line)
            sys.stdout.buffer.flush()
        self._events.write(
            "tool_call_rejected",
            id=message["id"],
            name=(message.get("params") or {}).get("name"),
            budget=budget,
            call_count=self._tool_call_count,
            cumulative_output_tokens=self._output_tokens,
        )
        self._record_rpc("proxy_to_controller", error, line, forwarded=False)
        return True

    def _record_rpc(self, direction: str, message: dict[str, Any], line: bytes, forwarded: bool) -> None:
        self._events.write(
            "rpc",
            direction=direction,
            message_kind=self._message_kind(message),
            id=message.get("id"),
            method=message.get("method"),
            byte_count=len(line),
            sha256=hashlib.sha256(line).hexdigest(),
            forwarded=forwarded,
        )

    def _message_kind(self, message: dict[str, Any]) -> str:
        if "method" in message:
            return "request" if "id" in message else "notification"
        return "error" if "error" in message else "response"

    def _tool_output_text(self, result: Any, error: Any) -> str:
        if error is not None:
            return json.dumps(error, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        if not isinstance(result, dict) or not isinstance(result.get("content"), list):
            return json.dumps(result, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        texts = [
            item["text"]
            for item in result["content"]
            if isinstance(item, dict) and item.get("type") == "text" and isinstance(item.get("text"), str)
        ]
        return "\n".join(texts)

    def _canonical_json(self, value: Any) -> bytes:
        return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()

    def _id_key(self, value: Any) -> tuple[str, str]:
        return type(value).__name__, json.dumps(value, ensure_ascii=False, separators=(",", ":"))

    def _touch(self) -> None:
        self._last_activity_ns = time.monotonic_ns()

    def _fail(self, reason: str, **fields: Any) -> None:
        with self._state_lock:
            if self._failure_reason is not None:
                return
            self._failure_reason = reason
            self._events.write("proxy_failure", reason=reason, **fields)
            self._stop.set()

    def _terminate_process_group(self) -> None:
        assert self._process is not None
        if self._process.poll() is not None:
            return
        try:
            if os.name == "nt":
                subprocess.run(
                    ["taskkill", "/PID", str(self._process.pid), "/T", "/F"],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    check=False,
                )
            else:
                os.killpg(self._process.pid, signal.SIGTERM)
            self._process.wait(timeout=1)
        except (ProcessLookupError, subprocess.TimeoutExpired):
            if self._process.poll() is None:
                if os.name == "nt":
                    self._process.kill()
                else:
                    os.killpg(self._process.pid, signal.SIGKILL)

    def _wait_for_process(self) -> int:
        assert self._process is not None
        try:
            return self._process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            self._terminate_process_group()
            return self._process.wait(timeout=2)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--events", type=Path, required=True)
    parser.add_argument("--tokenizer", choices=["o200k_base"], required=True)
    parser.add_argument("--max-calls", type=int, required=True)
    parser.add_argument("--max-output-tokens", type=int, required=True)
    parser.add_argument("--cwd", type=Path, required=True)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.command[:1] == ["--"]:
        args.command = args.command[1:]
    if not args.command:
        parser.error("a product command is required after --")
    if args.max_calls < 1 or args.max_output_tokens < 1:
        parser.error("budgets must be positive")
    return args


def main() -> int:
    args = parse_args()
    events = EventWriter(args.events)
    try:
        return RecordingProxy(
            events,
            args.command,
            args.cwd,
            args.max_calls,
            args.max_output_tokens,
        ).run()
    finally:
        events.close()


if __name__ == "__main__":
    raise SystemExit(main())
