# Task 1 Report — NextStepHint shared nudge formatter

## Status
Complete. Gate green (16/16).

## Files (owned only)
- `src/Miller.Server/Tools/NextStepHint.cs` (new)
- `tests/Miller.Tests/Server/NextStepHintTests.cs` (new)

## Contract implemented
`internal static class NextStepHint` with `internal static string Render(string toolCall, string? reason = null)`:
- `next: <toolCall>` when reason null/empty/blank
- `next: <toolCall> — <reason>` with U+2014 em dash padded by single spaces when reason given
- both args trimmed; single line, no trailing newline
- throws `ArgumentException` on null/blank toolCall (via `ArgumentException.ThrowIfNullOrWhiteSpace` — null yields
  `ArgumentNullException`, a subtype of `ArgumentException`; blank yields `ArgumentException`)
- throws `ArgumentException` when toolCall or reason contains `\n`/`\r` (guards the single-line invariant)

## Miller-first orientation (calls made)
- Loaded `mcp__miller__inspect` schema via ToolSearch (`select:mcp__miller__inspect`) to confirm the tool's shape
  before use (Miller MCP server was mid-connect during orientation, so I read the exact worktree files instead of
  issuing an inspect call — worktree content is what compiles).
- Read `src/Miller.Server/Tools/WorkspaceRootSafety.cs` — confirmed: file-scoped namespace `Miller.Server.Tools`,
  XML-doc `<summary>` style, and the `ArgumentException.ThrowIfNullOrWhiteSpace(...)` guard idiom used across the
  Tools folder. Matched that guard idiom rather than a hand-rolled null/blank check.
- Read `tests/Miller.Tests/Server/FreshnessStateTests.cs` — confirmed test namespace `Miller.Tests.Server`,
  `using Xunit;`, `sealed class`, `[Fact]`/`[Theory]`/`[InlineData]` + `Assert` conventions for a pure-logic seam.

## API-shape evidence
- Neighbouring Tools files use `namespace Miller.Server.Tools;` (file-scoped) and
  `ArgumentException.ThrowIfNullOrWhiteSpace` — NextStepHint mirrors both.
- `internal` visibility chosen per the task contract (Tasks 2–4 consume it in-assembly). Confirmed reachable from
  `Miller.Tests` — the test compiled and ran, so `InternalsVisibleTo(Miller.Tests)` is already configured.

## Gate invariant
Proves the shared hint FORMAT contract Tasks 2–4 and the format-drift test depend on: exact shape (bare + em-dash
reason forms), trimming, single-line / no-trailing-newline invariant, and the null/blank + newline argument guards.

## TDD sequence
1. Wrote failing tests first; initial run: 15 passed, 1 failed (`Render_NullOrBlankToolCall_Throws(null)` — guard
   throws `ArgumentNullException`, an `ArgumentException` subtype, so exact-match `Assert.Throws<ArgumentException>`
   rejected it).
2. Relaxed that single assertion to `Assert.ThrowsAny<ArgumentException>` (contract promises `ArgumentException`;
   the standard subtype satisfies it) — kept the implementation on the idiomatic `ThrowIfNullOrWhiteSpace` guard.
3. Green.

## Test output summary
`dotnet test tests/Miller.Tests --filter "FullyQualifiedName~NextStepHintTests"` →
**Passed! Failed: 0, Passed: 16, Skipped: 0, Total: 16.**

## Concerns
None. Render-layer-only pure string seam; no I/O, no new dependencies. Downstream tasks call
`NextStepHint.Render(...)` from within `Miller.Server`.
