# Context Conceptual Recall Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Close the conceptual `context` recall gap so natural-language questions can surface the answering implementation symbols lexically (with optional semantic help), without false `sufficient` confidence and without polluting symbol ranking.

**Architecture:** Keep symbol search as `name + signature` only. Expand what enters `context` pivots via (1) ranking/evidence tiers for existing arms, (2) a bounded source/doc content rescue that maps corpus hits to containing symbols, (3) competitive optional semantic seed strength, and (4) resolved one-hop test-subject promotion. All new pivot reasons remain discovery-tier for disposition (`partial` only) unless they already qualify under existing authoritative rules. Spec and live proof: [`docs/findings/2026-07-27-context-conceptual-recall-gap.md`](../findings/2026-07-27-context-conceptual-recall-gap.md).

**Tech Stack:** .NET 10, C#, xUnit, Miller `ContextTool` / `ContextPivotRanker` / content corpus (`ITextContentSearchIndex`), optional semantic seeds, `docs/contracts/context-json-v1.md`.

**Architecture Quality:** Changes stay inside the existing `context` tool pipeline (`BuildCandidates` → `ContextPivotRanker.Rank` → pack/render). No new MCP tools. `Miller.Core` stays free of I/O. Content rescue and test-subject promotion are Server-side candidate seeds, not symbol-index pollution. Main risk: over-promoting discovery hits into authoritative disposition or C#-only graph behavior — mitigate with discovery-tier reasons, exact-resolution-only promotion, and language-parity evidence before enabling test-subject promotion by default.

## Global Constraints

- Spec source of truth: `docs/findings/2026-07-27-context-conceptual-recall-gap.md` (live re-verified 2026-07-27).
- **No** folding doc comments, string literals, or source body text into **symbol** BM25/FTS ranking (`SearchableDocument` stays name + signature).
- **No** requiring the semantic arm. `MILLER_SEMANTIC=off` remains zero-work with byte-identical lexical-only context output for the lexical path.
- **No** new MCP tools (MCP-stinginess). Prefer existing `context` behavior + CLI + contract docs.
- **No** global bans of tests, `scripts/`, constants, Python, or `eval/` as pivots.
- **No** raising the four-pivot cap as a substitute for better candidates.
- Disposition: discovery reasons (`query_term_*`, `source_rescue_*`, `semantic_rank_*`, `query_term_*_subject`) must **not** authorize `sufficient`; only existing authoritative anchors + `query_rank_*` with real implementation bodies may.
- Value-declaration kinds (`constant`/`variable`/`field`/`property`) still never reach `sufficient` (`pivot_value_declaration_only`).
- Language parity: any feature that depends on resolved identifier edges must be proven on a real multi-language extract before default-on; silent C#-only success is a bug.
- Follow TDD: failing focused test → implement → pass. Do not weaken existing `ContextToolTests` / disposition tests.
- Guidance budgets (ADR-0001): do not grow `MILLER_AGENT_INSTRUCTIONS` core or tool description budgets for this work; contract + compact next_actions only.
- Fast suite remains default verification; Scale only for parity dogfood that needs a real extract/index.

## File Structure

| Path | Role |
| --- | --- |
| `src/Miller.Server/Tools/ContextTool.cs` | Candidate building, affinity tiers, source rescue load, semantic strength, test-subject promotion hook, next_actions |
| `src/Miller.Server/Cli/CliDispatch.cs` | Wire CLI context to content rescue (and parity with MCP) where seeds are loaded at the edge |
| `src/Miller.Core/Graph/ContextPivotRanker.cs` | Unchanged ordering keys unless a pure-tier test requires documenting existing `AnchorStrength` behavior only |
| `src/Miller.Indexing/ITextContentSearchIndex.cs` / `TextContentKind` | Existing content search surface for rescue |
| `docs/contracts/context-json-v1.md` | Document new reasons, discovery tiers, next_actions behavior |
| `docs/findings/2026-07-27-context-conceptual-recall-gap.md` | Mark plan link + post-ship status |
| `docs/README.md` | Map pointer to this plan |
| `tests/Miller.Tests/Server/ContextToolTests.cs` | Primary behavior tests (fixtures already have `SourceHit` helpers) |
| `tests/Miller.Tests/Graph/ContextPivotRankerTests.cs` | Only if ranker input contracts need explicit tier ordering proofs |

