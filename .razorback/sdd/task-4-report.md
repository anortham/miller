# Task 4 report: lease-owned Unix broker and bounded scheduler

## Result

Implemented the Unix shared semantic broker in
`/Users/murphy/source/julie-semantic-sidecar/.worktrees/shared-semantic-broker`.
The broker acquires the model service lock before starting its owner watcher or loading the model,
owns the accelerator-lock handle inside the scheduler engine, serves multiple UDS connections, and
uses a bounded weighted queue.

After clean lead and Grok review, the verified slice was committed locally as
`847cba5b9ee5972124b42735b123ddba6c8322b6`
(`feat: add shared Unix semantic broker`). Nothing was pushed, tagged, published, or released.

## TDD evidence

Initial RED:

```text
cargo test --test broker_lifecycle_tests --test broker_scheduler_tests
error[E0433]: cannot find `broker` in `julie_semantic_sidecar`
error[E0432]: unresolved import `julie_semantic_sidecar::broker`
exit 101
```

The first RED run also exposed an incorrect `fs4` test call. I corrected the test to the repository's
required fully-qualified `FileExt::try_lock` API and reran RED until the only failures were the missing
broker interfaces.

Additional RED cycles:

- Broker oversized-line test returned `invalid_json` instead of the frozen oversized
  `invalid_request`, proving the first local framing implementation had drifted.
- Saturated-shutdown test did not compile until a reusable protocol-owned internal-error renderer
  existed.
- Lease-ownership test did not compile until `BrokerEngine` received the real
  `Option<AcceleratorLease>` instead of a boolean.

Final focused GREEN:

```text
cargo test --test broker_lifecycle_tests --test broker_scheduler_tests --test broker_protocol_tests
broker_lifecycle_tests: 6 passed
broker_protocol_tests: 6 passed
broker_scheduler_tests: 3 passed
0 failed
```

## Implemented behavior

- Added the exact additive `broker --model --endpoint --lock --accelerator-lock` CLI surface.
- Added `broker::serve(BrokerConfig)` plus a real engine-loader seam used by tests and by the
  production `BrokerEngine`.
- Acquires the model service lock before owner-watchdog startup and model construction.
- Acquires the user-global accelerator lock as an optional OS lease and transfers the handle into
  `BrokerEngine`, so Task 6 can drop/release it locally during CPU demotion.
- Arms owner stdin before cold load. EOF removes a bound Unix endpoint and exits the process even
  while a non-cancellable loader is blocked; the OS releases both lock handles.
- Leaves crash socket residue, while both locks are released by the OS. Only the next service-lock
  holder removes that socket immediately before bind.
- Creates/secures the broker directory as `0700` and socket as `0600`.
- Accepts multiple Unix connections. A protocol `shutdown` reply is flushed before closing only that
  connection; service and accelerator locks remain held and a second connection stays healthy.
- Uses a 64-item `Mutex` + `Condvar` queue. While batch work waits, at most eight interactive
  dequeues occur before one batch dequeue.
- Rejects queue saturation and expiry through the existing protocol-v1 `internal_error` serializer.
  Saturation does not poison the connection.
- Arms a process-fatal 60-second watchdog around engine entry.
- Added a non-Unix temporary unsupported branch so Task 4 does not unconditionally construct a Unix
  endpoint on Windows before Task 5 adds the named-pipe transport.
- Updated README and AGENTS without adding environment knobs, PID/state files, tokens, HTTP, process
  polling, or self-reaper behavior.

## Protocol ownership extension

Task 4 modified `src/protocol.rs`, which was not in its original file list, after lead review found
that a broker-local line reader and JSON error serializer would drift from the frozen stdio contract.
The extension is deliberately small and transport-neutral:

- `read_request` / `FramedRequest` reuse the existing bounded reader and exact oversized response.
- `internal_error_reply` reuses the existing `internal_error` and `serialize_outcome` implementation.

Both stdio and broker mode now use the same framing response for an oversized line. The end-to-end
broker test sends `max_request_bytes + 1`, verifies the exact `invalid_request` class and limit
message, then verifies a healthy request on the same connection.

## Real-process coverage

- Eight simultaneous helpers: exactly one engine load; seven lock losers exit before loading.
- Owner EOF after bind: socket removed; model-service and accelerator locks both reacquirable.
- Owner EOF during a blocked loader: no endpoint bind; both locks reacquirable.
- Hard-killed owner: socket remains, both locks reacquirable, a service-lock loser cannot unlink it,
  and the next holder recovers.
- Connection-scoped shutdown: requesting connection reaches EOF, second connection remains healthy,
  process stays live, and both locks remain contended.
- 66 live connections against a blocked engine: one receives protocol-owned queue-full
  `internal_error`; after release, that same connection serves health.

