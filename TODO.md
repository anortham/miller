1. [Eros integration] Keep Miller's public CLI/export contracts ahead of Eros needs so Eros does not reach for private Miller internals or duplicate Miller behavior.
   - 2026-06-07 completed: `miller capabilities --json`, `miller refresh --json --wait`, `miller telemetry export --jsonl`, existing `workspace status --json`, `content export`, and `impact --json` are documented in `docs/contracts/cli-eros-v1.md`.
   - 2026-06-09 completed: `trace --json` now exposes versioned auto/path/bridge trace data for Eros via `docs/contracts/trace-json-v1.md` and `capabilities --json`.
   - Remaining: add or harden Miller CLI/export surfaces only when a concrete Eros workflow needs stable code facts or operations that the documented contracts do not cover.
   - 2026-06-11 contract ask (Eros H1 fleet projections, `~/source/eros/docs/plans/2026-06-11-eros-fleet-intelligence-design.md`): bulk symbol export, e.g. `miller symbols export --jsonl [--workspace-id ID]` emitting one row per symbol (id, name, kind, language, path, span, signature, has_doc, body_hash) so Eros can build per-repo symbol rollups (counts, kinds, doc coverage) without per-file `inspect` fan-out or private SQLite reads.
     - 2026-06-11 completed (same day, ships in 0.4.1): `miller symbols export --jsonl [--workspace-id SELECTOR] [--workspace DIR]` emits one deterministic JSONL row per symbol (`schema_version` 1: id, name, kind, language, path, line+byte span, visibility, parent_symbol_id, signature, has_doc, body_hash, is_test), advertised in `capabilities --json` `supported_export_formats` and documented in `docs/contracts/cli-eros-v1.md`. Schema-mismatched artifacts exit 3.
   - 2026-06-11 contract ask (same Eros workflow): complexity-metrics export. julie-extract v2.x already extracts `complexity_metrics` (consumed at `workspace health` aggregate level only); expose per-symbol/per-file rows via a versioned export, e.g. `miller complexity export --jsonl` or a `--kind complexity` feed, so Eros can rank hotspots fleet-wide.
     - 2026-06-11 completed (same day, ships in 0.4.1): `miller complexity export --jsonl [--workspace-id SELECTOR] [--workspace DIR]` emits one deterministic JSONL row per `complexity_metrics` row (file- and symbol-scope; `schema_version` 1), advertised in `capabilities --json` and documented in `docs/contracts/cli-eros-v1.md`.
2. [search follow-up] 2026-06-08 completed for next release: starter runner cases are Miller-native and scoped, the stale copied Julie `WorkspacePool` row is kept as historical/unit regression coverage instead of a live benchmark case, and `mode=file --json` intentionally keeps returning normal symbol rows from matching files for compatibility. No versioned file-result JSON object is needed for this release; compact file mode is the file-first human surface, while `mode=content|source|all-text` provide path/line/snippet text hits. Evidence: `.miller/eval/search-quality/runs/20260608T130009Z.json` (`--tag file`, 3/3 top1) and `.miller/eval/search-quality/runs/20260608T130023Z.json` (Miller-only maintained local cases, 7/7 top1); see `docs/findings/2026-06-08-post-content-corpus-search-quality.md`.
3. [product follow-up] 2026-06-08 triaged for this release from `~/source/julie/TODO.md`: self-improvement/searchability scoring remains a future Miller product idea.
   - 2026-06-09 upstream complete: `julie-extractors` v2.2.0 exposes parser-backed `structural_facts`, `complexity_metrics`, and clone-ready normalized `symbols.body_hash` semantics under SQLite/report contract v3.
   - 2026-06-09 Miller consume-first slice complete: Miller pins `julie-extract` v2.2.0 / schema 3 and reports structural/complexity fact availability through `workspace health --json`.
   - Remaining: design Miller-native query/ranking surfaces only after a concrete agent or Eros workflow needs them. Likely future slices are structural-fact search/filtering, complexity report/ranking with Miller-owned thresholds, and body-hash duplicate/clone discovery. No new feature from this list is release-blocking.
