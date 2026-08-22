# Context tool latency diagnosis (2026-08-21)

Read-only diagnosis of why the `context` MCP tool is slow, run against live telemetry
(`~/.miller/telemetry.db`), the phase logs (`.miller/logs/*.jsonl`), the live store
(read-only), and CLI probes on the Debug binary at `1.20.1+41e597a96ce4`. All fixes below
were approved by the user on 2026-08-21. The resolve-side causes (exact-stamp sidecar
rejection, refresh-first cross-workspace wait, CLI whole-generation fact load) are covered
by a separate workflow and are NOT re-diagnosed here — this document covers context's OWN
work after resolve (~6.1s of its 6.9s current-workspace p50).

## Headline

The dominant cost was a **whole-index scan that produces nothing**: every context call loaded
"supplemental edges" by scanning every test symbol in the view and parsing their JSON
metadata for test-linkage keys that **no store on this machine has ever contained**. This is
the same defect pattern the store's materialized resolution was replaced for (query-time
resolution, 2026-08): work proportional to the index, not to the question.

**Gated 2026-08-21** (commit `5b33eb82`): the linkage scan now runs only where an existence probe
proves a linkage key exists — nowhere, today. What remains under `supplemental` is the Blazor arm,
measured at 15 ms for the structural-fact read plus 273 ms for `BlazorComponentGraphReader`'s own
sorted `symbols` scan; that arm produces every edge the graph uses and is out of scope here.

## Latency split (telemetry)

`context` rows, all history (n=38):

| workspace | reference_mode | n | p50 ms | p90 ms | max ms |
|---|---|---|---|---|---|
| C:\source\miller | off | 27 | 6955 | 35514 | 147801 |
| C:\source\miller | usage | 4 | 38037 | 50323 | 50323 |
| C:\source\julie-extractors | usage | 4 | 113425 | 519811 | 519811 |
| C:\source\goldfish | off | 1 | 9104 | — | — |

Per-phase split, current build, since 2026-08-19 (18 calls, from the phase logs — a true
split, not an estimate):

| phase | n | p50 | p90 | max | sum ms | share |
|---|---|---|---|---|---|---|
| resolve | 18 | 3680 | 4633 | 8728 | 49324 | 17.2% |
| semantic_seeds | 18 | 43 | 12600 | 13236 | 34737 | 12.1% |
| source_rescue | 18 | 582 | 1666 | 2604 | 12734 | 4.4% |
| query_retrieval | 15 | 17 | 6847 | 12931 | 20473 | 7.1% |
| term_retrieval | 15 | 7 | 727 | 803 | 2723 | 0.9% |
| anchor_resolution | 15 | 247 | 1197 | 4121 | 9620 | 3.3% |
| **graph_reach** | 15 | **3625** | **5753** | **7903** | **43915** | **15.3%** |
| symbol_hydration … bounded_render | 15 | 0–4 | ≤13 | ≤15 | 243 | 0.1% |
| bundle (`usage` calls only) | 18 | 0 | 13787 | 90602 | 113749 | 39.6% |

**The `bundle` row above is a pre-fix-8 measurement and has no successor under that name.** It timed the
WHOLE reference-aware branch because the branch reported no inner phases. Fix 8 gave it the same phases the
off branch has, so what is left at the end of a usage call is only the remainder after `bounded_render` —
near zero. A trend that kept reading `bundle` across that boundary would show a collapse that is a renaming,
not a speed-up. The usage branch's terminal stamp is therefore `bundle_remainder`; the off branch keeps
`bundle`, where it always meant the remainder.

Inside `graph_reach`, the graph statement log shows `supplemental` owns it: 35 statements,
sum 68,391 ms. Bimodal: 15 of 35 calls missed the cache and paid mean 4,447 ms; the rest
paid 0–667 ms. Every call returned exactly 24 edges for a 4-id frontier.

CLI probes (near-idle machine): `miller search` warms 7.8s → 850ms; `miller context` on the
same query is a flat 13.6s on every run (no warm-up); `--token-budget 100` costs the same as
2000; `--max-hops 0` saves ~5.7s; `MILLER_SEMANTIC=off` changes nothing.

## The five defects, with evidence

### 1. The supplemental-edge load is a whole-index scan for data that does not exist

