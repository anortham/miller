# Beta Candidate Dogfood

- **Date:** 2026-06-05
- **Workspace:** `/Users/murphy/source/miller`
- **Miller build used for dogfood:** `0.1.0+e51eb98a82d0`
- **Follow-up README CLI example dogfood:** `0.1.0+c53474eae69e`
- **Final pushed read-path candidate:** `0.1.0+91288557137a`
- **`julie-extract` pin:** `julie-extract 2.1.3` for the final beta candidate; earlier snapshots below
  record the 2.1.1 state that existed when those specific checks were run.
- **Decision:** source-checkout beta is viable after final gates; remaining issues are documented beta limits, product/release decisions, or post-beta polish.

## Final Pushed Candidate Follow-Up - 2026-06-06

The read-path follow-up candidate was pushed to `origin/main` at commit
`91288557137a1711e148628b374863526ae4b3ab` (`perf: use sqlite graph for cli reads`), build version
`0.1.0+91288557137a`.

Local verification on the pushed commit:

```text
dotnet build Miller.slnx -c Release  -> 0 warnings / 0 errors
scripts/test.sh                      -> 1,631 fast tests passed
scripts/test.sh scale                -> 25 scale tests passed
git diff --check                     -> clean
```

GitHub Actions run `27063713900` passed on the same commit:

```text
build-test    -> restore, Release build, default time-budgeted test suite passed
windows-fast  -> restore, Restore julie-extract (Windows), Release build, scripts/test.ps1 passed
scale-test    -> skipped for push, as expected for the CI matrix
```

This supersedes the earlier final-gate note below. The current source-checkout beta candidate is the
branch tip at `9128855`.

Current pin follow-up:

```text
scripts/julie-pins.json -> version 2.1.3
MillerExtractContract.PinnedJulieExtractVersion -> 2.1.3
.tools/julie-extract --version -> julie-extract 2.1.3
```

## Earlier Restore And Version Evidence

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

## Dashboard Evidence

The dashboard was rebuilt from the source checkout and launched on loopback with the CLI launcher:

```bash
miller dashboard
```

The launcher starts one machine-global dashboard process and subsequent sessions reuse it while opening the
current workspace selector URL:

```text
dashboard started (pid 96245): http://127.0.0.1:4977/?workspace_id=816288f47c5b0cf50c2eed1a22557d97100a6811846cc2061687421eaa5cc227
~/.miller/dashboard.pid -> 96245
~/.miller/dashboard.json -> ProcessId 96245, Url http://127.0.0.1:4977
lsof -> dotnet PID 96245 listening on 127.0.0.1:4977
/healthz -> miller-dashboard ok
dashboard already running: http://127.0.0.1:4977/?workspace_id=816288f47c5b0cf50c2eed1a22557d97100a6811846cc2061687421eaa5cc227
miller dashboard --port 5001 -> dashboard already running: http://127.0.0.1:4977/?workspace_id=816288f47c5b0cf50c2eed1a22557d97100a6811846cc2061687421eaa5cc227
no listener on 127.0.0.1:5001
```

The root page rendered registered workspaces and a visible telemetry table, with static SSR Razor components
and htmx fragment targets:

```text
Miller Dashboard
Workspaces
Telemetry
Avg ms
p95 ms
Est tokens
hx-get="/fragments/dashboard?workspace_id=..."
hx-target="#dashboard-content"
```

Registry endpoint:

```text
/workspaces.json -> 18 registered workspaces
first: browser39-7a38e8d999bb, state loaded_existing
```

Scoped telemetry endpoint against the Miller workspace:

```text
/telemetry.json?workspace_id=816288f47c5b0cf50c2eed1a22557d97100a6811846cc2061687421eaa5cc227
total_calls: 1151
tools: 6
search: calls 439, error_count 27, last_call_ts 2026-06-05T23:25:41.655Z, last_outcome ok,
last_error_ts 2026-06-05T20:44:49.700Z, last_error_kind IncompatibleExtractException
recent_errors[0]: search IncompatibleExtractException at 2026-06-05T20:44:49.700Z
```

The combined dashboard fragment for the Miller workspace returned `#dashboard-content`, the selected Miller
workspace row, `#telemetry-panel`, per-tool rows including `context` and `search`, `Last call`, `Last error`,
and the `Recent errors` list. Static assets were served from the Release output and accepted `HEAD` checks:

```text
/dashboard.css -> 200 OK, 4338 bytes
/lib/htmx/htmx.min.js -> 200 OK, 50917 bytes
```

The dashboard does not hydrate full indexes for listing or telemetry. `DashboardData.ReadWorkspaces` reads
`~/.miller/workspaces.db`; `DashboardData.ReadTelemetrySummary` reads `~/.miller/telemetry.db`; empty or missing
DB behavior is pinned by `DashboardRegistryReadTests`.

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
- the loopback dashboard shows registered workspaces and scoped per-tool telemetry without requiring JSON
  inspection;
- cross-platform restore/test evidence exists in CI;
- region search stays opt-in and documented;
- the remaining work before calling beta is the final build/fast/scale/diff gate on the commit that
  contains this note.
