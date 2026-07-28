### Task 4: Build the lease-owned Unix broker and bounded scheduler

**Files:**
- Modify: sidecar `src/lib.rs`
- Modify: sidecar `src/main.rs`
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
- [ ] Eight concurrent starts produce one model-loaded broker; losing processes exit before engine load.
- [ ] Closing owner stdin ends the broker and releases endpoint/service lock.
- [ ] A killed owner leaves no child or cleanup requirement.
- [ ] A stale Unix endpoint is removed only by the next service-lock holder.
- [ ] `shutdown` closes one broker connection without releasing the service or accelerator lease.
- [ ] Owner EOF during model load terminates before endpoint bind and releases both locks.
- [ ] While batch work waits, one batch is dequeued after at most eight interactive dequeues.
- [ ] Queue-full and expired requests receive `internal_error`; connections remain usable.
- [ ] Stdio conformance is unchanged.