Chain: `SqliteSymbolGraphIndex.SupplementalEdges()` (`SqliteSymbolGraphIndex.cs:659`) →
`ReadSupplementalEdges` (`:674`) → `TestLinkageReader.Read` (`TestLinkageReader.cs:11`) +
`SqliteBridgeReader.ReadStructuralFacts` (`SqliteBridgeReader.cs:259`).

`TestLinkageReader.Read` runs, with no bound tied to the query:

```sql
SELECT symbol_id, metadata_json
FROM symbols
WHERE is_test = 1 AND metadata_json IS NOT NULL
ORDER BY symbol_id;
```

Measured against Miller's live store (4.6 GB, generation 1095, read-only):

| measurement | value |
|---|---|
| rows in `symbols` (whole family, all history) | 608,086 |
| symbols visible to the pinned view | 127,187 |
| rows the query returns | 7,128 |
| query time with `ORDER BY symbol_id` | **2,537 ms** |
| same query, `ORDER BY` removed | **178 ms** (14x) |
| query plan (current) | `SCAN s USING INDEX idx_read_symbols_symbol` (whole family table) |
| graph edges produced from those 7,128 rows | **0** |

The 7,128 metadata blobs were parsed the way the reader parses them: **no `test_linkage` or
`test_coverage` key exists in any of them.** A follow-up probe ran the discriminating query
against **every store family on this machine** (Miller, julie-extractors, goldfish,
razorback, one other; 3 to 26,590 test symbols each): **zero linkage rows in all five.**
julie-extract does not emit this metadata. The reader is dead code paying ~4.4s to return
nothing. The keys that DO exist: `is_test`, `isStatic`, `decorators`, `isAsync`,
`returnType`, `markdown_kind`, `info_string`, `language`, `test_lifecycle`.

**The cache does not save you.** `GetOrAddSupplementalEdges`
(`WorkspaceIndexProvider.cs:1346`) keys on `KeyFor(workspaceId, snapshot)` (`:1513`), which
folds `StoreLogSequence`, `ManifestHash`, `Revision`, `SearchStamp`, `ContentStamp`,
`VectorStamp` — **any single converged file change invalidates it** (same trap the comment
at `WorkspaceIndexProvider.cs:520-523` records for the search sidecar). Measured hit rate
during active development: 57% (15 misses / 35 calls).

**The CLI never caches at all.** `CliDispatch.cs:3801` builds `new
SqliteSymbolGraphIndex(session)` with no `loadSupplementalEdges` delegate, so every CLI
process pays the full load — the flat 13.6s.

**Shipped 2026-08-21 (fixes 1, 2, 3, 7).** The linkage scan is now gated by a `LIMIT 1`
existence probe, the `ORDER BY` is gone, the cache key is the manifest identity, and the edge
endpoints resolve in one batched statement. Re-measured read-only against the live store the
same day (32,436 test symbols carry metadata now, up from 7,128): the old sorted scan 2,978 ms,
the same scan unsorted 220 ms, the existence probe 206 ms and zero JSON parses. What remains
under `supplemental` on a cache miss is the Blazor arm — 15 ms for the fact read plus 273 ms
for `BlazorComponentGraphReader.ReadEvidence`'s own sorted `symbols` scan (34,021 rows), which
is NOT dead code and is out of scope here. The CLI keeps its null delegate on purpose: a
one-shot process can never hit a cross-call cache, and both paths compute the same edges.

### 2. N+1 term-rescue reads drag in the whole-generation fact load under reference_mode=off

`PromoteTermRescueTestSubjects` (`ContextTool.cs:2012-2024`) calls `readOutgoing(symbolId)`
once per promoted test symbol (up to `TermRescuePromotionReadLimit = 8`), one round trip
each. The batched `ReferenceEvidenceReader.ReadMany` exists and is used by the usage path
(`ContextTool.cs:243`) — this call site does not use it. It runs even when
`reference_mode=off`, and the first read pulls the whole-generation resolution load.
Measured: `context "search sidecar" --max-hops 0` = 671 ms (no promotion) vs `context
"workspace refresh" --max-hops 0` = 6,828 ms (promotion). Server-side, one
`anchor_resolution` phase hit 137,358 ms while its symbol-lookup delta was 220 ms.

### 3. Throwaway retrieval before a cheap gate

