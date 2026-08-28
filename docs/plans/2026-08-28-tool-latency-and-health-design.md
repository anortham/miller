# Tool latency and health-window design

**Status:** approved direction, pending written-spec review

## Goal

Remove multi-second edit cold starts, bring ordinary `context` calls below an interactive latency bound,
reduce large `impact` tails without weakening evidence, and make workspace health describe the same recent
telemetry window as workspace status.

The MCP tool names, arguments, result shapes, freshness rules, and compact/JSON compatibility remain unchanged.

## Measured baseline

Measurements use the 143,134-symbol Miller workspace at revision 56555.

- First `edit replace_text` preview: 5,164 ms. Three identical warm previews: 16, 18, and 21 ms.
- Warm `context` usage calls: 13,634 to 18,473 ms, with 2,706 to 3,122 symbol lookups per call.
- Lagging-sidecar `context` calls: 40,977 to 64,913 ms, with lookup work consuming 94% to 97% of wall time.
- `impact changed_paths`: p95 12,559 ms, maximum 53,754 ms.
- `impact git_diff`: p95 15,359 ms, maximum 30,367 ms.
- Workspace health: 1,499 retained errors labeled recent; the existing seven-day status window contains 94.

## Architecture quality

**Affected modules:** `Miller.Server.Tools`, `Miller.Server.Workspaces`, `Miller.Indexing` SQLite symbol and graph
readers, and telemetry health aggregation.

**Caller-facing interface:** unchanged MCP `edit`, `context`, `impact`, and `workspace health` contracts.
Performance and health accuracy improve behind their existing interfaces.

**Depth/locality check:** edit routing belongs in the existing workspace symbol-read provider; context batching
belongs inside the FTS implementation; graph query reuse belongs inside one `ReachWithEvidence` call; time-window
policy belongs in telemetry aggregation. Callers do not learn storage details.

**Test surface:** tool outputs and existing provider interfaces, plus deterministic SQL statement, batch, and
materialization counts. Wall-clock thresholds are release measurements, not CI assertions.

**Seams/adapters:** reuse `IWorkspaceSymbolReadProvider`, add one batch-resolve member with a default fallback to
the existing `ISymbolLookupIndex`, and reuse `ISymbolGraphReachability` plus existing SQLite telemetry hooks. No
new lookup interface or MCP tool is introduced.

**Rejected shortcuts:** unconditional repository prewarming, a global cache, arbitrary timeouts, wall-clock CI
tests, early impact termination, or reducing context result quality before batching is measured.

**Architecture risk:** medium-high. Edit changes its read provider at the MCP/DI boundary; graph evidence reuse
must preserve reachability, truncation, and evidence bytes.

## Design

### 1. Edit reads the pinned symbol projection

`EditTool` will resolve the current workspace through `IWorkspaceSymbolReadProvider` with
`WorkspaceRefreshMode.None`. It will construct `SmartTargetResolver` from the returned `ISymbolLookupIndex` and
pass the lookup plus its pinned `IWorkspaceReadSession` to `EditService`.

`EditService` will depend on `ISymbolLookupIndex`, not `MillerRepositoryIndex`. Its operations already require
symbol definitions, indexed edit spans, reference evidence, and source text rather than the full dependency graph.
Rename coverage will read the pinned session's index level through the existing string-based guard.

Store mode will avoid lazy full graph materialization. Legacy `MILLER_INDEX_STORE=off` already holds an eager
repository index, so it will reuse `legacySnapshot.Index` through `ISymbolLookupIndex` rather than loading a second
symbol projection.

This removes the first-edit load of hundreds of thousands of reference, identifier, relationship, and graph rows.
Freshness remains pinned to the workspace read session, and apply still performs the existing write-through and
post-apply convergence. Store sessions snapshot manifest entries, so stale-span recovery must resolve a new
`WorkspaceSymbolReadContext` after convergence before retrying; it must not poll or retry against the original
pinned session.

### 2. Lagging-sidecar validation filters before ordering

`SqliteSymbolReader.ReadForPaths` will move the requested path predicate inside its ordered CTE. The query will
rank only symbols from requested paths rather than ranking every named symbol before filtering.

The live row will retain the sidecar row's original `DocId`. This prevents path batches from assigning colliding
local ordinals that would change relaxed-search de-duplication and reranker tie-breaking. Ordering, role/currency
evidence, missing-path behavior, duplicate suppression, and the 500-parameter batch bound otherwise remain
unchanged. `ReadForSymbolIds` is the reference query shape.

`LaggingSidecarSymbolLookup` keeps its request-scoped path cache. If the resulting count guard still shows repeated
hydration, the implementation may reuse the existing filtered symbol-ID reader internally; it must not add a
global cache or public interface.

### 3. Context batch-hydrates FTS results

`ContextQueryRetrieval` remains the one request-local retrieval session and keeps its exact
`(query, limit, excludeTests)` cache key. Distinct ranking windows remain distinct.

`ISymbolLookupIndex` will add a document-ID batch-resolve member with a default per-document fallback.
`FtsSymbolSearchIndex` will implement bounded hydration, and every production wrapper will forward the call:
`MeasuredSymbolLookupIndex` records the same resolve telemetry, `LaggingSidecarSymbolLookup` applies live-row
verification to every returned row, and `ContextSearchCacheLookupIndex` passes through without bypassing the
wrapped policy.

`SearchTool.CollectSymbolCandidates` will collect document IDs, batch-resolve them in groups of at most 500, then
preserve the existing visibility, relaxation, scoring, and output order. Fetch escalation and relaxation are
separate passes; count guards apply per window and per relaxation pass rather than assuming one hydration pass per
tool call.

