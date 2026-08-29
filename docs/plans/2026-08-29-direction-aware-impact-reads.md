# Direction-aware impact reads implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Remove direction-opposite query-time resolution reads from graph traversal and prove the fixed warm resident impact p95 moves from 8.296 seconds toward the 5-second product target without changing results.

**Architecture:** Keep direction selection inside `QueryTimeResolutionReader`. Graph scratch construction receives the existing `Direction`; forward reads build within/source collections, reverse reads build named collections, and `Both` builds both. Non-graph exact and fallback readers retain the full bidirectional `ResolveQuery` behavior.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, xUnit, Serilog, existing Miller impact replay and graph-resolution telemetry.

**Architecture Quality:** The change stays inside the query-time reader that owns site SQL, scratch reuse, and resolution. Public interfaces remain unchanged. Architecture risk is medium because a partial scratch can make graph edges depend on paired-consumer call order if the completeness rule is wrong.

## Global Constraints

- Approved design: `docs/plans/2026-08-28-direction-aware-impact-reads-design.md`.
- Do not add a reference sidecar, cache, producer-store table, materialized resolution, dependency, or MCP tool.
- Preserve query-time resolution policy v6, resolver behavior, identifier detail loading, relationship reads, edge order, confidence, provenance, truncation, compact output, and JSON output.
- Do not optimize by `GraphReadKind`. The paired resolution and unresolved-name consumers reuse one scratch value and need the union of their data for the selected direction.
- `ReadInboundExact`, `ReadInboundFallback`, `ReadOutgoingExact`, and `ReadOutgoingFallback` must retain full bidirectional resolution reads.
- Do not bundle lazy identifier-detail loading into this plan.
- Use strict `razorback:test-driven-development`. Production code follows a correctly failing test.
- Fixed workload: `impact --changed-paths` over exactly `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs`, `src/Miller.Server/Cli/CliDispatch.cs`, `src/Miller.Server/Tools/WorkspaceRender.cs`, `src/Miller.Server/Tools/WorkspaceTool.cs`, `src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs`, and `src/Miller.Server/Workspaces/WorkspaceRemoval.cs`; workspace `/home/murphy/source/miller/.worktrees/tool-latency-health`; depth 2; limit 200; sequential execution.
- Recorded resident warm calls are 8.189, 8.262, 8.296, 8.244, and 8.271 seconds; warm p95 and maximum are 8.296 seconds.
- Recorded output SHA-256 is `fc9ad40c061d620c346a90866dda9ea47fcb81ce3af081caa00ec3931e2ca483`, with 53 impacted symbols and 147 likely tests.
- Keep the change only when output parity holds, warm p95 is at most 6.222 seconds, and no warm sample exceeds 8.296 seconds. The product target is warm p95 at most 5.000 seconds.

---

## Verification Strategy

**Project source of truth:** `CLAUDE.md` testing, build, query-time resolution, and Scale-suite rules.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~QueryTimeResolutionReaderTests"` for the first failing and passing cycles.

**Worker ceiling:** `dotnet test --filter "FullyQualifiedName~QueryTimeResolutionReaderTests|FullyQualifiedName~SqliteSymbolGraphIndexTests|FullyQualifiedName~FamilyStoreReadSessionTests|FullyQualifiedName~BoundedRevisionFactCacheTests"` plus Release builds of `Miller.Indexing` and `Miller.Tests`.

**Worker gate invariant:** Direction-specific tests prove the unused SQL arms report zero rows and operations, `Both` retains controlled fixture counts, paired consumers reuse one complete scratch in either order, and family-store graph results remain equal to the existing expected graph.

**Lead affected-change scope:** Review the exact diff with Miller `inspect` and `trace`, run the four-class focused union once on the integrated source tree, then run `dotnet build Miller.slnx -c Release`.

**Branch gate:** A bare `dotnet test` once on the final source tree, `scripts/test.sh scale`, and `dotnet build Miller.slnx -c Release` with 0 warnings and 0 errors.

