# Task 6: `search mode=markers` — collapse multi-marker lines

## Status: DONE

## What changed

`src/Miller.Server/Tools/MarkerSearch.cs`:
- `MarkerSearchHit` record shape changed from `(string Marker, RegionSearchHit Region)` to
  `(IReadOnlyList<string> Markers, RegionSearchHit Region)`.
- `FindMarkers` now keys its dictionary by `RegionId` alone (previously `marker + "\0" + RegionId`).
  A private `RegionMarkers` accumulator collects every matched marker per region into a
  `HashSet<string>`. `Take(limit)` therefore counts **distinct regions**, not (marker, region) pairs.
- New `OrderMarkers` helper orders a region's markers by canonical rank (`DefaultMarkers` index)
  then ordinal name — the same tiebreak the pre-collapse ordering used — so `Markers[0]` is the
  "first" marker. Region sort remains `(Path, Line, first-marker rank, first-marker name)`.
- `RenderCompact` joins markers with `,`: `path:line  TODO,FIXME,HACK  kind  Containing`.
- `RenderJson` keeps `"marker"` = first marker (contract-compatible, additive-only rule) and adds
  an additive `"markers"` array (all markers, ordered) immediately after it.

`tests/Miller.Tests/Server/MarkerSearchTests.cs`: 3 new tests (written first, TDD).

No other files needed changes. Both internal callers — `SearchRouteExecutor.cs:130` and
`CliDispatch.Todos` (`CliDispatch.cs:189`) — consume `MarkerSearch.Run`, which returns a `string`
and keeps its exact signature. Neither touches the `MarkerSearchHit` record, so the shape change is
fully internal to `MarkerSearch.cs`. CliDispatch was left untouched (respecting the Task-2 caution).

## Miller calls used + API-shape evidence

- `mcp__miller__inspect` / `search` / `trace` schemas loaded via ToolSearch for orientation; symbol
  discovery cross-checked against worktree files (the Miller index serves the main checkout, which
  lacks this branch's landed changes — so worktree files are the source of truth for exact text).
- Read worktree `MarkerSearch.cs`: record at :189, `FindMarkers` :43, `RenderCompact` :128,
  `RenderJson` :158 — matched the spec's cited line anchors.
- `grep` for `MarkerSearchHit` / `MarkerSearch` / `FindMarkers` across `src/` + `tests/` confirmed
  the only consumers of the record are inside `MarkerSearch.cs`; all external callers use the
  string-returning `MarkerSearch.Run`: `SearchRouteExecutor.cs:129-130`, `CliDispatch.cs:168,189`,
  and `MarkerSearchTests.cs`. This is the evidence that the record shape is not exposed and no
  caller edits are required.

## Invariant the tests prove

- **Collapse:** a single region whose text matches TODO+FIXME+HACK renders exactly one block
  (`src/A.cs:5  TODO,FIXME,HACK  comment  A.Run`); the region header appears once, not once per
  marker. (`Run_MultipleMarkersInOneRegion_CollapsesToSingleBlock`)
- **Limit = regions:** two regions each matching two markers (4 marker/region pairs) with `limit=1`
  yields `count == 1` and only the first region. (`Run_Limit_CountsDistinctRegionsNotMarkerPairs`)
- **JSON additive + ordered:** `marker` = first marker by canonical rank (`TODO`); `markers` =
  `["TODO","FIXME","HACK"]` ordered by `DefaultMarkers` index regardless of source text order.
  (`Run_Json_MultiMarkerRegion_HasFirstMarkerAndOrderedMarkersArray`)
- Existing single-marker tests (compact block + JSON `marker`, counts 2/1/1) still pass unchanged —
  no regression to the single-marker path.

## Verification

- `dotnet build src/Miller.Server/Miller.Server.csproj -c Release` → 0 Warning(s) / 0 Error(s).
- `dotnet test tests/Miller.Tests --filter "FullyQualifiedName~Marker"` → **11 passed, 0 failed**.
- `dotnet test ... "FullyQualifiedName~CliDispatch&FullyQualifiedName~Todo"` → **1 passed** (CLI
  todos path compiles and passes; the only Todos-specific test).

## Concerns

None. `MILLER_AGENT_INSTRUCTIONS.md` not touched. JSON change is additive-only. Compact output
shrinks for multi-marker regions (one block instead of N).
