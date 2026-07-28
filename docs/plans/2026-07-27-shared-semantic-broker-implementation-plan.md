# Shared Semantic Broker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Replace Miller's per-process semantic child with one lease-owned, user-local semantic broker shared by concurrent Miller sessions, then make semantic retrieval default-on without allowing multiple sessions to overcommit GPU memory.

**Architecture:** `julie-semantic-sidecar` gains a `broker` mode that keeps the frozen `julie.embedding.sidecar` v1 envelopes on each local IPC connection while moving lifecycle, scheduling, and accelerator ownership into a separate broker contract. One Miller process owns the broker through stdin EOF and, on Windows, a kill-on-close Job Object; all Miller processes may connect through a Unix-domain socket or cancellable Windows named pipe. The broker owns only an embedding engine and bounded queues; Miller retains every workspace, vector artifact, ranking, telemetry, and fallback decision.

**Tech Stack:** .NET 10, C# `System.IO.Pipes` and Unix-domain sockets, Native-AOT-safe Win32 interop, Rust 1.82, `windows-sys` 0.61.2, `fs4`, frozen NDJSON sidecar protocol v1, llama.cpp, xUnit, Cargo tests, GitHub Actions, PowerShell hardware soak.

**Architecture Quality:** Approved shape: a narrow compute broker with one engine thread, one user/broker-contract/protocol/model service identity, one user-global accelerator lease, no workspace awareness, and no durable control plane. The main risks are cancellable Windows named-pipe I/O, owner-death cleanup, multi-version coexistence, and GPU resource exhaustion. If live code requires HTTP, PID files, state files, token files, detached ownership, broker-initiated version restarts, or access to `vectors.db`, report a plan mismatch instead of adding that machinery.

## Global Constraints

- The Miller `v1.14.0` release remains paused until this plan's Task 10 gates pass and the user gives fresh release approval.
- `MILLER_SEMANTIC=off` remains a permanent zero-work guarantee: no endpoint derivation, directory creation, connection, process launch, model preparation, vector read, or telemetry write caused solely by semantics.
- After Task 9, unset or blank `MILLER_SEMANTIC` means `SemanticMode.On`; `off|0|false` means Off; `shadow` means Shadow; `on|1|true` means On; an unrecognized value remains Off.
- No new MCP tool is added.
- `docs/contracts/semantic-sidecar-protocol-v1.md` remains frozen. Broker transport and lifecycle live in the separate `docs/contracts/semantic-broker-v1.md` contract.
- Existing sidecar `serve` stdio behavior and conformance remain byte-compatible for Julie, evaluation, package smoke, and tests.
- The broker serves only `health`, `embed_query`, `embed_batch`, and the existing protocol `shutdown`; it never receives a workspace root, database path, vector artifact, symbol identity, or retrieval policy. In broker mode, `shutdown` writes the normal response and closes only that client connection; it never stops the accept loop or broker process.
- `<workspace>/.miller/vectors.db` remains a Miller-owned, per-workspace derived artifact. The broker is stateless across process restarts except for existing sidecar model/backend caches.
- One user/broker-contract/protocol/model identity has at most one live model-loaded broker. Identity is SHA-256 over `julie.semantic.broker|1|julie.embedding.sidecar|1|<model_id>|<model_sha256>`, rendered as the first 16 lowercase hex characters. Binary version is deliberately excluded.
- All model identities share one user-global accelerator lock at `<miller-home>/semantic/accelerator-v1.lock`; only its holder may load an accelerated backend.
- A model-specific broker that does not hold the accelerator lock loads CPU directly. It never probes or allocates the GPU first.
- Runtime `ResourceExhausted` from an accelerated engine releases the accelerator lease, reloads the same model on CPU, retries the idempotent request once, and stays on CPU for the rest of that broker lifetime. Classification is typed and initially covers proven allocation failures such as `ContextAlloc`; ordinary `Decode`, `Encode`, item, or application failures do not demote unless separately proven and typed.
- The broker queue capacity is 64 total requests. While batch work is waiting, the scheduler dequeues at most eight interactive `health`/`embed_query` requests and then one `embed_batch`; full or expired work returns an `internal_error` envelope without closing the connection.
- Existing protocol budgets remain: 120 seconds for cold `health`, 30 seconds per embedding request, and 500 ms for stdio `shutdown`. Broker connect probing uses 250 ms; owner recovery must converge within 30 seconds on the target Windows laptop.
- A broker request active longer than 60 seconds causes the broker watchdog to terminate its own process so the OS releases the service and accelerator locks.
- IPC is current-user-only: Unix broker directory mode `0700`, socket mode `0600`; Windows pipe rejects remote clients and uses a current-user security descriptor.
- Only a service-lock holder may remove a stale Unix socket, and it does so before binding. Windows named pipes have no stale path and never use PID/state cleanup.
- Windows named-pipe connect, read, and write operations must be genuinely cancellable. A documented no-op timeout is a release blocker.
- The broker is not detached. Its stdin watcher is armed before model load; stdin EOF terminates the broker even during cold load. On Windows the owning Miller process attempts to assign it to a `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` Job Object before use; attach failure is visible as degraded ownership, while stdin EOF remains authoritative.
- The factory that successfully spawns retains broker ownership for its process lifetime. Owner disposal closes owner stdin and the Job handle; non-owner disposal closes only its client connections and never signals broker death.
- A client that loses the spawn race polls the deterministic endpoint through the full 120-second initialization budget before failing open to lexical.
- Version or model disagreement never restarts or kills an existing broker. Compatible clients connect to their deterministic identity; incompatible identities use another CPU broker or fail open to lexical.
- Every semantic failure returns a stated failed outcome to Miller; search continues lexically. No broker error may fault the MCP call or corrupt lexical byte identity.
- Sidecar logs and Miller telemetry never record query text, document text, paths, symbols, snippets, or vectors.
- `julie-semantic-sidecar` target version for this work is `0.1.0-rc.5`; Miller remains the already-prepared `1.14.0` candidate.
- Pushes, tags, GitHub releases, and the final Miller release require explicit user approval at the boundaries named in Tasks 9 and 10.

---

## File Structure

### Miller repository — `/Users/murphy/source/miller`

