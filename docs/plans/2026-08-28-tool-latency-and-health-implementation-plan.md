# Tool Latency and Health Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Remove the measured edit, context, and impact latency causes while making workspace health use the established seven-day telemetry window.

**Architecture:** Store-mode edit reads move from the full repository holder to the pinned workspace symbol provider. Symbol lookup gains one wrapper-safe batch-resolve operation, lagging-sidecar validation keeps stable sidecar document identities, impact removes the largest measured repeated graph statement family, and health windows both adjacent telemetry aggregates consistently.

**Tech Stack:** .NET 10, C#, Microsoft.Data.Sqlite, xUnit, Miller MCP and telemetry ledger.

**Architecture Quality:** Existing workspace, lookup, graph, and telemetry modules remain the caller-facing seams. MCP contracts stay unchanged. Risk is medium-high because symbol identity, wrapper policy, stale-session recovery, and graph evidence completeness are load-bearing.

## Global Constraints

- MCP tool names, arguments, compact output, JSON shape, freshness rules, and diagnostic semantics remain unchanged.
- `Miller.Core` stays pure with zero I/O dependencies.
- Store-mode edits must not materialize `MillerRepositoryIndex`; legacy mode reuses its already-eager index and must not build a duplicate projection.
- Store stale-span recovery resolves a new `WorkspaceSymbolReadContext` after convergence.
- Live lagging-sidecar rows retain the sidecar row's original `DocId`.
- Batch symbol resolution is part of `ISymbolLookupIndex`; all production wrappers preserve live-row validation and resolve telemetry.
- Context keeps exact `(query, limit, excludeTests)` memo keys. Fetch escalation and relaxation remain separate passes.
- Impact optimization follows measured `GraphStatementPhase` counts and preserves reachability, evidence, truncation, ordering, and phase attribution.
- Workspace health `summary` and `outcomes` both use `TelemetryHighlights.RecentWindowDays`.
- No new MCP tool, dependency, network call, global cache, startup prewarm, timeout, or result-quality reduction.
- Tests contain no comments. Production changes add no narration comments.
- TDD is mandatory: each behavior test must fail for the expected missing behavior before production code changes.

## Verification Strategy

**Project source of truth:** `AGENTS.md` testing and build sections.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<AssignedTestClass>"` from the task worktree. Multiple assigned classes run as separate focused commands.

**Worker ceiling:** Assigned focused test classes plus `dotnet build <directly changed project> -c Release --no-restore`. Workers do not run the bare fast suite, Scale suite, security scans, or release build.

**Worker gate invariant:** Each focused test proves the task's result, identity, batching, freshness, or telemetry-window contract; each direct project build proves the owned production/test files compile with zero warnings.

**Lead affected-change scope:** After each parallel batch, run the union of touched focused classes and `dotnet build Miller.slnx -c Release --no-restore` once.

**Branch gate:** Bare `dotnet test` once, then `dotnet build Miller.slnx -c Release --no-restore` once on the final source tree.

**Security scope:** `gitleaks detect` for `security-secrets`; `dotnet list Miller.slnx package --vulnerable --include-transitive` for `security-deps`. Any secret or critical/high dependency finding blocks handoff.

**Replay/metric evidence:** Hard gates are output parity, deterministic statement/batch/materialization counts, context warm p95 at or below 3,000 ms with no warm call above 5,000 ms, and changed-path/git-diff impact warm p95 at or below 5,000 ms. Mean, cold-run latency, token count, and maximum outside the fixed warm sample are report-only.

**Escalation triggers:** Because `Miller.Indexing` changes, run `scripts/test.sh scale` at the branch gate. Any extractor or CT-provider change would require its specialist scope, but neither is planned.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the task explicitly owns the failing contract.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp under this plan's `.razorback/sdd` workspace. Reuse only same-HEAD passing evidence.

## Fixed Performance Replays

Use workspace `/home/murphy/source/miller/.worktrees/tool-latency-health` after its index and sidecars are current. Run each exact MCP request six times sequentially, discard the first call, and compute nearest-rank p95 from the five warm telemetry rows.

