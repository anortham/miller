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


class _WindowsProcessJobSetupError(RuntimeError):
    def __init__(self, message: str, job: "_WindowsProcessJob") -> None:
        super().__init__(message)
        self.job = job


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


class _WindowsProcessJob:
    _CREATE_SUSPENDED = 0x00000004
    _TH32CS_SNAPTHREAD = 0x00000004
    _THREAD_SUSPEND_RESUME = 0x0002
    _EXTENDED_LIMIT_INFORMATION = 9
    _JOB_OBJECT_BASIC_PROCESS_ID_LIST = 3
    _KILL_ON_JOB_CLOSE = 0x00002000
    _PROCESS_SET_QUOTA = 0x0100
    _PROCESS_TERMINATE = 0x0001
    _THREAD_TERMINATE = 0x0001
    _ERROR_NO_MORE_FILES = 18
    _ERROR_MORE_DATA = 234
    _ERROR_INVALID_PARAMETER = 87
    _ERROR_NOT_FOUND = 1168

    def __init__(self, process: subprocess.Popen[bytes]) -> None:
        import ctypes
        from ctypes import wintypes

        class BasicLimitInformation(ctypes.Structure):
            _fields_ = [
                ("PerProcessUserTimeLimit", ctypes.c_longlong),
                ("PerJobUserTimeLimit", ctypes.c_longlong),
                ("LimitFlags", wintypes.DWORD),
                ("MinimumWorkingSetSize", ctypes.c_size_t),
                ("MaximumWorkingSetSize", ctypes.c_size_t),
                ("ActiveProcessLimit", wintypes.DWORD),
                ("Affinity", ctypes.c_size_t),
                ("PriorityClass", wintypes.DWORD),
                ("SchedulingClass", wintypes.DWORD),
            ]

        class IoCounters(ctypes.Structure):
            _fields_ = [
                ("ReadOperationCount", ctypes.c_ulonglong),
                ("WriteOperationCount", ctypes.c_ulonglong),
                ("OtherOperationCount", ctypes.c_ulonglong),
                ("ReadTransferCount", ctypes.c_ulonglong),
                ("WriteTransferCount", ctypes.c_ulonglong),
                ("OtherTransferCount", ctypes.c_ulonglong),
            ]

        class ExtendedLimitInformation(ctypes.Structure):
            _fields_ = [
                ("BasicLimitInformation", BasicLimitInformation),
                ("IoInfo", IoCounters),
                ("ProcessMemoryLimit", ctypes.c_size_t),
                ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryUsed", ctypes.c_size_t),
                ("PeakJobMemoryUsed", ctypes.c_size_t),
            ]

        class ThreadEntry32(ctypes.Structure):
            _fields_ = [
                ("dwSize", wintypes.DWORD),
                ("cntUsage", wintypes.DWORD),
                ("th32ThreadID", wintypes.DWORD),
                ("th32OwnerProcessID", wintypes.DWORD),
                ("tpBasePri", wintypes.LONG),
                ("tpDeltaPri", wintypes.LONG),
                ("dwFlags", wintypes.DWORD),
            ]

        self._ctypes = ctypes
        self._thread_entry_type = ThreadEntry32
        self._invalid_handle_value = ctypes.c_void_p(-1).value
        self._kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self._kernel32.CreateJobObjectW.argtypes = [wintypes.LPVOID, wintypes.LPCWSTR]
        self._kernel32.CreateJobObjectW.restype = wintypes.HANDLE
        self._kernel32.SetInformationJobObject.argtypes = [
            wintypes.HANDLE,
            wintypes.DWORD,
            wintypes.LPVOID,
            wintypes.DWORD,
        ]
        self._kernel32.SetInformationJobObject.restype = wintypes.BOOL
        self._kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        self._kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
        self._kernel32.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
        self._kernel32.TerminateJobObject.restype = wintypes.BOOL
        self._kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        self._kernel32.CloseHandle.restype = wintypes.BOOL
        self._kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self._kernel32.OpenProcess.restype = wintypes.HANDLE
        self._kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
        self._kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
        self._kernel32.Thread32First.argtypes = [wintypes.HANDLE, ctypes.POINTER(ThreadEntry32)]
        self._kernel32.Thread32First.restype = wintypes.BOOL
        self._kernel32.Thread32Next.argtypes = [wintypes.HANDLE, ctypes.POINTER(ThreadEntry32)]
        self._kernel32.Thread32Next.restype = wintypes.BOOL
        self._kernel32.QueryInformationJobObject.argtypes = [
            wintypes.HANDLE,
            wintypes.DWORD,
            wintypes.LPVOID,
            wintypes.DWORD,
            ctypes.POINTER(wintypes.DWORD),
        ]
        self._kernel32.QueryInformationJobObject.restype = wintypes.BOOL
        self._kernel32.OpenThread.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self._kernel32.OpenThread.restype = wintypes.HANDLE
        self._kernel32.CancelSynchronousIo.argtypes = [wintypes.HANDLE]
        self._kernel32.CancelSynchronousIo.restype = wintypes.BOOL
        self._kernel32.ResumeThread.argtypes = [wintypes.HANDLE]
        self._kernel32.ResumeThread.restype = wintypes.DWORD

        self._handle = self._kernel32.CreateJobObjectW(None, None)
        if not self._handle:
            raise ctypes.WinError(ctypes.get_last_error())
        self._terminated = False
        try:
            limits = ExtendedLimitInformation()
            limits.BasicLimitInformation.LimitFlags = self._KILL_ON_JOB_CLOSE
            if not self._kernel32.SetInformationJobObject(
                self._handle,
                self._EXTENDED_LIMIT_INFORMATION,
                ctypes.byref(limits),
                ctypes.sizeof(limits),
            ):
                raise ctypes.WinError(ctypes.get_last_error())
            process_handle = getattr(process, "_handle", None)
            owned_process_handle = False
            if process_handle is None:
                process_handle = self._kernel32.OpenProcess(
                    self._PROCESS_SET_QUOTA | self._PROCESS_TERMINATE,
                    False,
                    process.pid,
                )
                if not process_handle:
                    raise ctypes.WinError(ctypes.get_last_error())
                owned_process_handle = True
            try:
                if not self._kernel32.AssignProcessToJobObject(self._handle, process_handle):
                    raise ctypes.WinError(ctypes.get_last_error())
            finally:
                if owned_process_handle:
                    self._close_handle(process_handle)
        except BaseException as error:
            try:
                self.close()
            except BaseException as cleanup_error:
                raise _WindowsProcessJobSetupError(
                    f"Windows process job setup failed: {error}; cleanup failed: {cleanup_error}",
                    self,
                ) from error
            raise

    def resume_primary_thread(self, process_id: int) -> None:
        import ctypes

        snapshot = self._kernel32.CreateToolhelp32Snapshot(self._TH32CS_SNAPTHREAD, 0)
        if not snapshot or self._handle_value(snapshot) == self._invalid_handle_value:
            raise ctypes.WinError(ctypes.get_last_error())
        thread_ids: list[int] = []
        try:
            entry = self._thread_entry_type()
            entry.dwSize = ctypes.sizeof(entry)
            if not self._kernel32.Thread32First(snapshot, ctypes.byref(entry)):
                error = ctypes.get_last_error()
                if error != self._ERROR_NO_MORE_FILES:
                    raise ctypes.WinError(error)
            else:
                while True:
                    if entry.th32OwnerProcessID == process_id:
                        thread_ids.append(entry.th32ThreadID)
                    if self._kernel32.Thread32Next(snapshot, ctypes.byref(entry)):
                        continue
                    error = ctypes.get_last_error()
                    if error != self._ERROR_NO_MORE_FILES:
                        raise ctypes.WinError(error)
                    break
        finally:
            self._close_handle(snapshot)
        if len(thread_ids) != 1:
            raise RuntimeError(f"expected one suspended primary thread, found {len(thread_ids)}")
        thread = self._kernel32.OpenThread(self._THREAD_SUSPEND_RESUME, False, thread_ids[0])
        if not thread:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            previous_count = self._kernel32.ResumeThread(thread)
            if previous_count == 0xFFFFFFFF:
                raise ctypes.WinError(ctypes.get_last_error())
            if previous_count != 1:
                raise RuntimeError(f"unexpected primary thread suspend count: {previous_count}")
        finally:
            self._close_handle(thread)

    def process_tree_pids(self, root_pid: int) -> set[int]:
        import ctypes

        del root_pid
        pointer_size = ctypes.sizeof(ctypes.c_size_t)
        capacity = 16
        while True:
            buffer_size = 8 + capacity * pointer_size
            buffer = ctypes.create_string_buffer(buffer_size)
            returned = ctypes.c_uint32()
            success = self._kernel32.QueryInformationJobObject(
                self._handle,
                self._JOB_OBJECT_BASIC_PROCESS_ID_LIST,
                buffer,
                buffer_size,
                ctypes.byref(returned),
            )
            error = ctypes.get_last_error() if not success else 0
            counts = ctypes.cast(buffer, ctypes.POINTER(ctypes.c_uint32))
            process_count = int(counts[1])
            required_size = max(
                int(returned.value),
                8 + process_count * pointer_size,
            )
            if not success and error != self._ERROR_MORE_DATA:
                raise ctypes.WinError(error)
            if process_count > capacity or required_size > buffer_size:
                capacity = max(
                    capacity * 2,
                    process_count,
                    (required_size - 8 + pointer_size - 1) // pointer_size,
                )
                continue
            process_ids = ctypes.cast(
                ctypes.byref(buffer, 8),
                ctypes.POINTER(ctypes.c_size_t),
            )
            return {int(process_ids[index]) for index in range(process_count)}

    def wait_for_process_tree_exit(self, root_pid: int, timeout: float) -> None:
        deadline = time.monotonic() + timeout
        while True:
            if not self.process_tree_pids(root_pid):
                return
            if time.monotonic() >= deadline:
                raise subprocess.TimeoutExpired("process-tree", timeout)
            time.sleep(0.02)

    def terminate(self) -> None:
        if not self._handle:
            raise RuntimeError("Windows process job is closed")
        if self._terminated:
            return
        if not self._kernel32.TerminateJobObject(self._handle, 1):
            raise self._ctypes.WinError(self._ctypes.get_last_error())
        self._terminated = True

    def cancel_thread_io(self, thread: threading.Thread) -> None:
        import ctypes

        thread_id = thread.native_id
        if thread_id is None:
            return
        handle = self._kernel32.OpenThread(self._THREAD_TERMINATE, False, thread_id)
        if not handle:
            error = ctypes.get_last_error()
            if error == self._ERROR_INVALID_PARAMETER:
                return
            raise ctypes.WinError(error)
        try:
            if not self._kernel32.CancelSynchronousIo(handle):
                error = ctypes.get_last_error()
                if error != self._ERROR_NOT_FOUND:
                    raise ctypes.WinError(error)
        finally:
            self._close_handle(handle)

    def close(self) -> None:
        if not self._handle:
            return
        handle = self._handle
        if not self._kernel32.CloseHandle(handle):
            raise self._ctypes.WinError(self._ctypes.get_last_error())
        self._handle = None

    def _close_handle(self, handle: Any) -> None:
        if not self._kernel32.CloseHandle(handle):
            raise self._ctypes.WinError(self._ctypes.get_last_error())

    def _handle_value(self, handle: Any) -> int:
        value = getattr(handle, "value", handle)
        return 0 if value is None else int(value)


