# Task C1 report — Typed candidate seam in SearchRouteExecutor

Status: **DONE**
Commit SHA: none - parallel-lead-commit
Worktree: `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`, branch `worktree-semantic-p2`

## What I implemented

Split the symbol search route into two stages with a typed candidate list between them, changing zero output
bytes.

1. **`SymbolCandidate`** (`src/Miller.Core/Search/SymbolCandidate.cs`, new) — a Miller.Core record carrying
   exactly the fields symbol rendering reads: `DocId`, `SymbolId`, `Name`, `Signature`, `Kind`, `FilePath`,
   `StartLine`, `Score`. Zero I/O deps (no `IndexedSymbol` reference — Miller.Core cannot see Miller.Indexing).
   `DocId` + `SymbolId` are carried for P3: they are the join keys another arm's hits match back on.

2. **`SearchTool.CollectSymbolCandidates(...)`** (stage one) — everything that needs the index: file-mode
   detection, escalating fetch, test/low-signal/scope filtering, and the empty-result near-match suggestions.
   Returns `SymbolCandidateSet(Candidates, OutsideScope, EmptySuggestions, FileMode, Filters)`.

3. **`SearchTool.RenderSymbolCandidates(...)`** (stage two) — takes no index and performs no lookup. Every
   rendered byte comes from the set plus presentation options.

4. **Renderers converted to `SymbolCandidate`** — `RenderJson`, `RenderCompact`, `RenderFileCompact`,
   `RenderDefinitionCompact`, `RenderFilteredMissCompact`, `FindPromotableDefinitionIndex`, and the
   `Append*` helpers on the symbol path. `RenderJson` lost its parallel `scores` list — score now rides the
   candidate, which removes an index-alignment invariant.

5. **`SearchTool.Run`** keeps its exact signature (486 call sites) and is now `Collect` + `Render`.

6. **`SearchRouteExecutor.CollectSymbolCandidates(index, route, request)`** — the named seam P3's fusion arm
   interposes on. `RunSymbols` = collect → render.

Content / text / regions / markers routes are untouched.

## Verification

| Gate | Invariant proven | Command | Result |
|---|---|---|---|
| Pre/post byte parity | The refactor changes no output byte for any covered query shape | dumped all 18 golden cases against a clean `git archive HEAD` tree in `/tmp/c1-baseline`, then against the refactored worktree; `diff` of the two JSONL dumps | **IDENTICAL** (18/18), 2026-07-20 09:20 |
| Worker scope | Golden corpus + seam behavior hold | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~SearchGoldenParity\|FullyQualifiedName~SearchRouteExecutor"` | **Passed 26/26**, 313 ms, 2026-07-20 09:24 |
| Fast suite (ceiling) | No regression anywhere else in the tree (incl. the other 480+ `SearchTool.Run` callers) | `scripts/test.sh` | **Passed 3724, Failed 0, Skipped 1**, 2026-07-20 09:27 |

The golden expectations baked into `SearchGoldenParityTests.cs` are the **pre-refactor** (HEAD) values, so the
file is a genuine gate rather than a snapshot of my own output.

### One tripwire failure, pre-existing

`scripts/test.sh` reports `ERROR: fast suite took 60s (> 30s ceiling)` after a green run. This is **not from
this task**: the same clean HEAD tree with my test file *removed* runs the fast suite in **1 m 16 s** — slower
than the 60 s measured with my changes present. My 19 golden tests take **291 ms** total. The tripwire is
firing on a pre-existing condition on this branch/machine (other P2 lanes are adding tests concurrently);
flagged for the lead, not fixed here since the files are not mine.

## Files changed

- Created `src/Miller.Core/Search/SymbolCandidate.cs`
- Created `tests/Miller.Tests/Server/SearchGoldenParityTests.cs` (18 cases, compact + JSON)
- Modified `src/Miller.Server/Tools/SearchTool.cs` (+187/−65 region)
- Modified `src/Miller.Server/Tools/SearchRouteExecutor.cs`
- Extended `tests/Miller.Tests/Server/SearchRouteExecutorTests.cs` (+3 tests)

## Miller calls used

| Call | What it confirmed |
|---|---|
| `context(query="SearchRouteExecutor symbols route candidate generation and rendering")` | Seed set: `SearchRouteExecutor:18`, its 4 tests, and the two callers `SearchTool.cs:144` / `CliDispatch.cs:320` |
| `inspect(target="src/Miller.Server/Tools/SearchRouteExecutor.cs", depth=full)` | File symbol list: 3 records + 6 methods; `RunSymbols` at :20, `EnsureKind` private at :147 |
| `trace(target="SearchRouteExecutor", mode=refs)` | 14 references — all in `CliDispatch.Search`, `SearchTool.Search`, and the 4 executor tests. No other consumer to break |
| `inspect(target="SearchTool.Run", depth=full)` | Full body, 11 params, 19 decisions; the `kept`/`scores`/`outsideScope` triple and the exact render dispatch order |
| `inspect(target="IndexedSymbol", depth=full)` | Record shape + `ToSearchableDocument`; confirmed Miller.Core cannot reference it, so `SymbolCandidate` must be field-wise |

## API-shape evidence

- `SearchRouteExecutionResult(string Output, int Count, long SourceBytes = 0)` — `SearchRouteExecutor.cs:16`
  via `inspect` file listing. **Unchanged** by this task, as the brief requires.