4. [site follow-up] 2026-06-08 completed: GitHub Pages now shows real token-savings metrics from six cloned open-source repos under `~/source` (Flask, Express, Zod, Newtonsoft.Json, Gson, jq). Evidence and the repeatable measurement command live in `docs/findings/2026-06-08-site-token-savings.md`; the aggregate site claim is 97.1% less context and about 75K estimated tokens avoided for compact file-orientation workflows.
5. [cross-workspace access] 2026-06-08 completed/documented: a session in workspace_A stays put, runs `workspace list`, and routes code read tools to workspace_B by `workspace_id` selector (display ID, unique prefix, full ID, registered root path). If workspace_B is not registered, use MCP `workspace operation=open path=/absolute/repo` or CLI `miller workspace open --path /absolute/repo --full`, then retry the read. `workspace_id=all` is limited to `content search` text audits across registered workspace content DBs; symbol `search`, `inspect`, `context`, `impact`, and `trace` target one workspace per call.
6. [lock follow-up] 2026-06-08 completed: `miller workspace full` now writes a Windows-safe full-scan request under `.miller/requests/` when a healthy MCP/server process already owns `.miller/indexer.lock`; the lock-holding leader drains that request under its existing `_opsGate`, runs `Scan(force: true)`, and the CLI polls the readable revision/sidecars instead of requiring users to kill tasks. Remaining caveat: the currently running leader must be on a build that includes this request drain.
7. [watcher follow-up] 2026-06-08 completed: the live `FileSystemWatcher` path now applies Miller's built-in segment filter plus `.gitignore` and `.julieignore` before dispatching per-file extract ops. Ignore matching includes git-root `.gitignore` inheritance for nested workspace roots; `.gitignore`/`.julieignore` changes under the workspace force a delta scan; ancestor `.gitignore` files outside a nested workspace root get dedicated watchers that also force a scan.

----------------------------------------------------------------
taken from a session on windows, is this the best ux?

• Called
  └ miller.inspect({"target":"WorkspaceTool.ResolveTarget","workspace_id":"current","depth":"full","format":"compact","scope":"src/Miller.Server/Tools/WorkspaceTool.cs"})
    workspace: miller-6662d0bd90fe
    'WorkspaceTool.ResolveTarget' not found. Try search to locate it.

