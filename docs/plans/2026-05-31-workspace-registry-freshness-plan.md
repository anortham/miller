# Workspace Registry and Freshness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Implement the approved workspace registry, cross-workspace freshness, stable workspace identity, BLAKE3 freshness, and dashboard discovery design.

**Architecture:** Keep symbol indices local at `<workspace>/.miller/symbols.db` and add a machine-global registry at `~/.miller/workspaces.db`. Read tools route through one provider seam that can serve the current live index or a registered workspace's local DB, refreshing the target first when `ensure_fresh=true`. Julie remains the extract/hash authority; Miller owns registry, routing, locks, telemetry attribution, and dashboard discovery.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, ModelContextProtocol SDK, Serilog, xUnit, Julie Rust `external_extract`, NuGet `Blake3` 2.2.1 behind a Miller wrapper.

**Architecture Quality:** Approved shape is a central metadata registry plus local per-workspace indices. Main risk is letting cross-workspace refresh turn into daemon-style hidden coordination; v1 avoids IPC and uses lock-based scans with explicit stale/lock-busy reporting.

---

## Source Of Truth

Read these first:

- Approved design: `docs/plans/2026-05-31-workspace-registry-freshness-design.md`
- Repo rules: `AGENTS.md` and `CLAUDE.md`
- Current single-workspace design: `docs/m7-design.md`
- Freshness design gap: `docs/m3-design.md`
- Julie contract facts: `docs/findings/julie-contract-verified.md`

No `RAZORBACK.md` exists in this repo. Model routing below uses inherited harness defaults.

## Implementation Shape

Registry code lives in `Miller.Indexing` because it is SQLite infrastructure shared by the MCP server and the
dashboard. It must not depend on MCP, hosted services, Serilog, or dashboard code.

Identity rule: Miller registry `workspace_id` and Julie `external_extract_metadata.workspace_id` converge on the
same stable full SHA-256 root hash. Do not keep dual long-term identities. Legacy UUID-backed DBs are repaired by the
Julie force-rebind path before they are considered registered and fresh.

### New Miller Files

- Create `src/Miller.Indexing/WorkspaceId.cs`
  - Stable SHA-256 full hex from canonical root.
  - Human `display_id` helper: sanitized leaf name plus short prefix.

- Create `src/Miller.Indexing/WorkspaceRegistry.cs`
  - SQLite owner for `~/.miller/workspaces.db`.
  - Owns schema creation, WAL pragmas, upsert, list, get, mark missing, mark error, remove.

- Create `src/Miller.Indexing/WorkspaceRegistryRow.cs`
  - Immutable row record plus `WorkspaceRegistryState` enum.

- Create `src/Miller.Indexing/ExtractFileHashReader.cs`
  - Reads `files.hash` for a relative path.
  - Reads `external_extract_metadata.hash_algorithm`.

- Create `src/Miller.Indexing/ContentHasher.cs`
  - Wraps NuGet `Blake3` behind `Blake3Hex(byte[] bytes)` and `Blake3FileHex(string path)`.
  - No tool or service calls package APIs directly.

- Create `src/Miller.Server/Workspaces/WorkspaceReadContext.cs`
  - Carries one fixed index snapshot, fixed resolver built over that same index, DB path, workspace id/root,
    revision, freshness status, and warning text.

- Create `src/Miller.Server/Workspaces/IWorkspaceIndexProvider.cs`
  - Resolves `workspace_id` + `ensure_fresh` into `WorkspaceReadContext`.

- Create `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
  - Current workspace returns live `IndexHolder`.
  - Registered workspace loads local DB read-only and caches by `(workspace_id, index_db_path, last_revision)`.

- Create `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
  - Lock-based target refresh for non-current workspaces.
  - Implements 2-second lock-busy wait with 100 ms polling.

- Create `src/Miller.Server/Workspaces/WorkspaceRefreshResult.cs`
  - Typed result: `refreshed`, `unchanged`, `lock_busy`, `missing_root`, `missing_index`, `failed`.

- Create `src/Miller.Dashboard/Miller.Dashboard.csproj`
  - Minimal local dashboard host using `Microsoft.NET.Sdk.Web`.
  - Read-only by default against `~/.miller/workspaces.db` and `~/.miller/telemetry.db`.

- Create `src/Miller.Dashboard/Program.cs`
  - Kestrel endpoints for workspaces and telemetry summary.
  - Refresh actions call the same lock-based refresh path only when explicitly invoked.

