# Performance Audit Closure Implementation Plan

**Status:** Implemented and verified on 2026-08-24 at `ceca0003`.

**Design:** `docs/plans/2026-08-24-performance-audit-closure-design.md`

## Goal

Close every deferred or open item in `docs/findings/2026-08-23-performance-audit.md` on the existing audit branch. Preserve public output and correctness while removing measured CT and graph hot paths, enforcing the approved retention policy, and ending with zero open ledger rows.

## Execution rules

- Use strict TDD for production behavior: write one focused behavioral test, observe the expected failure, implement the minimum repair, and rerun the same scope.
- Use Miller `context`, `inspect`, `trace`, and `impact` before changing symbols or APIs. Do not infer API shapes.
- Workers run only the named focused test classes. The lead owns affected batches, performance measurements, Scale tests, the full fast suite, and Release build.
- Preserve active CT runs, current state/watermarks, active/newest-complete generations, live provider outputs, and live leases.
- Keep public CLI/MCP JSON, selection semantics, graph ranking/truncation, provider result semantics, and default graph limits byte-identical.
- No external-model review is scheduled. Security scope is `none declared`: this work adds no auth, secrets, dependency, network, or external-input surface.

## Verification strategy

| Scope | Command or evidence | Owner |
| --- | --- | --- |
| worker-red-green | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~<owned test classes>"` | worker |
| affected-change | Focused combined classes for the completed slice plus `git diff --check` | lead |
| performance | Fixed before/after workload plus deterministic counters, query plan, allocations, bytes, or operation counts | lead |
| branch-gate | `scripts/test.sh`; `scripts/test.sh scale`; `dotnet build Miller.slnx -c Release`; `git diff --check` | lead |
| live CT | Stop/restore existing CT state; run the fixed daemon/provider workload and record CPU, allocations, disk, and output identity | lead |

## Parallel execution contract

| Task | Ownership | Depends on | Serialization required | Commit mode |
| --- | --- | --- | --- | --- |
| 1. Baselines and observers | Measurement artifacts/tests only; no production behavior | none | Yes — must precede optimized code | serial-worker-commit |
| 2. Cursor probe and convergence | Poller, read-session probe, cursor persistence, focused tests | 1 | Yes — shared CT schema and cursor contract | serial-worker-commit |
| 3. Active-project selection | CT project schema/store, selector, retry/status projection, focused tests | 2 | Yes — consumes migrated schema and poller cursor | serial-worker-commit |
| 4. Completion batching | CT result schema/store completion path, focused tests | 3 | Yes — shared CT schema/store | serial-worker-commit |
| 5. History retention | CT retention store/coordinator maintenance, focused tests | 4 | Yes — consumes observed-time schema | serial-worker-commit |
| 6. Cache janitor | Generation/cache lifecycle and focused tests | 5 | Yes — shared coordinator maintenance tail | serial-worker-commit |
| 7. .NET output deduplication | Dotnet provider output layout and Scale fixture/tests | 1 | No — disjoint provider files | parallel-lead-commit when paired with Task 8 |
| 8. Graph-read batching | Query-time resolution, bounded fact cache, graph observers/tests | 1 | No — disjoint indexing files | parallel-lead-commit when paired with Task 7 |
| 9. Audit closure and branch gate | Findings/design/docs, measurements, all gates | 2–8 | Yes — integration verification | serial lead task |

## Task 1: Capture fixed baselines and deterministic observers

**Files:** existing performance test/fixture files under `tests/Miller.Tests/Testing/**` and `tests/Miller.Tests/Indexing/**`; measurement evidence in `docs/findings/2026-08-23-performance-audit.md`.

**Build:** Add or extend test-only observers that count full compatibility projections, CT completion SQL statements/temp sorts, graph resolve/detail/slice work, and .NET generation bytes/file identities. Capture the exact unchanged baseline workloads before any production optimization.

**Acceptance criteria:**

- [x] Baseline evidence records workload identity and direct operation counts for every remaining performance finding.
- [x] Observers measure behavior without changing production output or timing policy.
- [x] Focused tests prove each observer detects the current hot path.

## Task 2: Replace full-session ticks and converge durable cursors

**Files:** `src/Miller.Indexing/Reads/WorkspaceReadSessionFactory.cs`; `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`; `src/Miller.Testing/Store/CtSchema.cs`; a cursor store partial; poller/read-session/schema tests.

**Build:** Extend the freshness probe with the stable CT identity/revision. Persist the last reconciled cursor per workspace. On unchanged ticks perform zero full opens; on change, reconcile probe-to-session with bounded drift retry. Startup reconciles persisted-to-live, advances watermarks on complete empty deltas, and fails closed on identity mismatch or unavailable history.

**Acceptance criteria:**

- [x] An unchanged tick opens zero compatibility projections.
- [x] Restart after an empty or changed interval converges without a recovery run.
- [x] Moving cursor, truncation, and identity mismatch remain partial/unavailable rather than false green.

## Task 3: Make selection project-filtered and live state bounded

**Files:** `src/Miller.Testing/Store/CtSchema.cs`; CT project/case/status store partials; `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`; daemon queue/retry state; status tests.

