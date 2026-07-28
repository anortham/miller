# Task 6: Enforce one accelerator and recover runtime OOM to CPU

## Worktree

- Repository: `/Users/murphy/source/julie-semantic-sidecar/.worktrees/shared-semantic-broker`
- Branch: `codex/shared-semantic-broker`
- Required baseline: `d468402`
- Leave the implementation uncommitted for lead and Grok review.
- Write the final report to `/Users/murphy/source/miller/.worktrees/shared-semantic-broker-plan/.razorback/sdd/task-6-report.md`.

## Owned files

- `src/engine_trait.rs`
- `src/engine.rs`
- `src/health.rs`
- `src/broker/lease.rs`
- `src/broker/engine.rs`
- `tests/engine_tests.rs`
- new `tests/broker_accelerator_tests.rs`
- report path above

Do not modify Task 5 transport files or unrelated work.

## Contract

- Consume the broker's user-global accelerator lease and the existing backend selection/load fallback.
- Produce typed `EngineFailureClass::ResourceExhausted`, explicit `BackendPolicy::{Auto,CpuOnly}`, truthful accelerator-lease health, permanent broker-lifetime CPU demotion, and exactly one retry.
- Only a typed resource-exhaustion error triggers retry. Never infer OOM from a message or broad `Decode`/`Encode` kind.
- Preserve existing wire rendering, including `ContextAlloc: <message>`.
- On accelerated resource exhaustion, drop the accelerated engine before releasing the accelerator lease, load `CpuOnly`, update health, retry once, and remain CPU for all later calls.
- CPU resource exhaustion returns the normal application failure without a retry loop.
- Ordinary decode, encode, item, and application failures never demote or retry.
- A second model broker that cannot acquire the accelerator lease starts CPU without probing or allocating GPU.

## TDD

Write failing tests first for:

- accelerated resource exhaustion reloads CPU, retries once, releases the lease, and permanently remains CPU;
- two model identities cannot both construct accelerated engines;
- CPU resource exhaustion does not loop;
- ordinary decode, encode, item, and application failures do not demote or retry;
- health reports the resolved CPU backend and degradation reason.

Record exact RED evidence. If a model-backed or hardware test cannot run locally, state the boundary; do not invent evidence.

## Verification

Run:

- focused `broker_accelerator` and relevant engine tests;
- `cargo fmt --all -- --check`;
- `cargo clippy --all-targets -- -D warnings`;
- `cargo test`;
- `python3 -B -m unittest discover -s scripts/tests -p 'test_*.py'`;
- relevant existing model-backed engine/hardware smoke only when prepared/supporting hardware is available.

Use Miller impact before finalizing. Do not commit or push. Return the path, branch, HEAD, dirty state, RED/GREEN evidence, design decisions, and remaining hardware evidence.
