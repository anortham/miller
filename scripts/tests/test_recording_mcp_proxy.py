import hashlib
import json
import os
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path


SCRIPTS_ROOT = Path(__file__).resolve().parents[1]
PROXY = SCRIPTS_ROOT / "benchlib" / "recording_mcp_proxy.py"
FAKE_SERVER = SCRIPTS_ROOT / "tests" / "fixtures" / "agent-efficiency" / "fake_mcp_server.py"
sys.path.insert(0, str(SCRIPTS_ROOT))

from benchlib.agent_contract import count_tool_output_tokens


def _json_line(value: dict) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode() + b"\n"


def _proxy_command(
    events: Path,
    cwd: Path,
    product_args: list[str],
    max_calls: int = 8,
    max_output_tokens: int = 12000,
) -> list[str]:
    return [
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
        if path.exists():
            return
        time.sleep(0.01)
    raise AssertionError(f"timed out waiting for {path}")


def _process_exists(pid: int) -> bool:
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


def _wait_for_process_exit(pid: int, timeout: float = 5) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if not _process_exists(pid):
            return True
        time.sleep(0.01)
    return False


class RecordingMcpProxyTests(unittest.TestCase):
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
            process.send_signal(signal.SIGTERM)
            returncode = process.wait(timeout=10)
            assert process.stdin is not None
            assert process.stdout is not None
            assert process.stderr is not None
            process.stdin.close()
            process.stdout.close()
            process.stderr.close()
            events = _events(events_path)

        self.assertEqual(128 + signal.SIGTERM, returncode)
        failure = next(event for event in events if event["event"] == "proxy_failure")
        self.assertEqual({"reason": "signal", "signal": signal.SIGTERM}, {key: failure[key] for key in ["reason", "signal"]})
        self.assertTrue(_wait_for_process_exit(child_pid))


if __name__ == "__main__":
    unittest.main()
