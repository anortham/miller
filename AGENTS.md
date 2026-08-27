# Miller — agent working notes

Miller is a read-only .NET 10 SQLite/MCP consumer of `julie-extract` output. Parser-backed
extraction belongs to the pinned `julie-extract` binary; embedding generation belongs to the pinned
`julie-semantic-sidecar` binary. Miller may re-read workspace source text (content corpus, source
regions, explicit text search) with extractor hashes/spans as freshness guards. Architecture:
[README.md](README.md). Documentation map (current vs historical): [docs/README.md](docs/README.md).

This file is a rules and onboarding doc. Design history, field reports, and rationale live in
`docs/` (plans, findings, ADRs) and in git history — link to them, do not inline them here.

## Ownership boundary

- Miller owns the deterministic local agent-tool core (search, inspect, context, trace, impact,
  edit, workspace lifecycle, content import, patterns, telemetry, metrics/reports) plus default-on,
  off-switchable local semantic retrieval
  ([ADR-0003](docs/adr/ADR-0003-semantic-retrieval-ownership.md); `MILLER_SEMANTIC=off` is a
  permanent zero-work guarantee; lexical-only output stays byte-identical).
- `julie-extractors` / `julie-extract` owns extraction and standalone extract workflows. Do not
  absorb them, and do not add fleet-level semantic surfaces (cross-workspace ranking, guidance/
  confidence views, embeddings-as-a-service) — those were reserved by the retired Eros.
- The family store is a shared contract: `julie-extract` writes `store.db`/`coord.db`/manifests;
  Miller reads a pinned view and writes its own sidecars. Both repos have the same owner — when the
  store is wedged, repair it directly and fix the root cause in whichever repo owns it. Store mode
  is default-on; `MILLER_INDEX_STORE=off` exports to the legacy artifact first — never serve a
  stale legacy artifact.
- References are answered at query time (`QueryTimeResolver`, policy v6, `RevisionFactCache`).
  There is no materialized resolution; `views.resolution_state` is permanently `unbound`.
- **Adding a new MCP tool requires explicit user approval.** Keep the MCP surface stingy: prefer
  improving an existing tool, a CLI/export contract, a skill, or the dashboard.

## Language parity (load-bearing product rule)

A feature built on `julie-extract` data is not done until it works for every language
julie-extractors supports. Verify per-language coverage on a real extract before shipping
(`SELECT language, kind, COUNT(*) FROM <table> GROUP BY 1,2`). When a capability needs new
extraction, add it across all supported languages in `julie-extractors`, not one at a time.

## Testing — read this before running tests

The suite is split in two; keeping them separate is load-bearing (guards fail the build if the
split erodes).

- **Inner loop = focused tests only:** `dotnet test --filter "FullyQualifiedName~<TestClassName>"`.
  Do not run suites per edit.
- **Fast suite at task boundaries:** a bare `dotnet test` runs only `Category!=Scale` (enforced by
  `VSTestTestCaseFilter` in [`Miller.Tests.csproj`](tests/Miller.Tests/Miller.Tests.csproj)). Run it
  once when a coherent task lands.
- **Scale suite is opt-in:** `scripts/test.sh scale` (or `all`; PowerShell mirrors exist). Run at
  the branch gate or when touching the indexing/extract or CT-provider path. Scale tests skip (not
  fail) when `.tools/julie-extract` or a provider toolchain is missing.
- **Never rerun a green suite on an unchanged tree.** Cite the prior run instead.

Rules when adding tests:

- A test that spawns `julie-extract` MUST be `[Trait("Category","Scale")]` at class level and MUST
  use `ScaleTestSupport.RequireJulieServer()` — the single launch signal
  [`ScaleTraitConventionTests`](tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs) trusts.
  Do not re-add private locator copies.
- A test that spawns a real CT provider MUST be Scale-tagged and use the
  [`CtProviderTestSupport`](tests/Miller.Tests/Testing/CtProviderTestSupport.cs) `Require*` signals;
  guarded by `CtScaleTraitConventionTests`.
