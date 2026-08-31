# Stateless workspace targeting implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make one user-level Miller MCP registration safely serve several explicit workspaces without cwd, Roots, connection, or prior-call target inference.

**Architecture:** Add machine-global host paths before primary binding, enforce explicit MCP scope at the request boundary, and route every non-null selector through registered-workspace reads. Retarget lifecycle, content, tests, and edit construction to the resolved workspace. Keep startup primary binding only for background indexing and keep CLI current-workspace behavior unchanged.

**Tech Stack:** .NET 10, C# 14, ModelContextProtocol 1.4.0, SQLite, xUnit, Miller registry and family-store sidecars.

**Architecture Quality:** High-risk caller-interface change. `McpWorkspaceTargetPolicy` owns MCP scope validation, `MillerHostPaths` owns machine-global paths, explicit IDs always use registered routing, and target-bound edit context owns read paths, locking, and convergence.

## Global Constraints

- The approved contract is `docs/plans/2026-08-30-stateless-workspace-targeting-design.md`.
- MCP `search`, `inspect`, `context`, `trace`, `impact`, `edit`, `patterns`, `content`, and `tests` advertise required `workspace_id`.
- `workspace list`, `workspace open(path)`, `workspace remove(path)`, `workspace prune`, and registry dashboard launch remain callable without an ID.
- `current` and `primary` are refused only at the MCP boundary. CLI bytes stay compatible.
- `content workspace_id=all` means registered rows only and remains read-only.
- Reads use `WorkspaceSelectorIntent.Read`; mutations use `WorkspaceSelectorIntent.Mutate` and never guess.
- Explicit selectors always use registered routing, even when they name the bound primary.
- Startup env/cwd binding may run background indexing, watching, and vectors, but never selects an MCP target.
- No MCP request sends `roots/list`.
- `MILLER_HOME` and the test override move every machine-global path together.
- Edit locks, file-converge requests, recovery, and fallback refresh target the resolved workspace.
- File convergence is normal. Bounded refresh with `bypassBackoff: true` is fallback only.
- Do not upgrade `ModelContextProtocol` 1.4.0. Add no MCP tool.
- Keep all existing permanent zero-work guarantees and keep `Miller.Core` free of I/O.
- Edit `CLAUDE.md`, run `scripts/sync-agents.sh`, and never hand-edit generated `AGENTS.md`.
- Use TDD. Workers run focused classes only. The lead owns fast, Scale, Release, and Windows gates.
- Commit mode is `parallel-lead-commit`. Workers do not commit.

---

## Verification Strategy

**Project source of truth:** `CLAUDE.md` and `AGENTS.md` Testing, Build, Host lifecycle, Workspace registry, CT, and Guidance sections.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<AssignedTestClass>"`, combining only assigned focused classes.

**Worker ceiling:** Focused classes plus `dotnet build Miller.slnx -c Debug --no-restore` when signatures require compile-wide evidence.

**Worker gate invariant:** The assigned filter proves the new MCP, routing, host, or mutation contract through caller-facing behavior.

**Lead affected-change scope:** After each batch, run the union of focused filters and one Debug build.

**Branch gate:** Restore pins with `scripts/restore-julie-extract.sh` and `scripts/restore-semantic-sidecar.sh`. On committed clean HEAD run one bare `dotnet test`, `scripts/test.sh scale`, `dotnet build Miller.slnx -c Release`, Windows fast verification with `win-test`, `git diff --check`, `cmp -s CLAUDE.md AGENTS.md`, and Miller branch impact.

**Security scope:** none declared. Project instructions name no secrets or dependency scanner.

**Replay/metric evidence:** Required schemas, diagnostic codes, zero Roots requests, explicit-ID A/B parity, target lock/request paths, pass counts, and instruction budgets are hard gates. Timings are report-only.

