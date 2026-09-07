# Workspace, guidance, and storage implementation plan

> For agentic workers: use `razorback:subagent-driven-development` for independent tasks and `razorback:executing-plans` for serial work. This document is a proposed plan, not implementation approval.

**Goal:** Make workspace freshness and recovery honest, reduce repeated guidance, and expose storage pressure without weakening retention safeguards.

**Architecture:** Keep workspace evidence collection in Server/Indexing and rendering pure. Keep store retention in julie-extract. Reuse existing tools, diagnostic records, sidecar reclaim journals, and guidance delivery channels.

**Tech stack:** .NET 10, SQLite, Node.js hooks; the pinned julie-extract process contract.

## Validation

Validated on Miller `40f7c71a7703b369998903e5f9a536fda006da57`, with producer source at `b7a7c62a061c707dd5809d4c401bf36ed286fbbe`. The original report remains unchanged. IDs below follow the original area's table row order.

| Finding | Verdict and evidence | Task |
|---|---|---|
| W1: permanent pending banner | Verified. `WorkspaceIndexProvider.ResolveRegisteredState`, lines 1044–1048, always returns pending for Background. `StartBackgroundRefresh`, line 1105, discards its result and catches failures without storing them. `WorkspaceFreshnessView`, lines 17–30, converts pending into false freshness. Live reads reported pending at revision 87715 while health reported fresh at that revision. Registry revision equality alone cannot prove source freshness. | W1 |
| W2: reader refresh refusal | Verified control-flow difference. `WorkspaceTool.RenderTargetAction`, line 1235, takes the local branch only for a bound current workspace; otherwise it calls `CrossWorkspaceRefreshService`, whose lock wait is two seconds. The exact timing remains the earlier measurement. A live leader and equal revision do not prove that a refresh request was delegated. | W2 |
| W3: health hides useful warnings | Verified. `WorkspaceRender.HealthCompact`, lines 1438–1454, emits only warning/action zero; a live call omitted two of each. The particular leader mismatch is historical, not present in this session's printed leader line. | W3 |
| W4: list loses current workspace | Partly verified. Error preference is real and undocumented. `WorkspaceFactsAssembler.TakePreferringErrors`, line 569, reserves a non-error slot when a Current row exists. Live `list limit=5` kept Miller and four error rows. With no bound primary there may be no Current row to reserve. | W3 |
| W5: opaque store provenance in compact | Verified in `WorkspaceRender.StatusCompact` and `StoreProvenanceLabel`; health also truncates the long provenance line. Identifiers remain useful in exhaustive diagnostics. | W3 |
| W6: old onboarding errors presented as friction | Verified. `TelemetryOnboardingReader.Read`, lines 73–110, defaults to 30 days and anchors the window to the latest event, not the current time. `WorkspaceOnboardingAssembler` supplies no narrower window. A dormant workspace can therefore show arbitrarily old friction as recent guidance. | W4 |
| W7: repeated prune nudge | Verified. `WorkspaceTool.StaleRegistryHint`, line 693, recomputes it on each eligible compact call. | W3 |
| G1: discovery core wastes budget | Verified qualitative issue; numeric correction: the current core is 1,839 characters, not the report's 1,871. It contains the unrelated Goldfish variable and terse routing lines. The exception list also omits `remove`, although the parameter schema includes it. | G1 |
| G2: hook ignores cwd | Verified omission in `hooks/miller-session-hook.cjs`, lines 19–36. Proposed automatic binding is rejected: hook cwd is a candidate, not proof of user intent or registration. This is particularly important for moved shells, subagents, and GUI sessions. | G2 |
| G3: invalid skill examples | Verified in `.agents/skills/miller-explore-area/SKILL.md`, lines 27–31 and 51–59, and `miller-search-debug/SKILL.md`, lines 43–64. Literal pipe-joined mode/content_kind values and pipe-separated regions are invalid examples. Bare scoped workspace calls also occur. Overview/full wording needs to distinguish bounded relationship callers from complete relations. | G1 |
| G4: allowed-tools and frontmatter | `arguments:` and hard-coded direct MCP names exist. A plugin-only prefix replacement would break direct registrations, including this session's `mcp__miller__*` tools. Validate both installation forms; do not globally replace one valid namespace with another. | G1 |
| G5: stale-sidecar diagnostic | Verified. `SymbolSearchSidecar.cs:299`, `ContentCorpusSidecar.cs:422`, and `FtsRegionSearchIndex.cs:57` throw untyped InvalidOperationException with CLI wording. `ToolDiagnostic.FromException`, lines 91–148, leaves this as internal_failure. The historical error rate is not a current failure rate. | W2 |
| G6: Codex/Cursor hooks | Mixed. Cursor manifest has no hooks. Codex manifest already wires `hooks/claude-codex-hooks.json`. The install doc contradicts itself. Current official Codex docs support trusted plugin hooks, so the blanket claim that Codex cannot load them is outdated. Runtime delivery still needs a versioned harness test. | G2 |
| G7: repeated targeting prose | Verified in the source skills and supplied Razorback block. Duplication is measurable; the exact injected total depends on host and installed plugin version. Current Razorback guidance in this session includes both tests and workspace_id, contradicting that part of the report. | G1 |
| D1: disk pressure and absent visibility | Large allocation verified: Miller's `gen-001/store.db` is 15,473,782,784 bytes in this session. Existing producer `RetentionPlan` includes logical/physical budgets and compaction_required. `StoreMaintenanceRunner` mainly consumes pruned_request_rows. The exact 55 GB total and retained-row counts remain historical. | D1 |
| D2: prune/reclaim behavior | Partly verified. 93 registered roots and 39 missing roots reproduced. A dry run reproduced 33 would-prune, 60 kept, and six unconfirmed-lineage refusals. `WorkspaceTool.Prune`, line 1512, already passes awaitProducerRetirement=false. `WorkspaceRegistryPrune.Run`, lines 161–182, records intent before deleting and queues retirement. Six unconfirmed roots are a separate safety refusal, not proof of absent asynchronous reclaim. Partial successful results still receive a whole-call refusal diagnostic. | D2 |