• Called
  └ miller.inspect({"target":"WorkspaceTool.FindByCanonicalRoot","workspace_id":"current","depth":"full","format":"compact","scope":"src/Miller.Server/Tools/
        WorkspaceTool.cs"})
    'CliDispatch.WorkspaceOpen' not found. Try search to locate it.

• Called miller.inspect({"target":"CliDispatch.RootMatches","workspace_id":"current","depth":"full","format":"compact","scope":"src/Miller.Server/Cli/CliDispatch.cs"})
  └ workspace: miller-6662d0bd90fe
    'CliDispatch.RootMatches' not found. Try search to locate it.

• Called miller.inspect({"target":"DashboardData.SameRoot","workspace_id":"current","depth":"full","format":"compact","scope":"src/Miller.Dashboard/DashboardData.cs"})
  └ workspace: miller-6662d0bd90fe
    'DashboardData.SameRoot' not found. Try search to locate it.

    10:52:54.112 [ERR] (role:leader pid:3856 cid:) ModelContextProtocol.Server.McpServer: "content" threw an unhandled exception.
System.ArgumentException: The arguments dictionary is missing a value for the required parameter 'operation'. (Parameter 'arguments')
   at Microsoft.Shared.Diagnostics.Throw.ArgumentException(String, String) + 0x32
   at Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunctionDescriptor.<>c__DisplayClass40_0.<GetParameterMarshaller>b__3(AIFunctionArguments, CancellationToken) + 0x129
   at Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunction.<InvokeCoreAsync>d__28.MoveNext() + 0x254
--- End of stack trace from previous location ---
   at Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunction.<InvokeCoreAsync>d__28.MoveNext() + 0x85f
--- End of stack trace from previous location ---
   at ModelContextProtocol.Server.AIFunctionMcpServerTool.<InvokeAsync>d__17.MoveNext() + 0x2e7
--- End of stack trace from previous location ---
   at ModelContextProtocol.Server.McpServerImpl.<>c__DisplayClass51_1.<<ConfigureTools>b__4>d.MoveNext() + 0x380
--- End of stack trace from previous location ---
   at Miller.Server.Telemetry.TelemetryCallToolFilter.<>c__DisplayClass1_0.<<Create>b__1>d.MoveNext() + 0x758
--- End of stack trace from previous location ---
   at ModelContextProtocol.Server.McpServerImpl.<>c__DisplayClass51_2.<<ConfigureTools>b__5>d.MoveNext() + 0x1d1
10:52:54.113 [INF] (role:leader pid:3856 cid:) ModelContextProtocol.Server.McpServer: Server (miller 0.3.1+9fc99ed3a3e2), Client (claude-code 2.1.168) method 'tools/call' request handler completed in 1.8912ms.
10:52:55.061 [INF] (role:leader pid:3856 cid:) ModelContextProtocol.Server.McpServer: Server (miller 0.3.1+9fc99ed3a3e2), Client (claude-code 2.1.168) method 'tools/call' request handler called.
10:52:55.061 [ERR] (role:leader pid:3856 cid:) ModelContextProtocol.Server.McpServer: "content" threw an unhandled exception.
System.ArgumentException: The arguments dictionary is missing a value for the required parameter 'operation'. (Parameter 'arguments')
   at Microsoft.Shared.Diagnostics.Throw.ArgumentException(String, String) + 0x32
   at Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunctionDescriptor.<>c__DisplayClass40_0.<GetParameterMarshaller>b__3(AIFunctionArguments, CancellationToken) + 0x129
   at Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunction.<InvokeCoreAsync>d__28.MoveNext() + 0x254
--- End of stack trace from previous location ---
   at Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunction.<InvokeCoreAsync>d__28.MoveNext() + 0x85f
--- End of stack trace from previous location ---
   at ModelContextProtocol.Server.AIFunctionMcpServerTool.<InvokeAsync>d__17.MoveNext() + 0x2e7
--- End of stack trace from previous location ---
   at ModelContextProtocol.Server.McpServerImpl.<>c__DisplayClass51_1.<<ConfigureTools>b__4>d.MoveNext() + 0x380
--- End of stack trace from previous location ---
   at Miller.Server.Telemetry.TelemetryCallToolFilter.<>c__DisplayClass1_0.<<Create>b__1>d.MoveNext() + 0x758
--- End of stack trace from previous location ---
   at ModelContextProtocol.Server.McpServerImpl.<>c__DisplayClass51_2.<<ConfigureTools>b__5>d.MoveNext() + 0x1d1
10:52:55.061 [INF] (role:leader pid:3856 cid:) ModelContextProtocol.Server.McpServer: Server (miller 0.3.1+9fc99ed3a3e2), Client (claude-code 2.1.168) method 'tools/call' request handler completed in 0.697ms.
10:52:55.651 [INF] (role:leader pid:3856 cid:) ModelContextProtocol.Server.McpServer: Server (miller 0.3.1+9fc99ed3a3e2), Client (claude-code 2.1.168) method 'tools/call' request handler called.
10:52:55.651 [ERR] (role:leader pid:3856 cid:) ModelContextProtocol.Server.McpServer: "content" threw an unhandled exception.
System.ArgumentException: The arguments dictionary is missing a value for the required parameter 'operation'. (Parameter 'arguments')
   at Microsoft.Shared.Diagnostics.Throw.ArgumentException(String, String) + 0x32
   at Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunctionDescriptor.<>c__DisplayClass40_0.<GetParameterMarshaller>b__3(AIFunctionArguments, CancellationToken) + 0x129
   at Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunction.<InvokeCoreAsync>d__28.MoveNext() + 0x254
--- End of stack trace from previous location ---
   at Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunction.<InvokeCoreAsync>d__28.MoveNext() + 0x85f
--- End of stack trace from previous location ---
   at ModelContextProtocol.Server.AIFunctionMcpServerTool.<InvokeAsync>d__17.MoveNext() + 0x2e7
--- End of stack trace from previous location ---
   at ModelContextProtocol.Server.McpServerImpl.<>c__DisplayClass51_1.<<ConfigureTools>b__4>d.MoveNext() + 0x380
--- End of stack trace from previous location ---
   at Miller.Server.Telemetry.TelemetryCallToolFilter.<>c__DisplayClass1_0.<<Create>b__1>d.MoveNext() + 0x758
--- End of stack trace from previous location ---
   at ModelContextProtocol.Server.McpServerImpl.<>c__DisplayClass51_2.<<ConfigureTools>b__5>d.MoveNext() + 0x1d1
10:52:55.651 [INF] (role:leader pid:3856 cid:) ModelContextProtocol.Server.McpServer: Server (miller 0.3.1+9fc99ed3a3e2), Client (claude-code 2.1.168) method 'tools/call' request handler completed in 0.5992ms.


  content requires an operation arg (read / search / list), but the MCP schema exposed to the client doesn't mark it required — so omitting it bypasses client validation
  and throws an unhandled exception server-side instead of a clean "missing parameter" message. Once I passed operation, it behaved correctly:
  - operation=read → "content read requires source_id" (clean handled error)
  - operation=search / list → "No results" / "No imported content"

  Fix plan/status:
  - [x] 2026-06-08 completed: Content tool UX now defaults omitted `operation` to `list` at the MCP callable signature, so `content({})` returns the normal compact list/no-content response instead of throwing before Miller's handler runs. Pinned by `ContentToolTests.Content_McpCallWithNoArguments_DefaultsToListInsteadOfThrowing`.
  - [x] 2026-06-08 completed: `SmartTargetResolver` now resolves `Parent.Member`/`Class.Member` targets through the same caller-facing path used by `inspect`, `trace`, `impact`, and edit targeting. Pinned by `SmartTargetResolverTests.Resolve_ClassQualifiedMember_ResolvesThroughParentNameAndScope`; live CLI sanity checks resolved `WorkspaceTool.ResolveTarget`, `CliDispatch.RootMatches`, and `DashboardData.SameRoot`.

----------------------------------------------------------------
Cursor plugin import failure from Claude plugin cache:

Cursor auto-discovered Miller as `plugin-miller-miller` from the Claude plugin install, but the server stayed
`connected=false, statusType=error`. Local reproduction showed the Claude manifest's MCP args depend on
`${CLAUDE_PLUGIN_ROOT}`; when a non-Claude client leaves that placeholder unexpanded, Node tries to load a literal
`${CLAUDE_PLUGIN_ROOT}/bin/miller-plugin-launcher.cjs` path and exits before Miller starts.

Fix plan/status:
- [x] 2026-06-08 completed: add a first-class `.cursor-plugin/plugin.json` with a Cursor-safe relative launcher
  (`node ./bin/miller-plugin-launcher.cjs`, `cwd: "."`) instead of relying on Cursor's Claude-plugin fallback.
  Pinned by `plugin-manifest.test.cjs`; docs now list `.cursor-plugin/plugin.json` as a release-sync manifest.

----------------------------------------------------------------
0.3.2 release prep:

Fix plan/status:
- [x] 2026-06-08 completed: bumped the active Miller build/package version to `0.3.2` in
  `Directory.Build.props`, `miller-plugin.json`, Claude Code/Cursor/Codex plugin manifests, and Claude marketplace
  metadata. Release notes added at `docs/release-notes/v0.3.2.md`.
- [x] 2026-06-08 completed: strengthened `plugin-manifest.test.cjs` so future version bumps must keep
  `Directory.Build.props`, `miller-plugin.json`, plugin manifests, and Claude marketplace metadata aligned.
- [x] 2026-06-08 completed: package-only run `27152945415` was not promoted. ARM macOS returned a valid
  `tools/list` response, but the workflow assertion used `echo "$out" | grep -q` under `set -o pipefail`, so
  `grep -q` closed early and `echo` failed with SIGPIPE. Fixed the release workflow to use Bash string matching
  and pinned the guard in `MillerExtractContractTests.ReleaseWorkflowPublishesVerifiablePrereleasePackages`.
- [x] 2026-06-08 completed: `v0.3.2` was published from validated package-only run `27153317515` through
  promotion run `27153759953`; README/site/docs current-release links now point at live `v0.3.2` facts.
- [x] 2026-06-11 completed (Eros ask, cli-eros-v1 lock_busy exit code): chose option 2 — `lock_busy` now
  exits 0 for `refresh`/`workspace refresh|full|open` (the latest readable DB is served and a live leader
  owns convergence; the payload's `status`/`index_fresh: false` are the freshness gate). Exit 3 is reserved
  for genuinely unusable-index outcomes (`missing_root`, `missing_index`, `failed`,
  `ineligible_extractor`). `CliDispatch.RefreshExitCode` + the contract doc exit-code table updated;
  pinned by `RefreshExitCode_MapsStatusesToContractExitCodes`.
- [x] 2026-06-11 completed (Eros field report, "julie-extract hang" on openclaw): triaged — NOT a per-file
  julie-extract hang. An isolated force scan of `/Users/murphy/source/openclaw` with the pinned 2.4.0
  binary completes in ~90s (status ok, 13,308 files, 660,851 symbols, ~2.0GB artifact; no minimized repro
  needed). Reconstructed timeline (registry stamps + Eros checkpoint 4019e1af): Eros's fleet sync
  auto-rebuilt 11 schema-2 workspaces serially ending 22:14:00Z, then openclaw's force scan started
  (artifact created 22:15:06Z), was killed by `JulieExtractRunner`'s fixed 600s TOTAL timeout at exactly
  22:25:06Z, and Eros's pipeline retry then refreshed over the partial artifact in ~90s (scan-stamp
  22:27:49Z) — so the scan was slow under the sweep's ingest/embedding churn, not hung (a deterministic
  hang would have hung the retry too). Fixed by making the bounded wait progress-aware
  (`ExtractWaitPolicy`): the child is killed only after 10 minutes with NO artifact/output progress
  (db/-wal/-shm bytes + output activity), with a 60-minute absolute backstop; the two kill messages now
  distinguish "no progress (likely hang)" from "progressing but over the hard cap (load)". On question
  (2): `workspace full --json` already exits 3 on `status: failed` at HEAD (verified empirically on
  0.4.1+86c7529 via an injected sensitive-root registry row; exit-0 did not reproduce) — Eros likely hit a
  pre-0.4.1 binary.
- [x] 2026-06-11 Eros field report #2 (openclaw `workspace full` — root cause profile): the 0.4.2
  progress-aware wait kept the rebuild alive 50+ min, but it could never finish. A 3s `sample` of the
  julie-extract scan at ~98% CPU shows it is NOT extracting (the scan spool .jsonl is complete) — it is
  in the DB merge phase doing random indexed READS against the existing 2.0GB symbols.db: top-of-stack
  pread (1257 samples), walFindFrame (388), sqlite3BtreeNext/btreeParseCellPtr/vdbeCompareMemString,
  pcache1Fetch thrash. The WAL was 380MB and could not checkpoint because the running `miller serve`
  processes hold read snapshots; effective write throughput was ~7KB/s (days to complete). This explains
  the 90s-isolated vs 50min-live gap in the earlier triage: isolated = bulk insert into a FRESH db;
  live = in-place merge into the served 2GB artifact under readers, with per-page walFindFrame overhead
  growing as the WAL grows. Eros killed the doomed scan after capturing the profile; the raw profile is
  preserved at docs/investigation-jx-openclaw-sample.txt.
  FIXED 2026-06-12 (build-to-temp + atomic promote, plus a freshness gap the report could not see):
  - `JulieExtractRunner.Scan(force:true)` now extracts into `symbols.db.rebuild` and
    `FullRebuildPromotion.Promote` replaces the live artifact (folds any temp WAL, removes the stale live
    -wal/-shm, overwrite-move with bounded Windows retry). A failed force scan leaves the live artifact
    untouched. Escape hatch: `MILLER_FULL_REBUILD_INPLACE=1`. Covers every force path through the single
    scan chokepoint (leader full scan, extractor-upgrade rescan, cross-workspace `workspace full`,
    bootstrap auto-rebuild).
  - The promote makes rebuilds restart julie's revision counter, which the serve freshness pipeline could
    not see: `FreshnessService` now opens a TRANSIENT reader per poll (a long-lived connection would
    freeze on the old unlinked inode) and `FreshnessPoller`/`IndexHolder` track
    `artifact_metadata.artifact_id`, swapping when the identity changes even when the revision went
    backwards or tied. `CrossWorkspaceRefreshService.WaitForExternalRevision` (lock-busy `workspace full`
    routed to a live leader) confirms via the same artifact-id arm. Registered-workspace caches were
    already rebuild-safe (file-stamp keys, b54510e).
  - Residual (accepted): sidecar `EnsureBuilt` skips when the rebuilt artifact's revision EXACTLY ties the
    sidecar's stamped revision (same source ⟹ same content-derived symbols, so the artifact stays valid);
    predates this change via julie's incompatible-heal delete+recreate.
  - Verified: fast suite 2178 green, scale 28 green (new `FullRebuildPromotionTests`,
    `FullRebuildScanScaleTests`, artifact-id poller/PollNow/cross-workspace cases), plus a live re-run of
    the failing openclaw `workspace full` with the fixed binary.

----------------------------------------------------------------
Architecture-quality follow-up from 2026-06-14 review:

Fix plan/status:
- [x] Shared search command routing: MCP `SearchTool` and CLI `CliDispatch.Search` both own mode routing
  for regions/content/source/external/web/symbol search. Refactor toward one internal command core while
  keeping MCP telemetry and CLI stdout/exit-code behavior as adapters.
  - 2026-06-14 started: extracted `SearchRoutePlanner` so the shared mode/regions route decision now has
    one owner; MCP and CLI adapters still own their provider/sidecar opening, telemetry, stdout, and exit
    codes. Verified by focused route-planner tests plus existing MCP/CLI search tests.
  - 2026-06-14 completed: added `SearchRouteExecutor` so route kinds map to the shared `SearchTool.Run*`
    execution/rendering policy in one place, including content kinds, source bytes, test hiding, filters, and
    region mode notes. MCP and CLI now keep only adapter-specific provider/sidecar opening, telemetry, stdout,
    and exit-code behavior. Verified by focused executor tests plus existing MCP/CLI search tests.
- [x] Shared graph traversal core: `SymbolGraph` and `SqliteSymbolGraphIndex` duplicate breadth-first
  reachability and shortest-path algorithms; only node existence and neighbor lookup differ. Start here
  because it is low-risk and has tight existing graph/SQLite parity tests.
  - 2026-06-14 completed: added internal `GraphTraversal` in `Miller.Core` and routed both in-memory and
    SQLite-backed graph implementations through it without changing `ISymbolGraphReachability`. Verified by
    focused `GraphTraversalTests` and existing graph/SQLite parity tests.
- [x] Indexer host orchestration split: `IndexerService` still mixes leadership session, yield handling,
  watcher topology, sidecar convergence, registry marking, and many test hooks. `IndexerCore` already earns
  its keep; look for the next internal module boundary before adding more policy to the hosted service.
  - 2026-06-14 started: extracted `SidecarCorruptionRecovery` so derived-artifact corruption detection,
    delete-and-rebuild retry, and logging live outside `IndexerService`. Existing sidecar convergence still lives
    in the hosted service; next slice should target either a sidecar converger or registry-publishing adapter.
    Verified by focused recovery and indexer sidecar tests.
  - 2026-06-14 continued: added `IndexerSidecarConverger` so content/search sidecar path derivation,
    full-vs-incremental convergence, corruption recovery, and sidecar logging live behind one internal hosting
    interface. `IndexerService` now only supplies current-workspace inputs and keeps registry revision marking.
    Remaining split candidates are registry publishing, leadership/yield handling, watcher topology, and test
    hooks. Verified by focused converger/recovery/indexer sidecar tests.
  - 2026-06-14 continued: added `WorkspaceRegistryScanPublisher` so required startup scan stamps and best-effort
    convergence scan stamps have one registry-facing adapter. This preserves the existing behavior split:
    startup stamp failures still flow through the startup-scan failure path, while convergence stamp failures log
    and leave status views potentially stale. Remaining split candidates are leadership/yield handling, watcher
    topology, and test hooks. Verified by focused publisher, bootstrap, and indexer scan tests.
  - 2026-06-14 continued: added `IndexerWatcherSet` so `FileSystemWatcher` construction, HEAD watcher setup,
    ancestor `.gitignore` watcher setup, event subscription, and disposal live behind one lifecycle container.
    Event routing still stays in `IndexerService` for now. Remaining split candidates are leadership/yield
    handling and the broad test-hook surface. Verified by focused watcher-set, watcher gate/filter, and indexer
    scan tests.
  - 2026-06-14 completed: added `IndexerLeadershipCoordinator` so extractor-version probing, claim eligibility,
    ineligibility log throttling, post-yield cooldown, yield-request deduplication, and leader-side yield
    decisions live outside `IndexerService`. `IndexerService` still owns the actual leader lifecycle resources
    (`_lease`, `_ops`, `_core`, watchers, role state) and its remaining test hooks are adapter seams for those
    service-owned resources rather than policy owners. Verified by focused coordinator and service leadership
    tests.
- [x] Shared workspace facts assembly: MCP `WorkspaceTool` and CLI `CliDispatch.Workspace*` share rendering
  but still separately parse selector/status behavior and assemble workspace facts. Refactor toward a shared
  pure facts/query layer while preserving CLI usage errors and MCP telemetry behavior.
  - 2026-06-14 started: added `WorkspaceFactsAssembler` for registered-workspace status facts and registry list
    entries. It preserves the adapter split explicitly: CLI status remains read-only on missing indexes, while
    MCP status marks the registry row missing and reports typed `missing_index`. Verified by focused assembler,
    workspace-tool, and CLI dispatch tests. Remaining duplicate facts paths are current-workspace fallback and
    health-specific status facts.
  - 2026-06-14 completed: extended `WorkspaceFactsAssembler` to own registered health facts, unreadable-index
    health facts, and CLI unregistered-local facts. `WorkspaceTool` and `CliDispatch` now keep selector parsing,
    telemetry, stdout, and health extraction adapters local while sharing facts assembly. The only remaining direct
    `WorkspaceFacts` construction is the live MCP current-workspace snapshot over runtime services, which is not a
    duplicated CLI/MCP path. Verified by focused workspace facts/tool/CLI tests.
