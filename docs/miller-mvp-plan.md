# Miller MVP implementation plan (2026-05-29)

The build plan for **Miller**, the .NET personal/local code-intelligence MCP server. Grounded in the settled
design: [architecture-decision](findings/architecture-decision.md), [miller-toolbox](findings/miller-toolbox.md),
[julie-eros-audit](findings/julie-eros-audit.md).

## North star & sequencing principle

Fast + token-thrifty + **daily-dogfoodable**. So the plan is ordered to reach a *usable-by-me* state as early as
possible (M2), then layer the differentiator and the rest. Every milestone is independently verifiable, builds on
the last, and ends green. TDD throughout; the non-negotiable structural rule is a **hard seam between logic and
infrastructure** so the differentiator is unit-testable with no live DB/subprocess/transport — the property julie's
test suite lost ([julie-eros-audit](findings/julie-eros-audit.md) §4).

## Reference codebases (use them, don't guess)

julie (`~/source/julie`, Rust — the shipping reference), eros (`~/source/eros`, Python — the richer local
feature spec), and `miller-python` (the prior Python attempt, `~/source/miller`) remain on disk as **live
references and data points** for any question that arises during the build: the exact extract contract/schema,
watcher & freshness behavior, tool semantics and defaults, telemetry shape, ranking choices, resolver breadcrumbs.
When a design or implementation question comes up, read the answer out of one of these (or the
[findings](findings/) docs) rather than guessing — they are the corpus this entire design was mined from.

## Solution structure (established in M0)

```
Miller.Core       pure logic, ZERO I/O deps: contract record types, the in-memory inverted index + BM25 ranking,
                  the structural cross-reference resolver. Unit-tested in milliseconds against in-memory fixtures.
Miller.Indexing   infrastructure: julie-server extract subprocess wrapper, SQLite (WAL) read layer, file watcher
                  + single-writer indexer. Populates Core's types from the extract DB.
Miller.Server     MCP host (ModelContextProtocol SDK, already in use), the 7 tools, the telemetry interceptor + ledger.
Miller.Tests      unit (Core, fast) + contract (against the committed julie-snapshot fixture DB) + a tagged,
                  excluded-by-default scale/integration set.
```
Core depends on nothing; Indexing/Server depend on Core; tests hit Core directly and the infra via the snapshot
fixture. This seam is what keeps the default suite < 10s.

## Cross-cutting rules (apply to every milestone)
- **TDD**: test first, against Core where possible.
- **Default `dotnet test` (unit + contract) stays < 10s**; scale/integration tests are `[Trait("Category","Scale")]`
  and excluded from the default run. CI fails if the default suite regresses past budget. (julie had *no* test CI;
  Miller has one from M0.)
- **Telemetry on every tool call from M2 onward** (the ledger that will drive future tool refinement).
- **No daemon in the read path**: readers open SQLite (WAL) and build their own in-memory index; the writer is a
  separate, optional process whose death degrades freshness, not reads.
- **Symbol IDs are span-derived and churn on edit** ([julie-eros-audit](findings/julie-eros-audit.md) §2): never
  persist resolved cross-file links keyed on symbol ID across a file update without re-resolving.

---

## Milestones

### M0 — Restructure & rebrand (clear the decks)
**Deliverable:** a clean Miller solution that builds, with CI green on an empty-but-real test suite.
- Remove `Codesearch.Interop` (UniFFI/cdylib — superseded by the extract subprocess) and `Codesearch.Embeddings`
  (embeddings out of the default path). Drop `MemoryTool.cs` (memory stays in goldfish). Discard the dead embedding
  working-tree changes.
- Rename `Codesearch.*` → `Miller.*` (namespaces, csproj, assembly names, `.sln`). Create the 4-project structure above.
- Establish the logic↔infra seam: `Miller.Core` references nothing infrastructural.
- Add GitHub Actions CI: `dotnet build` + `dotnet test` (default suite), with the < 10s budget assertion.
- Push `main` to `anortham/miller` (first push as Miller).
**Verify:** solution builds zero-warning; `dotnet test` green; CI passes on push.
**Exit:** repo is "Miller," dead code gone, CI gating.

### M1 — Extract subprocess + SQLite read layer + in-memory index (host scaffold)
**Deliverable:** index a repo and answer a ranked query in-process, no MCP yet.
- `Miller.Indexing`: wrapper over `julie-server extract scan|update|delete` (subprocess: locate binary, invoke,
  parse exit, surface errors). **DECIDED:** vendor a **version-pinned** `julie-server` build per target platform
  (committed under `tools/`, via Git LFS if size warrants), version pinned in repo config so extraction is
  reproducible and the cargo/cdylib build is fully escaped. Pull the pinned binary from julie's GitHub release.
- SQLite (WAL, read-only) layer over the verified schema (`symbols`, `identifiers`, `files`, `relationships`,
  `types` — [julie-eros-audit](findings/julie-eros-audit.md) §1).
- `Miller.Core` in-memory inverted index: `FrozenDictionary` postings rebuilt from SQLite at startup (~35 MB, <1s
  per the bench), BM25 ranking — port from `spike/Codesearch.Spike/SearchBench.cs`.
**Verify:** contract tests against the committed `fixtures/databases/julie-snapshot/symbols.db` (no live extractor
needed); ranked queries return correct ordering on the snapshot + one small live repo.
**Exit:** the read core works and is fast.

