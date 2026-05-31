---
id: miller-project-direction-amp-milestone-state
title: Miller — project direction &amp; milestone state
status: active
created: 2026-05-30T16:32:42.067Z
updated: 2026-05-31T11:25:58.276Z
tags: []
---

## What Miller is

A read-only **.NET 10** SQLite/MCP server that consumes `julie-server extract` output to answer structural code-intelligence questions (search, inspect, context, trace, impact, edit, workspace) for AI coding agents — token-thrifty, no embeddings, no source parsing of its own. Lineage: **Julie** (Rust, shipping, the extractor) → **Miller** (.NET, building, this repo) → **Eros** (commercial, future). Miller is a **product for unknown users**, not a personal tool — hold tests + error handling to product bar.

Repo: `github.com/anortham/miller`, branch `main`. julie pins: **schema 28, contract 2, julie-server v7.13.0** (binary fetched into `.tools/`, gitignored; `MillerExtractContract` is the one-line source of truth).

## The differentiator (why Miller exists)

A **deterministic cross-language structural resolver** — links code across language boundaries (C# entity ↔ EF table, TS call ↔ the C# route that serves it) **without embeddings**. This is M4 and it's the whole point; everything before it is table stakes. **NOW BUILT.**

## Milestone state (as of 2026-05-31)

- **M0-M8: DONE & pushed.** Scaffold, read core, MCP host + search/inspect/telemetry, freshness, context + impact, edit, workspace, logging polish.
- **M4 trace + cross-language resolver: DONE.** Shipped as 4 phases on branch `feature/m4-cross-language-resolver`, **PR #1 OPEN, CI green (build-test pass), MERGEABLE/CLEAN** — awaiting human merge. The two julie blockers (bridge anchors, `test_role`) landed in julie 7.13.0; re-pin done.
  - Phase A `152eb99` resolver foundation (normalizers, SymbolResolver, BridgeScorer trust contract).
  - Phase B `a5d9de1` three legs: entity↔table (EF `DbSet<T>`), DTO↔entity (`CreateMap`), route (TS↔C# endpoint).
  - Phase C `6efcb7b` `trace` tool (auto/path/bridge) + in-memory `BridgeGraph` + `SymbolGraph.ShortestPath`.
  - Phase D `0ecab17` live Scale gate + honesty probe — the shippable gate. Precision 6/6=1.00 (floor 0.75), recall 1.00/buildable leg, measured live against real julie-server; 4 hard guards on scored payload. Numbers in design-doc appendix.
  - **Honest limits:** Dapper-FROM entity↔table is DROPPED — unbuildable on julie's lean 28/2 contract (no use-site name/kind on `type_arguments`); EF `DbSet<T>` is sole entity↔table anchor; restoring needs a julie-side `type_arguments` widening. STRONG grades are single-repo (MyraNext); cross-repo generalization (Tycho/LabHandbookV2 + fetch/manual-mapping fixtures) is explicitly out of scope (Task 1, not done).
- **M9 ad-hoc big-file/log-viewer: PARKED** (spec only). Build deferred until julie's full-content-into-SQLite ingest cost on a multi-GB log is measured. Real competitor is grep/rg/jq. Don't build without running the gating experiments in the M9 design doc.

## Next real work (post-merge)

- Merge PR #1 (human gate).
- If cross-repo STRONG claims are wanted: **Task 1** — extract Tycho + LabHandbookV2 + a fetch-based and a manual-mapping fixture, measure recall per leg across repos, write into design §9. Until then every STRONG grade is MyraNext-only.
- Optional julie-side ask: widen `type_arguments` so Dapper-FROM becomes buildable (would restore the second entity↔table anchor).

## Guardrails that are now load-bearing (don't erode)

- **Two test suites.** Default (`Category!=Scale`) must stay <10s and pure (currently 1050 tests, ~3s). Scale (`Category=Scale`) spawns real julie-server / builds big fixtures and is opt-in. A bare `dotnet test` runs ONLY fast (csproj `VSTestTestCaseFilter`).
- **Any julie-spawning test MUST** be `[Trait("Category","Scale")]` at class level AND obtain the binary via `ScaleTestSupport.RequireJulieServer()`. The `ScaleTraitConventionTests` guard fails the build otherwise — add the trait, don't weaken the guard.
- **Build is 0 warnings / 0 errors** (`TreatWarningsAsErrors`); analyzer warnings (CA1416, xUnit1051) are build errors.
- **Never trust a workflow/test self-report.** Independently rebuild + run both suites via TRX counters before claiming green.
- `Miller.Core` stays pure (zero I/O deps).

## Cross-language principle (project-wide)

Features ship for **every capable language**, not just C#/TS. Consume julie's all-language signals (e.g. `is_test` in `symbols.metadata`, computed across 34+ langs by julie's `test_detection.rs`); never hardcode a "languages I care about" list. This directly governs how M4's resolver is built.

## Working norms

- Commit/push only when asked (pushes to anortham/miller are pre-authorized; M4 pushed at user request as PR #1).
- Redirect noisy bash to /tmp logfiles and Read back; never parallel-batch shell calls where one grep/find exit-1 cascades-cancels the batch.
- ADHD-friendly replies: lead with the answer, concise bullets, plain words.