| File | Responsibility |
|---|---|
| `docs/contracts/semantic-broker-v1.md` | Frozen broker identity, IPC, owner lease, scheduling, failure, and compatibility contract. |
| `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs` | Transport-neutral semantic connection/session state machine; stdio remains one implementation. |
| `src/Miller.Indexing/Semantic/SemanticBrokerEndpoint.cs` | Deterministic broker-contract/protocol/model identity, paths, and Windows pipe name. |
| `src/Miller.Indexing/Semantic/SharedSemanticBrokerConnectionFactory.cs` | Connect-first, spawn-on-demand, reconnect, owner-process lifetime, and snapshot. |
| `src/Miller.Indexing/Semantic/WindowsBrokerJob.cs` | Native-AOT-safe Job Object setup and kill-on-close ownership. |
| `src/Miller.Indexing/Semantic/SemanticEmbeddingSessionBroker.cs` | Process-local query/batch fairness over the shared remote connection; no process ownership. |
| `src/Miller.Indexing/Semantic/SemanticSearchArm.cs` | Production factory switches from stdio child to shared broker. |
| `src/Miller.Server/Hosting/MillerServiceRegistration.cs` | Singleton production wiring using `ToolsRoot` and machine Miller home. |
| `src/Miller.Indexing/SemanticActivation.cs` | Default-on activation policy after all broker gates pass. |
| `src/Miller.Server/Tools/WorkspaceRender.cs` | Additive broker health in existing workspace status/health output. |
| `docs/contracts/vectors-v1.md` and `tests/Miller.Tests/Indexing/SemanticOffGuaranteeTests.cs` | Default-on wording plus explicit Off zero-work coverage for broker construction and path access. |
| `scripts/Miller.SemanticBrokerProbe/` | Real-process probe used by multi-session soak scripts. |
| `scripts/semantic-broker-soak.sh` / `.ps1` | Concurrent start, owner crash, model/version, process-count, latency, and GPU-memory gates. |

### Sidecar repository — `/Users/murphy/source/julie-semantic-sidecar`

| File | Responsibility |
|---|---|
| `src/protocol.rs` | Reusable single-line request processor plus unchanged stdio loop. |
| `src/broker/mod.rs` | Broker configuration and top-level lifecycle. |
| `src/broker/queue.rs` | Bounded weighted interactive/batch scheduler. |
| `src/broker/lease.rs` | Service and accelerator OS file locks. |
| `src/broker/watchdog.rs` | Owner-stdin EOF and 60-second active-request watchdog. |
| `src/broker/transport/mod.rs` | Platform-neutral listener/connection traits. |
| `src/broker/transport/unix.rs` | UDS bind, permissions, accept, cleanup. |
| `src/broker/transport/windows.rs` | Overlapped named-pipe server, cancellation, remote rejection, current-user ACL. |
| `src/broker/engine.rs` | Single-thread engine ownership, additive health facts, CPU demotion and one retry. |
| `src/engine_trait.rs` | Typed `ResourceExhausted` classification while preserving wire messages. |
| `src/engine.rs` | Explicit backend policy and resource-exhaustion classification. |
| `src/main.rs` | Additive `broker` CLI verb; existing verbs unchanged. |
| `AGENTS.md` and `README.md` | Additive broker CLI/lifecycle documentation without expanding the existing environment surface. |
| `.github/workflows/ci.yml` | Windows broker lifecycle lane and cross-platform broker contract tests. |

## External API Grounding

- .NET 10 `NamedPipeClientStream.ConnectAsync(TimeSpan, CancellationToken)` provides a bounded, cancellation-aware connect: <https://learn.microsoft.com/dotnet/api/system.io.pipes.namedpipeclientstream.connectasync?view=net-10.0>.
- Win32 named pipes require `FILE_FLAG_OVERLAPPED` for nonblocking/cancellable connect/read/write and support `PIPE_REJECT_REMOTE_CLIENTS`: <https://learn.microsoft.com/windows/win32/api/namedpipeapi/nf-namedpipeapi-createnamedpipew> and <https://learn.microsoft.com/windows/win32/ipc/named-pipe-open-modes>.
- `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` terminates associated processes when the last Job Object handle closes; nested jobs are supported on Windows 8 and later: <https://learn.microsoft.com/windows/win32/procthread/job-objects>.
- `windows-sys` 0.61.2 has MSRV 1.71, below this repo's Rust 1.82 floor, and exposes `Win32_System_Pipes` and `Win32_System_JobObjects`: <https://docs.rs/crate/windows-sys/0.61.2/source/Cargo.toml.orig>.

## Verification Strategy

**Project source of truth:** Miller `CLAUDE.md`/`AGENTS.md`, `docs/contracts/semantic-sidecar-protocol-v1.md`, `docs/release-process.md`; sidecar `AGENTS.md`, `.github/workflows/ci.yml`, and `scripts/hardware-smoke.{sh,ps1}`.

**Worker red/green scope:** Miller focused xUnit filters through `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter FullyQualifiedName~<test>`; sidecar focused targets through `cargo test <test-name>` plus `cargo fmt --all -- --check`.

**Worker ceiling:** Miller workers may run `scripts/test.sh` but not Scale/all or release packaging unless assigned. Sidecar workers may run `cargo test`, `cargo clippy --all-targets -- -D warnings`, `cargo fmt --all -- --check`, and Python harness tests, but not model-backed ignored tests or release workflows unless assigned.

**Worker gate invariant:** Each task's focused tests prove its interface and failure invariant; no worker may substitute compilation for the named behavior.

**Lead affected-change scope:** Miller `scripts/test.sh` after every coherent Miller batch; sidecar all four documented fast gates after every coherent sidecar batch.

**Branch gate:** Miller `scripts/test.sh all` plus `dotnet build Miller.slnx -c Release`; sidecar all four fast gates, broker integration targets on Unix and Windows, then the existing conformance matrix.

**Replay/metric evidence:** Hard gates are one model-loaded broker for N same-model clients, one accelerator lease across model identities, no request exceeding its client deadline, owner-crash recovery within 30 seconds, lexical success on every injected broker failure, GPU memory delta for N same-model sessions no more than one-session delta plus 256 MiB, and unchanged sidecar protocol conformance. Retrieval metrics are report-only unless they regress below the already-recorded policy-v2 hybrid results (recall@10 `0.703125`, nDCG `0.645212`, MRR `0.672771`, top-1 `0.618056`), in which case release stops for diagnosis.

