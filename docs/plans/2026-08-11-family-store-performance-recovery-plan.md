# Family Store Read Performance Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make the default family-store path serve context, impact, and trace without hydrating and retaining the full repository graph in every Miller process.

**Architecture:** `WorkspaceReadContext` will carry the narrow `ISymbolLookupIndex` and `ISymbolGraphReachability` contracts. Family-store reads will reuse the generation-checked on-disk FTS lookup and execute bounded graph queries through the pinned `IWorkspaceReadSession`; legacy reads will keep the existing `MillerRepositoryIndex` and in-memory graph.

**Tech Stack:** .NET 10, C#, Microsoft.Data.Sqlite, xUnit, Miller family-store read sessions and search sidecar.

**Architecture Quality:** High-risk performance refactor with an approved narrow-interface shape. Keep database access inside Miller.Indexing, preserve pinned family-store visibility, and do not expose SQL or family-store details to tools.

## Global Constraints

- Do not add a new MCP tool or public CLI contract.
- Preserve byte-identical tool output for the same indexed snapshot.
- Keep `Miller.Core` free of I/O dependencies.
- Family-store queries must run through the pinned `IWorkspaceReadSession`; do not open raw store files from tool code.
- Do not materialize a full `MillerRepositoryIndex`, `SymbolSearchProjection`, or `SymbolGraph` on the default family-store context/impact/trace path.
- Bounded caches must have explicit caps and must not scale with the whole workspace.
- Warm context, impact, and trace target at most 2 seconds on this development machine and 5 seconds on constrained Windows-oriented dogfood.
- Retained private/PSS target at most 350 MB per idle Miller host after bounded read calls; peak target at most 600 MB.
- Never repeat an unchanged operation lasting more than 60 seconds without new phase or query-count evidence.

---

## Verification Strategy

**Project source of truth:** `AGENTS.md` testing and build sections.

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~WorkspaceIndexProviderTests|FullyQualifiedName~SqliteSymbolGraphIndexTests|FullyQualifiedName~ContextToolTests|FullyQualifiedName~ImpactToolTests|FullyQualifiedName~TraceToolTests"` narrowed further to the exact new test during each RED/GREEN cycle.

**Worker ceiling:** The affected test classes above. Do not run the bare fast suite, Scale suite, or a real large-workspace replay.

**Worker gate invariant:** Family-store contexts never invoke the full session-index loader; disk-backed lookup and graph operations preserve resolution/traversal output and pinned-view correctness.

**Lead affected-change scope:** Run the affected classes once after both tasks are approved, then `dotnet build Miller.slnx -c Release`.

**Branch gate:** `scripts/test.sh` once on the final source tree. Scale is required only for the exact family-store parity/performance fixture selected by the lead; do not run the entire Scale suite.

**Security scope:** none declared.

**Replay/metric evidence:** Hard gates are no full-index loader call, parity of symbol/graph outputs, bounded query/cache counts, warm tool wall time, and retained PSS budgets. Phase timings, page faults, CPU, and SQLite read counts are report-only until a stable deterministic threshold exists.

**Escalation triggers:** Any output parity failure, pinned-view violation, sidecar-disabled regression, process retained PSS above 350 MB, or tool wall time above 2 seconds requires focused diagnosis before broader verification.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. Reuse green evidence for an unchanged tree.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Disk-backed family-store read context | None - serial | `src/Miller.Indexing/SqliteSymbolGraphIndex.cs`; `src/Miller.Server/Workspaces/WorkspaceReadContext.cs`; `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`; `src/Miller.Server/Tools/ContextTool.cs`; `src/Miller.Server/Tools/ImpactTool.cs`; `src/Miller.Server/Tools/TraceTool.cs`; `tests/Miller.Tests/Indexing/SqliteSymbolGraphIndexTests.cs`; `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`; directly required context/impact/trace tests | Yes | Task 2 measures and instruments the exact path established by Task 1.
| Task 2: Lazy family-store bootstrap and freshness | None - serial | `src/Miller.Indexing/IndexHolder.cs`; `src/Miller.Server/Hosting/FreshnessService.cs`; `src/Miller.Server/Hosting/FreshnessPoller.cs`; `src/Miller.Server/Hosting/IndexBootstrapService.cs`; `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`; `src/Miller.Server/Tools/WorkspaceTool.cs`; directly required tests | Yes | Consumes Task 1's lean provider contract; removes the background hydration observed after Task 1 began.
| Task 3: Bounded read telemetry and resource regression | None - serial | telemetry helper/source selected from existing telemetry abstractions; focused telemetry tests; one family-store resource regression test; this plan and design docs | Yes | Consumes Tasks 1 and 2's final interactive and background read paths.
| Task 4: Rebuilt-host dogfood and cancellation isolation | None - serial | process-level harness/evidence and production source/tests only if a measured budget miss selects them | Yes | Requires Tasks 1-3 committed so the live measurements exercise the candidate read path.

