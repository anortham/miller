---
id: miller-project-direction-amp-milestone-state
title: Miller — product direction & milestone state
status: active
created: 2026-05-30T16:32:42.067Z
updated: 2026-06-03T04:05:00Z
tags:
  - miller
  - project-direction
  - status
  - dogfood
  - read-path
---

## What Miller Is

Miller is the intended replacement for Julie's daemon-heavy code intelligence path: a read-only .NET 10 SQLite/MCP server that consumes `julie-extract` output from the standalone `julie-extractors` product instead of parsing source itself or running embeddings.

Current architecture contracts:
- Index DBs stay local at `<workspace>/.miller/symbols.db`.
- Central discovery lives at `~/.miller/workspaces.db`.
- Read tools accept `workspace_id` selectors: display ID, unique prefix, full workspace ID, `current`, or `primary`; explicit `workspace_id` defaults refresh-first.
- Stable `workspace_id` is SHA-256 of canonical workspace root.
- Extraction integration is CLI-first through the pinned `julie-extract` binary.
- Current product pin is `julie-extract` v2.0.3 from `julie-extractors`; compatibility gates are SQLite schema 1, extract contract 1, and report schema 1.
- File freshness uses `files.content_hash` (`blake3:<hex>`, normalized before comparison) and is guarded by `artifact_metadata.hash_algorithm=blake3`.
- `artifact_metadata` is the artifact metadata surface for schema/contract/hash/root keys.

## Current State - 2026-06-03

- Julie-extractors migration is now the active extract contract line in Miller's handoff docs: `julie-extract` v2.0.3, SQLite schema 1, extract contract 1, report schema 1.
- M0-M8 are done on `main`, including registry, freshness, and dashboard foundations.
- Search dogfood UX cleanup is committed as `aa4e15f fix(search): clean up workspace selectors and file mode`: file-mode search now searches file path fragments, auto mode routes path-like queries to file search, compact workspace/read-tool output favors display IDs over raw SHA-256 IDs, and workspace selectors accept display ID, unique prefix, full ID, `current`, or `primary`.
- Restart dogfood after `aa4e15f` found two follow-up fixes: current display-ID prefixes such as `miller-816` must route to the live current index without registry refresh, and content search must run `JulieSchemaGate` before reading `files` so incompatible old Julie DBs fail with an actionable artifact error instead of raw SQLite.
- Verification for the search UX cleanup and follow-up fixes: `dotnet build Miller.slnx -c Release`, `scripts/test.sh all`, and `git diff --check` passed locally before commit.
- Large-workspace dogfood fixes are committed on `main` as `afffbd2 fix: bound large workspace read costs`.
- Brief/state update is committed as `a75e702 docs: update miller dogfood brief`.
- User rebuilt/restarted the Miller MCP server after those commits; current MCP tools are responding.
- MCP hot-reload/restart smoothing was discussed and intentionally deferred to avoid sidetracking.
- Dashboard is intended and should query the central registry plus shared telemetry, not discover workspaces by crawling arbitrary filesystem roots.
- M9/ad-hoc content and SQLite FTS5 remain product candidates, but symbol search should stay in-memory BM25 unless profiling proves a real need to move it.
- Live-test engine remains parked behind a spike; do not let it distract from registry/read-path correctness.

## Large Repo Dogfood

Dogfood repos registered under `~/source`:
- OpenClaw: `/Users/murphy/source/openclaw`, workspace id `36c53da0da7dca5eb5931da951a0abde79068f6d1a8cd68ef062fcdb5681d12e`, 565,828 symbols, `.miller/symbols.db` about 1.9 GB.
- Hermes Agent: `/Users/murphy/source/hermes-agent`, workspace id `a3a86f07956e0f8dbf1d2019dfdaae4158b564299909c2ca92d663ddbe9244a3`, 237,385 symbols, `.miller/symbols.db` about 673 MB.