**Escalation triggers:** Any edit to sidecar envelope fields/methods/error codes requires a v2 contract and owner decision. Any Windows cancellation failure, orphan broker, second accelerator holder, real-process hang, lexical-byte change, model identity mismatch, or GPU OOM without CPU recovery triggers the full platform/scale gates and blocks release.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, repo, branch, commit SHA, result, and timestamp in `docs/findings/2026-07-27-shared-semantic-broker-verification.md`. Hardware rows additionally record OS build, GPU/driver, VRAM, process tree, backend, one-session memory delta, N-session delta, recovery duration, and maximum request latency.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Freeze broker contract | None - serial | Miller contract, ADR/design/docs-map files only | Yes | All implementation consumes this contract. |
| Task 2: Extract reusable sidecar protocol processor | Batch A | Sidecar `src/protocol.rs`, protocol tests only | No | None - safe parallel batch. |
| Task 3: Make Miller sessions connection-factory based | Batch A | Miller session abstractions and existing fake/session tests only | No | None - safe parallel batch. |
| Task 4: Build Unix broker and scheduler | None - serial | Sidecar broker core, Unix transport, CLI/lib exports, broker tests | Yes | Requires Task 2's processor contract. |
| Task 5: Add hardened Windows transport | None - serial | Sidecar Windows transport, target dependency, Windows tests/CI only | Yes | Extends Task 4's transport seam. |
| Task 6: Add accelerator policy and OOM demotion | None - serial | Sidecar broker engine/lease, engine classification, recovery tests | Yes | Requires the working broker lifecycle and Task 4 lock primitive. |
| Task 7: Connect and supervise the broker from Miller | None - serial | Miller endpoint, shared connection factory, Windows Job Object, DI wiring, tests | Yes | Requires Tasks 3-6 interfaces. |
| Task 8: Prove multi-session behavior and dogfood | None - serial | Cross-repo integration tests, probe/soak scripts, verification ledger | Yes | Requires both repos integrated. |
| Task 9: Publish rc.5, pin it, switch default-on, expose health | None - serial | Sidecar version/release files; Miller pins, activation, status/health, docs/release notes | Yes | Requires Task 8 hard gates and explicit publish approval. |
| Task 10: Run release gates and close evidence | None - serial | Verification ledger and existing v1.14.0 release evidence/notes | Yes | Final acceptance and fresh release approval boundary. |

### Task 1: Freeze the broker lifecycle and transport contract

**Files:**
- Create: Miller `docs/contracts/semantic-broker-v1.md`
- Modify: Miller `docs/adr/ADR-0003-semantic-retrieval-ownership.md`
- Modify: Miller `docs/plans/2026-07-19-miller-semantic-integration-design.md`
- Modify: Miller `docs/plans/2026-07-21-semantic-production-readiness-repair-design.md`
- Modify: Miller `docs/README.md`
- Test: Miller `tests/Miller.Tests/Docs/SemanticBrokerContractTests.cs`

**Interfaces:**
- Consumes: frozen `julie.embedding.sidecar` v1 envelopes and `SemanticEncoderPin`.
- Produces: exact identity/hash algorithm, endpoint/lock layout, `broker` argv, owner lease, scheduling, security, OOM, compatibility, and fail-open rules used by every later task.

**Contract inputs:** Global Constraints and External API Grounding above.

**File ownership:** Miller contract, ADR/design/docs-map files only.

**Serialization required:** Yes.

**Dependency reason:** All implementation consumes this contract.

**Step 1: Write the failing contract guard**

```csharp
[Fact]
public void BrokerContract_LocksTheFailureProneLifecycleOut()
{
    string text = File.ReadAllText(RepoFile("docs/contracts/semantic-broker-v1.md"));
    Assert.Contains("stdin EOF", text, StringComparison.Ordinal);
    Assert.Contains("JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE", text, StringComparison.Ordinal);
    Assert.Contains("No PID file", text, StringComparison.Ordinal);
    Assert.Contains("No broker-initiated restart", text, StringComparison.Ordinal);
    Assert.Contains("PIPE_REJECT_REMOTE_CLIENTS", text, StringComparison.Ordinal);
    Assert.Contains("julie.semantic.broker|1|julie.embedding.sidecar|1|", text, StringComparison.Ordinal);
    Assert.Contains("shutdown closes only the requesting connection", text, StringComparison.Ordinal);
}
```

**Step 2: Run it to verify it fails**

Run: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter FullyQualifiedName~SemanticBrokerContractTests`

Expected: FAIL because `semantic-broker-v1.md` does not exist.

**Step 3: Write the contract**

The contract must include this normative command and identity:

```text
julie-semantic-sidecar broker \
  --model <model-id> \
  --endpoint <uds-path-or-full-pipe-name> \
  --lock <model-service-lock-path> \
  --accelerator-lock <user-global-accelerator-lock-path>

identity_input = "julie.semantic.broker|1|julie.embedding.sidecar|1|" + model_id + "|" + model_sha256
identity = lowercase_hex(sha256(UTF8(identity_input)))[0..16]
```

The deterministic layout is:

```text
<miller-home>/semantic/broker-<identity>.lock
<miller-home>/semantic/broker-<identity>.sock
<miller-home>/semantic/accelerator-v1.lock
miller-semantic-<identity>
\\.\pipe\miller-semantic-<identity>
```

The fourth form is the short pipe name passed to `.NET NamedPipeClientStream`; the fifth is the
full server pipe name passed to `CreateNamedPipeW`.

It must say:

- Each IPC connection carries frozen protocol-v1 NDJSON, one request in flight per connection, and multiple connections per broker.
- Unix endpoints are absolute socket paths. Windows derives both `\\.\pipe\<name>` for `CreateNamedPipeW` and the short `<name>` for `NamedPipeClientStream`.
- `shutdown` preserves stdio process-stop behavior but closes only the requesting broker connection.
- The stdin watcher is armed before model load; only the service-lock holder may unlink a stale Unix endpoint.
- Owner disposal closes stdin/Job ownership; non-owner disposal closes only client connections.
- Spawn losers poll the endpoint through the full initialization budget.
- While a batch waits, at most eight interactive dequeues precede one batch dequeue.

**Step 4: Run the focused docs gate**

Expected: PASS with all lifecycle exclusions and exact literals present.

**Step 5: Apply commit mode**

`serial-worker-commit`: commit the owned documentation/tests after focused verification and record the SHA.

**Acceptance criteria:**
- [x] Contract contains no PID, state, token, HTTP, port, workspace, DB, or self-update mechanism.
- [x] Existing sidecar protocol remains frozen and separately referenced.
- [x] ADR and historical design explicitly supersede process-local ownership with the approved broker.
- [x] Focused contract guard passes.

### Task 2: Extract a reusable sidecar protocol processor without changing stdio

**Files:**
- Modify: sidecar `src/protocol.rs:36-214`
- Test: sidecar `tests/protocol_tests.rs`
- Test: sidecar `tests/serve_tests.rs`

**Interfaces:**
- Consumes: frozen protocol-v1 request limits and `EmbedEngine`.
- Produces: `ProtocolReply` and `process_line`, usable by stdio and broker connections.

**Contract inputs:** Task 1 contract; protocol conformance A1-A23 and B1-B6.

**File ownership:** Sidecar `src/protocol.rs`, protocol tests only.

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**Step 1: Write failing equivalence tests**

```rust
#[test]
fn reusable_processor_matches_stdio_for_success_error_blank_and_shutdown() {
    for line in fixture_lines() {
        assert_eq!(processor_reply(line), stdio_reply(line));
    }
}
```

**Step 2: Verify red**

Run: `cargo test reusable_processor_matches_stdio`

Expected: FAIL because the reusable processor does not exist.

**Step 3: Extract the interface**

```rust
pub struct ProtocolReply {
    pub line: String,
    pub stop_connection: bool,
}

