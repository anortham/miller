# Worktree View Retirement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Retire the exact producer view whenever Miller removes or prunes a family-store workspace, remind agents to perform targeted cleanup after worktree removal, and remove the nine recorded missing-root views from the Miller family.

**Architecture:** Reuse `StoreSidecarReclaimTarget` as the authoritative pre-delete family/view capture. A new indexing adapter invokes the pinned extractor's existing `store maintain retire-view` command and validates its JSON report. Workspace removal and prune refuse to delete registry membership when producer retirement fails; existing sidecar reclaim and coordinator maintenance run only after retirement succeeds.

**Tech Stack:** .NET 10, C#, Microsoft.Data.Sqlite, pinned `julie-extract`, Node hook tests, xUnit.

**Architecture Quality:** Existing `workspace remove|prune|list` remain the caller-facing interfaces. Process details stay in `Miller.Indexing`; orchestration stays in the workspace lifecycle. Architecture risk is medium because producer/registry deletion ordering is load-bearing.

## Global Constraints

- No new MCP tool, workspace operation, dependency, startup subprocess, automatic prune, timeout-based result cut, or direct write to producer SQLite tables.
- The view id always comes from `store_members` before registry deletion. Runtime code never rediscovers it by listing producer views or hashing a root.
- Session hooks remain static, injection-only, fail-open, and disabled by `MILLER_SESSION_HOOKS=0`.
- `workspace list` JSON remains byte-compatible; missing-root guidance is compact-only.
- Dry-run prune performs no producer, registry, sidecar, or maintenance writes.
- Producer retirement occurs before registry deletion. A retirement failure leaves the member registered and reports the exact error.
- `view_not_found` is safe only for the exact captured family/view target and normalizes to `AlreadyAbsent`.
- Existing sidecar `.reclaim-owed` handling and producer coordinator maintenance remain intact.
- The one-time cleanup is limited to the nine audited missing-root view ids in family `a271f2bd-7368-4da6-b5aa-24ffad69fb1f`.
- The main checkout, `tool-latency-health`, and the present `ct-dogfood-round2` root are never retirement targets.
- Tests contain no narration comments. Production changes add no narration comments.
- TDD is mandatory: every behavior test fails for the intended missing behavior before production changes.

## Verification Strategy

**Project source of truth:** `AGENTS.md` testing, build, store-sidecar, and release sections.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<AssignedTestClass>"` and `node --test tests/plugin/hooks-session-hook.test.cjs` for the assigned hook packet.

**Worker ceiling:** Assigned focused classes plus `dotnet build <directly changed project> -c Release --no-restore`. Workers do not run the bare fast suite, full Scale suite, security scans, or destructive cleanup.

**Worker gate invariant:** Adapter tests prove command shape/report validation; orchestration tests prove retirement-before-delete and retry preservation; guidance tests prove static delivery and compact-only rendering.

**Lead affected-change scope:** Run the union of adapter, workspace removal/prune/render, and hook tests, then `dotnet build Miller.slnx -c Release --no-restore` once after code tasks land.

**Branch gate:** Bare `dotnet test` once, then `dotnet build Miller.slnx -c Release --no-restore` once on the final source tree.

**Security scope:** `gitleaks detect` for `security-secrets`; `dotnet list Miller.slnx package --vulnerable --include-transitive` for `security-deps`.

**Replay/metric evidence:** Hard gates are zero live-view retirement, exact view counts before/after cleanup, no current-root changes, and successful targeted retirement. Cleanup duration and reclaimed bytes are report-only.

**Escalation triggers:** Because producer store lifecycle and `Miller.Indexing` change, run `scripts/test.sh scale`. Any real test that launches `julie-extract` is Scale-tagged and uses `ScaleTestSupport.RequireJulieServer()`.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the task owns that contract.

