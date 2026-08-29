# Impact Read-Path Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Split the fixed impact workload into measured query-time resolution subphases and determine whether an in-place read-path repair can replace the proposed reference sidecar.

**Architecture:** Keep every measurement inside the existing `QueryTimeResolutionReader` and `GraphStatementObservation` path. Record one generation-scoped resolution breakdown without changing graph edges, MCP output, or query-time resolution policy; then use the live store and the exact fixed workload to compare visibility joins and identify the dominant cost.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, SQLite query plans, Serilog, xUnit.

**Architecture Quality:** Affected modules are `Miller.Indexing` query-time resolution and the existing server graph-phase logger. The caller-facing interface and result contracts remain unchanged. Measurement stays local behind the existing statement-observer seam, tests use that same seam, and no new artifact or public extension point is introduced. Architecture risk is medium because the observer crosses Indexing and Server, while semantic risk is low because the spike does not alter edge construction.

## Global Constraints

- Do not add `references.db`, another sidecar, a resident identifier collection, materialized resolution, a new MCP tool, a dependency, or a producer-store write.
- Preserve query-time resolution policy v6, graph edge ordering, confidence, provenance, truncation, and compact/JSON output byte-for-byte.
- The fixed workload is `impact --changed-paths` over exactly these six paths: `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs`, `src/Miller.Server/Cli/CliDispatch.cs`, `src/Miller.Server/Tools/WorkspaceRender.cs`, `src/Miller.Server/Tools/WorkspaceTool.cs`, `src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs`, `src/Miller.Server/Workspaces/WorkspaceRemoval.cs`; workspace `/home/murphy/source/miller/.worktrees/tool-latency-health`; depth 2; limit 200; sequential execution.
- Baseline at `e31612ce`: cold 10.94 seconds; warm samples 10.58, 10.73, 10.75, 10.74, and 10.62 seconds; warm nearest-rank p95 and maximum 10.75 seconds.
- Instrumentation must report candidates, sites-within, sites-named, pendings-by-source, pendings-by-name, identifier details, resolver work, and relationships with elapsed time, rows, and operation counts where meaningful.
- The spike may measure alternative SQL on a temporary connection-local table. It must not retain schema or data changes.
- This plan diagnoses only. Any performance fix that survives the spike requires a separate approved implementation plan.

---

## Verification Strategy

**Project source of truth:** `CLAUDE.md` testing, build, language-parity, and query-time-resolution rules.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~QueryTimeResolutionReaderTests|FullyQualifiedName~SqliteSymbolGraphIndexTests|FullyQualifiedName~WorkspaceIndexProviderTests"` with the narrowest class used during each TDD cycle.

**Worker ceiling:** The three focused classes above plus Release builds of `Miller.Indexing`, `Miller.Server`, and `Miller.Tests`.

**Worker gate invariant:** Focused tests prove one completed resolution pass reports one complete breakdown, scratch reuse does not duplicate it, operation counts match controlled fixtures, cancellation reports only completed work, and graph/result semantics stay unchanged.

**Lead affected-change scope:** Review the exact diff, run the focused union once on the integrated tree, then build `Miller.slnx -c Release`.

**Branch gate:** Bare `dotnet test`, `scripts/test.sh scale`, and `dotnet build Miller.slnx -c Release` once on the final source tree.

**Security scope:** `gitleaks detect`; `dotnet list Miller.slnx package --vulnerable --include-transitive`.

**Replay/metric evidence:** The instrumentation overhead hard gate is warm p95 no worse than 11.30 seconds on the fixed workload. Graph edge/result parity and complete subphase accounting are hard gates. Individual phase timings, SQL-plan comparisons, and the 5,000 ms product gate are report-only during this diagnostic plan.

**Escalation triggers:** Any graph semantic change, incomplete phase accounting, more than 5% replay overhead, or a need to modify producer schema stops the spike and records the evidence. Indexing changes require the Scale suite at the branch gate.

**Assigned verification failure:** Workers stop and report when an assigned gate fails unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. Replay entries also record every sample and phase count.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Resolution subphase instrumentation | None - serial | Modify `src/Miller.Indexing/Reads/QueryTimeResolutionReader.cs`, `src/Miller.Indexing/SqliteSymbolGraphIndex.cs`, `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`; modify `tests/Miller.Tests/Indexing/Reads/QueryTimeResolutionReaderTests.cs`, `tests/Miller.Tests/Indexing/SqliteSymbolGraphIndexTests.cs`, `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs` | Yes | Task 2 requires the committed breakdown contract and rebuilt branch binary. |
| Task 2: Fixed replay and SQL falsification | None - serial | Create `docs/findings/2026-08-28-impact-read-path-spike.md`; modify `docs/README.md` and `docs/findings/2026-08-28-tool-latency-and-health-recovery.md` | Yes | Consumes Task 1 logs and exact phase names; no measurement is valid before that source is fixed. |

Commit mode is `serial-worker-commit` for both tasks.

### Task 1: Resolution subphase instrumentation

