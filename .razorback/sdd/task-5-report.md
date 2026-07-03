# Task 5 Report — ServerInstructions discovery core + core gates

## Status: COMPLETE (worker scope green)

## Files changed (owned, committed)
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` — entire content replaced with the golden discovery core (verbatim from the plan, no wording change).
- `tests/Miller.Tests/Server/AgentInstructionsTests.cs` — core gates rewritten.

## Miller-first orientation (API-shape evidence)
- `inspect tests/Miller.Tests/Server/AgentInstructionsTests.cs` — confirmed the reflection mechanism: `DiscoverToolMethods()` → `ToolName(method)` exposed as `ToolNames()` MemberData, and the three constants (`MaxServerInstructionsChars=12_000` at :18, `MaxToolDescriptionChars=900`, `MaxParameterDescriptionChars=250`). Confirmed which tests read `Load()` vs a tool `[Description]` attribute before editing.
- `inspect AgentInstructions depth=overview` — confirmed the API shape I must NOT change: `public static string Load()` (no args, returns string), backed by embedded resource `MILLER_AGENT_INSTRUCTIONS.md`. The embedded-resource load path is untouched; I only replaced the resource content.

(Miller MCP serves the main checkout; all edits were made with Read/Write/Edit on worktree paths. Miller `edit` was NOT used.)

## Char measurements (gate invariant evidence)
| Doc | raw chars | CRLF-normalized |
|-----|-----------|-----------------|
| OLD `MILLER_AGENT_INSTRUCTIONS.md` | 11,856 | 11,982 |
| NEW discovery core | 1,861 | **1,887** |

- New core is 1,887 CRLF-normalized — matches the plan's pre-measurement, ≤ 1,900 gate. No whitespace trim needed; golden text shipped verbatim.
- The OLD doc's 11,982 normalized chars "passed" the fictional 12,000-char budget by 18 chars — the old gate would have failed on the next paragraph, and the doc was already ~6x past Claude Code's real ~2KB ServerInstructions truncation window (measured cut at char 2,047 on 2026-07-02).

## Gate invariants (what the rewritten tests prove)
1. `Load_CoreFitsClaudeCodeDeliveryWindow` — `Load()` after `ReplaceLineEndings("\r\n")` ≤ **1,900** chars, so the embedded doc survives Claude Code's ~2KB/shared-4KB ServerInstructions truncation. Comment cites the real limit + 2,047 measured cut (2026-07-02). Replaces the deleted 12k-fiction gate.
2. `Load_RoutingTableNamesEveryTool` — reflection-driven `[Theory]` over every `[McpServerTool]` name; asserts a routing line `- <name> — ` (em dash) exists per tool. A new tool without a routing line fails here. (Retargets the old `Load_DocumentsEveryTool` from backtick-name mention to the real routing table.)
3. `Load_ReturnsNonEmptyInstructions` — kept; still pins the lead rule `Search before reading`.
4. `Load_PinsBehavioralAdoptionLanguage` — retargeted from removed old-doc phrases to new-core behavioral phrases actually present: `One Miller call beats shell greps and full-file reads`, `Structure before content`, `Impact before changing`, `do NOT re-verify Miller results with grep/find`.

## Deletions / retargets (loss accounting)
Deleted (constant + fiction gate), as instructed:
- constant `MaxServerInstructionsChars = 12_000` → now `= 1_900`.
- `Load_StaysUnderClaudeCodeInstructionBudget` → replaced by `Load_CoreFitsClaudeCodeDeliveryWindow`.

Retargeted (intent preserved):
- `Load_DocumentsEveryTool` → `Load_RoutingTableNamesEveryTool` (same reflection MemberData; asserts routing line instead of backtick mention).
- `Load_PinsBehavioralAdoptionLanguage` (phrases swapped to new-core equivalents).

Deleted because the content is DELIBERATELY no longer in the discovery core (moved to tool `[Description]` attributes in Task 6, or to skills/docs in Task 7 / CLAUDE.md). Each is a superseded core-content assertion, not a narrowed requirement — destinations noted:
- `Load_DocumentsCrossWorkspaceReadParameters` → params now live in the tool descriptions (Task 6 description-clause gates).
- `Load_SearchModeEnum_IncludesContentMode` → search modes now in the `search` [Description] (Task 6).
- `Load_DocumentsRegionSearchAndHasDoc` → regions/has_doc now in the `search` [Description] (Task 6).
- `Load_DocumentsSubagentToolPrimer` → subagent primer relocated to skills / `docs/agent-guidance.md` (Task 7).
- `Load_DocumentsTraceRecoveryGuidance` → trace recovery detail now in the `trace` [Description] (Task 6).
- `Load_DocumentsContentAndPatternsRecoveryGuidance` → content/patterns recovery detail now in those [Description]s (Task 6).
- `Load_DocumentsOverviewFirstInspectGuidance` → overview-first guidance now in the `inspect` [Description] (Task 6; the `InspectToolDescription_*` gate already guards it).
- `Load_DocumentsDashboardLaunchWorkflow` → dashboard routing kept in the `workspace` [Description] (`WorkspaceToolDescription_RoutesDashboardLaunchRequests` still guards it) + CLAUDE.md.
- `Load_DocumentsWebContentWorkflow` → web-research workflow relocated to the `miller-web-research` skill (Task 7).
- `Load_DocumentsTokenSavingEditWorkflow` → edit selectors now in the `edit` [Description] (`EditToolDescription_DocumentsTokenSavingSelectors` still guards it).

Untouched (NOT my task): all `*ToolDescription*` gates and `ToolDescriptions_StayWithinClaudeCodeBudgets` (≤900/≤250) — these read `[Description]` attributes, which Task 5 does not modify; they still pass against the current (old) descriptions. Task 6 owns them.

Kept as-is: `Load_DoesNotAdvertiseTodosAsSeparateMcpTool`, `Load_DoesNotAdvertiseMetricsAsMcpTool` (DoesNotContain guards — still valid), `PublicMcpToolNames_AreTheDocumented1_0Surface` (reflection surface pin).

## Verification (worker red → green)
- RED (new gates vs old doc): `Failed: 11, Passed: 18, Total: 29` — `Load_CoreFitsClaudeCodeDeliveryWindow` (11982 > 1900), `Load_RoutingTableNamesEveryTool` (all 9, no `- <name> — ` in old doc), `Load_PinsBehavioralAdoptionLanguage`.
- GREEN (new gates vs golden core): `dotnet test tests/Miller.Tests --filter "FullyQualifiedName~AgentInstructionsTests"` → **Passed: 29, Failed: 0, Total: 29**, 45 ms.

## Concerns / notes for the lead
- Description-gate tests still green because descriptions are unchanged; Task 6 will retarget the description-phrase assertions when it swaps in the golden descriptions and adds the trace≤1,500 / search≤1,100 budget overrides. No conflict with my changes.
- No plan mismatch: golden core shipped verbatim, fit ≤1,900 without any whitespace trim.
- `.razorback/sdd/task-1-report.md` shows as modified from the parallel Task 1 worker — left untouched. Commit includes ONLY my two owned files.