**Verification ledger:** Record invariant, command, scope, commit SHA, result, and timestamp under this plan's `.razorback/sdd` workspace. Reuse only same-source-tree passing evidence.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Producer retirement adapter | Batch A | Create `src/Miller.Indexing/Store/StoreViewRetirementRunner.cs`; create `tests/Miller.Tests/Indexing/StoreViewRetirementRunnerTests.cs` | No | None - safe parallel batch. |
| Task 2: Workspace removal and prune orchestration | Batch B | Modify `src/Miller.Server/Workspaces/WorkspaceRemoval.cs`; modify `src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs`; modify `src/Miller.Server/Tools/WorkspaceRender.cs`; modify `src/Miller.Server/Tools/WorkspaceTool.cs`; modify `src/Miller.Server/Cli/CliDispatch.cs`; modify `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs`; test `tests/Miller.Tests/Server/WorkspaceRemovalTests.cs`; test `tests/Miller.Tests/Server/WorkspaceRegistryPruneTests.cs`; test `tests/Miller.Tests/Server/WorkspaceToolPruneTests.cs`; test `tests/Miller.Tests/Server/WorkspaceToolTests.cs`; test `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` | Yes | Depends on Task 1's exact adapter/outcome contract. |
| Task 3: Hook lifecycle guidance | Batch A | Modify `hooks/miller-routing-block.md`; test `tests/plugin/hooks-session-hook.test.cjs` | No | None - safe parallel batch. |
| Task 4: Targeted stale-view cleanup and evidence | None - serial | Create `docs/findings/2026-08-28-miller-stale-view-cleanup.md`; external state limited to the audited Miller family store, its registry rows, and per-view sidecars | Yes | Runs only after Tasks 1-3, affected-change verification, and a clean intentional source tree. |

Batch A uses `parallel-lead-commit`. Task 2 owns the compact missing-root hint with the rest of workspace rendering. Task 4 is lead-owned destructive maintenance under the user's explicit approval.

### Task 1: Producer retirement adapter

**Files:**
- Create: `src/Miller.Indexing/Store/StoreViewRetirementRunner.cs`
- Create: `tests/Miller.Tests/Indexing/StoreViewRetirementRunnerTests.cs`

**Interfaces:**
- Consumes: `StoreSidecarReclaimTarget(Guid FamilyId, string ViewId, string StoreRoot)` and an explicit pinned extractor path.
- Produces: `StoreViewRetirementRunner.Run(binaryPath, target, apply, timeout)` plus `ForToolsRoot`; `StoreViewRetirementOutcome` records `Planned|Retired|AlreadyAbsent|Failed`, exact family/view identity, retired counts, and error text.

**Contract inputs:** `julie-extract store maintain retire-view --store <root> --family <uuid> --view <id> [--apply] --json`; report schema 1; `disposition=planned` for preview; `counts.retired_views`; `view_not_found` normalization for the exact captured target.

**File ownership:** Create `src/Miller.Indexing/Store/StoreViewRetirementRunner.cs`; create `tests/Miller.Tests/Indexing/StoreViewRetirementRunnerTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Add the narrow producer-process adapter beside `StoreMaintenanceRunner`. Parse and validate the full identity/action/mode/disposition contract, bound execution, kill timed-out children, and never throw for expected process/I/O/report failures.

**Approach:** Follow `StoreMaintenanceRunner` process construction and test seams. Keep preview and apply explicit. Treat exact-target `view_not_found` as `AlreadyAbsent`; reject family/view mismatches, malformed reports, other failure classes, and missing binaries.

**Acceptance criteria:**
- [ ] RED tests prove no retirement adapter exists and malformed/wrong-identity reports cannot be accepted.
- [ ] Preview omits `--apply`; apply includes it; every argv includes exact store, family, view, and `--json`.
- [ ] Planned, retired, already-absent, mismatch, nonzero-exit, malformed-report, and timeout outcomes are deterministic.
- [ ] The adapter never writes producer SQLite directly and never derives a view id from a root.
- [ ] Focused adapter tests pass and `Miller.Indexing`/`Miller.Tests` build cleanly.
- [ ] Worker hands the verified diff to the lead without committing.

### Task 2: Workspace removal and prune orchestration

**Files:**
- Modify: `src/Miller.Server/Workspaces/WorkspaceRemoval.cs`
- Modify: `src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceRender.cs`
- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs`
- Modify: `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceRemovalTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceRegistryPruneTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceToolPruneTests.cs`
- Test: `tests/Miller.Tests/Server/WorkspaceToolTests.cs`
- Test: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Interfaces:**
- Consumes: Task 1 `StoreViewRetirementRunner.ForToolsRoot` callback and `StoreSidecarReclaimTarget.Capture`.
- Produces: removal/prune ordering that previews/applies retirement before registry deletion and reports per-target retirement failures without losing membership.