**Escalation triggers:** Extractor, family-store, CT provider, vector, or Windows path/lock changes require their documented specialist gates.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope, commit SHA, result, and timestamp. Reuse green evidence on unchanged HEAD.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Global host paths | None - serial | Create `src/Miller.Server/Hosting/MillerHostPaths.cs`; modify `src/Miller.Server/Hosting/WorkspaceContext.cs`, `MillerServiceRegistration.cs`, `IndexBootstrapService.cs`, `src/Miller.Server/Workspaces/WorkspaceOpenPrimeService.cs`; test `MillerHomeTests.cs`, `HostStartupRegistrationTests.cs`, `WorkspaceOpenPrimeServiceTests.cs` | Yes | Every later task needs unbound global services. |
| Task 2: MCP target policy | Batch A | Create `src/Miller.Server/Tools/McpWorkspaceTargetPolicy.cs` and two named tests; modify `WorkspaceBindingCallToolFilter.cs`, both Program files, and the ten exact tool files listed in Task 2 | No | None - safe parallel batch after Task 1. |
| Task 3: Registered routing | Batch A | Modify `WorkspaceIndexProvider.cs`, `MillerServiceRegistration.cs`, `ReadToolWorkspaceRouting.cs`; test the four exact files listed in Task 3 | No | None - safe parallel batch after Task 1. |
| Task 4: Lifecycle, content, CT | Batch B | Modify `WorkspaceTool.cs`, `WorkspaceFactsAssembler.cs`, `ContentTool.cs`, `TestsTool.cs`; test the five exact files listed in Task 4 | No | None - safe parallel batch after Tasks 2 and 3. |
| Task 5: Target-bound edit | Batch B | Create `WorkspaceEditContextFactory.cs` and `RegisteredWorkspaceWriteThrough.cs`; modify the six exact files and test the four exact files listed in Task 5 | No | None - safe parallel batch after Tasks 2 and 3. |
| Task 6: Remove Roots targeting | None - serial | Delete/retire `WorkspaceBindingService.cs` and `WorkspaceRootsNotificationService.cs`; modify `MillerServiceRegistration.cs`, `WorkspaceBindingCallToolFilter.cs`, `Program.cs`; create and modify the four exact tests listed in Task 6 | Yes | Final runtime integration needs Tasks 4 and 5. |
| Task 7: Guidance and docs | None - serial | Modify `MILLER_AGENT_INSTRUCTIONS.md`, `README.md`, `docs/install.md`, two historical docs, the approved design, `CLAUDE.md`; regenerate `AGENTS.md`; test `AgentInstructionsTests.cs` | Yes | Docs must describe final verified behavior. |

### Task 1: Global host paths

**Files:**
- Create: `src/Miller.Server/Hosting/MillerHostPaths.cs`
- Modify: `src/Miller.Server/Hosting/WorkspaceContext.cs`
- Modify: `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceOpenPrimeService.cs`
- Test: `tests/Miller.Tests/Indexing/MillerHomeTests.cs`
- Test: `tests/Miller.Tests/Server/HostStartupRegistrationTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceOpenPrimeServiceTests.cs`

**Interfaces:** Consumes `MillerHome.Resolve()`, app base directory, and deferred bootstrap. Produces `MillerHostPaths` plus unbound-safe registry, telemetry, runner, broker, and open-prime dependencies.

**Contract inputs:** The test home override feeds the same path value. Startup primary and `UpsertSeen` remain.

**File ownership:** Create `src/Miller.Server/Hosting/MillerHostPaths.cs`; modify `src/Miller.Server/Hosting/WorkspaceContext.cs`, `src/Miller.Server/Hosting/MillerServiceRegistration.cs`, `src/Miller.Server/Hosting/IndexBootstrapService.cs`, `src/Miller.Server/Workspaces/WorkspaceOpenPrimeService.cs`; test `tests/Miller.Tests/Indexing/MillerHomeTests.cs`, `tests/Miller.Tests/Server/HostStartupRegistrationTests.cs`, `tests/Miller.Tests/Server/WorkspaceOpenPrimeServiceTests.cs`

**Serialization required:** Yes

**Dependency reason:** Every later task needs unbound global services.

**What to build:** Separate machine-global paths from primary workspace state. Let open-prime drain a registered ID without `WaitUntilBoundAsync`.

**Approach:** Keep one path source through computed delegation. Open telemetry without a default target and stamp each call later.

**Acceptance criteria:**
- [x] Global services resolve while bootstrap is deferred
- [x] Home overrides move all paths together
- [x] Open-prime refreshes without a primary
- [x] Startup binding remains covered
- [x] Focused verification passes and the diff is handed to the lead

### Task 2: MCP target policy and schemas

