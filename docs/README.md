# Miller documentation map

This directory mixes active contracts, current operating docs, historical design notes, and dogfood evidence.
Use this page to avoid treating old milestone plans as the current product contract.

## Current docs

- [`../README.md`](../README.md) - public entry point, install paths, current architecture summary, CLI surface.
- [`../CLAUDE.md`](../CLAUDE.md) / [`../AGENTS.md`](../AGENTS.md) - source-of-truth agent working notes and generated mirror.
- [`agent-guidance.md`](agent-guidance.md) - long-form agent reference: full workflow catalog, subagent-dispatch primer, and per-tool parameter detail relocated from the embedded ≤1,900-char ServerInstructions core.
- [`agent-setup-snippet.md`](agent-setup-snippet.md) - copy-paste CLAUDE.md/AGENTS.md/Cursor-rule routing snippet that keeps agents preferring Miller over shell grep when harnesses defer MCP tool schemas.
- [`adr/ADR-0001-guidance-delivery-channels.md`](adr/ADR-0001-guidance-delivery-channels.md) - accepted decision record for the three guidance channels (ServerInstructions discovery core, tool-description usage contracts, NextStepHint nudges) and their budgets.
- [`adr/ADR-0003-semantic-retrieval-ownership.md`](adr/ADR-0003-semantic-retrieval-ownership.md) - accepted decision record moving optional local semantic retrieval to Miller and reserving fleet-level semantics for Eros.
- [`plans/2026-07-22-miller-julie-agent-efficiency-decision-design.md`](plans/2026-07-22-miller-julie-agent-efficiency-decision-design.md) - accepted three-day agent-in-the-loop design for the immediate Miller-versus-Julie product decision, including correctness and efficiency gates plus controlled BGE-small/CodeRankEmbed diagnostics.
- [`plans/2026-07-22-miller-julie-agent-efficiency-implementation-plan.md`](plans/2026-07-22-miller-julie-agent-efficiency-implementation-plan.md) - execution-ready contract, proxy, Codex runner, additive scorer, visible calibration, and user-controlled sealed-verdict plan for the three-day decision.
- [`contracts/cli-eros-v1.md`](contracts/cli-eros-v1.md) - active Eros-facing CLI/export contract.
- [`contracts/canary-telemetry-v3.md`](contracts/canary-telemetry-v3.md) - Active bounded-decision semantic-canary contract: privacy-safe source identity, 100% identifier shadow, warm latency buckets, and multi-export aggregation.
- [`contracts/canary-telemetry-v2.md`](contracts/canary-telemetry-v2.md) - Compatible legacy semantic-canary contract for `MILLER_SEMANTIC_CANARY=on|1`; v2 remains readable but never pools with v3.
- [`contracts/canary-telemetry-v1.md`](contracts/canary-telemetry-v1.md) - Historical first semantic-canary contract; v1 rows are excluded from the active gate.
- [`findings/2026-07-21-p5-canary-runbook.md`](findings/2026-07-21-p5-canary-runbook.md) - Semantic canary operator runbook: v2/v3 profiles, weekly exports, day-14 review, day-30 promote/remove verdict, and rollback.
- [`findings/2026-07-22-semantic-decision-readiness.md`](findings/2026-07-22-semantic-decision-readiness.md) - Frozen local v3 cohort identity, client enrollment, resource measurements, smoke evidence, and remaining promotion blockers.
- [`findings/2026-07-22-semantic-decision-baseline.md`](findings/2026-07-22-semantic-decision-baseline.md) - Correct-mode visible retrieval replay and exact lexical/semantic/production comparison for the bounded decision.
- [`findings/2026-07-21-semantic-production-readiness-evaluation.md`](findings/2026-07-21-semantic-production-readiness-evaluation.md) - clean-corpus semantic go/no-go evidence and the evaluation-only verdict at commit `a547475`.
- [`contracts/semantic-sidecar-protocol-v1.md`](contracts/semantic-sidecar-protocol-v1.md) - frozen `julie.embedding.sidecar` v1 wire protocol, model knob table, `prepare` subcommand, backend selection, and conformance bars for `julie-semantic-sidecar`.
- [`contracts/vectors-v1.md`](contracts/vectors-v1.md) - frozen `<workspace>/.miller/vectors.db` artifact contract: five-field generation identity, invalidation matrix, dual cursors, vec0 storage schema, shadow/rollback lifecycle, status vocabulary.
- [`../eval/sidecar-conformance/`](../eval/sidecar-conformance/) - committed sidecar conformance fixtures: 39-text corpus plus CPU-generated golden vectors for both pinned models, regenerated and gated by `generate.py --verify`.
- [`contracts/workspace-status-v1.md`](contracts/workspace-status-v1.md) - active workspace status JSON contract.
- [`contracts/workspace-health-v1.md`](contracts/workspace-health-v1.md) - active workspace health JSON contract.
- [`contracts/workspace-onboarding-v1.md`](contracts/workspace-onboarding-v1.md) - active telemetry-derived workspace onboarding JSON contract.
- [`contracts/workspace-leader-json-v1.md`](contracts/workspace-leader-json-v1.md) - active workspace leader diagnostics and graceful handoff JSON contract.
- [`contracts/refresh-wait-v1.md`](contracts/refresh-wait-v1.md) - active refresh/wait JSON contract for Eros convergence.
- [`contracts/impact-index-revision-delta-v1.md`](contracts/impact-index-revision-delta-v1.md) - active index-revision changed-path delta JSON contract and unchanged `delta_status` semantics.
- [`contracts/impact-traversal-evidence-v1.md`](contracts/impact-traversal-evidence-v1.md) - active bounded traversal evidence JSON contract, including independent capability negotiation and scoped exhaustion limits.
- [`contracts/impact-test-role-evidence-v1.md`](contracts/impact-test-role-evidence-v1.md) - active positive test-role evidence contract for normal and index-revision impact JSON, including candidate-only/absence-unknown scope.
- [`contracts/trace-json-v1.md`](contracts/trace-json-v1.md) - active trace JSON contract for auto/path/refs/bridge output and additive `next_actions`.
- [`contracts/content-corpus-v1.md`](contracts/content-corpus-v1.md) - active content corpus schema/export contract.
- [`contracts/references-export-v1.md`](contracts/references-export-v1.md) - active references/usage JSONL export contract.
- [`contracts/references-candidates-v1.md`](contracts/references-candidates-v1.md) - experimental, evidence-gated dead-code candidate listing CLI contract (`references candidates`).
- [`contracts/patterns-json-v1.md`](contracts/patterns-json-v1.md) - active patterns JSON contract over extractor structural facts.
- [`contracts/metrics-json-v1.md`](contracts/metrics-json-v1.md) - active metrics JSON contract for churn, clone groups, and complexity ranking.
- [`contracts/metrics-history-v1.md`](contracts/metrics-history-v1.md) - active metric-history JSON contract for `miller metrics history` trend reads over the append-only `history.db` sidecar.
- [`contracts/rules-v1.md`](contracts/rules-v1.md) - active `miller rules` output contract for the instruction tier, plus the per-harness rules-file formats with the official doc URL each was verified against.
- [`plans/2026-07-19-miller-semantic-integration-design.md`](plans/2026-07-19-miller-semantic-integration-design.md) - authoritative program design for Miller's optional local semantic layer (sidecar binary, vector artifact, hybrid retrieval, phases P0-P6).
- [`plans/2026-07-19-p0-governance-and-gates-plan.md`](plans/2026-07-19-p0-governance-and-gates-plan.md) - approved phase-0 plan for the semantic program: boundary docs, telemetry stamping, gates, eval harness, and model benchmark.
- [`findings/2026-07-19-sqlite-vec-aot-spike.md`](findings/2026-07-19-sqlite-vec-aot-spike.md) - P0 hard-gate evidence: sqlite-vec v0.1.9 under Native AOT per release RID (osx-arm64 PASS; other RIDs via the isolated CI matrix job).
- [`plans/2026-07-07-metric-history-design.md`](plans/2026-07-07-metric-history-design.md) - design record for the P4 metric-history/trends slice (`history.db` sidecar, hybrid converge/heavy-arm snapshots, dashboard sparklines).
- [`plans/2026-07-07-metric-history-implementation-plan.md`](plans/2026-07-07-metric-history-implementation-plan.md) - implementation plan for the P4 metric-history/trends slice.
- [`plans/2026-06-26-miller-five-gap-implementation-plan.md`](plans/2026-06-26-miller-five-gap-implementation-plan.md) - historical implementation plan for metrics, empty-state recovery, leader handoff, dashboard panels, and clone/complexity discovery; the metrics MCP-tool portions were superseded by the CLI-only metrics contract.
- [`findings/2026-06-26-five-gap-implementation.md`](findings/2026-06-26-five-gap-implementation.md) - historical implementation evidence and boundary notes for the five-gap plan; use `contracts/metrics-json-v1.md` for the current CLI-only metrics surface.
- [`findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md`](findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md) - active Miller/Julie foundation matrix finding and adaptation-candidate ranking.
- [`findings/2026-07-06-site-token-savings-refresh.md`](findings/2026-07-06-site-token-savings-refresh.md) - current public-site token-savings measurement and reproduction command.
- [`plans/2026-06-28-overview-first-agent-workflow-guidance-plan.md`](plans/2026-06-28-overview-first-agent-workflow-guidance-plan.md) - approved implementation plan for overview-first inspect guidance, onboarding hints, public docs, and focused matrix evidence.
- [`plans/2026-06-28-trace-graph-workflow-recovery-guidance-plan.md`](plans/2026-06-28-trace-graph-workflow-recovery-guidance-plan.md) - approved implementation plan for trace no-path, bridge fallback, ambiguity, docs, and matrix evidence guidance.
- [`plans/2026-06-28-trace-content-patterns-quality-goal-plan.md`](plans/2026-06-28-trace-content-patterns-quality-goal-plan.md) - implemented goal plan for improving `trace`, `content`, and `patterns` usefulness, recovery output, guidance, and focused evidence.
- [`plans/2026-06-28-handoff-skills-implementation-plan.md`](plans/2026-06-28-handoff-skills-implementation-plan.md) - approved implementation plan for Miller-provided `handoff-out` and `handoff-in` skills.
- [`findings/2026-06-28-trace-content-patterns-quality-baseline.md`](findings/2026-06-28-trace-content-patterns-quality-baseline.md) - focused baseline, RED/GREEN replay matrix, and adoption notes for the trace/content/patterns quality slice.
- [`findings/2026-06-28-handoff-skills-dogfood.md`](findings/2026-06-28-handoff-skills-dogfood.md) - dogfood evidence for local handoff packet creation, intake validation, and tracked/untracked impact behavior.
- [`plans/2026-06-09-patterns-tool-design.md`](plans/2026-06-09-patterns-tool-design.md) - design record for the `patterns` tool over extractor structural facts.
- [`plans/2026-06-09-patterns-tool-implementation-plan.md`](plans/2026-06-09-patterns-tool-implementation-plan.md) - implementation plan for the `patterns` MCP/CLI surface.
- [`release-process.md`](release-process.md) - current release validation and promotion flow.
- [`release-notes/v1.13.0.md`](release-notes/v1.13.0.md) - latest release notes.
- [`release-notes/v1.12.0.md`](release-notes/v1.12.0.md) - historical `v1.12.0` release notes.
- [`release-notes/v1.11.1.md`](release-notes/v1.11.1.md) - historical `v1.11.1` release notes.
- [`release-notes/v1.11.0.md`](release-notes/v1.11.0.md) - historical `v1.11.0` release notes.
- [`release-notes/v1.10.0.md`](release-notes/v1.10.0.md) - historical `v1.10.0` release notes.
- [`release-notes/v1.9.0.md`](release-notes/v1.9.0.md) - historical `v1.9.0` release notes.
- [`release-notes/v1.8.1.md`](release-notes/v1.8.1.md) - historical `v1.8.1` release notes.
- [`release-notes/v1.8.0.md`](release-notes/v1.8.0.md) - historical `v1.8.0` release notes.
- [`release-notes/v1.7.0.md`](release-notes/v1.7.0.md) - historical `v1.7.0` release notes.
- [`release-notes/v1.6.0.md`](release-notes/v1.6.0.md) - historical `v1.6.0` release notes.
- [`release-notes/v1.5.1.md`](release-notes/v1.5.1.md) - historical `v1.5.1` release notes.
- [`findings/2026-07-18-v1.13.0-release-verification.md`](findings/2026-07-18-v1.13.0-release-verification.md) - live `v1.13.0` release verification.
- [`findings/2026-07-17-v1.12.0-release-verification.md`](findings/2026-07-17-v1.12.0-release-verification.md) - live `v1.12.0` release verification.
- [`findings/2026-07-17-v1.11.1-release-verification.md`](findings/2026-07-17-v1.11.1-release-verification.md) - live `v1.11.1` release verification.
- [`findings/2026-07-16-v1.11.0-release-verification.md`](findings/2026-07-16-v1.11.0-release-verification.md) - live `v1.11.0` release verification.
- [`findings/2026-07-14-v1.9.0-release-verification.md`](findings/2026-07-14-v1.9.0-release-verification.md) - live `v1.9.0` release verification.
- [`findings/2026-07-12-v1.8.1-release-verification.md`](findings/2026-07-12-v1.8.1-release-verification.md) - live `v1.8.1` release verification.
- [`findings/2026-07-12-v1.8.0-release-verification.md`](findings/2026-07-12-v1.8.0-release-verification.md) - live `v1.8.0` release verification.
- [`findings/2026-07-10-v1.7.0-release-verification.md`](findings/2026-07-10-v1.7.0-release-verification.md) - live `v1.7.0` release verification.
- [`findings/2026-07-09-v1.6.0-release-verification.md`](findings/2026-07-09-v1.6.0-release-verification.md) - live `v1.6.0` release verification.
- [`findings/2026-07-08-v1.5.1-release-verification.md`](findings/2026-07-08-v1.5.1-release-verification.md) - live `v1.5.1` release verification.
- [`release-notes/v1.5.0.md`](release-notes/v1.5.0.md) - historical `v1.5.0` release notes.
- [`findings/2026-07-08-v1.5.0-release-verification.md`](findings/2026-07-08-v1.5.0-release-verification.md) - live `v1.5.0` release verification.
- [`findings/2026-07-08-v1.5.0-dogfood.md`](findings/2026-07-08-v1.5.0-dogfood.md) - v1.5.0 dead-code/metric-history dogfood plus the full-rebuild timing baseline (8.4 s).
- [`findings/2026-07-06-v1.4.5-release-verification.md`](findings/2026-07-06-v1.4.5-release-verification.md) - live `v1.4.5` release verification.
- [`release-notes/v1.3.2.md`](release-notes/v1.3.2.md) - historical `v1.3.2` release notes.
- [`findings/2026-07-02-v1.3.2-release-verification.md`](findings/2026-07-02-v1.3.2-release-verification.md) - live `v1.3.2` release verification.
- [`release-notes/v1.3.1.md`](release-notes/v1.3.1.md) - historical `v1.3.1` release notes.
- [`findings/2026-07-01-v1.3.1-release-verification.md`](findings/2026-07-01-v1.3.1-release-verification.md) - live `v1.3.1` release verification.
- [`release-notes/v1.3.0.md`](release-notes/v1.3.0.md) - historical `v1.3.0` release notes.
- [`findings/2026-07-01-v1.3.0-release-verification.md`](findings/2026-07-01-v1.3.0-release-verification.md) - live `v1.3.0` release verification.
- [`findings/2026-06-29-v1.2.0-release-verification.md`](findings/2026-06-29-v1.2.0-release-verification.md) - live `v1.2.0` release verification.
- [`release-notes/v1.2.0.md`](release-notes/v1.2.0.md) - historical `v1.2.0` release notes.
- [`findings/2026-06-27-v1.1.2-release-verification.md`](findings/2026-06-27-v1.1.2-release-verification.md) - live `v1.1.2` release verification.
- [`release-notes/v1.1.2.md`](release-notes/v1.1.2.md) - historical `v1.1.2` release notes.
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