**Build:** Add/backfill normalized project association and indexes. Read cases/statuses only for the enabled project and reuse one revision snapshot. Make retry bookkeeping proportional to queued/in-flight work. Exclude disabled-project lifecycle pseudo-cases from active status while retaining history.

**Acceptance criteria:**

- [x] Selection does not load all workspace cases/statuses for every project.
- [x] Unknown/unmapped cases remain fail-closed and selection output is unchanged.
- [x] Retry keys are removed at terminal completion.
- [x] Disabled lifecycle rows no longer force an active partial verdict; re-enable restores visibility.

## Task 4: Batch run completion and index recent history

**Files:** `src/Miller.Testing/Store/CtSchema.cs`; `src/Miller.Testing/Store/ContinuousTestStore.Runs.cs`; related completion/flakiness tests.

**Build:** Materialize/backfill `observed_at`; add the covering index; batch the newest-50 outcome read across distinct affected test cases; prepare/batch score updates while preserving folding and rollback semantics.

**Acceptance criteria:**

- [x] Completion issues no per-result recent-history query or temp order-by sort.
- [x] Statement count is at most `C + 2R` for a small asserted constant `C`.
- [x] Flakiness, normalization, tie-break, rollback, and freshness results are unchanged.

## Task 5: Enforce CT history retention

**Files:** a new `ContinuousTestStore.Retention.cs`; `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`; retention tests.

**Build:** Add one transactional, idempotent prune operation invoked from maintenance. Preserve active runs, all history younger than 30 days, newest 50 outcomes/test, referenced artifacts, and newest artifact/enabled project. Report logical deletions and SQLite page/freelist facts without foreground compaction.

**Acceptance criteria:**

- [x] The exact 30-day/50-outcome keep-set is deterministic and workspace-scoped.
- [x] Current states, watermarks, flakiness, active failures, and protected artifacts are unchanged.
- [x] Repeated pruning is idempotent and transactional failure leaves the prior set intact.

## Task 6: Enforce workspace and machine cache limits

**Files:** `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`; `src/Miller.Testing/Daemon/CtGenerationPaths.cs`; generation/cache policy partials; cache maintenance tests.

**Build:** Reap protected generations first, then prune inactive cache entries older than seven days or until the 2 GiB workspace cap. Add one machine-global locked janitor under the Miller CT temp root to enforce 8 GiB oldest-unused-first, excluding live leases/processes. Use rename-before-delete and existing reap debt.

**Acceptance criteria:**

- [x] Workspace and machine caps are enforced, not report-only.
- [x] Active/newest-complete generations, live outputs, live leases, and non-Miller paths are never selected.
- [x] Selection order is deterministic and failed deletion remains recoverable debt.

## Task 7: Remove duplicate .NET runtime trees

**Files:** `src/Miller.Testing/Providers/Dotnet/DotnetTestProvider.cs`; focused provider tests and the real-provider Scale fixture.

**Build:** Keep one canonical runnable output tree per generation while preserving discovery, coverage/artifact paths, launchability, and result parsing. Compare the same fixture before and after by files, identities, total bytes, and test results.

**Acceptance criteria:**

- [x] The generation contains one canonical runnable runtime tree.
- [x] Fixed-fixture bytes materially decrease and test results remain identical.
- [x] Real provider execution passes as a Scale gate.

## Task 8: Batch shared Impact/Context graph reads

**Files:** `src/Miller.Indexing/Reads/QueryTimeResolutionReader.cs`; `src/Miller.Indexing/Resolution/RevisionFactCache.cs`; loader/observer seams as needed; Impact/Context/resolution tests.

**Build:** Reuse one query scratch per frontier, batch identifier-detail reads, and batch bounded version-slice misses. Add internal counters for resolve passes, detail commands/rows, and slice misses. Preserve evidence order/null semantics and full export/reference evidence.

**Acceptance criteria:**

- [x] Impact and Context rendered output is byte-identical on the fixed workload.
- [x] `--max-hops 0` performs zero frontier detail reads and slice loads.
- [x] Deterministic counters remove the measured 11k/429-style fan-out and warm ten-run p95 improves for both tools, or the audit retires a sub-finding with counter evidence.

## Task 9: Close the audit and verify the branch

**Files:** `docs/findings/2026-08-23-performance-audit.md`; design/plan checkboxes; Goldfish memories.

**Build:** Completed the identical after workloads, updated every finding to fixed with its commit SHA and evidence, and ran the affected gates, Release build, fast suite, Scale suite, live CT stop/restore, and related-worktree state checks.

**Closure evidence:** source `ceca0003`; Release build 0 warnings/0 errors; `scripts/test.sh all` fast `8,372 passed / 9 skipped / 0 failed` and Scale `162 passed / 16 skipped / 0 failed`; final foreground CT green at revision `40035` with `8,366` selected and stale `0`; final daemon state stopped with no provider and budget null; related worktrees reconciled with `main` clean at `28f680ac`, comparison worktree clean at `5cf2a52b`, and only this branch's planned documentation changes before this packet.

**Acceptance criteria:**

- [x] The findings ledger has zero deferred or open rows.
- [x] Every fixed row names a commit SHA and deterministic or fixed-workload evidence.
- [x] Release build is 0 warnings/0 errors; fast and Scale suites pass; live CT state is restored.
- [x] Branch and all related worktrees are reconciled and no task changes are stranded.
