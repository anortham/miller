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

## Snapshot bounded-shadow correction packet

- Worktree: `/home/murphy/source/miller/.worktrees/performance-recovery`; branch: `feature/performance-recovery`; correction start `eafc87b0a410044de8958587debbe6e06a305c83`.
- Starting dirty state: the lead-owned plan and the two snapshot files were already modified and unstaged. The direct-source experiment was replaced in place; the plan remained untouched.
- Miller was used first against `performance-recovery-9ee5b2dc77`; the index reported freshness error, so bounded source inspection followed. No live store, incident snapshot, replay, producer/store run, or .NET test was performed.
- RED: the direct `mode=ro` implementation ran 30 tests with one `ValueError: source family changed during snapshot`; the failure proved SQLite changed source `-shm` state. Earlier RED also caught the obsolete no-shadow assertion and ctime mutation from chmod.
- GREEN: `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_store_snapshot.py` — 30 passed, 0 failed, 0 skipped. `git diff --check` passed.
- Behavior: every SQLite input is streamed into a private `.perf-store-input-*` directory with its existing main/WAL/SHM triplet; source content, device/inode/size/mtime/ctime/mode facts are checked before and after shadow creation and use. SQLite opens only the shadow, so committed WAL rows are preserved without source mutation. Owner claims use the same private-shadow path before family copying; source state is rechecked before promotion. Destination quick-check, WAL/SHM absence, bounded digest, cleanup, and atomic promotion remain enforced.
- Tests now prove private-shadow cleanup, source bytes and metadata stability, committed WAL inclusion, same-size restored-mtime mutation detection, all-generation/base/sidecar traversal, and live/unknown/dead maintenance-owner gates.
- Owned files for this correction: `scripts/perf-store-snapshot.py`, `scripts/tests/test_perf_store_snapshot.py`, this report, and one pre-commit Goldfish checkpoint. No dependency, public surface, plan, live-store, push, or release change.
- Implementation commit: `dd2b83d9` (`perf: use private shadows for store snapshots`), with checkpoint `.memories/2026-08-14/081813_222c.md` (`checkpoint_222cc173`) included.
- Final state: owned implementation paths, report, and checkpoint are clean after commits `dd2b83d9` and `e344b130`; only the lead-owned `docs/plans/2026-08-13-miller-performance-recovery-plan.md` remains modified and unstaged.
- Risks: native Windows execution was unavailable; mocked Windows PID coverage remains the available check. Private shadowing adds a bounded stream copy per SQLite database and uses the local temporary filesystem. No real incident/store snapshot or replay was run.

## Snapshot correction cycle 4 — durable WAL inputs only

- Worktree: `/home/murphy/source/miller/.worktrees/performance-recovery`
- Branch: `feature/performance-recovery`
- Correction start: `3ea9f2b7ad1b7d1a46716b97796183d0a8cfad95`
- Starting dirty state: `.memories/briefs/miller-performance-recovery-implementation.md` and `docs/plans/2026-08-13-miller-performance-recovery-plan.md` were pre-existing modified, unstaged files; both were preserved and not staged.
- Miller was used first. Workspace `performance-recovery-9ee5b2dc77a2` reported `freshness: error` / `scan_failing`, and the named script bodies were stale or unavailable; bounded source inspection followed. No refresh, live-store access, replay, producer/store execution, or .NET test was run.
- RED: the new `test_snapshot_allows_shm_churn_during_wal_backup` failed with `ValueError: source database changed while creating read-only shadow` before the production change, proving source `-shm` churn was incorrectly treated as a durable mutation.
- GREEN: `PYTHONDONTWRITEBYTECODE=1 python scripts/tests/test_perf_store_snapshot.py` — **32 passed**, 0 failed, 0 skipped. The test's expected CLI usage diagnostic is captured; the process exited 0.
- Additional coverage proves committed WAL content is retained, source main/WAL bytes and device/inode/mtime/ctime/mode remain unchanged, source `-shm` churn is tolerated and never stream-copied, same-size WAL mutations still fail, destination WAL/SHM files are absent, and cleanup/promotion guards remain green.
- AST validation of both Python files passed; `git diff --check` passed.
- Behavior: `_database_state` and `_database_input` now treat only the main database and `-wal` as durable inputs. `_source_files` skips `-shm` before alias/reparse checks. SQLite still creates its own SHM inside the writable private shadow; the source is never opened, checkpointed, chmodded, or gated on SHM state.
- Owned implementation commit: `d7f636ca` (`perf: ignore source SQLite shm churn`), including Goldfish checkpoint `.memories/2026-08-14/085031_6615.md` (`checkpoint_6615f278`).
- Owned implementation files: `scripts/perf-store-snapshot.py`, `scripts/tests/test_perf_store_snapshot.py`, and the checkpoint. This report is updated in a separate report commit.
- Risks: native Windows execution remains unavailable locally; disposable mocked Windows coverage is unchanged. No real incident/store snapshot or replay was run.

