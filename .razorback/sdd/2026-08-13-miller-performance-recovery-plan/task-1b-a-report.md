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

## Snapshot and Windows safety correction packet

- Worktree: `/home/murphy/source/miller/.worktrees/performance-recovery`
- Branch: `feature/performance-recovery`
- Correction start: `7cd7529de9d5af8e7ed2ddebb6befebaaa3305ac`
- Start dirty state: only the lead-owned plan edit was modified and unstaged.
- Implementation commit: `b99cdf90f403de271a3c23ce05a70c3485834e09` (`perf: harden store snapshot safety`).
- Post-commit state: owned paths are clean; only `docs/plans/2026-08-13-miller-performance-recovery-plan.md` remains modified and unstaged.
- RED: after adding the correction contract tests, `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_store_snapshot.py` ran 15 tests with 11 failures and 1 error.
- GREEN: `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_store_snapshot.py` — 17 passed, 0 failed, 0 skipped.
- Verification: `python -m py_compile` passed and `git diff --check` passed.

The snapshot helper now requires an explicit live root in both the API and CLI, rejects canonical/parent-child/symlink/reparse and per-file hardlink aliases before temporary creation, and checks only the coordinator's `requests`, `writer_lease`, and `maintenance_intent` schemas. Request owners map to writer lease holders without parsing opaque identifiers. PID probes are tri-state and Windows calls use pointer-safe signatures; inaccessible probes refuse even expired leases. SQLite URIs are percent-encoded, WAL/SHM inputs are read through disposable shadow copies so source bytes remain untouched, source facts include main/WAL/SHM identity/size/mtime plus SQLite facts, claims are rechecked before promotion, and digests stream in bounded chunks. Destination SQLite files are normalized to DELETE journaling and temporary failures clean up without promoting a destination.

Owned changes are limited to `scripts/perf-store-snapshot.py`, `scripts/tests/test_perf_store_snapshot.py`, this report, and the pre-commit Goldfish checkpoint. No incident/live snapshot, real replay, dependency, harness/.NET/plan edit, push, or release was performed.

Risks: Windows native execution was not available locally; Windows API behavior is covered by injectable mocked probes. WAL shadowing adds a second bounded file copy for databases with sidecars. Verification used disposable fixtures only; the harness test suite and live family were not run.

## Harness review cycle 2

- Start: `/home/murphy/source/miller/.worktrees/performance-recovery`, branch `feature/performance-recovery`, base `29b37d6bdd6580a43846e27b39a82a90943d19a9`.
- Start dirty state: the lead-owned plan and an unrelated snapshot-helper edit were already modified and unstaged. The snapshot helper was not touched, tested, staged, or committed by this packet.
- Miller was used first: workspace `performance-recovery-9ee5b2dc77a2` reported `freshness_status=scan_failing`; indexed `_McpSession`/`_validate_command` inspection and harness impact were available. No refresh, live-store access, replay, or producer/store execution was attempted.
- RED: after adding the cycle-2 contract tests and before behavior changes, `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_recovery.py` ran 54 tests with 8 failures and 3 errors. Failures covered running-status retry, nonzero MCP exit preservation, bounded teardown/capture, UTF-8/queue behavior, context depth semantics, full deadline margin, producer flag whitelisting, and environment-marker removal.
- GREEN: `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_recovery.py` — 55 passed, 0 failed, 0 skipped.
- Verification: `git diff --check` passed; AST parsing of the two Python files passed.
- Cycle-2 files changed: `scripts/perf-recovery.py`, `scripts/tests/test_perf_recovery.py`, and `scripts/benchmarks/perf-recovery-workloads.json`. No snapshot, .NET, dependency, public-surface, lead-plan, replay, store, push, or release changes were made.
- Full resolve now uses a 1,501-second producer request deadline and 1,502,000ms harness timeout; hard budgets remain 60/120 seconds.
- Goldfish checkpoint: `.memories/2026-08-14/074656_71c5.md` (`checkpoint_71c5597e`), captured before commit.
- Implementation commit: `c8cf2bdd0a3b0c898c050b117c168b8b7a262e42` (`perf: harden recovery harness review contracts`).
- Final post-implementation state: the three harness files and checkpoint are clean; the lead plan, snapshot helper, and snapshot tests remain modified and unstaged. The report-only commit follows separately.

## Workspace status content correction packet

