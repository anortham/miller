# Task 5 Report: Trace Recovery And Agent Guidance

## Summary

- Extended `trace mode=bridge` fallback next actions to use `BridgeCapabilityReport.EvidenceCounts`.
- Added `patterns` recovery actions for route structural facts:
  - `patterns operation=search query=route`
  - `patterns operation=search pattern_id=htmx.attribute.v1`
  - `patterns operation=search pattern_id=vue.route_reference.v1`
- Preserved existing fallback actions for `trace refs`, `trace auto`, and `search source`.
- Updated server agent instructions to document ASP.NET minimal API, htmx, and Vue route structural fact consumption and `patterns` fallback audits.
- Added focused compact/JSON trace fallback tests and server instruction guard assertions.

## Miller Calls Used

- `workspace status path=/Users/murphy/source/miller/.worktrees/web-stack-structural-facts-bridge`: confirmed the isolated worktree index was fresh, reader mode, revision 13.
- `context` on Task 5 trace fallback and server instruction work: confirmed relevant entry points were `TraceTool`, `BridgeFallbackNextActions`, `TraceToolTests`, and `AgentInstructionsTests`.
- `inspect BridgeFallbackNextActions`, `inspect RunBridge`, `inspect RenderBridgeJson`, `inspect WriteNextActions`, `inspect BridgeCapabilityReport`: confirmed fallback call sites, JSON next-action rendering, provider evidence JSON, and capability report shape.
- `inspect TraceToolTests` and selected bridge fallback tests: confirmed existing helper shape and compatibility assertions before adding tests.
- `inspect AgentInstructionsTests`: confirmed existing instruction budget and recovery guidance assertions.
- `impact target=BridgeFallbackNextActions`: confirmed the planned code change flows through `RunBridge` and `Run`.
- `impact git=true` after edits: confirmed changed trace next-action surface and likely tests; worker scope covered `TraceToolTests` and `AgentInstructionsTests`.

## Verification Ledger

| Scope | Invariant | Command | Commit SHA / working tree | Result | Timestamp |
| --- | --- | --- | --- | --- | --- |
| RED worker scope | New tests fail for missing pattern fallback and instruction guidance | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~TraceToolTests|FullyQualifiedName~AgentInstructionsTests"` | `851b1cf646c9696acca193f8a93f40768defa75e` + test-only working tree | Failed as expected: 5 failures from new assertions, 86 passed | 2026-06-30T15:36:06Z |
| GREEN worker scope | Trace fallback and instruction behavior pass at worker scope | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~TraceToolTests|FullyQualifiedName~AgentInstructionsTests"` | `851b1cf646c9696acca193f8a93f40768defa75e` + Task 5 working tree | Passed: 92 passed, 0 failed, 0 skipped | 2026-06-30T15:36:06Z |
| Whitespace | Diff has no whitespace errors | `git diff --check` | `851b1cf646c9696acca193f8a93f40768defa75e` + Task 5 working tree | Passed | 2026-06-30T15:36:06Z |

## Acceptance Checklist

- [x] Compact bridge fallback includes patterns next actions when bridge route facts are relevant.
- [x] JSON bridge fallback includes equivalent structured next actions.
- [x] Server instructions document htmx/Vue route fact consumption and pattern-audit fallback.
- [x] Existing bridge trace output for successful paths remains compatible; successful bridge rendering code was not changed.
- [x] Worker-scope verification passes.
- [x] Task implementation commit created: `6783e54b098f5ea608f829588b150de435575be0`.

## Concerns Or Plan Mismatches

- The server instruction file was already close to its guarded 12,000-character budget. I trimmed duplicate subagent primer wording to keep the new route-fact guidance under budget without weakening tested guidance.
- No plan mismatches. Core bridge construction, reducer behavior, `PatternsTool`, and MCP tool surface were not changed.
- Lead inline review found no remaining Task 5 issues.

## Commit SHA

- Pre-commit base: `851b1cf646c9696acca193f8a93f40768defa75e`
- Task implementation commit: `6783e54b098f5ea608f829588b150de435575be0`
- Report update commit: `ef48fe05360b197397cc8b271bf86e513e832fbf`