## Task 1B-B copied-store baseline — blocked at producer/Miller eligibility

- State: `/home/murphy/source/miller/.worktrees/performance-recovery`, branch `feature/performance-recovery`, HEAD `25f7ca0b63629fb5fe27d7b0b35934b6d957a942`; Git-owned paths clean after the run. The task artifact directory is ignored; the copied family and staged workspace are untracked outside the worktree under `/home/murphy/.miller/perf-recovery/task-1b-baseline/`.
- Discovery: `.miller/store.json` identified live workspace `/home/murphy/source/miller/.worktrees/performance-recovery`, family `a271f2bd-7368-4da6-b5aa-24ffad69fb1f`, view `0be23b5f-20f3-4fc9-8ea1-19d3be06b630`, store root `/home/murphy/.miller/stores/a271f2bd-7368-4da6-b5aa-24ffad69fb1f`, and generation `gen-001`. Source size was 4,187,116 KiB; preflight filesystem `/dev/nvme0n1p3` had 1,839,196,440 KiB available. `lsof`/`fuser` found no target-family DB handles; the snapshot owner gate found no live or unknown coordinator owner and passed its pre/post checks. No process was stopped.
- Snapshot command: `PYTHONDONTWRITEBYTECODE=1 python scripts/perf-store-snapshot.py --source /home/murphy/.miller/stores/a271f2bd-7368-4da6-b5aa-24ffad69fb1f --destination /home/murphy/.miller/perf-recovery/task-1b-baseline/copied-store --live-root /home/murphy/source/miller/.worktrees/performance-recovery`. Result: PASS, generation `gen-001`, 16 SQLite databases, six resolution bases, `quick_check=ok`, `wal_shm=false`, destination SHA-256 `d81da66fb22abf43ed98af853046c37216bb281788b13c2b88db80aa5e84d6b2`, copied size 4,183,648 KiB, and no destination WAL/SHM/journal files. Source SHM churn was excluded by the verified helper contract; no source DB was opened or written by this packet.

## Snapshot correction cycle 5 — restore required runtime directories

