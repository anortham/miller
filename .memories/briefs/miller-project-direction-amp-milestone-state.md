---
id: miller-project-direction-amp-milestone-state
title: Miller — product direction & post-v0.2.0 work queue
status: active
created: 2026-05-30T16:32:42.067Z
updated: 2026-06-08T01:08:05.464Z
tags:
  - miller
  - project-direction
  - status
  - v0.2.0
  - eros-integration
  - search-quality
  - docs
  - content-corpus
---

## What Miller Is

- Miller is the read-only .NET 10 SQLite/MCP consumer of `julie-extract` output: the open-source/free local code-intelligence core.
- Eros remains the commercial extension above Miller. Eros should consume public contracts, process surfaces, and exported artifacts, not Miller private .NET internals.
- Miller answers structural/local questions: where code is, what symbols exist, what content exists, and how code connects. Eros owns higher-level guidance, confidence, semantic/vector workflows, and commercial dashboard/history views.
- Keep the free core deterministic, lexical/structural, daemon-light, and embedding-free.

Architecture contracts: workspace DBs live at `<workspace>/.miller/symbols.db`; central discovery is `~/.miller/workspaces.db`; `workspace_id` is SHA-256 of the canonical root; file freshness uses `files.content_hash` with `blake3:`; read tools accept workspace selectors. Current pin: `julie-extract` v2.1.3. Current Miller release version: `0.2.0`.

## Product Positioning

Miller is a living, up-to-date agent assistant, not just a one-time graph dump.

Frame Miller around freshness and workflow integration:

- current workspace state, freshness, and refresh lifecycle
- MCP + CLI surfaces designed for agents in the loop
- live registry, telemetry, dashboard, workspace selection, and content corpus search
- stale/corrupt symbol sidecars fail visibly; use `MILLER_SEARCH_SIDECAR=0` only as an explicit debugging fallback to in-memory BM25
- structural answers that can be re-run against the current checkout during active work

Public positioning should stay focused on the Miller/Eros vision: Miller as the free local code-intelligence core, Eros as the higher-level guidance and workflow layer.

## Current State - 2026-06-07

Miller `v0.2.0` has been published. Verified GitHub release facts: tag `v0.2.0`, non-draft, non-prerelease, published `2026-06-07T23:43:09Z`, target commit `8bceb137b880eaafb723ff69e886a893f5d799f8`, URL `https://github.com/anortham/miller/releases/tag/v0.2.0`. A fresh release check on 2026-06-08 confirmed the live release is still non-draft/non-prerelease and has four platform archives plus four `.sha256` sidecars.

The current `main` tip before the docs-audit work was `a1f7f99 Streamline release artifact promotion`, followed by local post-release Eros CLI contract/onboarding work. Future releases should use `docs/release-process.md` and the `promote_run_id` artifact-promotion workflow path. Do not tag, publish, overwrite, or release without explicit user approval.

`TODO.md` is the active routing surface for post-`v0.2.0` work. Preserve the user's local `TODO.md` changes when doing implementation or documentation work.

Local post-release contract work added Eros-facing CLI coverage for `capabilities --json`, `refresh --json --wait`, `telemetry export --jsonl`, and documented existing `workspace status --json`, `content export`, and `impact --json` behavior in `docs/contracts/cli-eros-v1.md`. Treat remaining Eros integration work as demand-driven contract hardening: add or extend public Miller surfaces only when a concrete Eros workflow needs facts the documented contracts do not cover.

The README/onboarding and documentation cleanup is now locally in progress: README is sidecar/content-corpus aware, manual release archive instructions use the versioned extracted directory, the public site starts with plugin install, `docs/README.md` maps current docs vs historical evidence, and stale milestone/dogfood docs now carry historical-status banners. `CLAUDE.md` was updated with the docs map and current dashboard/read-path guidance, then `AGENTS.md` was regenerated from it.

## Near-Term Routing

Use `TODO.md` for the active queue. Durable priorities are:

