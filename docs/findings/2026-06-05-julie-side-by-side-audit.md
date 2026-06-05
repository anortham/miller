# Julie Side-By-Side Functionality Audit

- **Date:** 2026-06-05
- **Scope:** Miller beta surface compared against Julie's current code-intelligence product behavior.
- **Result:** no missing fundamental beta blocker found. Miller already covers the core beta loop:
  startup catch-up indexing, live freshness, stale checks, workspace lifecycle, CLI/MCP read tools,
  search/inspect/context/trace/impact/edit, and embedded agent instructions. The original narrower
  follow-ups were file-policy parity proof, a Miller skill package, and measured Julie-vs-Miller
  search-quality comparisons before post-beta search widening; file-policy parity and skills are now
  closed in follow-up work.

## Summary

| Area | Julie | Miller | Audit result |
| --- | --- | --- | --- |
| Workspace storage | Separate physical indexes per workspace under `.julie/indexes/{workspace_id}/`. | Local `<workspace>/.miller/symbols.db` plus central `~/.miller/workspaces.db` registry. | Functionally covered for beta. Miller's shape is simpler and aligned with the read-only consumer model. |
| Startup/catch-up freshness | Daemon runs session-connect catch-up using mtime and BLAKE3. | Indexer leader runs `julie-extract scan --force=false` startup delta scan after attaching watchers. | Covered for beta. |
| Live filewatcher | Daemon watcher queues events and serializes all writers through a mutation gate. | Single writer-lock leader owns recursive `FileSystemWatcher`, `.git/HEAD` watcher, coalesced queue, and serialized extract ops. | Covered for beta. |
| Stale checks | Julie detects drift during catch-up and watcher processing. | Miller checks registry/build revision, `index_fresh`, sidecar revision, and BLAKE3 file hashes before body/content reads. | Covered for beta. |
| Ignore and file policy | Rich walker: `.gitignore`, nested/git-root inheritance, `.julieignore`, extra `--ignore-file`, broad blacklisted dirs/extensions/filenames, minified/large/generated handling. | Watcher filter only skips `.git`, `.miller`, `node_modules`, `target`, `bin`, and `obj`; actual scan/indexability is delegated to `julie-extract`. | Gap confirmed in 2.1.2; v2.1.3 is now pinned locally and fixes `vendor/` plus git-root `.gitignore` inheritance for nested workspaces. |
| Auto vendor-ignore detection | Julie has vendor-scan behavior and auto-generated `.julieignore` tests. | Miller has no separate vendor discovery layer. | Not beta-blocking if `julie-extract` scan policy is enough; needs parity evidence. |
| CLI | Julie has a broad CLI including search, refs, symbols, context, call-path, blast-radius, workspace, signals, extract, and generic tool calls. | Miller has beta CLI verbs for search, inspect, context, impact, trace, workspace, version, help, and serve. | Covered for beta; broader Julie-like CLI remains optional test/dev ergonomics. |
| Agent guidance | Julie ships plugin skills plus injected instructions. | Miller embeds server instructions, tool descriptions, a subagent prompt block, and a project-local `.agents/skills` package. | Covered for beta. |
| Search quality | Julie uses Tantivy with code-aware tokenization, richer symbol fields, relationship text, role demotion, graph centrality, and optional semantic/vector behavior. | Miller intentionally keeps beta symbol search to `name + signature`, with separate `mode=content` and opt-in `regions=` surfaces. | Covered for beta by current decision, but post-beta widening should be driven by a measured Julie-vs-Miller query matrix. |
| Restart ergonomics | Julie daemon/adapter can auto-start and auto-restart stale binaries. | Miller MCP clients must rebuild/restart/reconnect; README and `miller version` / `workspace status` expose build SHA. | Already decided not beta-blocking. |

## Evidence Notes

Julie evidence:

- `crates/julie-core/src/walk.rs` defines `WalkConfig::full_index()` with `.gitignore`,
  `.julieignore`, blacklisted directories, git-root `.gitignore` inheritance for nested workspaces,
  and extra ignore files.
- `crates/julie-core/src/shared.rs` defines broad blacklisted extensions, filenames, and directories.
- `crates/julie-core/src/file_policy.rs` rejects blacklisted paths, allows supported extractor
  extensions, admits likely text files, and degrades too-large/minified/generated content to text-only.
- `CLAUDE.md` records daemon catch-up indexing, stale binary auto-restart, mutation-gated watcher
  writers, Tantivy search, graph centrality ranking, and semantic/vector search.
- Julie has repo-local skills under `.claude/skills`: `dead-code-audit`, `editing`, `explore-area`,
  `impact-analysis`, `search-debug`, and `web-research`.

Miller evidence:

- `src/Miller.Server/Hosting/IndexerService.cs` attaches recursive watchers, watches `.git/HEAD`,
  runs startup delta scan, serializes extract ops, and rebuilds the search sidecar under the writer
  lock.
- `src/Miller.Server/Hosting/WatchPathFilter.cs` intentionally accepts by default and only drops a
  short set of noisy path segments.
- `src/Miller.Core/Search/SearchableDocument.cs` documents beta symbol ranking as `name + signature`.
- `src/Miller.Core/Search/Bm25.cs` keeps ranking in C# for both in-memory and FTS-backed symbol search.
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` gives embedded search-before-read guidance,
  per-tool descriptions, and a subagent prompt block.
- Follow-up work added Miller-local skills under `.agents/skills`: `miller-explore-area`,
  `miller-impact-analysis`, `miller-editing`, `miller-bridge-trace`, `miller-cross-workspace`,
  and `miller-search-debug`.

## Follow-Ups

1. **File policy parity release:** `julie-extractors` v2.1.3 now makes Miller's delegated scan path
   match Julie's walker policy for `vendor/` and git-root `.gitignore` inheritance in nested workspaces.
   Miller's local pin and restore path now target 2.1.3; rerun Windows CI on the exact pushed beta
   candidate. Evidence is recorded in `docs/findings/2026-06-05-file-policy-parity-dogfood.md`.
2. **Miller skills:** Closed. The project-local skill package now teaches agents the Miller workflow
   outside the MCP server instructions: `explore-area`, `impact-analysis`, `editing`, `bridge-trace`,
   `cross-workspace`, and `search-debug`.
3. **Search-quality matrix:** Build a side-by-side Julie/Miller query set before widening Miller symbol
   search post-beta. Include symbol-name, natural-language, doc-comment, literal/env-var, path/file,
   call-flow, and bridge-provider queries. Measure result usefulness, latency, output size, and whether
   `mode=content` / `regions=` solved the workflow without widening symbol ranking.
4. **Optional CLI ergonomics:** Keep the current beta CLI narrow. Add Julie-like wrappers only when they
   make tests or CI materially easier.
5. **Optional restart ergonomics:** Keep rebuild/restart manual for beta. Revisit a daemon/adapter only
   if MCP restart friction remains the dominant dogfood cost after beta.

## Beta Impact

The audit no longer has an open proof gap: file-policy dogfood confirmed an upstream extractor mismatch,
and the v2.1.3 pin closes it locally. Treat the exact-candidate Windows CI rerun as the remaining
cross-platform beta hardening item.
