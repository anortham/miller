# Task 5 report: Windows named-pipe broker transport

## Repository state

- Worktree: `/Users/murphy/source/julie-semantic-sidecar/.worktrees/shared-semantic-broker`
- Branch: `codex/shared-semantic-broker`
- Baseline HEAD: `847cba5b9ee5972124b42735b123ddba6c8322b6`
- Task 5 commit: `d468402` (`feat: add Windows semantic broker transport`)
- No push was made.
- The commit contains only the Task 5 owned files:
  - `.github/workflows/ci.yml`
  - `Cargo.lock`
  - `Cargo.toml`
  - `src/main.rs`
  - `src/broker/mod.rs`
  - `src/broker/transport/mod.rs`
  - `src/broker/transport/unix.rs`
  - `src/broker/transport/windows.rs`
  - `tests/broker_windows_tests.rs`

## RED

The Windows-only integration tests were written before the production transport. They cover:

- connect cancellation within one second;
- read cancellation within one second;
- write cancellation within one second;
- a one-ACE DACL whose SID equals the process token's `TOKEN_USER`;
- rejection of a `\\localhost\pipe\...` client while the local current-user client connects;
- three clients where one dies mid-line and the other two continue;
- owner stdin EOF with no Windows endpoint/PID cleanup files;
- connection-scoped `shutdown`.

Attempted RED command:

```text
cargo check --target x86_64-pc-windows-msvc --test broker_windows_tests
```

The target standard library installed successfully, but this macOS host could not reach the Rust
test compilation. The transitive `ring` C build invoked Clang for
`--target=x86_64-pc-windows-msvc` and failed because the MSVC SDK headers are not installed:

```text
ring-core/check.h:27:11: fatal error: 'assert.h' file not found
```

Therefore no Windows RED runtime result is claimed. The test was demonstrably absent from the
baseline and referenced the not-yet-existing Windows transport/API, but only a Windows runner can
execute the required behavioral RED/GREEN cycle. CI execution requires a push and remains
approval-gated.

## Implementation

- Added a platform-neutral broker `Listener`/`Connection` seam. The common broker knows only
  `bind`, `accept`, `Read`, and `Write`; platform details remain in adapters.
- Adapted Unix sockets to the seam. The existing Unix endpoint setup, stale-socket removal,
  permissions, framing, queueing, and connection-scoped shutdown behavior remain unchanged.
- Added Windows `BrokerEndpoint::Windows(String)` and CLI dispatch for the full server endpoint
  `\\.\pipe\<name>`.
- Added target-specific `windows-sys = 0.61.2`.
- Added an overlapped `CreateNamedPipeW` server using:
  - `PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED`;
  - `PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS`;
  - `PIPE_UNLIMITED_INSTANCES`;
  - 64 KiB input and output buffers.
- Added a current-process-token-user DACL with one `ACCESS_ALLOWED_ACE`.
- Added cancellable overlapped connect/read/write operations with one manual-reset event and one
  pinned `OVERLAPPED` allocation per operation.
- Corrected the Win32 immediate-completion paths after lead review against Microsoft documentation:
  - a nonzero `ConnectNamedPipe` result is already complete and now calls
    `finish_without_wait` instead of `GetOverlappedResult`;
  - `ReadFile` and `WriteFile` receive non-null immediate byte-count pointers;
  - a nonzero read/write result returns that immediate byte count without calling
    `GetOverlappedResult`;
  - only `ERROR_IO_PENDING` reaches the blocking `GetOverlappedResult` path;
  - `GetOverlappedResult` receives the mutable pointer to the pinned `OVERLAPPED`.
- Closed a cancellation/last-error race found during Grok review. A failed
  `ConnectNamedPipe`, `ReadFile`, or `WriteFile` now captures `GetLastError` immediately after the
  Win32 call and before checking the atomic cancellation latch or calling `CancelIoEx`.
  `finish_io` receives that captured `Option<u32>` instead of reading thread-local last-error
  after cancellation could overwrite it.
- Added an atomic cancellation latch so cancellation requested just before the worker issues its
  Win32 call is consumed immediately after issuance; this closes the scheduling race where
  `CancelIoEx` could otherwise observe no pending request.