### Task 1: Disk-backed family-store read context

**Files:**
- Modify: `src/Miller.Indexing/SqliteSymbolGraphIndex.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceReadContext.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Modify as required by the narrower context contract: `src/Miller.Server/Tools/ContextTool.cs`, `src/Miller.Server/Tools/ImpactTool.cs`, `src/Miller.Server/Tools/TraceTool.cs`
- Test: `tests/Miller.Tests/Indexing/SqliteSymbolGraphIndexTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`
- Test only when compilation or parity requires it: the directly affected Context/Impact/Trace test files

**Interfaces:**
- Consumes: `IWorkspaceReadSession.Read<TResult>(Func<SqliteConnection,TResult>)`, `ISymbolLookupIndex`, `ISymbolGraphReachability`, `FtsSymbolSearchIndex`, and `SmartTargetResolver(ISymbolLookupIndex)`.
- Produces: `WorkspaceReadContext` with separate lookup and graph interfaces, and `SqliteSymbolGraphIndex` support for pinned read sessions without transferring session ownership.

**Contract inputs:** Default family-store sidecar lookup is generation checked through `SymbolSearchSidecar.OpenStoreRequired`; sidecar-disabled operation may retain the bounded projection fallback but must not reintroduce full graph hydration.

**File ownership:** `src/Miller.Indexing/SqliteSymbolGraphIndex.cs`; `src/Miller.Server/Workspaces/WorkspaceReadContext.cs`; `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`; `src/Miller.Server/Tools/ContextTool.cs`; `src/Miller.Server/Tools/ImpactTool.cs`; `src/Miller.Server/Tools/TraceTool.cs`; `tests/Miller.Tests/Indexing/SqliteSymbolGraphIndexTests.cs`; `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`; directly required context/impact/trace tests

**Serialization required:** Yes.

**Dependency reason:** Establishes the read interface and ownership semantics measured by Task 2.

**Acceptance criteria:**
- [x] A focused RED proves a family-store `Resolve` invokes neither `_loadSessionIndex` nor a full graph loader.
- [x] `SqliteSymbolGraphIndex` can query through a pinned `IWorkspaceReadSession` and preserves existing reach/path evidence parity.
- [x] Context, impact, and trace consume `ISymbolLookupIndex` plus `ISymbolGraphReachability` without needing `MillerRepositoryIndex.Graph`.
- [x] Legacy artifact behavior and current-holder behavior remain unchanged.
- [x] Sidecar-disabled family-store lookup remains functional without a full graph hydration.
- [x] Assigned worker tests pass with zero warnings/errors.
- [x] Commit only owned files using `serial-worker-commit`.

### Task 2: Lazy family-store bootstrap and freshness

**Files:**
- Modify: `src/Miller.Indexing/IndexHolder.cs`
- Modify: `src/Miller.Server/Hosting/FreshnessService.cs`
- Modify only if its pure decision contract requires it: `src/Miller.Server/Hosting/FreshnessPoller.cs`
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs`
- Test: `tests/Miller.Tests/Server/FreshnessServicePollNowTests.cs`
- Test: directly required bootstrap/holder/workspace status tests

**Interfaces:**
- Consumes: Task 1's family-store provider path, `IndexHolder` revision/artifact state, `WorkspaceIndexFactsReader.ReadSymbolCounts`, and `WorkspaceReadSessionFactory`.
- Produces: an `IndexHolder` family-store state whose repository object is lazy while revision, artifact identity, and count metadata stay eager; freshness replaces the lazy generation without evaluating it.

**Contract inputs:** Legacy mode remains eager. A direct edit/legacy caller may materialize the lazy repository once. A family-store bootstrap, idle refresh tick, workspace status, search, inspect, context, impact, and trace must not materialize it.

**File ownership:** `src/Miller.Indexing/IndexHolder.cs`; `src/Miller.Server/Hosting/FreshnessService.cs`; `src/Miller.Server/Hosting/FreshnessPoller.cs`; `src/Miller.Server/Hosting/IndexBootstrapService.cs`; `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`; `src/Miller.Server/Tools/WorkspaceTool.cs`; directly required tests

**Serialization required:** Yes.

**Dependency reason:** Requires Task 1's lean tools so removing eager holder hydration does not break current-family reads.