## Current release and delivery evidence

- [`findings/2026-07-16-hook-delivery-verification.md`](findings/2026-07-16-hook-delivery-verification.md) - v1.10.0 release verification and live SessionStart/SubagentStart hook smoke on Claude Code, opt-out proof, the Codex inert-hooks limitation, and the T7.2 clock start.

## Current Blazor and bridge evidence

- [`findings/2026-07-14-blazor-namespace-resolution.md`](findings/2026-07-14-blazor-namespace-resolution.md) - current supported and fail-closed Blazor component namespace behavior, with fast and live-test evidence.
- [`plans/2026-07-13-blazor-import-namespace-resolution.md`](plans/2026-07-13-blazor-import-namespace-resolution.md) - implemented follow-up plan for inherited `_Imports.razor` and bounded project/folder namespace resolution.
- [`plans/2026-07-11-blazor-bridge-support-implementation-plan.md`](plans/2026-07-11-blazor-bridge-support-implementation-plan.md) - historical implementation plan for the initial Blazor navigation, component-reference, and dependency-injection graph support; it predates the namespace follow-up above.

## Historical evidence

The `m*-design.md`, `miller-mvp-plan.md`, most dated `docs/plans/`, and dated `docs/findings/` files are
historical design records, implementation plans, and dogfood evidence. Keep them when they explain why a decision
was made, but do not use them as current behavior unless a current doc above links to them for that purpose.

## Cleanup rule

Remove duplicate drafts and abandoned plans when they are unreferenced and actively misleading. Otherwise, prefer a
short historical-status banner over rewriting evidence docs to match today's implementation.