### M2 — MCP host + `search` + `inspect` + telemetry  ← FIRST DOGFOOD MILESTONE
**Deliverable:** Miller is usable from Claude Code for find + inspect (82% of real usage).
- `Miller.Server`: MCP stdio host (ModelContextProtocol SDK).
- `search` (query, mode=auto, limit=10, exclude_tests tri-state, format=compact) and `inspect` (smart-string target
  → file lists symbols / symbol shows def+sig+doc; depth=full adds refs/body/children). Smart-string resolution per
  [miller-toolbox](findings/miller-toolbox.md).
- **Telemetry interceptor** (the ledger): per-call record (tool, op, duration→histogram, bytes_examined,
  bytes_returned + est_tokens, index_fresh, outcome) → append-only `tool_telemetry` SQLite table. **OPEN (verify):**
  whether the MCP C# SDK exposes a tool-invocation filter hook; else wrap each tool in a `using Telemetry.Measure(...)`.
**Verify:** connect from Claude Code; search + inspect a real repo; telemetry rows written; latency in line with the bench.
**Exit:** *I can use Miller daily for search + inspect.* Start dogfooding here.

### M3 — Freshness: single-writer indexer + file watcher + mutation gate
**Deliverable:** the index stays fresh automatically; edits are safe.
- Single-writer indexer process: `FileSystemWatcher` → debounce/coalesce → shell `extract update/delete`. Port
  julie's reconcile-against-hash-on-overflow design ([julie-eros-audit](findings/julie-eros-audit.md) §3 "keep");
  watch `.git/HEAD` for branch-switch bursts. Never trust the event stream alone.
- Write-through on known edits + query-time staleness check (compare file mtime vs indexed).
- Mutation-gate primitive (eros pattern): detect stale-target before an edit.
- Validate SQLite WAL multi-process reads (writer + N reader instances).
**Verify:** external edit → index converges; missed-event/burst self-heals via hash reconcile; stale detection fires;
concurrent readers don't corrupt.
**Exit:** fresh-index correctness for the editing tools to rely on.

### M4 — `trace` + the structural cross-reference resolver  ← THE DIFFERENTIATOR
**Deliverable:** cross-language structural tracing nothing else does.
- `Miller.Core` resolver (pure, per [xlang-bridge-resolver-design] / [cross-language-bridge](findings/cross-language-bridge.md)):
  Entity↔DTO (`CreateMap`, `ToDto(this X)`, inline `new XDto{}` projections), Entity↔table (EF `DbSet` pluralization,
  `ToTable`, Dapper `FROM` literals), TS↔C# DTO (affix-fold + typed call `axios<T>`/`useApi<T>` ↔ `[Route]`),
  field-set Jaccard as corroborator only. Re-resolution pass after file updates (IDs churn).
- `trace` tool: default = refs + callers + callees; `to=` → shortest path; `mode=bridge` → cross-language path.
**Verify:** heavy Core unit tests (this is where TDD pays) on snapshot fixtures; bridge trace correct on a polyglot
fixture (MyraNext/Lab/Tycho-style); refs/call-path correct.
**Exit:** the sellable capability works.

### M5 — `context` + `impact` (fast, or vestigial)
**Deliverable:** task-bundle and change-safety, kept fast so they get used.
- `context`: token-budgeted relevant subgraph (target sub-100ms via the in-memory index; julie's was 439ms and skipped).
- `impact`: precomputed **transitive-closure** reachability in the in-memory index (fast, unlike julie's on-demand
  5s p95) + likely-tests.
**Verify:** latency budgets met; results correct on fixtures.
**Exit:** all read tools present and fast.

### M6 — `edit` (preview-first, freshness-gated)
**Deliverable:** index-aware editing.
- `edit`: operations (replace_text / replace_symbol_body / replace_symbol_signature / rename_symbol / insert_before
  / insert_after / add_doc). `dry_run=true` default (preview), `apply=true` to commit, `allow_stale` escape; blocked
  by the M3 mutation gate when the target is stale. Index-aware workspace-wide rename.
**Verify:** preview diffs correct; apply writes + triggers reindex; stale gate blocks; rename updates all sites.
**Exit:** the full 6 read/write tools complete.

### M7 — `workspace` + polish (+ optional dashboard)
**Deliverable:** admin tool and operational hygiene.
- `workspace` (operation=status default | refresh | full | list | open | remove).
- Soft budgets: warn-on-overage for latency + tokens per tool (cheap early-warning for refinement).
- Optional: minimal Kestrel dashboard reproducing the tool-breakdown telemetry view, decoupled (reads SQLite, runs
  standalone) — defer if it threatens focus.
**Exit:** 7-tool surface complete; telemetry-driven and self-monitoring.

---

## Open items to resolve before/within their milestone
1. ~~`julie-server` binary sourcing~~ **DECIDED**: vendor a version-pinned per-platform `julie-server` build (under
   `tools/`, Git LFS if needed), version pinned in repo config. M1 sub-detail: confirm binary size to choose
   commit-direct vs Git LFS.
2. **MCP C# SDK tool-invocation filter hook** for the telemetry interceptor (M2); fallback is a per-tool `Measure()` scope.
3. **NativeAOT posture** (M0 project setup): don't adopt patterns that foreclose AOT later, but AOT itself is a
   distribution-phase (post-MVP) CI concern, not a build blocker now.

## Explicitly out of MVP
- Memory tool (goldfish stays separate). Semantic/embedding search. The commercial/Eros tier (auth/tenancy/hosting).
- eros's agent-runner dashboard plane; test-confidence + dependency/CVE intelligence (phase-2 candidates).
```
First dogfood at M2. Differentiator at M4. The whole thing TDD'd behind a logic↔infra seam, gated by a <10s CI suite.
```