**Acceptance criteria:**
- [x] A focused RED proves family-store bootstrap or freshness evaluates the repository loader before any explicit legacy/edit access.
- [x] Family-store bootstrap records revision, artifact identity, and symbol count without building `MillerRepositoryIndex`.
- [x] A store revision advance replaces the lazy holder generation and advances metadata without evaluating either old or new repository factory.
- [x] First explicit `Current`/legacy-edit access evaluates the current generation once; subsequent access reuses it.
- [x] Legacy bootstrap/freshness semantics and atomic snapshot behavior remain unchanged.
- [x] Workspace status in family-store mode uses registered/store facts and does not force holder evaluation.
- [x] Every current family-store provider route reads holder metadata without touching the lazy repository; legacy routes still capture one atomic repository/revision snapshot.
- [x] Assigned worker tests pass with zero warnings/errors.
- [x] Commit only owned files using `serial-worker-commit`.

### Task 3: Bounded read telemetry and resource regression

**Files:**
- Modify: the smallest existing telemetry abstraction and tool/provider call sites required for phase metadata
- Test: focused telemetry tests under `tests/Miller.Tests/Server/`
- Test: one deterministic family-store read-resource regression under `tests/Miller.Tests/Server/` or `tests/Miller.Tests/Indexing/`
- Modify: `docs/plans/2026-08-11-family-store-performance-recovery-design.md`
- Modify: this plan

**Interfaces:**
- Consumes: Tasks 1 and 2's disk-backed `WorkspaceReadContext`, lazy family-store holder, and existing `TelemetryScope.SetMetadata` contract.
- Produces: real elapsed/count metadata for provider resolution, symbol lookup, graph traversal, and provider-cache entries; deterministic evidence that repeated family-store reads keep loaded-symbol and cache counts bounded.

**Contract inputs:** Telemetry is added to existing tool records only; no new MCP surface or new unbounded label cardinality.

**File ownership:** telemetry helper/source selected from existing telemetry abstractions; focused telemetry tests; one family-store resource regression test; this plan and design docs

**Serialization required:** Yes.

**Dependency reason:** Requires Tasks 1 and 2's final paths so measurement covers interactive and background work.

**Acceptance criteria:**
- [x] Existing telemetry records expose real provider resolve, lookup, and graph timings/counts plus bounded provider-cache entries.
- [x] A deterministic test proves repeated family-store read calls preserve cached index identity, report per-call deltas, and do not accumulate a workspace-sized graph cache.
- [x] The design records the implemented telemetry boundary and rejects a synthetic render phase.
- [x] Assigned worker tests pass with zero warnings/errors: exact 4/4 and affected ceiling 456/456.
- [x] Commit only owned files using `serial-worker-commit` (`75e86c0a`).

### Task 4: Rebuilt-host dogfood and cancellation isolation

**Files:**
- Create or modify only if required by a measured miss: the smallest process-level performance harness/test
- Modify only after a focused RED: exact production source selected by telemetry
- Modify: `PERF.md`
- Modify: this plan and design evidence

**Interfaces:**
- Consumes: Tasks 1-3's disk-backed provider, lazy holder, and bounded telemetry.
- Produces: one rebuilt-host latency/PSS/idle-I/O sample and one bounded worktree-open cancellation sample.

**Contract inputs:** Do not use the old MCP hosts as candidate evidence. Do not register/open the performance
worktree through the old host again. Every subprocess has a 60-second hard bound; a miss selects one phase and one
focused RED before any repeat. Record PID, commit, wall, CPU, RSS/PSS, logical reads, and telemetry metadata.

**File ownership:** process-level harness/evidence and only the production/test files selected by a measured miss

**Serialization required:** Yes.

**Dependency reason:** Candidate resource evidence is meaningful only after all read-path fixes and telemetry land.

**Acceptance criteria:**
- [x] Fresh candidate host performs no eager repository hydration at bootstrap or idle revision advance.
- [x] Warm inspect meets 500 ms and context/impact/trace meet 2 seconds on the development machine.
- [x] Idle retained PSS is at most 350 MB and an ordinary read peaks at most 600 MB.
- [ ] One bounded registration/open identifies its registry/refresh/extractor/sidecar phases; cancellation terminates the supervised extractor and returns the host to bounded idle.
- [x] Any missed budget becomes one telemetry-selected RED/fix; no unchanged long operation is repeated.

The registration/open item remains unexecuted because Miller exposes no registry-path override and the safe harness
must not repurpose `HOME`/`USERPROFILE` or mutate the user's shared registry. Exact-child cancellation was exercised
throughout the bounded candidate diagnostics; adding a supported registry-isolation seam is separate PERF-010 work.
