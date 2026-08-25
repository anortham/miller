import csv
import hashlib
import json
import os
import signal
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest import mock


SCRIPTS_ROOT = Path(__file__).resolve().parents[1]
PROXY = SCRIPTS_ROOT / "benchlib" / "recording_mcp_proxy.py"
FAKE_SERVER = SCRIPTS_ROOT / "tests" / "fixtures" / "agent-efficiency" / "fake_mcp_server.py"
sys.path.insert(0, str(SCRIPTS_ROOT))
sys.path.insert(0, str(SCRIPTS_ROOT / "benchlib"))

from benchlib.agent_contract import count_tool_output_tokens
from benchlib import recording_mcp_proxy


def _json_line(value: dict) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode() + b"\n"


def _proxy_command(
    events: Path,
    cwd: Path,
    product_args: list[str],
    max_calls: int = 8,
    max_output_tokens: int = 12000,
    product_environment: dict[str, str] | None = None,
) -> list[str]:
    command = [
        sys.executable,
        str(PROXY),
        "--events",
        str(events),
        "--tokenizer",
        "o200k_base",
        "--max-calls",
        str(max_calls),
        "--max-output-tokens",
        str(max_output_tokens),
        "--cwd",
        str(cwd),
    ]
    for name, value in sorted((product_environment or {}).items()):
        command.extend(("--product-env", f"{name}={value}"))
    return [
        *command,
        "--",
        sys.executable,
        str(FAKE_SERVER),
        *product_args,
    ]