**Security scope:** `gitleaks detect`; `dotnet list Miller.slnx package --vulnerable --include-transitive`.

**Replay/metric evidence:** Output hash, impacted count, likely-test count, complete phase accounting, warm p95 at most 6.222 seconds, and no warm sample above 8.296 seconds are hard gates. Warm p95 at most 5.000 seconds is the product completion gate. Individual subphase timings are report-only evidence for a possible later design.

**Escalation triggers:** Any result change, incomplete scratch reuse, non-graph exact/fallback read change, less than 25 percent p95 improvement, or a need for schema/storage work stops acceptance. Indexing read-path changes require the Scale suite.

**Assigned verification failure:** Workers stop and report when an assigned gate fails unless this plan explicitly defines the replay rejection path.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. Replay entries record the cold sample, every warm sample, output hash, result counts, and phase totals.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Direction-aware graph scratch | None - serial | Modify `src/Miller.Indexing/Reads/QueryTimeResolutionReader.cs` and `tests/Miller.Tests/Indexing/Reads/QueryTimeResolutionReaderTests.cs` | Yes | Task 2 requires the committed branch binary and telemetry produced by Task 1. |
| Task 2: Fixed replay and decision record | None - serial | Create `docs/findings/2026-08-29-direction-aware-impact-reads.md`; modify `docs/README.md`, `docs/findings/2026-08-28-impact-read-path-spike.md`, and `docs/plans/2026-08-28-direction-aware-impact-reads-design.md` | Yes | Measurement is invalid until Task 1 passes parity review and the Release binary is rebuilt. |

Commit mode is `serial-worker-commit` for both tasks.

### Task 1: Direction-aware graph scratch

**Files:**
- Modify: `src/Miller.Indexing/Reads/QueryTimeResolutionReader.cs:600-754`
- Modify: `tests/Miller.Tests/Indexing/Reads/QueryTimeResolutionReaderTests.cs:341-455`

**Interfaces:**
- Consumes: existing `ResolveGraphQuery(SqliteConnection, IReadOnlyList<string>, Direction, GraphReadKind)`, `ResolveQuery`, `GraphResolutionBreakdown`, and `PendingScratch` direction key.
- Produces: graph-only direction-aware scratch construction with unchanged `ReadResolutionEdges` and `ReadUnresolvedNameEdges` signatures and output records.

**Contract inputs:** `ResolveQuery` has four non-graph callers that require the current full read. Preserve them with a two-argument full-read path or explicit `Direction.Both`; only `ResolveGraphQuery` passes the requested graph direction. A skipped arm produces a zero `GraphResolutionMeasurement`. Do not narrow scratch by `GraphReadKind`.

**File ownership:** Modify `src/Miller.Indexing/Reads/QueryTimeResolutionReader.cs` and `tests/Miller.Tests/Indexing/Reads/QueryTimeResolutionReaderTests.cs`.

**Serialization required:** Yes.

**Dependency reason:** Task 2 requires the committed branch binary and telemetry produced by Task 1.

**What to build:** Add direction to graph scratch construction. Forward graph reads execute identifier-within and pending-by-source arms only; reverse graph reads execute identifier-named and pending-by-name arms only; `Both` executes all four. Candidate, detail, resolver, pending-resolution, and relationship phases keep their current code for the retained sites.

**Approach:** First add caller-facing tests that fail because forward observations still report named work and reverse observations still report within work. Add an either-order scratch-reuse test that compares literal serialized edges and proves one resolve pass. Implement the smallest branching inside `ResolveQuery`, using zero measurements for skipped arms. Keep the full-read entry used by exact/fallback consumers. Run the controlled mutation check: forcing reverse to execute a within arm or omitting a named arm must fail at least one test.