- `SearchRouteExecutionRequest` field set (`Query`, `Limit`, `Json`, `ExcludeTests`, `CompactBanner`,
  `FilePattern`, `Language`, `HasDocLookup`, `SuggestionLookup`) — `SearchRouteExecutor.cs:5`. Unchanged.
- `SearchTool.Run(ISymbolLookupIndex, string, SearchToolMode, int, bool?, bool, out int, string?, Func<…>?,
  string?, string?)` — `inspect SearchTool.Run` gave the exact 11-param signature; preserved verbatim.
- Renderer field usage — read from the bodies at `SearchTool.cs:1940/2074/2298/2135`: only `Name`, `Kind`,
  `FilePath`, `StartLine`, `Signature`, `SymbolId` and the score. That is exactly `SymbolCandidate`'s field
  set; nothing was guessed into it.
- `ISymbolLookupIndex` members (`Search`, `Resolve`, `FindByFilePathFragment`, …) — `inspect IndexedSymbol`
  callers list. Consumed as-is, not modified.

## Judgment calls

- **`src/Miller.Server/Tools/SearchTool.cs:55` — `SymbolCandidateSet` lives in Miller.Server, not Miller.Core.**
  It carries `ToolSearchFilters` (Miller.Server) and `IndexedSymbol` (Miller.Indexing), which Miller.Core must
  not see. Only `SymbolCandidate` itself — the piece the brief names — is in Core, per the zero-I/O rule.
- **`SearchTool.cs:55` — `EmptySuggestions` stays `IReadOnlyList<IndexedSymbol>` rather than becoming
  candidates.** `AppendSuggestions` / `RenderEmptyJson` are shared with the *text* routes, which the brief says
  to leave untouched; converting the type would have edited text-route call sites for no behavioral gain.
  Suggestions are a miss-path fact, never a fusion target. `OutsideScope` **was** converted, because
  `RenderFilteredMissCompact(IndexedSymbol)` is reached only from the symbol route.
- **`SearchTool.cs:949` — suggestions are computed during collection, not rendering.** Chose this over passing
  the index into `RenderSymbolCandidates` so that stage two is genuinely index-free. Guarded by the identical
  condition as before (`no hits && !fileMode && no outside-scope`), so no extra work is ever done.
- **`SearchTool.cs:918/1009` — `CollectSymbolCandidates`/`RenderSymbolCandidates` are `internal`, not
  `public`.** Forced by `ToolSearchFilters` being internal (CS0051 on the public record's deconstructor).
  Tests reach them via the existing `InternalsVisibleTo`.
- **`SearchRouteExecutorTests.cs` — added a test that renders a *reordered* candidate set** and asserts the
  JSON follows the list order. This is the assertion that actually proves the seam is a real interposition
  point rather than a cosmetic split; a type-shape test alone would not.

## Self-review findings

- **Completeness:** all four acceptance criteria met. ≥12 shapes → 18 (symbol/exact, auto, phrase, file-mode,
  file-pattern-filtered, language-filtered, filtered-miss, limit-edge with remainder note, empty-compact,
  empty-json, has_doc annotation, banner-prefixed, exclude-tests), each in the form the route actually emits;
  compact and JSON both covered; all 4 pre-existing `SearchRouteExecutorTests` pass unchanged.
- **Tests assert real values:** golden expectations are full literal output strings including the `next:`
  nudge, `… N more (raise limit)` tail, and exact float scores — not substring or shape checks.
- **YAGNI:** dropped `ParentId`, `Language`, and `IsTest` from `SymbolCandidate`; they are used by *filtering*
  (which happens before a candidate exists), never by rendering. Kept `DocId` only because P3 fusion needs the
  ordinal the lexical index ranks on.
- **Quality:** removed the `kept[i]`/`scores[i]` parallel-list coupling in `RenderJson`. No narration comments
  added; the one load-bearing comment on the `NextStepHint` nudge was carried over verbatim.
- **Blast radius checked:** `SearchTool.Run` signature untouched, so the 486 other references compile and pass
  unchanged — confirmed by the green fast suite, not by inspection alone.

## Concerns for the lead

1. **Fast-suite wall-time tripwire is red (60 s vs the 30 s ceiling), pre-existing.** Clean HEAD without my
   changes is 1 m 16 s. Someone's lane — or accumulated test growth — needs to own this; it will fail every
   worker's `scripts/test.sh` gate until fixed.
2. **Shared worktree contention.** The test assembly and Miller.Core were both transiently un-buildable during
   this task from other lanes' in-flight files (`VectorSidecarTests`/`SemanticActivationTests` referencing
   types that did not exist yet; `TextReplaceMatcher.cs` mid-edit). I worked around it with retry loops; final
   verification ran against a fully green tree. Worth a serialization note if more lanes run in parallel.
3. **P3 hand-off:** the fusion arm should reorder/extend `SymbolCandidateSet.Candidates` inside
   `SearchRouteExecutor.CollectSymbolCandidates` and leave `RenderSymbolCandidates` alone. `SearchTool.Run`
   composes the same two stages, so a fusion applied only at the executor seam will **not** affect the ~480
   other `Run` callers (context/impact/trace/CLI) — that is intentional, but the lead should confirm it
   matches the P3 design's intended blast radius.