**Contract inputs:** Existing protected-current, sensitive-root, in-use, registry-path, sidecar reclaim, maintenance, CLI exit-code, MCP byte-budget, and dashboard antiforgery contracts.

**File ownership:** Modify `src/Miller.Server/Workspaces/WorkspaceRemoval.cs`; modify `src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs`; modify `src/Miller.Server/Tools/WorkspaceRender.cs`; modify `src/Miller.Server/Tools/WorkspaceTool.cs`; modify `src/Miller.Server/Cli/CliDispatch.cs`; modify `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs`; test `tests/Miller.Tests/Server/WorkspaceRemovalTests.cs`; test `tests/Miller.Tests/Server/WorkspaceRegistryPruneTests.cs`; test `tests/Miller.Tests/Server/WorkspaceToolPruneTests.cs`; test `tests/Miller.Tests/Server/WorkspaceToolTests.cs`; test `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Serialization required:** Yes

**Dependency reason:** Depends on Task 1's exact adapter/outcome contract.

**What to build:** Inject retirement through every remove/prune caller. Capture once, preview/apply the producer target, delete registry membership only after success/already-absent, then preserve existing owed-sidecar reclaim and store maintenance ordering. Add the compact-only dry-run prune next step when workspace list reports missing roots.

**Approach:** Add an explicit refused retirement outcome for targeted removal and per-entry retirement failures for prune. Dry-run collects preview facts only. Compact/JSON rendering remains bounded and honest; existing JSON fields remain and new retirement facts are additive only where removal/prune already returns lifecycle results.

**Acceptance criteria:**
- [ ] RED tests prove current remove/prune deletes registry membership without producer retirement.
- [ ] Exact captured family/view retirement precedes every family-member registry deletion.
- [ ] Preview/apply failure keeps the member and producer view retriable; no sidecar reclaim or maintenance runs for that target.
- [ ] `AlreadyAbsent` proceeds safely; non-store workspaces preserve current behavior.
- [ ] Dry-run performs preview only and reports no mutation.
- [ ] Current, sensitive, invalid, and in-use targets still refuse before producer work.
- [ ] MCP, CLI, and dashboard call sites pass the pinned adapter and render actionable failure output within budgets.
- [ ] Missing-root compact list output gives a dry-run prune next step; zero-missing output does not; list JSON remains byte-identical.
- [ ] Focused removal, prune, workspace-tool, and CLI tests pass; `Miller.Server`, `Miller.Dashboard`, and `Miller.Tests` build cleanly. Miller impact reports no dashboard endpoint test candidate, so the dashboard build is its direct gate.
- [ ] Worker hands the verified diff to the lead without committing.

### Task 3: Hook lifecycle guidance

**Files:**
- Modify: `hooks/miller-routing-block.md`
- Test: `tests/plugin/hooks-session-hook.test.cjs`

**Interfaces:**
- Consumes: existing static SessionStart/SubagentStart injection.
- Produces: one lifecycle rule in hook guidance.

**Contract inputs:** Hook opt-out, event-specific JSON envelope, and routing-block budget/copies.

**File ownership:** Modify `hooks/miller-routing-block.md`; test `tests/plugin/hooks-session-hook.test.cjs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Add the targeted post-worktree-removal rule to the static routing block.