pub fn process_line<E: EmbedEngine>(
    line: &[u8],
    engine: &E,
    limits: RequestLimits,
) -> std::io::Result<Option<ProtocolReply>>;
```

`run_loop_with_limits` becomes only capped line reading plus `process_line` plus write/flush. Blank lines return `Ok(None)`. In stdio mode, `stop_connection` exits the process loop exactly as today. In broker mode, the response is flushed and only that connection handler exits; the accept loop, service lock, accelerator lease, and engine remain live. No envelope or error literal changes.

**Step 4: Run focused and full fast sidecar gates**

Run: `cargo test protocol_tests && cargo test serve_tests`

Then: `cargo test`, `cargo clippy --all-targets -- -D warnings`, `cargo fmt --all -- --check`, and the Python harness tests.

**Step 5: Apply commit mode**

`parallel-lead-commit`: hand the verified diff to the lead; do not commit from this lane.

**Acceptance criteria:**
- [x] Existing stdio output is byte-identical for every fixture row.
- [x] EOF and `shutdown` retain existing behavior in stdio mode.
- [x] Broker `shutdown` exposes connection-scoped stop only after its response is serialized; Task 4 proves multi-connection survival after flush.
- [x] No new protocol field, method, or error code exists.

### Task 3: Make Miller semantic sessions connection-factory based

**Files:**
- Modify: Miller `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs:60-1097`
- Modify: Miller `src/Miller.Indexing/Semantic/SemanticEvaluationAdapter.cs`
- Modify: Miller `scripts/Miller.PackageSemanticSmoke/PackageSemanticSmoke.cs`
- Modify: Miller `tests/Miller.Tests/Support/FakeSemanticSidecar.cs`
- Modify: Miller `tests/Miller.Tests/Indexing/SemanticEmbeddingSessionTests.cs`
- Modify: Miller `tests/Miller.Tests/Indexing/SemanticEmbeddingSessionBrokerTests.cs`
- Modify: Miller `tests/Miller.Tests/Indexing/SemanticEvaluationAdapterTests.cs`

**Interfaces:**
- Consumes: existing session retry/circuit/handshake behavior.
- Produces: async, transport-neutral `ISemanticSidecarConnectionFactory` and `ISemanticSidecarConnection`; `StdioSemanticSidecarConnectionFactory` preserves current process behavior.

**Contract inputs:** Task 1 client-disposal and deadline rules.

**File ownership:** Miller session abstractions and existing fake/session tests only.

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**Step 1: Write failing async-factory tests**

```csharp
[Fact]
public async Task TransportFailure_AbortsOnlyTheConnectionThenReconnectsThroughTheFactory()
{
    var factory = new SequencedConnectionFactory(faultedConnection, healthyConnection);
    await using var session = new SemanticEmbeddingSession(factory, expectedEncoder: Pin);
    Assert.True((await session.EmbedQueryAsync("natural language")).Success);
    Assert.Equal(2, factory.ConnectCount);
    Assert.True(faultedConnection.Aborted);
}
```

**Step 2: Verify red**

Run: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter FullyQualifiedName~SemanticEmbeddingSessionTests`

**Step 3: Replace process-specific interfaces**

```csharp
public interface ISemanticSidecarConnectionFactory : IAsyncDisposable
{
    ValueTask<ISemanticSidecarConnection> ConnectAsync(CancellationToken cancellationToken);
}

public interface ISemanticSidecarConnection : IAsyncDisposable
{
    TextWriter Input { get; }
    TextReader Output { get; }
    bool IsClosed { get; }
    void Abort();
}
```

`SemanticEmbeddingSession.StartIfNeededAsync` awaits `ConnectAsync`. Fatal recovery aborts only the connection. Session disposal always closes its connection. It disposes the factory only when constructed with explicit factory ownership for stdio/evaluation/test use; production broker sessions borrow the server/CLI-owned factory and never dispose it. The server host disposes its DI singleton, while `CliSemanticSession` disposes its invocation-wide factory after disposing the session.

**Step 4: Run focused and Miller fast gates**

Run the three semantic session test classes, then `scripts/test.sh`.

**Step 5: Apply commit mode**

`parallel-lead-commit`: hand the verified diff to the lead; do not commit from this lane.

**Acceptance criteria:**
- [x] All existing retry, circuit, handshake, application-error, timeout, and byte-identity tests remain green.
- [x] A connection factory may represent either a child process or shared IPC without session branching.
- [x] Session disposal cannot tear down a borrowed shared factory or broker owner lease.
- [x] Disposal has no implicit global `shutdown`.

### Task 4: Build the lease-owned Unix broker and bounded scheduler

**Files:**
- Modify: sidecar `src/lib.rs`
- Modify: sidecar `src/main.rs`
- Modify: sidecar `src/protocol.rs`
- Modify: sidecar `AGENTS.md`
- Modify: sidecar `README.md`
- Create: sidecar `src/broker/mod.rs`
- Create: sidecar `src/broker/queue.rs`
- Create: sidecar `src/broker/lease.rs`
- Create: sidecar `src/broker/watchdog.rs`
- Create: sidecar `src/broker/transport/mod.rs`
- Create: sidecar `src/broker/transport/unix.rs`
- Create: sidecar `src/broker/engine.rs`
- Test: sidecar `tests/broker_protocol_tests.rs`
- Test: sidecar `tests/broker_lifecycle_tests.rs`
- Test: sidecar `tests/broker_scheduler_tests.rs`

