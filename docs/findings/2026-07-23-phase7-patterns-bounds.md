# Phase 7 Patterns Bounds Evidence

## Outcome

Phase 7B makes free-text pattern fan-out visible, restores `directory` to its literal full-parent meaning,
adds an explicit `top_directory` rollup, and removes mutable catalog counts from active guidance.

## Implementation Evidence

- Query search examines the complete observed pattern-ID population, reports considered/matched/returned/omitted
  counts, and marks truncation when the 25-ID retrieval bound omits matches.
- JSON and compact output expose the same fan-out facts.
- `PatternFactsReader` returns the full normalized parent for `directory`; `top_directory` returns only the first
  parent segment.
- A synthetic SQLite fixture aggregates 10,005 structural facts into one exact directory count.
- The MCP description, public README, agent working notes, and both mirrored patterns-audit skills use runtime
  `patterns operation=list` output as the authoritative catalog instead of freezing counts.

## Ownership

SQLite aggregation and normalization remain in `PatternFactsReader`. Request parsing, typed diagnostics,
fan-out selection, and bounded rendering remain in `PatternsTool`. Parser recognition remains in
`julie-extractors`; `Miller.Core` is unchanged.

## Verification

- RED: six focused failures proved missing fan-out fields, stale two-segment directory grouping, the absent
  explicit top-directory mode, and hardcoded description counts.
- GREEN: 37 focused `PatternsToolTests` and `PatternFactsReaderTests` passed.
- Related pattern SQL, CLI, and guidance-budget scope: 114 passed.
- `dotnet build Miller.slnx -c Release --no-restore`: zero warnings and zero errors.
- `git diff --check`, `CLAUDE.md`/`AGENTS.md` mirroring, and patterns-skill mirroring passed.

The full worker record is `.razorback/sdd/takeover-phase-7-patterns-report.md`.
