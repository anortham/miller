# Task F1 — SemanticQueryPolicy — worker report

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-p3`
**Branch:** `worktree-semantic-p3` @ `6c25789`
**Dirty state at hand-off:** two untracked files, both owned by this task (plus impl-f2's files, untouched).
**Commit mode:** parallel-lead-commit — no `git add`, no `git commit` run.

## Files changed

| File | Status |
| --- | --- |
| `src/Miller.Core/Search/SemanticQueryPolicy.cs` | created (216 lines) |
| `tests/Miller.Tests/Core/SemanticQueryPolicyTests.cs` | created (40 test cases) |

Nothing else touched. `src/Miller.Indexing/Semantic/SemanticSearchArm.cs`,
`VectorConvergeService.cs`, and `tests/Miller.Tests/Indexing/SemanticSearchArmTests.cs` (impl-f2's
ownership) were never opened for edit.

## Implementation

`Miller.Core.Search` gains four pure public types, zero I/O, zero clock, zero index:

- `SemanticFusionClass { SymbolLookup, Conceptual, Mixed }` — the fusion profile F3 keys RRF weights on.
- `SemanticQueryReason { Empty, Short, IdentifierLike, PathLike, CodeSyntax, Prose, AmbiguousWeakLexical, AmbiguousStrongLexical }`
  — enum-only, so it is safe to persist under the "telemetry stays enum/counter-only, no query text" constraint.
- `LexicalEvidence(int HitCount, double TopScore, double RunnerUpScore)` — readonly record struct, `None`, `IsStrong`.
- `SemanticQueryRoute(bool IsHybrid, SemanticFusionClass HybridClass, SemanticQueryReason Reason)` — readonly record struct.

Entry point: `SemanticQueryPolicy.Route(string? query, LexicalEvidence? evidence) -> SemanticQueryRoute`,
plus `PolicyVersion = "policy-v1"` and `WireName(SemanticFusionClass) -> "symbol_lookup" | "conceptual" | "mixed"`.

### Decision ladder (first match wins)

1. blank ⟹ LexicalOnly / `Empty`
2. code punctuation `( ) { } [ ] ; = < > ! & | + * % : " ' \`` ⟹ LexicalOnly / `CodeSyntax`
3. path shape (`./`, `../`, `~/` prefix, or a `/`/`\` with no whitespace) ⟹ LexicalOnly / `PathLike`
4. single token ⟹ LexicalOnly / `Short` (≤3 chars) or `IdentifierLike`
5. prose (fusion class resolved to Conceptual, or any prose marker present) ⟹ **Hybrid** / `Prose`
6. otherwise **ambiguous** ⟹ `evidence.IsStrong ? LexicalOnly/AmbiguousStrongLexical : Hybrid/AmbiguousWeakLexical`

Fusion class is computed from word shape independently of the route, so F3 always has a class even on a
lexical-only route: all words identifier-shaped ⟹ `SymbolLookup`; no identifier-shaped word and
(≥5 words or a prose marker) ⟹ `Conceptual`; otherwise ⟹ `Mixed`. "Identifier-shaped" means the token
carries casing/punctuation prose cannot produce — an interior capital, `_`, `.`, or a digit — so
`indexing` is prose but `VectorSidecar`, `foo_bar`, `foo.Bar`, `Vector512` are not.

Constants (all versioned in Miller.Core alongside the policy): `PolicyVersion`,
`StrongLexicalDominanceRatio = 1.25`, `ShortQueryLength = 3`, `ProseWordCount = 5`.

## Judgment calls

### 1. The `LexicalEvidence` shape (asked for explicitly)

`LexicalEvidence(int HitCount, double TopScore, double RunnerUpScore)`, with
`IsStrong ⟺ HitCount ≥ 1 && TopScore > 0 && TopScore ≥ RunnerUpScore × 1.25`.

Why the runner-up field rather than the two-field `(HitCount, TopScore)` the brief sketched: Miller's
lexical score is **raw Okapi-BM25**, unbounded and corpus-dependent
(`src/Miller.Core/Search/Bm25.cs:43-61` — `Idf` is `ln(1 + (N-df+0.5)/(df+0.5))`, `TermScore` sums over
terms, then multiplicative exact-name boosts of 1.5/1.2/0.75). There is no existing normalized-confidence
value anywhere in the search path — I grepped `SearchTool.cs` for threshold/weak-score logic and the only
hit is `LooksLikeWeakIdentifierQuery`, which is a *query-shape* test, not a score test. An absolute
`TopScore >= 0.35`-style cut would therefore be unprincipled and would drift with corpus size.

Comparing the top hit to the runner-up is **scale-free** — a ratio, so it means the same thing on a
200-symbol repo and a 500k-symbol one — and it answers the actual question the ambiguous branch asks:
"did lexical already produce a clear winner?" A flat score cloud (12 hits, 3.0 vs 2.9) reads as weak
and earns the semantic arm; a dominant hit (9.0 vs 2.0) does not. `HitCount` is still load-bearing:
zero hits is weak regardless of score. Cost to F3 is one extra field it already has in hand at the
executor seam (the ranked list's `[0]` and `[1]` scores).

### 2. Existing query-shape helpers — reported, not duplicated (plan mismatch)

Miller *does* already have this logic, and it is all **private static** inside
`src/Miller.Server/Tools/SearchTool.cs`:

| Helper | Line |
| --- | --- |
| `QueryShapeFor` (internal) | `SearchTool.cs:510` |
| `LooksLikePathQueryShape` | `SearchTool.cs:1771` |
| `LooksLikeSourceCodeQuery` | `SearchTool.cs:1795` |
| `LooksLikeDocsOrConfigQuery` | `SearchTool.cs:1802` |
| `LooksLikeWeakIdentifierQuery` | `SearchTool.cs:1813` |
| `IsNaturalLanguagePhrase` | `SearchTool.cs:1833` |
| `IsPathLikeQuery` (index-dependent) | `SearchTool.cs:1870` |
| `PathQueryExtensions` | `SearchTool.cs:86` |

`Miller.Core` cannot reference `Miller.Server` (the dependency runs the other way), so **none of these
is reusable from where F1 must live**. Per the brief's instruction I am reporting this rather than
silently replicating it, and I did not lift them into Core: moving `QueryShapeFor` and friends out of
`SearchTool` is a real refactor of a 112KB file that F3/F4 are about to touch, and doing it from a
parallel worker would collide. **Decision:** define the policy on primitives Core already owns and take
the query string apart locally.

Two consequences the lead should weigh:

- `HasCodeSyntax` and `IsPathShaped` in the policy are *deliberately convergent* with
  `LooksLikeSourceCodeQuery` / `LooksLikePathQueryShape` — the punctuation set is identical (so the two
  classifiers cannot disagree about `count < limit`), but the path test drops the
  `PathQueryExtensions` table: a bare `notes.md` with no separator is a single token and already routes
  lexical-only via the single-token rung, so the extension table buys nothing here and copying a
  40-entry table into Core would have been the duplication worth avoiding.
- **Recommended follow-up (not F1 scope):** once F3/F4 land, lift `QueryShapeFor` and its predicates
  from `SearchTool` into a Core `QueryShape` helper and have both the empty-diagnosis classifier and
  this policy consume it. That collapses the convergence risk to zero. It is a cross-file refactor of a
  file two other tasks own, so it should be its own serialized task.

### 3. Deliberately NOT the empty-diagnosis classifier

Design §6.2 (line 337-339) says the ambiguous case must not be decided by the empty-diagnosis
classifier, "which was built for post-hoc labeling". Honored: `QueryShapeFor`'s `docs_like` /
`natural_language` / `source_like` vocabulary is not consulted, and in particular
`LooksLikeDocsOrConfigQuery`'s keyword list (`config`, `readme`, `install`, …) is **not** used as a
hybrid trigger — a query is prose because of its *grammar* (markers/length), not because it happens to
contain the substring "doc".

### 4. Strong lexical evidence never overrides a shape decision

Evidence is consulted **only** on the ambiguous rung. A prose query stays hybrid even with a dominant
lexical hit, and a path/identifier query stays lexical-only even with zero lexical hits. This keeps the
policy monotone and makes the P5 canary population well-defined (identifier queries are never
canary-eligible, per design §9.1). Two tests pin this.

### 5. Fusion class is always populated

Even on a lexical-only route the record carries a class. F3 must gate on `IsHybrid`, not on the class
being present. This avoids a nullable field and a null-check at every consumption site.

## Verification

| Gate | Command | Result |
| --- | --- | --- |
| worker-red-green | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~SemanticQueryPolicy"` | **40 passed, 0 failed, 34 ms** |
| worker-ceiling (fast suite) | `scripts/test.sh` | **4070 passed, 2 skipped, 0 failed; 22 s test / 28 s wall (ceiling 30 s)** |
| worker-ceiling (release build) | `dotnet build Miller.slnx -c Release` | **0 warnings, 0 errors** |

**Red state:** tests were authored before `SemanticQueryPolicy.cs` existed; the red was a compile
failure on every one of the 40 cases (no `SemanticQueryPolicy`, `LexicalEvidence`,
`SemanticFusionClass`, or `SemanticQueryReason` type). Green came from the single implementation file
with no test edits.

**Invariant proven by the 40 cases:** *route is a pure function of (query shape, lexical evidence), with
shape decisive and evidence consulted only on the ambiguous rung.* Concretely —
17 shape-routed queries (`FooBar`, `foo_bar`, `foo.Bar`, `IFooBar`, `getHTTPResponseCode`, `src/x/y.cs`,
`x/y`, `./src/App.cs`, `~/notes.md`, `src\Miller.Core\Search`, `a`, `cfg`, `id`, `Run(query)`,
`count < limit`, `""`, `"   "`) never go hybrid; 5 prose queries go hybrid/`Conceptual`; a prose query
naming a symbol goes hybrid/`Mixed`; the same ambiguous two-word query flips LexicalOnly ⇄ Hybrid purely
on evidence (dominant 9.0-vs-2.0 ⟹ lexical, flat 3.0-vs-2.9 and no-evidence ⟹ hybrid); ambiguous pairs
class correctly (`VectorSidecar TryOpen` ⟹ `SymbolLookup`, `release process` ⟹ `Mixed`); strong evidence
does not override prose *or* path/identifier shape; `null` evidence ≡ `LexicalEvidence.None`; repeated
calls are equal; leading/trailing whitespace is irrelevant; `PolicyVersion == "policy-v1"`; the three
wire names match the contract.

**Fast-suite budget:** the F1 tests add **34 ms**, well inside the "well under 1 s per task" constraint.

## Miller calls used

| Call | What it gave me |
| --- | --- |
| `context query="query shape classification heuristics: path-like, identifier-like, natural language phrase detection in search" token_budget=2500` | The orienting call. Surfaced all six shape predicates plus `QueryShapeFor` and `EmptyDiagnosisForSymbols` with file:line, which is how I learned the logic lives in `Miller.Server` and not `Miller.Core` — the finding that drove judgment call #2. |
| `search query="IsPathLikeQuery" mode=source` | **Failed** — `content.db not found`. |
| `inspect target=QueryShapeFor scope=src/Miller.Server/Tools/SearchTool.cs depth=full` | **Failed** — `search.db` missing. |

**Miller-first caveat, reported honestly:** this worktree has no built `.miller` sidecars, so `search`
and `inspect` both failed hard (search.db / content.db absent). `context` worked. After the two
failures I fell back to targeted line-range reads of `SearchTool.cs` at the exact lines `context` had
already given me — I never read the 112KB file whole, and I never read a whole file before listing its
symbols. The one whole-file read was `TextSearchQueryPlan.cs` (89 lines) to check whether Core already
had a query-shape planner worth extending; it does not — it is a tokenization/coverage plan, not a
shape classifier.

## API-shape evidence

| Shape decision | Evidence |
| --- | --- |
| Lives in `Miller.Core.Search` | Sibling of `Bm25.cs`, `TextSearchQueryPlan.cs`; Miller.Core is the zero-I/O project (CLAUDE.md "Project seam"). |
| Runner-up score in the evidence record | `Bm25.Idf`/`TermScore` (`Bm25.cs:43-61`) are unbounded raw scores; `ApplyExactNameAdjustments` (`:67`) multiplies by 1.5/1.2/0.75 — no normalized confidence exists to threshold against. |
| Enum reason, not string | Global constraint: "Persisted telemetry stays enum/counter-only (no query text)". |
| `WireName` mapping | Design §6.2 / brief: `symbol_lookup \| conceptual \| mixed`. |
| Not using the docs-keyword list as a hybrid trigger | Design line 337-339: ambiguous decided by weak lexical evidence, "not by the empty-diagnosis classifier". |
| `FrozenSet` for prose markers | Matches `TextSearchQueryPlan.CoverageStopWords` (`TextSearchQueryPlan.cs:8`). |
| Readonly record structs | Value semantics make the determinism/`null`-evidence tests assert on whole-value equality. |

## Concerns for the lead

1. **`SearchTool` query-shape logic should eventually be lifted into Core** (judgment call #2). Two
   classifiers now describe overlapping query shapes. I kept the punctuation set byte-identical to
   limit drift, but this is a real follow-up task, best serialized after F3/F4 release `SearchTool.cs`.
2. **Fast-suite headroom is now 28 s of the 30 s ceiling** with F1 + F2 both in the tree. F1 contributes
   34 ms, so this is not F1's doing — but the next task to add a slow fast-test will trip the guard.
   Worth a lead-level check before F3/F4 land.
3. **Transient shared-build breakage is real.** A `scripts/test.sh` run mid-way failed to compile on
   impl-f2's `SemanticSearchArmTests.cs` (`IVectorSearchPort` not yet defined), and an earlier full run
   took 63 s under CPU contention with the peer worker. Both cleared on retry; the reported numbers are
   from the clean run. If the lead re-runs gates, do it when no peer worker is mid-edit.
4. **`StrongLexicalDominanceRatio = 1.25` is an unvalidated starting constant**, exactly like the
   `fusion-v1` RRF weights. It is a named public constant in Core so P4/P5 shadow data can retune it
   without an API change.