- Context: `context(query="trace the workspace symbol read path from WorkspaceIndexProvider through lagging sidecar validation and batched FTS hydration", token_budget=3000, reference_mode="usage", reference_depth=1, exclude_tests=false, format="json", ensure_fresh=false)`.
- Impact changed paths: `impact(changed_paths=["src/Miller.Server/Tools/ContextTool.cs","src/Miller.Server/Tools/SearchTool.cs","src/Miller.Server/Workspaces/LaggingSidecarSymbolLookup.cs","src/Miller.Indexing/FtsSymbolSearchIndex.cs","src/Miller.Indexing/SqliteSymbolGraphIndex.cs"], limit=200, max_depth=2, format="json", ensure_fresh=false)`.
- Impact git diff: `impact(git=true, base="20a0606a", limit=200, max_depth=2, format="json", ensure_fresh=false)`.
- Edit: first and three repeated dry-run previews for a known `replace_text` target. Store-mode telemetry must show no full repository materialization; warm latency is report-only after the zero-materialization guard passes.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Lagging-sidecar path reads | Batch A | `src/Miller.Indexing/SqliteSymbolReader.cs`; `src/Miller.Server/Workspaces/LaggingSidecarSymbolLookup.cs`; `tests/Miller.Tests/Indexing/SqliteSymbolReaderTests.cs`; create `tests/Miller.Tests/Server/LaggingSidecarSymbolLookupTests.cs` | No | None - safe parallel batch. |
| Task 2: Lightweight edit reads | Batch A | `src/Miller.Server/Tools/EditTool.cs`; `src/Miller.Server/Tools/EditService.cs`; `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`; `tests/Miller.Tests/Server/EditToolTests.cs`; `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`; `tests/Miller.Tests/Server/LiveEditTests.cs`; `tests/Miller.Tests/Server/QmlToolEvidenceTests.cs` | No | None - safe parallel batch. |
| Task 3: Seven-day workspace health | Batch A | `src/Miller.Server/Telemetry/TelemetryLedger.cs`; `src/Miller.Server/Tools/WorkspaceTool.cs`; `tests/Miller.Tests/Server/TelemetrySummaryTests.cs`; `tests/Miller.Tests/Server/WorkspaceToolTests.cs` | No | None - safe parallel batch. |
| Task 4: Wrapper-safe context batching | Batch B | `src/Miller.Indexing/ISymbolLookupIndex.cs`; `src/Miller.Indexing/FtsSymbolSearchIndex.cs`; `src/Miller.Indexing/MillerRepositoryIndex.cs`; `src/Miller.Indexing/SymbolSearchProjection.cs`; `src/Miller.Server/Workspaces/ReadPhaseTelemetry.cs`; `src/Miller.Server/Workspaces/ContextSearchCacheLookupIndex.cs`; `src/Miller.Server/Workspaces/LaggingSidecarSymbolLookup.cs`; `src/Miller.Server/Tools/SearchTool.cs`; `tests/Miller.Tests/Indexing/FtsSymbolSearchIndexTests.cs`; `tests/Miller.Tests/Server/ContextQueryRetrievalTests.cs`; `tests/Miller.Tests/Server/SearchToolTests.cs`; `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs` | Yes | Depends on Task 1's stable live-row `DocId` and path-cache behavior; shares `LaggingSidecarSymbolLookup.cs`. |
| Task 5: Measured impact graph reduction | Batch B | `src/Miller.Indexing/SqliteSymbolGraphIndex.cs`; `tests/Miller.Tests/Indexing/SqliteSymbolGraphIndexTests.cs` | No | None - safe parallel batch after Batch A. |

Batch A uses `parallel-lead-commit`. After Task 1 is reviewed and committed, Tasks 4 and 5 dispatch together in Batch B and both use `parallel-lead-commit`. The lead stages exact owned paths only.

### Task 1: Lagging-sidecar path reads

**Files:**
- Modify: `src/Miller.Indexing/SqliteSymbolReader.cs`
- Modify: `src/Miller.Server/Workspaces/LaggingSidecarSymbolLookup.cs`
- Test: `tests/Miller.Tests/Indexing/SqliteSymbolReaderTests.cs`
- Create: `tests/Miller.Tests/Server/LaggingSidecarSymbolLookupTests.cs`

**Interfaces:**
- Consumes: `SqliteSymbolReader.ReadForPaths`, `IndexedSymbol.DocId`, existing 500-parameter batching, `LaggingSidecarSymbolLookup.LiveRow`.
- Produces: filtered-before-ordering path reads whose live wrapper preserves the sidecar `DocId`.

**Contract inputs:** `ReadForSymbolIds` filtered-CTE shape; relaxed search de-duplicates and reranks by `DocId`.