## Verification

Fresh after the final source change:

```text
cargo test
all non-ignored tests passed; 0 failed

cargo clippy --all-targets -- -D warnings
exit 0

cargo fmt --check
exit 0

python3 -B -m unittest discover -s scripts/tests -p 'test_*.py'
Ran 30 tests; OK

cargo test --release --test conformance -- --ignored --test-threads=1 --nocapture
9 passed; 0 failed; model-backed BGE and Qwen golden lanes passed

git diff --check
exit 0
```

Only `aarch64-apple-darwin` is installed in this worktree's Rust toolchain, so no Windows target
compile was claimed. The non-Unix cfg path is present and warnings-clean on the current build graph;
Task 5 still owns real `windows-2022` compile, named-pipe, cancellation, ACL, and lifecycle evidence.

## Miller evidence

Workspace selector:
`/Users/murphy/source/julie-semantic-sidecar/.worktrees/shared-semantic-broker` with refresh-first
reads. Miller onboarding, search, context, full symbol inspection, and impact were used before and
after implementation.

Key verified interfaces:

- `protocol::process_line` returns `ProtocolReply { line, stop_connection }`.
- `protocol::read_request` is shared by stdio and broker transports.
- `broker::serve_with_loader` keeps the non-`Send` llama engine on the scheduler thread.
- `BrokerEngine` owns the accelerator lease.
- `handle_connection` admits one request at a time and loops after framing, saturation, expiry, and
  application responses.

Post-change impact identifies the protocol regression suite, CLI tests, broker tests, stdio
conformance, and serve tests as the relevant consumers/gates.

## Architecture Quality

**Affected modules:** CLI dispatch, broker lifecycle, OS leases, Unix transport, protocol framing,
engine adapter, scheduler queue, watchdog.

**Caller-facing interface:** `broker::serve(BrokerConfig)` is the production surface.
`serve_with_loader` is the engine-construction seam used by real-process tests and is also the seam
Task 6 needs for backend policy. `BrokerEngine` hides the concrete engine plus accelerator lease.

**Depth/locality check:** Lock ordering, endpoint cleanup, queue policy, request serialization, and
engine lifetime stay inside the broker module. Miller callers only need the frozen four launch
arguments and protocol-v1 transport.

**Test surface:** Real spawned broker processes and actual Unix sockets exercise the same lifecycle,
queue, framing, and protocol interfaces production uses. The scheduler policy is separately tested
through `BrokerQueue`.

**Seams/adapters:** The transport directory isolates Unix now and Windows Task 5 later. The
transport-neutral protocol framing/error helpers prevent two wire implementations. The loader seam
earns its keep because production llama construction and deterministic fake construction both use it,
and Task 6 needs to consume the actual accelerator lease.

**Rejected shortcuts:** duplicate JSON envelope rendering; duplicate broker line framing; a detached
daemon; PID/state files; a static engine; per-connection engine entry; unbounded channels; a
production environment test hook; a boolean-only accelerator seam; a Windows blocking-I/O placeholder.

**Architecture risk:** medium. Process-fatal ownership and one scheduler thread keep failure and
engine concurrency local, but Task 5 must still prove Windows cancellation/ACL behavior and Task 6
must complete backend policy and typed OOM recovery.

Canonical checklist:

- Complexity stays local to `broker` plus two small protocol-owned helpers.
- The launch and serving interfaces are smaller than the lifecycle/scheduling behavior.
- Tests use real processes, sockets, locks, and protocol requests.
- Loader and transport seams have concrete current and next-task consumers.
- No speculative daemon, registry, auth, or restart machinery was added.
- The structural causes of duplicate protocol rendering and stranded accelerator ownership were
  fixed.

## Judgment calls and remaining evidence

- Task 4 creates and owns both OS lock lifetimes but intentionally does not choose `Auto` versus
  `CpuOnly`, classify OOM, demote, update health, or retry. Those remain Task 6.
- The 60-second watchdog uses the production constant and `std::process::abort`; the fast suite does
  not wait 60 seconds or introduce a test-only timeout hook.
- Expired work is proven to bypass engine entry and use the protocol-owned internal-error renderer.
  Queue-full recovery is additionally proven over a real connection.
- Windows named-pipe behavior is not implemented or claimed in Task 4.

## Final repository state

```text
path:   /Users/murphy/source/julie-semantic-sidecar/.worktrees/shared-semantic-broker
branch: codex/shared-semantic-broker
HEAD:   847cba5b9ee5972124b42735b123ddba6c8322b6
dirty:  clean
```

Related worktree inventory was rechecked after commit. The shared broker worktree is clean. The main
checkout's pre-existing `.DS_Store` files and the accelerated-backends worktree's pre-existing
ahead-of-origin commits remain untouched.