- Worktree: `/home/murphy/source/miller/.worktrees/performance-recovery`; branch: `feature/performance-recovery`; start `25f7ca0b63629fb5fe27d7b0b35934b6d957a942`.
- Root cause: the snapshot helper copied files but did not materialize the producer-required empty `spool/` and `scratch/` directories; the copied-store import consequently failed before recovery with `ENOENT`.
- RED: the two focused regressions failed because successful snapshots lacked both directories and source transient files were copied into the destination.
- GREEN: `PYTHONDONTWRITEBYTECODE=1 python -m unittest scripts.tests.test_perf_store_snapshot.PerfStoreSnapshotTests.test_snapshot_uses_read_only_backup_and_verifies_family scripts.tests.test_perf_store_snapshot.PerfStoreSnapshotTests.test_snapshot_does_not_copy_transient_store_directory_contents` — 2 passed; full `PerfStoreSnapshotTests` — 33 passed; `py_compile` and `git diff --check` passed.
- Behavior: `_copy_family_files` creates empty destination `spool/` and `scratch/` roots and skips source contents beneath those roots. SQLite main/WAL/SHM handling, source stability checks, destination validation, cleanup, and atomic promotion are unchanged.
- Files changed: `scripts/perf-store-snapshot.py` and `scripts/tests/test_perf_store_snapshot.py`. No live-store replay, producer run, .NET change, dependency, plan, push, or release was performed.
- Native Windows execution remains unavailable locally; the existing cross-platform and mocked owner-liveness coverage remains the available check.
- Build/tool identity: `dotnet build src/Miller.Server/Miller.Server.csproj -c Release --no-restore` passed with 0 warnings/errors. Miller `1.19.1+25f7ca0b6362`, binary SHA-256 `c79309e4dd7971e71ea44a2704ea40222e7bf2fa569f49e6c51dfd4f870d2818`; Julie source `51c2977f1c5ad9ff6f2f92010a25e3768bf5364e`, binary `2.33.2`, SHA-256 `750461a3f99013dfad689d9de9946b7a0aec07058ef64fbb5060d1d9cbaeac4f`.
- Baseline commands used the staged workspace pointer and copied family only, with `PATH=.tools:$PATH`; mutating rows were isolated by the harness. Startup leader/reader exited 2 because the copied incident state's first `startup_total` failed on an existing partial resolution output and later attempts were `startup_total=skipped`, leaving no leader session for the warm-reader row. `workspace.open.no_change` setup exited 2; direct copied-workspace CLI evidence was `ineligible_extractor` under the default policy. `producer.retry.identical` produced four records, all exit 1, no timeout; one-file and full resolve both exited 2 during their required fresh full-import setup, so no resolve phase ran and the 1,501-second full producer deadline was not exercised. Inspect produced four exit-3 records; context depth 0/1, semantic, and batch off/on produced 20 exit-3 records (batch off/on hashes matched the shared failure output); impact produced four exit-3 records; trace target discovery exited 2. The harness did not emit files for aborted slices.
- Evidence files: `artifacts/perf/task-1b-baseline/snapshot-result.json` (2,911 bytes, SHA-256 `0c06591fb1efd23fc878d1938924ccfcff77671ded512ac06da8362ff1447e15d`), `producer-retry.jsonl` (5,383 bytes, `daec8e55a9aa55096e1a1aec99eb69c61d77d4f955dc424329932cdeecdc08d7`), `tool-inspect.jsonl` (5,205 bytes, `afe49c8a8a16c988dbcf6adb3fcd0ad8434de0304d2bc5872de2287027b23164`), `tool-context.jsonl` (28,889 bytes, `016675e10bedcd3e53eedf6d5d1490b83226ab6a023e142ff4ebba71d17c8eb8`), and `tool-impact.jsonl` (5,257 bytes, `2cfbecd3af29e5851182658ac059d5f73226b7970da7bd8641c5214458863ea3`).
- Verification: no live-store facts were intentionally changed; the original worktree pointer remained untouched. No checkpoint or commit was made because the baseline acceptance requiring a complete full resolve and faithful successful rows was not met. Remaining blocker is a quiet, compatible copied-store/producer setup (or an explicitly approved downgrade-policy decision); this packet did not enable `MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1`.
- Producer diagnostic (copied-store scope only): one fresh isolated snapshot was used to invoke the absolute Julie binary with the exact manifest argv. It exited `1` in `3 ms`, `timed_out=false`, with empty stderr and structured stdout `failure_class=internal`, `coordinator=failed`, and `error.message=No such file or directory (os error 2)`. Executable resolution and all manifest flags were independently confirmed from the staged workspace, so this is an internal copied-family/isolated-root missing-path failure, not a shell/PATH or unknown-flag failure. The copied coordinator's sole nonterminal row is an import `c24cfe36c2ef4e0ab6720aedecbd4381` with stale owner `cli-253422` (heartbeat `1786694570396`), root `/home/murphy/source/miller/.worktrees/performance-recovery`, and no matching `perf-recovery-*` idempotency key; it was not repaired or hand-edited.

## Task 1B-B resumed baseline — copied snapshot is not Julie-runnable