- Added a `windows-x64 broker lifecycle` job. It runs before model-backed conformance because the
  conformance job now depends on it. The lifecycle job runs for pull requests, pushes to `main`,
  and manual dispatches, which is stronger than the required every-main-push rule.

## External API grounding

Primary sources checked before implementation:

- `CreateNamedPipeW`, including `FILE_FLAG_OVERLAPPED`, `PIPE_REJECT_REMOTE_CLIENTS`, unlimited
  instances, and security-attribute behavior:
  <https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-createnamedpipew>
- `ConnectNamedPipe`, including the required non-null `OVERLAPPED`, manual-reset event,
  `ERROR_IO_PENDING`, and the valid `ERROR_PIPE_CONNECTED` race:
  <https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-connectnamedpipe>
- `CancelIoEx`, including the rule that the `OVERLAPPED` cannot be freed or reused until the I/O
  itself completes:
  <https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex>
- `GetOverlappedResult`:
  <https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-getoverlappedresult>
- `ReadFile` and `WriteFile`, including buffer lifetime and `lpNumberOfBytes* = NULL` for
  overlapped handles:
  <https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-readfile>
  <https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-writefile>
- Named-pipe security and DACL access checks:
  <https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights>
- Process token and `TOKEN_USER` retrieval:
  <https://learn.microsoft.com/en-us/windows/win32/secauthz/access-tokens>
- Exact `windows-sys` 0.61.2 generated signatures:
  <https://docs.rs/windows-sys/0.61.2/windows_sys/Win32/System/Pipes/fn.CreateNamedPipeW.html>
  <https://docs.rs/windows-sys/0.61.2/windows_sys/Win32/System/Pipes/fn.ConnectNamedPipe.html>
  <https://docs.rs/windows-sys/0.61.2/windows_sys/Win32/System/IO/fn.CancelIoEx.html>
  <https://docs.rs/windows-sys/0.61.2/windows_sys/Win32/Storage/FileSystem/fn.ReadFile.html>
  <https://docs.rs/windows-sys/0.61.2/windows_sys/Win32/Storage/FileSystem/fn.WriteFile.html>

Plan correction: `windows-sys` 0.61.2 places `ReadFile`, `WriteFile`,
`FILE_FLAG_OVERLAPPED`, and `PIPE_ACCESS_DUPLEX` under
`Win32::Storage::FileSystem`. The implementation therefore adds the target-specific
`Win32_Storage_FileSystem` feature in addition to the five features listed in the plan. Omitting
it cannot compile the required nonblocking server.

## Win32 safety invariants

- No server I/O uses `std::fs::File`; all server connect/read/write calls are overlapped Win32 I/O.
- Each operation owns a separately allocated manual-reset event.
- Each `OVERLAPPED` is pinned before its address reaches Win32.
- Read/write caller buffers stay borrowed until the Win32 call reports immediate completion or
  `GetOverlappedResult` reports pending-operation completion.
- Immediate `ReadFile`/`WriteFile` completions return the byte count written through the non-null
  `lpNumberOfBytesRead`/`lpNumberOfBytesWritten` pointer; they never query a completed operation
  through `GetOverlappedResult`.
- Failed connect/read/write calls snapshot thread-local last-error before any cancellation call;
  later error classification uses only the snapshot.
- `PendingIo::drop` calls `CancelIoEx` and then waits in `GetOverlappedResult` before the event or
  `OVERLAPPED` allocation can be dropped.
- `Connection` clones share an `Arc`-owned pipe handle, so a handle cannot close while another
  clone has a pending operation.
- A client EOF/broken-pipe error ends only that connection thread; dropping the last connection
  clone disconnects and closes only that named-pipe instance.
- The accept loop creates a fresh unlimited-instance server handle for each client.
- Pending accept handles are registered under a mutex before waiting; cancellation holds that
  mutex while issuing `CancelIoEx`, preventing close/reuse races.
- Token and ACL buffers use `Vec<usize>`, not `Vec<u8>`, so casts to `TOKEN_USER`, `ACL`, and ACE
  structures are aligned.
- The token buffer remains live until the SID has been copied into the ACL.
- The ACL allocation remains live for the whole security-descriptor/CreateNamedPipeW call.
- Windows never unlinks an endpoint and creates no endpoint, PID, state, token, or cleanup file.

