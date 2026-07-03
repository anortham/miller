# Task 2 — search success nudge

**Status:** complete
**Commit:** 44d286d `feat(search): next-step inspect nudge on symbol hits`
**Branch/worktree:** guidance-delivery @ `/Users/murphy/source/miller/.worktrees/guidance-delivery`

## What shipped

When compact (non-JSON) symbol search returns hits, `SearchTool.Run` now appends one final line:

```
next: inspect target="<top-hit name>" depth=overview
```

rendered through the shared `NextStepHint.Render` formatter (Task 1). The hint is the last line,
exactly once per response, and names the top-ranked kept hit (`kept[0].Name`).

### Suppression (verified by tests)
- **JSON output** — returned before the nudge branch; byte-identical.
- **File-path top hit** — `fileMode` returns via `RenderFileCompact` before the nudge branch.
- **Empty results** — the `total == 0` failure paths return earlier and own their own hints.
- **Text mode** — routes through `Run` and renders symbol shape, but is gated out; only
  `mode is Auto or Symbol` nudges.
- **content/source/external/web/all-text/markers/regions modes** — structurally never reach `Run`
  (they route through `RunContent`/`RunContentCorpus`/`RunTextContent`/`RunRegions`/`RunMarkers`),
  so no nudge is possible.

### Escaping
Symbol name is escaped via a new private `EscapeCallString` (backslash-first, then quote) mirroring
`ContextTool.NextInspectLine`, so a name containing `"` or `\` stays a single well-formed line.

## Design decision — placement inside `Run`
File ownership was decisive. `SearchTool.Search` dispatches the symbol route through
`SearchRouteExecutor.RunSymbols` (a file I do not own), which passes `Run`'s output through
unchanged. `Run` is the only place in `SearchTool.cs` with access to `kept[0]`, `fileMode`, `mode`,
and `json`, so the nudge is computed there and flows straight to the response. This also matches the
sibling precedent (InspectTool/TraceTool append their nudge inside the pure render path).

Auto-mode rescue (`RenderAutoTextRescueCompact`) wraps `Run`'s primary output when symbol results are
weak; in that case the nudge concludes the primary symbol block and the rescue's own `Rerun with
mode=…` addendum follows. Existing rescue tests use `Assert.Contains` and remain green. Still exactly
one `next:` line per response.

## Miller-first orientation (calls made)
- `inspect src/Miller.Server/Tools/SearchTool.cs kind=method` — confirmed the symbol map: `Run :592`
  is the symbol-route core; `RunContent*`/`RunTextContent`/`RunRegions` are separate methods (so
  content/markers modes never reach `Run`); confirmed my added `EscapeCallString :1565` and the
  `RenderCompact`/`RenderFileCompact`/`RenderDefinitionCompact` renderers. Validated the API shape
  used for placement.
- Cross-checked `SearchRouteExecutor.RunSymbols` (read-only) — verified it returns `Run`'s output
  verbatim in `SearchRouteExecutionResult`, so a nudge added in `Run` reaches the client unmodified.
- Used Read/Edit only on worktree paths; never used Miller `edit`.

## Tests (TDD: red → green)
Added to `SearchToolTests.cs`:
- `Run_SymbolSuccess_AppendsInspectNudgeNamingTopHit` (Theory: Auto, Symbol) — hint is last line,
  names real top hit, exactly one `next:` line.
- `Run_SymbolNudge_EscapesQuotesAndBackslashesInName` — quote/backslash escaping.
- `Run_TextMode_DoesNotAppendInspectNudge`
- `Run_FileTopHit_DoesNotAppendInspectNudge`
- `Run_EmptySymbolResults_DoNotAppendInspectNudge`
- `Run_SymbolJson_RemainsByteIdenticalWithoutNudge`
- `RunContentCorpus_Compact_DoesNotAppendInspectNudge`

Updated two existing tests for the new (correct) behavior:
- `Run_SymbolModes_RenderExactCompactShape` — now mode-aware (Auto/Symbol expect the nudge suffix;
  Text does not).
- `Run_PreservesIndexOrdering_DoesNotReSort` — line parser skips the `next:` nudge line.

### Verification
- `dotnet test --filter FullyQualifiedName~SearchToolTests` → **Passed! 92/0/0**.
- `scripts/test.sh` (full fast suite) → **Passed! 2750/0/0**, 9s (under the 30s budget).

## Concerns
- Under auto-mode source/docs rescue the `next:` nudge is not the literal final line (the rescue
  addendum follows it); it still appears exactly once and concludes the primary block. This is
  one-line-max compliant. If the plan intends the nudge strictly after the rescue addendum, that
  would require touching the rescue wrapper — flagged rather than silently changed.
- No other files touched. Sibling report files (`task-1/3/4/5-report.md`) were left dirty and out of
  my commit.

## Fix follow-up — rescue owns its closing affordance (lead ruling)
Lead ruled the nudge must be **suppressed entirely** when the auto-mode source/docs/config rescue
fires, not merely precede it. Design rule: max one next-step affordance per output; the rescue's
`Rerun with mode=…` line is the single closing affordance, and it only fires when symbol results look
weak, so a `next: inspect` on a weak top hit competes with it.

- `RenderAutoTextRescueCompact` (SearchTool.cs) now strips the trailing `next: ` line from
  `primaryOutput` via new helper `StripTrailingNextHint` before composing the rescue block.
  Deterministic: NextStepHint emits the hint as one line with no trailing newline, so dropping the
  final line iff it starts with `"next: "` is unambiguous. Rationale recorded in a code comment.
- New TDD tests (SearchToolTests.cs): `Search_AutoMode_SourceRescue_SuppressesInspectNudge` and
  `Search_AutoMode_DocsConfigRescue_SuppressesInspectNudge` — assert the rescue output contains NO
  `next: ` line and still carries `Rerun with mode=source`/`mode=content`. Non-rescue symbol
  successes keep the nudge (existing tests stay green).
- Verification: `dotnet test tests/Miller.Tests --filter FullyQualifiedName~SearchToolTests` →
  **Passed! 94/0/0**.