**File ownership:** `src/Miller.Indexing/SqliteSymbolReader.cs`; `src/Miller.Server/Workspaces/LaggingSidecarSymbolLookup.cs`; `tests/Miller.Tests/Indexing/SqliteSymbolReaderTests.cs`; create `tests/Miller.Tests/Server/LaggingSidecarSymbolLookupTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Move requested-path filtering inside the ordered CTE so only selected paths are ranked. When a live row replaces a lagging sidecar row, copy the sidecar `DocId` onto the live row.

**Approach:** Preserve result ordering and evidence joins. Cover selected/missing paths, 501-path batching, duplicate paths, cross-batch identity, and relaxed-merge survival through real lookup behavior.

**Acceptance criteria:**
- [ ] The red tests fail because the query ranks the whole table and live rows lose sidecar identity.
- [ ] `ReadForPaths` filters before `ROW_NUMBER` ordering and preserves existing evidence/result parity.
- [ ] 501 unique paths use two batches; duplicates do not increase batch count.
- [ ] Live rows retain sidecar `DocId` across batches and cannot collide during relaxed merge.
- [ ] Focused `SqliteSymbolReaderTests` and `LaggingSidecarSymbolLookupTests` pass; direct projects build cleanly.
- [ ] Worker hands the verified diff to the lead without committing.

### Task 2: Lightweight edit reads

**Files:**
- Modify: `src/Miller.Server/Tools/EditTool.cs`
- Modify: `src/Miller.Server/Tools/EditService.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Test: `tests/Miller.Tests/Server/EditToolTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`
- Test as needed: `tests/Miller.Tests/Server/LiveEditTests.cs`
- Test as needed: `tests/Miller.Tests/Server/QmlToolEvidenceTests.cs`

**Interfaces:**
- Consumes: `IWorkspaceSymbolReadProvider.ResolveSymbolRead`, `WorkspaceSymbolReadContext`, `ISymbolLookupIndex`, `IWorkspaceReadSession`, `IndexLevelGuard.ReferenceLayerConverging(string)`.
- Produces: `EditService` over a pinned symbol lookup/session and a retry callback that resolves a fresh context after store convergence.

**Contract inputs:** All edit operations retain current output, target resolution, span, reference-evidence, stale diagnostics, apply, and write-through behavior.

**File ownership:** `src/Miller.Server/Tools/EditTool.cs`; `src/Miller.Server/Tools/EditService.cs`; `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`; `tests/Miller.Tests/Server/EditToolTests.cs`; `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`; `tests/Miller.Tests/Server/LiveEditTests.cs`; `tests/Miller.Tests/Server/QmlToolEvidenceTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Inject the existing workspace symbol provider into `EditTool`, change `EditService` to consume `ISymbolLookupIndex` plus the pinned session, and reopen the context after stale-span convergence. Store mode never touches the lazy repository holder; legacy mode reuses the eager holder index without loading a second projection.

**Approach:** Test every public edit operation through `EditTool` with a store holder factory that throws if materialized. Add a real store-session recovery fixture whose manifest changes after convergence, proving the retry sees new spans. Preserve provider telemetry and write-through behavior.

**Acceptance criteria:**
- [ ] Red tool tests prove current store edits materialize the holder and stale retry cannot observe a converged store session.
- [ ] Every store-mode edit operation succeeds without materializing `MillerRepositoryIndex`.
- [ ] Legacy mode reuses `legacySnapshot.Index` and builds no duplicate symbol projection.
- [ ] Store stale recovery resolves a fresh `WorkspaceSymbolReadContext` before retrying.
- [ ] Existing preview/apply, rename coverage, QML span, partial-apply, and convergence behaviors remain green.
- [ ] Focused edit, provider, live-edit, and QML tests pass; `Miller.Server` and `Miller.Tests` build cleanly.
- [ ] Worker hands the verified diff to the lead without committing.

### Task 3: Seven-day workspace health

**Files:**
- Modify: `src/Miller.Server/Telemetry/TelemetryLedger.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs`
- Test: `tests/Miller.Tests/Server/TelemetrySummaryTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceToolTests.cs`

**Interfaces:**
- Consumes: `TelemetryHighlights.RecentWindowDays`, `TelemetryLedger.SummarizeRecent`, existing lifetime outcome APIs.
- Produces: a windowed outcome aggregate used beside the windowed health summary.

**Contract inputs:** Health JSON/compact shapes remain unchanged; CLI one-shot health still omits resident telemetry.

**File ownership:** `src/Miller.Server/Telemetry/TelemetryLedger.cs`; `src/Miller.Server/Tools/WorkspaceTool.cs`; `tests/Miller.Tests/Server/TelemetrySummaryTests.cs`; `tests/Miller.Tests/Server/WorkspaceToolTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch.

