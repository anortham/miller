# Task 7 — text-content search: dedup identical (source, line) hits

## What I implemented

`mode=source` (and any text-content mode) could return the same `file:line` hit twice with identical
snippets because overlapping content-corpus chunks both match the same physical line. Added a shared
`DedupByLine` helper in `SearchTool.cs` and applied it inside both `RunTextContent` overload callbacks,
after the filter loop and before returning the escalation counts.

- New helper: `private static List<TextContentSearchHit> DedupByLine(List<TextContentSearchHit> hits)`
  - Keeps the first occurrence per `(SourceId, Line)` key, preserves order.
  - Returns the input list unchanged when `Count < 2` or nothing was collapsed (no allocation on the common path).
- `DedupByLine` is placed AFTER the complete `FetchWithEscalation` method so each method keeps exactly its
  own doc comment (follow-up commit fixed an initial placement that stacked two `<summary>` blocks).
- Applied `hits = DedupByLine(hits);` immediately before `return (fetched.Count, hits.Count);` in:
  - `RunTextContent(..., IReadOnlyCollection<string> contentKinds, ...)` (the collection overload, ~:882)
  - `RunTextContent(..., string contentKind, ...)` (the single-kind overload, ~:944)
- Placement is inside the `FetchWithEscalation` callback, so the escalation loop's `kept` count sees the
  deduped total — escalation to a larger window still triggers correctly when dedup leaves fewer than
  `limit` distinct rows. `hits` is a closure-captured local; the callback runs synchronously, so
  reassigning it propagates to the outer `total`/`page`/`sourceBytes` computation.

## Miller calls used + what each confirmed

- `inspect target=TextContentSearchHit depth=full` — confirmed the record's exact member names. The line
  member is **`Line`** (int), not `LineNumber`; source key member is **`SourceId`** (string). Also has
  `LineStart`, `LineEnd`, `ByteStart`, `ByteEnd`, `Snippet`, `SourceBytes`. Confirmed the two
  `RunTextContent` callers at `SearchTool.cs:845` and `:909`.
- ToolSearch surfaced the Miller MCP tool schemas; used `inspect` for the API shape.
- Read (targeted) `SearchTool.cs:820-969` — both overloads and the shared filter/escalation shape.
- Read `SearchTool.cs:540-564` — `FetchWithEscalation` semantics: callback returns `(Fetched, Kept)`;
  escalates while the index filled the window and `kept < limit`. This is why dedup must run inside the
  callback (so `kept` is the deduped count).
- Read `SearchRouteExecutor.cs:67-91` — **`SearchRouteExecutor.RunTextContent` delegates to
  `SearchTool.RunTextContent` (collection overload); it does NOT fetch independently.** So no separate
  dedup is needed there — the shared helper covers it. No edit to `SearchRouteExecutor.cs`.

## API-shape evidence

`TextContentSearchHit` (record, `src/Miller.Core/Search/TextContentSearchHit.cs:3`):
`SourceId, ChunkId, ContentKind, Path?, Url?, DisplayPath, Language, Score, Line, LineStart, LineEnd,
ByteStart, ByteEnd, Snippet, SourceBytes, ContainingSymbolId?, ContainingSymbolName?`. Dedup key uses
`SourceId` (string) + `Line` (int). The duplicate-bug rows differ only in `ChunkId` (two chunks over the
same line).

## Verification

- **Invariant proven:** text-content search renders at most one hit per `(SourceId, Line)`; `renderedCount`
  and the internal `total` reflect the deduped set; distinct lines from the same source survive;
  `sourceBytes` (grouped by source, max per source) is unchanged for the deduped page.
- Tests (TDD, red first): `RunTextContent_CollapsesDuplicateSourceLineHits` (was Actual:2 → now 1),
  `RunTextContent_KeepsDistinctLinesFromSameSource` (guards against over-collapsing distinct lines).
- Scope: `dotnet test tests/Miller.Tests --filter
  "FullyQualifiedName~SearchToolTests|FullyQualifiedName~SearchRouteExecutor"`
- Result: **Passed! 88/88, 0 failed** (re-run after the doc-placement fix commit).
- Build: `dotnet build src/Miller.Server` → 0 warnings / 0 errors.
- Timestamp: 2026-07-02.
- Commits: `99877f5` (fix + tests), `5c53966` (restore FetchWithEscalation doc placement).

## Files changed

- `src/Miller.Server/Tools/SearchTool.cs` (+24): `DedupByLine` helper + two call sites.
- `tests/Miller.Tests/Server/SearchToolTests.cs` (+53): two new facts.

## Judgment calls

- Helper returns a new list but short-circuits (returns the same instance) when nothing collapses — keeps
  the common no-dup path allocation-free while matching the requested
  `List<TextContentSearchHit> DedupByLine(...)` signature. Reassigning the closure-captured `hits` inside
  the synchronous callback is safe and propagates to the outer computation.
- `(SourceId, Line)` tuple key uses default string equality (ordinal), consistent with the existing
  `sourceBytes` grouping which uses `StringComparer.Ordinal` on `SourceId`.
- Did NOT touch `SearchRouteExecutor.cs` — it delegates, so the shared helper already covers its path
  (verified by reading the delegation). Did NOT touch `RunContentCorpus` / `RunRegions` — out of Task 7
  scope (the two `RunTextContent` overloads only).
- Did NOT touch `MarkerSearch.cs` or marker rendering (owned by another task).

## Self-review findings

- Confirmed escalation still works post-dedup: `kept` returned to `FetchWithEscalation` is the deduped
  count, so a query whose window is filled but dedups below `limit` still escalates to the next window.
- Confirmed order preserved (first occurrence kept) — the distinct-lines test asserts both lines render.
- JSON shape unchanged (dedup changes row count, not shape) — additive-only constraint satisfied.

## Concerns

- None. Change is local, no architecture impact. `sourceBytes` was already dedup-safe (max per source), so
  the fix is purely about not double-rendering a physical line.
- Note: this report file previously held the (committed, history-preserved) backend-http Task-7 report;
  overwritten here per the plan's task-N-report.md convention for this branch. Left uncommitted (not in my
  owned-file set).