## AnchorStrength ladder (lock this)

Preserve explicit task anchors above all retrieval:

| Band | Strength | Reasons |
| --- | --- | --- |
| Explicit anchors | 65–100 | `entry_symbol` 100, `stack_frame` 95, `stack_symbol` 90, `edited_file` 85, `failing_test` 80, `ambiguous_entry_symbol` 70, `entry_file` 65 |
| Full-query symbol retrieval | `TaskQueryAffinity` (0–50) after path/name fix | `query_rank_N` |
| Source / doc rescue | **35** fixed | `source_rescue_N` |
| Semantic seed (when served) | **26** fixed | `semantic_rank_N` |
| Term rescue | `min(TaskQueryAffinity, 18)` | `query_term_<term>` |
| Test-subject promotion | same strength as the term-rescue hit it replaces | `query_term_<term>_subject` |

`TaskQueryAffinity` path weight must be **≤ name weight** (recommended: name **12**, path **8**, signature **5**, kind **15**, multi-name bonus unchanged, cap still 50).

## Dogfood acceptance (end of plan)

Query:

```
how does a derived sidecar prove which extract generation it was built from
```

With `MILLER_SEMANTIC=off` (lexical path):

- At least one pivot **or** top packed neighbour is `SymbolsArtifactIdentity`, `MatchesArtifact`, or `Unprovable` (or a containing type promoted from those).
- Disposition is **not** `sufficient` solely because of discovery-tier reasons.
- `next_actions` includes a `search mode=source` (or equivalent) affordance when pivots are value-declaration-only / discovery-weak.

With semantic on (optional report): semantic seeds may appear but must not be required for the lexical acceptance above.

---

## Verification Strategy

**Project source of truth:** `AGENTS.md` / `Claude.md` testing split; `docs/contracts/context-json-v1.md`; findings doc above.

**Worker red/green scope:**  
`dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~ContextToolTests|FullyQualifiedName~ContextPivotRankerTests"`

**Worker ceiling:** Focused context/ranker tests only. Workers do not run full fast suite, Scale, live dogfood CLI, or branch gate unless the task says so.

**Worker gate invariant:** New reasons, tiers, and disposition rules match this plan; existing disposition and pivot tests remain green.

**Lead affected-change scope:** After each coherent batch:  
`dotnet build Miller.slnx -c Release` and `scripts/test.sh` (fast suite + wall-clock budget).

**Branch gate:** `dotnet build Miller.slnx -c Release`, `scripts/test.sh`, and when Task 5 (test-subject / parity) or content/index wiring is involved also `scripts/test.sh scale` if any Scale-tagged test is added. Live dogfood CLI check for the acceptance query with `MILLER_SEMANTIC=off`.

**Replay/metric evidence:** Hard gate = dogfood acceptance query under lexical-only. Report-only = same query with semantic on.