- Keep the fast suite fast and pure; real I/O or heavy fixtures belong in Scale.

## Build

- Targets `net10.0`; `dotnet build Miller.slnx -c Release` must be 0 warnings / 0 errors
  (warnings are errors via `Directory.Build.props`).
- `Miller.Core` is pure logic with ZERO I/O deps. Keep it that way.
- The build copies `.tools/` next to the output; `VerifyPinnedJulieExtractVersion` and
  `VerifyPinnedSemanticSidecarVersion` (in `Miller.Server.csproj`) fail the build if the restored
  binaries are missing or do not match `scripts/julie-pins.json` / `scripts/semantic-pins.json`.
  After a pin bump, re-run restore. Deliberate offline escape hatches:
  `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1`, `MILLER_ALLOW_MISSING_SEMANTIC=1` (CI sets both for
  no-restore jobs; `release.yml` restores both and sets neither).
- Source build of the extractor: `MILLER_JULIE_SOURCE=<path> scripts/restore-julie-extract.sh
  --from-source` (`.ps1` on Windows).
- Version is single-sourced in `Directory.Build.props` → `MillerVersion.Current`.

## Release

Process: [docs/release-process.md](docs/release-process.md) (two-step validate-then-promote).
Rules that gate every release:

- **Windows verification is a required pre-release gate.** CI runs no per-push Windows test job
  (hosted runners were slow and flaky; removed 2026-08-27). Before validating packages, run the
  fast suite on the local win-test guest (`win-test sync miller`, then
  `win-test run miller -- powershell -Command "dotnet test --filter 'Category!=Scale'"`) and
  record the result in the release verification finding.

- Release archives ship `miller`, the matching `.tools/julie-extract`, the semantic runtime, and
  the dashboard executable + `wwwroot` assets (local-first — no font CDN). Keep the platform matrix
  in step with `scripts/julie-pins.json`.
- Keep `Directory.Build.props`, `miller-plugin.json`, and the Claude/Cursor/Codex plugin manifests
  version-aligned on every release.
- **Every release ships release notes**: `docs/release-notes/v<version>.md`, the `docs/README.md`
  pointer, and the GitHub release body set from that file (`gh release edit --notes-file`). The
  workflow writes only a placeholder body.
- **A pushed release-prep commit is a live marketplace release.** Marketplaces serve manifests from
  `origin/main` HEAD; publish the GitHub release in the same working session or updates 404.
- Do not publish, retag, delete, or overwrite a release without explicit user approval. README
  release facts come from live GitHub data, never guessed.
- Tag-push 403 rule: GitHub refuses release creation targeting a commit whose diff against
  default-branch HEAD touches `.github/workflows`. Never push workflow-touching commits to main
  while a tag-push release run is building. Recovery: `scripts/release-promote.sh` (see
  release-process.md).

## Public docs

- `README.md` is the public first-use entry point — keep the quickstart near the top and install
  paths clear (plugins, release archive, manual MCP config, source checkout). Public site:
  `https://anortham.github.io/miller/`.
- When updating harness guidance, edit `CLAUDE.md` first, run `scripts/sync-agents.sh` (or `.ps1`),
  then confirm `cmp -s CLAUDE.md AGENTS.md`.

## Server & indexing invariants

Each rule below is load-bearing; the linked code/doc carries the full design.

- **Host lifecycle:** the Generic Host constructs every `IHostedService` up front; no hosted-service
  constructor may read an `IndexBootstrapService` getter (they throw until bound). Tool calls wait
  only the `MILLER_BOOTSTRAP_GRACE_SECONDS` window then return an actionable not-ready result. The
  host graph lives in
  [`MillerServiceRegistration.AddMillerServices`](src/Miller.Server/Hosting/MillerServiceRegistration.cs),
  guarded by `HostStartupRegistrationTests`.