1. Site metrics: add real token-savings metrics to the GitHub Pages site from open-source repos cloned under `~/source`.
2. Search-quality dogfood after content corpus FTS: keep symbol search narrow by default, exercise explicit `mode=content|source|external|web|all-text` plus `regions=...` on real workflows, and only widen symbol ranking if those explicit routes fail concrete agent tasks.
3. Structured search-output cleanup: replace or mark stale copied Julie cases such as `WorkspacePool`, and decide whether `mode=file --json` needs a versioned file-result shape. Scoped-miss UX is closed; content corpus modes already return path/line/snippet hits.
4. Julie backlog transfer: triage `~/source/julie/TODO.md` ideas for Miller fit, especially self-improvement/searchability scoring, tree-sitter pattern queries, AST complexity metrics, and body-hash duplication detection.
5. Cross-workspace access UX: clarify how an agent in `workspace_A` should examine code in `workspace_B` through Miller selectors, registry state, and MCP/CLI affordances.
6. Eros contract additions: only add new CLI/export surfaces when Eros has a concrete workflow need beyond the current documented public contracts.

## Search And Read-Path State

Symbol search is in the solved beta bucket and should remain narrow (`name + signature`) unless fresh dogfood shows explicit text modes failing real agent tasks. FTS5 remains recall-only for symbol search, with C# BM25 ranking retained for parity.

The post-beta file-content FTS work is implemented through the content corpus sidecar. Use explicit modes by intent: `mode=content` for docs/config prose, `mode=source` for workspace source-body text, `mode=external|web|all-text` for imported or all corpus text, and `regions=comment|doc_comment|string_literal` for scoped source-region text. Do not treat old pre-content-corpus search TODOs as open requirements to add doc comments, literals, or path tokens directly into symbol ranking.

The prior large-repo graph-heavy read-path gap is closed for the CLI path. CLI `search`, `inspect`, graph-only `context`, `impact`, and non-bridge `trace` avoid full graph/bridge hydration by using lazy disk symbol lookup plus on-demand SQLite graph reachability where appropriate. Bridge trace still intentionally uses the full bridge graph.

## Content Corpus And Web Research

Miller has an implemented content corpus for source/docs/config/external/web text, plus a mirrored `miller-web-research` skill. Web fetching stays outside Miller in the skill layer via `browser39`; Miller imports fetched markdown as `web` content and supports bounded search/read through `.miller/content.db`. Future work should dogfood and refine the existing skill, not restart the design.

## Source Regions

Miller consumes `julie-extractors` source regions. `SearchIndexWriter` schema v4 creates `regions_fts` and `search_regions`, populated when `MILLER_REGION_INDEX=1`; explicit `search regions=comment|doc_comment|string_literal` / CLI `--regions` routes to fail-closed disk region search.

Decision for now: keep region indexing opt-in. `MILLER_REGION_MAX_BYTES` is a useful safety guardrail, but not a default-on size solution for string-heavy repos because the cost is many small `string_literal` regions. If default-on region search is reopened, design kind-level indexing controls first, likely comment/doc-comment by default with `string_literal` still explicit.

## Guardrails

- Daemon-light: no Julie-style filewatcher/resource sink.
- Do not reintroduce full-index loads into `workspace status`, dashboard list/read paths, symbol search, content search, summary inspect, or graph-only CLI reads.
- Ranking stays in Miller's C# (`Miller.Core.Search.Bm25`); FTS5 is recall-only.
- Content search stays explicit by mode; comments/literals route through explicit `regions=`.
- Region indexing remains opt-in and fail-closed unless a fresh design changes that.
- `Miller.Core` stays pure logic with zero I/O dependencies.
- Build must stay 0 warnings / 0 errors.
- Test split is load-bearing: `scripts/test.sh` fast, `scripts/test.sh scale` opt-in; julie-spawning tests must be `[Trait("Category","Scale")]` through `ScaleTestSupport.RequireJulieServer()`.
- `AGENTS.md` is generated from `CLAUDE.md`; edit `CLAUDE.md` then run `scripts/sync-agents.sh`.