## Architecture quality

- Affected modules: workspace routing and refresh, diagnostics, telemetry onboarding, hook adapters, maintenance reporting.
- Caller-facing contracts remain the existing workspace/read tools. Proposed additive refresh activity and storage facts must have explicit unavailable states.
- Share background operation state through the existing singleton lifetime. Do not make the transient provider a singleton with live database handles.
- Tests exercise tool results and injected refresh schedules, not private dictionary contents.
- Reject freshness inferred only from matching revisions, fake delegated success, unbounded health scans, direct store deletes, and automatic registration from hook input.
- Risk is medium for freshness and storage contracts. Rendering and wording changes are low risk. Implementation must review those contracts before changing consumers.

## Global constraints

- Add no MCP tools. Keep `workspace_id` explicit and registered. `ensure_fresh=false` performs zero refresh work.
- Preserve `MILLER_SEMANTIC=off` and `MILLER_CT=off` zero-work guarantees.
- Core stays at or below 1,900 normalized characters. Preserve tested description budgets and ADR-0001's compact-only nudge contract.
- `CLAUDE.md` is authoritative; generate `AGENTS.md`. Edit `.agents/skills/` and generate `skills/`.
- Never remove a live root, reclaim a survivor's sidecars, fabricate a retirement view ID, or bypass active-reader protections.
- Use existing producer retention policy. A different retention promise requires an owner decision with measured alternatives.
- No deployment, publishing, or destructive cleanup is part of this plan.

## Verification strategy

Project source of truth is `CLAUDE.md`. For each behavioral task add a failing regression, run its focused class, implement, then run that same scope once. Existing passing tests are not evidence that a proposed behavior exists.