**What to build:** Add windowed outcome aggregation without removing lifetime APIs. Pass `SummarizeRecent(7)` and the matching seven-day outcome counts into both current and selected-workspace health paths.

**Approach:** Extend the raw telemetry test helper to accept an outcome. Test a 20-day-old error plus current rows, and assert adjacent health summary/outcome counts agree and old-only errors produce no warning.

**Acceptance criteria:**
- [ ] Red tests reproduce 1,499-style retained counts appearing as recent health errors.
- [ ] Current and selected workspace health use the same seven-day boundary for summary and outcomes.
- [ ] Lifetime outcome APIs and their existing tests remain unchanged.
- [ ] Compact and JSON health omit telemetry warnings when only old errors remain.
- [ ] Focused telemetry and workspace-tool tests pass; `Miller.Server` and `Miller.Tests` build cleanly.
- [ ] Worker hands the verified diff to the lead without committing.

### Task 4: Wrapper-safe context batching

**Files:**
- Modify: `src/Miller.Indexing/ISymbolLookupIndex.cs`
- Modify: `src/Miller.Indexing/FtsSymbolSearchIndex.cs`
- Modify: `src/Miller.Indexing/MillerRepositoryIndex.cs`
- Modify: `src/Miller.Indexing/SymbolSearchProjection.cs`
- Modify: `src/Miller.Server/Workspaces/ReadPhaseTelemetry.cs`
- Modify: `src/Miller.Server/Workspaces/ContextSearchCacheLookupIndex.cs`
- Modify: `src/Miller.Server/Workspaces/LaggingSidecarSymbolLookup.cs`
- Modify: `src/Miller.Server/Tools/SearchTool.cs`
- Test: `tests/Miller.Tests/Indexing/FtsSymbolSearchIndexTests.cs`
- Test: `tests/Miller.Tests/Server/ContextQueryRetrievalTests.cs`
- Test: `tests/Miller.Tests/Server/SearchToolTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`

**Interfaces:**
- Consumes: Task 1 stable live-row identity, existing `Search`/`Resolve`, fetch escalation, relaxation, and resolve telemetry.
- Produces: `IReadOnlyDictionary<int, IndexedSymbol> ISymbolLookupIndex.ResolveMany(IReadOnlyCollection<int> docIds)` with a default per-document fallback and wrapper forwarding.

**Contract inputs:** Exact candidate order, visibility, scoring, relaxation, diagnostics, result counts, and `(query, limit, excludeTests)` memoization remain byte-compatible.

**File ownership:** `src/Miller.Indexing/ISymbolLookupIndex.cs`; `src/Miller.Indexing/FtsSymbolSearchIndex.cs`; `src/Miller.Indexing/MillerRepositoryIndex.cs`; `src/Miller.Indexing/SymbolSearchProjection.cs`; `src/Miller.Server/Workspaces/ReadPhaseTelemetry.cs`; `src/Miller.Server/Workspaces/ContextSearchCacheLookupIndex.cs`; `src/Miller.Server/Workspaces/LaggingSidecarSymbolLookup.cs`; `src/Miller.Server/Tools/SearchTool.cs`; `tests/Miller.Tests/Indexing/FtsSymbolSearchIndexTests.cs`; `tests/Miller.Tests/Server/ContextQueryRetrievalTests.cs`; `tests/Miller.Tests/Server/SearchToolTests.cs`; `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`

**Serialization required:** Yes.

**Dependency reason:** Depends on Task 1's stable live-row `DocId` and path-cache behavior; shares `LaggingSidecarSymbolLookup.cs`.

**What to build:** Add bounded document-ID batch hydration at the lookup interface, implement it with one SQLite read path in FTS, forward it through every production wrapper, and make `SearchTool` hydrate each fetch window in batches before existing filtering/scoring.

