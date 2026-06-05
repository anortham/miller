---
id: miller-project-direction-amp-milestone-state
title: Miller — product direction & milestone state
status: active
created: 2026-05-30T16:32:42.067Z
updated: 2026-06-05T22:03:54.385Z
tags:
  - miller
  - project-direction
  - status
  - dogfood
  - search-quality
  - read-path
---

## What Miller is (the spine - all docs agree)

- Read-only .NET 10 SQLite/MCP consumer of `julie-extract`. **The open-source free code-intelligence core.**
- **Eros = commercial extension over the same data**, not a rewrite/duplicate. Eros should consume public contracts: `julie-extractors` artifacts, Miller CLI/MCP/process surfaces, and any explicitly documented Miller shareable artifacts. It must not depend on Miller private .NET types or internal indexes.
- Dividing line: Miller answers "where is the code and what does it structurally connect to?"; Eros answers "what should the agent do next, how confident, what higher-level evidence?"
- Stay daemon-light; embeddings/semantic/vector scale stay OUT of the free core. If Eros needs LanceDB-scale semantic retrieval, keep it behind an Eros projection adapter instead of moving that stack into Miller.

Architecture contracts: index DBs local at `<workspace>/.miller/symbols.db`; central discovery `~/.miller/workspaces.db`; `workspace_id` = SHA-256 of canonical root; freshness via `files.content_hash` (`blake3:`); read tools accept selectors. Current pin: `julie-extract` v2.1.3, SQLite schema 2, extract contract 2, report schema 2.

## Current state - 2026-06-05

M0-M8 are complete. Recently shipped on `main`: search projection split; collapsed-trigram **FTS5 sidecar default-ON** (`search.db`); CLI `status|list|refresh|full|open|remove` + single-sourced build version; source-region consumer path; `dotnet-web` bridge provider; cross-workspace selector fixes; local Miller skill package; `julie-extract` v2.1.3 file-policy parity; Julie-vs-Miller search-quality first pass; scoped `file_pattern` / `language` search filters; bridge trace disambiguation; lazy disk-backed CLI symbol search; and conservative content-search tuning for agent workflows.

The source-checkout beta checklist is effectively closed. Evidence recorded in `docs/plans/2026-06-05-beta-readiness-checklist.md`: local macOS restore/build/fast/scale/diff gates passed after the 2.1.3 pin, Windows `windows-fast` passed on the exact candidate commit, and branch-tip CI passed after evidence docs were committed.

## Search and read-path state

Symbol search is in the solved beta bucket. Miller keeps symbol ranking narrow (`name + signature`), with FTS5 as recall-only and C# BM25 ranking retained for parity. Exact-name import/module noise has been demoted, compact output is grouped for token efficiency, scoped search filters are available across symbol/content/region result kinds, and CLI one-shot symbol search no longer pays full graph or eager sidecar snapshot costs.

`mode=content` dogfood is now closed for beta. The projection stays docs-like and in-memory; no symbol-scope widening, result-kind merge, or persisted content index is justified yet. Tuning now requires meaningful query-term coverage on the selected snippet line, boosts/prefers token-phrase lines, returns `No results` for weak term-overlap misses, and requires exact token phrases for env-var/path/code-like content queries. Source comments/literals/env vars remain `regions=` scope. Evidence: `docs/findings/2026-06-05-search-content-dogfood.md`.

Large-repo evidence in `docs/findings/2026-06-05-large-repo-readpath-dogfood.md`: on OpenClaw (640k symbols, 664MB `search.db`), Release CLI `miller search "workspace status"` is ~0.26-0.27s, scoped symbol search ~0.26s, file mode ~0.31s, and summary inspect ~0.31s. Full inspect/context remain graph-heavy at ~6.7s and are post-beta unless real agent workflows need faster one-shot graph reads.

## Source regions / pillar-3 scope-aware lexical search

Miller consumes `julie-extractors` source regions. `SearchIndexWriter` schema v4 creates `regions_fts` + `search_regions`, populates them when `MILLER_REGION_INDEX=1`, and explicit `search regions=comment|doc_comment|string_literal` / CLI `--regions` routes to fail-closed disk region search.

Dogfood evidence in `docs/findings/2026-06-05-source-region-dogfood.md`: `julie-extractors` built 80,446 indexed regions in a 46M `search.db` with ~0.5s scoped queries; OpenClaw built 877,694 indexed regions in a 664M `search.db` with ~3.5s scoped queries. Decision: keep region indexing opt-in for beta. Default-on waits for follow-up on multi-token region query semantics and very large `string_literal` sidecars. `embedded`, region trigram recall, and exclusion queries remain deferred.

Current active follow-up: source-region default-on remeasurement from TODO #7, only if beta scope still needs it; otherwise the beta release docs/checklist are the routing surface.

## Beta readiness routing

Use `docs/plans/2026-06-05-beta-readiness-checklist.md` as the beta-candidate routing doc. Beta means Miller is usable as the free local code-intelligence core through MCP and CLI on real repos; it does not require Eros, embeddings, semantic/vector search, or full release-blocking Native AOT.

## Guardrails

- Daemon-light: no Julie-style filewatcher/resource sink.
- Do NOT reintroduce full-index loads into `workspace status`, dashboard list/read paths, symbol search, content search, or summary inspect.
- Symbol search correctness must survive a missing/stale/corrupt sidecar (self-heal to in-memory BM25). **Ranking stays in Miller's C#** (`Miller.Core.Search.Bm25`); FTS5 is recall-only.
- Content search stays docs-like; source comments/literals route through explicit `regions=`.
- Region search is explicit and fail-closed; it requires `MILLER_REGION_INDEX=1` and a refreshed `search.db`.
- Region indexing remains opt-in for beta.
- `Miller.Core` stays pure logic, ZERO I/O deps.
- Build 0 warnings / 0 errors (`dotnet build Miller.slnx -c Release`).
- Test split: `scripts/test.sh` (fast, every change) vs `scripts/test.sh scale` (indexing/extract path). julie-spawning test MUST be `[Trait("Category","Scale")]` via `ScaleTestSupport.RequireJulieServer()`.
- `AGENTS.md` generated from `CLAUDE.md` - edit `CLAUDE.md` then `scripts/sync-agents.sh`.
- SUPERSEDED (do not reinstate): "keep FTS5 parked" - FTS5 sidecar shipped default-on 2026-06-04 (shareability driver). Remaining caution: don't move BM25 *ranking* off in-memory C#; no embeddings in the free core.
