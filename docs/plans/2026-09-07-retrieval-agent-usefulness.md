# Retrieval agent usefulness implementation plan

**Status — 2026-09-08:** Source implementation and corrective dogfood work are recorded in the [retrieval verification ledger](../findings/2026-09-08-agent-usefulness-dogfood.md#retrieval). R7 producer changes are verified in source, with released producer/Miller pin adoption still pending approval. Performance conclusions apply only to the measurements and snapshots named in that ledger. The checklist below preserves the original acceptance criteria; use the ledger for current verified and outstanding status.

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make search, context, patterns, and imported-content answers reflect the evidence they actually contain, with bounded useful output.

**Architecture:** Keep classification and ranking policy in Miller.Core, SQLite filtering in Miller.Indexing, and contract/rendering changes in the existing tools. Keep framework recognition in julie-extractors. Extend existing contracts; add no MCP tool.

**Tech Stack:** .NET 10, C#, SQLite FTS5; Rust/tree-sitter in the separately owned producer.

**Architecture Quality:** Moderate risk: ranking, context evidence, and text pagination affect shared contracts. Preserve deterministic ordering, lexical-only behavior, source freshness guards, and bounded work. Avoid rewriting the large tools or introducing a second retrieval pipeline.

## Global Constraints

- Planning only was requested; this document does not authorize implementation, pushes, releases, or changes to unrelated dirty files.
- `Miller.Core` is pure logic with ZERO I/O deps.
- `MILLER_SEMANTIC=off` is a permanent zero-work guarantee; lexical-only output stays byte-identical where semantic-only score metadata changes.
- A feature built on `julie-extract` data must cover every supported language where the construct applies, and document non-applicable language/construct pairs. Validate per-language coverage on a real extract before shipping.
- No new MCP tools. Additive parameters and metadata must retain CLI/MCP contract consistency and budget tests.
- Preserve exact source positions, content hashes, existing filters, compact-only handoffs, and JSON compatibility unless a task explicitly identifies an additive contract change.
- TDD for behavior changes: add the named regression, prove it fails, implement, rerun its focused class. No narration comments in tests.

## Validated scope and corrections

Validation source: Miller `main`, commit `40f7c71a7703b369998903e5f9a536fda006da57`, `/home/murphy/source/miller`. Existing changes at entry: `docs/README.md`, `.memories/2026-09-07/`, and the input findings file. Source was inspected through Miller at revision 87715. Historical timings and corpus counts below were not remeasured.

| ID | Finding verdict and evidence | Plan task |
|---|---|---|
| S1 | Verified classifier defect: `ContentCorpusWriter.IsTestPath`, `src/Miller.Indexing/ContentCorpusWriter.cs:1093`, matches substrings `test`/`spec` and ignores its symbols argument. This falsely classifies `TestsCore.cs` and `InspectTool.cs`; the reported 114-row count is historical. `TestPathClassifier.Check`, `src/Miller.Core/Search/TestPathClassifier.cs:15`, already has segment/suffix rules. Phrase search is token coverage, not literal search (`FtsTextContentSearchIndex.cs:153-210`); a 2.5 token-phrase boost already exists at line 13/323. | 1, 2 |
| S2 | Verified representation mismatch: `SearchTool.FuseContentHits`, `src/Miller.Server/Tools/SearchTool.cs:3246-3282`, orders by local fused score then discards that value and returns the original lexical `Hit.Score`. `ProtectContentPrefix` at 3225 intentionally protects lexical leaders, so even RRF alone does not describe final order. | 2 |
| S3 | Partial: `CollectSymbolCandidates`, `SearchTool.cs:1564-1599`, already merges strict hits before OR fallback; fallback is reranked with `SymbolReranker`, not raw BM25 alone. `SymbolReranker.PhraseProximity`, `src/Miller.Core/Search/SymbolReranker.cs:190`, rewards complete ordered coverage but has no distinct partial-coverage tier. Historical `rescan` example needs a fixture before changing ranking. | 2 |
| S4 | Verified handoff shape, not claim that it can *only* return vocabulary: `CrossToolHandoff.SearchMarkerText`, `src/Miller.Server/Tools/CrossToolHandoff.cs:61`, intentionally finds literal mentions. Useful when marker facts are absent, misleading when a filter excluded existing facts. Do not invent other marker names without evidence. | 3 |
| S5 | Verified cap/alias/output contract: Search MCP maximum is 10 and `text` is a supported interpretation alias omitted from main prose. Exact-name searches observed an Other matches section. Replacing every useful alternative with a count would harm ambiguous discovery; suppress only low-signal alternatives after a decisive hit. | 3 |
| C1 | Partial, stronger than a bare body check: `DispositionFor`, `src/Miller.Server/Tools/Context/ContextBundleBuilder.cs:1708`, already requires implementation kind and authoritative reason; discovery-only implementations return partial. It does not consume anchor diagnostics or prove question coverage. A live broad validation query returned unrelated fixture pivots and `sufficient`. | 4 |
| C2 | Partial: edited files seed their own symbols at `ContextBundleBuilder.cs:610-630`, rank four pivots at 741. Callers can already appear as neighbors: `graph.Reach(... Direction.Both)` at 750. They cannot become edited-file-derived pivots. Test diversity at `src/Miller.Core/Graph/ContextPivotRanker.cs:70` can admit weak test evidence; the specific Python case remains historical. | 4 |
| C3 | Verified path gap: stack frame resolution uses raw frame path at `ContextBundleBuilder.cs:662-673`; `FtsSymbolSearchIndex.ResolveIndexedFilePath`, `src/Miller.Indexing/FtsSymbolSearchIndex.cs:812`, accepts exact indexed relative path or leaf name, not an absolute root-prefixed path. Aggregate unmatched-stack diagnostics already exist at builder lines 691-711; per-frame diagnostics do not. | 4 |
| C4 | Partial: ranker has anchor strength, distance, retrieval rank, diversity, tests, and pinning, but no kind or size field (`ContextPivotRanker.cs:41-90`). Builder filters kinds already; the proposed blanket constant/class penalty and arbitrary 200-line cutoff are not justified. Preserve exact named constants/classes, improve discovery member choice instead. | 4 |
| C5 | Unverified current performance regression: 8–13 s weekly averages and 1–2 s warm timings are historical. Do not infer projection caching as the sole cause; existing context lookup cache and phase telemetry must be measured. | 5 |
| P1 | Verified bounded metadata can omit values: `PatternsTool.MetadataPriority`, `src/Miller.Server/Tools/PatternsTool.cs:33`, prioritizes schema labels but omits generic value keys; `MetadataCompact` at 1845 selects only four keys unless filters require more. Route templates already have priority, so not every route loses its URL. | 6 |
| P2 | Verified producer recognition gap, counts historical: producer `crates/julie-extractors/src/base/framework_structural_facts/aspnet.rs:22` recognizes only MapGet/Post/Put/Patch/Delete. `collect_markup_framework_attributes`, `.../markup.rs:15`, iterates markup attributes, not Razor dictionaries. No MapMethods source hits in registered producer workspace. Producer was read-only; no real extract run. | 7 |
| T1 | Verified fixed truncation: `ContentTool.cs:1654,1850,2721` applies 160-unit truncation to compact/JSON; MCP also has a 160-byte bound. Truncation is reported, not silent. `context_lines=0` does not fix it. Unlimited single-line output would violate the response budget. | 8 |
| T2 | Verified missing search scope: `ContentTool.Execute`, `ContentTool.cs:174-183`, omits sourceId when calling Search; the parameter description currently promises read/shape/remove only. Hyphenated queries use token coverage and existing phrase boost, without a relaxation label. | 2, 8 |
| T3 | Partial: `ResolveContentSearchWorkspaces`, `ContentTool.cs:1241-1273`, already excludes error-state rows and selects Current/Ready/LoadedExisting. It does not check root presence. `SearchWorkspaces` at 493 already isolates failures and carries degraded coverage. The 36.8 s timing and 39 dead roots are historical, not a fresh latency proof. | 9 |

## Verification Strategy

**Project source of truth:** `CLAUDE.md` Testing and Build sections.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<TestClassName>"` for each named class below. Workers run only affected focused classes; fix failures without expanding silently to a suite.

**Worker gate invariant:** Tests prove source scoping before candidate limits, valid ranking/evidence labels, bounded lossless reads, stable existing filters, and honest partial coverage.

**Lead affected-change scope:** Run the union of affected focused classes after a coherent retrieval batch. Run `dotnet test` once at the completed task/branch boundary; do not rerun a green scope on an unchanged tree.

**Branch gate:** `dotnet build Miller.slnx -c Release` (zero warnings/errors), fast suite once, `scripts/test.sh scale` when producer/indexing paths change. Windows is required before a release, not required to write this plan.

**Security scope:** none declared; no dependency additions or external-model calls in this plan.

**Replay/metric evidence:** Output byte limits, source exclusivity, exact content reconstruction, and explicit-anchor correctness are hard gates. Historical latency and corpus counts are report-only until task 5 records a controlled baseline. No arbitrary wall-clock thresholds in unit tests.

**Escalation triggers:** Producer pin or extraction changes require actual per-language facts and Scale verification. Cache changes require concurrency, generation change, disposal, and bounded-memory tests plus Windows-compatible path cases.

**Verification ledger:** Record invariant, command, scope, commit, timestamp, result, and whether metrics are report-only. Reuse prior green runs on unchanged code.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| 1 classification | A | ContentCorpusWriter.cs, ContentCorpusWriterTests.cs, ContentCorpusSidecar.cs, ContentCorpusSidecarTests.cs | No | None - safe parallel batch. |
| 2 ranking | B | FtsTextContentSearchIndex.cs, ITextContentSearchIndex.cs, TextSearchQueryPlan.cs, ContentSearchIndex.cs, SymbolReranker.cs, SearchTool.cs, corresponding tests below | Yes | Finish classification; shared SearchTool changes serialize with task 3, shared content contracts with task 8. |
| 3 search presentation | C | SearchTool.cs, CrossToolHandoff.cs, SearchToolTests.cs, CrossToolHandoffTests.cs | Yes | Task 2 changes SearchTool. |
| 4 context relevance | A | ContextBundleBuilder.cs, ContextBundleRenderer.cs, ContextQueryService.cs, ContextPivotRanker.cs, ContextToolTests.cs, ContextPivotRankerTests.cs | No | None - safe parallel batch. |
| 5 context performance | D | Existing phase telemetry/cache files determined from measurement; no speculative cache implementation | Yes | Measure final task 4 behavior; coordinate shared cache work with edit-latency plan. |
| 6 pattern rendering | A | PatternsTool.cs, PatternsToolTests.cs | No | None - safe parallel batch. |
| 7 extraction parity | A producer | Producer aspnet.rs, markup.rs, framework registry and structural_facts.rs; Miller pin/update is later | No | None - safe parallel batch; pin adoption waits for producer verification/release authorization. |
| 8 content scope/read | C | ContentTool.cs, ContentCorpusExternalStore.cs, shared text-search interfaces/index from task 2, ContentToolTests.cs, FtsTextContentSearchIndexTests.cs, CliDispatch.cs, CliDispatchTests.cs | Yes | Shared index contract must settle in task 2. |
| 9 cross-workspace search | D | ContentTool.cs, ContentToolTests.cs, CliDispatchTests.cs | Yes | Task 8 owns the same files. |

Paths in this table are shorthand for the exact paths in the tasks. Commit mode: parallel-lead-commit; workers hand verified diffs to the lead. Documentation/contract edits are consolidated by the lead to avoid concurrent ownership.

## Task 1: Correct and repair persisted test classification

**Files:** `src/Miller.Indexing/ContentCorpusWriter.cs`, `src/Miller.Indexing/ContentCorpusSidecar.cs`, `tests/Miller.Tests/Indexing/ContentCorpusWriterTests.cs`, `tests/Miller.Tests/Indexing/ContentCorpusSidecarTests.cs`.

**Interfaces:** `ContentCorpusWriter.IsTestPath` delegates to existing `TestPathClassifier.Check`; remove unused symbols input if references permit. **Contract inputs:** source path; persisted content-corpus metadata. **Ownership/serialization:** batch A table.

Use existing test-path rules rather than copying heuristics. A fix only for newly written rows leaves existing same-revision corpora wrong: introduce a writer-policy version in corpus freshness, or an equivalent bounded migration, and rebuild/update classifications once for existing corpus rows. Cover both legacy and family-store builds. Do not bump the extractor just to repair Miller metadata.

- [ ] `src/InspectTool.cs`, `src/TestsCore.cs`, and production words containing spec/test remain visible; test directories and conventional test suffixes are classified consistently.
- [ ] Existing corpus at unchanged extractor revision converges to the new classification without an explicit user full rebuild.
- [ ] Focused `ContentCorpusWriterTests` and `ContentCorpusSidecarTests` pass.

## Task 2: Explain and rank lexical/semantic evidence honestly

**Files:** `src/Miller.Indexing/FtsTextContentSearchIndex.cs`, `src/Miller.Indexing/ITextContentSearchIndex.cs`, `src/Miller.Core/Search/TextSearchQueryPlan.cs`, `src/Miller.Core/Search/ContentSearchIndex.cs`, `src/Miller.Core/Search/SymbolReranker.cs`, `src/Miller.Server/Tools/SearchTool.cs`; tests `tests/Miller.Tests/Indexing/FtsTextContentSearchIndexTests.cs`, `tests/Miller.Tests/Server/SearchToolTests.cs`, `tests/Miller.Tests/Graph/ContextPivotRankerTests.cs` only if shared policy affects it. Add a focused `tests/Miller.Tests/Search/RetrievalEvidenceOrderingTests.cs` if existing ranking tests cannot isolate the policy.

**Interfaces:** Existing query-plan and hit contracts; add explicit match-evidence fields rather than replacing `score` semantics. **Contract inputs:** normalized query, literal source text, lexical score/rank, semantic rank, protected-prefix membership. **Ownership/serialization:** batch B.

Create deterministic fixtures: a literal hyphenated line, separated words, reversed words, stop-word variants, a repeated common term, two rare matching terms, semantic-only hit, and a protected lexical leader. Preserve exact literal queries and source filters. Introduce a literal-first tier for text retrieval; only label `relaxed=or` when actual OR evidence is served, not every nonliteral match. Distinguish token-AND from token-OR in diagnostics. Within symbol OR fallback, rank distinct meaningful query-term coverage ahead of score; retain strict results first and exact identifiers unchanged. Keep existing token-phrase boost where it helps within tiers.

For hybrid content JSON preserve lexical `score`, add ranking method/final rank and RRF score where available; protected lexical leaders must be explained as such. Do not call RRF the final scalar ordering when prefix protection changed it. Pure lexical outputs must not acquire semantic metadata. Hidden-test reporting must be bounded: report an exact count only when actually measured, otherwise say tests were excluded from a bounded candidate window. Do not scan the entire corpus simply to print an exact hidden total.

- [ ] Literal text precedes loose matches; semantic-only evidence remains discoverable without outranking authoritative literal matches.
- [ ] A one-term OR fallback cannot displace a two-term fallback solely by repetition; strict hits retain their prefix.
- [ ] Every relaxation and score field accurately describes the served ordering.
- [ ] Tests cover `exclude_tests` auto/true/false and corrected production filenames after task 1.
- [ ] Focused index/server/ranking tests pass; capture before/after representative lexical and hybrid query outputs.

## Task 3: Reduce search noise without losing useful recovery

**Files:** `src/Miller.Server/Tools/SearchTool.cs`, `src/Miller.Server/Tools/CrossToolHandoff.cs`, `tests/Miller.Tests/Server/SearchToolTests.cs`, `tests/Miller.Tests/Server/CrossToolHandoffTests.cs`.

**Interfaces:** `SearchMarkerText`, existing search descriptors/renderers. **Contract inputs:** requested limit versus effective limit, marker inventory/filter evidence, decisive exact hit. **Ownership/serialization:** batch C after task 2.

Print a compact clamp notice for limits above 10. Document `text` as the existing symbol-name alias; removing it would break clients. Replace low-signal alternative rows with a count only after a decisive hit; keep genuine same-name alternatives actionable. For filtered marker misses, report existing marker alternatives only when read from actual facts, preserving requested file/language/test filters. For a missing extraction/fact layer, retain the accurately labeled literal-source fallback. Keep these cross-tool nudges compact-only.

- [ ] Cap behavior is visible, alias remains compatible, useful ambiguity candidates remain available.
- [ ] Marker-empty fixture with other markers suggests only known alternatives; no-fact fixture makes no unsupported existence claim.
- [ ] Focused `SearchToolTests` and `CrossToolHandoffTests` pass, JSON next-actions remain unchanged for compact-only nudges.

## Task 4: Make context anchors constrain evidence claims

**Files:** `src/Miller.Server/Tools/Context/ContextBundleBuilder.cs`, `src/Miller.Server/Tools/Context/ContextBundleRenderer.cs`, `src/Miller.Server/Tools/Context/ContextQueryService.cs`, `src/Miller.Core/Graph/ContextPivotRanker.cs`, `tests/Miller.Tests/Server/ContextToolTests.cs`, `tests/Miller.Tests/Graph/ContextPivotRankerTests.cs`.

**Interfaces:** `BuildCandidates`, `DispositionFor`, `DispositionForReference`, renderer disposition calls, `ContextPivotRanker.Rank`. **Contract inputs:** normalized anchors, resolved/unmatched diagnostics, rendered body coverage, bounded caller evidence. **Ownership/serialization:** batch A.

Normalize absolute stack paths relative to the selected workspace before indexed lookup; do this at the workspace-aware boundary, not with filesystem I/O in Core. Handle Windows drive/UNC separators, spaces, and outside-root paths explicitly. Emit a bounded diagnostic for each unmatched frame. Do not accept a suffix from a different same-named file as an exact match.

Pass anchor evidence into both disposition paths. Unmatched explicit anchors must prevent sufficient; a rendered implementation is necessary, not proof a general why-question was answered. For unanchored discovery, use partial when relevance cannot be demonstrated. Preserve exact entry-symbol behavior and value-only partial results.

Let a bounded set of evidence-backed callers of edited file public symbols compete as pivots while retaining edited definitions and exact/fallback provenance. Use existing graph/reference services; avoid a new unbounded reference read per symbol. Improve discovery ranking with matching-member evidence and anchor-aware test admission. Do not globally demote large classes or constants: exact `HelpText` queries still need the value.

- [ ] Absolute and relative equivalent stack frames select the same symbol/body slice; unmatched frames are visible.
- [ ] A nonexistent explicit anchor plus an unrelated implementation cannot produce sufficient.
- [ ] Edited-file callers can be pivots, existing callers-as-neighbors behavior is retained, and unrelated tests cannot displace stronger anchor evidence.
- [ ] Large wrapper discovery chooses a matching member; exact constant/class queries remain correct.
- [ ] Focused `ContextToolTests` and `ContextPivotRankerTests` pass in ordinary and usage modes within the 2400-token MCP ceiling.

## Task 5: Diagnose context latency before choosing a cache

**Files to inspect, not automatically modify:** `src/Miller.Server/Workspaces/ContextSearchCacheLookupIndex.cs`, `src/Miller.Server/Workspaces/ReadPhaseTelemetry.cs`, `src/Miller.Server/Tools/Context/ContextQueryService.cs`; existing provider/cache implementation discovered from these references.

**Interfaces:** existing context phase observer and lookup cache. **Contract inputs:** identical workspace generation/revision, cold and warm calls, auto versus usage mode. **Ownership/serialization:** batch D, shared provider ownership coordinated with the edit-performance plan. The navigation/edit plan owns any shared immutable projection cache; this task reuses that owner rather than adding a competing cache.

Record phase costs for a small symbol query, broad why-query, edited-file query, and usage query, using the same pinned revision and explicit semantic setting. Separate projection loading, lookup, semantic inference, graph expansion, body reads, and formatting. Profile the dominant phase. Only if projection reload dominates, reuse a generation/revision-keyed immutable projection through the existing owner; never cache stale bodies or conflate one-shot bounded fact loading with resident sessions. If another phase dominates, bound or reuse that specific phase and retain cancellation and error semantics. Record the resulting implementation choice in this plan before editing cache code.

**Measured implementation decision, 2026-09-08:** the [pre-plan Release replay](../findings/2026-09-08-agent-usefulness-evidence/context-preplan-replay.json) compares `5308f3a7` with the repaired working tree on one pinned dataset: 48 calls spanning cold/warm symbol, broad, edited-file and usage queries. Warm median milliseconds were 324→309, 1341→1186, 546→626 and 1975→1652 respectively. Warm resolve cost was 14–24 ms in both builds; it did not dominate these calls. Usage reference work was 1691→1340 ms; edited-file graph calls increased 1→2 and lookups 677→727 with stronger anchor correctness. These observations do not establish a deterministic or universal latency improvement.

Retain the shared immutable projection owner and its verified identity, eviction, disposal and bounded-lifetime repairs from N6. Do not introduce a competing context cache: the measured warm bottleneck is not repeated projection construction. Preserve the additional edited-file evidence work as an explicit correctness tradeoff. The earlier [post-implementation comparison](../findings/2026-09-08-agent-usefulness-evidence/context-release-replay.json) has unrelated shared-host load and already contains the cache on both sides; it is descriptive evidence only. The original deterministic measured-work-reduction acceptance item below is not silently declared passed by these timings.

- [ ] A reproducible baseline and phase evidence replace historical averages as the decision input.
- [ ] Chosen change reduces the measured dominant work while semantic-off remains zero-work.
- [ ] Cache work, if justified, proves same-key reuse, generation invalidation, concurrent-load coalescing, disposal, and bounded lifetime; no guessed universal latency promise.

## Task 6: Preserve actionable pattern values in compact output

**Files:** `src/Miller.Server/Tools/PatternsTool.cs`, `tests/Miller.Tests/Server/PatternsToolTests.cs`.

**Interfaces:** `MetadataPriority`, `MetadataCompact`, `AppendMatchGroups`. **Contract inputs:** metadata object, requested metadata filters, page/group scope. **Ownership/serialization:** batch A.

Prioritize actual value/URL/route target alongside the name/key. Keep requested filter keys visible. Hoist schema constants only when equality is verified across the displayed group/page; label scope so a page-level constant is not presented as corpus-wide truth. Preserve JSON facts verbatim and byte limits. Long values need explicit bounded truncation, not silent omission.

- [ ] htmx URL, href, generic key/value, and route-template fixtures retain actionable values.
- [ ] Mixed framework/query-family fixtures are not incorrectly hoisted.
- [ ] Focused `PatternsToolTests` pass and compact stays within its budget.

## Task 7: Close producer route and attribute recognition gaps

**Producer files:** `/home/murphy/source/julie-extractors/crates/julie-extractors/src/base/framework_structural_facts/aspnet.rs`, `/home/murphy/source/julie-extractors/crates/julie-extractors/src/base/framework_structural_facts/markup.rs`, `/home/murphy/source/julie-extractors/crates/julie-extractors/src/base/framework_structural_facts/mod.rs`, `/home/murphy/source/julie-extractors/crates/julie-extractors/src/base/structural_fact_registry/framework/aspnet_node.rs`, `/home/murphy/source/julie-extractors/crates/julie-extractors/src/tests/structural_facts.rs`. Registry additions for any newly supported language shapes must be discovered through producer Miller before editing. **Miller adoption files:** `scripts/julie-pins.json`, focused structural-pattern consumer tests, and current pattern coverage documentation.

**Interfaces:** existing `aspnet.minimal_api.route.v1` and `htmx.attribute.v1` facts. **Contract inputs:** static route/method arguments, literal dictionary attributes, actual consuming markup/attribute spread. **Ownership/serialization:** independent producer batch; verified producer release precedes Miller pin adoption.

Add `MapMethods` recognition with static method arrays/collections, multiple verbs, route groups, and handler extraction from the correct argument position. Unknown verbs stay explicitly unknown; do not guess. Also verify `MapHead`/`MapOptions` support because the current five-method table omits them. Add dictionary/spread attribute recognition only with evidence the dictionary feeds markup; an arbitrary logging dictionary with an `hx-post` key must not become a route. Inventory analogous supported-language forms before implementation, and add fixtures for each applicable form. Coordinate interpolated htmx route work with the trace/bridge plan so producer metadata has one owner.

- [ ] Producer fixtures prove methods, route groups, dictionary consumption, unsupported/dynamic values, and negative string/comment cases.
- [ ] Per-language real-extract `SELECT language, kind, COUNT(*) FROM <table> GROUP BY 1,2` and structural-fact pattern counts document complete applicable coverage.
- [ ] Producer's documented focused tests pass; Miller consumes verified facts without owning a recognizer.
- [ ] Until the producer pin ships, public coverage describes the unsupported constructs honestly; pin restore and Miller Scale checks run before adoption completion.

## Task 8: Scope imported search and retrieve long lines safely

**Files:** `src/Miller.Server/Tools/ContentTool.cs`, `src/Miller.Indexing/ContentCorpusExternalStore.cs`, `src/Miller.Indexing/FtsTextContentSearchIndex.cs`, `src/Miller.Indexing/ITextContentSearchIndex.cs`, `src/Miller.Server/Cli/CliDispatch.cs`, `tests/Miller.Tests/Server/ContentToolTests.cs`, `tests/Miller.Tests/Indexing/FtsTextContentSearchIndexTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`.

**Interfaces:** `ContentTool.Content/Execute/Search`, index search contract, CLI content dispatch; proposed additive source filter and bounded read-width/continuation arguments. **Contract inputs:** source identity, workspace identity, line, width, content hash and byte budget. **Ownership/serialization:** batch C after task 2.

Pass `source_id` into search and apply it in SQL candidate selection before the 5000-candidate and output limits. Reuse source-id resolution and workspace-conflict checks from read/shape. Unknown sources and cross-workspace mismatches must be typed, not silently ignored. Define `all` plus one source as the source's owning workspace with explicit coverage, avoiding a registry-wide scan.

For `all`/`registered` plus a source, require the full workspace-qualified source ID. An unqualified or shortened ID needs an explicit workspace; never guess from primary/cwd or scan all corpora. Distinguish a missing owner, ambiguous short ID, and registry read failure. Existing `TryResolveSourceIdWorkspace` returning null for multiple causes is not sufficient evidence to choose a workspace.

Add `max_line_chars` with a documented bounded default and safe clamp. A zero-context single line may use the available response budget, but never unlimited output. Add a continuation bound to source identity/hash/line/offset so the remainder of one oversized line can be read exactly; preserve Unicode and label truncation in compact and JSON. Preserve legacy defaults, or explicitly document/test a deliberate default change. Update CLI parity and descriptors under existing budgets.

- [ ] A tiny new import remains discoverable inside one source when an older large log has stronger global matches.
- [ ] Source filtering occurs before candidate limits; wrong source/workspace fails clearly.
- [ ] No-primary, missing-owner, duplicate short-ID, and registry-read-failure fixtures never choose an implicit workspace or fan out to every corpus.
- [ ] Compact and JSON can reconstruct a long Unicode line exactly across pages; changed content invalidates continuation safely.
- [ ] Default reads stay bounded and existing line-window continuations still work.
- [ ] Focused `ContentToolTests`, `FtsTextContentSearchIndexTests`, and `CliDispatchTests` pass.

## Task 9: Avoid probing missing workspace roots and report coverage

**Files:** `src/Miller.Server/Tools/ContentTool.cs`, `tests/Miller.Tests/Server/ContentToolTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`.

**Interfaces:** `ResolveContentSearchWorkspaces`, `SearchWorkspaces`, existing coverage renderers. **Contract inputs:** registry candidates, canonical root presence, per-workspace open/search outcome. **Ownership/serialization:** batch D after task 8.

Retain current state filtering, check root presence before opening sidecars, and report skipped-missing, skipped-state, searched, and failed counts using existing coverage structures where possible. Distinguish not-found roots from access failures; race-time disappearance still enters isolated-failure handling. This is a read-only search: never retire registry rows or delete sidecars. Explicit workspace requests must report absence, not silently become an empty all-search.

- [ ] Missing ready rows cause zero sidecar opens; error-state rows remain excluded.
- [ ] One unreadable live workspace does not hide successful results from others; summary counts reconcile.
- [ ] Existing compact/JSON degraded coverage and CLI parity remain intact.
- [ ] Focused `ContentToolTests` and `CliDispatchTests` pass; compare all-workspace phase counts before/after without treating the historical 36.8 s as a hard threshold.