**Approach:** The measured wrapper records equivalent resolve counts/timing, the lagging wrapper validates every row against live state, and the context cache forwards policy rather than unwrapping. Guards count batches per escalation window and relaxation pass. No connection-session abstraction is added unless the fixed replay still misses after this task.

**Acceptance criteria:**
- [ ] Red tests show the current production wrapper chain cannot batch and performs per-hit SQLite resolves.
- [ ] Default fallback preserves every existing fake/implementation without custom batching.
- [ ] FTS hydrates at most 500 IDs per statement and preserves requested identity/order mapping.
- [ ] Measured, lagging, and context-cache wrappers preserve telemetry and live-row validation.
- [ ] Search/context result ordering, diagnostics, and relaxation output remain byte-compatible.
- [ ] Focused FTS, context-retrieval, search-tool, and provider tests pass; `Miller.Indexing`, `Miller.Server`, and `Miller.Tests` build cleanly.
- [ ] Fixed context replay meets p95 <=3,000 ms and max <=5,000 ms, or the worker reports the largest remaining measured phase without weakening behavior.
- [ ] Worker hands the verified diff to the lead without committing.

### Task 5: Measured impact graph reduction

**Files:**
- Modify: `src/Miller.Indexing/SqliteSymbolGraphIndex.cs`
- Test: `tests/Miller.Tests/Indexing/SqliteSymbolGraphIndexTests.cs`

**Interfaces:**
- Consumes: `GraphQueryTelemetry`, `GraphStatementPhase`, statement observer, fixed changed-path/git-diff replay.
- Produces: bounded execution of the largest repeated graph statement family with unchanged graph results and phase semantics.

**Contract inputs:** Do not assume a generic cache helps. Supplemental `SymbolExists` checks are the first candidate only if the red count fixture and fixed replay prove they dominate.

**File ownership:** `src/Miller.Indexing/SqliteSymbolGraphIndex.cs`; `tests/Miller.Tests/Indexing/SqliteSymbolGraphIndexTests.cs`

**Serialization required:** No.

**Dependency reason:** None - safe parallel batch after Batch A.

**What to build:** Capture phase counts for a high-frontier evidence traversal, write a failing count test for the dominant repeated family, and batch or reuse only that family. Preserve proof completeness, reverse/forward direction differences, early-exit behavior, and telemetry phase order.

**Approach:** Prefer batching supplemental endpoint existence checks when confirmed. Derive expected statement counts by hand from batch size and fixture cardinality. Do not add a cache whose hit set is already covered by the existing 4,000-entry evidence cache.

**Acceptance criteria:**
- [ ] Baseline fixture records statements by phase and names the dominant repeated family.
- [ ] Red test fails on the current repeated count for that family.
- [ ] Minimal implementation bounds the family without changing reached nodes, evidence, truncation, ordering, or phase attribution.
- [ ] Existing cancellation, fixed-query-family, high-frontier, and evidence parity tests remain green.
- [ ] Focused `SqliteSymbolGraphIndexTests` pass; `Miller.Indexing` and `Miller.Tests` build cleanly.
- [ ] Fixed changed-path and git-diff replays meet p95 <=5,000 ms, or the worker reports the next largest measured phase without speculative code.
- [ ] Worker hands the verified diff to the lead without committing.

## Lead Integration and Completion

- Review every worker report for Miller-first orientation, API-shape evidence, TDD red/green proof, gate invariants, and worktree state.
- Review Batch A tasks independently and commit exact owned paths plus this plan after each approval.
- Generate task briefs and dispatch Tasks 4 and 5 only after Task 1's commit is present.
- Run the fixed replays from this plan after all performance code lands. A missed hard latency target keeps the task open and routes the largest remaining measured phase to the owning worker.
- Update the design acceptance checkboxes and add `docs/findings/2026-08-28-tool-latency-and-health-recovery.md` with before/after metrics, deterministic counts, test evidence, and any report-only variance.
- Run affected-change verification, branch gate, Scale suite, security scopes, and final worktree reconciliation.

## Plan Acceptance Criteria

- [ ] Tasks 1-5 are TDD-complete, reviewed, and committed on the task branch.
- [ ] Context and impact fixed replays meet their hard latency targets.
- [ ] Edit store-mode zero-materialization and health seven-day contracts pass.
- [ ] Release build, fast suite, Scale suite, secrets scan, and dependency audit pass on final HEAD.
- [ ] Design and recovery finding carry exact verification evidence and completed checkboxes.