- Worker scope and ceiling: the classes named per task, using `dotnet test --filter "FullyQualifiedName~<ClassName>"`; plugin-only tasks use the named Node tests.
- Worker invariant: the stated observable behavior and its failure paths, with unavailable evidence never rendered as success.
- Lead affected-change scope: union of changed-area classes after a coherent batch; skip unchanged green scopes.
- Branch gate: `dotnet test` once and `dotnet build Miller.slnx -c Release`, zero warnings/errors. Add `scripts/test.sh scale` for indexing/producer changes. Windows gate is required before a release, not for writing these plans.
- Security scope: none declared for this documentation task; implementation follows repository security requirements without sending code to an external reviewer unless selected.
- Replay evidence: tool counts, bytes, and latency are report-only until fixtures establish a baseline. Correct freshness labels, exact row accounting, bounded output, no writes from status, and journal ordering are hard gates.
- Ledger: record invariant, command, scope, source SHA, result, and timestamp. Investigate failures; do not weaken existing safety tests.

## Parallel execution contract

Commit mode is parallel-lead-commit. Workers return their owned diff and evidence without committing. Source setup must inventory existing task worktrees before implementation.

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| W1 | A | WorkspaceIndexProvider.cs, WorkspaceFreshnessView.cs, BackgroundRefreshGate.cs, ReadToolWorkspaceRouting.cs and their named tests below | No | None; safe parallel batch |
| W2 | B | CrossWorkspaceRefreshService.cs, WorkspaceTool.cs, sidecar exceptions and diagnostics listed below | Yes | Uses W1 activity outcomes; shared WorkspaceTool ownership |
| W3 | C | WorkspaceRender.cs, WorkspaceFactsAssembler.cs, WorkspaceTool.cs and render/list tests | Yes | Shared WorkspaceTool with W2 and D2 |
| W4 | C2 | TelemetryOnboardingReader.cs, WorkspaceOnboardingAssembler.cs, WorkspaceOnboardingFacts.cs, WorkspaceRender.cs, onboarding tests | Yes | Shares WorkspaceRender.cs with W3 |
| G1 | D | Core, source/mirrored skills, routing block, guidance docs and guidance tests listed below | Yes | Describes final tool behavior from all plans |
| G2 | E | Hook adapter, plugin manifests and hook tests listed below | Yes | Shares plugin tests/docs with G1 |
| D1 | F | Maintenance runner, health facts/renderer, producer maintenance report and tests | Yes | Extends W3 health rendering; producer contract first |
| D2 | G | WorkspaceRegistryPrune.cs, WorkspaceRemoval.cs, WorkspaceTool.cs, prune diagnostics/tests | Yes | Shared WorkspaceTool and storage report |

## W1: report source freshness independently from refresh activity

**Files:** modify `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`, `WorkspaceFreshnessView.cs`, `BackgroundRefreshGate.cs`, and `src/Miller.Server/Tools/ReadToolWorkspaceRouting.cs`; tests in `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs` and `ReadToolWorkspaceRoutingTests.cs`. Add a new `WorkspaceFreshnessViewTests.cs` beside them if a separate policy fixture is needed.

**Interfaces:** preserve refresh modes; add a proposed bounded operation snapshot in the singleton refresh gate. It records workspace, generation/revision at scheduling, queued/running/finished/failed state, observation time, and result. These are proposed fields, not existing APIs.

**Contract inputs/file ownership:** only the files above, with the existing registry row and refresh result as evidence. W2 consumes the operation state. Serialize any later changes to these types.

