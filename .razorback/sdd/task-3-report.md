# Task 3 — inspect impact nudge

## Status
Complete. Committed `399936173e8f9185bc160d5b73856ef8718735d9` on branch `guidance-delivery`.

## What changed
- `src/Miller.Server/Tools/InspectTool.cs`
  - Added `ImpactHintMinReferences = 4` constant near the renderer (no inline magic number).
  - In `RenderSymbolCompact`, after the body/body-preview section and before the final `TrimEnd`,
    append one nudge line when `!sym.IsTest && refs.Count >= ImpactHintMinReferences`:
    `NextStepHint.Render($"impact target=\"{sym.Name}\"", $"{refs.Count} dependents")`.
    This tail runs only for overview/full (summary returns early); JSON has a separate renderer,
    so JSON output is byte-identical.
- `tests/Miller.Tests/Server/InspectToolTests.cs`
  - Added `HotSymbolFixture(refCount, isTest, name)` and 7 TDD tests: hint fires at overview + full
    with real name and count (and is the last line); absent at 3 refs, at depth=summary, for `IsTest`
    symbols, for file listings; JSON omits the hint and stays valid.

## Miller-first orientation (API-shape evidence)
Miller MCP tools were not reachable in this subagent session; equivalent evidence gathered via Read/Grep:
- `RenderSymbolCompact` (InspectTool.cs:375) — refs come from `ExtractReader.ReadReferences(dbPath, sym.Name)`
  (line 415); `refs.Count` is in method scope at the render tail (~line 470). Summary returns early at line 389,
  so the tail is overview/full only. Confirms where the hint attaches and that it renders last.
- `IndexedSymbol.IsTest` (IndexedSymbol.cs:23) — `bool` record property (julie typed `is_test`, all langs).
  Confirms the test-symbol suppression check.
- `NextStepHint.Render` (NextStepHint.cs:29) — returns single line `next: <call> — <reason>`. Confirms the shared formatter shape.
- File listings (`RenderFile`) and JSON (`RenderSymbolJson`) are separate methods — untouched, so file/JSON output is unaffected.

## Verification
`dotnet test tests/Miller.Tests --filter "FullyQualifiedName~InspectToolTests"` → Passed! 49/49, 0 failed (7 new).
Gate invariant proven: the impact nudge fires only above the dependent threshold (≥4) on non-test symbols in
compact overview/full output, and never in summary, file listings, test symbols, or JSON.

## Concerns
None. Touched only the two assigned source/test files (the report path was a stale prior-run artifact, overwritten here).