## GREEN and verification

Fresh local gates:

```text
cargo test
```

- 236 passed
- 0 failed
- 25 ignored model-backed tests
- Windows-only target compiled as 0 tests on macOS, as expected

```text
cargo clippy --all-targets -- -D warnings
cargo fmt --all -- --check
python3 -B -m unittest discover -s scripts/tests -p 'test_*.py'
```

- Clippy: pass
- Formatting: pass
- Python harness: 30 passed

Focused Unix regression:

```text
cargo test --test broker_lifecycle_tests --test broker_protocol_tests
```

- 12 passed
- 0 failed

Cross-target static evidence:

- Installed `x86_64-pc-windows-msvc` with `rustup target add`.
- Compiled `src/broker/transport/windows.rs` directly as Windows metadata against an exact-feature
  `windows-sys` 0.61.2 metadata build with `-D warnings`: pass.
- Compiled `tests/broker_windows_tests.rs` as Windows test metadata with `-D warnings`, exact
  Windows dependency metadata, and a shape-only stub of the sidecar seam: pass.
- Re-ran both isolated Windows metadata checks after the immediate-completion fix: pass with
  `-D warnings`.
- Re-ran both isolated Windows metadata checks after the captured-last-error race fix: pass with
  `-D warnings`.
- `buffered_read_and_write_return_the_exact_immediate_byte_counts` now exercises buffered
  read/write traffic and requires exact immediate byte counts.
- `git diff --check`: pass.
- Miller `impact` maps the platform seam to the Unix lifecycle/protocol suites and the new Windows
  lifecycle suite; those are the selected verification surfaces.

Missing evidence:

- No Windows runtime test ran on this Mac.
- Full-package `cargo check --target x86_64-pc-windows-msvc` is blocked by the missing MSVC C SDK
  headers before it reaches sidecar Rust code.
- The new `windows-2022` lifecycle job cannot run without an approval-gated push.

## Architecture Quality

- **Affected modules:** broker orchestration, transport seam, Unix adapter, new Windows adapter,
  Windows CLI dispatch, Windows lifecycle CI.
- **Caller-facing interface:** common broker callers see only `transport::bind`,
  `Listener::accept`, and a connection implementing `Read + Write`.
- **Depth/locality check:** ACL construction, handle ownership, overlapped state, cancellation, and
  named-pipe flags are all local to `transport/windows.rs`.
- **Test surface:** Unix callers use the same broker lifecycle/protocol tests; Windows tests use
  the real Windows adapter and full broker process seam.
- **Seams/adapters:** the seam now has two concrete adapters and earns its keep. Deleting it would
  spread platform branches through connection framing and queue orchestration.
- **Rejected shortcuts:** blocking `std::fs::File` server I/O, default DACLs, NULL DACLs, polling
  timeouts, PID/state files, stale-path cleanup, environment knobs, leaked events, and dropping
  pending `OVERLAPPED` buffers.
- **Architecture risk:** medium until the `windows-2022` runtime lifecycle job passes; low for the
  Unix regression surface based on fresh local gates.

Checklist:

- Keeps platform complexity local: yes.
- Caller-facing interface is smaller than the behavior unlocked: yes.
- Tests use the same adapter/broker interfaces as production: yes.
- The seam has two real adapters: yes.
- No speculative transport framework or extra endpoint type was added: yes.
- The structural cause was fixed by making transport explicit, not by adding Windows branches
  throughout broker framing/scheduling: yes.

## Judgment calls

- Used a single current-token-user allow ACE with `GENERIC_ALL`. The DACL contains no Everyone,
  anonymous, administrator, or LocalSystem ACE; `PIPE_REJECT_REMOTE_CLIENTS` independently rejects
  remote clients.
- Kept Windows cancellation controls on the Windows adapter. The common broker seam remains
  platform-neutral.
- Made the Windows lifecycle job run on all CI events so a pull request can catch Windows
  regressions before merge, while still satisfying every push to `main`.
- Did not amend the Miller plan or contract from the sidecar task. The extra required
  `Win32_Storage_FileSystem` feature is reported here for lead-owned plan reconciliation.
