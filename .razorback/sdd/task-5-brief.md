### Task 5: Add cancellable, current-user Windows named-pipe transport

**Files:**
- Modify: sidecar `Cargo.toml`
- Modify: sidecar `Cargo.lock`
- Modify: sidecar `src/main.rs`
- Modify: sidecar `src/broker/mod.rs`
- Modify: sidecar `src/broker/transport/mod.rs`
- Modify: sidecar `src/broker/transport/unix.rs`
- Create: sidecar `src/broker/transport/windows.rs`
- Test: sidecar `tests/broker_windows_tests.rs`
- Modify: sidecar `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: Task 4 transport module/Unix adapter and Task 1 identity-derived full server pipe name.
- Produces: the narrow listener/connection transport trait shared with Unix, plus an overlapped `CreateNamedPipeW` server with cancellation, `PIPE_REJECT_REMOTE_CLIENTS`, byte-mode NDJSON, and current-user ACL.

**Contract inputs:** External API Grounding URLs; `windows-sys = 0.61.2` target-specific features `Win32_Foundation`, `Win32_Security`, `Win32_Storage_FileSystem`, `Win32_System_IO`, `Win32_System_Pipes`, `Win32_System_Threading`. `ReadFile` and `WriteFile` are exposed through `Win32_Storage_FileSystem` in this exact crate version.

**File ownership:** Sidecar transport abstraction, its Unix adaptation, Windows transport, CLI/broker platform dispatch, target dependency, and Windows tests/CI only.

**Serialization required:** Yes.

**Dependency reason:** Extends Task 4's transport seam.

**Step 1: Write Windows-only failing tests**

```rust
#[cfg(windows)]
#[test]
fn cancelled_read_releases_the_pipe_instance_within_one_second() { /* real named pipe */ }

#[cfg(windows)]
#[test]
fn pipe_rejects_a_security_token_outside_the_current_user_acl() { /* ACL inspection */ }
```

Also start three clients, kill one mid-line, and prove the other two still complete requests.

**Step 2: Verify red on `windows-2022`**

Run: `cargo test --test broker_windows_tests -- --nocapture`

**Step 3: Implement overlapped transport**

```rust
let handle = CreateNamedPipeW(
    name,
    PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
    PIPE_UNLIMITED_INSTANCES,
    64 * 1024,
    64 * 1024,
    0,
    &security_attributes,
);
```

Every pending connect/read/write owns an `OVERLAPPED` event and is completed or canceled with `CancelIoEx` before its buffers/events are dropped. Do not use `std::fs::File` blocking reads and do not document a timeout as a no-op.

The sidecar receives the full server form `\\.\pipe\<name>`. Miller derives the short `<name>` from the same identity for `NamedPipeClientStream(".", name, ...)`; no caller passes the full Win32 path into the .NET client.

**Step 4: Run Windows broker, fast, and package-layout gates**

Add an explicit `windows-x64 broker lifecycle` CI step before model-backed conformance.

**Step 5: Apply commit mode**

`serial-worker-commit`: commit after Windows evidence is attached to the ledger.

**Acceptance criteria:**
- [ ] Connect, read, and write cancellation complete within one second in tests.
- [ ] Remote clients are rejected and ACL is current-user scoped.
- [ ] Client death mid-line cannot wedge an instance or another client.
- [ ] Windows broker lifecycle runs on every push to `main`, not only workflow dispatch.