**Files:**
- Modify: `src/Miller.Indexing/Reads/QueryTimeResolutionReader.cs`
- Modify: `src/Miller.Indexing/SqliteSymbolGraphIndex.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Test: `tests/Miller.Tests/Indexing/Reads/QueryTimeResolutionReaderTests.cs`
- Test: `tests/Miller.Tests/Indexing/SqliteSymbolGraphIndexTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`

**Interfaces:**
- Consumes: existing `GraphStatementObservation`, `QueryTimeResolutionReader.ResolveGraphQuery`, statement observer callback, and fixed server graph-phase logger.
- Produces: an internal immutable resolution-breakdown value attached only to the observation that performed `ResolveQuery`, with named time, row, and operation-count facts for Task 2.

**Contract inputs:** The existing observer phase order remains unchanged. `ResolveGraphQuery` scratch reuse emits no duplicate breakdown. Timings use `Stopwatch.GetTimestamp` and `GetElapsedTime`; tests assert counts and nonnegative durations, never wall-clock thresholds.

**File ownership:** Modify `src/Miller.Indexing/Reads/QueryTimeResolutionReader.cs`, `src/Miller.Indexing/SqliteSymbolGraphIndex.cs`, `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`; modify `tests/Miller.Tests/Indexing/Reads/QueryTimeResolutionReaderTests.cs`, `tests/Miller.Tests/Indexing/SqliteSymbolGraphIndexTests.cs`, `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`.

**Serialization required:** Yes.

**Dependency reason:** Task 2 requires the committed breakdown contract and rebuilt branch binary.

**What to build:** Add a packed internal breakdown to the existing graph observation rather than new phase enum values. Split identifier and pending site collection into measured within/source and named arms, measure candidate lookup, details, resolver loops, and relationships, and log the breakdown only when present.

**Approach:** Follow strict TDD. First prove controlled fixture counts and single-emission behavior fail. Then add the smallest internal records and stopwatch plumbing. Do not change SQL, direction selection, caching, proof traversal, graph edges, or public telemetry shapes.

**Acceptance criteria:**
- [x] RED proves the existing observer cannot report a complete resolution breakdown.
- [x] One real resolve pass reports all required subphases with deterministic row and operation counts.
- [x] Scratch reuse does not report the same work twice; cancellation reports no incomplete subphase.
- [x] Existing fixed phase order and graph outputs remain unchanged.
- [x] Server logs a bounded structured breakdown without exposing raw names or symbol ids.
- [x] Focused tests and worker Release builds pass; worker commits only owned files and reports Miller/API-shape evidence.

### Task 2: Fixed replay and SQL falsification

**Files:**
- Create: `docs/findings/2026-08-28-impact-read-path-spike.md`
- Modify: `docs/README.md`
- Modify: `docs/findings/2026-08-28-tool-latency-and-health-recovery.md`

**Interfaces:**
- Consumes: Task 1 breakdown logs, the rebuilt branch one-shot CLI, the live family store, and the fixed workload.
- Produces: a measured cause statement, sidecar verdict, and one recommended next implementation with explicit falsification evidence.

**Contract inputs:** Use one cold plus five repeated fixed-workload calls. On one SQLite connection, compare current `manifest_entries` visibility, `_miller_visible_entries`, and no-visibility upper bound for representative high-fanout names. Record `EXPLAIN QUERY PLAN`, elapsed time, returned rows, and existing live indexes.

**File ownership:** Create `docs/findings/2026-08-28-impact-read-path-spike.md`; modify `docs/README.md` and `docs/findings/2026-08-28-tool-latency-and-health-recovery.md`.

**Serialization required:** Yes.

**Dependency reason:** Consumes Task 1 logs and exact phase names; no measurement is valid before that source is fixed.

**What to build:** Run the fixed replay without changing source, extract all resolution subphase samples, and test Claude's visibility-join hypothesis against the live store. The finding must separate confirmed causes from rejected hypotheses and state whether direction-blind reads, unused detail loading, resolver CPU, proof traversal, or SQL visibility owns the next change.

**Approach:** Keep the machine quiet, preserve exact workload and view identity, discard the cold run for p95, and never combine experiments. The SQL comparison creates only connection-local temporary state. Do not implement the winning fix in this task.

**Acceptance criteria:**
- [ ] One cold plus five warm samples use the exact baseline workload and data.
- [ ] Every warm call has complete subphase times, row counts, and operation counts.
- [ ] Current and alternative visibility plans are measured on the same connection with equal result counts.
- [ ] The finding names the dominant measured cause and rejects unsupported hypotheses.
- [ ] The instrumentation overhead gate passes or the instrumentation is revised before evidence is accepted.
- [ ] The sidecar is either rejected with evidence or remains a measured last-resort candidate.
- [ ] Documentation links and final worktree state are clean; worker commits only owned files and reports exact commands.

## Plan acceptance criteria

- [x] Task 1 instrumentation is TDD-complete, reviewed, and committed without semantic changes.
- [ ] Task 2 reproduces the fixed workload and records a complete phase split and SQL comparison.
- [ ] The result identifies one evidence-backed next implementation or proves that more measurement is required.
- [ ] Fast, Scale, Release, secrets, dependency, and worktree gates pass on the final branch.
