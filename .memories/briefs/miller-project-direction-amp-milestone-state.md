---
id: miller-project-direction-amp-milestone-state
title: Miller — product direction & milestone state
status: active
created: 2026-05-30T16:32:42.067Z
updated: 2026-06-05T00:00:12.586Z
tags:
  - miller
  - project-direction
  - status
  - dogfood
  - read-path
---

## What Miller is (the spine — all docs agree)

- Read-only .NET 10 SQLite/MCP consumer of `julie-extract`. **The open-source free code-intelligence core.**
- **Eros = commercial extension over the same data**, not a rewrite/duplicate. The durable contract Eros depends on is the **on-disk `search.db` schema** (shareable artifact), NOT Miller's private .NET types or internal indexes.
- Dividing line: Miller answers "where is the code and what does it structurally connect to?"; Eros answers "what should the agent do next, how confident, what higher-level evidence?"
- Stay daemon-light; embeddings/semantic stay OUT of the free core (that is Eros's pillar-2).

Architecture contracts (unchanged): index DBs local at `<workspace>/.miller/symbols.db`; central discovery `~/.miller/workspaces.db`; `workspace_id` = SHA-256 of canonical root; freshness via `files.content_hash` (`blake3:`); read tools accept selectors. Pin: `julie-extract` v2.1.0, SQLite schema 2, extract contract 2, report schema 2.

## Current state — 2026-06-04

M0–M8 done. Recently SHIPPED on `main`: search projection split (OpenClaw first search ~1.8s/+55MB); collapsed-trigram **FTS5 sidecar default-ON** (`search.db`); CLI `status|list|refresh|full|open|remove` + single-sourced build version; large-workspace dogfood fixes.

## DEFERRED — source_regions / pillar-3 scope-aware lexical search (blocked on julie-extract 2.1.1)

Brainstormed + designed + **twice Codex-reviewed**; design is review-clean and ready: `docs/plans/2026-06-04-source-regions-pillar3-design.md`. **Blocked upstream:** julie-extract 2.1.0 emits `source_regions` only for JavaScript (verified live: 187 JS rows, **0 C#**, **0 doc_comments**), so a region-text index would be empty on C#/.NET. The user is adding all-language `source_regions` emission in the **julie-extractors 2.1.1** update (in progress); **resume when 2.1.1 lands** (re-verify coverage first). Design decisions locked: capability = region-TEXT index in `search.db` (slice file bytes; content search is docs-only and can't be reused) + cheap `has_doc` annotation. Resume conditions captured in the doc (has_doc from `symbols.doc_comment` not regions; CodeTokenizer token stream as FTS body; separate `MILLER_REGION_INDEX` flag + literal caps; `EnsureBuilt` needs workspaceRoot; fail-CLOSED region reads; per-language coverage warnings).

Process note: two adversarial Codex passes + a live-data check caught that the feature was upstream-blocked BEFORE any code was written — the reviews paid for themselves.

## Next track — TO DECIDE (work on something else until 2.1.1)

Leading unblocked candidate: **free-core / AOT release track** (`docs/plans/2026-06-04-free-core-boundary-and-aot-release.md`) — harden Native AOT (source-gen JSON, explicit MCP tool registration, Serilog trim), release matrix (linux-x64/arm64, osx-arm64, win-x64), platform archives paired with ONE matching `julie-extract`, align Eros-boundary docs. Alternatives: search cleanup follow-ups (widen symbol projection #3, re-measure #5, `mode=content` tuning #4) — small, can ride along; dogfood dashboard against registry.

## Guardrails

- Daemon-light: no Julie-style filewatcher/resource sink.
- Do NOT reintroduce full-index loads into `workspace status` / dashboard list/read paths.
- Symbol search correctness must survive a missing/stale/corrupt sidecar (self-heal to in-memory BM25). **Ranking stays in Miller's C#** (`Miller.Core.Search.Bm25`); FTS5 is recall-only.
- `Miller.Core` stays pure logic, ZERO I/O deps.
- Build 0 warnings / 0 errors (`dotnet build Miller.slnx -c Release`).
- Test split: `scripts/test.sh` (fast, every change) vs `scripts/test.sh scale` (indexing/extract path). julie-spawning test MUST be `[Trait("Category","Scale")]` via `ScaleTestSupport.RequireJulieServer()`.
- `AGENTS.md` generated from `CLAUDE.md` — edit `CLAUDE.md` then `scripts/sync-agents.sh`.
- SUPERSEDED (do not reinstate): "keep FTS5 parked" — FTS5 sidecar shipped default-on 2026-06-04 (shareability driver). Remaining caution: don't move BM25 *ranking* off in-memory C#; no embeddings in the free core.