def _events(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


def _wait_for_file(path: Path, timeout: float = 5) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if path.exists() and path.stat().st_size > 0:
            return
        time.sleep(0.01)
    raise AssertionError(f"timed out waiting for {path}")


def _process_exists(pid: int) -> bool:
    if os.name == "nt":
        try:
            result = subprocess.run(
                ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
                capture_output=True,
                text=True,
                check=False,
            )
        except OSError as error:
            raise AssertionError(f"tasklist probe failed: {error}") from error
        if result.returncode != 0:
            raise AssertionError(f"tasklist probe exited with status {result.returncode}: {result.stderr}")
        try:
            rows = list(csv.reader(result.stdout.splitlines()))
        except csv.Error as error:
            raise AssertionError(f"tasklist probe returned malformed CSV: {error}") from error
        return any(len(row) > 1 and row[1] == str(pid) for row in rows)
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def _wait_for_process_exit(pid: int, timeout: float = 5) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if not _process_exists(pid):
            return True
        time.sleep(0.01)
    return False


class RecordingMcpProxyTests(unittest.TestCase):
    def test_wait_for_file_requires_content(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "pending"
            path.touch()

            with self.assertRaises(AssertionError):
                _wait_for_file(path, timeout=0.02)

            path.write_text("ready", encoding="utf-8")
            _wait_for_file(path, timeout=0.02)

    def test_product_environment_is_passed_to_the_product_process(self) -> None:
        request = b'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}\n'
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events = root / "events.jsonl"
            environment_file = root / "environment.txt"
            completed = subprocess.run(
                _proxy_command(
                    events,
                    root,
                    ["--env-file", str(environment_file)],
                    product_environment={"JULIE_HOME": str(root / "julie-home")},
                ),
                input=request,
                capture_output=True,
                timeout=10,
            )

            self.assertEqual(0, completed.returncode, completed.stderr.decode())
            self.assertEqual(str(root / "julie-home"), environment_file.read_text(encoding="utf-8"))

    def test_initialize_response_is_forwarded_byte_for_byte(self) -> None:
        request = b'{"jsonrpc":"2.0","id":"init-1","method":"initialize","params":{}}\n'
        response = b'{"jsonrpc":"2.0","id":"init-1","result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"fake","version":"1"},"instructions":"fake instructions"}}\n'
        with tempfile.TemporaryDirectory() as directory:
            events = Path(directory) / "events.jsonl"
            completed = subprocess.run(
                _proxy_command(events, Path(directory), []),
                input=request,
                capture_output=True,
                timeout=10,
            )

        self.assertEqual(0, completed.returncode, completed.stderr.decode())
        self.assertEqual(response, completed.stdout)

    def test_records_complete_exchange_without_changing_protocol_bytes(self) -> None:
        initialize = {"jsonrpc": "2.0", "id": "init-1", "method": "initialize", "params": {}}
        initialized = {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}}
        server_response = {"jsonrpc": "2.0", "id": "server-1", "result": {"model": "none"}}
        tools_list = {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}
        tool_call = {
            "jsonrpc": "2.0",
            "id": "call-1",
            "method": "tools/call",
            "params": {"name": "echo", "arguments": {"text": "café"}},
        }
        input_bytes = b"".join(_json_line(value) for value in [initialize, initialized, server_response, tools_list, tool_call])
        expected_messages = [
            {
                "jsonrpc": "2.0",
                "id": "init-1",
                "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "fake", "version": "1"},
                    "instructions": "fake instructions",
                },
            },
            {"jsonrpc": "2.0", "method": "notifications/fake", "params": {"ready": True}},
            {"jsonrpc": "2.0", "id": "server-1", "method": "sampling/createMessage", "params": {}},
            {
                "jsonrpc": "2.0",
                "id": 2,
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
            },
            {
                "jsonrpc": "2.0",
                "id": "call-1",
                "result": {
                    "content": [{"type": "text", "text": "result café\nline"}],
                    "isError": False,
                },
            },
        ]
        expected_output = b"".join(_json_line(value) for value in expected_messages)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            received_path = root / "received.jsonl"
            completed = subprocess.run(
                _proxy_command(events_path, root, ["--received", str(received_path)]),
                input=input_bytes,
                capture_output=True,
                timeout=10,
            )
            self.assertTrue(events_path.exists(), "proxy did not write measurement events")
            events = _events(events_path)
            received = received_path.read_bytes()

        self.assertEqual(0, completed.returncode, completed.stderr.decode())
        self.assertEqual(expected_output, completed.stdout)
        self.assertEqual(b"fake diagnostic\n", completed.stderr)
        self.assertEqual(input_bytes, received)
        initialize_event = next(event for event in events if event["event"] == "initialize_response")
        self.assertEqual(hashlib.sha256(b"fake instructions").hexdigest(), initialize_event["instructions_sha256"])
        tools_event = next(event for event in events if event["event"] == "tools_list_response")
        expected_tools = expected_messages[3]["result"]["tools"]
        expected_tools_bytes = json.dumps(expected_tools, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
        self.assertEqual(hashlib.sha256(expected_tools_bytes).hexdigest(), tools_event["tools_sha256"])
        call_event = next(event for event in events if event["event"] == "tool_call")
        self.assertEqual("call-1", call_event["id"])
        self.assertEqual({"text": "café"}, call_event["arguments"])
        result_event = next(event for event in events if event["event"] == "tool_result")
        self.assertEqual(len("result café\nline".encode()), result_event["output_bytes"])
        self.assertEqual(count_tool_output_tokens("result café\nline"), result_event["output_tokens"])
        self.assertGreaterEqual(result_event["duration_ns"], 0)
        self.assertEqual(expected_messages[-1]["result"], result_event["result"])
        rpc_events = [event for event in events if event["event"] == "rpc"]
        self.assertEqual(len(input_bytes) + len(expected_output), sum(event["byte_count"] for event in rpc_events))
        self.assertTrue(all(event["monotonic_ns"] >= 0 for event in events))
        self.assertEqual(list(range(1, len(events) + 1)), [event["sequence"] for event in events])
        self.assertEqual("fake diagnostic", next(event for event in events if event["event"] == "stderr")["text"])
        self.assertEqual(0, next(event for event in events if event["event"] == "downstream_exit")["returncode"])

    def test_json_rpc_tool_error_is_forwarded_and_recorded(self) -> None:
        call = {
            "jsonrpc": "2.0",
            "id": "error-1",
            "method": "tools/call",
            "params": {"name": "echo", "arguments": {"error": True}},
        }
        expected_error = {"code": -32602, "message": "fake error"}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            completed = subprocess.run(
                _proxy_command(events_path, root, []),
                input=_json_line(call),
                capture_output=True,
                timeout=10,
            )
            events = _events(events_path)

        self.assertEqual(0, completed.returncode, completed.stderr.decode())
        response = json.loads(completed.stdout)
        self.assertEqual(expected_error, response["error"])
        error_event = next(event for event in events if event["event"] == "tool_error")
        self.assertEqual(expected_error, error_event["error"])
        error_text = json.dumps(expected_error, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        self.assertEqual(len(error_text.encode()), error_event["output_bytes"])
        self.assertEqual(count_tool_output_tokens(error_text), error_event["output_tokens"])

    def test_ninth_tool_call_is_rejected_without_reaching_product(self) -> None:
        calls = [
            {
                "jsonrpc": "2.0",
                "id": index,
                "method": "tools/call",
                "params": {"name": "echo", "arguments": {"text": str(index)}},
            }
            for index in range(1, 10)
        ]
        input_bytes = b"".join(_json_line(value) for value in calls)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            received_path = root / "received.jsonl"
            completed = subprocess.run(
                _proxy_command(events_path, root, ["--received", str(received_path)]),
                input=input_bytes,
                capture_output=True,
                timeout=10,
            )
            received = [json.loads(line) for line in received_path.read_text(encoding="utf-8").splitlines()]
            output = [json.loads(line) for line in completed.stdout.splitlines()]
            events = _events(events_path)

        self.assertEqual(0, completed.returncode, completed.stderr.decode())
        self.assertEqual(list(range(1, 9)), [message["id"] for message in received])
        self.assertEqual(set(range(1, 10)), {message["id"] for message in output})
        rejected = next(message for message in output if message["id"] == 9)
        self.assertEqual(-32001, rejected["error"]["code"])
        rejection_event = next(event for event in events if event["event"] == "tool_call_rejected")
        self.assertEqual(9, rejection_event["id"])
        transition = next(
            event for event in events if event["event"] == "budget_transition" and event["budget"] == "tool_calls"
        )
        self.assertEqual({"state": "closed", "used": 8, "limit": 8}, {key: transition[key] for key in ["state", "used", "limit"]})

    def test_crossing_token_response_is_forwarded_before_later_call_is_rejected(self) -> None:
        first = {
            "jsonrpc": "2.0",
            "id": "first",
            "method": "tools/call",
            "params": {"name": "echo", "arguments": {"text": "crossing response"}},
        }
        second = {
            "jsonrpc": "2.0",
            "id": "second",
            "method": "tools/call",
            "params": {"name": "echo", "arguments": {"text": "must not reach product"}},
        }
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            received_path = root / "received.jsonl"
            process = subprocess.Popen(
                _proxy_command(
                    events_path,
                    root,
                    ["--received", str(received_path)],
                    max_output_tokens=1,
                ),
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
            assert process.stdin is not None
            assert process.stdout is not None
            assert process.stderr is not None
            process.stdin.write(_json_line(first))
            process.stdin.flush()
            first_response = json.loads(process.stdout.readline())
            process.stdin.write(_json_line(second))
            process.stdin.flush()
            second_response = json.loads(process.stdout.readline())
            process.stdin.close()
            returncode = process.wait(timeout=10)
            stderr = process.stderr.read()
            process.stdout.close()
            process.stderr.close()
            received = [json.loads(line) for line in received_path.read_text(encoding="utf-8").splitlines()]
            events = _events(events_path)

        self.assertEqual(0, returncode, stderr.decode())
        self.assertEqual("first", first_response["id"])
        self.assertEqual("second", second_response["id"])
        self.assertEqual(-32001, second_response.get("error", {}).get("code"))
        self.assertEqual(["first"], [message["id"] for message in received])
        transition = next(
            event
            for event in events
            if event["event"] == "budget_transition" and event["budget"] == "tool_output_tokens"
        )
        self.assertGreater(transition["used"], transition["limit"])

    def test_coalesced_requests_on_an_open_pipe_are_forwarded_without_waiting_for_more_input(self) -> None:
        initialize = {"jsonrpc": "2.0", "id": "init-1", "method": "initialize", "params": {}}
        initialized = {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}}
        tools_list = {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}
        with tempfile.TemporaryDirectory() as directory:
            events = Path(directory) / "events.jsonl"
            process = subprocess.Popen(
                _proxy_command(events, Path(directory), []),
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
            try:
                assert process.stdin is not None and process.stdout is not None
                process.stdin.write(_json_line(initialize))
                process.stdin.flush()
                self.assertIn(b'"init-1"', process.stdout.readline())
                process.stdin.write(_json_line(initialized) + _json_line(tools_list))
                process.stdin.flush()
                response: list[bytes] = []

                def _read_until_tools_response() -> None:
                    for _ in range(8):
                        line = process.stdout.readline()
                        if not line or b'"tools"' in line:
                            response.append(line)
                            return

                reader = threading.Thread(target=_read_until_tools_response)
                reader.start()
                reader.join(timeout=5)
                self.assertFalse(reader.is_alive(), "tools/list response did not arrive while stdin stayed open")
                self.assertTrue(response and b'"tools"' in response[0], response)
            finally:
                if process.stdin and not process.stdin.closed:
                    process.stdin.close()
                process.wait(timeout=10)

    def test_product_crash_is_recorded_without_stdout_contamination(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            completed = subprocess.run(
                _proxy_command(events_path, root, ["--mode", "crash"]),
                input=b"",
                capture_output=True,
                timeout=10,
            )
            events = _events(events_path)

        self.assertEqual(23, completed.returncode)
        self.assertEqual(b"", completed.stdout)
        self.assertEqual(b"fake crash\n", completed.stderr)
        exit_event = next(event for event in events if event["event"] == "downstream_exit")
        self.assertEqual(23, exit_event["returncode"])
        self.assertIsNone(exit_event["failure_reason"])

    def test_normal_parent_exit_terminates_descendant_process_tree(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            child_pid_path = root / "child-pid"
            started = time.monotonic()
            completed = subprocess.run(
                _proxy_command(events_path, root, ["--mode", "spawn-and-exit", "--child-pid-file", str(child_pid_path)]),
                input=b"",
                capture_output=True,
                timeout=10,
            )
            child_pid = int(child_pid_path.read_text(encoding="utf-8"))
            elapsed = time.monotonic() - started

        self.assertEqual(0, completed.returncode, completed.stderr.decode())
        self.assertLess(elapsed, 4)
        self.assertTrue(_wait_for_process_exit(child_pid))

    def test_normal_parent_exit_escalates_descendant_ignoring_sigterm(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            child_pid_path = root / "child-pid"
            started = time.monotonic()
            completed = subprocess.run(
                _proxy_command(
                    events_path,
                    root,
                    ["--mode", "spawn-and-ignore-term", "--child-pid-file", str(child_pid_path)],
                ),
                input=b"",
                capture_output=True,
                timeout=10,
            )
            child_pid = int(child_pid_path.read_text(encoding="utf-8"))
            elapsed = time.monotonic() - started

        self.assertEqual(0, completed.returncode, completed.stderr.decode())
        self.assertLess(elapsed, 4)
        self.assertTrue(_wait_for_process_exit(child_pid))

    def test_normal_exit_does_not_wait_for_open_controller_and_drains_output(self) -> None:
        call = {
            "jsonrpc": "2.0",
            "id": "complete",
            "method": "tools/call",
            "params": {"name": "echo", "arguments": {"text": "complete output"}},
        }
        expected_stdout = _json_line(
            {
                "jsonrpc": "2.0",
                "id": "complete",
                "result": {
                    "content": [{"type": "text", "text": "result complete output\nline"}],
                    "isError": False,
                },
            }
        )
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            process = subprocess.Popen(
                _proxy_command(events_path, root, ["--mode", "exit-after-one"]),
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
            assert process.stdin is not None
            assert process.stdout is not None
            assert process.stderr is not None
            process.stdin.write(_json_line(call))
            process.stdin.flush()
            started = time.monotonic()
            try:
                returncode = process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.stdin.close()
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=5)
                raise
            finally:
                if not process.stdin.closed:
                    process.stdin.close()
            elapsed = time.monotonic() - started
            stdout = process.stdout.read()
            stderr = process.stderr.read()
            process.stdout.close()
            process.stderr.close()
            events = _events(events_path)

        self.assertLess(elapsed, 4)
        self.assertEqual(0, returncode, stderr.decode())
        self.assertEqual(expected_stdout, stdout)
        self.assertEqual(b"fake diagnostic\n", stderr)
        self.assertNotIn("proxy_failure", [event["event"] for event in events])
        self.assertEqual(
            ["downstream_started", "tool_call", "rpc", "stderr", "tool_result", "rpc", "downstream_exit"],
            [event["event"] for event in events],
        )
        self.assertEqual(0, next(event for event in events if event["event"] == "downstream_exit")["returncode"])
        self.assertIsNone(next(event for event in events if event["event"] == "downstream_exit")["failure_reason"])
        self.assertEqual(
            len(_json_line(call)) + len(expected_stdout),
            sum(event["byte_count"] for event in events if event["event"] == "rpc"),
        )

    def test_controller_cancellation_failure_closes_input_and_joins_reader(self) -> None:
        class BlockingStream:
            def __init__(self) -> None:
                self.released = threading.Event()
                self.closed = False

            def readline(self) -> bytes:
                self.released.wait()
                return b""

            def close(self) -> None:
                self.closed = True
                self.released.set()

        class ControllerInput:
            def __init__(self, stream: BlockingStream) -> None:
                self.buffer = stream

        class FailingJob:
            def cancel_thread_io(self, _thread: threading.Thread) -> None:
                raise OSError("injected cancellation failure")

        with tempfile.TemporaryDirectory() as directory:
            events_path = Path(directory) / "events.jsonl"
            events = recording_mcp_proxy.EventWriter(events_path)
            proxy = recording_mcp_proxy.RecordingProxy(
                events,
                [],
                Path(directory),
                max_calls=1,
                max_output_tokens=1,
            )
            stream = BlockingStream()
            controller_input = ControllerInput(stream)
            controller_thread = threading.Thread(target=stream.readline, daemon=True)
            product_thread = threading.Thread(target=lambda: None)
            stderr_thread = threading.Thread(target=lambda: None)
            controller_thread.start()
            product_thread.start()
            stderr_thread.start()
            proxy._windows_job = FailingJob()
            try:
                with mock.patch.object(recording_mcp_proxy.sys, "stdin", controller_input), mock.patch.object(
                    recording_mcp_proxy.os,
                    "name",
                    "nt",
                ):
                    proxy._join_threads(
                        controller_thread=controller_thread,
                        product_thread=product_thread,
                        stderr_thread=stderr_thread,
                    )
            finally:
                events.close()

        self.assertTrue(stream.closed)
        self.assertFalse(controller_thread.is_alive())
        self.assertEqual("controller_shutdown", proxy._failure_reason)

    def test_malformed_product_protocol_fails_closed_and_terminates_product(self) -> None:
        request = _json_line({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}})
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            pid_path = root / "pid"
            completed = subprocess.run(
                _proxy_command(events_path, root, ["--mode", "malformed", "--pid-file", str(pid_path)]),
                input=request,
                capture_output=True,
                timeout=10,
            )
            pid = int(pid_path.read_text(encoding="utf-8"))
            events = _events(events_path)

        self.assertEqual(70, completed.returncode)
        self.assertEqual(b"", completed.stdout)
        malformed = next(event for event in events if event["event"] == "malformed_protocol")
        self.assertEqual("product_to_controller", malformed["direction"])
        self.assertTrue(_wait_for_process_exit(pid))

    def test_controller_malformed_protocol_never_reaches_product(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            received_path = root / "received.jsonl"
            completed = subprocess.run(
                _proxy_command(events_path, root, ["--received", str(received_path)]),
                input=b"not-json\n",
                capture_output=True,
                timeout=10,
            )
            events = _events(events_path)

        self.assertEqual(70, completed.returncode)
        self.assertEqual(b"", completed.stdout)
        self.assertFalse(received_path.exists())
        malformed = next(event for event in events if event["event"] == "malformed_protocol")
        self.assertEqual("controller_to_product", malformed["direction"])

    def test_timeout_terminates_product_process_group(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            child_pid_path = root / "child-pid"
            environment = {**os.environ, "MILLER_RECORDING_PROXY_TIMEOUT_SECONDS": "0.1", "MILLER_RECORDING_PROXY_EOF_GRACE_SECONDS": "5"}
            completed = subprocess.run(
                _proxy_command(events_path, root, ["--mode", "hang", "--child-pid-file", str(child_pid_path)]),
                input=b"",
                capture_output=True,
                timeout=10,
                env=environment,
            )
            child_pid = int(child_pid_path.read_text(encoding="utf-8"))
            events = _events(events_path)

        self.assertEqual(70, completed.returncode)
        self.assertEqual("timeout", next(event for event in events if event["event"] == "proxy_failure")["reason"])
        self.assertTrue(_wait_for_process_exit(child_pid))

    def test_controller_eof_terminates_product_process_group(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            child_pid_path = root / "child-pid"
            environment = {**os.environ, "MILLER_RECORDING_PROXY_TIMEOUT_SECONDS": "5", "MILLER_RECORDING_PROXY_EOF_GRACE_SECONDS": "0.1"}
            completed = subprocess.run(
                _proxy_command(events_path, root, ["--mode", "hang", "--child-pid-file", str(child_pid_path)]),
                input=b"",
                capture_output=True,
                timeout=10,
                env=environment,
            )
            child_pid = int(child_pid_path.read_text(encoding="utf-8"))
            events = _events(events_path)

        self.assertEqual(70, completed.returncode)
        self.assertEqual("controller_eof", next(event for event in events if event["event"] == "proxy_failure")["reason"])
        self.assertTrue(_wait_for_process_exit(child_pid))

    def test_signal_terminates_product_process_group(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events_path = root / "events.jsonl"
            child_pid_path = root / "child-pid"
            process = subprocess.Popen(
                _proxy_command(events_path, root, ["--mode", "hang", "--child-pid-file", str(child_pid_path)]),
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
            _wait_for_file(child_pid_path)
            child_pid = int(child_pid_path.read_text(encoding="utf-8"))
            if os.name == "nt":
                process.terminate()
            else:
                process.send_signal(signal.SIGTERM)
            returncode = process.wait(timeout=10)
            assert process.stdin is not None
            assert process.stdout is not None
            assert process.stderr is not None
            process.stdin.close()
            process.stdout.close()
            process.stderr.close()
            events = _events(events_path)

        if os.name == "nt":
            self.assertEqual(1, returncode)
        else:
            self.assertEqual(128 + signal.SIGTERM, returncode)
            failure = next(event for event in events if event["event"] == "proxy_failure")
            self.assertEqual({"reason": "signal", "signal": signal.SIGTERM}, {key: failure[key] for key in ["reason", "signal"]})
        self.assertTrue(_wait_for_process_exit(child_pid))


if __name__ == "__main__":
    unittest.main()