- Worktree: `/home/murphy/source/miller/.worktrees/performance-recovery`
- Branch: `feature/performance-recovery`
- Correction base / starting commit: `9a8035b7c2bad4712f123b02eb09bf230b6ef226`.
- Starting dirty state: the lead-owned plan plus `scripts/perf-store-snapshot.py` and `scripts/tests/test_perf_store_snapshot.py` were modified and unstaged. Those files belong to other workers and were preserved without edits or staging.
- Miller was used first. Workspace `performance-recovery-9ee5b2dc77a2` reported scan-failing/stale state; `_status_probe_state` was not available as a fresh indexed symbol, so bounded source reads were used after the Miller checks. No refresh, live-store access, replay, producer/store execution, or .NET test was attempted.
- RED: after adding a fake session with the actual JSON-RPC `result.content` text-block shape, `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_recovery.py` ran 56 tests with 1 expected failure: content-text `bootstrap: failed` was incorrectly returned as `ready`.
- GREEN: `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_recovery.py` — 56 passed, 0 failed, 0 skipped.
- Behavior: `_status_probe_state` now treats case-insensitive `bootstrap: running` and `bootstrap: idle` content as retryable, `bootstrap: failed` and `bootstrap: unavailable` as failed, and bound workspace status content as ready by the existing default. Structured status handling also treats `idle` as retryable. `_bootstrap_session` and its single absolute deadline were not changed.
- Verification: AST parsing of `scripts/perf-recovery.py` and `scripts/tests/test_perf_recovery.py` passed; `git diff --check` passed.
- Owned changes in this correction: `scripts/perf-recovery.py`, `scripts/tests/test_perf_recovery.py`, this report, and Goldfish checkpoint `.memories/2026-08-14/075315_8c00.md` (`checkpoint_8c00cf03`).
- Implementation commit: `cfb92605a093c4ede26e92bc7915646c4d221219` (`perf: parse MCP workspace status content`).
- The implementation commit staged only the two harness files and the checkpoint. The report is being recorded separately; the lead plan and snapshot-worker files remain modified and unstaged.
- No real incident replay, snapshot copy, producer/store execution, dependency/public-surface change, .NET edit, push, or release was performed.

## Snapshot review cycle 2

- Worktree: `/home/murphy/source/miller/.worktrees/performance-recovery`; branch: `feature/performance-recovery`.
- Start: `29b37d6bdd6580a43846e27b39a82a90943d19a9`; the shared branch advanced to `2ffb6ce3b76159b6ecc17d5ff0468e2c783a876b` during this packet. Start dirty state included the lead-owned plan and the unstaged connection-cleanup fix in `scripts/perf-store-snapshot.py`; both were preserved.
- Miller was used first against workspace `performance-recovery-9ee5b2dc77a2`; its index remained scan-failing/stale, so bounded source reads followed. No refresh, live-store access, incident snapshot, replay, harness, .NET, dependency, push, or release was run.
- RED: after adding cycle-2 tests and before the helper changes, `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_store_snapshot.py` ran 26 tests with 6 failures. Failures covered direct-source shadow-copy use, live-root existence/symlink validation, same-size content changes, and digest-before-promotion cleanup.
- GREEN: `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_store_snapshot.py` — 26 passed, 0 failed, 0 skipped.
- Behavior: database/WAL/SHM shadow copies were removed; source databases are opened directly through percent-encoded `mode=ro` URIs and backed up to temporary destinations. Existing WAL `-shm` files are protected as read-only during the connection and restored afterward so the stopped WAL committed row is included without source-byte changes. Source metadata and streamed content digests are stable-checked before/after reads and around each database backup. Explicit live roots must be existing directories; live/source symlink, reparse, and hardlink aliases are rejected. Complete destination digest data is computed before promotion, and cleanup errors plus destination-presence violations are surfaced. Partial source/destination connections close on all failures.
- Verification: `git diff --check` passed; the focused suite passed. The task-range check is expected to pass after the committed memory whitespace fix is included.
- Owned files: `scripts/perf-store-snapshot.py`, `scripts/tests/test_perf_store_snapshot.py`, this report, `.memories/2026-08-14/073244_699c.md` (trailing blank-line removal), and checkpoint `.memories/2026-08-14/075543_29bd.md` (`checkpoint_29bd69a7`). The lead plan remains modified and unstaged.
- Implementation commit: `4e5f2186af66842750521b7b7a16d5d15f896390` (`perf: harden source store snapshot coherence`). Post-implementation state: only `docs/plans/2026-08-13-miller-performance-recovery-plan.md` remains modified and unstaged.
- Risks: native Windows SQLite/filesystem behavior was not executable locally; mocked Windows PID coverage remains the available check. The temporary read-only `-shm` protection should be exercised on Windows before live use. Only disposable fixtures were used.
