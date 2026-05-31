---
id: miller-project-direction-amp-milestone-state
title: Miller — project direction &amp; milestone state
status: active
created: 2026-05-30T16:32:42.067Z
updated: 2026-05-30T16:32:42.067Z
tags: []
---

## What Miller is

A read-only **.NET 10** SQLite/MCP server that consumes `julie-server extract` output to answer structural code-intelligence questions (search, inspect, context, trace, impact, edit, workspace) for AI coding agents — token-thrifty, no embeddings, no source parsing of its own. Lineage: **Julie** (Rust, shipping, the extractor) → **Miller** (.NET, building, this repo) → **Eros** (commercial, future). Miller is a **product for unknown users**, not a personal tool — hold tests + error handling to product bar.

Repo: `github.com/anortham/miller`, branch `main`. julie pins: schema 26, contract 1, julie-server v7.12.2 (binary fetched into `.tools/`, gitignored).

## The differentiator (why Miller exists)

A **deterministic cross-language structural resolver** — links code across language boundaries (C# entity ↔ EF table, TS call ↔ the C# route that serves it) **without embeddings**. This is M4 and it's the whole point; everything before it is table stakes.

## Milestone state (as of 2026-05-30)

- **M0-M7: DONE & committed.** Scaffold, read core, MCP host + search/inspect/telemetry, freshness (single-writer indexer + watcher), context + impact, edit, workspace.
- **M8 logging polish: DONE & pushed** (676dd6f) — per-pid files, role enricher, reaper, correlation id.
- **Test-suite defenses: DONE & pushed** (ef15ecf) — fast/scale split is now enforced; see "Guardrails" below.
- **M4 trace + cross-language resolver: BLOCKED on julie.** Needs two julie-side enrichment plans first: (1) bridge anchors, (2) `test_role`. Re-pin to schema/contract once those land. This is the next real work — the hope is julie is ready soon.
- **M9 ad-hoc big-file/log-viewer: PARKED** (spec only, 173d3e3). Build deferred until the make-or-break risk is measured: julie's full-content-into-SQLite ingest cost on a multi-GB log. Real competitor is grep/rg/jq, not just tail. The one durable win is structurally-bounded output. Likely fork if built: structured→julie ingest, logs/text→direct streaming line-reader. Don't build without running the gating experiments in docs/m9-design.md.

## Guardrails that are now load-bearing (don't erode)

- **Two test suites.** Default (`Category!=Scale`) must stay <10s and pure. Scale (`Category=Scale`) spawns real julie-server / builds big fixtures and is opt-in. A bare `dotnet test` runs ONLY fast (csproj `VSTestTestCaseFilter`).
- **Any julie-spawning test MUST** be `[Trait("Category","Scale")]` at class level AND obtain the binary via `ScaleTestSupport.RequireJulieServer()`. The `ScaleTraitConventionTests` guard fails the build otherwise — add the trait, don't weaken the guard.
- **Build is 0 warnings / 0 errors** (`TreatWarningsAsErrors`); analyzer warnings (CA1416, xUnit1051) are build errors.
- **Never trust a workflow/test self-report.** Independently rebuild + run both suites via TRX counters before claiming green.
- `Miller.Core` stays pure (zero I/O deps).

## Cross-language principle (project-wide)

Features ship for **every capable language**, not just C#/TS. Consume julie's all-language signals (e.g. `is_test` in `symbols.metadata`, computed across 34+ langs by julie's `test_detection.rs`); never hardcode a "languages I care about" list. This directly governs how M4's resolver must be built.

## Working norms

- Commit/push only when asked (just did, at user request; pushes to anortham/miller are pre-authorized).
- Redirect noisy bash to /tmp logfiles and Read back; never parallel-batch shell calls where one grep/find exit-1 cascades-cancels the batch.
- ADHD-friendly replies: lead with the answer, concise bullets, plain words.
