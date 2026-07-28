# Task 8 — Prove multi-session, crash, version, and GPU behavior

## Worktrees

- Miller: `/Users/murphy/source/miller/.worktrees/shared-semantic-broker-plan`
  - branch `codex/shared-semantic-broker-plan`
  - baseline `687011f5`
- Sidecar: `/Users/murphy/source/julie-semantic-sidecar/.worktrees/shared-semantic-broker`
  - branch `codex/shared-semantic-broker`
  - baseline `741850a`
- Keep all edits in these worktrees. Do not commit, push, tag, publish, or release.

## Goal

Create repeatable, process-level proof that same-model Miller sessions share one model-loaded broker, different model identities remain isolated while at most one broker owns acceleration, owner/broker/client crashes unblock and recover, GPU residency is effectively constant as session count grows, and Windows pipe operations remain bounded through reconnect and sleep/resume.

## Required files

Miller:

- Create `scripts/Miller.SemanticBrokerProbe/Miller.SemanticBrokerProbe.csproj`
- Create `scripts/Miller.SemanticBrokerProbe/Program.cs`
- Create `scripts/semantic-broker-soak.sh`
- Create `scripts/semantic-broker-soak.ps1`
- Create `tests/Miller.Tests/Indexing/SemanticBrokerScaleTests.cs`
- Create `docs/findings/2026-07-27-shared-semantic-broker-verification.md`

Sidecar:

- Create `tests/broker_multi_process_tests.rs`

Add only the minimal project/solution/test registration needed to build and run these surfaces.

## Frozen scenarios

The probe must exercise the production Miller connector, handshake, query and batch embedding traffic, optional hold duration, and JSON output without input text. The soak runners must cover:

1. One warm client baseline.
2. Eight simultaneous same-model clients.
3. Concurrent query and convergence-style batch load.
4. Kill a non-owner client mid-request.
5. Kill the owner Miller process mid-request.
6. Kill the broker process mid-request.
7. Start old/new model identities concurrently.
8. Keep `ResourceExhausted` fault injection in fake-engine tests only; add no production test hook or environment variable.
9. Windows sleep/resume plus rapid reconnect loop.
10. Let a short-lived CLI/probe own the broker, connect a long-lived client, then exit the owner during the surviving request and prove bounded reconnect/re-election.
11. A configurable 30-minute soak, extendable to overnight.

The scripts must record:

- exact candidate/checksum identity;
- client and broker process tree;
- model-loaded broker count;
- owner recovery time;
- hung/failed request counts;
- broker/model/backend/accelerator/degraded snapshot facts;
- `nvidia-smi` global memory before/after where available.

WDDM per-process `N/A` is not sufficient GPU proof. Use global delta plus broker count. Never persist request text.

## TDD and verification

1. Use Miller search/context/inspect and sidecar search before reading; run impact before edits.
2. Add failing assertions first:
   - exactly one model-loaded broker for eight same-model clients;
   - many-session GPU delta no more than one-session delta + 256 MiB;
   - owner/broker recovery within 30 seconds;
   - zero hung requests;
   - old/new identities use separate endpoints but at most one accelerator lease.
3. Prove the current rc.4/no-broker path fails or skips with an explicit actionable reason; do not claim it as passing evidence.
4. Implement the probe, scripts, Scale harness, sidecar multi-process integration coverage, and verification ledger.
5. Run:
   - sidecar focused multi-process tests, full fast Rust/Python/clippy/fmt gates;
   - Miller probe build, focused Scale tests when a broker-capable local sidecar is available, `scripts/test.sh`, and Release build;
   - a local macOS same-model/crash/short-soak run using the from-source sidecar candidate when feasible.
6. Run Miller impact and `git diff --check` in both repos.
7. Write `.razorback/sdd/task-8-report.md` with exact RED/GREEN, process/soak results, acceptance matrix, and honest pending Linux/Windows/NVIDIA/30-minute rows.
8. Do not commit or push. The lead will review with Grok and decide the commit boundary.

## Acceptance

- Same-model N-session steady state has exactly one live model-loaded sidecar.
- Multi-model steady state has at most one accelerated broker.
- GPU memory is effectively constant with N same-model sessions.
- Every crash unblocks the active request and later requests recover or remain lexical.
- No orphan broker remains after owner termination.
- Short-lived owner exit recovers without hanging a surviving client.
- Windows named-pipe operations honor deadlines under sleep/resume and process death.
- Any platform/hardware row not runnable locally is explicit and remains a release gate, never silently marked complete.
