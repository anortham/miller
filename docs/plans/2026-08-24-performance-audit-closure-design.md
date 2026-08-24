# Performance Audit Closure Design

**Status:** Implemented and verified on 2026-08-24 at `ceca0003`.

## Goal

Close every deferred or open item in `docs/findings/2026-08-23-performance-audit.md` without first merging the partial hardening branch to `main`. A finding closes only when the branch contains the repair and an identical before/after measurement or deterministic operation-count guard proves it, or when measurement conclusively retires the finding.

## Branch and delivery strategy

Continue in `/home/murphy/source/miller/.worktrees/perf-ct-audit-2026-08-23` on `perf/ct-audit-2026-08-23`. Each slice remains buildable, focused-test green, locally committed, and independently revertible. `main` stays untouched until the audit ledger has zero deferred/open entries and the final Release, fast, Scale, live CT, performance, and worktree gates all pass.

Rejected alternatives:

- Merging the first hardening slice to `main` now would intentionally land known open findings and duplicate integration gates.
- A child branch stacked on the performance branch would preserve the first slice but add reconciliation and measurement ambiguity without enabling an independent release.
- One monolithic “fix everything” commit would make performance attribution, rollback, and review unreliable.

## Findings covered

| Finding | Closure slice |
| --- | --- |
| Poller opens a full family read session/projection every 250 ms | Cursor probe and convergence |
| Startup/cursor drift does not empty-delta converge freshness | Cursor probe and convergence |
| Selection reads all cases/statuses once per enabled project | Active-project selection |
| Retry-attempt keys grow across revisions | Active-project selection |
| Disabled discovery pseudo-cases keep status partial | Active-project selection |
| Run completion performs `2 + 4R` operations and history sorts | Completion/history indexing |
| CT run/result/artifact history is unbounded | History retention |
| CT generation/cache storage grows without an enforcing janitor | Build-cache/output retention |
| Impact over-hydrates identifier details and bounded slices | Shared graph-read batching |
| Context repeats the same graph-resolution work | Shared graph-read batching |

## Approved storage policy

- Preserve every active/running run.
- Preserve all run/result/artifact history younger than 30 days.
- Preserve at least the newest 50 outcomes per test case even when older than 30 days.
- Preserve artifacts referenced by retained runs and the newest artifact per enabled project.
- Prune inactive build caches after seven days.
- Enforce a 2 GiB per-workspace CT build-root cap and an 8 GiB machine-wide CT build-root cap.
- Never delete an active generation, the newest complete generation, a live provider output, or a cache protected by a live CT lease.
- Use deterministic oldest-unused-first selection, recoverable rename-before-delete, and the existing reap-debt pattern.
- Automatic history pruning bounds logical rows. Physical SQLite compaction is report-only unless a nonblocking existing maintenance primitive can prove it is safe; no foreground `VACUUM` is introduced.

## Architecture

### 1. Cheap cursor probe and startup convergence

`MillerArtifactRevisionSource` must not open `WorkspaceReadSessionFactory.Open` on an unchanged 250 ms tick. Extend the existing freshness probe so it returns the stable family/artifact identity and revision needed to construct the same `CtFreshnessKey` as a full session. The probe reads the versioned freshness contract and does not create compatibility temp tables or hydrate manifest/symbol projections.

When the probe reports the same cursor, the poll returns status-only with zero full-session opens. When it advances, the impact path opens a bounded full session, verifies that its cursor still matches the probe, and retries a bounded number of times on drift. It never retains a compatibility projection across generations.

Persist the last successfully reconciled cursor per workspace in `ct.db`. On daemon startup, reconcile that cursor to the live cursor instead of arming from the live cursor and forgetting the interval. A complete empty interval advances green watermarks without running providers. A changed interval selects impacted tests. A changed identity or unavailable history remains fail-closed and queues the existing honest full-selection path; it never reports stale rows green without evidence.

