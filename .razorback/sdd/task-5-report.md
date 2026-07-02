# Task 5 — `inspect depth=full`: dedup callees, group references by file

## Status
DONE.

## What I implemented
Compact-rendering-only changes in `RenderSymbolCompact` (`InspectTool.cs`). JSON path untouched.

1. **References grouped by file.** The `## references` block now prints one `path:l1,l2,…` line per file
   instead of one `path:line` row per reference. New helper `AppendGroupedReferences` groups the
   `refs.Take(relationLimit)` slice with an insertion-ordered map (first-seen file order; incoming line order
   within a file). Grouping happens **after** `Take(relationLimit)`, and `AppendOmittedLine` still counts the
   underlying `refs.Count` against `relationLimit` — the ref limit / omitted semantics are unchanged; only the
   rendering of the kept refs is compacted.

2. **Callees deduped by name.** The `## callees` block now collapses repeated callee names via new helper
   `DistinctCallees` (returns a `DistinctCallee` record-struct list: first-seen name, first location, count).
   Rendered as `Name  path:line`, with ` ×N` appended only when `N>1`. Dedup runs **before** `Take(relationLimit)`
   so full depth spends its 50-row budget on 50 *distinct* callees. No name is filtered (no keyword blocklist —
   `nameof`/`ArgumentException` still render; only the repetition cost is removed). `AppendOmittedLine` is called
   with the **distinct** count (`distinctCallees.Count`) so `... N more callees` can never overstate.

Overview depth reuses the exact same rendering with its own `OverviewRelationLimit` (3); both depths are covered
by tests.

## Miller MCP calls used + what each confirmed
Per the task note, the Miller index serves the main checkout (no Task-4 changes), so I used Miller for
symbol/API-shape discovery and read the **worktree** files for exact edit text.
- Region orientation done via direct Read of the worktree `InspectTool.cs` (the authoritative post-Task-4 text) —
  Miller's served copy predates this branch, so editing off it would be unsafe. I confirmed the region line numbers
  (`RenderSymbolCompact` :375, references :415-422, callees :433-440) with `grep -n` on the worktree file and
  re-located them post-Task-4.
- API-shape evidence gathered by reading source directly (worktree):
  - `ExtractReader.ReadReferences` / `ReadCallees` (`src/Miller.Indexing/ExtractReader.cs:145,165`) both return
    `IReadOnlyList<SymbolRef>`, `ORDER BY path, start_line, identifier_id` — so refs/callees arrive already
    path/line ordered (a file's ref sites are contiguous; my insertion-ordered map is robust regardless).
  - `SymbolRef` (`src/Miller.Indexing/SymbolDetail.cs:38`) = `(Name, Kind, FilePath, StartLine, ContainingSymbolId)`.
    Callees are `SymbolRef` too — dedup key is `Name`, rendered location is `FilePath:StartLine`.
  - JSON path (`RenderSymbolJson`, `InspectTool.cs`) reads `ReadCallees`/`ReadReferences` independently and emits
    one object per raw row — confirmed untouched by my change.

## API-shape evidence
- References query orders by `path, start_line, identifier_id`; callees additionally filter
  `containing_symbol_id = $cid AND kind = 'call'`. Grouping/dedup are pure post-processing over these ordered lists.
- `AppendOmittedLine(sb, total, visible, label)` prints `... {total-visible} more {label} (use depth=full)` only
  when `total > visible`. For callees I pass the **deduped** total; for references I pass the raw ref total (spec).

## Verification
- **Invariant proven:** At `depth=full`, compact `## references` renders exactly one comma-joined `path:lines`
  line per file and compact `## callees` renders each callee name once with a `×N` count (N>1 only), with dedup
  applied before the relation limit and omitted-counts reflecting refs (references) / distinct names (callees);
  the JSON output remains raw and undeduped (4 ref objects / 6 callee objects, no `×`).
- **Scope:** `dotnet test tests/Miller.Tests --filter "FullyQualifiedName~InspectToolTests"`
- **Result:** Passed — Failed: 0, Passed: 42, Skipped: 0 (3 new tests + 39 existing). Build compiled clean
  under `TreatWarningsAsErrors` (0 warnings / 0 errors).
- **Timestamp:** 2026-07-02
- **SHA:** see commit below.

New tests:
- `Run_SymbolFull_GroupsReferencesByFile_AndDedupsCallees` — grouped `src/a.cs:5,8,12` + `src/b.cs:3`; callees
  `TryParse ×2`, `nameof ×2`, single-occurrence `Validate`/`Check` uncounted; no `more callees` line under the 50 limit.
- `Run_SymbolOverview_OmittedCounts_UseRefsAndDistinctCallees` — overview limit 3: refs omitted counts underlying
  refs (`... 1 more refs`, one grouped file line); callees omitted counts distinct names (`... 1 more callees`,
  explicitly NOT `... 3 more callees` that 6 raw sites would give) → proves dedup-before-limit.
- `Run_SymbolFull_Json_KeepsRawUndedupedRefsAndCallees` — parses JSON: `refs` length 4, `callees` length 6, no `×`.

## Files changed
- `src/Miller.Server/Tools/InspectTool.cs` — references/callees blocks in `RenderSymbolCompact` + two private
  helpers (`AppendGroupedReferences`, `DistinctCallees` + `DistinctCallee` record-struct). Did not touch
  `BodyPreview`/`FilterDocCommentLines` (Task 4).
- `tests/Miller.Tests/Server/InspectToolTests.cs` — `GroupAndDedupFixture` + 3 tests.
- `.razorback/sdd/task-5-report.md` — this report (overwrote a stale report from a superseded plan).

## Judgment calls
- **Insertion-ordered map for grouping** rather than relying on contiguity. Refs are already path-ordered so a
  simple consecutive group would work, but the map costs nothing and is order-robust if ordering ever changes.
- **`DistinctCallee` record-struct** for the deduped list (clear field names, cheap `with`-mutation of count).
- **Exact overview ref line** asserted (`src/a.cs:5,8,12`, `src/b.cs` absent) so the test pins that grouping runs
  on the post-`Take` slice, not the whole list.

## Self-review findings
- Existing `Run_SymbolFull_OnMethod_ShowsRefsCallersCalleesBody` still passes: GetUser's two refs live in two
  different files, so each renders as its own single-line group — `Contains` assertions unaffected. Verified green.
- Confirmed `×` (U+00D7) appears only in compact output; JSON test asserts its absence there.

## Concerns
None. No architecture impact; change is confined to two compact-render blocks + two private helpers.
