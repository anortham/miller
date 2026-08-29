# Tool latency and health recovery — 2026-08-28

This finding records the recovery work from the [approved design](../plans/2026-08-28-tool-latency-and-health-design.md) and [implementation plan](../plans/2026-08-28-tool-latency-and-health-implementation-plan.md). MCP names, arguments, output shapes, freshness rules, and result semantics were kept unchanged.

## Baseline

The baseline was the 143,134-symbol workspace at extractor revision 56555.

- The first store-mode `edit replace_text` preview took 5,164 ms. Three identical warm previews took 16, 18, and 21 ms.
- Warm `context` calls took 13,634–18,473 ms and performed 2,706–3,122 symbol lookups. Lagging-sidecar calls took 40,977–64,913 ms, with lookup work consuming 94–97% of wall time.
- `impact changed_paths` measured p95/max 12,559/53,754 ms. `impact git_diff` measured p95/max 15,359/30,367 ms.
- Workspace health retained 1,499 lifetime errors while the established seven-day window contained 94.

## Recovered behavior

### Edit reads

Commit `82bf3e92` routes store-mode edits through one pinned workspace symbol-read context. Focused provider evidence proves zero full `MillerRepositoryIndex` materialization, and stale-span recovery resolves a fresh context after convergence. Legacy mode reuses its eager snapshot index without a duplicate projection.

### Lagging-sidecar reads

Commit `c55da95f` moves requested-path filtering inside the ordered CTE and preserves the sidecar row's `DocId` when a live row replaces it. Real SQLite coverage proves 501 unique paths use exactly two 500-path batches and duplicate paths do not increase the batch count. The focused reader and wrapper tests passed 17/17 and 1/1.

### Context hydration

Commit `a8687f32` adds wrapper-safe `ResolveMany` hydration. FTS reads 1,200 IDs in exactly `[500, 500, 200]` batches; measured, context-cache, and lagging wrappers retain their policies and telemetry. The exact MCP six-call replay used the resident `1.25.0+058199ca50f1` process against the recovered worktree state:

| sample | latency |
|---|---:|
| cold | 2,927 ms |
| warm | 2,817 / 2,767 / 2,760 / 2,787 / 2,754 ms |
| warm nearest-rank p95 / maximum | 2,817 / 2,817 ms |

This passes the 3,000 ms p95 and 5,000 ms warm maximum gates for the actual MCP path on this machine. The branch code's deterministic tests prove batching and wrapper behavior; a separate rebuilt-branch one-shot CLI replay was 4,330–4,390 ms warm and is report-only because it includes process startup. A branch-binary MCP host was not swapped into this live session, so the replay does not isolate the commit's contribution from the recovered sidecar/cache state.

### Impact graph reads

Commit `854deae4` increases the existing proof-frontier batch from 100 to 500 IDs. The deterministic high-frontier fixture's `FamilyResolution` statements fall from 40 to 20, with existing reachability, evidence, truncation, ordering, cancellation, and direction coverage remaining green.

The remaining real-workload hotspot is `UnresolvedNameForward`: 7 executions, 2,188 rows, and 8,754 ms in the phase measurement. The current stable resident MCP changed-path call is approximately 6.1 s. The rebuilt branch one-shot changed-path calls were 9.30, 9.35, 9.02, 9.31, 9.25, and 8.94 s; discarding the first gives a warm nearest-rank p95/max of 9.35/9.35 s, including startup. The 5,000 ms impact gate remains open. The post-rejection git-diff replay was not run.

The follow-up [impact read-path spike](2026-08-28-impact-read-path-spike.md) split the remaining work. Warm
query-time resolution spends a median 1,893 ms in identifier-within reads, 1,710 ms in pending-within reads,
and 2,858 ms in identifier-detail plus identifier/pending resolver loops. The named visibility arms total only
498 ms. The live `idx_read_manifest_entries_version(version_id,view_id,generation)` index is present and selected
by SQLite; an equivalent connection-local visibility projection is faster in isolation but cannot explain the
miss. The spike therefore rejects a reference sidecar as the next change and recommends a separate,
direction/consumer-aware read plan with lazy detail hydration. The 5,000 ms impact gate remains open.

The subsequent [stale-view cleanup](2026-08-28-miller-stale-view-cleanup.md) retired nine missing-root family views. On the same committed Task 2 git diff, the resident graph phase fell from 12,061 ms to 8,284–8,418 ms, about a 30% improvement. This confirms stale views were real overhead, but the 5,000 ms gate remains open.

## Health window

Commit `df00953d` makes both workspace-health `summary` and `outcomes` use `TelemetryHighlights.RecentWindowDays` (seven days). Lifetime outcome APIs remain available. The old 1,499-vs-94 mismatch is no longer presented as current health, and compact/JSON shapes remain unchanged.

## Focused verification

The task reports record these focused results on the committed branch:

- Task 1: `SqliteSymbolReaderTests` 17/17 and `LaggingSidecarSymbolLookupTests` 1/1; Indexing, Server, and Tests Release builds had 0 warnings and 0 errors.
- Task 2: `EditToolTests` 182/182, `WorkspaceIndexProviderTests` 115/115, and non-Scale `QmlToolEvidenceTests` 4/4; Server and Tests Release builds had 0 warnings and 0 errors. `LiveEditTests` remained Scale-only.
- Task 3: `TelemetrySummaryTests` 34/34 and `WorkspaceToolTests` 103/103; Server and Tests Release builds had 0 warnings and 0 errors.
- Task 4: the focused FTS, context-retrieval, search-tool, provider, and lagging-wrapper set passed 403/403; FTS plus hardening tests passed 75/75; the solution Release build had 0 warnings and 0 errors.
- Task 5: `SqliteSymbolGraphIndexTests` passed 28/28; Indexing and Tests Release builds had 0 warnings and 0 errors.

## Rejected experiments

These candidates were measured and reverted after failing to improve the fixed workload:

- The outer 1,000-ID graph batch correction showed no real-workload win.
- An ordered multi-name CTE took 38.82 s.
- A no-order multi-name CTE took 10.43 s.
- A per-name no-temporary-sort correction took 9.51 s.

The name-repeat distribution experiment exceeded its 60-second diagnostic cap before producing reliable counts, so no cache recommendation was made. No global cache, timeout, result cut, or speculative query rewrite remains.

## Commits

The recovery commits are `df00953d` (health window), `82bf3e92` (edit reads), `c55da95f` (lagging-sidecar reads), `854deae4` (graph proof batching), and `a8687f32` (context hydration). The implementation-plan and design commits are `b40d2fe6` and `20a0606a`.

## Branch verification

- Fast suite: 9,188 passed, 9 skipped, 0 failed.
- Scale suite: 203 passed, 18 skipped, 0 failed.
- Release build: 0 warnings, 0 errors.
- Secrets scan: 2,073 commits and 892.46 MB scanned; no leaks found.
- Dependency audit: no vulnerable direct or transitive packages in any solution project.
- Both repository worktrees were clean before this documentation packet.

## Open gates

- The fixed impact changed-path and git-diff p95 gate is not met.
- The overall replay gate remains open because impact misses; the fast, Scale, build, secrets, dependency, and worktree gates pass.
- The recovery evidence is complete for the proven edit, lagging-sidecar, context, graph-count, and seven-day-health contracts, with the impact latency miss disclosed.

At documentation capture, the task worktree was `/home/murphy/source/miller/.worktrees/tool-latency-health`, branch `fix/tool-latency-health`, at `a8687f32`; the branch was clean before this uncommitted documentation packet was created.