### 2. Active-project selection, retries, and discovery status

Normalize the CT project association on `test_cases` instead of repeatedly decoding `metadata.ct_project_path`. Add an internal nullable project key/path column and indexes through the existing `CtSchema` migration path. Provider discovery and discovery-failure pseudo-cases populate it; migration backfills existing rows from their metadata.

Add project-filtered store reads for provider-managed cases and their statuses. One selection snapshot is built per `(workspace, index identity, revision)` and reused across enabled-project fan-out. Unknown/unmapped cases remain fail-closed. Selected IDs, reasons, evidence, and whole-suite eligibility remain byte-for-byte compatible.

Move “retry already spent” state onto the pending/in-flight run or evict it on terminal completion. Retry memory becomes proportional to live queued/in-flight work rather than process lifetime, while preserving one retry per test case and revision.

Active verdict/status queries exclude lifecycle rows belonging to disabled projects but retain their historical rows. Enabled-project discovery failures remain visible and continue to block a green verdict. Disable/re-enable therefore changes active projection, not historical storage.

### 3. Completion batching and indexed history

Materialize the effective result observation time on `test_results` and index `(workspace_id, test_case_id, observed_at DESC, id DESC)`. Migration backfills it from the owning run’s end/start timestamp with the current deterministic epoch fallback.

Run completion writes results and freshness state in the existing transaction, then reads recent outcomes for all distinct affected test cases in one bounded window query. It computes the unchanged 50-outcome flakiness policy and applies score updates in a prepared/batched path. The production history query must use the new index and produce no temporary order-by B-tree.

The hard performance target is to remove `R` recent-history queries/sorts and reduce statement count from `2 + 4R` to at most `C + 2R`, where `C` is a small fixed constant asserted by a deterministic observer. Status normalization, worst-wins folding, stale/fresh ordering, tie-breaks, and transaction rollback remain unchanged.

### 4. CT history retention

Add one internal transactional `PruneContinuousTestHistory` operation and invoke it from the existing coordinator maintenance tail. It computes the retained run/result/artifact set using the approved 30-day/50-outcome policy, protects active runs, deletes children before parents where cascades are not sufficient, and is deterministic and idempotent per workspace.

Retention must not change current `ct_test_states`, current freshness/watermarks, flakiness computed from the retained newest 50 outcomes, active failure visibility, or another workspace’s rows. Maintenance records rows considered/deleted and page/freelist facts for evidence; deletion success is not misreported as file-size reclamation.

### 5. Build-cache and generation retention

Turn the existing generation disk budget from report-only into an enforcing maintenance policy. Per-workspace maintenance first reaps superseded generations using the existing active/newest-complete protections, then prunes inactive cache entries older than seven days or until the 2 GiB cap is met.

A machine-global local janitor coordinates through one lock under the existing Miller CT temp root. It discovers Miller-owned CT build roots, excludes any root with a live lease/provider process, and prunes oldest inactive candidates until total usage is at or below 8 GiB. It never adds an MCP tool or absorbs fleet semantic orchestration.

The .NET provider receives a separate output-layout repair: intermediate artifacts and the canonical runnable output must not duplicate the same runtime tree inside one generation. A controlled real-provider fixture records paths, file identities, bytes, and launchability before the production change. The final layout keeps one canonical runnable tree, preserves coverage/artifact discovery, and proves a materially smaller generation with the same test results. Cache eviction and output-layout changes remain separate commits so their effects are attributable.

### 6. Shared Impact/Context graph-read batching

Impact and Context share the same structural cost in `QueryTimeResolutionReader`: resolution and unresolved-name arms independently build query scratch state, read identifier details one site at a time, and cause bounded version slices to load piecemeal.

