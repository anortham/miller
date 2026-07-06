---
id: miller-project-direction-amp-milestone-state
title: Miller — product direction & post-v1.4.3 work queue
status: active
created: 2026-05-30T16:32:42.067Z
updated: 2026-07-06T00:24:01.505Z
tags:
  - miller
  - project-direction
  - status
  - v1.4.3
  - eros-ct
  - search-quality
  - ranking
  - site-metrics
---

## What Miller Is

- Miller is the read-only .NET 10 SQLite/MCP consumer of `julie-extract` output: the open-source/free local code-intelligence core.
- The replacement story is Miller + `julie-extractors` + Eros. Miller owns deterministic navigation, retrieval, workspace lifecycle, editing, and CLI/export contracts. `julie-extractors` owns parser-backed extraction. Eros owns semantic/vector retrieval, guidance, confidence views, history, CT orchestration, and commercial surfaces.
- Keep the free core deterministic, lexical/structural, daemon-light, and embedding-free.

Architecture contracts: workspace DBs at `<workspace>/.miller/symbols.db`; central discovery `~/.miller/workspaces.db`; `workspace_id` = SHA-256 of canonical root; freshness via `files.content_hash` (`blake3:`); read tools accept workspace selectors; search sidecar `.miller/search.db` default-on; content corpus `.miller/content.db`.

## Product Positioning

Miller is a living, up-to-date agent assistant, not a one-time graph dump. Frame it around freshness (version-aware leadership, revision deltas, artifact-id rebuild detection), a disciplined 9-tool MCP surface, token-cheap bounded outputs, and stable CLI/JSON contracts that let orchestrators (Eros) build on Miller without coupling.

## Current State — 2026-07-06

- Released and verified: Miller v1.4.3 (`1.4.3+216de3ea3b36`), pin `julie-extract` 2.8.1. Release evidence remains in `docs/findings/`.
- Backend-http bridge trace, patterns catalog (~130 pattern ids / ~36 languages), revision-delta impact (`impact --json --from-index-revision N --from-artifact-id ID` -> typed delta envelope with `tests[]`), and guidance-delivery budgets are shipped.
- The Rust CT impact work is complete through the local/live-gate evidence path and locally merged: Eros main has `00e8726 docs: record Rust CT impact live gate`; Miller main has `3df50ac docs: checkpoint Rust CT impact verification`. No push, tag, release, Miller pin bump, or Eros pin bump has been done without approval.
- The first text-search miss diagnosis slice is locally merged on Miller main at `e89236b diagnose text search empty results`: empty `search` and `content search` rows keep `empty_reason` and now add privacy-safe `query_shape` plus `empty_diagnosis`; compact empty hints now hand off to `mode=source`, `mode=content`, `mode=file`, or longer-query recovery when query shape supports it. Verification: focused SearchTool/ContentTool tests passed (114), and `scripts/test.sh` passed (2,837).
- After the merge, the running MCP/server-side search indexes for `/Users/murphy/source/miller` reported stale sidecar schema versions (`search.db` expected 7, had 6; `content.db` expected 2, had 1). Refresh/rebuild sidecars before trusting fresh local telemetry from main.

## Work Queue

1. **Text-search miss diagnosis and reduction — active.** The instrumentation slice is done; next collect a fresh dogfood window on the new build, aggregate empty rows by `tool`, `op`, `empty_reason`, `empty_diagnosis`, and `query_shape`, then fix the largest bucket. Likely fixes should improve empty-state handoffs, auto-routing/rescue, query-shape guidance, sidecar freshness/corpus coverage, or FTS/tokenizer behavior depending on the measured split. Do not widen default symbol ranking as the first fix.
2. **Ranking polish: present-but-not-top.** The foundation matrix logged 18 rows where the right anchor was returned but not ranked first (`docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md`). Small, benchmark-gated improvements only after the empty-result diagnosis has live data; keep ranking in C# BM25, FTS5 recall-only.
3. **Publish token-savings metrics on the public site.** Oldest open durable adoption priority: real measured token/latency savings from open-source repos under `~/source`, surfaced on `https://anortham.github.io/miller/` with reproducible methodology.
4. **Release/push follow-through — approval-gated.** Local main is ahead with completed Rust CT evidence and text-search diagnosis commits, but nothing has been pushed or released. Push/release/tag/pin changes only with explicit approval.

## Success Criteria

- Rust CT impact: done locally when live Eros CT evidence shows `scope=impacted selected=<small>` for the real julie-extractors edit and Miller docs/checkpoints record the gate. Any public release/pin/push remains separately approval-gated.
- Text-search miss diagnosis: a comparable telemetry window shows the cause split for source/content empty results; follow-up changes materially reduce empty rates or clearly document true no-hit/corpus limits.
- Ranking polish: present-but-not-top rows reduced without breaking existing hard gates.
- Site metrics: public site shows real measured numbers with reproducible methodology.

## Guardrails

- Stingy MCP surface: new MCP tools require explicit user approval; prefer existing tools, CLI/export contracts, skills, or dashboard.
- Daemon-light: no filewatcher/resource sink. No full-index loads in status/dashboard/list paths.
- Ranking stays in `Miller.Core.Search.Bm25`; FTS5 recall-only. Content search stays explicit by mode; comments/literals via `regions=`. Region indexing opt-in, fail-closed.
- `Miller.Core` pure logic, zero I/O. Build 0 warnings/0 errors. Test split load-bearing (`scripts/test.sh` fast / `scale` opt-in; julie-spawning tests tagged Scale via `ScaleTestSupport`).
- Language parity: julie-extract-backed features must work for every supported language before shipping.
- `AGENTS.md` generated from `CLAUDE.md` (`scripts/sync-agents.sh`).
- No tag/publish/push/release without explicit user approval. A pushed release-prep commit is a live marketplace release — publish in the same session.

## References

- `docs/plans/2026-07-05-rust-ct-impact-single-release.md` — completed Rust CT impact plan/evidence path
- `.memories/2026-07-06/002001_b68e.md` — text-search miss diagnosis checkpoint
- `docs/findings/2026-06-27-miller-julie-foundation-effectiveness-matrix.md` — ranking/recovery evidence
- `docs/contracts/cli-eros-v1.md` — public Eros-facing surfaces
- `TODO.md` — product/conditional backlog