**Implementation steps:**
1. Write a regression using the existing fake scheduler. A Background read while cooldown rejects scheduling must not claim a pending scan. A scheduled failure must affect the next read; a completed unchanged refresh must clear activity.
2. Keep coalescing and cooldown in `BackgroundRefreshGate`; retain bounded completion evidence without retaining a provider or DB session. Record success/failure in `StartBackgroundRefresh`, including scheduler failure.
3. Render separate source and activity facts. A served snapshot can be readable with freshness unknown. A registry watermark match is insufficient if no source check was observed. A generation change invalidates old completion evidence.
4. Add no-primary and cross-process cases. Where another process's queue is not observable, say unknown rather than queue empty. Preserve foreground refresh when no readable snapshot exists.

- [ ] Background scheduling never forces source freshness to false by itself.
- [ ] Completed, skipped, failed, in-flight, and unknown states are distinguishable.
- [ ] No-primary startup, generation replacement, concurrent callers, and ensure_fresh=false pass focused tests.
- [ ] Warnings survive to the next call without making the read wait for background work.

## W2: truthful reader refresh and sidecar recovery

**Files:** `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`, `src/Miller.Server/Tools/WorkspaceTool.cs`, `ToolDiagnostic.cs`, `src/Miller.Indexing/SymbolSearchSidecar.cs`, `ContentCorpusSidecar.cs`, `FtsRegionSearchIndex.cs`; new `src/Miller.Indexing/SidecarUnavailableException.cs`. Tests: `tests/Miller.Tests/Server/CrossWorkspaceRefreshServiceTests.cs`, `ToolDiagnosticTests.cs`, and `WorkspaceToolTests.cs`.

**Interfaces:** existing explicit refresh and diagnostic contracts; proposed typed sidecar exception carries artifact kind and recovery reason. Indexing does not reference Server.

**Contract inputs/file ownership:** W1 state and existing leader/coordinator request evidence. W2 owns the files above until its focused tests pass.

Retrieval task 1 must integrate first because it also owns `ContentCorpusSidecar.cs`. Preserve its new classification-policy freshness reason in the typed diagnostic.

1. Reproduce both bound-primary and deferred-primary refresh paths with a live leader. Assert a queued/delegated response only after the producer accepts a request. If no supported handoff exists, return readable-but-not-refreshed with a precise next action, not fabricated success.
2. Use current leader/request evidence to avoid a redundant lock wait when the outcome is already known. Preserve explicit bypassBackoff and rebuild intent semantics. Do not promise that ensure_fresh=true can defeat leader contention.
3. Replace all equivalent stale-sidecar throws with the typed exception. Map it to unavailable, with a harness-neutral explanation and a workspace-scoped MCP recovery action. Locate all throw sites before editing.
4. Verify unknown exceptions remain internal_failure and corruption retains its stronger classification.

- [ ] Refresh success, queued work, refused work, and unchanged readable data are distinct.
- [ ] A stale sidecar yields a copyable recovery step containing the selected workspace.
- [ ] No new automatic retry loop or false promise of completed freshness is introduced.

## W3: reserve compact output for actionable workspace facts

**Files:** `src/Miller.Server/Tools/WorkspaceRender.cs`, `WorkspaceFactsAssembler.cs`, `WorkspaceTool.cs`; tests `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`, `WorkspaceHealthLeaderTests.cs`, and `WorkspaceToolTests.cs`.

**Interfaces:** compact rendering and list selection. Exhaustive health retains provenance IDs. File ownership serializes with W2/D1/D2.

1. Render up to three warnings and matching actions in priority order: availability/corruption and leader/version problems before capability summaries. State the remaining count.
2. Keep compact store level, generation, and member count; preserve full provenance in exhaustive diagnostics.
3. Make error preference explicit in list output. Preserve a bound Current row. Reserve at most `floor(limit / 4)` slots for pinned errors, then fill by recency; at limits below four, use recency plus bound-current preference. Return exact matched/omitted counts. No-primary lists must not invent a current repository.
4. Emit the maintenance nudge only in onboarding. Keep list's missing-root counts as facts. Avoid process-global suppression that would deprive unrelated sessions of guidance.

