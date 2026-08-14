# Task 1B-A report

## Source state

- Worktree: `/home/murphy/source/miller/.worktrees/performance-recovery`
- Branch: `feature/performance-recovery`
- Base / starting commit: `a53b138dcef4eb67b88b0ef10146e7316ef36ee2`
- Starting state: clean at the requested base.
- Final implementation commit: `6c7e030c536bbcad1c1563c632b3e3241fb5f4d2` (`perf: add faithful recovery replay harness`).
- Goldfish checkpoint committed at `.memories/2026-08-14/064352_353b.md`.
- Final owned-tree state after the implementation commit: clean for owned paths; the lead-only plan edit remains modified and unstaged.
- A lead-only edit to `docs/plans/2026-08-13-miller-performance-recovery-plan.md` was already present while this packet ran; it is not part of this packet and was not staged.

Miller evidence was used first. The assigned workspace was registered as `performance-recovery-9ee5b2dc77a2`, but its index reported `freshness: error` after `julie-extract store import` exited 135. Context, inspect, impact, and trace were still used for the indexed `ContextTool` seam; script/manifest/snapshot files were unindexed, so bounded reads were used without refresh or replay.

## RED/GREEN evidence

- Python RED: after adding the contract tests, `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_recovery.py` reported 29 tests with 1 error and 4 failures. The failures covered missing execution-kind dispatch/validation, missing producer/MCP argv support, the Windows null-memory gate, and the expanded manifest.
- C# RED: with the old default-on predicate temporarily restored, `dotnet test --filter "FullyQualifiedName~ContextToolTests.RunReferenceAware_DefaultsBatchReadsOff"` failed 1/1 with expected batch count `0`, actual `1`.
- Python GREEN: `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_recovery.py` — 29 passed.
- Snapshot GREEN: `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_store_snapshot.py` — 3 passed.
- C# GREEN: `dotnet test --filter "FullyQualifiedName~ContextToolTests"` — 111 passed, 0 failed, 0 skipped.
- Brief filter GREEN: `dotnet test --filter "FullyQualifiedName~ReferenceEvidenceReaderTests"` — 27 passed, 0 failed, 0 skipped.
- `git diff --check` passed.

## Changes

- Added and validated `miller_cli`, `mcp_bootstrap`, and `julie_store` execution kinds.
- Routed MCP workloads through `miller serve` initialize/initialized framing and Task 5A `startup_total` JSONL phase evidence; the reader row keeps the leader session alive when both startup rows are selected.
- Routed producer rows through the checked `JulieStoreClient.BuildArguments` contract, including store/view, request identity, timeout, JSON, and the full-resolve `JULIE_STORE_RESOLUTION_DELTA=off` oracle. One-file preparation changes an isolated staged file and performs the producer import before resolve.
- Added strict timeout-vs-hard-budget validation, isolated mutating workload snapshots, lexical depth controls, semantic-on and batch off/on parity rows, and the Windows missing-`PrivateUsage` hard-gate failure.
- Added `perf-store-snapshot.py`: explicit source/destination, alias/live/owner checks, read-only SQLite backup for every SQLite database in the family, stable source facts, destination quick checks, and WAL/SHM refusal.
- Restored context reference batching to explicit opt-in (`1`/`on`/`true`) in the actual live seam, `ContextTool`, with a focused default-off regression.

## Files

- `scripts/perf-recovery.py`
- `scripts/tests/test_perf_recovery.py`
- `scripts/benchmarks/perf-recovery-workloads.json`
- `scripts/perf-store-snapshot.py`
- `scripts/tests/test_perf_store_snapshot.py`
- `src/Miller.Server/Tools/ContextTool.cs`
- `tests/Miller.Tests/Server/ContextToolTests.cs`
- This report.

## Risks and limits

- Per packet safety constraints, no real incident-store replay, live DB copy, baseline capture, or long producer resolve was run. The harness and snapshot paths were exercised only with disposable test fixtures.
- The named `Server/Tools/ReferenceEvidenceReader` paths in the original brief do not exist at the base; the lead corrected ownership to the actual `ContextTool`/`ContextToolTests` seam.
- The assigned Miller index remains unhealthy on the pre-existing exit-135 import failure; no refresh or live-store mutation was attempted.

## Harness correction packet

- Correction base: `d81e187fcb6d8ddf0af47bc8bd486342d9e80844` on `feature/performance-recovery`.
- RED: after the correction tests were added and before the behavior changes, `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_recovery.py` ran 41 tests with 10 failures and 3 errors. The failures covered producer path/setup/scope contracts, workspace-open convergence, MCP status/deadline/PID/attempt/cleanup behavior, depth semantics, output atomicity, and Windows handle signatures.
- GREEN: `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_recovery.py` — 44 passed, 0 failed, 0 skipped.
- Verification: `git diff --check` passed.
- Correction changes are limited to `scripts/perf-recovery.py`, `scripts/tests/test_perf_recovery.py`, and `scripts/benchmarks/perf-recovery-workloads.json`; the snapshot helper/tests, .NET files, and lead-owned plan were not changed by this packet.
- The correction uses disposable test fixtures only. No real incident replay, live-store copy, producer resolve, snapshot copy, push, or release was run.
- Goldfish checkpoint: `.memories/2026-08-14/071901_9ffa.md` (`checkpoint_9ffab36c`), captured before commit.
- Correction implementation commit: `89ec57492f65b62dc2c2586e596a006011e09491` (`perf: correct recovery replay harness contracts`).
- Final state after the correction commit: owned paths are clean; only the lead plan edit remains modified and unstaged. No additional implementation work is required for this packet.
