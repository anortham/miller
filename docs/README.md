# Miller documentation map

This directory mixes active contracts, current operating docs, historical design notes, and dogfood evidence.
Use this page to avoid treating old milestone plans as the current product contract.

## Current docs

- [`../README.md`](../README.md) - public entry point, install paths, current architecture summary, CLI surface.
- [`../CLAUDE.md`](../CLAUDE.md) / [`../AGENTS.md`](../AGENTS.md) - source-of-truth agent working notes and generated mirror.
- [`contracts/cli-eros-v1.md`](contracts/cli-eros-v1.md) - active Eros-facing CLI/export contract.
- [`contracts/workspace-status-v1.md`](contracts/workspace-status-v1.md) - active workspace status JSON contract.
- [`contracts/workspace-health-v1.md`](contracts/workspace-health-v1.md) - active workspace health JSON contract.
- [`contracts/workspace-onboarding-v1.md`](contracts/workspace-onboarding-v1.md) - active telemetry-derived workspace onboarding JSON contract.
- [`contracts/workspace-leader-json-v1.md`](contracts/workspace-leader-json-v1.md) - active workspace leader diagnostics and graceful handoff JSON contract.
- [`contracts/refresh-wait-v1.md`](contracts/refresh-wait-v1.md) - active refresh/wait JSON contract for Eros convergence.
- [`contracts/trace-json-v1.md`](contracts/trace-json-v1.md) - active trace JSON contract for auto/path/refs/bridge output and additive `next_actions`.
- [`contracts/content-corpus-v1.md`](contracts/content-corpus-v1.md) - active content corpus schema/export contract.
- [`contracts/references-export-v1.md`](contracts/references-export-v1.md) - active references/usage JSONL export contract.
- [`contracts/patterns-json-v1.md`](contracts/patterns-json-v1.md) - active patterns JSON contract over extractor structural facts.
- [`contracts/metrics-json-v1.md`](contracts/metrics-json-v1.md) - active metrics JSON contract for churn, clone groups, and complexity ranking.
- [`plans/2026-06-26-miller-five-gap-implementation-plan.md`](plans/2026-06-26-miller-five-gap-implementation-plan.md) - implementation plan for metrics, empty-state recovery, leader handoff, dashboard panels, and clone/complexity discovery.
- [`findings/2026-06-26-five-gap-implementation.md`](findings/2026-06-26-five-gap-implementation.md) - implementation evidence and boundary notes for the five-gap plan.
- [`findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md`](findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md) - active Miller/Julie foundation matrix finding and adaptation-candidate ranking.
- [`plans/2026-06-28-overview-first-agent-workflow-guidance-plan.md`](plans/2026-06-28-overview-first-agent-workflow-guidance-plan.md) - approved implementation plan for overview-first inspect guidance, onboarding hints, public docs, and focused matrix evidence.
- [`plans/2026-06-28-trace-graph-workflow-recovery-guidance-plan.md`](plans/2026-06-28-trace-graph-workflow-recovery-guidance-plan.md) - approved implementation plan for trace no-path, bridge fallback, ambiguity, docs, and matrix evidence guidance.
- [`plans/2026-06-28-trace-content-patterns-quality-goal-plan.md`](plans/2026-06-28-trace-content-patterns-quality-goal-plan.md) - proposed long-running goal plan for improving `trace`, `content`, and `patterns` usefulness, recovery output, guidance, and focused evidence.
- [`plans/2026-06-09-patterns-tool-design.md`](plans/2026-06-09-patterns-tool-design.md) - design record for the `patterns` tool over extractor structural facts.
- [`plans/2026-06-09-patterns-tool-implementation-plan.md`](plans/2026-06-09-patterns-tool-implementation-plan.md) - implementation plan for the `patterns` MCP/CLI surface.
- [`release-process.md`](release-process.md) - current release validation and promotion flow.
- [`release-notes/v1.1.2.md`](release-notes/v1.1.2.md) - latest release notes.
- [`findings/2026-06-27-v1.1.2-release-verification.md`](findings/2026-06-27-v1.1.2-release-verification.md) - live `v1.1.2` release verification.
- [`release-notes/v1.1.1.md`](release-notes/v1.1.1.md) - historical `v1.1.1` release notes.
- [`findings/2026-06-26-v1.1.1-release-verification.md`](findings/2026-06-26-v1.1.1-release-verification.md) - historical live `v1.1.1` release verification.
- [`release-notes/v1.1.0.md`](release-notes/v1.1.0.md) - historical `v1.1.0` release notes.
- [`findings/2026-06-26-v1.1.0-release-verification.md`](findings/2026-06-26-v1.1.0-release-verification.md) - live `v1.1.0` release verification.
- [`release-notes/v1.0.1.md`](release-notes/v1.0.1.md) - historical `v1.0.1` release notes.
- [`findings/2026-06-26-v1.0.1-release-verification.md`](findings/2026-06-26-v1.0.1-release-verification.md) - live `v1.0.1` release verification.
- [`plans/2026-06-25-mcp-roots-workspace-binding-design.md`](plans/2026-06-25-mcp-roots-workspace-binding-design.md) - current Cursor/MCP roots binding design and fallback-root guardrails.
- [`findings/2026-06-25-cursor-project-local-mcp-config.md`](findings/2026-06-25-cursor-project-local-mcp-config.md) - superseded interim Cursor project-local MCP workaround.
- [`release-notes/v1.0.0.md`](release-notes/v1.0.0.md) - historical `v1.0.0` release notes.
- [`findings/2026-06-24-v1.0.0-release-verification.md`](findings/2026-06-24-v1.0.0-release-verification.md) - live `v1.0.0` release verification.
- [`release-notes/v0.5.8.md`](release-notes/v0.5.8.md) - historical `v0.5.8` release notes.
- [`findings/2026-06-23-v0.5.8-release-verification.md`](findings/2026-06-23-v0.5.8-release-verification.md) - live `v0.5.8` release verification.
- [`release-notes/v0.5.7.md`](release-notes/v0.5.7.md) - historical `v0.5.7` release notes.
- [`findings/2026-06-22-v0.5.7-release-verification.md`](findings/2026-06-22-v0.5.7-release-verification.md) - live `v0.5.7` release verification.
- [`release-notes/v0.5.6.md`](release-notes/v0.5.6.md) - historical `v0.5.6` release notes.
- [`findings/2026-06-20-v0.5.6-release-verification.md`](findings/2026-06-20-v0.5.6-release-verification.md) - live `v0.5.6` release verification.
- [`release-notes/v0.5.5.md`](release-notes/v0.5.5.md) - historical `v0.5.5` release notes.
- [`findings/2026-06-19-v0.5.5-release-verification.md`](findings/2026-06-19-v0.5.5-release-verification.md) - live `v0.5.5` release verification.
- [`findings/2026-06-16-v0.5.4-release-verification.md`](findings/2026-06-16-v0.5.4-release-verification.md) - live `v0.5.4` release verification.
- [`release-notes/v0.5.4.md`](release-notes/v0.5.4.md) - historical `v0.5.4` release notes.
- [`release-notes/v0.5.3.md`](release-notes/v0.5.3.md) - historical `v0.5.3` release notes.
- [`findings/2026-06-15-v0.5.3-release-verification.md`](findings/2026-06-15-v0.5.3-release-verification.md) - live `v0.5.3` release verification.
- [`release-notes/v0.5.2.md`](release-notes/v0.5.2.md) - historical `v0.5.2` release notes.
- [`findings/2026-06-14-v0.5.2-release-verification.md`](findings/2026-06-14-v0.5.2-release-verification.md) - live `v0.5.2` release verification.
- [`release-notes/v0.5.1.md`](release-notes/v0.5.1.md) - historical `v0.5.1` release notes.
- [`findings/2026-06-13-v0.5.1-release-verification.md`](findings/2026-06-13-v0.5.1-release-verification.md) - live `v0.5.1` release verification.
- [`release-notes/v0.5.0.md`](release-notes/v0.5.0.md) - historical `v0.5.0` release notes.
- [`findings/2026-06-12-v0.5.0-release-verification.md`](findings/2026-06-12-v0.5.0-release-verification.md) - live `v0.5.0` release verification.
- [`release-notes/v0.3.6.md`](release-notes/v0.3.6.md) - historical `v0.3.6` release notes.
- [`findings/2026-06-09-v0.3.6-release-verification.md`](findings/2026-06-09-v0.3.6-release-verification.md) - live `v0.3.6` release verification.
- [`release-notes/v0.3.5.md`](release-notes/v0.3.5.md) - historical `v0.3.5` release notes.
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
- [`reference/static-ssr-htmx-alpine-pattern.md`](reference/static-ssr-htmx-alpine-pattern.md) - Miller UI pattern for static SSR Razor, Minimal API fragments, htmx, and Alpine CSP (`Miller.Dashboard` reference).

## Historical evidence

The `m*-design.md`, `miller-mvp-plan.md`, most dated `docs/plans/`, and dated `docs/findings/` files are
historical design records, implementation plans, and dogfood evidence. Keep them when they explain why a decision
was made, but do not use them as current behavior unless a current doc above links to them for that purpose.

## Cleanup rule

Remove duplicate drafts and abandoned plans when they are unreferenced and actively misleading. Otherwise, prefer a
short historical-status banner over rewriting evidence docs to match today's implementation.