- [ ] Three meaningful health warnings fit within the existing tool budget.
- [ ] Limits 1, 3, 5, and 20 work with zero/all/mixed errors and absent/present Current rows.
- [ ] Status and routine reads no longer repeat a prune instruction.
- [ ] Exhaustive diagnostics retain all stable identifiers.

## W4: show recent onboarding friction

**Files:** `src/Miller.Server/Telemetry/TelemetryOnboardingReader.cs`, `src/Miller.Server/Tools/WorkspaceOnboardingAssembler.cs`, `WorkspaceOnboardingFacts.cs`, `WorkspaceRender.cs`; tests `tests/Miller.Tests/Server/TelemetryOnboardingReaderTests.cs`. Rendering edits serialize with W3.

**Interfaces:** proposed explicit time anchor and current-friction window; existing 30-day historical API remains available.

1. Add clock-controlled fixtures with old errors, recent success, and a workspace with no calls for a month.
2. Use a current-time seven-day window for friction/misses used as recommendations. Preserve and label longer historical flow statistics separately. Name the window in the result; do not silently call old history current.
3. Group recoverable failures by stable diagnostic code where recorded. Retain unknown/historical codes honestly rather than guessing from an exception name.

- [ ] A dormant workspace shows no recent observations rather than a fresh-looking old spike.
- [ ] Historical totals remain available and clearly dated.
- [ ] Legacy rows missing version/diagnostic metadata remain readable.

## G1: consolidate guidance and repair copyable examples

**Files:** `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`, `hooks/miller-routing-block.md`, `docs/agent-setup-snippet.md`, `docs/agent-guidance.md`, `docs/install.md`, `CLAUDE.md`, generated `AGENTS.md`, `.agents/skills/*/SKILL.md`, generated `skills/*/SKILL.md`, `tests/plugin/hooks-routing-block.test.cjs`, `tests/plugin/plugin-manifest.test.cjs`, `tests/Miller.Tests/Server/AgentInstructionsTests.cs`. Tool owners update their own Description attributes; this task reviews the combined budget after they land.

**Interfaces:** the five existing guidance channels. This task owns wording and mirror generation, not tool behavior.

1. Replace the negative binding paragraph with explicit registered-selector use and discovery/open fallback. Keep no-primary support and `remove` among unscoped exceptions. Spend recovered space on import-before-read, impact defaults, and CT status/run routing.
2. Put an opt-in CT workflow beside impact: inspect status after an edit; use CT run only for enabled workspaces and report selection cost. Keep direct focused tests valid when CT is off. Do not tell agents to enable CT merely to get a verdict.
3. Fix all enum examples to one accepted value. Use comma-separated regions. Add workspace_id to scoped examples, and use legal limits. Explain overview's bounded callers versus full relations.
4. Replace `arguments:` with `argument-hint`. Test frontmatter against both direct-server and plugin tool names. If allowed-tools is retained, list supported precise names for both forms; do not use an unrestricted wildcard. Verify actual host behavior before claiming permission restrictions are repaired.
5. Update descriptions to explain effective caps, region dispatch, body-only edits, and the actual paging contracts implemented by the other plans. Keep valid CLI and MCP spellings distinct with a short mapping, rather than renaming established commands.
6. Remove redundant long targeting blocks while keeping each workflow usable when loaded alone. The separate source change belongs to `/home/murphy/source/razorback/skills/using-razorback/SKILL.md`: defer to Miller's injected workflow when present, with a complete fallback when absent. Inventory that repository's current changes and follow its own tests before editing. Do not edit installed plugin cache files or assume the external copy is missing CT today.
7. Run `scripts/sync-agents.sh`, `scripts/sync-plugin-skills.sh`, `cmp -s CLAUDE.md AGENTS.md`, `node --test tests/plugin/hooks-routing-block.test.cjs tests/plugin/plugin-manifest.test.cjs`, and the AgentInstructionsTests scope.