**Files:**
- Create: `src/Miller.Server/Tools/McpWorkspaceTargetPolicy.cs`
- Create: `tests/Miller.Tests/Server/McpWorkspaceTargetPolicyTests.cs`
- Create: `tests/Miller.Tests/Server/McpToolSchemaTests.cs`
- Modify: `src/Miller.Server/Telemetry/WorkspaceBindingCallToolFilter.cs`
- Modify: `src/Miller.Server/Program.cs` and `eval/semantic-model-eval/Program.cs`
- Modify: `src/Miller.Server/Tools/SearchTool.cs`, `InspectTool.cs`, `ContextTool.cs`, `TraceTool.cs`, `ImpactTool.cs`, `EditTool.cs`, `PatternsTool.cs`, `ContentTool.cs`, `TestsTool.cs`, `WorkspaceTool.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceBindingCallToolFilterTests.cs`
- Test: `tests/Miller.Tests/Server/CallToolFilterTelemetryTests.cs`

**Interfaces:** Consumes tool name and arguments. Produces scope classification and stable `workspace_id_required` or `implicit_workspace_selector_refused` diagnostics.

**Contract inputs:** Prove SDK 1.4 generated schemas. Use required wrapper parameters if annotations do not work.

**File ownership:** Create `src/Miller.Server/Tools/McpWorkspaceTargetPolicy.cs`, `tests/Miller.Tests/Server/McpWorkspaceTargetPolicyTests.cs`, `tests/Miller.Tests/Server/McpToolSchemaTests.cs`; modify `src/Miller.Server/Telemetry/WorkspaceBindingCallToolFilter.cs`, `src/Miller.Server/Program.cs`, `eval/semantic-model-eval/Program.cs`, `src/Miller.Server/Tools/SearchTool.cs`, `InspectTool.cs`, `ContextTool.cs`, `TraceTool.cs`, `ImpactTool.cs`, `EditTool.cs`, `PatternsTool.cs`, `ContentTool.cs`, `TestsTool.cs`, `WorkspaceTool.cs`, `tests/Miller.Tests/Server/WorkspaceBindingCallToolFilterTests.cs`, `tests/Miller.Tests/Server/CallToolFilterTelemetryTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch after Task 1.

**What to build:** Replace bind-first request handling with explicit scope validation before tool construction.

**Approach:** Test `tools/list` schemas. Reject `current` and `primary` only on MCP. Keep `content all` read-only and use typed diagnostics.

**Acceptance criteria:**
- [ ] Nine schemas require `workspace_id`
- [ ] Only approved workspace operations omit it
- [ ] Missing/implicit targets fail before construction
- [ ] CLI/direct cores keep current behavior
- [ ] Focused verification passes and the diff is handed to the lead

### Task 3: Registered routing

**Files:**
- Modify: `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
- Modify: `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- Modify: `src/Miller.Server/Tools/ReadToolWorkspaceRouting.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`
- Test: `tests/Miller.Tests/Indexing/IndexLevelContextTests.cs`
- Test: `tests/Miller.Tests/Server/ReadToolWorkspaceRoutingTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceRegistrySelectorTests.cs`

**Interfaces:** Consumes global paths, optional primary, registry selectors, and registered sessions. Produces providers where non-null means registered and null means internal primary.

**Contract inputs:** The same explicit ID has the same result regardless of binding state.

**File ownership:** Modify `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`, `src/Miller.Server/Hosting/MillerServiceRegistration.cs`, `src/Miller.Server/Tools/ReadToolWorkspaceRouting.cs`; test `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`, `tests/Miller.Tests/Indexing/IndexLevelContextTests.cs`, `tests/Miller.Tests/Server/ReadToolWorkspaceRoutingTests.cs`, `tests/Miller.Tests/Server/WorkspaceRegistrySelectorTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch after Task 1.

**What to build:** Make provider construction unbound-safe and remove the explicit-selector shortcut to current.

**Approach:** Access primary lazily only for null routes. Add no-primary, different-primary, and matching-primary A/B coverage.

**Acceptance criteria:**
- [x] Explicit IDs work unbound
- [x] Binding state does not change explicit semantics
- [x] Null current routes stay green
- [x] Registered level/freshness contracts stay green
- [x] Focused verification passes and the diff is handed to the lead

### Task 4: Lifecycle, content, and CT