**Acceptance criteria:**
- [x] RED proves forward currently runs named arms and reverse currently runs within arms.
- [x] Forward reports zero named rows and operations; reverse reports zero within rows and operations; `Both` retains existing counts.
- [x] Paired consumers reuse one scratch in either call order without losing resolution, pending, or unresolved-name edges.
- [x] Non-graph inbound/outgoing exact and fallback behavior stays on the full-read path.
- [x] Existing graph parity, homonym, pending override, QML, bounded-cache, and ordering tests pass.
- [x] Worker focused union and assigned Release builds pass; the worker commits only owned files and reports RED/GREEN, Miller, API-shape, gate, worktree, branch, and dirty-state evidence.

### Task 2: Fixed replay and decision record

**Files:**
- Create: `docs/findings/2026-08-29-direction-aware-impact-reads.md`
- Modify: `docs/README.md`
- Modify: `docs/findings/2026-08-28-impact-read-path-spike.md`
- Modify: `docs/plans/2026-08-28-direction-aware-impact-reads-design.md`

**Interfaces:**
- Consumes: Task 1 Release binary, fixed workload, `GraphResolutionBreakdown` logs, recorded baseline, and output hash.
- Produces: exact before/after evidence, a keep-or-reject verdict, and either product-gate completion or a measured residual-cost statement for a separate design.

**Contract inputs:** Run one rebuilt one-shot CLI call for byte-level contract parity, then one cold and five warm resident calls on the same task view and fixed workload for the product metric and phase split. Record the CLI output hash and counts, every resident duration, candidate-batch shape, and summed subphase rows/operations/times. Do not mix one-shot and resident timings or change source, dataset, command, view, or concurrency during either replay role.

**File ownership:** Create `docs/findings/2026-08-29-direction-aware-impact-reads.md`; modify `docs/README.md`, `docs/findings/2026-08-28-impact-read-path-spike.md`, and `docs/plans/2026-08-28-direction-aware-impact-reads-design.md`.

**Serialization required:** Yes.

**Dependency reason:** Measurement is invalid until Task 1 passes parity review and the Release binary is rebuilt.

**What to build:** Rebuild the branch Release binary, run the one-shot contract replay and resident performance replay, and document whether the change meets the 6.222-second keep gate and 5.000-second product gate. The finding must show the skipped-arm zeros and quantify the remaining detail, resolver, named, candidate, relationship, and orchestration costs.

**Approach:** Keep the machine quiet. Compare the one-shot result to the recorded hash and counts. Discard only the resident cold call for p95 and keep the five resident warm samples as the performance series. If parity fails or warm p95 exceeds 6.222 seconds, stop acceptance and report the exact rejection evidence so the lead can route a Task 1 revert. If the keep gate passes but the product gate remains open, name the largest measured residual without proposing bundled work. Update the design acceptance checklist only for criteria proved by the replay.

**Acceptance criteria:**
- [ ] One one-shot parity call and one cold plus five warm resident calls use the exact workload and view.
- [ ] Output hash, 53 impacted symbols, and 147 likely tests remain unchanged.
- [ ] Forward-opposite arms report zero work on the reverse impact workload and every resolution pass has a complete breakdown.
- [ ] Warm p95 is at most 6.222 seconds and no warm sample exceeds 8.296 seconds, or the task reports rejection evidence without claiming acceptance.
- [ ] The finding states whether the 5.000-second product gate closed and names the largest measured residual if it did not.
- [ ] Documentation links, design checklist, and worker worktree state are clean; the worker commits only owned files and reports exact replay evidence.

## Plan acceptance criteria

- [x] Task 1 is TDD-complete, lead-reviewed, and committed without public or non-graph behavior changes.
- [ ] Task 2 proves byte-identical results and either accepts or rejects the change from the fixed performance gates.
- [ ] Accepted code meets the 25 percent warm p95 improvement gate; product completion requires warm p95 at most 5.000 seconds.
- [ ] Fast, Scale, Release, secrets, dependency, and worktree gates pass on the final accepted source tree.
