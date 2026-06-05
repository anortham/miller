# Miller Beta Readiness Checklist

- **Date:** 2026-06-05
- **Status:** Active checklist
- **Repos involved:** `miller`, `julie-extractors`
- **Decision level:** Product readiness gate

## Beta Definition

Miller is beta-ready when it is usable as the free local code-intelligence core for real agent work:

- MCP and CLI surfaces both work on real repositories.
- Search, inspect, context, trace, impact, and workspace lifecycle behavior are reliable enough to
  dogfood without falling back to Julie.
- The default path stays deterministic, lexical/structural, SQLite-backed, and embedding-free.
- Install/restore instructions are clear enough for another developer to try it.
- Known limits and post-beta work are documented instead of hidden.

Beta does not require Eros work, organization workflows, semantic/vector search, or a fully
release-blocking Native AOT gate. AOT remains release-readiness work unless beta is redefined as a
standalone packaged binary release.

## Current State

M0-M8 are complete. Miller has the default-on symbol search sidecar, workspace registry, cheap
registered-workspace status/list paths, projection-specific `search` and summary `inspect`, content
search, CLI verbs, and the `julie-extract` 2.1.1 source-region consumer.

## Must Finish Before Beta

### 1. Source Regions Closeout

- [x] Implement the `julie-extract` 2.1.1 and `source_regions` consumer path.
- [x] Dogfood `MILLER_REGION_INDEX=1` on real repositories.
- [x] Record region-search evidence: representative queries, result quality, build time, and
  `search.db` size delta. See `docs/findings/2026-06-05-source-region-dogfood.md`.
- [x] Decide beta behavior: keep region indexing opt-in for beta.
- [x] Explicitly defer follow-ups: `embedded` regions, trigram recall for regions, and exclusion
  queries.
- [ ] Investigate post-beta/default-on quality and size follow-ups from dogfood: multi-token region
  query semantics and very large `string_literal` sidecars.

### 2. Search And Inspect Quality

- [ ] Dogfood symbol search on real workflows using names, identifiers, doc comments, literals, and
  path-like queries.
- [ ] Decide whether the symbol projection must widen before beta. Candidate inputs:
  `symbols.doc_comment`, `identifiers.name`, bounded `identifiers.code_context`,
  `literals.literal_text`, and path tokens.
- [ ] Dogfood `mode=content` and document its current limits.
- [ ] Confirm region search, content search, and symbol search have clear, non-overlapping user
  expectations.
- [ ] Keep BM25 ranking in C# and keep embeddings out of the free-core beta path.

### 3. CLI And MCP Product Surface

- [x] CLI verbs exist for `search`, `inspect`, `context`, `impact`, `trace`, and `workspace`.
- [x] Workspace CLI supports `status`, `list`, `refresh`, `full`, `open`, and `remove`.
- [ ] Document CLI examples in `README.md`.
- [ ] Document stable-enough text and JSON output expectations for beta.
- [ ] Add or update CLI smoke tests for the commands a CI user would naturally call.
- [ ] Decide whether MCP restart/reconnect developer ergonomics are beta-blocking. Default answer:
  not blocking unless dogfood shows repeated product failures.

### 4. Performance And Reliability

- [ ] Re-run large-repo dogfood for first-read `search` and summary `inspect`.
- [ ] Confirm `workspace status`, dashboard/list, and registry reads do not hydrate full indexes.
- [ ] Confirm missing, stale, or corrupt symbol sidecars self-heal to in-memory BM25 search.
- [ ] Confirm region search fails closed when `MILLER_REGION_INDEX=1` is missing a fresh
  region-capable sidecar.
- [ ] Re-measure after any widened symbol projection before deciding incremental in-memory patching
  is still necessary.

### 5. Packaging, Restore, And Install

- [ ] Verify `scripts/restore-julie-extract.sh` restores the pinned 2.1.1 binary on a clean machine
  or clean checkout.
- [ ] Keep `scripts/julie-pins.json`, `PinnedJulieExtractVersion`, and contract tests in sync.
- [ ] Document MCP configuration and CLI install/run paths.
- [ ] Define beta package shape: source checkout only, platform archive, or both.
- [ ] If archives are included, pair each Miller target with only the matching `julie-extract`
  target and checksums.

### 6. Docs And User-Facing Limits

- [ ] Update `README.md` with current architecture, setup, CLI, MCP, and troubleshooting.
- [ ] Ensure `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` documents every beta tool surface.
- [ ] Document known beta limits: no embeddings, no Eros workflows, region search default status,
  AOT status, and any large-repo caveats.
- [x] Make `TODO.md` point at this checklist for beta-routing work.

## Final Beta Candidate Gate

Run these on the exact commit considered for beta:

```bash
dotnet build Miller.slnx -c Release
scripts/test.sh
scripts/test.sh scale
git diff --check
```

Also capture a short dogfood note with:

- `julie-extract` version and restore evidence.
- Real-repo workspace list/status output.
- Representative `search`, `inspect`, `context`, `trace`, and `impact` examples.
- Region-search examples if `MILLER_REGION_INDEX=1` is included in beta.
- Any failures or limits accepted for beta.

## Not Beta Blockers Unless Evidence Says Otherwise

- Full release-blocking Native AOT.
- Eros architecture or commercial workflow decisions.
- LanceDB, embeddings, semantic search, or vector projections.
- `embedded` source regions.
- Region trigram recall or exclusion queries.
- Incremental in-memory patching after refresh.
- Dashboard polish beyond local operational status.
