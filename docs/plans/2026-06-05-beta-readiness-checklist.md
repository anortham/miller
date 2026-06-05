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

## Dashboard Boundary

Miller keeps a narrow loopback dashboard for local operational status: registered workspaces, index
freshness, telemetry summaries, tool latency/failure signals, sidecar health, and manual refresh or
troubleshooting actions. Eros owns the richer product UI: agent guidance, next-action recommendations,
higher-level evidence/confidence, semantic/vector retrieval views, and commercial workflows.

The Miller dashboard must stay daemon-light and must not hydrate full indexes merely to list or
status workspaces. It should read the workspace registry, telemetry DB, and lightweight sidecar facts.

## Must Finish Before Beta

### 1. Source Regions Closeout

- [x] Implement the `julie-extract` 2.1.1 and `source_regions` consumer path.
- [x] Dogfood `MILLER_REGION_INDEX=1` on real repositories.
- [x] Record region-search evidence: representative queries, result quality, build time, and
  `search.db` size delta. See `docs/findings/2026-06-05-source-region-dogfood.md`.
- [x] Decide beta behavior: keep region indexing opt-in for beta.
- [x] Explicitly defer follow-ups: `embedded` regions, trigram recall for regions, and exclusion
  queries.
- [x] Record post-beta/default-on follow-ups from dogfood: multi-token region query semantics were
  tightened to all distinct terms and `MILLER_REGION_MAX_BYTES` exposes the existing per-region cap;
  default-on still waits on re-measurement and very large `string_literal` sidecars.

### 2. Search And Inspect Quality

- [x] Dogfood symbol search on Miller workflows using names, identifiers, doc comments, literals/env
  vars, and path-like queries. See `docs/findings/2026-06-05-search-content-dogfood.md`.
- [x] Hide low-signal `import`/`module` rows for natural-language symbol search while keeping them for
  single-identifier queries.
- [x] Hide low-signal `import`/`module` rows for path-like file search while preserving real file
  symbols.
- [x] Decide whether the symbol projection must widen before beta: no, not solely for prose,
  doc-comments, literals, or docs. Use `mode=content` / `regions=` and revisit after fresh evidence.
- [x] Dogfood `mode=content` and document its current limits.
- [x] Confirm region search, content search, and symbol search have clear, non-overlapping beta
  expectations.
- [x] Keep BM25 ranking in C# and keep embeddings out of the free-core beta path.

### 3. CLI And MCP Product Surface

- [x] CLI verbs exist for `search`, `inspect`, `context`, `impact`, `trace`, and `workspace`.
- [x] Workspace CLI supports `status`, `list`, `refresh`, `full`, `open`, and `remove`.
- [x] Document CLI examples in `README.md`.
- [x] Document stable-enough text and JSON output expectations for beta.
- [x] Add or update CLI smoke tests for the commands a CI user would naturally call. The
  fast in-process suite covers `search`, `inspect`, `context`, `impact`, `trace`, and
  workspace lifecycle verbs; subprocess coverage pins the real binary for representative
  version/search/workspace flows.
- [x] Decide whether MCP restart/reconnect developer ergonomics are beta-blocking: not blocking
  for beta. README documents the expected restart requirement and `miller version` /
  `workspace status` build-SHA checks.

### 4. Performance And Reliability

- [x] Re-run large-repo dogfood for first-read `search` and summary `inspect`. See
  `docs/findings/2026-06-05-large-repo-readpath-dogfood.md`.
- [x] Confirm `workspace status`, dashboard/list, and registry reads do not hydrate full indexes.
- [x] Confirm missing, stale, stale-schema, damaged-FTS, or corrupt symbol sidecars self-heal to
  in-memory BM25 search.
- [x] Confirm region search fails closed when `MILLER_REGION_INDEX=1` is missing a fresh
  region-capable sidecar.