**Interfaces:**
- Consumes: Task 1 `broker` argv/identity and Task 2 `process_line`.
- Produces: `broker::serve(BrokerConfig)`, UDS transport, singleton-before-model-load, owner EOF, watchdog, and 64-item weighted scheduler.

**Contract inputs:** Current `LlamaEngine::load`, `UnreadyEngine`, `fs4`, request limits, current-user Unix permissions.

**File ownership:** Sidecar broker core, Unix transport, CLI/lib exports, broker tests.

**Serialization required:** Yes.

**Dependency reason:** Requires Task 2's processor contract.

**Task 4/Task 6 boundary:** Task 4 owns both OS-lock lifetimes: it creates the service-lock and
accelerator-lock primitives, holds any acquired handles for the broker lifetime, and proves owner EOF
releases both. Task 6 owns accelerator policy: only the accelerator-lock holder may select `Auto`, a
non-holder selects `CpuOnly` without a GPU probe, and typed runtime resource exhaustion demotes and
retries once. Task 4 must not invent Task 6's backend-selection or demotion policy.

**Step 1: Write failing real-process tests**

```rust
#[test]
fn concurrent_broker_starts_load_one_engine_and_losers_exit() { /* 8 processes */ }

#[test]
fn owner_stdin_eof_removes_socket_and_releases_lock() { /* close owner pipe */ }

#[test]
fn waiting_batch_runs_after_at_most_eight_interactive_dequeues() { /* fake engine */ }

#[test]
fn stale_socket_is_unlinked_only_after_service_lock_acquisition() { /* hard-kill first owner */ }

#[test]
fn shutdown_response_closes_only_its_connection() { /* second connection remains healthy */ }

#[test]
fn owner_eof_during_model_load_terminates_before_endpoint_bind() { /* blocking fake loader */ }
```

**Step 2: Verify red**

Run: `cargo test --test broker_lifecycle_tests --test broker_scheduler_tests`

**Step 3: Implement the broker core**

```rust
pub struct BrokerConfig {
    pub model_id: String,
    pub endpoint: BrokerEndpoint,
    pub service_lock: PathBuf,
    pub accelerator_lock: PathBuf,
}

pub fn serve(config: BrokerConfig) -> std::io::Result<()> {
    let service_lease = ServiceLease::try_acquire(&config.service_lock)?;
    let owner = OwnerWatchdog::start(std::io::stdin())?;
    let engine = BrokerEngine::load(&config)?;
    BrokerServer::bind(config, service_lease, owner, engine)?.run()
}
```

The service lock is acquired before owner-watchdog startup and engine construction. The lock holder removes any stale Unix socket immediately before bind. `BrokerQueue` uses `Mutex<State>` plus `Condvar`, rejects at 64, and, while batch work waits, schedules at most eight interactive dequeues before one batch dequeue. The dedicated watchdog is armed before model load; owner EOF terminates even a blocked cold load, while an active request older than 60 seconds calls `std::process::abort()`. Sidecar `AGENTS.md` and `README.md` document the additive `broker` verb without adding environment knobs.

**Step 4: Run broker and existing conformance gates**

Run focused broker tests, all four sidecar fast gates, and `cargo test --release --test conformance -- --ignored --test-threads=1 --nocapture` on the reference machine.

**Step 5: Apply commit mode**

`serial-worker-commit`: commit after lead review and record the SHA.

**Acceptance criteria:**
- [x] Eight concurrent starts produce one model-loaded broker; losing processes exit before engine load.
- [x] Closing owner stdin ends the broker and releases endpoint/service lock.
- [x] A killed owner leaves no child or cleanup requirement.
- [x] A stale Unix endpoint is removed only by the next service-lock holder.
- [x] `shutdown` closes one broker connection without releasing the service or accelerator lease.
- [x] Owner EOF during model load terminates before endpoint bind and releases both locks.
- [x] While batch work waits, one batch is dequeued after at most eight interactive dequeues.
- [x] Queue-full and expired requests receive `internal_error`; connections remain usable.
- [x] Stdio conformance is unchanged.

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

### Task 6: Enforce one accelerator and recover runtime OOM to CPU

**Files:**
- Modify: sidecar `src/engine_trait.rs`
- Modify: sidecar `src/engine.rs`
- Modify: sidecar `src/health.rs`
- Modify: sidecar `src/broker/lease.rs`
- Modify: sidecar `src/broker/engine.rs`
- Test: sidecar `tests/engine_tests.rs`
- Test: sidecar `tests/broker_accelerator_tests.rs`

**Interfaces:**
- Consumes: Task 4's user-global accelerator-lock primitive and current backend selection/load fallback.
- Produces: `EngineFailureClass::ResourceExhausted`, explicit `BackendPolicy`, accelerator lease health, permanent broker-lifetime CPU demotion, and one idempotent retry.

**Contract inputs:** Only a typed resource-exhaustion error triggers retry; ordinary item/application errors remain v1 `internal_error` without engine reload.

**File ownership:** Sidecar broker engine/lease, engine classification, recovery tests.

**Serialization required:** Yes.

**Dependency reason:** Requires the working broker lifecycle.

**Step 1: Write failing recovery tests**

```rust
#[test]
fn accelerated_resource_exhaustion_reloads_cpu_retries_once_and_releases_lease() {
    let engine = faulting_accelerated_engine_once();
    let result = broker_engine(engine).embed_query("query").unwrap();
    assert_eq!(result.backend, "cpu");
    assert_eq!(result.attempts, 2);
    assert!(!result.accelerator_lease_held);
}
```

Also prove two model identities cannot both construct an accelerated engine.
Prove ordinary `Decode`, `Encode`, item, and application failures do not demote or retry.

**Step 2: Verify red**

Run: `cargo test broker_accelerator`

**Step 3: Add typed classification and explicit load policy**

```rust
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EngineFailureClass { Application, ResourceExhausted }

pub enum BackendPolicy { Auto, CpuOnly }
```

`EngineError` gains a typed failure-class field while preserving its existing kind/message wire rendering. `ContextAlloc` construction uses `ResourceExhausted`; string rendering remains exactly `"ContextAlloc: <message>"`. Do not classify all `Decode`/`Encode` failures by prefix or message—only separately proven allocation variants may be added later with their own tests. `BrokerEngine` holds the mutable engine only on its scheduler thread, drops the accelerated engine before releasing the accelerator lock, loads `CpuOnly`, updates additive health metadata, and retries once.

**Step 4: Run engine, broker, conformance, and hardware smoke**

