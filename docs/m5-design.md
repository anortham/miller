# M5 design — `context` + `impact` (kept fast)

Status: **design, ready to build**. Decision-driven, grounded against the pinned `julie-server` v7.12.2
(schema 26 / contract 1) on a real extract. House style matches [m3-design](m3-design.md) /
[m6-design](m6-design.md). Confidence ~86.

## Goal

Ship the last two **read** tools — `context` (task-anchored, token-budgeted bundle) and `impact` (change-safety
/ blast radius) — and ship them **fast**. This is the founding thesis restated by julie's own telemetry
([miller-toolbox](findings/miller-toolbox.md) §"why this shape"): the two slowest julie tools (`get_context`
439ms/1.2s p95, `blast_radius` 1.3s/**5s p95**) were the two *least used*. Agents route around slow tools. So
M5's exit bar is not "it works" — it is **"it works AND it is fast enough that an agent reaches for it."**
context target sub-100ms; impact must not recompute reachability from the DB per hop the way julie did.

## The seam that makes it possible (and testable)

Both tools ride a **symbol dependency graph**. The plan's non-negotiable structural rule
([miller-mvp-plan](miller-mvp-plan.md)) is a hard logic↔infra seam so the graph engine is unit-testable with no
live DB. So:

- **Core (pure, zero I/O):** `SymbolGraph` — forward + reverse adjacency + bounded BFS; `ContextPacker` — the
  token-budget selection. Both unit-tested on in-memory edge fixtures in milliseconds. This is where the
  correctness lives and where TDD pays.
- **Indexing (infra):** `SymbolGraphReader` reads the edge tables and resolves them into a Core edge list;
  `RepositoryIndexLoader` reads symbols + edges and builds the index+graph as one immutable unit.
- **Server (surface):** `ContextTool` / `ImpactTool` — smart-string resolution, telemetry, compact/json render.

---

## Verified contract facts (checked live, schema 26)

A real 3-file polyglot extract (`OrderController → OrderService → Repo`, plus an xUnit test):

1. **`relationships`** = `from_symbol_id → to_symbol_id` (both **resolved** symbol ids) + `kind`
   (`calls`/`uses`/...) + `confidence`. **But sparse**: only the directly-extracted edges. `analysis_state` was
   `stale` / `analyzed_revision` NULL — julie's full relationship *analyze* pass does NOT run under
   `extract scan`, so this table is a precise-but-partial subset. (2 rows for a graph with ~6 real edges.)
2. **`identifiers`** = the **dense** edge source (13 rows): `name`, `kind` (`call`/`type_usage`/`variable_ref`/
   `member_access`), `file_path`, byte/line spans, `containing_symbol_id` (**resolved** — the dependent), and
   `target_symbol_id` (**NULL** until on-demand resolution). Some rows (namespace refs `Shop`/`Tests`) have NULL
   `containing_symbol_id`.
3. **They agree where they overlap:** `Process → Validate` appears as a `relationships.calls` edge AND an
   `identifiers` `Validate/call` row with `containing=Process`. Union + dedup is safe.
4. **`symbols`** carries both `start_line` AND `end_line` (whole-symbol span) → line-precise diff→symbol mapping
   is possible. (`IndexedSymbol` currently keeps only `start_line`; decision-7 adds `EndLine`.)
5. **`symbols.metadata.is_test`** is julie's cross-language test flag (verified: the xUnit `ProcessWorks` method
   carries `{"is_test":true}`; non-tests carry no key). `IndexedSymbol.IsTest` already parses it — this is
   `impact`'s "likely tests" leg, all-language, consumed not re-derived (cross-language principle).

---

## Decisions

### D1 — Surface = toolbox §3 + §5, verbatim
No new params, no scope creep.

`context`:

| param | default | notes |
|---|---|---|
| `query` ✅ | — | the task/question |
| `token_budget` | 4000 | hard bound on returned size |
| `max_hops` | 1 (0–2) | neighbor expansion radius |
| `entry_symbols` | null | seed symbol names/ids |
| `failing_test` | null | scenario hint (folded into seeds) |
| `stack_trace` | null | scenario hint (folded into seeds) |
| `format` | compact | compact\|json |

`impact`:

| param | default | notes |
|---|---|---|
| `target` | null | symbol or file (smart-resolved) |
| `changed_paths` | null | a set of changed files |
| `diff` | null | a unified diff |
| `max_depth` | 2 | reverse-reachability radius |
| `limit` | 100 | cap on impacted symbols |
| `format` | compact | compact\|json |

Exactly **one** of `target` / `changed_paths` / `diff` is required (toolbox L146); zero → a clear usage message,
not an error.

### D2 — Edge model = `relationships` ∪ name-resolved `identifiers`
Build a directed **dependency** edge `A → B` meaning "A depends on B" (A calls/uses B):

- **From `relationships`:** `from_symbol_id → to_symbol_id` directly (precise, by id). Carry `kind`.
- **From `identifiers`:** for each row with a non-NULL `containing_symbol_id` C and a `name` N, resolve N to
  **every** indexed symbol of that name `{T₁…Tₖ}` (via the index name map) and add `C → Tᵢ`. Carry `kind`.

Rules:
- Drop a row with NULL `containing_symbol_id` (no source node) or whose name resolves to **no indexed symbol**
  (external/library refs — `Assert.Equal`, the `Xunit` import — fall out naturally, bounding the graph to
  indexed symbols).
- A symbol is never its own dependency (drop self-loops from same-name resolution).
- **Dedup** `(from,to)` keeping a resolved-kind label where both sources contribute.

**Honesty (decision, not a hidden flaw):** name resolution **over-approximates on homonyms** — two methods both
named `Process` make a call to `Process` an edge to *both*. For a **blast radius this is the safe direction**
(over-include rather than miss a caller). For `context` it just widens the neighbor set slightly. This is the
documented limitation until M4's resolved cross-ref edges (and julie's analyze pass) tighten target resolution.
We do **not** silently claim precision. ([cross-language-feature-scope] honesty clause.)

### D3 — Precompute the GRAPH in memory, traverse on demand; NOT a materialized closure
julie's `blast_radius` was 5s p95 because it walked the DB **per hop**. Miller precomputes the **adjacency**
once at index build (forward + reverse `Dictionary<symbolId, symbolId[]>`) and runs a **bounded in-memory BFS**
per query — sub-millisecond at the default depths.

**Tradeoff surfaced (per the completeness rule):** a *fully materialized* transitive closure (every symbol →
its entire reachable set) would be O(V²) memory — infeasible at ~565k symbols. The plan's words "precomputed
transitive-closure reachability in the in-memory index" are satisfied by **graph-in-memory + on-demand
traversal**: the expensive part julie paid (DB round-trips per hop) is gone; bounded BFS over an in-memory map
already dwarfs julie's latency. A materialized closure for unbounded queries is a *measured* optimization, not
taken now. (If the user wants unbounded-depth materialization, that's a deliberate later call.)

### D4 — Pure logic ↔ infra seam (the property julie's tests lost)
- **`Miller.Core.Graph.SymbolGraph`** (pure): built from a node set (`id`, `isTest`) + a resolved edge list
  (`from`,`to`,`kind`). Exposes `Dependencies(id)`, `Dependents(id)`, and `Reach(starts, maxDepth, limit,
  direction)` returning reached nodes **with hop distance** (for relevance ordering). Deterministic ordering.
  Zero I/O. **Heavily unit-tested** (cycles, diamonds, depth caps, limit caps, missing nodes, self-loops).
- **`Miller.Indexing.SymbolGraphReader`** (infra): one SELECT over `relationships` + one over `identifiers`;
  resolves identifier names→ids via the symbol name map; returns the Core edge list. Read-only, parameterized,
  same D4 read discipline as the other readers.
- **`impact` reachability and `context` packing are pure Core** — the differentiator-grade testability bar.

### D5 — `impact` = reverse reachability + likely tests
"downstream of editing X" = everything that depends on X = **reverse** closure (`Dependents`).

Seeds:
- **target** → symbol: itself; file (smart-resolved): all its symbols.
- **changed_paths** → all symbols in each file.
- **diff** → parse `+++ b/<path>` headers + `@@ -a,b +c,d @@` hunks → per-file changed line ranges → symbols
  whose `[start_line, end_line]` **intersect** a changed range (line-precise; needs decision-7). If a file has
  no intersecting span (or no spans recorded), degrade to **all symbols in that file** (safe over-approximation,
  logged — no silent narrowing).

Result: reverse-closure symbols to `max_depth`, capped at `limit`, **partitioned** into impacted symbols vs
**likely tests** (`IsTest`). Render compact/json with provenance (`name kind file:line`, hop distance) and a
test list. Empty seed set / not-found → a clear note.

### D6 — `context` = search-seed + bounded neighbor expansion + token-budget pack
- **Seeds:** `Search(query)` (BM25, existing) ∪ resolved `entry_symbols` ∪ symbol names parsed from
  `failing_test` / `stack_trace` (scenario hints folded into seeds — the "mode-switch without a mode enum" the
  toolbox intends), resolved to ids.
- **Expand:** **both-direction** neighbors (`Dependencies` + `Dependents`) to `max_hops` (default 1, range 0–2),
  carrying hop distance.
- **Pack:** order candidates by (seed BM25 rank, then hop distance, then stable id); greedily add each
  (signature + provenance line) while the running **estimated token cost** ≤ `token_budget`; stop when the next
  would overflow. The selection algorithm is a pure **`Miller.Core.Graph.ContextPacker`** taking
  `(orderedCandidates, perItemTokenCost, budget) → selected` — unit-tested. Token cost is computed by the Server
  (decision-8) and passed in, keeping Core pure.
- **Perf:** sub-100ms — in-memory throughout, no per-hop DB.

### D7 — Add `EndLine` to `IndexedSymbol`
One extra `end_line` column in the startup SELECT + one record field (NULL→0). Enables line-precise diff→symbol
mapping (D5) and is broadly useful. `ToSearchableDocument` is unchanged (Core scoring doesn't need it). Cheap;
no separate per-call DB round-trip for the diff path.

### D8 — Token estimation reuses the Server `TokenEstimator`
The existing telemetry token estimator (chars/4 or the .NET tokenizer) computes each candidate's cost; the Core
packer stays pure (cost passed in). One estimator, consistent with the telemetry KPI.

### D9 — Graph travels with the index, swapped atomically
The graph must be ready when the index is published (M3's lock-free `IndexHolder` swap; symbol-ids churn on
edit). So the graph is **part of `MillerRepositoryIndex`**: `Build(symbols, edges)` builds index + graph as one
immutable unit; the existing `Build(symbols)` keeps working (empty graph) for the search/inspect tests that
don't need it. Both production build sites route through one **`RepositoryIndexLoader.Load(dbPath)`** (reads
symbols + edges, builds the unit) — replacing the two inline `MillerRepositoryIndex.Build(symbols)` calls in
`IndexBootstrapService.Run` and `IndexRebuilder.Rebuild`, so freshness rebuilds and bootstrap stay identical.

### D10 — Telemetry on both tools
Same pattern as search/inspect: `TelemetryContext.Current` — set target/query, `ResultCount`, `Outcome`
(Ok/Empty/Error), and `bytes_examined` ≈ nodes visited. The north-star token column comes from the returned
bytes the interceptor already measures.

### D11 — Scale-gated perf proof
A `[Trait("Category","Scale")]` test on a real extract asserts: graph build stays within the existing rebuild
budget (M3 measured 412ms read+build @ 50k), `context` < 100ms, and `impact` is fast (well under julie's 5s) at
default depths. Default suite stays < 10s (Core graph tests are in-memory and instant).

---

## Components (by layer)

**Miller.Core/Graph**
- `SymbolGraph.cs` — `Build(nodes, edges)`; `Dependencies`/`Dependents`/`Reach`; `GraphNode`/`GraphEdge`/
  `ReachedNode(id, hop)` records.
- `ContextPacker.cs` — pure budget selection.

**Miller.Indexing**
- `SymbolGraphReader.cs` — edge load + name resolution → Core edge list.
- `RepositoryIndexLoader.cs` — symbols + edges → `MillerRepositoryIndex` (index + graph).
- `MillerRepositoryIndex.cs` — `Build(symbols, edges)` overload + `Graph` accessor + graph-backed
  `Dependents/Dependencies` pass-throughs hydrating ids to `IndexedSymbol`.
- `IndexedSymbol.cs` — `+ EndLine`. `SqliteSymbolReader.cs` — SELECT `end_line`.
- `IndexRebuilder.cs` — delegate to `RepositoryIndexLoader`.

**Miller.Server**
- `Tools/ImpactTool.cs` + `Tools/ContextTool.cs` — `[McpServerTool]` + pure `Run(...)` cores.
- `Tools/DiffPaths.cs` (or `Resolution/`) — pure unified-diff → (path, line-ranges) parser.
- `Hosting/IndexBootstrapService.cs` — build via `RepositoryIndexLoader`.
- `Program.cs` — register both tools.

## Test strategy
- **Core unit (fast, default suite):** SymbolGraph (cycles/diamonds/depth/limit/self-loop/missing), ContextPacker
  (exact-fit, overflow, zero budget, empty), diff parser (multi-file, rename headers, context-only hunks).
- **Indexing contract (synth DB):** SymbolGraphReader resolves identifier names→ids, unions relationships, drops
  NULL-containing / unresolved-name / self edges; RepositoryIndexLoader builds a populated graph.
- **Server:** ImpactTool/ContextTool `Run(...)` over a synth index — reverse closure correctness, test
  partition, budget honored, both formats, not-found, exactly-one-input guard.
- **Scale (excluded by default):** D11 latency proofs on a live extract.

## Implementation order (TDD by layer)
1. Core `SymbolGraph` + tests. 2. Core `ContextPacker` + tests. 3. Core/diff parser + tests. 4. `EndLine`
enrichment. 5. `SymbolGraphReader` + contract tests. 6. `RepositoryIndexLoader` + `Build(symbols,edges)` +
rewire both build sites. 7. `ImpactTool` + tests. 8. `ContextTool` + tests. 9. Register + Scale latency tests.

## Verify / exit
- Build 0/0; default suite green and < 10s; Scale green on the live binary.
- `impact("Validate")` on the fixture → {Process, Handle, ProcessWorks} with ProcessWorks listed as a likely
  test. `context("order processing")` → a budget-bounded bundle of the OrderService cluster.
- D11 latency budgets met.
- **Exit:** all read tools present and *fast*; the editing tool (M6) and the freshness gate already rely on the
  index this milestone enriches.

## Explicitly NOT in M5
- M4's resolved cross-language bridge edges (blocked on julie). M5 runs on today's call/ref graph; M4 will feed
  richer, precise edges into the **same** `SymbolGraph` with no tool change.
- A materialized N² closure (decision-3). Hard budget gates / dashboard (M7).
