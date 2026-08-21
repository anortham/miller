# CT Watermark Freshness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make CT behave like NCrunch for agents: an edit marks only reachable tests stale, a debounced auto-run executes only those, greens survive unrelated edits, and CT follows agents into worktrees.

**Architecture:** Fix the self-referential status key, replace the per-write freshness identity with a generation identity, wire the existing (tested, uncalled) watermark carry-forward using the impact selector's keep-set, narrow the stale set to the impacted set, and let the running daemon adopt family worktrees. Spec: `docs/plans/2026-08-21-ct-watermark-freshness-design.md` (approved 2026-08-21).

**Tech Stack:** .NET 10, SQLite (`ct.db` sidecar), xUnit.

**Architecture Quality:** Approved shape: freshness logic stays in `Miller.Testing` (`ContinuousTestDurableFreshness`, `ContinuousTestStore.Coverage`); identity intake stays at the single seam `CtFactAdapter`; `Miller.Core` stays I/O-free; no new MCP tools. Main risk: the watermark advance must be transactional with the staleness computation so a crash between them fails stale, never fresh.

## Global Constraints

- The design doc's "Safety invariants (unchanged from today)" section binds every task verbatim.
- Unknown reachability is always stale, never fresh. A red never becomes fresh without a rerun.
- A generation change (rebuild, promote, view/family change, extractor upgrade, schema heal) marks every result stale. This fail-safe is absolute.
- Revision alone is never a freshness key.
- `MILLER_CT=off` (`0`/`false`/`no`) remains a permanent zero-work guarantee, including all new code paths.
- Status reads never create `ct.db`, never create `.miller/ct/`, never start the daemon.
- Tests spawning `julie-extract` or a CT provider carry `[Trait("Category","Scale")]` and use `ScaleTestSupport` / `CtProviderTestSupport` locators (CLAUDE.md testing rules).
- Build must stay 0 warnings / 0 errors (warnings are errors).
- Every task follows TDD: failing test observed before implementation.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section) and `scripts/test.ps1`.