### Miller Files To Modify

- `Miller.slnx`
  - Add `src/Miller.Dashboard/Miller.Dashboard.csproj`.

- `src/Miller.Indexing/Miller.Indexing.csproj`
  - Add `PackageReference Include="Blake3" Version="2.2.1"`.

- `src/Miller.Indexing/JulieExtractRunner.cs`
  - Add `workspaceId` parameter to scan arg builder and live scan calls.
  - Emit `--workspace-id <stable-id>` for scans.

- `src/Miller.Indexing/ExtractReport.cs`
  - Add `HashAlgorithm`.
  - Preserve existing nullable parsing behavior.

- `src/Miller.Indexing/JulieSchemaGate.cs`
  - Gate on Julie extract contract 3.
  - Verify `hash_algorithm=blake3`.

- `src/Miller.Indexing/MillerExtractContract.cs`
  - Bump to Julie version that ships contract 3.
  - Keep schema 28 for this work.

- `src/Miller.Server/Hosting/WorkspaceContext.cs`
  - Add `RegistryDbPath`.
  - Keep telemetry DB at `~/.miller/telemetry.db`.

- `src/Miller.Server/Hosting/IndexBootstrapService.cs`
  - Compute stable workspace id before first scan.
  - Register current workspace during bootstrap.
  - Handle existing DB with UUID/mismatched workspace id by forcing a Julie-backed rebind/rebuild path.

- `src/Miller.Server/Hosting/IndexerService.cs`
  - Run startup delta scan when this process becomes leader.
  - Update registry after scan outcomes.

- `src/Miller.Server/Hosting/JulieExtractOps.cs` and `src/Miller.Server/Hosting/IExtractOps.cs`
  - Carry stable workspace id through scan operations.

- `src/Miller.Server/Hosting/FreshnessGate.cs`
  - Replace SHA-256 text comparison with BLAKE3 bytes-vs-`files.hash`.
  - Keep exact-text comparison only where already available and not as the required path.