- State: worktree `/home/murphy/source/miller/.worktrees/performance-recovery`, branch `feature/performance-recovery`, HEAD `b0715fe9dd97533c4645f917bcc81002ccfc439f`. Start state was clean; after this report/evidence update only this report is tracked as modified. No source, harness, plan, pin, or database file was edited. The copied families and JSONL evidence are external/ignored under `/home/murphy/.miller` and `artifacts/perf/task-1b-baseline/`.
- Discovery: the live pointer still identifies family `a271f2bd-7368-4da6-b5aa-24ffad69fb1f`, view `0be23b5f-20f3-4fc9-8ea1-19d3be06b630`, generation `gen-001`, and source `/home/murphy/.miller/stores/a271f2bd-7368-4da6-b5aa-24ffad69fb1f`. Source size was `4,223,924 KiB`; the filesystem had `1,836,896,144 KiB` available. No observed process had a source-family DB file open; the helper owner gate passed before and after copying. No process was stopped.
- Build identity: `dotnet build src/Miller.Server/Miller.Server.csproj -c Release --no-restore` passed with 0 warnings/errors. Miller was `1.19.1+b0715fe9dd97`, SHA-256 `c79309e4dd7971e71ea44a2704ea40222e7bf2fa569f49e6c51dfd4f870d2818`; Julie was `2.33.2`, SHA-256 `750461a3f99013dfad689d9de9946b7a0aec07058ef64fbb5060d1d9cbaeac4f`, source `51c2977f1c5ad9ff6f2f92010a25e3768bf5364e`.
- Incident snapshot: a new destination `/home/murphy/.miller/perf-recovery-task1b-b-incident-b0715fe9/copied-store` was promoted; the prior `/home/murphy/.miller/perf-recovery/task-1b-baseline/copied-store` was not overwritten. The snapshot passed `quick_check`, contains 16 SQLite databases and six bases, has `wal_shm=false`, SHA-256 `924c5b1dd3524b1cca528732f0a7fd67e2ef9ff52149b62b9dabb04e1f53c391`, copied size `4,223,492 KiB`, no destination WAL/SHM/journal files, and empty `spool`/`scratch` directories. Snapshot evidence is `artifacts/perf/task-1b-baseline/incident-snapshot-b0715fe9.json` (615 bytes, SHA-256 `3465571ecb21a347d27e7b1c2901fd0eae73300d7a41c878fe9244c45f9b9a8f`).
- Producer gate: using an external incident workspace clone and the exact manifest `producer.retry.identical` row, the harness produced four records. Every attempt exited `1` in about `33 ms`, timed out false, and had no producer timing/version or hard-gate pass. Evidence is `artifacts/perf/task-1b-baseline/incident-producer-retry-b0715fe9.jsonl` (4 lines, 5,383 bytes, SHA-256 `a68d6e3fe49425963998bf1aeb9db91e085062e096a9af3d81081b707a852b5d`).
- Copied-store diagnosis: a fresh disposable snapshot was invoked with the exact absolute Julie argv. `store import` exited `1` in `44 ms` with structured `failure_class=internal`, `coordinator=failed`, and `error.message=SQLite pragma journal_mode is "delete", expected "wal"`; `gen-001/store.db` reports `journal_mode=delete`. This is not a PATH, timeout, unknown-flag, or missing-runtime-directory failure. Evidence is `artifacts/perf/task-1b-baseline/producer-diagnostic-import-b0715fe9.json` (870 bytes, SHA-256 `d8c24ec4fabb16d6afa1b2fa2aca79158e26683ba334ad9694d3c5e2f157980d`).
- Public setup check: on a separate disposable copy, public `store resolve` exited `1` with `resolution_failed: resolution base file identity mismatch: catalog counts, bytes, or SHA-256 differ from the file`; the destination remained `journal_mode=delete`. Evidence is `artifacts/perf/task-1b-baseline/resolve-diagnostic-b0715fe9.json` (1,145 bytes, SHA-256 `b51b987c1423252ef9797ee75aef2e225a131781a280625d71b0134cb8464511`). No hand-edit, downgrade, checkpoint, or source mutation was attempted.
- Gate decision: stop after the incident producer gate. The healed fixture, startup leader/reader, cheap tools, one-file resolve, and full resolve were not run because the verified copied store cannot be made ready through the available public Julie path: import rejects its DELETE-journaled destination and resolve rejects the backup-produced base identity. This packet therefore remains **BLOCKED**; no Task 1B-B checkpoint or commit was made. Native Windows execution remains unverified locally.

## Snapshot correction cycle 6 — preserve WAL-free identity and WAL mode

- Worktree: `/home/murphy/source/miller/.worktrees/performance-recovery`; branch: `feature/performance-recovery`; start `b0715fe9dd97533c4645f917bcc81002ccfc439f`.
- Miller workspace status was fresh, but the snapshot script was unavailable through the indexed sidecar; bounded local symbol inspection followed. No live store, copied-store replay, producer command, or .NET test was run.
- RED: WAL-free base bytes changed under SQLite backup, zero-byte partial databases became nonempty SQLite files, and a nonempty-WAL destination reported `journal_mode=delete`.
- GREEN: focused regression trio passed; full `PerfStoreSnapshotTests` — 35 passed, 0 failed; `py_compile` and `git diff --check` passed.
- Behavior: zero-byte WAL files are nondurable and omitted from family state/copy. WAL-free databases use stable stream copies with source pre/post facts, exact size/hash equality, and immutable quick checks when valid. Nonempty-WAL databases continue through private shadow/SQLite backup, retain `journal_mode=wal`, checkpoint/truncate, and remove destination WAL/SHM sidecars after close. Cleanup and source-mutation gates remain enforced.
- Files changed: `scripts/perf-store-snapshot.py`, `scripts/tests/test_perf_store_snapshot.py`, and this report. No source/live database mutation, dependency, plan, push, or release change.
- Native Windows execution remains unavailable locally; Windows sharing/locking behavior remains a pre-live risk.
