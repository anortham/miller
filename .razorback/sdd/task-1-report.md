# Task 1 report — `trace refs` resolve `containing=` to symbol name

## What I implemented

Compact `trace mode=refs` rendered `containing=<32-hex symbol id>` — pure token waste that forced a
follow-up inspect. Now the enclosing symbol id is resolved to its name.

- `ReferenceLine(SymbolRef)` → `ReferenceLine(ISymbolLookupIndex index, SymbolRef reference)`
  (`TraceTool.cs`). Resolves `ContainingSymbolId` via `index.FindBySymbolId`. Renders `  in=<Name>` when
  resolved; renders **nothing** when unresolvable (the raw hash is unusable in compact output). Single call
  site inside `RunRefs` threads the `index` it already holds.
- JSON `WriteReference` gains an **additive** `containing_symbol_name` (string|null) written immediately
  after the existing `containing_symbol_id`. The id is kept (chainable in JSON); the name is null when the
  containing id is absent or does not resolve. Threaded `ISymbolLookupIndex index` through
  `RenderRefsJson` (all 5 call sites, all inside `RunRefs`) and into `WriteReference`.

No JSON keys renamed/removed — change is strictly additive. Compact output shrinks per line (a symbol name
replaces a 32-hex id and the `containing=` prefix becomes `in=`) while gaining a usable name.

## Miller calls used + what each confirmed

Miller MCP server was still connecting during orientation, so I confirmed API shapes by reading the exact
worktree files (content-identical to the indexed main checkout at HEAD):

- `TraceTool.cs:492 RunRefs` — confirmed `index` (`ISymbolLookupIndex`) is in scope and already calls
  `index.FindBySymbolId(seedId)` at :526, so no new dependency to thread in.
- `TraceTool.cs:600 ReferenceLine` — confirmed the old `containing=` render and that it is the only compact
  ref-line site (single caller at the `foreach` in `RunRefs`).
- `TraceTool.cs:2101 WriteReference` + `1930 RenderRefsJson` — confirmed JSON key order and that
  `RenderRefsJson` is private, called only from within `RunRefs` (5 sites), so threading `index` is local.
- `InspectTool.cs:595 DistinctCallers` — confirmed the resolution pattern:
  `index.FindBySymbolId(cid)` → `containing.Name`, fallback to id. I reused the resolve step but dropped the
  id fallback in compact per spec.
- `SymbolDetail.cs:38 SymbolRef` — confirmed `ContainingSymbolId` is `string?` (nullable).
- `grep` across `src` + `tests` — confirmed `containing_symbol_name` is an existing JSON key elsewhere
  (SearchTool/MarkerSearch/Context), so the new name is consistent with codebase convention; and that the
  only test asserting compact `containing=` is `TraceToolTests.cs:759` (my owned file).

## API-shape evidence

- `ISymbolLookupIndex.FindBySymbolId(string) : IndexedSymbol?` — used pre-existing at `TraceTool.cs:526`
  and `InspectTool.cs:603`.
- `IndexedSymbol.Name : string` — used at `InspectTool.cs:605`.
- `SymbolRef.ContainingSymbolId : string?` — `SymbolDetail.cs:38`.

## Verification

- Invariant proven: compact refs render `in=<enclosing symbol name>` and never a raw containing id;
  JSON carries both `containing_symbol_id` (unchanged) and additive `containing_symbol_name`; an
  unresolvable containing id yields no `in=` segment (compact) and `null` name (JSON).
- Scope label: worker-red-green.
- Command: `dotnet test tests/Miller.Tests --filter "FullyQualifiedName~TraceToolTests"` (csproj default
  filter excludes Scale).
- Result: Passed — Failed 0, Passed 86, Skipped 0 (Duration ~354ms). Build 0 warnings / 0 errors
  (warnings-as-errors).
- Timestamp: 2026-07-02.
- Commit SHA: see returned message.

## Files changed

- `src/Miller.Server/Tools/TraceTool.cs` — `ReferenceLine` signature + resolution; `WriteReference` +
  `containing_symbol_name`; `RenderRefsJson` index param + 5 call sites.
- `tests/Miller.Tests/Tools/TraceToolTests.cs` — updated compact + JSON refs tests to the new behavior;
  added `Refs_Compact_UnresolvableContainingRendersNoInSegment`.

## Judgment calls

- `TraceTool.cs:600 ReferenceLine` — chose to drop the segment entirely on unresolvable id over keeping the
  hash, because the spec says the raw hash is unusable in compact output; JSON still keeps the id for
  chaining.
- `TraceTool.cs:1930 RenderRefsJson` — chose to thread `ISymbolLookupIndex` through the existing private
  render path over resolving names eagerly in `RunRefs` into a new tuple/DTO, because it keeps the change
  minimal, matches how `RunRefs` already passes state, and avoids a new type.
- Test fixtures — chose to add a real `("caller", "CallerMethod", …)` symbol to the index so the containing
  id resolves, over inventing a mock lookup, because `BuildSymbolIndex` is the file's existing fixture
  helper (reused per instructions).

## Self-review findings

- Confirmed no other file references the changed private signatures (`ReferenceLine`, `WriteReference`,
  `RenderRefsJson` are all `private static` in `TraceTool.cs`).
- Confirmed the 5 `RenderRefsJson` error-path sites pass empty `references: []`, so `WriteReference` never
  runs there, but they still correctly receive `index`.
- Confirmed compact truncation/empty paths are unaffected (they don't go through `ReferenceLine`).

## Concerns

- None. Change is render-layer only, additive JSON, within existing seams.
