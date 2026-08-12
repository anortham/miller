# Family-store performance recovery design

## Status

Implemented and accepted on 2026-08-12 for the Miller interactive read path. Julie producer integration and the
separate registry-isolation gate remain release-preparation work.

## Problem

The released family-store path is correct but not usable under normal agent load:

- Published Julie 2.32.0 spent 520,055 ms resolving a 199,123-row scoped request and read about 98 GB through 24 million read syscalls.
- A byte-identical artifact retry repeated 143 seconds of materialization.
- A warm Miller `context` call took 6,938 ms after its index cache already existed.
- Four Miller hosts retained about 5.4 GB private PSS; each family-store host retained roughly 1.0–2.0 GB.

The producer repeatedly queries or materializes data it can prove is unchanged. The consumer hydrates an entire `MillerRepositoryIndex` and graph in every MCP process even though interactive tools request bounded rows and bounded graph traversals.

## Performance budgets

- Warm `context`, `impact`, and `trace`: at most 2 seconds on the development machine and 5 seconds under the constrained Windows profile.
- Cold family-store interactive read: at most 5 seconds on the development machine.
- Retained private PSS after representative interactive reads: at most 350 MB per Miller host; peak at most 600 MB.
- Byte-identical producer retry: no file-version materialization and at most 2 seconds after source identity verification.
- One-file incremental resolution: at most 5 seconds when the scope remains incremental.
- Full resolution on the real Miller-sized store: at most 60 seconds.

Budgets are release gates, not report-only numbers. A gate that misses identifies the next bottleneck; it is not repeated until new telemetry or a focused fix exists.

## Execution policy

1. Add phase timings and counters at component boundaries.
2. Capture one representative run for a slow path.
3. Fix the largest measured phase with a deterministic red/green regression.
4. Run one focused real replay with wall, CPU, RSS/PSS, row, and syscall evidence.
5. Move to the next largest phase. Broad and repeated performance suites run only after focused gates pass.

No operation over 60 seconds may be repeated without new phase-level evidence. Do not run three-sample benchmarks before a focused fix is green.

## Producer design

### Idempotent imports

After source SHA and artifact metadata verification, an import may reuse the current manifest only when a prior coordinator request is terminal, its payload is byte-identical, and the current manifest generation/hash still match inside the store transaction. Changed input, incomplete prior requests, and changed current state take the normal materialization path.

### Resolution lookup

Resolution must report time and row/query counts for scope construction, phase freezing, candidate lookup, writes, diff, and publication. Candidate lookup must not issue one store query per identifier or repeatedly scan the same name population. The implementation should batch names and graph keys behind the existing resolution-session boundary, using indexed/materialized visible-version joins where that is faster. Schema/index changes are allowed when `EXPLAIN QUERY PLAN` and the focused fixture prove the need.

## Consumer design

Family-store reads will stop constructing a full `MillerRepositoryIndex` for interactive tools.

- Implement a session-backed `ISymbolLookupIndex` that queries only requested names, ids, paths, children, and bounded search candidates from the pinned `IWorkspaceReadSession`.
- Implement a session-backed `ISymbolGraphReachability` that performs deterministic bounded breadth-first traversal with batched neighbour queries.
- Change `WorkspaceReadContext` to carry the lookup and graph interfaces required by tools instead of requiring the concrete in-memory repository index.
- Keep the legacy artifact path on the existing immutable in-memory index until evidence justifies changing it.
- Cache only small immutable metadata and prepared/query projections. Do not retain all symbols, relations, or per-generation graphs in each MCP host.

Tool output contracts and MCP tool count do not change. Database queries stay behind the indexing/workspace projection boundary rather than leaking into `ContextTool`, `ImpactTool`, or `TraceTool`.

Implemented on `dabcddd7`, `1fa03ac9`, and `4f7ff626`:

- Default family reads use the generation-checked FTS sidecar plus a pinned-session SQLite graph; normal reads do
  not call the full repository/session loader.
- Bridge tracing preserves output by loading only its bridge projection once and only when bridge mode is used.
- `IndexHolder` publishes eager revision/artifact/count metadata with a single-flight lazy repository. Store
  bootstrap, freshness, status, and every current family read route avoid evaluating it.
- Lazy factories are pinned to the captured family/view/generation identity; a generation race fails explicitly.
  Legacy routes retain one atomic repository/revision snapshot.
- Focused verification is green: 361/361 for the disk-backed read slice and 246/246 for lazy bootstrap/freshness.
  Real rebuilt-host latency and PSS remain release gates.

Bounded read telemetry is implemented on `75e86c0a`:

- Family read records carry real provider resolve elapsed time, lookup calls/time, graph calls/time, and bounded
  provider-cache entries.
- The generation-cached lookup decorator preserves the existing index identity; each context subtracts a captured
  baseline so a second call reports only its own work. The graph decorator remains context-local.
- No synthetic render timing was added because routing has no honest post-render boundary. Rendering will be
  measured at the process/tool wall in dogfood unless a real internal seam proves necessary.
- Exact cache/delta/no-growth tests passed 4/4 and the affected ceiling passed 456/456.

## Telemetry

Existing tool telemetry gains measured provider resolve, symbol lookup, graph traversal, and bounded cache facts at
the family-read boundary. Producer diagnostics gain bounded counters for candidate queries, candidate rows, phase
rows, and materialization chunks. Add a phase only at a real timing boundary; do not emit a backend name as a fake
phase or infer render time from work that ends before rendering. Telemetry must not add an MCP tool or materially
change compact tool output.

## Windows/constrained validation

The release gate runs the same correctness and performance fixtures with extraction jobs capped at one or two, records working set/private bytes and wall time, and verifies cancellation/progress behavior. Linux results alone cannot close the Windows performance gate.

## Architecture Quality

**Affected modules:** Julie store coordinator/executor/resolution session and schema indexes; Miller indexing read sessions, workspace projections, and context/impact/trace routing.

**Caller-facing interface:** Existing Miller tool and CLI contracts remain stable. Internal tools depend on `ISymbolLookupIndex` and `ISymbolGraphReachability` instead of a concrete full index.

**Depth/locality check:** SQL and store visibility remain inside producer/read-projection modules. Tool algorithms stay pure over small interfaces.

**Test surface:** Coordinator public requests, `IWorkspaceReadSession`, existing tool entry points, canonical output equality, bounded wall/resource fixtures, and one real dogfood replay.

**Seams/adapters:** The existing symbol lookup and graph reachability interfaces earn the database-backed implementations; no speculative abstraction is added.

**Rejected shortcuts:** forcing GC, increasing timeouts, raising cache/window sizes, disabling family-store mode, sharing mutable in-memory graphs across processes, or adding Windows-only behavior.

**Architecture risk:** high, because the consumer changes data access for several tools and the producer may change store indexes. Byte-identical outputs and pinned-view consistency are mandatory.

## Acceptance criteria

- [x] Producer identical retry skips all file-version materialization and preserves crash/change safety.
- [ ] Resolution telemetry identifies query/row counts per phase and no per-identifier candidate-query explosion remains.
- [x] Family-store context/impact/trace use database-backed lookup and graph traversal without full-index hydration.
- [x] Existing compact and JSON outputs are byte-identical for fixed fixtures.
- [ ] Development-machine latency and memory budgets pass.
- [ ] Constrained Windows latency, memory, progress, and cancellation gates pass.
- [ ] Julie and Miller focused correctness suites pass once on the final source trees.
- [ ] No push, tag, publish, or release occurs without separate approval.
