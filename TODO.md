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
