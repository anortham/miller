# Task F3 report — RRF fusion at the executor seam

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-p3` · branch `worktree-semantic-p3`
**Commit:** see "Commit" below · **Status:** complete, all assigned gates green

## Files changed

Created:
- `src/Miller.Core/Search/RrfFusion.cs` — pure weighted-RRF fusion (`fusion-v1`), zero I/O.
- `tests/Miller.Tests/Core/RrfFusionTests.cs` — 17 tests.
- `tests/Miller.Tests/Server/HybridSearchTests.cs` — 15 tests.

Modified:
- `src/Miller.Server/Tools/SearchRouteExecutor.cs` — fusion seam in `RunSymbols`, `ISymbolFusionArm` /
  `SymbolFusionRequest` contract, production `SemanticSymbolFusionArm`.
- `src/Miller.Server/Tools/SearchTool.cs` — `SymbolVisibilityPolicy` on `SymbolCandidateSet`, additive JSON
  fusion fields, optional `ISymbolFusionArm` ctor param.
- `src/Miller.Server/Hosting/MillerServiceRegistration.cs` — lazy arm/session composition.

No other file touched (`git status` shows exactly these six).

## Implementation summary

**`RrfFusion` (Miller.Core, pure).** `Fuse(lexical, semantic, weights, rankConstant = 60)` returns
`FusedCandidate(Candidate, RrfScore, LexicalRank?, SemanticRank?)`. Each arm is deduped by `SymbolId` before
ranking (first occurrence wins, later ranks shift up); a symbol both arms return keeps the **lexical**
`SymbolCandidate`, so `score` keeps meaning lexical score everywhere. Score is
`wL/(k+rankL) + wS/(k+rankS)` with a missing arm contributing 0 — rank-based only, never score-mixing. Total
order: fused score desc, lexical score desc, `SymbolId` ordinal. Constants `FusionProfile = "fusion-v1"`,
`RankConstant = 60`, weights 1.0/0.3 · 0.5/1.0 · 0.8/0.8 per class.

**Executor seam.** `SearchRouteExecutionRequest` gains `ISymbolFusionArm? FusionArm = null`. `RunSymbols`
collects lexical candidates exactly as before, then — only when an arm is present, the route is not file-mode,
and the arm returns a non-empty list — swaps `Candidates` for the fused order and builds a
`SymbolId -> FusedCandidate` map for rendering. Every other path is the untouched pre-existing code.
`null` from the arm means "not mine"; that is the single channel through which off, shadow, unready artifact,
fingerprint mismatch, lexical-only route, and any arm failure all reach byte-identical lexical output.

**Production arm.** `SemanticSymbolFusionArm(SemanticMode, Func<SemanticSearchArm>)`: gates on
`SemanticMode.On` **before** invoking the arm factory (shadow/off never construct anything), routes via
`SemanticQueryPolicy.Route` with `LexicalEvidence` built from the already-ranked list's top two scores (no
extra retrieval), queries `QuerySymbolsAsync(k = clamp(limit*2, 10, 500))` with an allow predicate that
re-applies the lexical visibility rules, resolves hits through `index.FindBySymbolId`, and fuses.

**Filter-aware recall.** The allow predicate is passed *into* F2's arm, which answers a rejecting filter by
escalating recall (k → 2k → … → 500) rather than truncating. Filtering therefore costs nothing in recall —
`ToolSearchFilters` never silently drops semantic hits.

**Output contract.** JSON stays a bare array; fused rows gain `rrf_score`, plus `lexical_rank` /
`semantic_rank` only when that arm ranked them. A lexical-only run passes `fusion: null` and writes no fusion
keys at all. Compact layout is untouched — only row order changes (and the top-hit `NextStepHint`, which by
design names the new top hit).

**Host lifecycle.** The session is a `Lazy<SemanticEmbeddingSession?>` singleton (a per-query session would
reset the child process, restart count, and circuit state); the arm is transient, resolved per tool call well
after `StartAsync`. No hosted-service constructor reads a bootstrap getter — `WorkspaceContext` is read inside
the lazy factory, i.e. on the first hybrid query. `HostStartupRegistrationTests` green.

**Blast radius.** Fusion is reachable only through `SearchRouteExecutor.RunSymbols` with a non-null
`FusionArm`, and only `SearchTool.Search` supplies one. `SearchTool.Run` (~480 callers: context/impact/trace/
CLI) never constructs a `SearchRouteExecutionRequest` and is structurally unable to fuse.

## Judgment calls

1. **`ISymbolFusionArm` is public, typed over public types.** DI activation (`WithTools<SearchTool>`) requires
   a public constructor, which forced the parameter type public. Rather than making the internal
   `SymbolCandidateSet` public, I introduced `SymbolFusionRequest(Query, Candidates, Limit, Allows)` over
   already-public types (`SymbolCandidate`, `IndexedSymbol`). The arm gets strictly less than the full
   candidate set, which is also the better seam.
2. **Visibility policy carried on `SymbolCandidateSet` as an optional trailing member.** Needed so the semantic
   arm cannot surface a test symbol or out-of-filter file the same query would have hidden lexically. Optional
   with a default so existing constructions (including `SearchRouteExecutorTests`, which I do not own) still
   compile unchanged.
3. **`limit` pages, fusion extends.** Fused output may contain more candidates than lexical did; rendering
   still pages at `limit` and the "… N more" note reflects the fused total. This is the brief's
   "reorders/extends candidates".
4. **Sync-over-async at the seam.** The executor is synchronous and F2's arm is async. `GetAwaiter().GetResult()`
   is safe here — MCP stdio/CLI has no synchronization context, and the arm never throws (it returns a reason).
   Noted as the one place a future async executor would want revisiting.
5. **Compact-layout test tightened after a real red.** My first assertion demanded byte-equal sorted lines;
   it failed only because the `next: inspect target=…` nudge follows the new top hit. That is correct behavior,
   not a layout change, so the test now asserts result-line equality plus the nudge tracking the new top hit —
   a stronger, more honest statement. No gate was weakened.

## Verification

| Gate | Command | Red state | Result |
|---|---|---|---|
| Pure RRF math/weights/dedupe/ties | `--filter FullyQualifiedName~RrfFusion` | `CS0246: SemanticRankedCandidate not found` | **17 passed / 48 ms** |
| Hybrid seam + byte-identity + fail-open | `--filter FullyQualifiedName~HybridSearch` | `CS0246: ISymbolFusionArm not found`, then 1 real assertion failure (see judgment call 5) | **15 passed** (32 combined / 102 ms) |
| Golden parity + executor + startup + off-guarantee + instructions | `--filter …SearchGoldenParity\|SearchRouteExecutorTests\|HostStartupRegistration\|SemanticOffGuarantee\|AgentInstructions` | n/a (pre-existing, must not regress) | **86 passed / 282 ms** |
| Full fast suite | `scripts/test.sh` | n/a | **4102 passed, 0 failed, 2 skipped — wall 28s** (ceiling 30s; cold-rebuild run reported 58s, warm run 28s) |
| Release build | `dotnet build Miller.slnx -c Release` | n/a | **Build succeeded — 0 warnings / 0 errors** |

Fast-suite cost of this task: ~0.1s. No new Scale tests needed (all fakes are in-process).

### What each gate proves, per acceptance criterion

- *Pure RRF tests* — rank math (`1.0/61` exactly), the three frozen weight pairs, dedupe-before-ranking in both
  arms, all three tie-break levels, determinism across repeated runs, and `RrfFusion.FusionProfile ==
  MillerSemanticContract.FusionProfile` (cross-project constant agreement, since Core cannot reference Indexing).
- *Hybrid end-to-end* — a conceptual query reorders lexical candidates (`Gadget` to the top) and extends the
  list with a semantic-only symbol; JSON rows carry `rrf_score` / `lexical_rank` / `semantic_rank`; compact
  result lines are identical modulo order.
- *Byte-identity* — no-arm, shadow mode, unavailable artifact, empty-served result, lexical-only route, and
  empty-lexical miss path each assert `Equal(lexical render, fused render)` on the exact output string. Shadow
  and lexical-only additionally assert `port.OpenCount == 0` — proof of non-consultation, not just equal bytes.
  `SearchGoldenParityTests` (18 cases) passes **unchanged**.
- *Fail-open* — a `VectorStoreException` thrown mid-query inside the store yields byte-identical lexical output.
- *Filter-awareness* — semantic hits outside `file_pattern`, test symbols under `exclude_tests`, and hits
  absent from the index are each proven absent from rendered output.

## Miller calls used

| Call | What it proved |
|---|---|
| `context query="how does SearchRouteExecutor collect symbol candidates and render search results"` | Located the C1 seam (`SearchRouteExecutor.cs:25`, `SearchTool.cs:55/918/1009`) and surfaced `SearchGoldenParityTests` as the gate — without opening the 112KB `SearchTool.cs`. |
| `inspect target="src/Miller.Server/Tools/SearchRouteExecutor.cs"` | Full symbol listing (3 classes, 7 methods) before reading — confirmed the file is small enough to read whole. |
| `inspect target="SymbolCandidateSet" depth=full` | Exact 5-member shape, 6 callers, and the doc stating rendering never touches the index. |
| `inspect target="SymbolCandidate" depth=full` | 8-member shape and all 32 reference sites — evidence that changing this record would be high blast radius, so I left it untouched. |
| `inspect target="ISymbolLookupIndex" depth=full` | Confirmed `FindBySymbolId(string) -> IndexedSymbol?` exists — the mechanism turning semantic hits into renderable candidates. |
| `trace target="RunSymbols" mode=refs` | The 5 call sites: `SearchTool.cs:301` (MCP), `CliDispatch.cs:438` (CLI), and 3 tests including golden parity — established that an optional-default request field keeps every existing caller lexical. |
| `impact git=true` (post-edit) | 29 impacted symbols / 71 likely tests; flagged `SearchToolTests`, `HostStartupRegistrationTests`, `AgentInstructionsTests` as the risk set — all confirmed green. |

Miller was fully functional in this worktree (sidecars present); no fallback reads were needed. Targeted
line-range reads were used only for regions `context`/`inspect` had already pinpointed inside `SearchTool.cs`
(never the whole file).

## API-shape evidence

Every upstream contract below was read from committed source, not assumed:

- `SemanticQueryPolicy.Route(string?, LexicalEvidence?) -> SemanticQueryRoute(IsHybrid, HybridClass, Reason)` —
  `src/Miller.Core/Search/SemanticQueryPolicy.cs:104`. `LexicalEvidence(HitCount, TopScore, RunnerUpScore)` with
  `.None` / `.IsStrong` at `:54–64`. Confirmed the class is populated on lexical-only routes too, so I gate on
  `IsHybrid` and read `HybridClass` only after that check.
- `SemanticSearchArm.QuerySymbolsAsync(query, k, Func<VectorMatch,bool>?, ct)` —
  `src/Miller.Indexing/Semantic/SemanticSearchArm.cs:131` (the `Async` name, as the hand-off corrected).
- `SemanticQueryResult(Hits, UnavailableReason)` with `Served => UnavailableReason is null` — `:16–23`.
  Empty-served is treated as "no fusion, unchanged bytes", distinct from unavailable; both return `null`.
- `SemanticHit(SymbolId?, DocId?, FilePath, Rank, Cosine)` — `:9`. Only `SymbolId` is read; `DocId`-bearing
  chunk hits cannot reach this path because only `QuerySymbolsAsync` is called.
- `SemanticSearchArm.MaxCandidates = 500` — `:78`; used as the `k` clamp ceiling.
- `VectorMatch(RowId, Distance, UnitId, Path)` — `src/Miller.Indexing/Semantic/VectorStore.cs:7`; `UnitId` is
  the symbol id the allow predicate resolves.
- `MillerSemanticContract.FusionProfile = "fusion-v1"` — `MillerSemanticContract.cs:86`; asserted equal to
  `RrfFusion.FusionProfile` by test.
- `SemanticMode { Off, Shadow, On }` — `src/Miller.Indexing/SemanticActivation.cs:5–16`. **Note:**
  `VectorSidecar.Enabled` is `Mode is not Off`, i.e. **true under shadow** — so the fusion gate is
  `Mode is SemanticMode.On`, not `Enabled`. Using `Enabled` here would have silently broken the shadow
  guarantee.
- `WorkspaceContext.WorkspaceRoot` / `.ToolsRoot` — `src/Miller.Server/Hosting/WorkspaceContext.cs:17–20`.
- `SemanticSearchArm.ProcessSession(toolsRoot)` — `:119`, mirrors `VectorConvergeService.ProcessSession`
  (`VectorConvergeService.cs:710`); returns null when the binary is absent.
- `FakeSemanticSidecar.InProcessLauncher()` in namespace `Miller.Tests.Support` — the in-process embedding fake
  F2's tests use, reused here to keep the hybrid tests in the fast suite.

## Concerns / hand-off notes for F4 and F5

1. **`SemanticSymbolFusionArm` is not yet observable.** It returns `null` on every abstention without recording
   *why*. Telemetry (enum-only: policy reason, fusion class, `UnavailableReason` bucket) is unbuilt — F5's
   determinism contract likely wants it, and the reason strings from F2 are already available at the call site.
2. **Sync-over-async** (judgment call 4) is contained to one line in `SemanticSymbolFusionArm.Fuse`.
3. **`k = clamp(limit*2, 10, 500)`** is a defensible first choice, not a tuned one. It is a single constant in
   one method if evaluation says otherwise.
4. **`ISymbolFusionArm` / `SymbolFusionRequest` are public API of `Miller.Server`**, forced by DI activation.
   If F5 adds `--arm lexical|semantic|hybrid`, it should set/clear `SearchRouteExecutionRequest.FusionArm` on
   the CLI path rather than adding another public surface. `--arm semantic` (semantic-only) is *not* expressible
   through the current seam, which always fuses against the lexical list — F5 will need either a weight of
   `(0, 1)` plus an empty lexical list, or a small extension here.
5. **No MCP parameter was added** — the arm is composed from `MILLER_SEMANTIC` only, per the MCP-stinginess rule.