**Escalation triggers:** Contract field renames, MCP description budget pressure, multi-language parity failure for promotion, any change to symbol search document text composition.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp for lead gates and dogfood.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Ranking tiers + path/name + term test policy | None - serial | Modify `ContextTool.cs`; tests in `ContextToolTests.cs` (+ ranker tests only if needed) | Yes | Foundation for all later strength comparisons. |
| Task 2: Discovery next_actions | None - serial | Modify `ContextTool.cs` render paths; tests in `ContextToolTests.cs` | Yes | Same file as Task 1; after tiers so reasons/dispositions are stable. |
| Task 3: Source/doc rescue pivots | None - serial | Modify `ContextTool.cs`, `CliDispatch.cs` (seed load); tests in `ContextToolTests.cs` | Yes | Depends on strength ladder (Task 1); main class fix. |
| Task 4: Semantic seed strength | None - serial | Modify `ContextTool.cs` `LoadSemanticSeeds`/`BuildCandidates` semantic AddSignal; tests in `ContextToolTests.cs` | Yes | Same affinity ladder; after or with Task 3 but serial due to same files. |
| Task 5: Test-subject promotion | None - serial | Modify `ContextTool.cs` (+ optional exact-outgoing helper); tests in `ContextToolTests.cs`; parity note in findings | Yes | Depends on term-rescue policy (Task 1); language-parity gate. |
| Task 6: Contracts, docs map, dogfood closeout | None - serial | `docs/contracts/context-json-v1.md`, findings, `docs/README.md`, optional agent-instructions only if budget allows (prefer not) | Yes | After behavior ships. |

Commit mode: `serial-worker-commit` per task after worker verification, or lead batches commits after inline review if using parallel-lead-commit — this plan is fully serial, so worker commits after each task are fine.

### Task 1: Ranking tiers + path/name + term-rescue test policy

**Files:**
- Modify: `src/Miller.Server/Tools/ContextTool.cs` (`TaskQueryAffinity`, full-query arm, per-term arm around `BuildCandidates` query loops ~815–862)
- Test: `tests/Miller.Tests/Server/ContextToolTests.cs`
- Optional: `tests/Miller.Tests/Graph/ContextPivotRankerTests.cs` only if proving pure strength ordering with synthetic signals

**Interfaces:**
- Consumes: `SearchTool.CollectSymbolCandidates`, `SearchTool.ResolveExcludeTests` / `IsNaturalLanguagePhrase`, `ContextPivotRanker.Rank`
- Produces: Full-query arm uses `TaskQueryAffinity` after path≤name fix; term arm uses `min(affinity, 18)` and `excludeTests` forced to the **parent query’s** auto policy (`ResolveExcludeTests(null, fullQuery, Symbol)`), not the one-word term policy; reasons unchanged (`query_rank_N`, `query_term_<term>`)

**Contract inputs:** AnchorStrength ladder in this plan; phrase = ≥2 words per `SearchTool.IsNaturalLanguagePhrase`.

**File ownership:** Modify `ContextTool.cs`; tests in `ContextToolTests.cs` (+ optional ranker tests)

**Serialization required:** Yes

**Dependency reason:** Foundation for all later strength comparisons.

**What to build:** Fix inverted path-vs-name weights; demote term rescue into its own strength band; make term rescue inherit the original NL query’s auto-hide-tests decision so one-word terms cannot reintroduce tests on conceptual queries.

**Approach:**
1. TDD: fixture where path token matches more strongly than name today (`eval/sidecar/...` vs name without path word) — after fix, name match outranks path-only peer when affinities would otherwise invert.
2. TDD: NL multi-word query + term rescue would have returned a test under old policy — after fix, auto-hide applies to term arm when parent query is NL without test intent.
3. TDD: synthetic or fixture where full-query hit and term-only hit compete — term-only cannot outrank equal/full affinity solely via restarting rank at 1 when strengths differ by band (term cap 18 vs full affinity >18).
4. Implement weight + cap + `excludeTests: parentPolicy` (compute once from full `query`).

**Acceptance criteria:**
- [ ] Path-only affinity ≤ name-only affinity for the same term set (path weight ≤ name weight).
- [ ] Term-rescue pivots use strength ≤ 18; full-query `query_rank_*` still uses uncapped `TaskQueryAffinity` (≤50).
- [ ] On NL queries without test intent, term rescue does not reintroduce test pivots via one-word auto policy.
- [ ] Existing context tests still pass.
- [ ] Worker-scope verification passes; commit or hand off per commit mode.

### Task 2: Discovery-aware next_actions