No connection-lifetime abstraction is added in this slice. If batch hydration meets the statement-count guard but
misses the latency target, a later measured step may reuse one FTS read connection for the request.

### 4. Impact reuses graph evidence inside one traversal

The first implementation step records `GraphQueryTelemetry` by phase on the fixed workloads. Traversal stops at
`maxDepth`, proof reads the unexpanded max-depth frontier, enrichment may read the opposite direction, and an
existing 4,000-entry evidence cache already covers some reuse. A generic request context cannot be assumed to save
those queries.

The implementation will remove the largest measured repeated statement family while preserving phase semantics.
The first explicit candidate is batching supplemental endpoint `SymbolExists` checks, which currently issue one
statement per endpoint. Proof queries remain early-exit and partial results cannot satisfy completeness. If another
phase owns the fixed-workload count, the plan must name that phase and its count before substituting the fix.

The change preserves reached counts, candidate limits, centrality, visibility, truncation, deterministic ordering,
evidence fields, and `GraphStatementPhase` attribution. It will not stop traversal early based on the output limit.

### 5. Workspace health uses the seven-day outcome window

The lifetime `SummarizeOutcomes` APIs remain available. Workspace health will use both `SummarizeRecent` and a
windowed outcome aggregate with `TelemetryHighlights.RecentWindowDays` so its adjacent `summary` and `outcomes`
objects apply the same seven-day policy as status.

Existing compact and JSON shapes remain unchanged. Counts and warning presence may change because old failures no
longer degrade current health. CLI one-shot health continues to omit resident telemetry.

## Performance acceptance

Use fixed workloads on the same workspace and revision, discard the first run, then record at least five warm runs.

- Standard 3,000-token `context` workload: p95 at or below 3,000 ms and no warm call above 5,000 ms.
- Fixed large `impact changed_paths` and `impact git_diff` workloads: p95 at or below 5,000 ms.
- First store-mode edit preview after bootstrap and after a freshness generation swap: no full repository
  materialization; preview latency is measured but guarded primarily by a zero-materialization assertion.
- Legacy edit preview reuses its already-eager repository index and does not build a duplicate symbol projection.
- No result-count, ordering, evidence, truncation, or diagnostic regressions.

If batching passes deterministic count guards but misses a target, the task remains incomplete. The next change
must remove the largest measured phase; it may not hide the miss with a larger limit, a timeout, or a weaker result.

## Test strategy

### Edit

- Store-mode tool tests inject a holder factory that fails if materialized and exercise every edit operation.
- Legacy provider tests prove current symbol reads reuse the eager repository index without building a projection.
- Store stale-span recovery converges, resolves a fresh read context, and succeeds without exhausting the old
  session's retry budget.
- Existing preview/apply, stale-span, rename-coverage, and post-apply convergence tests remain byte-compatible.

### Symbol and context reads

- `ReadForPaths` result and evidence parity against the general reader, including selected and missing paths.
- A 501-path input performs exactly two 500-row reader batches; duplicate paths do not increase the count.
- Live rows retain sidecar `DocId` values across multiple path batches; relaxed merge cannot drop a collision.
- FTS hydration batch counts are asserted per fetch-escalation window and relaxation pass. A production wrapper-chain
  test proves bounded statements, live-row verification, and resolve telemetry remain active.
- Existing exact-key context retrieval memo tests remain green.
- Tool-level context candidates and rendered output remain unchanged.

### Impact

- Fixed workloads record statement counts per `GraphStatementPhase` before implementation.
- Supplemental endpoint existence checks are batched when they are the measured dominant repeated family.
- Wide-frontier tests assert bounded batches per phase and direction without changing phase attribution.
- Existing result, evidence, cancellation, truncation, and query-family tests remain green.

### Health

- A 20-day-old error plus current outcomes proves both windowed aggregates exclude the old error. The raw-test insert
  helper accepts an outcome so the fixture can create an old error rather than only an old success.
- Compact and JSON workspace health omit the telemetry warning when only old errors remain.
- Existing lifetime outcome grouping remains unchanged.

## Implementation order

1. Add red count/result guards and fixed benchmark commands.
2. Fix `ReadForPaths` filtering and verify context/impact tail workloads.
3. Route edit through the pinned symbol projection, including legacy mode.
4. Add wrapper-safe batched FTS hydration and remeasure context.
5. Record graph statements by phase, remove the largest repeated family, and remeasure impact.
6. Switch both workspace-health telemetry aggregates to the seven-day window.
7. Run focused tests after each task, then the fast suite and release build once at the branch gate.

## Security scope

- `security-secrets`: repository-standard whole-tree secrets scan at the branch gate.
- `security-deps`: repository-standard NuGet vulnerability audit at the branch gate.
- No new dependencies, network calls, routes, authorization decisions, or sensitive telemetry fields.

## Acceptance criteria

- [ ] Every store-mode edit operation avoids full `MillerRepositoryIndex` materialization; legacy mode reuses its
      eager index without a duplicate projection.
- [ ] Store stale-span recovery reopens its pinned symbol read after convergence.
- [ ] Lagging-sidecar path validation filters before ordering, retains sidecar `DocId`, and respects batch limits.
- [ ] Context FTS hits hydrate in bounded batches with byte-identical candidate ordering and diagnostics.
- [ ] Production lookup wrappers preserve live-row validation and resolve telemetry during batch hydration.
- [ ] Standard context workload meets the 3-second p95 and 5-second maximum warm targets.
- [ ] Impact removes the largest measured repeated graph statement family without changing phase attribution.
- [ ] Fixed changed-path and git-diff impact workloads meet the 5-second p95 target.
- [ ] Workspace health summary and outcomes use the same seven-day error window as status.
- [ ] Focused tests, fast suite, and release build pass with zero warnings and errors.