- **Version-aware leadership:** `artifact_metadata.binary_version` never goes backwards. Older
  bundled extractor → permanent reader; newer leader → forced full rescan on claim; newer reader
  displaces an older live leader via a graceful `yield` (never kill processes). Equal versions never
  yield. Downgrade escape hatch: `MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1`. Logic:
  [`LeadershipEligibility`](src/Miller.Indexing/LeadershipEligibility.cs); design:
  [`docs/plans/2026-06-11-version-aware-leadership-design.md`](docs/plans/2026-06-11-version-aware-leadership-design.md).
- **Full rebuilds promote, never merge:** force scans extract into `symbols.db.rebuild` and
  atomically promote ([`FullRebuildPromotion`](src/Miller.Indexing/FullRebuildPromotion.cs)). Never
  point a force scan at the live served DB. Rebuild detection is by `artifact_id` change, never
  revision comparison; `FreshnessService` reopens its reader per poll. In-place escape hatch:
  `MILLER_FULL_REBUILD_INPLACE=1`; promote retry bound: `MILLER_PROMOTE_RETRY_TIMEOUT`.
- **Extraction parallelism is always capped:** every scan argv carries `--jobs`
  ([`ExtractJobsPolicy`](src/Miller.Indexing/ExtractJobsPolicy.cs), default
  `min(4, cores/2)`); `MILLER_EXTRACT_JOBS` overrides; an explicit `jobs:` argument beats the env
  var (it carries a post-OOM safety response).
- **Every scan is supervised:** `--spool-dir <.miller>/spool`, `--progress-file
  <.miller>/scan.progress` (a sibling of the spool dir, never inside it), and `--parent-pid`,
  resolved from the artifact's directory by
  [`ExtractSupervisionPolicy`](src/Miller.Indexing/ExtractSupervision.cs).
  `MILLER_EXTRACT_SUPERVISION=off` restores the bare argv.
- **Scan intent, not `bool force`:** every whole-repo scan carries a
  [`ScanIntent`](src/Miller.Core/Freshness/ScanIntent.cs). Only `UserFullRebuild` may downgrade to a
  delta on retry; repair intents exist because the artifact cannot be trusted. The rescan latch is a
  set discharged by `ScanIntentPolicy.Satisfies`; `ExtractorUpgrade` is discharged by any completed
  force. Watcher overflow is not a force.
- **A downgrade is a third scan outcome** (neither success nor failure): the prior artifact serves
  with degraded freshness and the rebuild is still owed. It must not clear the failure record,
  must not discharge the pending rebuild, and reaches callers as `ScanOutcome.Kind.Downgraded`.
  Only the automatic path may downgrade (`bypassBackoff` gates it).
- **Scan-failure backoff is persisted and is the only retry timer:**
  `<workspace>/.miller/scan-failure.json`
  ([`ScanFailureJournal`](src/Miller.Indexing/ScanFailureJournal.cs)), schedule 30s→2m→10m→30m
  jittered, shared across processes. Exit 137 clamps the next attempt to `--jobs 1`. Explicit user
  requests pass `bypassBackoff: true`; the automatic path (`WorkspaceIndexProvider`) must not. The
  coalescing guard is the singleton `BackgroundRefreshGate` — never move it onto the transient
  `WorkspaceIndexProvider`. Records fold via `Strongest`; clearing uses
  `ScanIntentPolicy.ClearsFailureRecord`, not `Satisfies`.
- **A linked worktree's `.git` is a FILE.** Resolve the admin dir through
  [`GitWorktreeLayout`](src/Miller.Indexing/GitWorktreeLayout.cs) (no `git` subprocess) and watch
  `GitDir`, never `CommonDir`.
- **Root disappearance and path reuse:** `workspace_id` is SHA-256 of the canonical root, so a
  removed-and-recreated worktree reuses the id.
  [`WorkspaceRootPresenceMonitor`](src/Miller.Indexing/WorkspaceRootPresenceMonitor.cs) suspends
  scanning on disappearance and re-bootstraps at `RootRebind` when the root returns with a
  different identity (compared only across a disappearance).
- **Sensitive-root guard:** [`WorkspaceRootSafety`](src/Miller.Server/Tools/WorkspaceRootSafety.cs)
  refuses home dirs, drive roots, and system dirs — at the top of `Program.cs` and in
  `workspace open`. Keep the forbidden set in step with julie's `root_safety.rs`.