- `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
  - Register registry, refresh service, provider, and dashboard-shared readers.
  - Preserve hosted-service lifecycle rule: no hosted-service constructor reads bootstrap getters.

- `src/Miller.Server/Telemetry/TelemetryScope.cs`
  - Add `SetWorkspace(string? workspaceId, string? workspaceRoot)`.

- `src/Miller.Server/Telemetry/TelemetryRecord.cs`
  - Add `WorkspaceRoot`.

- `src/Miller.Server/Telemetry/TelemetryLedger.cs`
  - Persist per-record workspace root when supplied; fall back to ledger root for current-workspace calls.

- `src/Miller.Server/Tools/SearchTool.cs`
  - Use `IWorkspaceIndexProvider`.
  - Add `workspace_id` and `ensure_fresh`.
  - Render cross-workspace freshness warning.

- `src/Miller.Server/Tools/InspectTool.cs`
  - Use provider context for index, resolver, and DB path.
  - Add `workspace_id` and `ensure_fresh`.

- `src/Miller.Server/Tools/ContextTool.cs`
  - Use provider context for index and resolver.
  - Add `workspace_id` and `ensure_fresh`.

- `src/Miller.Server/Tools/ImpactTool.cs`
  - Use provider context for index and resolver.
  - Add `workspace_id` and `ensure_fresh`.

- `src/Miller.Server/Tools/TraceTool.cs`
  - Use provider context for index and resolver.
  - Add `workspace_id` and `ensure_fresh`.

- `src/Miller.Server/Tools/WorkspaceTool.cs`
  - Use registry for `list`.
  - Allow `status`, `refresh`, `full`, and `remove` by `workspace_id` or `path`.
  - Keep current-workspace default behavior.

- `src/Miller.Server/Tools/WorkspaceRender.cs`
  - Render multi-row registry list.
  - Render target refresh state and lock-busy states.

- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
  - Document `workspace_id` and `ensure_fresh`.

- `CLAUDE.md`
  - Update generated-agent instructions when tool behavior changes, then run `scripts/sync-agents.sh`.

### Julie Files To Modify

- `/Users/murphy/source/julie/src/external_extract/metadata.rs`
  - Bump `EXTRACT_CONTRACT_VERSION` from 2 to 3.
  - Add required metadata key `hash_algorithm`.
  - Write `hash_algorithm=blake3`.
  - Allow `scan --force --workspace-id <id>` to rebind an existing external DB whose old workspace id was a generated UUID.

- `/Users/murphy/source/julie/src/external_extract/report.rs`
  - Add `hash_algorithm` to `ExternalExtractReport`.
  - Include it in JSON/text/markdown output.

- `/Users/murphy/source/julie/src/external_extract/operations.rs`
  - Fill `hash_algorithm` in scan/update/delete/info/failed reports.
  - Preserve no-op revision semantics.

- `/Users/murphy/source/julie/src/external_extract/info.rs`
  - Surface `hash_algorithm` from metadata.

- `/Users/murphy/source/julie/src/tests/external_extract/**`
  - Add contract tests for metadata, report field, force rebind, and raw-byte BLAKE3 hash semantics.

## Task 1: Julie Contract 3 For Hash Metadata And Stable Workspace Rebind

**Owner:** Julie worker.

**Files:**

- Modify: `/Users/murphy/source/julie/src/external_extract/metadata.rs`
- Modify: `/Users/murphy/source/julie/src/external_extract/report.rs`
- Modify: `/Users/murphy/source/julie/src/external_extract/operations.rs`
- Modify: `/Users/murphy/source/julie/src/external_extract/info.rs`
- Test: `/Users/murphy/source/julie/src/tests/external_extract/**`

**Build:**

- Add `hash_algorithm=blake3` as required external extract metadata.
- Add report field `hash_algorithm`.
- Bump extract contract to 3.
- Let `scan --force --workspace-id <stable-id>` repair an existing external DB that has a different workspace id.
- Keep non-force mismatch strict.
- Keep schema version 28 unless a migration is actually needed.

**Acceptance:**

- `extract scan --json` includes `"hash_algorithm": "blake3"`.
- `extract info --json` includes `"hash_algorithm": "blake3"`.
- Existing DB with mismatched workspace id fails non-force scan.
- Existing DB with mismatched workspace id succeeds under force scan and rewrites metadata to requested id.
- `files.hash` remains BLAKE3 over raw bytes.

**Verification:**

- Run Julie external extract tests covering this area.
- Build Julie release artifact or local binary for Miller scale tests.

## Task 2: Miller Contract Re-pin And Stable Workspace IDs

**Owner:** Miller indexing worker.

**Files:**

- Create: `src/Miller.Indexing/WorkspaceId.cs`
- Modify: `src/Miller.Indexing/JulieExtractRunner.cs`
- Modify: `src/Miller.Indexing/ExtractReport.cs`
- Modify: `src/Miller.Indexing/JulieSchemaGate.cs`
- Modify: `src/Miller.Indexing/MillerExtractContract.cs`
- Modify: `scripts/julie-pins.json`
- Test: `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs`
- Test: `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs`
- Test: `tests/Miller.Tests/Indexing/ExtractReportParsingTests.cs`

**Build:**

- `WorkspaceId.FromCanonicalRoot(root)` returns 64-char SHA-256 hex.
- `WorkspaceId.Display(root, id)` returns sanitized leaf plus 8-12 char prefix.
- `JulieExtractRunner.BuildScanArgs` accepts `workspaceId` and emits `--workspace-id`.
- All production scans pass the stable id.
- Miller gates on Julie extract contract 3 and `hash_algorithm=blake3`.

**Acceptance:**

- Same canonical root always produces same full id.
- Different worktrees produce different ids.
- Scan args include `--workspace-id <id>`.
- Missing/wrong `hash_algorithm` fails with an actionable incompatible-extract error.
- Old contract 2 DBs fail loudly and tell the user to restore/rescan with the pinned Julie.

**Verification:**

- `scripts/test.sh` after this task.
- `scripts/test.sh scale` only after the new Julie binary is restored.

## Task 3: Central Workspace Registry

**Owner:** Miller indexing worker. May run in parallel after Task 2 APIs are drafted.

**Files:**

- Create: `src/Miller.Indexing/WorkspaceRegistry.cs`
- Create: `src/Miller.Indexing/WorkspaceRegistryRow.cs`
- Modify: `src/Miller.Server/Hosting/WorkspaceContext.cs`
- Test: `tests/Miller.Tests/Indexing/WorkspaceRegistryTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceContextTests.cs`

**Build:**

- Add `RegistryDbPath = <home>/.miller/workspaces.db`.
- Registry schema:
  - `workspace_id TEXT PRIMARY KEY`
  - `display_id TEXT NOT NULL`
  - `canonical_root TEXT NOT NULL`
  - `index_db_path TEXT NOT NULL`
  - `last_seen_at TEXT NOT NULL`
  - `last_scan_at TEXT`
  - `last_revision INTEGER`
  - `state TEXT NOT NULL`
  - `last_error TEXT`
- Set WAL, `synchronous=NORMAL`, `busy_timeout=3000`.
- Upsert is idempotent and never rewrites one workspace row from another id.
- `List()` orders current/ready rows first, then by `display_id`.

**Acceptance:**

- Registry can be opened by multiple processes.
- `UpsertSeen`, `MarkScanned`, `MarkMissing`, `MarkError`, `Remove`, `Get`, and `List` are covered.
- Registry state strings are stable for dashboard use.

**Verification:**

- `scripts/test.sh`

## Task 4: Bootstrap Registration And Startup Delta Scan

**Owner:** Miller hosting worker. Depends on Tasks 2 and 3.

**Files:**

- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs`
- Modify: `src/Miller.Server/Hosting/IndexerService.cs`
- Modify: `src/Miller.Server/Hosting/JulieExtractOps.cs`
- Modify: `src/Miller.Server/Hosting/IExtractOps.cs`
- Test: `tests/Miller.Tests/Server/IndexBootstrapServiceTests.cs`
- Test: `tests/Miller.Tests/Server/IndexerServiceScanTests.cs`
- Scale Test: `tests/Miller.Tests/Server/LiveFreshnessTests.cs`

**Build:**

- Bootstrap computes canonical root and stable workspace id before scan.
- Missing DB path scans with stable workspace id.
- Existing DB path loads quickly, registers row, and records state `loaded_existing`.
- Leader startup runs `extract scan(force:false)` once after it wins the lock.
- After startup scan, refresh poll or explicit reload swaps in the updated index.
- Existing UUID-backed DBs are repaired by the Julie force-rebind path from Task 1, then reloaded.

**Acceptance:**

- A file edited while Miller is down is visible after startup scan converges.
- Status can distinguish loaded existing index from confirmed fresh index.
- Non-leader process does not run startup scan.
- Bootstrap registry row has stable id, root, DB path, and current revision when available.

**Verification:**

- `scripts/test.sh`
- `scripts/test.sh scale` because this touches live extract/startup convergence.

## Task 5: Cross-Workspace Refresh Service And Provider

**Owner:** Miller workspace worker. Depends on Tasks 2, 3, and 4.

**Files:**

- Create: `src/Miller.Server/Workspaces/WorkspaceReadContext.cs`
- Create: `src/Miller.Server/Workspaces/WorkspaceRefreshResult.cs`
- Create: `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
- Create: `src/Miller.Server/Workspaces/IWorkspaceIndexProvider.cs`
- Create: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Modify: `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`
- Test: `tests/Miller.Tests/Server/CrossWorkspaceRefreshServiceTests.cs`

**Build:**

- Current workspace resolution returns live `IndexHolder.Current`, live resolver, and current DB path.
- Current workspace resolution captures `IndexHolder.Snapshot()` exactly once and builds `new SmartTargetResolver(index)`
  over that captured index. Do not return `_holder.Current` plus the holder-backed singleton resolver; a concurrent
  swap could split the index and resolver.
- Registered target resolution:
  - finds registry row by `workspace_id`;
  - verifies root exists and is not sensitive;
  - refreshes first when `ensure_fresh=true`;
  - opens local DB read-only through `RepositoryIndexLoader.Load`;
  - builds a fixed `SmartTargetResolver` over that loaded index;
  - caches by `(workspace_id, index_db_path, last_revision)`.
- Refresh service:
  - acquires target `.miller/indexer.lock`;
  - runs Julie `extract scan(force:false)` with stable workspace id;
  - updates registry;
  - on busy lock waits 2 seconds, polling each 100 ms for a visible revision change;
  - returns `lock_busy` for admin refresh when still unconfirmed;
  - returns `unconfirmed_lock_busy` warning for read tools when serving latest readable DB.

**Acceptance:**

- Project A can refresh project B when B is unlocked.
- Project A never scans project B when B's lock is held.
- Missing root marks registry row `missing`.
- Missing DB is created by refresh when lock is available.
- Cache reloads after revision changes.
- Provider tests prove snapshot consistency across a forced holder swap.

**Verification:**

- `scripts/test.sh`
- `scripts/test.sh scale` for live cross-workspace refresh.

## Task 6: Telemetry Attribution For Target Workspaces

**Owner:** Miller telemetry worker. Can run after Task 5 API exists.

**Files:**

- Modify: `src/Miller.Server/Telemetry/TelemetryScope.cs`
- Modify: `src/Miller.Server/Telemetry/TelemetryRecord.cs`
- Modify: `src/Miller.Server/Telemetry/TelemetryLedger.cs`
- Test: `tests/Miller.Tests/Server/TelemetryLedgerTests.cs`
- Test: `tests/Miller.Tests/Server/CallToolFilterTelemetryTests.cs`

**Build:**

- Add `TelemetryScope.SetWorkspace(workspaceId, workspaceRoot)`.
- `TelemetryScope.Dispose` writes overridden workspace id/root when set.
- `TelemetryLedger.Record` uses record root when present, ledger root otherwise.
- Tool/provider code overrides `TelemetryScope.IndexFresh` with target-workspace freshness after routing; the central
  filter's process-local `IndexFreshProbe` remains only the default for current-workspace calls.
- Current-workspace calls preserve existing row values.

**Acceptance:**

- A cross-workspace search from project A records project B's workspace id/root.
- A cross-workspace search records target freshness, not project A's process-local freshness.
- Current-workspace telemetry tests still pass.
- Target query remains privacy-hashed with SHA-256.

**Verification:**

- `scripts/test.sh`

## Task 7: Read Tool Routing

**Owner:** Miller tool worker. Depends on Task 5 and Task 6.

**Files:**

- Modify: `src/Miller.Server/Tools/SearchTool.cs`
- Modify: `src/Miller.Server/Tools/InspectTool.cs`
- Modify: `src/Miller.Server/Tools/ContextTool.cs`
- Modify: `src/Miller.Server/Tools/ImpactTool.cs`
- Modify: `src/Miller.Server/Tools/TraceTool.cs`
- Test: `tests/Miller.Tests/Server/SearchToolTests.cs`
- Test: `tests/Miller.Tests/Server/InspectToolTests.cs`
- Test: `tests/Miller.Tests/Server/ContextToolTests.cs`
- Test: `tests/Miller.Tests/Server/ImpactToolTests.cs`
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs`
- Scale Test: `tests/Miller.Tests/Server/LiveSearchInspectTests.cs`

**Build:**

- Constructor injection switches from `IndexHolder`/`SmartTargetResolver` direct use to `IWorkspaceIndexProvider`.
- Keep static `Run(...)` method signatures intact for `ContextTool`, `ImpactTool`, and `TraceTool`. Add a workspace
  banner/warning argument to `SearchTool.Run` and `InspectTool.Run` so rendering stays testable. Only the MCP shell
  chooses the workspace context.
- Add parameters:
  - `workspace_id: string? = null`
  - `ensure_fresh: bool? = null`
- Defaulting:
  - current workspace: provider treats null as live current index;
  - explicit workspace: null `ensure_fresh` resolves to true.
- Compact output prefixes cross-workspace warnings:
  - `workspace: <display_id> <canonical_root>`
  - `freshness: unconfirmed_lock_busy` when applicable.

**Acceptance:**

- Existing tool calls without `workspace_id` render the same core results.
- Explicit `workspace_id` routes to target index.
- Explicit `workspace_id` defaults to refresh-first behavior.
- `ensure_fresh=false` skips target scan and reports stale/unconfirmed status when known.
- Tool wrappers call `TelemetryScope.SetWorkspace(...)` and set target `IndexFresh` from `WorkspaceReadContext`.
- Symbol BM25 ordering tests do not change.

**Verification:**

- `scripts/test.sh`
- `scripts/test.sh scale` for live cross-workspace `search` and `inspect`.

## Task 8: Workspace Tool And Renderers

**Owner:** Miller workspace tool worker. Depends on Tasks 3 and 5.

**Files:**

- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceRender.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceToolTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`
- Scale Test: `tests/Miller.Tests/Server/LiveWorkspaceTests.cs`

**Build:**

- `workspace list` reads registry rows and marks current workspace.
- `workspace status` accepts `workspace_id` or `path`.
- `workspace refresh` accepts `workspace_id` or `path`; current workspace uses `IndexerService.TryScanAsLeader`.
- `workspace full` accepts `workspace_id` or `path`; current workspace uses leader path; target workspace uses lock-based force scan.
- `workspace open(path)` primes, registers, and returns stable workspace id.
- `workspace remove(path|workspace_id)` unregisters the row and removes the local index directory when no writer lock is held, refusing live in-use deletion.
- Unknown workspace messages tell the agent to use `workspace open(path)`.

**Acceptance:**

- `workspace list` shows multiple registered workspaces.
- `workspace refresh(workspace_id=...)` refreshes unlocked target.
- Busy target refresh returns lock-busy, not success.
- Current workspace behavior remains honest for leader/non-leader.

**Verification:**

- `scripts/test.sh`
- `scripts/test.sh scale` for live open/refresh/remove paths.

## Task 9: BLAKE3 Freshness Gate

**Owner:** Miller freshness worker. Depends on Task 1 and Task 2.

**Files:**

- Create: `src/Miller.Indexing/ExtractFileHashReader.cs`
- Create: `src/Miller.Indexing/ContentHasher.cs`
- Modify: `src/Miller.Indexing/Miller.Indexing.csproj`
- Modify: `src/Miller.Server/Hosting/FreshnessGate.cs`
- Test: `tests/Miller.Tests/Indexing/ExtractReaderEditTests.cs`
- Test: `tests/Miller.Tests/Server/FreshnessGateTests.cs`
- Test: `tests/Miller.Tests/Freshness/StalenessCheckTests.cs`

**Build:**

- Add `Blake3` 2.2.1 package behind `ContentHasher`.
- Read indexed BLAKE3 from `files.hash`.
- Hash current disk bytes, not decoded text.
- Compare `files.hash` to current BLAKE3.
- Return stale when hash algorithm metadata is absent or not `blake3`.
- Do not remove `StalenessCheck`; keep it as the pure hash comparison primitive.

**Acceptance:**

- Byte-identical file reads fresh.
- Changed bytes read stale.
- Same decoded text with different bytes does not false-fresh.
- Missing `files.hash` or wrong hash algorithm blocks unless caller allows stale.

**Verification:**

- `scripts/test.sh`

## Task 10: Minimal Dashboard Discovery

**Owner:** Dashboard worker. Depends on Task 3 and Task 6.

**Files:**

- Create: `src/Miller.Dashboard/Miller.Dashboard.csproj`
- Create: `src/Miller.Dashboard/Program.cs`
- Modify: `Miller.slnx`
- Modify: `tests/Miller.Tests/Miller.Tests.csproj`
- Test: `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`

**Build:**

- Local Kestrel host binds to loopback only.
- `Miller.Tests` references `Miller.Dashboard` only for dashboard-specific tests.
- `GET /` renders an HTML workspaces table from `~/.miller/workspaces.db`.
- `GET /workspaces.json` returns registry rows.
- `GET /telemetry.json?workspace_id=<id>` returns telemetry summary scoped to the requested workspace.
- No background refresh loop.
- `POST /workspaces/{workspace_id}/refresh` calls the same lock-based refresh service and reports lock-busy honestly.

**Acceptance:**

- Dashboard discovers workspaces without scanning the filesystem.
- Dashboard can show telemetry for a selected workspace.
- Dashboard does not start the MCP server or an indexer daemon.

**Verification:**

- `scripts/test.sh`
- Manual loopback smoke only when UI changes are made.

## Task 11: Docs, Agent Instructions, And Sync

**Owner:** Docs worker. Runs after tool/API behavior is stable.

**Files:**

- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Modify: `CLAUDE.md`
- Generated: `AGENTS.md`
- Modify: `README.md`
- Modify: `docs/miller-mvp-plan.md`
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

**Build:**

- Document `workspace_id` and `ensure_fresh`.
- Document stable local index plus central registry.
- Document BLAKE3 vs SHA-256 split.
- Update dashboard section from deferred to registry-backed.
- Run `scripts/sync-agents.sh` after editing `CLAUDE.md`.

**Acceptance:**

- Agent instructions mention every tool parameter.
- AGENTS and CLAUDE are byte-for-byte synced.
- MVP plan no longer contradicts the registry/dashboard decision.

**Verification:**

- `scripts/test.sh`

## Verification Strategy

**Project source of truth:** `AGENTS.md`, `CLAUDE.md`, `tests/Miller.Tests/Miller.Tests.csproj`, and `scripts/test.sh`.

**Worker red/green scope:** For pure Miller tasks, run the narrow xUnit class or `scripts/test.sh` if the class filter is not stable. For Julie contract work, run Julie's external extract tests for the touched module.

**Worker ceiling:** Workers may run `scripts/test.sh`. Workers touching live extract/startup/cross-workspace refresh may run `scripts/test.sh scale`. Broad `dotnet build Miller.slnx -c Release` is lead-owned unless a worker changes project files.

**Worker gate invariant:** Each worker proves the behavior it owns: registry rows persist, provider routes, refresh locks serialize, read tools route to target workspace, BLAKE3 compares bytes, dashboard reads registry.

**Lead affected-change scope:** After each coherent batch, run `scripts/test.sh`. After Tasks 1, 2, 4, 5, 7, or 8, also run `scripts/test.sh scale`.

**Branch gate:** Before handoff, run `dotnet build Miller.slnx -c Release`, `scripts/test.sh`, and `scripts/test.sh scale` with the restored Julie contract-3 binary.

**Replay/metric evidence:** For search quality, existing `MillerSearchIndexTests` are hard gates. Any added FTS/content work is out of this plan and must not alter symbol BM25 ordering.

**Escalation triggers:** Broaden verification if a change touches `MillerServiceRegistration`, hosted-service lifecycle, Julie contract versioning, `SingleWriterLock`, telemetry DDL, or tool signatures.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless their task explicitly owns updating that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. For scale gates, record whether `.tools/julie-server` was present and which Julie version/contract it emitted.

## Model Routing

**Project source of truth:** No `RAZORBACK.md` exists. Use harness inherited defaults.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage.
- Harness mapping: inherit.

**Implementation tier:** bounded worker tasks from this plan.
- Harness mapping: inherit.

**Mechanical tier:** docs, generated sync, project-file additions, manifest updates.
- Harness mapping: inherit.

**Gate-interpretation reviewer:** reviewer for failed tests, contract-version mismatches, or lock/freshness ambiguity.
- Harness mapping: inherit.

**Escalation tier:** hosted-service lifecycle changes, Julie contract changes, telemetry DDL, cross-process locking.
- Harness mapping: inherit.

**Worker eligibility:** Workers may implement tasks with disjoint files. Julie and Miller workers run in separate worktrees or separate repos.

**Escalation triggers:** repeated lock-busy failures, contract mismatch after repin, schema migration need, startup host graph failure, telemetry DDL incompatibility.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, replay evidence, metrics, or acceptance gates.

**Unsupported harness behavior:** If the harness cannot choose models per agent, use inherited defaults and continue.

## Parallelization

Safe parallel batches:

- Batch A: Task 1 Julie contract and Task 3 Miller registry can run independently.
- Batch B: Task 2 Miller re-pin follows Task 1's local binary but can start with tests and arg plumbing.
- Batch C: Task 5 provider, Task 6 telemetry, and Task 9 BLAKE3 can run after their API seams are merged.
- Batch D: Task 7 read tools and Task 8 workspace tool split cleanly after Task 5.
- Batch E: Task 10 dashboard and Task 11 docs run after registry/tool behavior stabilizes.

Do not parallelize:

- `MillerServiceRegistration` edits from multiple workers.
- `WorkspaceTool` and `WorkspaceRender` edits from different workers.
- Julie contract bump and Miller contract re-pin without an explicit integration checkpoint.

## Final Acceptance Checklist

- [ ] Julie emits contract 3 with `hash_algorithm=blake3`.
- [ ] Miller passes stable full SHA-256 workspace ids to Julie scans.
- [ ] Existing UUID-backed external DBs are repaired safely.
- [ ] `~/.miller/workspaces.db` records all opened/refreshed workspaces.
- [ ] `workspace list` shows registry rows, not only current process workspace.
- [ ] Read tools accept `workspace_id` and `ensure_fresh`.
- [ ] Explicit cross-workspace reads refresh first by default.
- [ ] Busy target locks never cause a second writer.
- [ ] Startup delta scan catches edits made while Miller was down.
- [ ] File freshness compares current raw-byte BLAKE3 to Julie `files.hash`.
- [ ] Cross-workspace telemetry rows carry target workspace id/root.
- [ ] Minimal dashboard discovers workspaces from the registry.
- [ ] Symbol BM25 ordering does not regress.
- [ ] `dotnet build Miller.slnx -c Release` passes with zero warnings.
- [ ] `scripts/test.sh` passes.
- [ ] `scripts/test.sh scale` passes with the restored Julie contract-3 binary.
