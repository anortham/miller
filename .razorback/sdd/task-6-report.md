# Task 6 — Enforce one accelerator and recover runtime OOM to CPU

## Worktree state

- Repository: `/Users/murphy/source/julie-semantic-sidecar/.worktrees/shared-semantic-broker`
- Branch: `codex/shared-semantic-broker`
- Baseline HEAD: `d4684028306c652faf3a13593f48e0e64ce26d36`
- Task 6 commit: `741850a` (`feat: recover semantic broker OOM on CPU`)
- Dirty state after commit: clean.
- Task 5 transport files and unrelated files were not modified.

## RED evidence

1. Initial contract RED:
   - Command: `cargo test broker_accelerator`
   - Exit: `101`
   - Expected compile failures proved the interfaces did not exist:
     `BackendPolicy`, `EngineFailureClass`, `EmbedEngine::is_accelerated`,
     `EngineError::resource_exhausted`, `EncodeFailure::resource_exhausted`,
     `EngineError.failure_class`, and `BrokerEngine::load_with`.
2. Lead-review regression RED:
   - Command:
     `cargo test broker_accelerator_static_engine_without_recovery_loader_returns_the_original_failure`
   - Exit: `101`
   - Expected runtime failure: panic at `src/broker/engine.rs` with
     `recoverable broker engine has a loader`.
   - The production guard was then added so `BrokerEngine::new` safely returns the original typed
     failure when it has no recovery loader.

## Implementation

- Added typed `EngineFailureClass::{Application, ResourceExhausted}` while keeping
  `EngineError` display and protocol rendering unchanged.
- Added explicit `BackendPolicy::{Auto, CpuOnly}` and `LlamaEngine::load_with_policy`.
  `CpuOnly` enters the existing forced-CPU path before accelerator discovery or benchmarking.
- Marked only `ContextAlloc` construction as typed resource exhaustion. Decode, encode, item,
  application, and message text do not classify OOM.
- Added resolved-backend truth through `EmbedEngine::is_accelerated`.
- Added a broker-owned typed policy loader and single-scheduler-thread mutable state.
  No accelerator lease selects `CpuOnly` on the initial load.
- An accelerated typed resource exhaustion:
  1. drops the accelerated engine;
  2. releases the accelerator lease;
  3. loads `CpuOnly`;
  4. records permanent broker-lifetime degradation;
  5. retries exactly once.
- CPU resource exhaustion and the CPU retry result are returned normally without another reload.
  Later calls remain on CPU.
- Auto loads that resolve CPU, plus unready loads, release the accelerator lease immediately.
- Health adds truthful `accelerator_lease_held`; after recovery it reports resolved CPU,
  `accelerated: false`, and
  `accelerator resource exhausted; permanently demoted to CPU` at both top level and
  `load_policy.degraded_reason`.

## Lead findings fixed

- Accelerated Decode, Encode, Item, and Application failures now run under a real held lease in
  tests. They retain `[BackendPolicy::Auto]`, keep the lease, never load CPU, and use
  `"out of memory"` as the message to prove classification is typed rather than textual.
- `BrokerEngine::new` cannot panic on typed exhaustion: recovery requires both a held lease and a
  recovery loader; otherwise the original failure and lease are preserved.
- Resolved CPU and unready engines explicitly prove that an acquired lease is released.

## GREEN evidence

- `cargo test broker_accelerator`: 7 passed, 0 failed.
- `cargo test context_allocation_resource_exhaustion_is_typed_without_changing_wire_rendering`:
  1 passed, 0 failed.
- `cargo fmt --all -- --check`: passed.
- `cargo clippy --all-targets -- -D warnings`: passed.
- `cargo test`: passed across all fast suites; 244 passed, 0 failed, 25 ignored.
- `python3 -B -m unittest discover -s scripts/tests -p 'test_*.py'`: 30 passed.
- `git diff --check`: passed.
- Miller post-edit impact completed at revision 28. It identified the expected broker,
  protocol, engine, backend-selection, health, and test consumers; focused accelerator,
  engine, manifest, protocol, broker lifecycle/scheduler, and full fast gates cover them.

## Hardware and model-backed boundary

- No real OOM was induced and no hardware archive smoke is claimed. The hardware smoke requires
  an exact checksum-bound packaged archive, which this uncommitted worktree does not provide.
- The first prepared-model ignored engine run used the test harness default parallelism and was
  invalid because llama's process-global backend rejected concurrent initialization with
  `BackendAlreadyInitialized`; that run is not counted as evidence.
- `cargo test --test engine_tests -- --ignored --test-threads=1`: passed on Apple M2 Ultra;
  11 passed, 0 failed, 9 filtered out in 369.50 seconds. This directly covered the CPU-only
  model load path introduced by Task 6.
- Windows/NVIDIA low-VRAM runtime recovery remains release-gate evidence, not local Task 6 evidence.

## Result

- Grok's final focused review returned GO with no Critical or Important findings after the recovery
  gate, engine-before-lease drop order, one-retry limit, permanent CPU mode, terminal reload
  failure, health overlay, and public-constructor safety were checked.
- Task 6 implementation and local verification are complete and committed locally; nothing was
  pushed.