`LoadSemanticSeeds` (`ContextTool.cs:514`) runs a full lexical retrieval at `:527`, THEN
checks `SemanticQueryPolicy.Route(query).IsHybrid` at `:537` and returns `[]` when not
hybrid — the retrieval is discarded. The pivot ranker at `:1565` retrieves the same query
again with a different `limit`, and `ContextSearchCacheLookupIndex` keys on
`(query, limit, mode)` (`ContextSearchCacheLookupIndex.cs:19`), so it misses the cache.

### 4. 48 wasted existence probes per call

`BatchNeighbourEvidence` (`SqliteSymbolGraphIndex.cs:453-467`) calls `Contains(edge.To)` /
`Contains(edge.From)` per supplemental edge. `_symbolExistsCache` is per graph instance and
`ResolveFamilyStoreGraph` builds a new instance per call, so the cache always starts empty:
48 `SELECT 1 FROM symbols` round trips for 24 edges — the 150–667 ms floor on cache-hit
calls.

**Shipped 2026-08-21 (fix 7, commit `5b33eb82`).** One batched `IN (...)` statement per 500
endpoints replaces the point lookups. **Amended 2026-08-21 (review finding 3):** the prime is LAZY —
it runs on the first `Contains` miss against a supplemental endpoint, not at load time. The calls it
replaces were all short-circuited (`LoadDependents` never probes at all), so priming eagerly would
have put work proportional to the whole edge set onto queries that touch none of it.

### 5. The token budget is applied after all the work

`BoundFinalOutput` runs at `ContextTool.cs:304`, after all retrieval, graph, and body work.
Measured: budget 100 → 14.4 s; budget 2000 → 13.5 s.

### Plus: the usage branch is dark

`ContextTool.cs:213` (`reference_mode=usage`) passes no phase callback — no phase split
exists for the branch that owns the worst numbers in the dataset (113 s, 519 s
cross-workspace). Instrumentation is the prerequisite for diagnosing that tail.

## Approved fix plan (in order)

1. **Gate the linkage scan behind a costless existence probe** (`LIMIT 1` for the metadata
   keys): scan never runs where the data does not exist (everywhere, today). The feature
   stays intact for the day julie-extract emits linkage — per the language-parity rule that
   emission lands in julie-extractors across all languages first.
   **SHIPPED 2026-08-21** (`5b33eb82`). **Hardened after review (finding 1):** raw text alone is
   not a superset of what the parser accepts. `JsonDocument.TryGetProperty` compares the UNESCAPED
   property name, so a blob that writes one letter of the key as a JSON backslash-u escape produces
   an edge no `LIKE` can see — the gate would fail CLOSED and drop every edge silently. Such a
   spelling must contain a backslash, so the probe keeps the two `LIKE` arms as a cheap prefilter
   and adds a `json_valid`/`json_type` check for backslash rows only. Re-measured read-only against
   the live store (2026-08-21): 32,594 test symbols carry metadata and **none of them contains a
   backslash**, so the parsed arm runs for no row at all — old probe 199/199/204 ms, new probe
   202/203/200 ms over three passes. An in-memory row with the escaped spelling opens the new gate
   and did not open the old one.
2. **Drop the pathological `ORDER BY`** from `TestLinkageReader.cs:22`. Measured 14x on the same
   result set; worth it even with the gate.
   **SHIPPED 2026-08-21** (`5b33eb82`) — and the sort was dropped OUTRIGHT, not moved into memory
   as this line first proposed. Justification, verified before shipping and pinned by a test after
   review (finding 4): both consumers fold these edges into a per-neighbour dictionary under the
   same total tie-break over (kind priority, source priority, confidence, source, kind)
   (`SqliteSymbolGraphIndex.AddEdge`/`CompareEdges`, `SymbolGraph.AddNeighbour`/`CompareEdge`) and
   emit neighbours sorted by neighbour id, so two edges a row-order swap could exchange are equal
   in every field that reaches `GraphNeighbour`. `LoadDependencies`/`LoadDependents` collect into a
   `SortedSet`. The test writes the same linkage rows in both orders and asserts identical
   `ReachWithEvidence` output.
