# Beta Candidate Dogfood

- **Date:** 2026-06-05
- **Workspace:** `/Users/murphy/source/miller`
- **Miller build used for dogfood:** `0.1.0+e51eb98a82d0`
- **Follow-up README CLI example dogfood:** `0.1.0+c53474eae69e`
- **`julie-extract` pin:** `julie-extract 2.1.1`
- **Decision:** source-checkout beta is viable after final gates; remaining issues are documented beta limits or post-beta polish.

## Restore And Version Evidence

Current binary:

```text
0.1.0+e51eb98a82d0
```

Extractor restore:

```text
Restoring julie-extract v2.1.1 for aarch64-apple-darwin
sha256 OK
Installed: /Users/murphy/source/miller/.tools/julie-extract
julie-extract 2.1.1
```

Windows restore/build/test evidence is captured in GitHub Actions run `27014962618`.

## Workspace Evidence

Current workspace status:

```text
# workspace  miller 0.1.0+e51eb98a82d0
miller-816288f47c5b  /Users/murphy/source/miller  [reader]
symbols: 6091  ext: 7  rev: 368  unknown  queue: empty
freshness: ready
```

Workspace registry listed 16 real repositories, including Miller, Julie, julie-extractors,
OpenClaw, Hermes, codenav, browser39, Flask, Express, and worktree entries. This validates that
`workspace list` remains a registry/read-path operation rather than a full-index hydration path.

Current local DB sizes:

```text
symbols.db  51M
search.db   5.6M
```

## Representative CLI Queries

Symbol search:

```bash
miller search "WorkspaceIndexProvider" --limit 5
```

Returned `WorkspaceIndexProvider`, both constructors, provider tests, and the recording provider.
This is the expected symbol-oriented behavior.

Content search:

```bash
miller search "beta readiness checklist" --mode content --limit 5
```

Returned the beta checklist, `TODO.md`, `README.md`, and prior search/region findings. This is the
right beta surface for prose/docs/workflow questions.

File inspect:

```bash
miller inspect src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs
```

Returned a compact file symbol listing with imports, fields, constructors, and provider methods,
including `ResolveSymbolSearch`, `ResolveContentSearch`, `ResolveRegionSearch`, and the cache helpers.

Context:

```bash
miller context "workspace registry freshness and search routing" --token-budget 1200
```

Returned relevant design docs, routing helpers, workspace render tests, provider tests, and registry
symbols. The bundle was useful for orienting on a cross-cutting area without reading full files.

Trace:

```bash
miller trace 397226f1fff52678b930467aeb01a860 --depth 1 --limit 10
```

The target is the `SearchTool.Search` method ID from JSON inspect output. Trace returned adjacent
telemetry, routing, mode parsing, and index-provider symbols.

Impact:

```bash
miller impact src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs --max-depth 1 --limit 10
```

Returned impacted tool methods (`WorkspaceTool`, `SearchTool`, `InspectTool`) and likely provider
tests. File-path impact is a usable beta workflow for broad change planning.

## README CLI Example Follow-Up

After the adoption-guidance restart, the direct binary equivalent of the README CLI examples was rerun on
build `0.1.0+c53474eae69e`.

The old generic examples exercised command shapes, but were not good source-checkout dogfood: `trace GetUser`
and `impact GetUser` returned clean "not found" messages, and `inspect auth/UserService.cs` returned an empty
file listing. The README examples now use real Miller targets:

```bash
miller search "WorkspaceIndexProvider" --limit 5
miller search "source-checkout beta" --mode content --limit 5
miller inspect src/Miller.Server/AgentInstructions.cs --depth full
miller context "CLI workspace routing" --token-budget 2000
miller trace AgentInstructions --depth 2
miller impact AgentInstructions --max-depth 2
miller workspace status
miller workspace list
miller version
```

Those commands returned useful results from the Miller checkout. JSON output was also spot-checked for
`search`, `inspect`, `context`, `impact`, and `workspace status`; `trace` remains text-only as documented.

## Region Search

Default region search fails closed without the opt-in environment variable:

```text
region search requires MILLER_REGION_INDEX=1 and a refreshed search sidecar.
```

With `MILLER_REGION_INDEX=1`, the current workspace also failed closed because the existing region
sidecar was stale:

```text
search.db ... is stale: revision 363, expected 368. Refresh or rebuild the search index.
```

A plain `workspace refresh` advanced the readable workspace revision but did not rebuild the stale
region sidecar. An explicit `workspace full` attempt returned `lock_busy` because a live Miller process
held the writer/indexer lock. This matches the beta decision: region indexing is opt-in, explicit, and
not a blocker for the default beta path. Successful opt-in region rebuild/query evidence remains in
`docs/findings/2026-06-05-source-region-dogfood.md`.

## Accepted Beta Limits

- Ambiguous symbol targets in `inspect`, `trace`, and `impact` require a file path, a more specific
  symbol, or a `symbol_id`. The CLI reports ambiguity clearly, but this remains a polish area.
- `workspace --help` currently falls through to `workspace status`; command-specific help polish can
  wait unless dogfood shows it confuses users.
- `workspace full` can return `lock_busy` while another Miller process owns the writer lock. That is
  acceptable for beta, but the troubleshooting path should stay visible in docs.
- Region indexing remains opt-in and stale-region sidecars fail closed.

## Beta Routing

The source-checkout beta path is ready for final gate verification:

- default search, content search, inspect, context, trace, impact, workspace status/list, and restore
  paths all worked on real Miller state;
- cross-platform restore/test evidence exists in CI;
- region search stays opt-in and documented;
- the remaining work before calling beta is the final build/fast/scale/diff gate on the commit that
  contains this note.