Add internal read counters for resolve passes, identifier-detail SQL commands/rows, and actual bounded-slice misses. Reuse one query scratch object per graph frontier, batch identifier-detail reads while preserving input order/null semantics, and batch only the bounded version slices requested by that frontier. A graph-only evidence mode may omit site-span hydration only when rendered Impact/Context output remains byte-identical; export/reference evidence paths keep the full shape.

Default `max-hops`, `ReachCap`, ranking, truncation, and public output do not change. `--max-hops 0` must perform zero frontier detail reads/slice loads. Identical warm ten-run p95 workloads and deterministic counter guards prove both tools independently.

## Architecture Quality

**Affected modules:** `Miller.Testing` poller/queue/store/provider lifecycle; `Miller.Indexing` workspace probing, query-time resolution, bounded fact cache; server status/read callers.

**Caller-facing interfaces:** Existing CLI/MCP JSON, provider contracts, test-selection output, detailed status APIs, and graph rendering stay unchanged. New seams are internal: cursor checkpoint/probe, project-filtered store reads, retention policies, generation janitor policy, and graph read counters/batches.

**Depth/locality check:** Cursor correctness stays behind the revision source/poller boundary; active-project knowledge stays in the store/selection boundary; retention stays in maintenance policy; query batching stays behind graph reads. Callers do not learn schema, pruning, batching, or retry bookkeeping details.

**Test surface:** Existing caller-facing poller/queue/status/store/provider/Impact/Context interfaces plus deterministic observer and query-plan evidence. No source-text-only assertions are used when behavior can be exercised.

**Seams/adapters:** The existing probe, store partials, coordinator maintenance tail, generation reap policy, graph observer, and bounded fact cache are reused. No new MCP tool, public diagnostics endpoint, or cross-workspace semantic service is added.

**Rejected shortcuts:** Raising intervals/caps without removing work; retaining a live compatibility session; defaulting Context to fewer hops; deleting all history; filtering stale pseudo-cases only in one renderer; a destructive global cache sweep; wall-clock-only guards.

**Architecture risk:** High. Cursor and freshness changes are fail-safe correctness paths; schema migrations and retention delete data; output-layout changes launch real providers; graph batching must preserve byte-identical answers.

## Error handling and safety

- Cursor probe uncertainty, identity mismatch, truncated delta, and moving generations remain unavailable/partial, never green by assumption.
- Schema migration is transactional, versioned, backfilled, and readable by the current branch before any pruning is enabled.
- Retention and cache selection support dry policy evaluation in tests, protect live leases/runs, rename before delete, and record recoverable debt.
- A failed cache/output/layout repair leaves the last complete generation runnable.
- Graph batching falls back to the existing bounded read semantics on missing evidence; it never silently switches a one-shot bounded caller to whole-generation loading.

## Measurement and verification

- Performance changes use a recorded before workload, identical after workload, and deterministic operation/allocation/query-plan guards. Wall time and RSS are report-only unless direct process/operation identity makes them stable.
- CT focused development uses only owned test classes. Real-provider and live-daemon measurements are Scale/lead gates.
- The branch gate is `scripts/test.sh`, `scripts/test.sh scale`, Release build with zero warnings/errors, `git diff --check`, live CT stop/restore, and related-worktree reconciliation.
- The audit ledger closes only when every table row above has a fixed SHA and evidence; no item remains “deferred” under a different name.

## Acceptance criteria

- [x] Unchanged CT ticks open zero full compatibility projections; startup cursor drift converges without a recovery run.
- [x] Selection reads are project-filtered/once-per-revision, retry state is bounded, and disabled lifecycle rows do not affect active verdicts.
- [x] Completion has no per-result history query/temp sort and meets the statement-count bound.
- [x] History and build storage obey the approved retention/cap policies without deleting protected state.
- [x] .NET generations contain one canonical runnable runtime tree and are materially smaller on the fixed fixture.
- [x] Impact and Context outputs remain byte-identical while detail-query/slice counts and p95 improve for both tools.
- [x] The final audit contains zero deferred/open findings and all project verification gates pass.
