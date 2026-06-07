# Content corpus Phase 6 dogfood

Date: 2026-06-07

## Scope

Phase 6 adds explicit cross-workspace and audit workflows on top of the content corpus:

- `content search ... --workspace-id all` searches registered workspace content DBs and reports `display_id` plus
  `workspace_id` on each hit.
- `search --mode external|web|all-text` makes all content kinds reachable through the existing search-quality runner.
- `miller-text-audit` documents exact-term audits with bounded reads.
- `context` integration from content hits remains opt-in.

## Evidence

- Focused .NET tests: `ContentToolTests`, `CliDispatchTests`, `SearchQualityCliTests`,
  `SearchQualityParsersTests`, `SearchToolTests`, and `AgentInstructionsTests` passed with 172 tests.
- Plugin tests passed with 11 tests, including the new text-audit skill and skill mirror check.
- Cross-workspace behavior is pinned by tests that create two registered workspaces with separate `content.db`
  files and assert compact plus JSON output includes `alpha (ws-alpha)` / `beta (ws-beta)` and matching
  `workspace_id` / `display_id` fields.
- Search-quality coverage is pinned by starter cases tagged for `error-string`, `assertion`, `config-key`,
  `docs`, `external`, and `web`.

## Notes

This phase intentionally does not auto-feed content hits into `context`. The audit skill tells agents to use
bounded `content read` windows first and only call `context` when the user asks for surrounding code context.