- **CLI vs server:** the same binary branches at the top of `Program.cs` on
  [`CliDispatch.IsCliInvocation`](src/Miller.Server/Cli/CliDispatch.cs) — no args or `serve` → MCP
  host; any other verb → CLI and exit. The CLI owns stdout and starts no logging/background
  services.
- **Bounded fact reads are one-shot-CLI-only, requested by name** (`OpenForOneShotCli` →
  `RevisionFactCache.LoadBounded`): absence of a fact-cache store is NOT the signal — resident
  store-less sessions (edit/tests tools, CT daemon) keep the full load. A bounded cache never
  reports a missing version as an empty slice, never advances generations, and reads through its
  own connection in one deferred transaction. `MILLER_BOUNDED_FACTS=off` restores the full load.
  Guarded by `BoundedRevisionFactCacheTests` and the rendered A/B in `CliDispatchTests`.
- **Eros-facing surfaces are public process/artifact contracts**, documented in
  [`docs/contracts/cli-eros-v1.md`](docs/contracts/cli-eros-v1.md). Add new ones only for concrete
  workflows the documented contracts do not cover.
- **A launch may never fail silently:** the Node launcher logs every install stage to
  `~/.miller/logs/launcher-<date>.log` AND stderr, bounds downloads (15s idle watchdog, 3 retries),
  sweeps stale install leftovers, and prunes the version cache (keep installing + one
  most-recently-used). `Program.cs` wraps startup in one catch →
  [`StartupFailureLog`](src/Miller.Server/Logging/StartupFailureLog.cs) (exit 70, always stderr);
  Serilog `SelfLog` is enabled to stderr before `CreateLogger`; a
  [`StartupBreadcrumb`](src/Miller.Server/Logging/StartupBreadcrumb.cs) line names the log dir
  unconditionally (no breadcrumb ⟹ miller never started). Reader-facing doc:
  [docs/install.md](docs/install.md) "When the plugin fails to connect".
- **Logging:** all processes append to one shared daily pair (`.miller/logs/miller-<date>.log` +
  `.jsonl`, Serilog `shared:true`); `pid`/`role`/`cid` are line properties. No per-pid files, no
  startup reaper.

## Workspace registry & sidecars

- Index DBs live at `<workspace>/.miller/symbols.db`; discovery is `~/.miller/workspaces.db`. Read
  tools accept `workspace_id` selectors (display ID, prefix, full ID, root path, `current`,
  `primary`). An explicit `workspace_id` defaults to serve-then-refresh (background, coalesced);
  `ensure_fresh=true` blocks; `ensure_fresh=false` does zero refresh work. Cross-workspace asks:
  stay in the current session, `workspace list`, pass the selector; `workspace_id=all` is only for
  `content search` text audits.
- The dashboard reads the registry, telemetry, and read-only aggregate facts; it may perform
  registry-lifecycle mutations through its antiforgery-protected POST endpoints (ADR-0002). It must
  not hydrate full indexes for list/detail views.
- **A workspace leaving the family store takes its sidecars with it:** `workspace remove`/`prune`
  reclaim the whole per-view family from the shared store via `StoreSidecarReclaim` (view id
  captured from `store_members` BEFORE the delete; never rediscover by listing or re-hashing; never
  touch a view a survivor claims; unfinished reclaims write a `.reclaim-owed` record and are owed,
  never dropped).
- **Hash split:** `workspace_id` = SHA-256 of the root; file freshness = `files.content_hash`
  (blake3), guarded by `artifact_metadata.hash_algorithm`.