Run all four fast gates plus the model-backed engine tests and existing hardware smoke on supported hardware.

**Step 5: Apply commit mode**

`serial-worker-commit`: commit after recovery and no-double-accelerator evidence.

**Acceptance criteria:**
- [ ] A second model broker starts CPU without probing/allocating GPU.
- [ ] Accelerated resource exhaustion retries once on CPU and all later calls remain CPU.
- [ ] CPU resource exhaustion returns an application failure without retry loop.
- [ ] Non-allocation `Decode`, `Encode`, item, and application failures never trigger demotion.
- [ ] Health truthfully reports resolved CPU backend and degradation reason.

### Task 7: Connect, spawn, and supervise the broker from Miller

**Files:**
- Create: Miller `src/Miller.Indexing/Semantic/SemanticBrokerEndpoint.cs`
- Create: Miller `src/Miller.Indexing/Semantic/SharedSemanticBrokerConnectionFactory.cs`
- Create: Miller `src/Miller.Indexing/Semantic/WindowsBrokerJob.cs`
- Modify: Miller `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs`
- Modify: Miller `src/Miller.Indexing/Semantic/SemanticSearchArm.cs:232-246`
- Modify: Miller `src/Miller.Indexing/Semantic/SemanticEmbeddingSessionBroker.cs`
- Modify: Miller `src/Miller.Server/Hosting/MillerServiceRegistration.cs:73-96`
- Modify: Miller `src/Miller.Server/Cli/CliDispatch.cs:3516-3532`
- Test: Miller `tests/Miller.Tests/Indexing/SemanticBrokerEndpointTests.cs`
- Test: Miller `tests/Miller.Tests/Indexing/SharedSemanticBrokerConnectionFactoryTests.cs`
- Test: Miller `tests/Miller.Tests/Indexing/SemanticOffGuaranteeTests.cs`
- Test: Miller `tests/Miller.Tests/Server/HostStartupRegistrationTests.cs`
- Test: Miller `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 3-6 broker argv, endpoint identity, IPC, and connection factory.
- Produces: connect-first/spawn-on-demand production factory, retained owner process/stdin, Windows Job Object, reconnect, and broker snapshot.

**Contract inputs:** `Miller.Indexing` receives pure `millerHome` and `toolsRoot` strings and never references Server-layer `WorkspaceContext`. Server/CLI callers derive `millerHome` from the parent of `WorkspaceContext.RegistryDbPath`; executable remains `SemanticSidecarLayout.ExecutablePath(ToolsRoot)`.

**File ownership:** Miller endpoint, shared connection factory, Windows Job Object, DI wiring, tests.

**Serialization required:** Yes.

**Dependency reason:** Requires Tasks 3-6 interfaces.

**Step 1: Write failing endpoint and launch-race tests**

```csharp
[Fact]
public async Task EightFactories_ConvergeOnOneBrokerAndAllHandshake()
{
    await using var group = await BrokerFactoryGroup.StartAsync(count: 8);
    Assert.All(await group.HandshakesAsync(), h => Assert.Equal(Pin.ModelSha256, h.ModelSha256));
    Assert.Equal(1, group.ModelLoadedBrokerCount);
}
```

Windows tests close the owner's Job handle and assert broker exit; Unix tests close owner stdin.
Add a cold-load race in which one factory wins spawn, seven lose the service lock, and every loser continues endpoint polling until the winning broker handshakes. Add owner/non-owner disposal tests and an Off host-registration test that proves no broker factory/path/directory work occurs.

**Step 2: Verify red**

Run focused endpoint/factory tests.

**Step 3: Implement the production factory**

```csharp
public sealed class SharedSemanticBrokerConnectionFactory :
    ISemanticSidecarConnectionFactory
{
    public ValueTask<ISemanticSidecarConnection> ConnectAsync(
        CancellationToken cancellationToken);

    public SemanticBrokerSnapshot Snapshot { get; }
}
```

Connection order is: 250 ms direct connect; process-local spawn gate; retry direct connect; spawn `broker` with exact Task 1 argv and redirected stdin; on Windows attempt to attach the child to a kill-on-close Job Object before broker use; poll/handshake through the existing 120-second init budget. Losing children exit on the sidecar service lock, and their factories keep polling the deterministic endpoint instead of failing after the 250 ms probe. A Job attach failure is recorded in `SemanticBrokerSnapshot` as degraded ownership; stdin EOF remains authoritative.

The server registers one lazy `SharedSemanticBrokerConnectionFactory` singleton and gives it pure `toolsRoot`/`millerHome` inputs only on first semantic use. The one-shot CLI creates one invocation-wide factory inside its single `CliSemanticSession`. Do not use a static global. Production MCP/CLI paths do not call `ProcessSemanticSidecarLauncher.ForServe`; stdio remains only for explicit evaluation, conformance, package-smoke, and test paths.

The factory that successfully spawns owns `Process`, stdin, and Job handle until factory disposal. Owner disposal closes stdin and then the Job handle; a factory that only connected is a non-owner and closes only its client connections. Ownership belongs to the factory, not to an individual connection. A transport failure closes only that client connection and re-enters connect-first logic.

Use `NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)` with `ConnectAsync(TimeSpan, CancellationToken)` on Windows and `Socket(AddressFamily.Unix, ...)` with cancellation on Unix.

**Step 4: Run focused, fast, and Scale semantic gates**

Run focused tests, `scripts/test.sh`, then the semantic Scale class with a from-source restored rc.5 candidate.

**Step 5: Apply commit mode**

`serial-worker-commit`: commit after both Unix and Windows factory evidence.

**Acceptance criteria:**
- [ ] Production DI has one process-local client broker but no process-local model child.
- [ ] Server DI owns one lazy factory singleton; the CLI owns one invocation-wide factory; no static global or production MCP/CLI `ForServe` path remains.
- [ ] Eight independent factories share one same-model broker.
- [ ] Spawn losers wait through cold initialization and handshake instead of prematurely falling back.
- [ ] Owner disposal terminates its broker; non-owner disposal never does.
- [ ] Windows Job attachment occurs before broker use, and attach failure is visible as degraded ownership with stdin EOF still authoritative.
- [ ] Owner death releases the broker; surviving clients reconnect/re-elect within 30 seconds.
- [ ] Missing, incompatible, or failed broker returns lexical outcomes, never MCP failures.
- [ ] Semantic Off never constructs the factory, derives an endpoint, creates `semantic/`, resolves workspace/tools/home, connects, or launches.

### Task 8: Prove multi-session, crash, version, and GPU behavior

**Files:**
- Create: Miller `scripts/Miller.SemanticBrokerProbe/Miller.SemanticBrokerProbe.csproj`
- Create: Miller `scripts/Miller.SemanticBrokerProbe/Program.cs`
- Create: Miller `scripts/semantic-broker-soak.sh`
- Create: Miller `scripts/semantic-broker-soak.ps1`
- Create: Miller `tests/Miller.Tests/Indexing/SemanticBrokerScaleTests.cs`
- Create: Miller `docs/findings/2026-07-27-shared-semantic-broker-verification.md`
- Test: sidecar `tests/broker_multi_process_tests.rs`

**Interfaces:**
- Consumes: production Miller connector and candidate sidecar package.
- Produces: repeatable process/GPU evidence and a release ledger.

**Contract inputs:** Run on macOS, Linux, Windows CI, and the user's Windows laptop with 6GB NVIDIA GPU.

**File ownership:** Cross-repo integration tests, probe/soak scripts, verification ledger.

**Serialization required:** Yes.

**Dependency reason:** Requires both repos integrated.

**Step 1: Write the failing probe assertions**

```powershell
Assert-Equal 1 (Get-ModelLoadedBrokerCount)
Assert-LessOrEqual ($manySessionGpuDeltaMiB) ($oneSessionGpuDeltaMiB + 256)
Assert-LessOrEqual $ownerRecovery.TotalSeconds 30
Assert-Equal 0 $hungRequests
```

**Step 2: Verify the soak fails against rc.4/current Miller**

Expected: multiple process-local sidecars or no broker endpoint.

**Step 3: Implement the probe and scenarios**

The probe opens production connections, handshakes, embeds query/batch traffic, holds for a requested duration, and emits JSON without input text. The scripts run:

1. One warm client baseline.
2. Eight simultaneous same-model clients.
3. Concurrent query and convergence load.
4. Kill a non-owner client mid-request.
5. Kill the owner Miller process mid-request.
6. Kill the broker process mid-request.
7. Start old/new model identities concurrently.
8. Exercise `ResourceExhausted` only through fake-engine unit/integration tests; do not add production test hooks or environment variables.
9. Windows sleep/resume and rapid reconnect loop.
10. Let a short-lived CLI own the broker, connect a long-lived MCP client, then exit the CLI during an MCP request and prove bounded reconnect/re-election.
11. Thirty-minute soak; extend to overnight before release if any retry/recovery row fails once.

**Step 4: Run and record all hard gates**

Use `nvidia-smi` global memory before/after plus broker PID/process tree. WDDM per-process `N/A` is not accepted as sole proof; global delta and process count remain required.

**Step 5: Apply commit mode**

`serial-worker-commit`: commit scripts/tests/ledger after all non-release hardware rows pass.

**Acceptance criteria:**
- [ ] Same-model N-session steady state has exactly one live model-loaded sidecar.
- [ ] Multi-model steady state has at most one accelerated broker.
- [ ] GPU memory is effectively constant with N same-model sessions.
- [ ] Every crash unblocks the active request and later requests recover or remain lexical.
- [ ] No orphan broker remains after owner termination.
- [ ] Short-lived CLI ownership is diagnosed and recovers without hanging a surviving MCP client.
- [ ] Windows named-pipe operations honor deadlines under sleep/resume and process death.

### Task 9: Publish sidecar rc.5, pin it, switch semantic default-on, and expose health

**Files:**
- Modify: sidecar `Cargo.toml`
- Modify: sidecar `Cargo.lock`
- Create: sidecar `docs/release-notes/v0.1.0-rc.5.md`
- Modify: Miller `scripts/semantic-pins.json`
- Modify: Miller `src/Miller.Indexing/SemanticActivation.cs:23-47`
- Modify: Miller `docs/adr/ADR-0003-semantic-retrieval-ownership.md`
- Modify: Miller `docs/contracts/vectors-v1.md`
- Modify: Miller `src/Miller.Server/Tools/WorkspaceRender.cs`
- Modify: Miller `src/Miller.Server/Tools/WorkspaceTool.cs`
- Modify: Miller `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`
- Modify: Miller `src/Miller.Server/Cli/CliDispatch.cs`
- Modify: Miller `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`
- Modify: Miller `tests/Miller.Tests/Server/WorkspaceFactsAssemblerTests.cs`
- Modify: Miller `tests/Miller.Tests/Server/WorkspaceToolTests.cs`
- Modify: Miller `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Modify: Miller `README.md`
- Modify: Miller `CLAUDE.md`, then regenerate `AGENTS.md`
- Modify: Miller `docs/README.md`
- Modify: Miller `docs/release-notes/v1.14.0.md`
- Test: Miller `tests/Miller.Tests/Indexing/SemanticActivationTests.cs`
- Test: Miller `tests/Miller.Tests/Indexing/SemanticOffGuaranteeTests.cs`
- Test: Miller `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`
- Test: Miller `tests/Miller.Tests/Server/WorkspaceFactsAssemblerTests.cs`
- Test: Miller `tests/Miller.Tests/Server/WorkspaceToolTests.cs`
- Test: Miller `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Interfaces:**
- Consumes: published, downloaded, checksum-verified `julie-semantic-sidecar v0.1.0-rc.5` assets and Task 7 snapshot.
- Produces: live rc.5 pins, default-on activation, additive broker health, and exact public docs.

**Contract inputs:** This task has a hard approval boundary before sidecar push/tag/release. Asset names/digests come from live release facts; never guess or prefill hashes. Default-on wording must replace current opt-in/default-Off claims in the active ADR, vector contract, README, agent guidance, CLI help/status copy, and tests; historical findings/plans remain historical unless they claim current behavior.

**File ownership:** Sidecar version/release files; Miller pins, activation, status/health, docs/release notes.

**Serialization required:** Yes.

**Dependency reason:** Requires Task 8 hard gates and explicit publish approval.

**Step 1: Prepare and verify rc.5 without publishing**

Run all sidecar branch gates and package all four platform archives. Download/inspect local artifacts and run broker smoke from each.

**Step 2: Stop for explicit sidecar publish approval**

Report exact sidecar repo path, branch, commit, all worktrees' dirty states, candidate tag `v0.1.0-rc.5`, gates, and archive names/digests. Approval applies only to that state.

**Step 3: Publish and pin live facts**

After approval, push/tag/release rc.5, download all assets, verify checksums and broker smoke, then replace Miller's version/names/SHA-256 values in `scripts/semantic-pins.json`.

**Step 4: Write default-on and health tests, then implement**

```csharp
[Theory]
[InlineData(null, SemanticMode.On)]
[InlineData("", SemanticMode.On)]
[InlineData("off", SemanticMode.Off)]
[InlineData("0", SemanticMode.Off)]
[InlineData("false", SemanticMode.Off)]
[InlineData("shadow", SemanticMode.Shadow)]
[InlineData("on", SemanticMode.On)]
[InlineData("1", SemanticMode.On)]
[InlineData("true", SemanticMode.On)]
[InlineData("bogus", SemanticMode.Off)]
public void ActivationPolicy(string? raw, SemanticMode expected) =>
    Assert.Equal(expected, SemanticActivation.FromEnvValue(raw));
