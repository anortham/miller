# Task 6 report — nine golden tool descriptions + description gates

**Status:** complete, worker scope green.
**Branch/worktree:** `guidance-delivery` @ `/Users/murphy/source/miller/.worktrees/guidance-delivery`.
(This file previously held a superseded, unrelated "search mode=markers collapse" Task 6 report; overwritten per
the guidance-delivery plan's Task 6 report destination.)

## What changed

Replaced the `[Description]` attribute on the `[McpServerTool]` method of all nine tools with the pre-approved
golden text (impl-plan § "Tool descriptions (Task 6) — final text per tool"), and rewrote the description gates in
`AgentInstructionsTests.cs`. No method bodies, parameter descriptions, or other file content touched.

Ten files (all owned):
- `src/Miller.Server/Tools/{SearchTool,InspectTool,ContextTool,TraceTool,ImpactTool,EditTool,PatternsTool,ContentTool,WorkspaceTool}.cs`
- `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

## Golden-text fidelity

Each new attribute was reconstructed (concatenated string literals joined) and compared **byte-for-byte** to the
plan's blockquote text — all nine `match=True`, no wording change, only concatenation whitespace.

## Per-tool char counts (attribute description text, after)

| tool | chars | budget | result |
|------|------:|-------:|--------|
| search | 815 | 1,100 | OK |
| inspect | 518 | 900 | OK |
| context | 496 | 900 | OK |
| trace | 947 | 1,500 | OK |
| impact | 605 | 900 | OK |
| edit | 668 | 900 | OK |
| patterns | 593 | 900 | OK |
| content | 543 | 900 | OK |
| workspace | 636 | 900 | OK |

## Before/after totals

- **Descriptions-only total (design §4 baseline metric): before 4,512 → after 5,821 chars** (≤ 9,000). The
  impl-plan quotes "4,522"; design §4 quotes "4,512"; direct source measurement gives **4,512** (the ~10-char
  delta is em-dash/escape counting). Used the design's exact 4,512 baseline.
- Parameter-description text (unchanged by this task): **7,853 chars**.
- Full serialized schema (descriptions + params): before 12,365 → after **13,674 chars** (reported for
  transparency; see the gate-semantics note).

## Gate rewrite (AgentInstructionsTests.cs)

Removed the single `ToolDescriptions_StayWithinClaudeCodeBudgets` (≤900) theory and the five bespoke old-wording
content assertions (`InspectToolDescription_DocumentsOverviewFirstGuidance`,
`TraceToolDescription_DocumentsRecoveryGuidance`, `ContentAndPatternsToolDescriptions_DocumentRecoveryGuidance`,
`WorkspaceToolDescription_RoutesDashboardLaunchRequests`, `EditToolDescription_DocumentsTokenSavingSelectors`) —
those pinned pre-golden phrasing and are superseded by the generic golden-clause gate. New gates:

1. **Per-tool budget** — `ToolDescriptions_StayWithinPerToolBudget` (theory over reflected tool methods),
   table-driven: default 900, documented overrides `trace`=1,500, `search`=1,100. Comment records the client-side
   ~2KB/description hard cap.
2. **Golden-clause assertions** — `ToolDescriptions_AreSelfSufficientUsageContracts` (theory): every description
   contains `NOT for:` and an example call; for the seven tools cut by the old ~2KB window (context, trace,
   impact, edit, patterns, content, workspace) the `NOT for:` clause (extracted from `NOT for:` up to the example)
   must name ≥1 other Miller tool by name.
   - **Note:** the example check matches `Example` (not the literal `Example:`) because the `patterns` golden text
     legitimately uses the plural `Examples:`; requiring `Example:` verbatim would force a wording change to golden
     text (a plan mismatch). Matching `Example` faithfully validates the example-clause contract for all nine.
3. **Total schema budget** — `CombinedToolDescriptions_StayWithinTotalSchemaBudget` (fact): asserts the combined
   nine-description text ≤ 9,000; failure message includes the measured description total, the parameter total, and
   the full schema total.
4. **Parameter-description gate (≤250)** — preserved verbatim in a dedicated theory
   `ToolParameterDescriptions_StayWithinBudget` (same logic, unchanged threshold).

Task 5's core gates were **not** touched (`Load_CoreFitsClaudeCodeDeliveryWindow`, `Load_RoutingTableNamesEveryTool`,
`Load_PinsBehavioralAdoptionLanguage`), nor the todos/metrics negative tests or `PublicMcpToolNames`.

## Gate-semantics note (one concern for the lead to confirm)

The lead/impl-plan/CLAUDE.md phrase the total gate as "description **+ all parameter-description** text ≤ 9,000".
That is **unsatisfiable as literally written**: parameter descriptions alone are 7,853 chars, so descriptions+params
is 12,365 today and 13,674 after — already far over 9,000 before this task, and no in-scope golden/param edit can
fix it. The design doc's own recorded baseline resolves the intent: design §4 states "before (4,512 chars today)",
and **4,512 is exactly the descriptions-only total** (a params-inclusive baseline would read 12,365). Items 4 (each
param ≤250) and 5 (total ≤9,000) are the two separate schema-bloat guards; the ≤9,000 total tracks the description
text that actually grows. I implemented the gate as **descriptions-only ≤ 9,000** (before 4,512 → after 5,821) —
the only reading consistent with the design's stated baseline and achievable — while the test's failure message
still surfaces the full descriptions+params schema total. If the lead intends a params-inclusive ceiling, the
threshold must rise above ~13,674 (a design decision), so it is flagged here rather than silently chosen.

## TDD evidence (red → green)

- **Red** (new gates vs. old descriptions): `ToolDescriptions_AreSelfSufficientUsageContracts` failed for all nine
  ("… description must include a 'NOT for:' routing clause") — 9 failed / 34 passed / 43 total.
- **Green** (after golden swap): **43 passed / 0 failed / 0 skipped**, 43 ms.

## Verification

`cd .worktrees/guidance-delivery && dotnet test tests/Miller.Tests --filter "FullyQualifiedName~AgentInstructionsTests"`
→ **Passed! Failed: 0, Passed: 43, Skipped: 0, Total: 43**. Build succeeded under warnings-as-errors (0 warnings).

**Gate invariant proven:** every tool description is a self-sufficient post-discovery usage contract — carries a
`NOT for:` routing clause (naming another tool for the seven cut tools) and a copyable example — within its
per-tool budget and the combined description budget.

## Miller-first orientation (evidence)

- `grep [McpServerTool(Name=…)]` across the nine tool files confirmed each decorated method + line, so each
  `[Description]` edit targeted the method attribute only (not a parameter/class attribute).
- `Read` on each worktree file confirmed the exact current concatenated-literal attribute text before editing
  (Miller `edit` never used; worktree text read directly per task rule).
- Miller `inspect` (summary) confirmed API shape: `EditTool.cs` → `Edit :50 [McpServerTool(Name = "edit")]`;
  `AgentInstructionsTests.cs` → gate members `ToolDescriptions_StayWithinPerToolBudget :152`,
  `ToolParameterDescriptions_StayWithinBudget :167`, `ToolDescriptions_AreSelfSufficientUsageContracts :184`,
  `CombinedToolDescriptions_StayWithinTotalSchemaBudget :215`, and constants `MaxCombinedToolDescriptionChars=9_000`,
  `DefaultToolDescriptionChars=900`, `MaxParameterDescriptionChars=250`.
