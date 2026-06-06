---
id: miller-project-direction-amp-milestone-state
title: Miller — product direction & beta candidate state
status: active
created: 2026-05-30T16:32:42.067Z
updated: 2026-06-06T13:40:36.460Z
tags:
  - miller
  - project-direction
  - status
  - beta
  - read-path
  - search-quality
---

## What Miller Is

- Miller is the read-only .NET 10 SQLite/MCP consumer of `julie-extract` output: the open-source/free local code-intelligence core.
- Eros remains the commercial extension above Miller. Eros should consume public contracts and process/tool surfaces, not Miller private .NET internals.
- Miller answers structural/local questions: where code is, what symbols exist, and how they connect. Eros owns higher-level guidance, confidence, and semantic/vector workflows.
- Keep the free core deterministic, lexical/structural, daemon-light, and embedding-free.

Architecture contracts: workspace DBs live at `<workspace>/.miller/symbols.db`; central discovery is `~/.miller/workspaces.db`; `workspace_id` is SHA-256 of canonical root; file freshness uses `files.content_hash` with `blake3:`; read tools accept workspace selectors. Current pin: `julie-extract` v2.1.3, SQLite schema 2, extract contract 2, report schema 2.

## Current State - 2026-06-06

M0-M8 are complete. The source-checkout beta candidate is now branch-tip `main` at `91288557137a1711e148628b374863526ae4b3ab` (`0.1.0+91288557137a`). It has been pushed to `origin/main`.

Verification on the pushed commit:

- Local macOS: `dotnet build Miller.slnx -c Release` passed with 0 warnings/errors; `scripts/test.sh` passed 1,631 fast tests; `scripts/test.sh scale` passed 25 scale tests; `git diff --check` was clean.
- GitHub Actions run `27063713900` passed on commit `91288557137a1711e148628b374863526ae4b3ab`: main build/test passed, and `windows-fast` restored `julie-extract`, built, and passed `scripts/test.ps1`.

## Search And Read-Path State

Symbol search is in the solved beta bucket. Miller keeps symbol ranking narrow (`name + signature`), with FTS5 as recall-only and C# BM25 ranking retained for parity. Prose/docs use `mode=content`; comments/literals/env vars use explicit `regions=`. Do not widen the beta symbol projection without fresh dogfood evidence.

The prior large-repo graph-heavy read-path gap is closed for the beta CLI path. CLI `search`, `inspect`, graph-only `context`, `impact`, and non-bridge `trace` now avoid full graph/bridge hydration by using lazy disk symbol lookup plus on-demand SQLite graph reachability where appropriate.

OpenClaw evidence from the 2026-06-05/06 reruns:

- ambiguous full inspect: `8.75s` / ~1.45GB max RSS -> `0.60s` / ~69MB
- scoped unique full inspect: `0.38s` / ~68MB
- broad graph-only context: `0.77s` / ~119MB
- impact: `0.31s` / ~69MB
- trace auto: `0.32s` / ~69MB

Bridge trace still intentionally uses the full bridge graph. Do not add incremental in-memory patching unless new evidence says the current projection-specific paths are insufficient.

## Source Regions

Miller consumes `julie-extractors` source regions. `SearchIndexWriter` schema v4 creates `regions_fts` and `search_regions`, populated when `MILLER_REGION_INDEX=1`; explicit `search regions=comment|doc_comment|string_literal` / CLI `--regions` routes to fail-closed disk region search.

Decision for beta: keep region indexing opt-in. The source-region cap remeasurement is closed. `MILLER_REGION_MAX_BYTES` is a useful safety guardrail, but not a default-on size solution for string-heavy repos because the cost is many small `string_literal` regions. If default-on region search is reopened, design kind-level indexing controls first, likely comment/doc-comment by default with `string_literal` still explicit.

## Beta Routing

Use `docs/plans/2026-06-05-beta-readiness-checklist.md` and `TODO.md` as routing surfaces. Current practical state: the source-checkout beta is technically ready from the Miller side after the pushed CI run. Remaining decisions are product/release decisions, not known implementation blockers.

Next recommended work:

1. Decide whether to tag/publish a prerelease beta. Do not tag, publish, overwrite, or release without explicit approval.
2. Before public beta positioning, do the separate Graphify comparison the user deferred, so we are clear about Miller's differentiated reason to exist.
3. Post-beta: measure whether symbol projection widening (`doc_comment`, identifiers, bounded context/literals/path tokens) helps real workflows; keep it out of the current beta unless fresh dogfood changes the decision.

## Guardrails

- Daemon-light: no Julie-style filewatcher/resource sink.
- Do not reintroduce full-index loads into `workspace status`, dashboard list/read paths, symbol search, content search, summary inspect, or graph-only CLI reads.
- Ranking stays in Miller's C# (`Miller.Core.Search.Bm25`); FTS5 is recall-only.
- Content search stays docs-like; comments/literals route through explicit `regions=`.
- Region indexing remains opt-in and fail-closed for beta.
- `Miller.Core` stays pure logic with zero I/O dependencies.
- Build must stay 0 warnings / 0 errors.
- Test split is load-bearing: `scripts/test.sh` fast, `scripts/test.sh scale` opt-in; julie-spawning tests must be `[Trait("Category","Scale")]` through `ScaleTestSupport.RequireJulieServer()`.
- `AGENTS.md` is generated from `CLAUDE.md`; edit `CLAUDE.md` then run `scripts/sync-agents.sh`.