Large-workspace issues fixed in `afffbd2`:
- `workspace status` for external workspaces loaded the full index. Added `WorkspaceIndexFactsReader` so status reads cheap metadata instead.
- Identifier fallback name resolution could fan out catastrophically. OpenClaw had about 1.09M identifier rows, and global fallback could produce hundreds of millions of speculative edges. `SymbolGraphReader` now caps fallback targets; precise relationship rows are still preserved.
- `WorkspaceIndexProvider` cache could duplicate large loads and retain stale revisions. It now uses single-flight loading, evicts failed loads, and evicts older entries for the same workspace when revision/path changes.
- Review found and fixed an in-flight stale-load race: stale loads can still return to their original caller, but stale keys can no longer evict newer cache keys after a registry revision/path change.

Dogfood after fixes:
- OpenClaw external status: about 153 ms.
- OpenClaw first cross-workspace search: about 6.2 s / 1.2 GB peak RSS; pre-fix runaway exceeded 36 GB and was killed.
- Hermes first cross-workspace search: about 3.1 s / 327 MB peak RSS.

Verification after fixes:
- `dotnet build Miller.slnx -c Release`: passed, 0 warnings/errors.
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~WorkspaceIndexProviderTests`: 13/13 passed.
- `scripts/test.sh`: 1160/1160 fast tests passed.
- `scripts/test.sh scale`: 14/14 scale tests passed.
- `git diff --check`: clean.
- External reviewer agent: no material findings.

## Next Product Work

Immediate next work: restart onto the new binary after the follow-up commit/push and re-dogfood current display-prefix routing plus cross-workspace content search on registered repos.

Primary product work after that: design and implement large-workspace read projections for first-read `search` / `inspect` performance.

Problem:
- Cross-workspace `search` / `inspect` still route through `WorkspaceIndexProvider` to `RepositoryIndexLoader.Load`.
- That hydrates the full `MillerRepositoryIndex`: symbols, BM25, graph, bridge data, resolver.
- On OpenClaw this still costs about 6.2s and 1.2 GB RSS on first cross-workspace search.

Recommended direction:
- Add projection-specific loading rather than using one full repository index for every read surface.
- `search` projection: load only the symbol/text fields needed for BM25 search results.
- `inspect` projection: support targeted symbol/file detail lookup without preloading full graph/bridge structures.
- `trace` / `impact`: lazy-load graph and bridge projections only when needed.
- Keep `workspace status` and dashboard metadata on cheap facts readers.
- Keep SQLite FTS5 parked unless profiling proves BM25 itself is the bottleneck.

Expected design acceptance criteria:
- External `workspace status` remains cheap and does not hydrate full indexes.
- First cross-workspace `search` avoids graph/bridge hydration.
- First cross-workspace `inspect` avoids loading the whole repo unless the request genuinely needs broader graph context.
- Projection cache invalidation still keys off workspace id, DB path, and revision.
- Fast suite remains fast; scale/dogfood harness captures large-workspace first-read cost.

Secondary work after read projections:
1. Dogfood dashboard against registry with Miller, Julie, OpenClaw, and Hermes registered.
2. Review `TODO.md` question about fuller Miller CLI coverage for CI/testing.
3. Revisit M9/ad-hoc content and FTS5 after symbol read path is stable.
4. Reconsider MCP restart smoothing only if it keeps interrupting work.

## Guardrails

- Keep Miller daemon-light: no Julie-style filewatcher/resource sink.
- Do not reintroduce full-index loads into `workspace status` or dashboard list/read paths.
- Do not allow unbounded global identifier fallback unless Julie emits stable `target_symbol_id` relationships that make fallback unnecessary.
- Keep provider cache eviction tied to insertion of the currently selected registry key, not load completion order.
- Do not jump to SQLite FTS5 before proving BM25/search computation, rather than hydration breadth, is the bottleneck.
- Keep the test split: `scripts/test.sh` for fast suite every change; `scripts/test.sh scale` when indexing/extract behavior changes.
- Build must stay warning-clean: `dotnet build Miller.slnx -c Release`.
- `Miller.Core` stays pure logic with no I/O dependencies.
- `AGENTS.md` is generated from `CLAUDE.md`; edit `CLAUDE.md` then run `scripts/sync-agents.sh`.
