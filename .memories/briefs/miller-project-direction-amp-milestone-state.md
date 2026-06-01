---
id: miller-project-direction-amp-milestone-state
title: Miller — project direction & milestone state
status: active
created: 2026-05-30T16:32:42.067Z
updated: 2026-06-01T00:54:03.755Z
tags:
  - miller
  - project-direction
  - status
  - dogfood
---

## What Miller Is

Miller is the intended replacement for Julie's daemon-heavy code intelligence path: a read-only .NET 10 SQLite/MCP server that consumes pinned `julie-server extract` output instead of parsing source itself or running embeddings.

Current architecture contracts:
- Index DBs stay local at `<workspace>/.miller/symbols.db`.
- Central discovery lives at `~/.miller/workspaces.db`.
- Read tools accept `workspace_id` and `ensure_fresh`; explicit `workspace_id` defaults refresh-first.
- Stable `workspace_id` is SHA-256 of canonical workspace root.
- File freshness uses Julie's raw-byte BLAKE3 in `files.hash`, guarded by `external_extract_metadata.hash_algorithm=blake3`.
- Current Julie extract line for Miller is v7.13.2 / extract contract 3 / schema 28.

## Current State - 2026-06-01

- M0-M8 are done on `main`, including registry, freshness, and dashboard foundations.
- Large-workspace dogfood fixes are committed on `main` as `afffbd2 fix: bound large workspace read costs`.
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

MCP restart note:
- The Release binary was rebuilt.
- Current Codex CLI Miller children were killed to force a restart, but this active session's `miller` MCP transport stayed closed instead of auto-respawning. A fresh Codex session should pick up the rebuilt binary from `~/.codex/config.toml`; current app-server/subagent Miller processes were intentionally left alone.

## Next Work

1. Start a fresh Codex session or otherwise restart the MCP client so `miller` tools attach to the rebuilt `afffbd2` binary.
2. Attack the remaining first-read cost: cross-workspace `search`/`inspect` still hydrate too much of the index on first use. Likely direction is projection-specific loading or a persisted read/search model, not moving all symbol search to SQLite FTS5 by default.
3. Dogfood the dashboard against the registry with Miller, Julie, OpenClaw, and Hermes registered.
4. Revisit M9/ad-hoc content and FTS5 for file/content search after the symbol read-path is stable.
5. Review `TODO.md` questions about Miller CLI coverage and smoother MCP rebuild/reconnect workflow.

## Guardrails

- Keep Miller daemon-light: no Julie-style filewatcher/resource sink.
- Do not reintroduce full-index loads into `workspace status` or dashboard list/read paths.
- Do not allow unbounded global identifier fallback unless Julie emits stable `target_symbol_id` relationships that make fallback unnecessary.
- Keep provider cache eviction tied to insertion of the currently selected registry key, not load completion order.
- Keep the test split: `scripts/test.sh` for fast suite every change; `scripts/test.sh scale` when indexing/extract behavior changes.
- Build must stay warning-clean: `dotnet build Miller.slnx -c Release`.
- `Miller.Core` stays pure logic with no I/O dependencies.
- `AGENTS.md` is generated from `CLAUDE.md`; edit `CLAUDE.md` then run `scripts/sync-agents.sh`.