- **Telemetry:** shared ledger at `~/.miller/telemetry.db` (`tool_telemetry`); export via
  `miller telemetry export --jsonl --workspace-id all`. The export is type-only (privacy), but the
  local DB stores full `error_message`/`error_detail` — read those before declaring a field error
  opaque (the 2026-08-27 audit's 107 "opaque" edit errors were one already-fixed cause).
- **Search sidecar:** symbol search serves from the Miller-owned FTS5 artifact
  `<workspace>/.miller/search.db`, written by the lock-holding writer, read-only elsewhere. On by
  default; `MILLER_SEARCH_SIDECAR=0` opts into the in-memory BM25 fallback. Ranking stays in C#
  (`Miller.Core.Search.Bm25`); FTS5 is recall-only. `search.db` stays lexical-only — the semantic
  arm lives in `vectors.db` and is fused after ranking (ADR-0003). Design:
  [`docs/plans/2026-06-04-symbol-search-collapsed-trigram-design.md`](docs/plans/2026-06-04-symbol-search-collapsed-trigram-design.md).
- **Content corpus:** file/content text search serves from `<workspace>/.miller/content.db` plus
  explicit imports. Keep symbol search narrow (`name + signature`); route by intent
  (`mode=content|source|external|web|all-text`, `regions=` for comments/strings).
- **Patterns:** the `patterns` surface reads `structural_facts` from julie-extractors. Miller
  lists/groups/filters generic `pattern_id` facts; it must not own parser recognition. New fact
  shapes go into julie-extractors across all languages first.
- **Web research:** fetching stays in the `miller-web-research` skill layer; Miller imports fetched
  markdown as `web` content.

## Continuous testing (CT)

Authoritative operating doc: [docs/continuous-testing.md](docs/continuous-testing.md). JSON
contract: [`docs/contracts/tests-cli-v1.md`](docs/contracts/tests-cli-v1.md). Rules that must not
erode:

- CT verdicts live in the self-contained revision-keyed sidecar `<workspace>/.miller/ct.db` (no
  foreign keys into other artifacts). Providers write only under supervised CT paths — never
  workspace `bin`/`obj`. The default build root is workspace-local and flat
  (`.miller/ct-<proj12>`, deepest assembly dir five levels below the root); peer-root scans
  recognize a build root only by its `ct-` prefix; over-long roots fall back to the legacy temp
  shape.
- Freshness is the composite `(IndexGenerationIdentity, revision)` from the live snapshot; an
  identity change makes every result stale (the rebuild fail-safe). Within one identity, a revision
  advance is a watermark keep-set (staleness first, in one transaction): fresh unreachable greens
  carry forward; impacted cases go stale; red/skipped never advance; unknown reachability reads
  stale.
- An impacted RED keeps its state and committed key on every staling path; a case selected but
  never reported restores red with an owed stamp at run commit. A stamped red is owed (backlog +
  drain execute it); an unstamped live-key red is trimmed from automatic selections — no automatic
  red loop.
- An EXPLICIT run adds every red case (the trim's red exception short-circuits the freshness test);
  skipped stays skipped. Automatic runs execute only impacted ∪ owed.
- Truncated/degraded/unavailable impact = Unknown: everything stale, nothing executes, never a
  whole-suite fallback. A truncated read resolves through the selector to Unknown and the cursor
  advances; Unavailable is reserved for genuinely unreadable deltas and pauses auto-runs — a
  first-class status fact (`daemon.auto_runs_paused` + `pause_reason`).
- Two bounded exceptions to "Unknown executes nothing": the once-per-project inventory seed
  (discovery-owed pending on a store with no cases) and the idle drain (`CtIdleDrainPolicy`: one
  drain of the owed stale set once the workspace settles, 5-minute cooldown, explicit-run
  selection rules).
- Auto-runs debounce trailing-edge (`MILLER_CT_DEBOUNCE`, default 2s); changes during a run queue a
  follow-up. `selected` is the live index key; no readable index means `selected: null`.
- **Safety:** opt-in per workspace (`.miller/ct.enabled`), default off; worktrees inherit the main
  checkout's opt-in; `.miller/ct.disabled` beats inheritance; `MILLER_CT=off` beats everything
  (permanent zero-work guarantee). One family daemon adopts registered opted-in worktrees. Explicit
  start only; status reads never create files or start the daemon. `tests disable` retires a
  project's cases from every read without deleting rows.
- A status read on a workspace that never DECIDED runs the project-inventory scan (write-nothing)
  so `projects: 0` means "none exist", not "nobody looked". An opt-out tombstone or any recorded
  row is a decision — do not re-list what someone turned off.
- An enable that can never work is REFUSED (exit 3, writes nothing) — no supported toolchain, or
  xUnit v2-only (`ContinuousTestFrameworkSupport` classifies from package ids; the two shared ids
  prove nothing; mixed repos enable the supported projects and report the rest). Reversing an
  opt-out tombstone is always allowed.
- The daemon runs from a private per-build shadow copy
  ([`CtDaemonShadowCopy`](src/Miller.Testing/Daemon/CtDaemonShadowCopy.cs)); the key is a digest of
  the whole copied file set. A record about a root the process is leaving may only REPLACE an
  existing file (`CtDaemonWriteMode.ReplaceExistingOnly`) — attach creates, detach/stop/lease/log
  do not.
- Loop-stall detection is report-only (`loop_tick_at_utc` + daemon-computed `loop_age_seconds`;
  lag over `MILLER_CT_LOOP_STALL_TIMEOUT`, default 90s, while idle/queued = wedge). Miller reports
  and never kills. A silent test process (10 min, `MILLER_CT_STALL_TIMEOUT`) is killed and the run
  failed — the bound is on silence, not duration. One workspace executes tests at a time
  (user-global budget).
- Daemon build awareness: the lease records `miller_version`; explicit start from a different build
  replaces the old daemon (numeric version ordering, never replace newer). Core:
  [`CtDaemonVersion`](src/Miller.Testing/Daemon/CtDaemonVersion.cs).
- The MCP `tests` tool (approved 2026-08-18): `status|failures|start|stop|enable|disable|run`.
  Status is cheap; start is the only spawn.
- The dashboard Tests panel is a projection of `TestsCore` results (no second reading of ct.db),
  reads one registry row, and polls only while enabled and readable.

## Dashboard

- htmx plus ONE plain-JS file (`wwwroot/js/dashboard-site.js`); Razor Components is a static-SSR
  template engine only. No Alpine, no second framework, no interactive render mode, no Blazor
  circuit. `idiomorph-ext.min.js` loads before the site glue (the `morph` swap preserves
  scroll/focus/open state). State that must outlive a poll lives at module scope, never on a DOM
  node. Guards: `Dashboard_ShipsNoAlpineRuntimeOrDirective`,
  `DashboardSite_OwnsTheSortAndFilterControllers`, `DashboardScripts_LoadsIdiomorphBeforeTheSiteGlue`.
  Audit: [`docs/plans/2026-08-26-dashboard-interaction-cleanup.md`](docs/plans/2026-08-26-dashboard-interaction-cleanup.md).
- Dashboard launch requests: use the Miller `workspace` tool with `operation=dashboard`. Never
  search plugin cache directories for dashboard files.

## Guidance delivery channels

- Three channels with distinct jobs: the embedded
  [`MILLER_AGENT_INSTRUCTIONS.md`](src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md) core is the
  discovery contract (≤1,900 chars — clients truncate `ServerInstructions` silently); tool
  `[Description]`s are the usage contracts (budgets gated in `AgentInstructionsTests`); one-line
  `NextStepHint` nudges are compact-only. Do not grow the budgets without reading
  [ADR-0001](docs/adr/ADR-0001-guidance-delivery-channels.md).
- Empty-result cross-tool handoffs come from the one decision table
  [`CrossToolHandoff`](src/Miller.Server/Tools/CrossToolHandoff.cs) and must be
  `ToolDiagnosticAction.CompactOnly` (JSON `next_actions` stays byte-identical). A handoff must be
  honest about the input shape — a filter miss names the filter, not another mode. Gates:
  `CrossToolHandoffTests`.

## AGENTS.md is generated

`AGENTS.md` mirrors this file byte-for-byte. Edit `CLAUDE.md` only, then run
`scripts/sync-agents.sh` or `scripts/sync-agents.ps1`. The pre-commit hook (installed via
`scripts/install-hooks.sh`, `core.hooksPath=.githooks`) fails the commit if they diverge.