- [ ] Every example uses a valid enum, separator, scope, and limit.
- [ ] Core remains within 1,900 characters and names all ten tools with useful routing.
- [ ] Routing block, rules output, and setup snippet remain byte-identical.
- [ ] Description tests enforce behavior and budgets without pinning unexplained jargon.
- [ ] Direct MCP installs and plugin installs both remain supported.

## G2: contextual hook candidates and host-specific delivery

**Files:** `hooks/miller-session-hook.cjs`, `hooks/claude-codex-hooks.json`, new `hooks/cursor-hooks.json`, `.cursor-plugin/plugin.json`, `.codex-plugin/plugin.json`, `docs/install.md`, `tests/plugin/plugin-manifest.test.cjs`, `tests/plugin/hooks-routing-block.test.cjs`, and `tests/plugin/hooks-session-hook.test.cjs`.

**Interfaces:** unchanged static routing block plus a separate host-context appendix. Cursor uses its own output envelope. File ownership serializes after G1.

1. Parse bounded stdin defensively. A host-native absolute project path may be printed as a candidate, using JSON encoding and avoiding instruction-like interpolation. Do not compute a display hash, assume registration, register automatically, or override the conversation's project root. If host context is absent/ambiguous, retain discovery instructions.
2. Preserve the static block exactly so `miller rules` and the setup snippet stay valid. A subagent's changed cwd must not replace the intended parent project silently. Test malformed JSON, no input, spaces, symlinks, Windows paths, and hook opt-out.
3. Add Cursor sessionStart output using `additional_context`, not the Claude `hookSpecificOutput` envelope. Validate plugin root expansion and command paths with the current official schema.
4. Rewrite the Codex install paragraph to describe trusted plugin hooks plus a fallback for versions/configurations that do not deliver them. Test trusted/untrusted/disabled delivery on a named runtime before release. Do not assert a universal plugin-hook limitation from an old issue.