class RecordingProxy:
    def __init__(
        self,
        events: EventWriter,
        command: list[str],
        cwd: Path,
        max_calls: int,
        max_output_tokens: int,
        product_environment: dict[str, str] | None = None,
    ) -> None:
        self._events = events
        self._command = command
        self._cwd = cwd
        self._max_calls = max_calls
        self._max_output_tokens = max_output_tokens
        self._product_environment = dict(product_environment or {})
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
        self._windows_job: _WindowsProcessJob | None = None

    def run(self) -> int:
        popen_options: dict[str, Any] = {}
        previous_handlers: dict[int, Any] = {}
        threads: list[threading.Thread] = []
        returncode = 70
        if os.name == "nt":
            popen_options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP | _WindowsProcessJob._CREATE_SUSPENDED
        else:
            popen_options["start_new_session"] = True
        try:
            self._process = subprocess.Popen(
                self._command,
                cwd=self._cwd,
                env={**os.environ, **self._product_environment},
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                **popen_options,
            )
            if os.name == "nt":
                try:
                    self._windows_job = _WindowsProcessJob(self._process)
                except _WindowsProcessJobSetupError as error:
                    self._windows_job = error.job
                    raise
                self._windows_job.resume_primary_thread(self._process.pid)
            self._events.write("downstream_started", pid=self._process.pid, cwd=str(self._cwd), command=self._command)
            previous_handlers = self._install_signal_handlers()
            controller_thread = threading.Thread(
                target=self._pump_controller, name="mcp-controller", daemon=True)
            product_thread = threading.Thread(
                target=self._pump_product, name="mcp-product", daemon=True)
            stderr_thread = threading.Thread(
                target=self._pump_stderr, name="mcp-stderr", daemon=True)
            threads = [controller_thread, product_thread, stderr_thread]
            for thread in threads:
                thread.start()

            timeout_seconds = float(os.environ.get("MILLER_RECORDING_PROXY_TIMEOUT_SECONDS", "120"))
            eof_grace_seconds = float(os.environ.get("MILLER_RECORDING_PROXY_EOF_GRACE_SECONDS", "1"))
            eof_started_ns: int | None = None
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
            self._terminate_process_group()
            self._join_threads(
                controller_thread=controller_thread,
                product_thread=product_thread,
                stderr_thread=stderr_thread,
            )
        except BaseException as error:
            if self._failure_reason is None:
                reason = "process_setup" if not threads else "proxy_error"
                self._fail(reason, detail=repr(error))
            returncode = 70
        finally:
            cleanup_error = self._cleanup_process_tree()
            if cleanup_error is not None:
                self._fail("process_cleanup", detail=repr(cleanup_error))
                returncode = 70
            if any(thread.is_alive() for thread in threads):
                self._stop.set()
                self._close_process_streams()
                for thread in threads:
                    thread.join(timeout=2)
            self._close_process_streams()
            if self._process is not None:
                self._events.write(
                    "downstream_exit",
                    returncode=returncode,
                    failure_reason=self._failure_reason,
                    signal=self._signal_number,
                )
            self._restore_signal_handlers(previous_handlers)
        if self._failure_reason is not None:
            return 128 + self._signal_number if self._signal_number is not None else 70
        return returncode

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
        except (BrokenPipeError, OSError, ValueError) as error:
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

    def _join_threads(
        self,
        *,
        controller_thread: threading.Thread,
        product_thread: threading.Thread,
        stderr_thread: threading.Thread,
    ) -> None:
        threads = [controller_thread, product_thread, stderr_thread]
        if self._failure_reason is None:
            product_thread.join()
            stderr_thread.join()
        else:
            product_thread.join(timeout=2)
            stderr_thread.join(timeout=2)
        controller_thread.join(timeout=1)
        if controller_thread.is_alive() and os.name == "nt" and self._windows_job is not None:
            deadline = time.monotonic() + 1
            while controller_thread.is_alive() and time.monotonic() < deadline:
                try:
                    self._windows_job.cancel_thread_io(controller_thread)
                except BaseException as error:
                    self._fail("controller_shutdown", detail=repr(error))
                    break
                controller_thread.join(timeout=min(0.05, max(0, deadline - time.monotonic())))
        if controller_thread.is_alive():
            self._close_controller_stream()
            controller_thread.join(timeout=1)
        if controller_thread.is_alive():
            self._fail("controller_shutdown")
        if any(thread.is_alive() for thread in threads):
            self._stop.set()
            self._close_process_streams()
            for thread in threads:
                thread.join(timeout=2)

    def _cleanup_process_tree(self) -> BaseException | None:
        if self._process is None:
            return None
        errors: list[BaseException] = []
        try:
            self._terminate_process_group()
        except BaseException as error:
            errors.append(error)
        if self._windows_job is not None:
            try:
                self._windows_job.wait_for_process_tree_exit(self._process.pid, 2)
            except BaseException as error:
                errors.append(error)
        if errors:
            try:
                self._terminate_windows_fallback()
            except BaseException as error:
                errors.append(error)
            if self._windows_job is not None:
                try:
                    self._windows_job.wait_for_process_tree_exit(self._process.pid, 2)
                except BaseException as error:
                    errors.append(error)
        job = self._windows_job
        if job is not None:
            closed = False
            try:
                job.close()
                closed = True
            except BaseException as error:
                errors.append(error)
                try:
                    self._terminate_windows_fallback()
                except BaseException as fallback_error:
                    errors.append(fallback_error)
                try:
                    job.close()
                    closed = True
                except BaseException as retry_error:
                    errors.append(retry_error)
            if closed:
                self._windows_job = None
        if errors:
            return RuntimeError("; ".join(str(error) for error in errors))
        return None

    def _terminate_process_group(self) -> None:
        if self._process is None:
            return
        if self._windows_job is not None:
            self._windows_job.terminate()
            if self._process.poll() is None:
                try:
                    self._process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    self._process.kill()
                    self._process.wait(timeout=2)
            self._windows_job.wait_for_process_tree_exit(self._process.pid, 2)
            return
        if os.name == "nt":
            self._terminate_windows_fallback()
            return
        try:
            os.killpg(self._process.pid, signal.SIGTERM)
        except ProcessLookupError:
            return
        deadline = time.monotonic() + 1
        while self._posix_process_group_alive() and time.monotonic() < deadline:
            if self._process.poll() is None:
                try:
                    self._process.wait(timeout=min(0.05, max(0, deadline - time.monotonic())))
                except subprocess.TimeoutExpired:
                    pass
            else:
                time.sleep(0.01)
        if self._posix_process_group_alive():
            try:
                os.killpg(self._process.pid, signal.SIGKILL)
            except ProcessLookupError:
                return
            if self._process.poll() is None:
                self._process.wait(timeout=1)

    def _posix_process_group_alive(self) -> bool:
        assert self._process is not None
        try:
            os.killpg(self._process.pid, 0)
        except ProcessLookupError:
            return False
        except PermissionError:
            return True
        return True

    def _terminate_windows_fallback(self) -> None:
        if self._process is None:
            return
        result = subprocess.run(
            ["taskkill", "/PID", str(self._process.pid), "/T", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
            timeout=2,
        )
        taskkill_error: RuntimeError | None = None
        if result.returncode != 0:
            taskkill_error = RuntimeError(f"taskkill exited with status {result.returncode}")
        try:
            self._process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            self._process.kill()
            self._process.wait(timeout=2)
        if self._process.poll() is None:
            raise RuntimeError("Windows process remained alive after tree teardown")
        if self._windows_job is not None:
            self._windows_job.wait_for_process_tree_exit(self._process.pid, 2)
        if taskkill_error is not None:
            raise taskkill_error

    def _wait_for_process(self) -> int:
        assert self._process is not None
        try:
            return self._process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            self._terminate_process_group()
            return self._process.wait(timeout=2)

    def _close_process_streams(self) -> None:
        if self._process is None:
            return
        for stream in (self._process.stdin, self._process.stdout, self._process.stderr):
            if stream is not None:
                try:
                    stream.close()
                except OSError:
                    pass

    def _close_controller_stream(self) -> None:
        stream = getattr(sys.stdin, "buffer", sys.stdin)
        try:
            stream.close()
        except (OSError, ValueError):
            pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--events", type=Path, required=True)
    parser.add_argument("--tokenizer", choices=["o200k_base"], required=True)
    parser.add_argument("--max-calls", type=int, required=True)
    parser.add_argument("--max-output-tokens", type=int, required=True)
    parser.add_argument("--cwd", type=Path, required=True)
    parser.add_argument("--product-env", action="append", default=[])
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.command[:1] == ["--"]:
        args.command = args.command[1:]
    if not args.command:
        parser.error("a product command is required after --")
    if args.max_calls < 1 or args.max_output_tokens < 1:
        parser.error("budgets must be positive")
    product_environment: dict[str, str] = {}
    for item in args.product_env:
        if "=" not in item:
            parser.error("--product-env must be NAME=VALUE")
        name, value = item.split("=", 1)
        if not name or name in product_environment or "\0" in value:
            parser.error("--product-env names must be unique and non-empty")
        product_environment[name] = value
    args.product_environment = product_environment
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
            args.product_environment,
        ).run()
    finally:
        events.close()


if __name__ == "__main__":
    raise SystemExit(main())