**Files:**
- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`
- Modify: `src/Miller.Server/Tools/ContentTool.cs`
- Modify: `src/Miller.Server/Tools/TestsTool.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceToolTests.cs`, `tests/Miller.Tests/Server/WorkspaceToolPruneTests.cs`, `tests/Miller.Tests/Server/LiveWorkspaceTests.cs`, `tests/Miller.Tests/Server/ContentToolTests.cs`, `tests/Miller.Tests/Server/TestsToolTests.cs`

**Interfaces:** Consumes host paths, global registry/ledger, optional primary snapshot, and explicit selectors. Produces unbound lifecycle, content, and CT behavior.

**Contract inputs:** Mutations use `Mutate` intent. `content all` has no synthetic current row. Writer-lock safety remains.

**File ownership:** Modify `src/Miller.Server/Tools/WorkspaceTool.cs`, `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`, `src/Miller.Server/Tools/ContentTool.cs`, `src/Miller.Server/Tools/TestsTool.cs`; test `tests/Miller.Tests/Server/WorkspaceToolTests.cs`, `tests/Miller.Tests/Server/WorkspaceToolPruneTests.cs`, `tests/Miller.Tests/Server/LiveWorkspaceTests.cs`, `tests/Miller.Tests/Server/ContentToolTests.cs`, `tests/Miller.Tests/Server/TestsToolTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch after Tasks 2 and 3.

**What to build:** Remove constructor-time primary needs and resolve all target paths from registered rows.

**Approach:** Use optional primary facts only for live self-protection. Keep CT start explicit and content locks per selected DB.

**Acceptance criteria:**
- [ ] Registry-wide operations work unbound
- [ ] Named lifecycle operations use the selected row
- [ ] Content and CT target the selected workspace
- [ ] Prune/remove retain safety
- [ ] Focused verification passes and the diff is handed to the lead

### Task 5: Target-bound edit

**Files:**
- Create: `src/Miller.Server/Workspaces/WorkspaceEditContextFactory.cs`
- Create: `src/Miller.Server/Hosting/RegisteredWorkspaceWriteThrough.cs`
- Modify: `src/Miller.Server/Tools/EditTool.cs` and `EditService.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceSymbolReadContext.cs` and `WorkspaceIndexProvider.cs`
- Modify: `src/Miller.Server/Hosting/MillerServiceRegistration.cs` and `LeaderWriteThrough.cs`
- Test: `tests/Miller.Tests/Server/EditToolTests.cs`, `tests/Miller.Tests/Server/LeaderScanRequestQueueTests.cs`, `tests/Miller.Tests/Server/EditWriteLockTests.cs`, `tests/Miller.Tests/Indexing/IndexLevelContextTests.cs`

**Interfaces:** Consumes explicit ID, resolved symbol context, target edit lock, file-converge queue, and bounded refresh. Produces a disposable target edit service/context.

**Contract inputs:** Queue file convergence first. Refresh with `bypassBackoff: true` only after bounded recovery sees no revision advance.