Sources checked 2026-09-07: [Codex plugin hooks](https://learn.chatgpt.com/docs/hooks#plugin-bundled-hooks) documents explicit hook trust and plugin manifest paths. [Cursor hooks](https://prod.cursor.com/docs/hooks#sessionstart) documents the sessionStart response shape. These support delivery adapters, not implicit workspace binding.

- [ ] Hook faults never prevent the session from starting.
- [ ] Candidate context never bypasses explicit registered workspace selection.
- [ ] Each host receives its documented JSON envelope and the same static rules.
- [ ] Install docs distinguish available capability from verified local delivery.

## D1: expose retention pressure and diagnose the large store

**Files:** `src/Miller.Indexing/Store/StoreMaintenanceRunner.cs`, `src/Miller.Server/Tools/WorkspaceHealthFacts.cs`, `WorkspaceFactsAssembler.cs`, `WorkspaceRender.cs`; tests `tests/Miller.Tests/Indexing/StoreMaintenanceRunnerTests.cs` and `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`. Producer work, if a defect is reproduced: `/home/murphy/source/julie-extractors/crates/julie-extract-artifact/src/store/maintenance.rs`, `crates/julie-extract-cli/src/store/maintenance.rs`, and `crates/julie-extract-artifact/tests/store_maintenance_contract.rs`.

CT accounting ownership also belongs to this task: `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`, `src/Miller.Server/Tools/TestsCore.cs`, and proposed `src/Miller.Testing/Daemon/CtDiskAccountingSnapshot.cs`; tests `tests/Miller.Tests/Testing/Analysis/ContinuousTestStoreApplierTests.cs` and `tests/Miller.Tests/Testing/Daemon/Engine/CtBuildCacheMaintenanceTests.cs`. Serialize after the CT plan's TestsCore changes. Reuse existing persisted generation accounting where it supplies the facts; the proposed snapshot need only carry otherwise missing budget, timestamp, and measurement-state facts.

**Interfaces:** consume the existing producer report's `retention`, `capacity`, `failure_class`, and `error` fields. Keep production health cheap by reading a bounded recorded snapshot; explicit diagnostic inspection may run the producer. No full fact-cache hydration or recursive home-directory scan on status.

1. Add report parsing fixtures with retention pressure, compaction required, reader-pinned history, and failed inspection. Failed reports' zero counters must never be rendered as zero usage.
2. Render physical allocated bytes separately from retained logical bytes, with the producer's target/ceiling, measurement time, and reason reclamation is blocked. Include Miller sidecar allocation and CT disk warnings through their owning facts rather than conflating all budgets.

   `ContinuousTestCoordinator.CommitMaintenance`, line 754, already persists generation accounting and logs the over-budget result at line 782. Publish the bounded accounting outcome there, then project it through TestsCore and workspace health. Status never walks build directories or creates a missing accounting record. Failed/missing measurements remain unavailable, not zero; retain the time of the last valid measurement.
3. Re-run read-only inspection with the pinned binary on a stable snapshot: `.tools/julie-extract store maintain inspect --store <registered-store-root> --json`. This validation's attempt returned exit 1, `stale_plan`, `maintenance_inspection_raced`, because coord.db changed. Its zero-valued metrics are invalid. Do not stop live processes just to make the measurement pass.
4. Establish whether retained bytes are policy-protected, reclaimable, or awaiting compaction. Compare the pinned version to producer HEAD. If existing maintenance correctly protects readers, expose that fact. If expired/unreachable data remains despite a valid maintenance pass, reduce it to a producer contract test and repair the owning path. Use a disposable family for apply/compaction verification; obtain explicit approval before destructive work on the user's live store.
5. Pin/restore a producer fix only after its own contract and platform gates pass. If no producer defect reproduces, retain existing policy and close the diagnosis with allocation/protection evidence, not a speculative new retention algorithm.

- [ ] Health shows pressure or explicitly unmeasured/unavailable evidence, never fabricated zeroes.
- [ ] Active readers, current manifests, other views, and unknown liveness remain protected.
- [ ] Diagnosis records bytes before/after and the reason each retained class survives.
- [ ] Any verified producer leak has a regression and fix in julie-extractors; Miller does not delete store tables directly.

## D2: report partial prune and preserve retirement safeguards

**Files:** `src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs`, `WorkspaceRemoval.cs`, `src/Miller.Server/Tools/WorkspaceTool.cs`, `WorkspaceRender.cs`; tests `tests/Miller.Tests/Server/WorkspaceRegistryPruneTests.cs`, `WorkspaceToolTests.cs`, `WorkspaceRenderTests.cs`, and `tests/Miller.Tests/Indexing/StoreSidecarReclaimTests.cs`.

**Interfaces:** existing prune/remove result types and durable reclaim intent. Proposed additive outcome distinction for partial success, with per-row reasons and exact scoped actions.

1. Add a mixed fixture: removable missing root, unconfirmed removed worktree, surviving root, and journal-write failure. MCP already does asynchronous retirement; preserve that path.
2. Report removed/would-remove, kept, retirement-owed, and blocked counts independently. Preserve positive result_count and successful telemetry when useful work exists; expose blocked entries as warnings rather than relabeling the whole operation empty/refused.
3. Distinguish unconfirmed lineage from producer failure. Suggest exact `workspace remove path=...` only where that operation's existing safety checks can authorize explicit removal. A missing directory alone must not authorize deletion of a still-owned producer view.
4. Keep intent-before-delete, survivor protection, retryable owed records, and no writes in dry_run. Add crash/retry tests where changes touch that sequence. No redesign is needed merely to add journaling that already exists.

- [ ] Mixed prune results remain actionable and correctly counted.
- [ ] Safety refusals remain visible and are not silently bypassed.
- [ ] A failed intent write keeps the row; a completed unregister never loses retirement debt.
- [ ] No deletion is performed during planning or validation.