**Worker red/green scope:** the focused test class(es) covering the change: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Debug --filter "FullyQualifiedName~<TestClassName>"`. Run the whole class, not only the new tests.

**Worker ceiling:** focused classes only. Workers never run the fast or scale suite.

**Worker gate invariant:** each task's acceptance criteria list the behavior its focused tests must prove.

**Lead affected-change scope:** after each merged batch: `$env:CONFIG='Debug'; scripts/test.ps1` (fast suite, Debug — the session MCP server locks Release output in the main checkout; the worktree may use Release).

**Branch gate:** `scripts/test.ps1 all` (fast + scale) in the worktree, plus the live worktree-adoption scenario from the design's worktree acceptance criteria run against a real daemon.

**Security scope:** none declared.

**Replay/metric evidence:** the design's acceptance criteria are hard gates; suite wall-clock numbers are report-only.

**Escalation triggers:** touching `ContinuousTestStore` schema or migrations → run `CtSchemaTests` and `ContinuousTestStoreTests` in the worker scope; touching the provider spawn path → scale suite before merge.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Live-index status key | Batch A | Modify `src/Miller.Server/Tools/TestsCore.cs`; test `tests/Miller.Tests/Server/TestsCore*` | No | None - safe parallel batch. |
| Task 2: Worktree enablement inheritance | Batch A | Modify `src/Miller.Testing/Daemon/ContinuousTestPolicy.cs`; test `tests/Miller.Tests/Testing/Daemon/**` policy tests | No | None - safe parallel batch. |
| Task 3: Generation identity | None - serial | Modify `src/Miller.Indexing/Reads/WorkspaceReadSnapshot.cs`, `src/Miller.Indexing/Testing/CtFactAdapter.cs`, `src/Miller.Indexing/Testing/ICtFactSource.cs`; test `tests/Miller.Tests/Indexing/**` | Yes | Tasks 4-5 key freshness on the identity this task introduces. |
| Task 4: Watermark carry-forward | None - serial | Modify `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Store/ContinuousTestDurableFreshness.cs`, `src/Miller.Server/Tools/TestsCore.cs`, `src/Miller.Testing/Parsing/ContinuousTestStatusSummary.cs`; test `tests/Miller.Tests/Testing/Store/Coverage/DurableFreshnessTests.cs` + queue tests | Yes | Needs Task 3's identity; touches TestsCore after Task 1. |
| Task 5: Stale = impacted; runs execute stale | None - serial | Modify `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`, `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`; test `tests/Miller.Tests/Testing/**` selector + coordinator tests | Yes | Correct only once Task 4 carries unimpacted greens forward. |
| Task 6: Debounced auto-run | None - serial | Modify `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`; test queue/poller tests | Yes | Shares queue files with Task 4; meaningful only with narrow stale sets from Task 5. |
| Task 7: Daemon adopts family worktrees | None - serial | Modify `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`, `src/Miller.Testing/Daemon/CtDaemonLauncher.cs`, `src/Miller.Server/Tools/TestsCore.cs`; test daemon host tests | Yes | Builds on Task 2's inheritance; shares TestsCore with Tasks 1/4. |
| Task 8: CT delta seam family id | None - serial | Modify `src/Miller.Indexing/Testing/CtFactAdapter.cs`; test `tests/Miller.Tests/Indexing/**` | Yes | Shares `CtFactAdapter.cs` with Task 3. |
| Task 9: Live acceptance validation | None - serial | No production files; may add Scale tests under `tests/Miller.Tests/Testing/**` | Yes | Validates the composed behavior of all prior tasks. |

Commit mode: `parallel-lead-commit` for Batch A; `serial-worker-commit` for Tasks 3-9.

---

### Task 1: Live-index status key

**Files:**
- Modify: `src/Miller.Server/Tools/TestsCore.cs` (`SelectedFrom` at `:964`, call sites `:203`, `:849`)
- Test: existing TestsCore status tests (locate with Miller: `search TestsCore mode=symbol`, then the test class that covers status)

**Interfaces:**
- Consumes: `ICtFactSource.Cursor` (`src/Miller.Indexing/Testing/ICtFactSource.cs`) — the live index cursor.
- Produces: status/verdict computation whose selected key always comes from the live cursor, never from stored `ct.db` rows. Stored rows are compared against it.

**Contract inputs:** `CtFreshnessKey`, `ContinuousTestStatus`, `ContinuousTestDurableFreshness.IsCommittedFreshAt`.

**File ownership:** Modify `src/Miller.Server/Tools/TestsCore.cs`; test `tests/Miller.Tests/Server/TestsCore*`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** `SelectedFrom` derives the reported key from stored rows — a self-referential guard (uniform stale rows read green forever) and the cause of the live status flip (rev 32424 vs 32161 in consecutive reads). Make status take the selected key from the live fact-source cursor; stored rows are then judged against it.

**Approach:** The live cursor is already reachable where statuses are loaded (`:203`, `:849` pass a `freshness` fallback today — invert the precedence: live cursor first, stored rows never). Keep the "no index available" path honest: no cursor → no key → verdict `unknown`, not green.

**Acceptance criteria:**
- [ ] A set of stored rows at an old key with a newer live cursor reports stale, never green.
- [ ] Two consecutive status reads with no index writes between them report the same key.
- [ ] No cursor available → verdict `unknown`.
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 2: Worktree enablement inheritance

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestPolicy.cs` (`IsWorkspaceOptedIn`, `EnabledMarkerPath`)
- Test: policy tests under `tests/Miller.Tests/Testing/Daemon/` (locate the existing policy test class with Miller)

**Interfaces:**
- Consumes: `GitWorktreeLayout` (`src/Miller.Indexing/GitWorktreeLayout.cs`) — resolves a linked worktree's main repo without a `git` subprocess. NOTE: check project references first — if `Miller.Testing` cannot reference `Miller.Indexing`, port the minimal `.git`-file parse into a small internal helper rather than adding a project reference; record which way it went.
- Produces: `ContinuousTestPolicy.IsWorkspaceOptedIn(root)` returns true when the root itself is opted in OR the root is a linked worktree whose main checkout is opted in.

**Contract inputs:** `.miller/ct.enabled` marker semantics; a linked worktree's `.git` is a FILE with `gitdir:` (CLAUDE.md load-bearing note).

**File ownership:** Modify `src/Miller.Testing/Daemon/ContinuousTestPolicy.cs`; test `tests/Miller.Tests/Testing/Daemon/**` policy tests

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** A linked worktree of a CT-enabled repo counts as enabled. Explicit local state wins: a worktree with its own marker stays enabled; `MILLER_CT=off` still wins over everything.

**Approach:** Resolve the main checkout from the worktree's `.git` file; probe its `.miller/ct.enabled`. A non-git root or an unreadable link inherits nothing (fail closed). Disable on a worktree writes a local override the inheritance respects (a worktree-local `.miller/ct.disabled` tombstone beats the inherited enable; pick the exact marker shape to match existing marker conventions in `ContinuousTestPolicy`).

**Acceptance criteria:**
- [ ] Worktree of an enabled repo → opted in, with zero manual calls.
- [ ] Worktree of a never-enabled repo → off.
- [ ] `MILLER_CT=off` → off everywhere.
- [ ] A worktree-local disable beats the inherited enable.
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 3: Generation identity

**Files:**
- Modify: `src/Miller.Indexing/Reads/WorkspaceReadSnapshot.cs` (`IndexIdentity` at `:59`; add `IndexGenerationIdentity`)
- Modify: `src/Miller.Indexing/Testing/CtFactAdapter.cs` (`:45-49`, the only CT identity intake)
- Modify: `src/Miller.Indexing/Testing/ICtFactSource.cs` (`CtIndexCursor`) only if the cursor shape must carry both values
- Test: `tests/Miller.Tests/Indexing/` (existing snapshot/adapter tests; locate with Miller)

**Interfaces:**
- Consumes: `WorkspaceReadSnapshot` fields (family store cursor, artifact metadata).
- Produces: `WorkspaceReadSnapshot.IndexGenerationIdentity` — changes ONLY on: full rebuild/promote (`artifact_id` change), store view or family change, extractor upgrade, schema heal. Never includes `store_log_sequence` or the revision counter. `CtIndexCursor` becomes `(IndexGenerationIdentity, Freshness.Revision)` in both modes.

**Contract inputs:** Family-store identity components visible in `IndexIdentity` today (family id, view id, generation, manifest hash); legacy mode's `artifact_id`. Rebuild detection is by `artifact_id`/generation change, never by revision comparison alone (CLAUDE.md).

**File ownership:** Modify `src/Miller.Indexing/Reads/WorkspaceReadSnapshot.cs`, `src/Miller.Indexing/Testing/CtFactAdapter.cs`, `src/Miller.Indexing/Testing/ICtFactSource.cs`; test `tests/Miller.Tests/Indexing/**`

**Serialization required:** Yes

**Dependency reason:** Tasks 4-5 key freshness on the identity this task introduces.

**What to build:** The freshness identity that ignores routine writes. Today the family-store identity joins the whole cursor including `store_log_sequence` (moves on every write — six counts for one file save), and the family-mode cursor even uses `StoreLogSequence` as its revision. Both must go.

**Approach:** Build `IndexGenerationIdentity` from the stable generation components only. In `CtFactAdapter.Cursor`, return `(snapshot.IndexGenerationIdentity, snapshot.Freshness.Revision)` for both modes. Old `ct.db` rows recorded under the old identity string simply read stale once — acceptable, one-time cost, no migration needed (verify `CtSchema` has no identity-format assumption that breaks; if it does, report a plan mismatch rather than migrating ad hoc).

**Acceptance criteria:**
- [ ] A store write that changes no indexed file leaves the cursor identical.
- [ ] A file-change delta advances the cursor revision but not the identity.
- [ ] A simulated rebuild (artifact/generation change) changes the identity.
- [ ] Legacy (non-store) mode has the same three properties.
- [ ] Worker-scope verification passes; commit per commit mode.

### Task 4: Watermark carry-forward

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs` (`ObserveFreshRevision` and the impact-observation path)
- Modify: `src/Miller.Server/Tools/TestsCore.cs` + `src/Miller.Testing/Parsing/ContinuousTestStatusSummary.cs` (verdict/staleness join `IsWatermarkFreshAt`)
- Use (already exists, do not rewrite): `ContinuousTestStore.Coverage.cs:836` `AdvanceContinuousTestFreshWatermark`, `ContinuousTestDurableFreshness.cs:27` `IsWatermarkFreshAt`
- Test: `tests/Miller.Tests/Testing/Store/Coverage/DurableFreshnessTests.cs` (extend) + daemon queue tests

**Interfaces:**
- Consumes: Task 3's cursor; `ContinuousTestImpactSelector.Select` impacted set; `ContinuousTestImpactResult.ChangedPaths` from `ContinuousTestRevisionPoller`.
- Produces: on each revision advance R0→R1 with changed files F: keep-set = all currently fresh (committed OR watermark) GREEN cases not in impacted set I; watermark advanced to R1 for the keep-set; cases in I stale; unknown reachability (impact outcome degraded/unavailable) → advance nothing. Verdict/staleness treat watermark-fresh as fresh.

**Contract inputs:** Only greens ride the watermark (design). `ContinuousTestImpactOutcome` (`ContinuousTestRevisionPoller.cs:25-30`) signals delta availability; delta-unavailable advances nothing and never triggers a full run.

**File ownership:** Modify `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Store/ContinuousTestDurableFreshness.cs`, `src/Miller.Server/Tools/TestsCore.cs`, `src/Miller.Testing/Parsing/ContinuousTestStatusSummary.cs`; test `DurableFreshnessTests.cs` + queue tests

**Serialization required:** Yes

**Dependency reason:** Needs Task 3's identity; touches TestsCore after Task 1.

**What to build:** The production caller for the watermark machinery that exists with zero callers. This is the heart of the plan.

**Approach:** The daemon queue already observes fresh revisions and impact results per workspace. At that observation point, compute the keep-set and advance watermarks in the same store transaction that records the new staleness; a crash between the two must leave cases stale, never fresh. Chained edits: a second unrelated change keeps carrying what the first kept (watermark-fresh cases re-enter the keep-set).

**Acceptance criteria:**
- [ ] Unrelated change (no impacted overlap): all greens watermark to R1; verdict stays green; stale count 0.
- [ ] Impacted change: impacted cases stale; unimpacted greens fresh; reds stay red and never watermark.
- [ ] Two chained unrelated changes: greens fresh at R2 via watermark alone.
- [ ] Delta-unavailable: nothing advances; all previously committed-fresh cases go stale (fail-safe).
- [ ] Generation change: watermark ignored; everything stale.
- [ ] Worker-scope verification passes; commit per commit mode.

### Task 5: Stale = impacted; runs execute the stale set

**Files:**
- Modify: `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs` (`Select` at `:84-164`, staleIds at `:97-101`)
- Modify: `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs` (run selection consumes the stale set)
- Test: selector tests + coordinator tests under `tests/Miller.Tests/Testing/`

**Interfaces:**
- Consumes: Task 4's watermark semantics (stale = not committed-fresh and not watermark-fresh at the selected key).
- Produces: `ContinuousTestSelectionResult.StaleTestCaseIds` = the impacted set (plus already-stale cases), never blanket-everything; explicit `tests run` and auto-runs execute the current stale set only. `verdict=green` still requires zero stale + zero red at the selected key — unchanged definition.

**Contract inputs:** `WorkspaceScope` requests (true workspace-wide events like generation change) legitimately stale everything — keep that branch.

**File ownership:** Modify `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`, `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`; test selector + coordinator tests

**Serialization required:** Yes

**Dependency reason:** Correct only once Task 4 carries unimpacted greens forward.

**What to build:** Remove the `staleIds = every case` arm for path-scoped changes. A 3-file edit must produce a stale set of dozens, not 7,690.

**Acceptance criteria:**
- [ ] Path-scoped change: stale set == impacted set ∪ previously stale cases.
- [ ] WorkspaceScope request still stales everything.
- [ ] `tests run` executes exactly the stale set.
- [ ] Green definition unchanged (zero stale + zero red at selected key).
- [ ] Worker-scope verification passes; commit per commit mode.

### Task 6: Debounced auto-run

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`
- Test: queue/poller tests

**Interfaces:**
- Consumes: Task 5's narrow stale set.
- Produces: after a change lands and the debounce quiet period elapses, the daemon runs the stale set automatically. `MILLER_CT_DEBOUNCE` (seconds) tunes the quiet period; default in low single digits chosen against the poller cadence and recorded in the code and the tests. Changes during an executing run queue a follow-up selection; they never kill a healthy run.

**Contract inputs:** Live dogfood 2026-08-20 observed NO auto-run after edits while the daemon was running-idle — first diagnose why (razorback:systematic-debugging; the poller observed the revision, so the break is between observation and execution), then build the debounce on the fixed path. The documented contract (CLAUDE.md: "executes nothing until a new change or an explicit run") already promises change-triggered execution.

**File ownership:** Modify `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`; test queue/poller tests

**Serialization required:** Yes

**Dependency reason:** Shares queue files with Task 4; meaningful only with narrow stale sets from Task 5.

**What to build:** The NCrunch loop: edit → quiet period → impacted tests run → verdict updates. Coalesce save bursts (timer resets on each new change).

**Acceptance criteria:**
- [ ] The root cause of the missing auto-run is identified and stated in the task report.
- [ ] A change triggers exactly one run after the quiet period; a burst of changes triggers one run.
- [ ] A change during execution queues a follow-up; the running suite is not killed.
- [ ] `MILLER_CT_DEBOUNCE` honored; `off`-style values are NOT accepted (debounce 0 = immediate, but auto-run itself is governed by CT enablement, not the debounce).
- [ ] Worker-scope verification passes; commit per commit mode.

### Task 7: Daemon adopts family worktrees

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs` (`:90-136`, single-workspace host today)
- Modify: `src/Miller.Testing/Daemon/CtDaemonLauncher.cs`
- Modify: `src/Miller.Server/Tools/TestsCore.cs` (status on a worktree workspace reports adopted state)
- Test: daemon host tests under `tests/Miller.Tests/Testing/Daemon/`

**Interfaces:**
- Consumes: Task 2's inheritance (`ContinuousTestPolicy.IsWorkspaceOptedIn`); the workspace registry (`~/.miller/workspaces.db`) as the discovery surface for family worktrees; `GitWorktreeLayout` (or the Task 2 helper) for "same repo" resolution.
- Produces: a running daemon serves every registered worktree of its repo: per-worktree revision polling, selection, staleness, and runs keyed to that worktree's own index and `ct.db`. Adoption is dynamic — a worktree registered after daemon start is picked up on the next poll cycle; a removed worktree detaches cleanly.

**Contract inputs:** The queue already keys observations by `WorkspaceId` (`ContinuousTestRevisionObservation`). The user-global execution budget is shared: at most one workspace executes at a time, worktrees included. The per-workspace daemon lease (`CtDaemonLease`) must not double-serve: if a worktree somehow runs its own daemon, the family daemon must not adopt it.

**File ownership:** Modify `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`, `src/Miller.Testing/Daemon/CtDaemonLauncher.cs`, `src/Miller.Server/Tools/TestsCore.cs`; test daemon host tests

**Serialization required:** Yes

**Dependency reason:** Builds on Task 2's inheritance; shares TestsCore with Tasks 1/4.

**What to build:** The host grows from one workspace to a dynamic set: the repo's main root plus its registered, opted-in worktrees. One process, one lease (held on the main root), N polled workspaces.

**Approach:** On each poll cycle, enumerate registered workspaces whose root resolves to the same main repo and which pass `IsWorkspaceOptedIn`; attach/detach pollers accordingly. Missing roots (removed worktrees) detach silently. Status for an adopted worktree names the serving daemon.

**Acceptance criteria:**
- [ ] Daemon running on main + a registered enabled-by-inheritance worktree → the worktree gets status/selection/runs with zero manual calls beyond `workspace open`.
- [ ] A change in the worktree triggers a debounced impacted run against the worktree's index under the shared budget.
- [ ] A never-enabled repo's worktree is not adopted.
- [ ] Removing the worktree detaches it; the main workspace's CT state is untouched.
- [ ] Worker-scope verification passes; commit per commit mode.

### Task 8: CT delta seam family id

**Files:**
- Modify: `src/Miller.Indexing/Testing/CtFactAdapter.cs` (do NOT touch `RevisionDeltaReader.cs`)
- Test: `tests/Miller.Tests/Indexing/` adapter tests

**Interfaces:**
- Consumes: Task 3's cursor/identity shape.
- Produces: the CT-side delta read passes the family id so delta availability is judged against the right family (step 4 of the 2026-08-20 plan, quoted in the design).

**Contract inputs:** `docs/plans/2026-08-20-ct-dogfood-defects-and-2344-pin.md` step 4 under Decision 1's "order" list.

**File ownership:** Modify `src/Miller.Indexing/Testing/CtFactAdapter.cs`; test `tests/Miller.Tests/Indexing/**`

**Serialization required:** Yes

**Dependency reason:** Shares `CtFactAdapter.cs` with Task 3.

**What to build:** The contained delta-seam fix so CT's changed-path reads stay correct in family-store mode.

**Acceptance criteria:**
- [ ] CT delta reads carry the family id; a family-store workspace's changed-path query returns that workspace's delta, not a cross-family misread.
- [ ] `RevisionDeltaReader.cs` untouched.
- [ ] Worker-scope verification passes; commit per commit mode.

### Task 9: Live acceptance validation

**Files:**
- Create (if gaps found): Scale tests under `tests/Miller.Tests/Testing/` tagged `[Trait("Category","Scale")]` using `CtProviderTestSupport.RequireDotnet()`
- No production files.

**Interfaces:**
- Consumes: everything above.
- Produces: the design doc's two acceptance checklists ticked with evidence, or precise defect reports.

**Contract inputs:** Design acceptance criteria (both lists) are the hard gate. Live scenario: enabled workspace, daemon running, edit one source file → only impacted tests run after debounce → verdict green with no full run; edit an unrelated markdown file → verdict stays green, stale 0.

**File ownership:** No production files; may add Scale tests under `tests/Miller.Tests/Testing/**`

**Serialization required:** Yes

**Dependency reason:** Validates the composed behavior of all prior tasks.

**What to build:** Run the NCrunch litmus scenarios against a real daemon in the worktree; tick each design acceptance criterion with evidence (status outputs, `ct.db` queries). Any failure is a defect report back into the relevant task, not a criteria edit.

**Acceptance criteria:**
- [ ] Every design acceptance criterion (freshness list + worktree list) ticked with recorded evidence.
- [ ] Branch gate (`scripts/test.ps1 all`) green in the worktree.
- [ ] Verification ledger complete for the branch HEAD.