3. **Fix the supplemental-edge cache key**: the edge set depends on test symbols and bridge
   facts only; `StoreLogSequence`/`SearchStamp`/`ContentStamp`/`VectorStamp` do not belong
   in the key.
   **SHIPPED 2026-08-21** (`5b33eb82`). The key is the manifest identity plus the index level and
   its three level stamps. The level stamps are what catches a level completion — 10,480
   `version_level_completed` rows against 1,480 `manifest_flipped` in this store's log, so it is the
   most frequent invalidation event there is, and it moves NO manifest field. Pinned by a test after
   review (finding 2).
4. **Batch the term-rescue reference reads** via `ReferenceEvidenceReader.ReadMany` (same
   bounds record as the usage path).
   **SHIPPED 2026-08-21.** The promotion set is read in ONE call: `BuildCandidates` now takes a batched
   `readOutgoingMany` delegate instead of a per-symbol one, and the promotion loop reads the whole ordered
   set (≤8) up front. Eight promoted tests cost 4 `ResolveQuery` scratch builds instead of 16, and a
   promotion set that is empty reads nothing at all. The delegate replaced the single-symbol one rather than
   sitting beside it — that call site was its only consumer, so nothing can quietly reintroduce the N+1.
   The reference-aware (`usage`) path batches only under the existing `MILLER_CONTEXT_REFERENCE_BATCH`
   opt-in, the same gate `BuildReferenceItems` reads; with the opt-in off it keeps its per-symbol reader.
   **Amended after review (findings 3, 4 and 6):** the batch reads OUTGOING evidence only, through the new
   `ReferenceEvidenceReader.ReadOutgoingMany`. The first version called `ReadMany`, which also runs the whole
   inbound resolution pass plus a name lookup and a same-name `COUNT(*)` per symbol — work this path discards,
   paid on the `reference_mode=off` path the batch exists to make cheaper. No before/after latency was
   measured for this fix, and none is claimed: the counter tests prove one read where there were eight, not a
   time. `ReadMany` also threw `ArgumentException` for any id missing from `symbols`, and one read for the
   whole set turned that into a denial of all ≤8 promotions where the per-symbol reader dropped only the
   offending id — reachable in normal operation, because the promotion ids come from the search sidecar and
   inspect/context now serve a readable LAST-GOOD sidecar that can name a symbol the served view no longer
   has. `ReadOutgoingMany` filters the ids against the snapshot first and leaves an unresolvable one ABSENT
   from the result. `MILLER_CONTEXT_REFERENCE_BATCH` deliberately does not reach this path: that switch picks
   between two live implementations on the usage path, while here the batch replaced its only caller, so a
   switch could only restore the N+1 this fix removed.
5. **Move the hybrid-route check above the lexical retrieval** in `LoadSemanticSeeds`
   (`:537` above `:527`) — work that cannot reach the output must not run.
   **SHIPPED 2026-08-21.** A lexical-route query now performs zero retrievals in the seed phase, pinned by a
   test that reads the measured index's search count at the `semantic_seeds` phase boundary.
6. **Share one retrieval** between the semantic seed check and the pivot ranker (retrieve
   once at the larger limit; ranking output must stay byte-identical — assert it).
   **WITHDRAWN 2026-08-21 (review finding 1). The two retrievals cannot be shared; the seed gate keeps its
   own limit of 2.** Two separate obstacles, found in that order:
   - The gate asks with `excludeTests` from the reference mode (false under `reference_mode=off`) while the
     ranker asks with `SearchTool.ResolveExcludeTests`, which hides tests for an ordinary phrase query.
     Computing the gate's `LexicalEvidence` over a test-hidden population flips admission and changes the
     bundle. The first attempt kept each side's own test policy and moved only the LIMIT.
   - Moving the limit is not byte-identical either, and that is the decisive one. The gate keeps its
     retrieved ids as `lexicalIds`, the membership set that admits a semantic hit whenever admission is
     `RerankOnly` (≥2 hits, top ≥ 1.25× runner-up — a common shape on a real repo).
     `CollectSymbolCandidates` does NOT truncate to the limit: the limit only sets the over-fetch window
     (`min(limit*4+10, 500)`), so limit 2 returns 18 rows and limit 10 returns 43 on the same 43-symbol
     query. A semantic hit on a symbol only in the wide set is dropped at 2 and admitted at 10, and the
     rendered bundle changes. The residual-risk note first written here named the hit bucket and the score
     ratio and MISSED this mechanism; both stay identical across the two windows, `lexicalIds` does not.
   What remains of this fix is the per-call memo (`ContextQueryRetrieval`, keyed on query+limit+excludeTests
   over ONE index), which removes the exact duplicate retrievals — chiefly the term-rescue retrievals the
   promotion path repeats. The gate and the ranker each pay for their own window, as they did before. The
   claim of byte-identical rendering under a shared window is withdrawn, and the three A/B tests that carried
   it were replaced: they used 3–6-symbol fixtures, where both windows return every symbol, and they compared
   a hand-copy of the gate rather than the gate. `Context_RerankOnlyAdmission_RefusesASemanticSeedOutsideThe
   NarrowSeedWindow` drives the real `ContextTool.Context` over a 43-symbol fixture, proves the fixture
   discriminating (admitting the far seed DOES change the bundle), and fails if the gate's limit is widened.
