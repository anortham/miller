# Miller documentation map

This directory mixes active contracts, current operating docs, historical design notes, and dogfood evidence.
Use this page to avoid treating old milestone plans as the current product contract.

## Current docs

- [`../README.md`](../README.md) - public entry point, install paths, current architecture summary, CLI surface.
- [`../CLAUDE.md`](../CLAUDE.md) / [`../AGENTS.md`](../AGENTS.md) - source-of-truth agent working notes and generated mirror.
- [`contracts/cli-eros-v1.md`](contracts/cli-eros-v1.md) - active Eros-facing CLI/export contract.
- [`contracts/workspace-health-v1.md`](contracts/workspace-health-v1.md) - active workspace health JSON contract.
- [`contracts/trace-json-v1.md`](contracts/trace-json-v1.md) - active trace JSON contract for auto/path/bridge output.
- [`contracts/content-corpus-v1.md`](contracts/content-corpus-v1.md) - active content corpus schema/export contract.
- [`plans/2026-06-09-patterns-tool-design.md`](plans/2026-06-09-patterns-tool-design.md) - design for the future `patterns` tool over extractor structural facts.
- [`plans/2026-06-09-patterns-tool-implementation-plan.md`](plans/2026-06-09-patterns-tool-implementation-plan.md) - implementation plan for the future `patterns` MCP/CLI surface.
- [`release-process.md`](release-process.md) - current release validation and promotion flow.
- [`release-notes/v0.3.5.md`](release-notes/v0.3.5.md) - latest published release notes.
- [`findings/2026-06-08-v0.3.5-release-verification.md`](findings/2026-06-08-v0.3.5-release-verification.md) - live `v0.3.5` release verification.
- [`plans/2026-06-09-miller-quality-review-goal-design.md`](plans/2026-06-09-miller-quality-review-goal-design.md) - active long-running quality review goal design.
- [`plans/2026-06-09-miller-quality-review-goal-implementation-plan.md`](plans/2026-06-09-miller-quality-review-goal-implementation-plan.md) - execution plan for the long-running quality review goal.
- [`release-notes/v0.3.4.md`](release-notes/v0.3.4.md) - historical `v0.3.4` release notes.
- [`findings/2026-06-08-v0.3.4-release-verification.md`](findings/2026-06-08-v0.3.4-release-verification.md) - live `v0.3.4` release verification.
- [`release-notes/v0.3.3.md`](release-notes/v0.3.3.md) - historical `v0.3.3` release notes.
- [`findings/2026-06-08-v0.3.3-release-verification.md`](findings/2026-06-08-v0.3.3-release-verification.md) - live `v0.3.3` release verification.
- [`release-notes/v0.3.2.md`](release-notes/v0.3.2.md) - historical `v0.3.2` release notes.
- [`findings/2026-06-08-v0.3.2-release-verification.md`](findings/2026-06-08-v0.3.2-release-verification.md) - live `v0.3.2` release verification.
- [`release-notes/v0.3.1.md`](release-notes/v0.3.1.md) - historical `v0.3.1` release notes.
- [`findings/2026-06-08-v0.3.1-release-verification.md`](findings/2026-06-08-v0.3.1-release-verification.md) - live `v0.3.1` release verification.
- [`findings/2026-06-08-v0.3.0-release-verification.md`](findings/2026-06-08-v0.3.0-release-verification.md) - live `v0.3.0` release verification.
- [`findings/2026-06-08-v0.3.0-release-prep-review.md`](findings/2026-06-08-v0.3.0-release-prep-review.md) - `0.3.0` release-prep review.
- [`release-notes/v0.2.0.md`](release-notes/v0.2.0.md) - historical `v0.2.0` release notes.
- [`search-quality-runner.md`](search-quality-runner.md) - current search-quality comparison runner.
- [`plans/2026-06-04-symbol-search-collapsed-trigram-design.md`](plans/2026-06-04-symbol-search-collapsed-trigram-design.md) - active symbol-search sidecar design history, with current fail-visible behavior noted.
- [`plans/2026-06-07-content-corpus-fts5-search-plan.md`](plans/2026-06-07-content-corpus-fts5-search-plan.md) - implemented content corpus plan; current contract is in `contracts/content-corpus-v1.md`.

## Historical evidence

The `m*-design.md`, `miller-mvp-plan.md`, most dated `docs/plans/`, and dated `docs/findings/` files are
historical design records, implementation plans, and dogfood evidence. Keep them when they explain why a decision
was made, but do not use them as current behavior unless a current doc above links to them for that purpose.

## Cleanup rule

Remove duplicate drafts and abandoned plans when they are unreferenced and actively misleading. Otherwise, prefer a
short historical-status banner over rewriting evidence docs to match today's implementation.
