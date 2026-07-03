# Task 4 — trace refs nudge

**Status:** ✅ Complete
**Commit:** `a6f9029` — `feat(trace): impact nudge after non-empty refs`
**Branch/worktree:** `guidance-delivery` @ `/Users/murphy/source/miller/.worktrees/guidance-delivery`

> Note: this file previously held an unrelated "inspect depth=overview strips doc-comment lines"
> report (a different plan run). Overwritten per the Task 4 assignment, which designated this path.

## What changed

In `TraceTool.RunRefs` compact (non-JSON) `mode=refs` output, when at least one reference is
shown AND the resolved target is not a test symbol (`targetSymbol.IsTest == false`), a single
final line is appended after the references block and after any truncation note:

```
next: impact target="<target name>" — before editing
```

Rendered via `NextStepHint.Render($"impact target=\"{targetSymbol.Name}\"", "before editing")`.

- **JSON byte-identical** — the JSON branch returns earlier (before the compact `StringBuilder`
  render), so it is untouched.
- **Empty refs unchanged** — the `shown.Length == 0` branch (existing recovery hint + `next_actions`)
  is the `if` arm; the nudge lives only in the `else` (non-empty) arm.
- **Test targets suppressed** — `if (!targetSymbol.IsTest)` guard.
- **Exactly one hint line max** — appended once at the tail; `TrimEnd('\n')` keeps it as the last line.

Files touched (only these two):
- `src/Miller.Server/Tools/TraceTool.cs` — the `else` branch of the compact refs render.
- `tests/Miller.Tests/Tools/TraceToolTests.cs` — 5 new tests.

## Miller-first orientation (calls + evidence)

- `inspect src/Miller.Server/Tools/TraceTool.cs` → located `RunRefs` (:492) and `ReferenceLine`
  (:600); confirmed the compact refs render lives inside `RunRefs`, JSON path exits earlier.
- Read `src/Miller.Server/Tools/NextStepHint.cs` → confirmed
  `internal static string Render(string toolCall, string? reason = null)` yields
  `next: <toolCall> — <reason>` (U+2014, single line, no trailing newline).
- Read `TraceTool.cs` refs region → confirmed `targetSymbol` is non-null at the render (guarded at
  :526), carries `IsTest`, and that the truncation note (`"reference trace truncated by limit."`)
  is appended inside the same `else` arm before the tail.

## TDD

Tests written first (red), then implementation (green):
1. `Refs_NonEmpty_AppendsImpactNudge_ForNonTestTarget` — hint present, real target name, ends the output, exactly one `next:` line.
2. `Refs_NonEmpty_TruncationNote_KeepsImpactNudgeAsFinalLine` — nudge renders after the truncation note.
3. `Refs_NonEmpty_TestTarget_SuppressesImpactNudge` — no `next:` for an `IsTest` target.
4. `Refs_Empty_HasNoImpactNudge` — empty path emits no `next: impact`.
5. `Refs_NonEmpty_Json_HasNoImpactNudge` — JSON still well-formed, no `next: impact`.

## Verification

`dotnet test tests/Miller.Tests --filter "FullyQualifiedName~TraceToolTests"` →
**Passed! Failed: 0, Passed: 91, Skipped: 0** (188 ms). Build clean under warnings-as-errors.

Gate invariant satisfied: the refs→impact nudge fires only on non-empty, non-test compact refs
output.

## Concerns

None. Scope stayed within the two owned files; other worktree changes
(`.razorback/sdd/task-1-report.md`, `task-5-report.md`) are other agents' and were left untouched.