7. **Batch the edge-endpoint existence probes** (or hoist the cache) — kept, because fix 1 removes
   only the linkage arm and the Blazor arm still puts 24 edges on the path.
   **SHIPPED 2026-08-21** (`5b33eb82`), amended after review (finding 3) to prime lazily on the
   first endpoint miss instead of at load time.
8. **Instrument the `usage` branch** with the same phase callback the off branch has, so the
   cross-workspace tail becomes diagnosable.
   **SHIPPED 2026-08-21.** `RunReferenceAwareActionableWithCancellation` takes the same phase callback and
   emits the candidate-build phases (`query_retrieval` … `candidate_ordering`), plus `candidate_build`,
   `pivot_bodies`, the branch's own `reference_items`, `candidate_pack`, and `bounded_render`. Same log
   channel, no change to the tool's output contract.
   **Amended after review (finding 5): one telemetry key DID change meaning and was renamed for it.** The
   terminal `bundle` stamp used to time the whole usage branch (the 39.6% row in the latency table above);
   with inner phases reporting, it measures only the leftover after `bounded_render`. A usage call now stamps
   `bundle_remainder` instead, so no trend reads a rename as a collapse. The off branch, where `bundle`
   always meant the leftover, keeps the name.

**Deferred on purpose:** skipping test-subject promotion when `reference_mode=off` — it
changes ranking; fix 4 may make it moot. Measure after fix 4, then decide.

## Known exposure accepted with serve-then-refresh (recorded 2026-08-21)

A cross-workspace read with an explicit `workspace_id` now serves the pinned view and refreshes
behind it (`WorkspaceRefreshMode.Background`). That keeps the read session OPEN while the refresh
it started runs, **in the same process**. If that refresh promotes a full rebuild,
`FullRebuildPromotion` replaces `symbols.db` against this process's own open SQLite handle, and
Windows does not open with `FILE_SHARE_DELETE` — so the promote falls back on its retry loop
against a reader it cannot see as remote. The blocking arm could not produce this shape: the
refresh always finished before the read opened anything.

Why it is accepted rather than fixed:

- The automatic path runs a DELTA (`bypassBackoff: false`, no force intent), which promotes
  nothing. A promote needs a force intent — extractor upgrade, schema/corruption heal, or a user
  rebuild — which is rare behind a cross-workspace read.
- Deferring the background work while a read session is open would defer nearly every refresh,
  since the session is open for exactly the call that starts it.
- The promote retry loop already exists for held handles, and `MILLER_PROMOTE_RETRY_TIMEOUT`
  raises its budget (seconds, or a `TimeSpan`).

**If a promote-failure report on Windows traces back to a cross-workspace read, this is the
mechanism** — raise `MILLER_PROMOTE_RETRY_TIMEOUT` first, and consider raising the default rather
than re-diagnosing it from scratch. The same note is on
`WorkspaceIndexProvider.StartBackgroundRefresh`.

## Open questions (with discriminating experiments)

1. ~~Does julie-extract ever emit `test_linkage`/`test_coverage`?~~ **ANSWERED: no.** Zero
   rows across all five store families on this machine (probe 2026-08-21).
