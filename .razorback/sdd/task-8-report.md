# Task 8 — single-say recovery text (content empty search + onboarding unresolved rows)

## Status: DONE

## What changed

Two double-say trims in the render layer. No JSON changes on either surface; compact output shrinks
without losing information.

### 1. `ContentTool.RenderNoResultsCompact` (content empty search)
- **Before:** stated the recovery advice twice — once as a three-clause prose sentence
  (`Try content_kind=…; use workspace_id=all only…; use search mode=source…`) and again as the
  structured `Next:` action block, which already encodes all three (`content_kind=all-text`,
  `workspace_id=all`, `mode=source`).
- **After:** dropped the prose sentence. Kept `No results for content <op>.` and the fact line
  `Tried content_kind=<kind>.` (states what was attempted — not duplicated by Next). `AppendContentNextActions`
  unchanged, so the advice now appears exactly once.
- `RenderNoResultsJson` untouched (it only ever carried `next_actions`).

### 2. `WorkspaceRender.OnboardingCompact` hot-targets loop
- **Before:** `facts.HotTargets.Take(5)` rendered one line per row, including unresolved hashes whose
  label is the placeholder `unresolved repeated target` (conveys nothing individually).
- **After:** partition rows by the same predicate `TargetLabel` uses for the placeholder — added
  `IsUnresolvedTarget(target) => target.Path is null && target.Name is null` (equivalent to
  `Confidence == "unresolved_hash"` as produced by `WorkspaceTargetHashResolver.ResolveOne`). Resolved
  rows render as before, still capped at `.Take(5)`. Any unresolved rows (however many) collapse into a
  single aggregate line: `- unresolved repeated targets: N (M calls total)` where N = count, M = summed
  calls. If ALL hot targets are unresolved, only the aggregate line renders under `hot targets:`.
- `WriteHotTargetsJson` untouched — JSON still emits every row per-row.

## Interfaces confirmed (evidence)
- `RecoveredTargetHash` (`src/Miller.Indexing/WorkspaceTargetHashResolver.cs:22`): unresolved rows carry
  `Confidence="unresolved_hash"` with `SymbolId/Name/Kind/Path/StartLine` all null.
- `TargetLabel` (`WorkspaceRender.cs:1341`): falls through to `"unresolved repeated target"` exactly when
  `Path is null && Name is null` — the predicate reused for `IsUnresolvedTarget`.
- `SearchNoResultsNextActions`/`AppendContentNextActions`/`FormatContentActionCommand`: the Next block
  renders `content search … content_kind=source workspace_id=all` and `search … mode=source`, so the
  advice tokens survive prose removal.
- `content_kind="docs"` normalizes to canonical `workspace_docs` (`TextContentKind.cs:6`) before rendering.

Miller MCP read tools were not directly available in this agent's tool surface; per the task's own caution
(the served index lacks this branch's changes) I read the exact worktree files instead — appropriate here.

## Tests (TDD — failing first, then implement)
`tests/Miller.Tests/Server/ContentToolTests.cs`
- Rewrote `Content_SearchNoResults_CompactStatesRecoveryAdviceOnceInNextBlock`: asserts the fact line
  present, `Next:` present, `workspace_id=all` and `mode=source` each appear **exactly once**, and the three
  old prose clauses are ABSENT.

`tests/Miller.Tests/Server/WorkspaceRenderTests.cs`
- `Onboarding_Compact_CollapsesUnresolvedHotTargetsIntoOneAggregateLine` (mixed): resolved row renders
  individually; 2 unresolved rows collapse to `- unresolved repeated targets: 2 (5 calls total)`; exactly one
  aggregate line; no per-row unresolved line.
- `Onboarding_Compact_AllUnresolvedHotTargets_RendersOnlyAggregateLine`: header + single aggregate line only.
- `Onboarding_Json_KeepsUnresolvedHotTargetsPerRow`: JSON keeps all 3 rows, 2 with `confidence=unresolved_hash`
  (proves JSON unchanged).

## Invariant proven
The recovery advice on the content empty-search surface, and each unresolved hot-target, is expressed **at most
once** in compact output (advice → Next block only; unresolved rows → one aggregate line regardless of count),
while JSON on both surfaces is byte-shape unchanged.

## Verification
From the worktree:
`dotnet test tests/Miller.Tests --filter "FullyQualifiedName~ContentToolTests|FullyQualifiedName~WorkspaceRenderTests|FullyQualifiedName~WorkspaceToolTests|FullyQualifiedName~Onboarding"`
→ **Passed! Failed: 0, Passed: 116, Skipped: 0.** Build clean (warnings-as-errors, 0/0).

## Concerns
None. Ownership boundaries respected: only `ContentTool.cs` (RenderNoResultsCompact region),
`WorkspaceRender.cs` (OnboardingCompact hot-targets region + adjacent TargetLabel helper), and the two test
files were touched. The Task-2 list region and sibling-owned files were not disturbed.