**Files:**
- Modify: `src/Miller.Server/Tools/ContextTool.cs` (JSON + compact next_actions near disposition render ~1820 and compact equivalent)
- Test: `tests/Miller.Tests/Server/ContextToolTests.cs` (extend value-declaration disposition test)

**Interfaces:**
- Consumes: `DispositionFor` / `pivot_value_declaration_only` and selected pivots
- Produces: When disposition is not `sufficient`, `next_actions` may include a source-search call in addition to (or instead of only) inspect-on-junk-pivots

**Contract inputs:** Existing `next_actions` shape `{ call, reason }`; query string available in render path (thread `query` into renderers if not already).

**File ownership:** Modify `ContextTool.cs` next_actions; tests in `ContextToolTests.cs`

**Serialization required:** Yes

**Dependency reason:** Same file as Task 1; after tiers so reasons/dispositions are stable.

**What to build:** For `partial` with `pivot_value_declaration_only` (and optionally other weak discovery-only bundles), emit a next action like `search(query=\"…\", mode=\"source\")` with reason that source/docs may hold conceptual language. Keep at least one inspect action only when a pivot is a plausible implementation kind; do not only point at constants.

**Approach:** Prefer deterministic rule: if every pivot fails `CarriesImplementation`, lead with source-search next action; else keep inspect-first but append source-search when disposition reason is `pivot_value_declaration_only`.

**Acceptance criteria:**
- [ ] Value-declaration-only bundle includes a `mode=source` (or CLI-equivalent) next action.
- [ ] `sufficient` bundles still omit `next_actions`.
- [ ] Worker-scope verification passes; commit or hand off per commit mode.

### Task 3: Bounded source/doc content rescue into pivots