2. ~~Where do the 24 supplemental edges come from?~~ **ANSWERED: all 24 are Blazor
   component-reference edges from the dashboard's `.razor` files.** The earlier probe spelled
   the pattern id `blazor.component.reference`; the emitted id is
   `blazor.component_reference.v1` (`BridgeStructuralPatterns.BlazorComponentReference`), so
   it matched nothing. Counted read-only against the live store at the pinned view
   (`e32fd74f…`, generation 1344) on 2026-08-21: **26** visible `blazor.component_reference.v1`
   facts over 8 `.razor` files; 24 name a tag that resolves to a visible razor-component class
   symbol, and the 2 that do not are `<AntiforgeryToken>`, an ASP.NET framework component with
   no workspace symbol. `BlazorComponentGraphReader` emits one `uses` edge per resolvable fact
   and drops the rest — **24**, matching the count every call reported.
   **Consequence for fix 1:** only the TEST-LINKAGE arm of `ReadSupplementalEdges` is dead; the
   Blazor arm carries every edge the graph actually uses and must keep running. The gate is on
   the linkage scan alone.
3. **What causes `FindByName` bursts of 468 calls at 13.0 s?** (Seen twice on 2026-08-20,
   ~27.8 ms/call; other calls do 98 calls at ~0 ms.) Hypothesis: the in-memory projection
   answered instead of the FTS sidecar — would be a search-sidecar availability bug, not a
   context bug. Experiment: log which lookup index answered (`IsSidecar`) beside the phase
   delta.
   **EXPERIMENT SHIPPED 2026-08-21, still unanswered — it needs the next burst.** Every
   family-store read now claims a `SymbolLookupBackend` where the index is chosen
   (`WorkspaceIndexProvider.MeasureFamilyLookup`), and the claim rides on the measuring wrapper
   that already counts the calls, so the count and the index that produced it cannot come apart.
   Two surfaces carry it: the `Context lookup phase …` log line reports
   `ContextLookupBackend` beside the `FindByName` delta, and the read telemetry metadata carries
   `read_lookup_backend` beside `read_lookup_count`/`read_lookup_ms`. Four values, no guessing:
   `search_sidecar`, `session_projection`, `lagging_sidecar` (the sidecar supplied the recall and
   every row was re-read from the live artifact — the read pays both costs, so it is not filed
   under the plain sidecar), and `mixed` when one wrapper served two backends. An undeclared
   wrapper reports `unattributed` rather than a guess. The hypothesis is confirmed if the next
   burst reads `session_projection`.
4. **What makes cross-workspace `usage` calls cost 113–519 s?** Fix 8 is the experiment.
   **PARTIAL ANSWER (2026-08-21, first instrumented runs at HEAD `3c8ab6ce`):** two identical
   MCP calls (`reference_mode=usage`, workspace `julie-extractors`) cost 27.8 s then 21.1 s.
   The tail is one phase: `reference_items` at 17.1 s cold and **16.2 s warm — a per-call
   cost, not a one-time load**. Everything the caches cover got cheap on the second call
   (`resolve` 3.1 s → absent, `anchor_resolution` 5.3 s → 1.1 s, `graph_reach` 1.9 s →
   0.4 s), so the usage-branch enrichment itself does ~16 s of work per call on this
   generation. The same query from the one-shot CLI (bounded facts) finished in 8.3 s total.
   Next experiment: sub-phase the `reference_items` arm (fact-slice reads vs identifier
   streaming vs rendering) to name the 16 s.
5. ~~Does the CLI `inspect --depth overview` / `trace refs` ~4.9 s drop once the
   bounded-fact-cache fix lands?~~ **ANSWERED: yes.** Measured at HEAD `3c8ab6ce` on
   2026-08-21 (Release binary, warm store): `inspect ContextTool --depth overview` 1.08 s,
   `trace refs ContextTool` 1.25 s. The ~4 s whole-generation load is gone; the remaining
   ~1 s is process start plus the bounded per-file read.

## Related

- Inspect-latency diagnosis (same day, separate workflow): resolve-side causes — exact-stamp
  sidecar rejection in `ResolveFamilyStoreLookup`, refresh-first cross-workspace wait in
  `ReadToolWorkspaceRouting`, CLI whole-generation `RevisionFactCache` load.
- Design rule (user-affirmed twice): prefer query-time reads over whole-set precompute; a
  cache is an optimization on top of a bounded read, not the mechanism that makes an
  unbounded read tolerable. Follow-up: once the CLI bounded reference read proves
  byte-identical, consider retiring the server's whole-generation `RevisionFactCache` load
  behind the same bounded path.