- [x] Re-measure after any widened symbol projection before deciding incremental in-memory patching
  is still necessary: not applicable before beta because the beta decision is not to widen the
  symbol projection.

### 5. Cross-Platform Compatibility

- [x] Keep the MCP server launch path cross-platform: `mcp-config.json` invokes the `miller`
  binary with `serve`, not a shell script.
- [x] Provide PowerShell mirrors for beta-critical scripts: `restore-julie-extract.ps1`,
  `test.ps1`, `sync-agents.ps1`, and `install-hooks.ps1`.
- [x] Add a convention guard so beta-critical shell scripts cannot drift back to Unix-only
  entry points.
- [x] Add a Windows fast-suite CI job that uses the PowerShell test wrapper.
- [x] Verify the Windows restore/test path on a Windows host or CI run:
  GitHub Actions run `27014619404` on 2026-06-05 passed
  `scripts/restore-julie-extract.ps1`, `dotnet build`, and `scripts/test.ps1`.
- [x] Audit package/archive scripts for Unix-only assumptions before a packaged beta: not
  required for source-checkout beta. Keep as release-readiness follow-up if beta expands to
  platform archives.

### 6. Packaging, Restore, And Install

- [x] Verify `scripts/restore-julie-extract.sh` restores the pinned 2.1.1 binary on a clean machine
  or clean checkout. Fresh macOS arm64 restore on 2026-06-05 downloaded v2.1.1, verified
  sha256, and installed `julie-extract 2.1.1`.
- [x] Verify `scripts/restore-julie-extract.ps1` restores the pinned 2.1.1 binary on Windows x64.
  GitHub Actions run `27014619404` on 2026-06-05 restored the Windows x64 archive
  before the Windows build and fast suite.
- [x] Keep `scripts/julie-pins.json`, `PinnedJulieExtractVersion`, and contract tests in sync.
- [x] Document MCP configuration and CLI install/run paths.
- [x] Define beta package shape: source checkout beta with per-platform restore scripts.
  Platform archives remain release-readiness work, not a beta blocker.
- [x] If archives are included, pair each Miller target with only the matching `julie-extract`
  target and checksums: not required for source-checkout beta; the release workflow already
  verifies Unix vs. Windows packaged extractor shape for archive builds.

### 7. Docs And User-Facing Limits

- [x] Update `README.md` with current architecture, setup, CLI, MCP, and troubleshooting.
- [x] Ensure `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` documents every beta tool surface.
- [x] Document known beta limits: no embeddings, no Eros workflows, region search default status,
  AOT status, and any large-repo caveats including full `inspect` cost.
- [x] Make `TODO.md` point at this checklist for beta-routing work.

## Final Beta Candidate Gate

Run these on the exact commit considered for beta:

Unix/macOS:

```bash
dotnet build Miller.slnx -c Release
scripts/test.sh
scripts/test.sh scale
git diff --check
```

Windows PowerShell:

```powershell
dotnet build Miller.slnx -c Release
scripts/test.ps1
scripts/test.ps1 scale
git diff --check
```

Also capture a short dogfood note with:

- `julie-extract` version and restore evidence.
- Real-repo workspace list/status output.
- Representative `search`, `inspect`, `context`, `trace`, and `impact` examples.
- Region-search examples if `MILLER_REGION_INDEX=1` is included in beta.
- Any failures or limits accepted for beta.
- Cross-platform evidence: Unix/macOS wrapper output and Windows PowerShell wrapper output or CI run.

Captured in `docs/findings/2026-06-05-beta-candidate-dogfood.md`.

## Not Beta Blockers Unless Evidence Says Otherwise

- Full release-blocking Native AOT.
- Eros architecture or commercial workflow decisions.
- LanceDB, embeddings, semantic search, or vector projections.
- `embedded` source regions.
- Region trigram recall or exclusion queries.
- Incremental in-memory patching after refresh.
- Dashboard polish beyond local operational status.
