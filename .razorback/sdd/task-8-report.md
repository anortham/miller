# Task 8 report

## Scope

- Miller worktree: `/Users/murphy/source/miller/.worktrees/shared-semantic-broker-plan`,
  branch `codex/shared-semantic-broker-plan`, baseline `687011f5`.
- Sidecar worktree: `/Users/murphy/source/julie-semantic-sidecar/.worktrees/shared-semantic-broker`,
  branch `codex/shared-semantic-broker`, baseline `741850a`.
- Cross-workspace Miller selectors used:
  `shared-semantic-broker-plan-267b76f3a5df` and `shared-semantic-broker-c9b6e2577a10`.
- No commit, push, tag, publish, or release was performed.

## RED

1. `cargo test --test broker_multi_process_tests` exited 101 with `E0425` for the three missing scenario
   drivers. The test assertions were present first: eight clients load one model, old/new endpoints differ with
   at most one accelerator, recovery is within 30 seconds, and hung/failed counts are zero.
2. The first production-connector soak failed before spawn because the macOS temp path made the Unix socket
   longer than 104 characters. The probe was changed to surface `SemanticEmbeddingSession.UnavailableReason`;
   the runner now chooses a short `/tmp` home.
3. The restored Miller runtime is absent and the repository pin is rc.4. Focused Scale tests skip with an
   actionable rc.5/from-source instruction. This is recorded as unavailable, never as passing evidence.
4. Grok's false-pass review was reproduced with eight focused validator tests and ten sidecar `E0609` RED
   assertions. The old harness could infer recovery from process lifetime, swallow process failures, truncate a
   30-minute soak at 120 seconds, and accept meaningless GPU deltas. The final re-review added four validator
   cases and a `CS0117` RED compile failure for the missing recorded-NVIDIA evidence contract.

## GREEN

- Sidecar focused multi-process test: 4 passed, 0 failed.
- Probe Release build: 0 warnings, 0 errors.
- Corrected source-candidate macOS short soak:
  - candidate SHA-256 `6d2fa03c08d051d9be28bd32570d06e233492987310352a26ee52ac9f10d21b9`;
  - warm broker 1, eight-client broker 1;
  - old/new endpoints distinct, accelerated brokers 0 on CPU;
  - post-kill broker recovery 0.789 seconds, post-kill owner recovery 0.880 seconds;
  - 17/17 normal probes completed with exit code 0, both expected kills observed;
  - configured 5-second traffic ran 5.070 seconds;
  - 0 hung, 0 failed, 0 failed events, 0 final broker processes.
- Shared false-pass validator tests: 12 passed, 0 failed.
- Sidecar process proof now measures seven losing broker candidates, four concurrent query and four concurrent
  batch clients, expected model identity per endpoint, in-flight embed unblock, client-simulated replacement,
  and zero live brokers after cleanup. It uses a fake engine through production `serve_with_loader`; it is
  deterministic lifecycle evidence, not real model-load or VRAM evidence.
- Recorded-soak Scale assertion passed. NVIDIA assertion skipped because this Mac has no NVIDIA device.
- Full sidecar gates passed: active Rust tests, clippy with warnings denied, formatting, and 30 Python tests.
- Miller fast suite passed 5,246 tests with 2 platform/runtime skips in 28 seconds; Release build completed with
  0 warnings and 0 errors.
- Focused Task 8 validator/Scale run with the from-source candidate and recorded summary passed 14 tests and
  actionably skipped the repo rc.4/absent-runtime and NVIDIA-only rows.

## Produced surfaces

Miller:

- `scripts/Miller.SemanticBrokerProbe/`
- `scripts/semantic-broker-soak.sh`
- `scripts/semantic-broker-soak.ps1`
- `tests/Miller.Tests/Indexing/SemanticBrokerScaleTests.cs`
- `docs/findings/2026-07-27-shared-semantic-broker-verification.md`

Sidecar:

- `tests/broker_multi_process_tests.rs`

The probe uses the production shared connector, performs the real health handshake plus concurrent query/batch
traffic, emits candidate/snapshot/recovery JSONL, and never emits input text. Startup/request/grace budgets are
separate from the configured traffic duration. Both runners invoke the same validator, exit nonzero for any
non-null false acceptance row, reject GPU acceptance that disagrees with `gpu.pass`, and keep unavailable
hardware rows `null`. The recorded NVIDIA test requires accelerated warm evidence, one warm broker, at least
64 MiB of warm GPU growth, `gpu.pass=true`, and many-session growth no more than warm plus 256 MiB.
`ResourceExhausted` remains in the existing fake-engine tests; no production hook or environment variable was
added.

## Acceptance

| Requirement | Result |
|---|---|
| One model-loaded broker for eight same-model sessions | Passed on macOS and deterministic process test |
| Separate old/new endpoints, at most one accelerator lease | Passed |
| Owner/broker/client death unblocks, recovers within 30 seconds | Passed on macOS |
| Zero hung requests and no orphan broker | Passed on macOS |
| Global GPU delta no more than warm plus 256 MiB | Pending 6GB NVIDIA Windows laptop |
| Windows sleep/resume and rapid reconnect | PowerShell scenario implemented; execution pending Windows release gate |
| Linux release candidate | Pending Linux release gate |
| Default 30-minute/optional overnight soak | Runner implemented; duration gate pending |
| PowerShell execution | Pending because `pwsh` is unavailable on this Mac |

The exact live proof and pending hardware/platform rows are maintained in
`docs/findings/2026-07-27-shared-semantic-broker-verification.md`.