```

Status/health adds broker state, endpoint identity, owner/non-owner role, server version/model, backend, accelerator lease, reconnect count, and degraded reason. It never emits full pipe/socket paths or PIDs in compact MCP output; exhaustive JSON may include PID for local diagnosis.

Update `SemanticOffGuaranteeTests` so unset/blank are no longer Off cases, while explicit `off|0|false` prove that host registration never constructs the broker factory, derives an endpoint, creates `<miller-home>/semantic`, resolves semantic workspace/tools/home inputs, connects, launches, reads vectors, or writes semantic telemetry.

**Step 5: Run Miller restore/build/fast/Scale/docs gates**

Restore from published rc.5, build Release, run `scripts/test.sh all`, package smoke, sync `CLAUDE.md` → `AGENTS.md`, and verify no global config is required for semantic activation.

**Acceptance criteria:**
- [x] Miller pins only live, downloaded, checksum-verified rc.5 assets.
- [x] Default-on policy exactly matches Global Constraints.
- [x] Explicit `off|0|false` performs zero semantic work, and unset/blank no longer appears in an Off test or current-behavior contract.
- [x] Existing status/health reports enough broker facts to diagnose sharing and CPU degradation.
- [x] Public docs state that sessions share a broker and only one broker may own acceleration.

### Task 10: Run final release gates and close evidence

**Files:**
- Modify: Miller `docs/findings/2026-07-27-shared-semantic-broker-verification.md`
- Modify: Miller `docs/release-notes/v1.14.0.md`
- Create: Miller `docs/findings/2026-07-27-v1.14.0-release-verification.md`

**Interfaces:**
- Consumes: completed Tasks 1-9 and live rc.5 release facts.
- Produces: one reviewable v1.14.0 candidate with complete cross-platform evidence and a fresh release approval packet.

**Contract inputs:** Miller release process, clean-worktree rule, all related worktrees, no push/release without fresh approval.

**File ownership:** Verification ledger and existing v1.14.0 release evidence/notes.

**Serialization required:** Yes.

**Dependency reason:** Final acceptance and fresh release approval boundary.

**Step 1: Run impact and complete review**

Run Miller `impact` on the full git diff, inspect every impacted semantic/status/public contract, and run the likely tests it reports. Review the complete diff for raw text logging, unbounded waits, hidden per-process fallback, and Windows no-op behavior.

**Step 2: Run branch and expensive gates**

Run:

```text
scripts/test.sh all
dotnet build Miller.slnx -c Release
all package/release smoke for four RIDs
sidecar protocol conformance on all four platforms
semantic broker soak on macOS/Linux/Windows
Windows 6GB NVIDIA hardware gate
visible policy-v2 retrieval replay
```

**Step 3: Verify default-on dogfood**

Remove `MILLER_SEMANTIC=on` from only the isolated test harness environment, start multiple Miller sessions, and prove semantic treatment plus one broker occurs from the unset default. Do not alter the user's global configs as part of the gate.

**Step 4: Reconcile every worktree and prepare approval packet**

Report current path, branch, commit, `git status --short --branch`, and `git worktree list` plus status for every Miller and sidecar worktree that could hold related changes.

**Step 5: Stop for fresh Miller push/release approval**

The packet includes candidate commit, tag `v1.14.0`, exact live rc.5 pin, all hard-gate results, report-only retrieval metrics, and remaining risks. After approval, follow `docs/release-process.md` through push, tag, GitHub release notes, asset verification, and plugin-marketplace verification in the same session.

**Acceptance criteria:**
- [x] No hidden per-process production sidecar path remains.
- [ ] All hard gates and complete review pass.
- [x] Retrieval value is preserved and default-on dogfood needs no enabling env var.
- [ ] Both repos and every related worktree are reconciled before approval.
- [ ] Release occurs only after fresh approval of the verified final state.

## Rejected Shortcuts

- Default-on with the current per-process sidecar: correct fallback, unbounded aggregate VRAM.
- Idle-unload alone: lowers average memory but does not prevent simultaneous GPU allocations.
- A detached always-on daemon: recreates stale ownership, upgrade, and orphan lifecycle problems.
- Porting Julie's current embedding-host verbatim: its Windows named-pipe timeout is explicitly a no-op and the path is not exercised on its CI/dev host.
- HTTP loopback plus token/discovery files: adds failure surfaces without helping local compute sharing.
- PID/state files for liveness: OS locks, connection state, owner stdin, and Job Objects are authoritative.
- Newer-client-wins restarts: recreates Julie's version flap; deterministic broker-contract/protocol/model identities coexist instead.
- Silent per-process stdio fallback when broker connection fails: reintroduces the VRAM problem. Production falls back to lexical.
- Message-string OOM detection: resource exhaustion is typed at the engine boundary.
- Treating Windows CI compilation as Windows lifecycle proof: cancellation, owner death, and process/GPU soak run on Windows.

## Definition of Done

- Semantic retrieval is On when `MILLER_SEMANTIC` is unset and Off remains a zero-work guarantee.
- Concurrent same-model Miller sessions share one model-loaded broker and one GPU allocation.
- Different broker-contract/protocol/model identities cannot simultaneously own acceleration.
- Owner, client, or broker death never leaves a hanging MCP request or orphan model process.
- Accelerated resource exhaustion demotes to CPU and retries once; subsequent calls remain healthy on CPU.
- Windows named-pipe connect/read/write cancellation is tested and bounded.
- The frozen sidecar stdio protocol and Julie compatibility remain intact.
- Miller's lexical-only output remains byte-identical whenever semantics abstain or fail.
- Live status/health makes broker sharing, ownership, backend, and degradation diagnosable.
- Sidecar rc.5 and Miller v1.14.0 release assets are published only with explicit approval and verified from downloaded artifacts.