**Approach:** Keep the rule one line and actionable. Do not run registry discovery from the hook. Assert both hook events include it and opt-out omits it.

**Acceptance criteria:**
- [x] RED tests prove neither hook event currently carries worktree cleanup guidance.
- [x] SessionStart and SubagentStart deliver the targeted remove/prune rule within the existing budget.
- [x] Hook tests pass and the routing block remains within its enforced budget.
- [x] Worker hands the verified diff to the lead without committing.

### Task 4: Targeted stale-view cleanup and evidence

**Files:**
- Create: `docs/findings/2026-08-28-miller-stale-view-cleanup.md`
- External state: family `a271f2bd-7368-4da6-b5aa-24ffad69fb1f`, its nine audited missing-root views, matching stale registry members, and exact per-view sidecars

**Interfaces:**
- Consumes: verified branch CLI removal/prune behavior, pinned extractor preview/apply, captured before inventory.
- Produces: exact before/after finding with retired view ids, counts, durations, reclaimed sidecars, registry outcomes, and surviving views.

**Contract inputs:** Nine view ids and roots captured from the pre-cleanup store inventory; user approval in this session; main/task/ct-dogfood exclusion set.

**File ownership:** Create `docs/findings/2026-08-28-miller-stale-view-cleanup.md`; external state limited to the audited Miller family store, its registry rows, and per-view sidecars

**Serialization required:** Yes

**Dependency reason:** Runs only after Tasks 1-3, affected-change verification, and a clean intentional source tree.

**What to build:** Record a fresh read-only inventory and preview all nine exact retirements. Apply them one at a time, using targeted branch CLI removal for registered members and exact pinned-extractor retirement for the two producer-only legacy views. Reclaim exact sidecars, run producer GC once after the last retirement, and record final inventory.

**Approach:** Stop immediately on any family/view mismatch, unexpected live root, preview failure, apply failure, or current-view selection. Never run broad `workspace prune` for the one-time cleanup. Verify the three excluded present roots before and after each destructive phase.

**Acceptance criteria:**
- [ ] The finding records 12 producer views, 2 active Git worktrees, 9 missing-root views, and the excluded present-root inventory before mutation.
- [ ] Every retirement preview matches the captured family/view and reports exactly one planned retirement.
- [ ] All nine missing-root producer views are absent afterward; no excluded view is changed.
- [ ] Matching stale registry members and per-view sidecars are removed; producer-only legacy views are documented separately.
- [ ] Producer GC completes or reports an actionable retained-work reason.
- [ ] Final inventory and an impact replay quantify the cleanup effect.
- [ ] Branch and external-state verification evidence is committed with the finding.

## Lead Integration and Completion

- Review every worker report for Miller-first evidence, TDD RED/GREEN proof, exact process/API shapes, and worktree state.
- Commit exact owned paths after each inline review and record real SHAs in the plan ledger.
- Run affected focused tests and one Release solution build after Tasks 1-3.
- Run Task 4 only from the verified clean branch state and capture fresh before/after identities.
- Update this plan and the design acceptance checkboxes with exact verification evidence.
- Run fast, Scale, Release, secrets, dependency, and final worktree gates after cleanup evidence lands.

## Plan Acceptance Criteria

- [ ] Tasks 1-3 are TDD-complete, reviewed, and committed.
- [ ] Workspace remove/prune retires producer views before registry deletion and preserves failures for retry.
- [ ] Hook and compact-list guidance prevents and exposes missed cleanup without startup mutation.
- [ ] Nine audited missing-root views and their reclaimable per-view state are removed; excluded present views survive.
- [ ] Fast, Scale, Release, secrets, dependency, and worktree gates pass.
- [ ] Design, plan, and cleanup finding carry completed evidence.