**Files:**
- Modify: `src/Miller.Server/Tools/ContextTool.cs` (`Context` MCP entry, `RunActionable` / `BuildCandidates`, seed loader)
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` context branch (~2020–2075) to load rescue seeds when content.db is available
- Test: `tests/Miller.Tests/Server/ContextToolTests.cs` (use `SourceHit` + fixture symbols)

**Interfaces:**
- Consumes: `ITextContentSearchIndex.Search(query, kinds, limit, excludeTests)` for `TextContentKind.WorkspaceSource` (and optionally workspace docs if cheap); `TextContentSearchHit.ContainingSymbolId` / name; `ISymbolLookupIndex.FindBySymbolId`; `PreferDefinitionPivot`; `IsQueryPivot`
- Produces: Up to **3** discovery seeds with reason `source_rescue_N`, `AnchorStrength = 35`, admitted in `BuildCandidates` **before** `ContextPivotRanker.Rank`
- Optional injection: pass `IReadOnlyList<ContextSourceSeed>` into `BuildCandidates` like `semanticSeeds` (keep Core free of content I/O by loading seeds at MCP/CLI edge)

**Contract inputs:** NL queries only (`SearchTool.IsNaturalLanguagePhrase(query)`); inherit parent auto-test policy; skip when query empty; fail-soft if content index unavailable (no throw; no seeds).

**File ownership:** `ContextTool.cs`, `CliDispatch.cs`, `ContextToolTests.cs`

**Serialization required:** Yes

**Dependency reason:** Depends on strength ladder (Task 1); main class fix.

**What to build:** For natural-language context queries, run a small content/source search, map hits to containing symbols, and admit them as discovery-tier pivots so conceptual prose can reach the pivot set without changing symbol ranking.

**Approach:**
1. TDD: two symbols — wrong lexical name hit vs correct symbol whose **body/doc text** is only in a synthetic content hit with `ContainingSymbolId` set to the correct symbol. Context query is conceptual NL; expect correct symbol as pivot with `source_rescue_1` (or within top pivots).
2. TDD: `MILLER_SEMANTIC=off` / no semantic arm still gets the rescue (lexical content only).
3. TDD: non-phrase identifier query does **not** run source rescue (avoid changing symbol-lookup style context).
4. TDD: discovery reason does not flip disposition to `sufficient` even with a method body.
5. Implement seed loader: limit 6 content hits → map unique symbols → max 3 seeds; PreferDefinitionPivot; skip tests when parent policy hides tests; skip non-`IsQueryPivot` kinds.
6. Wire MCP: resolve text content via `IWorkspaceTextContentSearchProvider` if the workspace provider implements it (same pattern as `SearchTool`); else empty seeds.
7. Wire CLI: open content corpus path from extract db when present; else empty.

**Acceptance criteria:**
- [ ] Conceptual fixture query surfaces the content-mapped implementation symbol as a pivot with `source_rescue_*`.
- [ ] Symbol search index documents remain name+signature only (no product change to `SearchableDocument`).
- [ ] Missing content index degrades silently to today’s lexical symbol path.
- [ ] Discovery rescue alone ⇒ `partial`, never `sufficient`.
- [ ] Worker-scope verification passes; commit or hand off per commit mode.

### Task 4: Competitive optional semantic seed strength

**Files:**
- Modify: `src/Miller.Server/Tools/ContextTool.cs` (`BuildCandidates` semantic seed `AddSignal` currently strength `0` ~1007–1018)
- Test: `tests/Miller.Tests/Server/ContextToolTests.cs` (`Context_SemanticSeedAnchorsConceptualQueryWhenServed` and a new case where weak lexical affinity would previously bury seeds)

**Interfaces:**
- Consumes: existing `LoadSemanticSeeds` / `ContextSemanticSeed`
- Produces: `semantic_rank_N` signals with `AnchorStrength = 26` (not 0); still non-authoritative for disposition

**Contract inputs:** AnchorStrength ladder; ADR-0003 off-switch; admission policy unchanged (`SemanticQueryPolicy.DecideAdmission`).

**File ownership:** `ContextTool.cs`, `ContextToolTests.cs`

**Serialization required:** Yes

**Dependency reason:** Same affinity ladder; serial due to same files.

**What to build:** When semantic seeds are served, give them discovery-tier strength so they can displace pure path/name junk, without outranking real anchors or strong full-query lexical hits that score above 26, and without authorizing `sufficient`.

**Approach:**
1. Update/extend tests: with lexical junk affinity ~12–18 and a semantic seed for the true symbol, true symbol becomes a pivot when arm serves.
2. With `MILLER_SEMANTIC=off` / null arm, output remains lexical-only (existing guarantee).
3. Disposition: semantic-only implementation body remains `partial` (contract already states this — add regression if missing).

**Acceptance criteria:**
- [ ] Served semantic seeds use strength 26 and can enter the top-4 against low-affinity lexical noise.
- [ ] Semantic-off path unchanged (no seeds, no vector work).
- [ ] Semantic-only pivot ⇒ not `sufficient`.
- [ ] Worker-scope verification passes; commit or hand off per commit mode.

### Task 5: Test-subject promotion (resolved one-hop only)

**Files:**
- Modify: `src/Miller.Server/Tools/ContextTool.cs` (`BuildCandidates` after term-rescue signals, before `Rank`)
- Test: `tests/Miller.Tests/Server/ContextToolTests.cs`
- Findings: update parity status section in `docs/findings/2026-07-27-context-conceptual-recall-gap.md` when gate runs

**Interfaces:**
- Consumes: term-rescue test signals (`query_term_*` + `IsTest`); **exact** outgoing references only (`ReferenceEvidenceReader.ReadOutgoing` exact rows, or graph edges that are already exact-target only — prefer exact outgoing evidence with non-null target symbol id)
- Produces: When a term-rescue test hit has **exactly one** distinct non-test resolved target (optionally promote `ParentId` container type/class/struct if the sole target is a member and container is unique), replace the test pivot signal with the subject at the same strength and reason `query_term_<term>_subject`. If 0 or >1 subjects, keep the test signal unchanged.

**Contract inputs:** Only when parent query has no test intent (`!HasTestOrDefIntent`); only term-rescue path (not full-query test hits); never promote unresolved/name-fallback edges.

**File ownership:** `ContextTool.cs`, `ContextToolTests.cs`, findings parity note

**Serialization required:** Yes

**Dependency reason:** Depends on term-rescue policy (Task 1); language-parity gate.

**What to build:** Narrow lexical bridge: long test names that already match conceptual terms promote their single resolved subject into the pivot set instead of sitting as the pivot.

**Approach:**
1. TDD: test method symbol + exact outgoing edge to one production method; NL query term-rescues the test; expect production subject pivot with `*_subject` reason and no test pivot (or test demoted).
2. TDD: two exact outgoing targets ⇒ no promotion.
3. TDD: unresolved-only outgoing ⇒ no promotion.
4. TDD: query with test intent ⇒ no promotion.
5. Implement before `ContextPivotRanker.Rank`; need db path or precomputed outgoing map — thread optional `Func<string, OutgoingReferenceEvidenceSet>? readOutgoing` into `BuildCandidates` for testability (CLI/MCP supply `ReferenceEvidenceReader.ReadOutgoing`).
6. **Parity gate (hard before default-on):** on a real multi-language extract (or Scale dogfood), sample term-rescue test hits across languages julie-extract supports and report resolved-outgoing rate. If promotion quality is C#-skewed, keep promotion but document limitation and consider shipping only when `language` of the test symbol is in a proven set — prefer prove-all, not allowlist-by-default without evidence. Record results in the findings doc.

**Acceptance criteria:**
- [ ] Exactly-one resolved subject promotes; ambiguous/unresolved does not.
- [ ] Reason is `query_term_<term>_subject` and remains discovery-tier for disposition.
- [ ] Test-intent queries do not promote.
- [ ] Parity evidence recorded; no silent C#-only claim of general correctness.
- [ ] Worker-scope verification passes; commit or hand off per commit mode.

### Task 6: Contracts, docs map, dogfood closeout

**Files:**
- Modify: `docs/contracts/context-json-v1.md`
- Modify: `docs/findings/2026-07-27-context-conceptual-recall-gap.md` (status → implemented / residual risks)
- Modify: `docs/README.md` (plan pointer next to findings)
- Do **not** expand MCP tool descriptions unless a one-line next_actions contract requires it (prefer contract-only)

**Interfaces:**
- Consumes: shipped reasons and disposition behavior from Tasks 1–5
- Produces: accurate contract + findings status

**Contract inputs:** Reason strings: `source_rescue_N`, `query_term_<term>_subject`, strength/discovery rules, next_actions source-search behavior.

**File ownership:** docs files listed above

**Serialization required:** Yes

**Dependency reason:** After behavior ships.

**What to build:** Document the new candidate arms and next_actions behavior; mark the findings gap closed or partially residual; add this plan to the docs map; run live dogfood acceptance.

**Approach:**
1. Update contract disposition/reason section and next_actions notes.
2. Update findings status + link to this plan and verification ledger.
3. Lead runs: `MILLER_SEMANTIC=off` context on the dogfood query; record pivots/disposition/next_actions in findings.
4. Lead runs `scripts/test.sh` and Release build; Scale if Task 5 added Scale tests.

**Acceptance criteria:**
- [ ] Contract documents discovery reasons and that they do not authorize `sufficient`.
- [ ] Findings reflect shipped state and dogfood evidence.
- [ ] Live lexical dogfood acceptance query meets the Dogfood acceptance section.
- [ ] Branch gate green; verification ledger filled.

---

## Execution notes

- Prefer **razorback:subagent-driven-development** with **serial** tasks (shared `ContextTool.cs`).
- Required skills while implementing: razorback:test-driven-development, razorback:requesting-code-review (inline after each task), razorback:verification-before-completion before claiming done.
- Do not start implementation until this plan is explicitly **approved**.
