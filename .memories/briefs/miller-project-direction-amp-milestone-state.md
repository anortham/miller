---
id: miller-project-direction-amp-milestone-state
title: Miller — product direction & milestone state
status: active
created: 2026-05-30T16:32:42.067Z
updated: 2026-06-05T02:19:27.498Z
tags:
  - miller
  - project-direction
  - status
  - dogfood
  - read-path
---

## What Miller is (the spine - all docs agree)

- Read-only .NET 10 SQLite/MCP consumer of `julie-extract`. **The open-source free code-intelligence core.**
- **Eros = commercial extension over the same data**, not a rewrite/duplicate. Eros should consume public contracts: `julie-extractors` artifacts, Miller CLI/MCP/process surfaces, and any explicitly documented Miller shareable artifacts. It must not depend on Miller private .NET types or internal indexes.
- Dividing line: Miller answers "where is the code and what does it structurally connect to?"; Eros answers "what should the agent do next, how confident, what higher-level evidence?"
- Stay daemon-light; embeddings/semantic/vector scale stay OUT of the free core. If Eros needs LanceDB-scale semantic retrieval, keep it behind an Eros projection adapter instead of moving that stack into Miller.

Architecture contracts (unchanged): index DBs local at `<workspace>/.miller/symbols.db`; central discovery `~/.miller/workspaces.db`; `workspace_id` = SHA-256 of canonical root; freshness via `files.content_hash` (`blake3:`); read tools accept selectors. Pin: `julie-extract` v2.1.1, SQLite schema 2, extract contract 2, report schema 2.

## Current state - 2026-06-05

M0-M8 done. Recently shipped on `main`: search projection split (OpenClaw first search ~1.8s/+55MB); collapsed-trigram **FTS5 sidecar default-ON** (`search.db`); CLI `status|list|refresh|full|open|remove` + single-sourced build version; large-workspace dogfood fixes.

## Source regions / pillar-3 scope-aware lexical search - implemented and dogfooded

`julie-extractors` 2.1.1 landed and Miller pins/restores `julie-extract` v2.1.1. Miller consumer path is implemented and committed in `e9b5a35`: `SearchIndexWriter` schema v3 always creates `regions_fts` + `search_regions`, and populates them when `MILLER_REGION_INDEX=1`; explicit `search regions=comment|doc_comment|string_literal` / CLI `--regions` routes to fail-closed disk region search; symbol search adds best-effort `has_doc` from `symbols.doc_comment`.

Dogfood evidence in `docs/findings/2026-06-05-source-region-dogfood.md`: `julie-extractors` built 80,446 indexed regions in a 46M `search.db` with ~0.5s scoped queries; OpenClaw built 877,694 indexed regions in a 664M `search.db` with ~3.5s scoped queries. Decision: keep region indexing opt-in for beta. Default-on waits for follow-up on multi-token region query semantics and very large `string_literal` sidecars. `embedded`, region trigram recall, and exclusion queries remain deferred.

Docs: `docs/plans/2026-06-04-source-regions-pillar3-design.md`, `docs/plans/2026-06-05-source-regions-pillar3-implementation-plan.md`, `docs/findings/2026-06-05-source-region-dogfood.md`.

## Beta readiness routing

Use `docs/plans/2026-06-05-beta-readiness-checklist.md` as the beta-candidate routing doc. Beta means Miller is usable as the free local code-intelligence core through MCP and CLI on real repos; it does not require Eros, embeddings, semantic/vector search, or full release-blocking Native AOT.

## Next track - search quality and beta docs

Immediate priority: work down the beta checklist after source-region closeout. Next likely items: dogfood symbol search and `mode=content`, decide whether the symbol projection must widen before beta, then document CLI/README behavior. Miller's functional success as the free core determines which Eros paths should call Miller, which should read shared artifacts, and which truly need Eros-owned projections such as LanceDB-backed semantic retrieval.

## Guardrails

- Daemon-light: no Julie-style filewatcher/resource sink.
- Do NOT reintroduce full-index loads into `workspace status` / dashboard list/read paths.
- Symbol search correctness must survive a missing/stale/corrupt sidecar (self-heal to in-memory BM25). **Ranking stays in Miller's C#** (`Miller.Core.Search.Bm25`); FTS5 is recall-only.
- Region search is explicit and fail-closed; it requires `MILLER_REGION_INDEX=1` and a refreshed `search.db`.
- Region indexing remains opt-in for beta.
- `Miller.Core` stays pure logic, ZERO I/O deps.
- Build 0 warnings / 0 errors (`dotnet build Miller.slnx -c Release`).
- Test split: `scripts/test.sh` (fast, every change) vs `scripts/test.sh scale` (indexing/extract path). julie-spawning test MUST be `[Trait("Category","Scale")]` via `ScaleTestSupport.RequireJulieServer()`.
- `AGENTS.md` generated from `CLAUDE.md` - edit `CLAUDE.md` then `scripts/sync-agents.sh`.
- SUPERSEDED (do not reinstate): "keep FTS5 parked" - FTS5 sidecar shipped default-on 2026-06-04 (shareability driver). Remaining caution: don't move BM25 *ranking* off in-memory C#; no embeddings in the free core.