**File ownership:** Create `src/Miller.Server/Workspaces/WorkspaceEditContextFactory.cs`, `src/Miller.Server/Hosting/RegisteredWorkspaceWriteThrough.cs`; modify `src/Miller.Server/Tools/EditTool.cs`, `src/Miller.Server/Tools/EditService.cs`, `src/Miller.Server/Workspaces/WorkspaceSymbolReadContext.cs`, `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`, `src/Miller.Server/Hosting/MillerServiceRegistration.cs`, `src/Miller.Server/Hosting/LeaderWriteThrough.cs`; test `tests/Miller.Tests/Server/EditToolTests.cs`, `tests/Miller.Tests/Server/LeaderScanRequestQueueTests.cs`, `tests/Miller.Tests/Server/EditWriteLockTests.cs`, `tests/Miller.Tests/Indexing/IndexLevelContextTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch after Tasks 2 and 3.

**What to build:** Make preview/apply resolve, lock, write, reopen, and converge the named registered workspace.

**Approach:** Add index DB path to resolved context. Construct `EditApplier` against target `.miller`. Reuse local write-through only for the actual serviced primary.

**Acceptance criteria:**
- [ ] Non-primary preview/apply works
- [ ] Only target edit lock is used
- [ ] Apply queues target file convergence
- [ ] Recovery reopens the same target and bounds fallback
- [ ] Ambiguous mutation touches no files
- [ ] Focused verification passes and the diff is handed to the lead

### Task 6: Remove Roots targeting

**Files:**
- Delete or retire: `src/Miller.Server/Hosting/WorkspaceBindingService.cs`
- Delete or retire: `src/Miller.Server/Hosting/WorkspaceRootsNotificationService.cs`
- Modify: `src/Miller.Server/Hosting/MillerServiceRegistration.cs`
- Modify: `src/Miller.Server/Telemetry/WorkspaceBindingCallToolFilter.cs`
- Modify: `src/Miller.Server/Program.cs`
- Create: `tests/Miller.Tests/Server/StatelessWorkspaceTargetingIntegrationTests.cs`
- Modify: `tests/Miller.Tests/Server/HostStartupRegistrationTests.cs`, `tests/Miller.Tests/Server/WorkspaceBindingCallToolFilterTests.cs`, `tests/Miller.Tests/Server/CallToolFilterTelemetryTests.cs`

**Interfaces:** Consumes completed explicit routing. Produces one process serving two IDs with no Roots requests.

**Contract inputs:** Startup primary may exist but cannot alter explicit output. Deferred bootstrap may remain forever.

**File ownership:** Delete or retire `src/Miller.Server/Hosting/WorkspaceBindingService.cs` and `src/Miller.Server/Hosting/WorkspaceRootsNotificationService.cs`; modify `src/Miller.Server/Hosting/MillerServiceRegistration.cs`, `src/Miller.Server/Telemetry/WorkspaceBindingCallToolFilter.cs`, `src/Miller.Server/Program.cs`; create `tests/Miller.Tests/Server/StatelessWorkspaceTargetingIntegrationTests.cs`; modify `tests/Miller.Tests/Server/HostStartupRegistrationTests.cs`, `tests/Miller.Tests/Server/WorkspaceBindingCallToolFilterTests.cs`, `tests/Miller.Tests/Server/CallToolFilterTelemetryTests.cs`

**Serialization required:** Yes

**Dependency reason:** Final runtime integration needs Tasks 4 and 5.

**What to build:** Remove request-time Roots binding/rebinding and prove the final host lifecycle at the MCP boundary.

**Approach:** Use isolated home and unsafe cwd, open two temp workspaces, call both IDs, and assert no state bleed or Roots callback.

**Acceptance criteria:**
- [ ] Production sends no `roots/list`
- [ ] Deferred bootstrap blocks no explicit call
- [ ] One process serves two IDs
- [ ] Matching/different primary changes nothing
- [ ] Focused verification passes and the diff is handed to the lead

### Task 7: Guidance and docs

**Files:**
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Modify: `README.md` and `docs/install.md`
- Modify: `docs/findings/2026-06-25-cursor-project-local-mcp-config.md`
- Modify: `docs/plans/2026-06-25-mcp-roots-workspace-binding-design.md`
- Modify: `docs/plans/2026-08-30-stateless-workspace-targeting-design.md`
- Modify: `CLAUDE.md` and regenerate `AGENTS.md`
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

**Interfaces:** Consumes final behavior. Produces list/open then pass-ID guidance and synced project instructions.

**Contract inputs:** Server instructions stay within 1,900 characters. Existing description budgets do not grow.

**File ownership:** Modify `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`, `README.md`, `docs/install.md`, `docs/findings/2026-06-25-cursor-project-local-mcp-config.md`, `docs/plans/2026-06-25-mcp-roots-workspace-binding-design.md`, `docs/plans/2026-08-30-stateless-workspace-targeting-design.md`, `CLAUDE.md`; regenerate `AGENTS.md`; test `tests/Miller.Tests/Server/AgentInstructionsTests.cs`

**Serialization required:** Yes

**Dependency reason:** Docs must describe final verified behavior.

**What to build:** Replace Roots/current guidance for GUI clients and mark old guidance historical.

**Approach:** Replace text within budgets, keep CLI wording separate, edit CLAUDE first, then sync.

**Acceptance criteria:**
- [ ] GUI setup explains list/open then required ID
- [ ] Instruction and metadata budgets pass
- [ ] Old Roots guidance is historical
- [ ] CLAUDE and AGENTS are byte-identical
- [ ] Focused verification passes and the diff is handed to the lead
