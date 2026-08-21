# CT Watermark Freshness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make CT behave like NCrunch for agents: an edit marks only reachable tests stale, a debounced auto-run executes only those, greens survive unrelated edits, and CT follows agents into worktrees.

**Architecture:** Fix the freshness identity at BOTH intakes (adapter and poller), fix the self-referential status key, define the impacted/stale contract, wire the watermark carry-forward as one crash-atomic store operation, then let the running daemon adopt family worktrees. Spec: `docs/plans/2026-08-21-ct-watermark-freshness-design.md` (approved 2026-08-21). This revision incorporates the 2026-08-21 Codex plan review (12 findings, all accepted; the two load-bearing factual claims were independently verified against the code).

**Tech Stack:** .NET 10, SQLite (`ct.db` sidecar), xUnit.

**Architecture Quality:** Approved shape: freshness logic stays in `Miller.Testing`; identity intake is reworked at BOTH seams (`CtFactAdapter` and the poller's `MillerArtifactRevisionSource`/`MillerFactImpactSource`) into one cursor type carrying generation identity, revision, and family/artifact id; `Miller.Core` stays I/O-free; no new MCP tools. Main risk: the watermark advance and the staleness write must be ONE store transaction so a crash between them fails stale, never fresh.

## Global Constraints

- The design doc's "Safety invariants (unchanged from today)" section binds every task verbatim.
- Unknown, degraded, truncated, or unavailable reachability is always stale, never fresh, and never triggers a full-suite fallback run.
- Only GREEN results ride the watermark. A red or skipped result never becomes watermark-fresh (the existing SQL at `ContinuousTestStore.Coverage.cs:867` carries red/skipped and must be fixed, not reused as-is).
- A generation change (rebuild, promote, view/family change, extractor upgrade, schema heal) marks every result stale. This fail-safe is absolute.
- Revision alone is never a freshness key.
- `MILLER_CT=off` (`0`/`false`/`no`) remains a permanent zero-work guarantee, including all new code paths — checked BEFORE any registry or filesystem access.
- Status reads never create `ct.db`, never create `.miller/ct/`, never start the daemon, and never create registry directories/schema (`WorkspaceRegistry.Open` creates — use a non-creating read path).
- Tests spawning `julie-extract` or a CT provider carry `[Trait("Category","Scale")]` and use `ScaleTestSupport` / `CtProviderTestSupport` locators (CLAUDE.md testing rules).
- Build must stay 0 warnings / 0 errors (warnings are errors).
- Every task follows TDD: failing test observed before implementation.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section) and `scripts/test.ps1`.

**Worker red/green scope:** the focused test class(es) covering the change: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Debug --filter "FullyQualifiedName~<TestClassName>"`. Run the whole class, not only the new tests.

**Worker ceiling:** focused classes only. Workers never run the fast or scale suite.

**Worker gate invariant:** each task's acceptance criteria list the behavior its focused tests must prove.

**Lead affected-change scope:** after each task lands: `$env:CONFIG='Debug'; scripts/test.ps1` (fast suite; the worktree may use Release).

**Branch gate:** `scripts/test.ps1 all` (fast + scale) in the worktree, plus the live scenarios in Task 9.

**Security scope:** none declared.

**Replay/metric evidence:** the design's acceptance criteria are hard gates; suite wall-clock numbers are report-only.

**Escalation triggers:** touching `ContinuousTestStore` schema or migrations → run `CtSchemaTests` and `ContinuousTestStoreTests` in the worker scope; touching the provider spawn path → scale suite before merge.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Parallel Execution Contract

All tasks are serial: the freshness cursor (Task 1) feeds everything, `TestsCore.cs` is touched by Tasks 2, 3, and 4, and the daemon queue by Tasks 5 and 6. Commit mode: `serial-worker-commit` for every task.

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Generation-identity cursor at both intakes | None - serial | Modify `src/Miller.Indexing/Reads/WorkspaceReadSnapshot.cs`, `src/Miller.Indexing/Testing/CtFactAdapter.cs`, `src/Miller.Indexing/Testing/ICtFactSource.cs`, `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`; test `tests/Miller.Tests/Testing/FactAdapter/CtFactAdapterTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestRevisionPollerTests.cs`, `tests/Miller.Tests/Indexing/**` | Yes | Every later task keys freshness on this cursor. |
| Task 2: Live status projection | None - serial | Modify `src/Miller.Server/Tools/TestsCore.cs`, `src/Miller.Testing/Parsing/ContinuousTestStatusSummary.cs`, `src/Miller.Testing/Contracts/ContinuousTestingModels.cs`; test TestsCore + summary tests | Yes | Consumes Task 1's cursor; Task 5 extends the projection with watermark data. |
| Task 3: Worktree enablement + tombstone | None - serial | Modify `src/Miller.Testing/Daemon/ContinuousTestPolicy.cs`, `src/Miller.Server/Tools/TestsCore.cs` (Enable/Disable ops); test policy + TestsCore enable/disable tests | Yes | Shares `TestsCore.cs` with Task 2. |
| Task 4: Impacted/stale contract | None - serial | Modify `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs` (selection shaping), `src/Miller.Testing/ContinuousTestCoordinator.cs`, `src/Miller.Server/Tools/TestsCore.cs` (run op); test selector + queue + coordinator tests | Yes | Task 5's keep-set is the complement of this contract's impacted set. |
| Task 5: Crash-atomic watermark | None - serial | Modify `src/Miller.Testing/Store/ContinuousTestStore.Coverage.cs`, `src/Miller.Testing/Store/ContinuousTestDurableFreshness.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs` (verdict path); test `tests/Miller.Tests/Testing/Store/Coverage/DurableFreshnessTests.cs` + queue tests | Yes | Needs Tasks 1, 2, 4. Shares queue files with Task 4. |
| Task 6: Debounced auto-run | None - serial | Modify `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`; test queue/poller tests | Yes | Shares queue files with Tasks 4-5; meaningful only with narrow stale sets. |
| Task 7: Daemon adopts family worktrees | None - serial | Modify `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`, `src/Miller.Testing/Daemon/CtDaemonLauncher.cs`, `src/Miller.Testing/Daemon/CtDaemonProtocol.cs`, `src/Miller.Server/Tools/TestsCore.cs`; test daemon host/protocol tests | Yes | Builds on Task 3; shares TestsCore and host files with earlier tasks. |
| Task 8: Docs and contracts | None - serial | Modify `CLAUDE.md`, `docs/contracts/tests-cli-v1.md`, regenerate `AGENTS.md` via `scripts/sync-agents.ps1` | Yes | Documents the semantics Tasks 1-7 shipped. |
| Task 9: Live acceptance validation | None - serial | No production files; may add Scale tests under `tests/Miller.Tests/Testing/**` | Yes | Validates the composed behavior of all prior tasks. |

---

### Task 1: Generation-identity cursor at both intakes

**Files:**
- Modify: `src/Miller.Indexing/Reads/WorkspaceReadSnapshot.cs` (`IndexIdentity` at `:59`; add `IndexGenerationIdentity`)
- Modify: `src/Miller.Indexing/Testing/ICtFactSource.cs` (`CtIndexCursor` grows to carry generation identity, revision, and family/artifact id)
- Modify: `src/Miller.Indexing/Testing/CtFactAdapter.cs` (`:45-49`)
- Modify: `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs` (`MillerArtifactRevisionSource.RefreshAsync` `:304-320` — the SECOND identity intake; `MillerFactImpactSource.ImpactAsync` `:364-380` — identity compare and the `RevisionDeltaReader.Read(session, revision, identity)` call at `:377` gets the family id; `RevisionDeltaReader.cs` itself stays untouched if its signature already accepts what the family id needs — if it does not, report a plan mismatch rather than changing it ad hoc)
- Test: `tests/Miller.Tests/Testing/FactAdapter/CtFactAdapterTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestRevisionPollerTests.cs`, snapshot tests under `tests/Miller.Tests/Indexing/`

**Interfaces:**
- Consumes: `WorkspaceReadSnapshot` fields (family store cursor, artifact metadata, `Freshness.Revision`).
- Produces: `WorkspaceReadSnapshot.IndexGenerationIdentity` — a version-prefixed string (e.g. `ctgen1:` prefix so no legacy `ct.db` identity can ever collide with it) composed field-by-field from ONLY: mode marker, family id, view id, store generation, manifest hash (family mode) / artifact_id + hash_algorithm (legacy mode). EXCLUDED by specification: `store_log_sequence`, revision, freshness level, resolution_state, and any per-write counter. `CtIndexCursor` becomes `(GenerationIdentity, Revision: Freshness.Revision, FamilyId)` in both modes and BOTH intakes build it the same way through one shared helper — no second hand-rolled construction may remain.

**Contract inputs:** Rebuild detection is by artifact/generation change, never revision comparison (CLAUDE.md). Old `ct.db` rows carry the legacy identity format; the prefix guarantees they read stale once and never match again — no migration, but `CtSchema.cs:202` must be checked for identity-format assumptions; a real conflict is a plan-mismatch report, not an ad-hoc migration.

**File ownership:** Modify `src/Miller.Indexing/Reads/WorkspaceReadSnapshot.cs`, `src/Miller.Indexing/Testing/CtFactAdapter.cs`, `src/Miller.Indexing/Testing/ICtFactSource.cs`, `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`; test `tests/Miller.Tests/Testing/FactAdapter/CtFactAdapterTests.cs`, `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestRevisionPollerTests.cs`, `tests/Miller.Tests/Indexing/**`

**Serialization required:** Yes

**Dependency reason:** Every later task keys freshness on this cursor.

**What to build:** The freshness identity that ignores routine writes, installed at BOTH intakes. Today the adapter (`CtFactAdapter.cs:45-49`) and the poller (`ContinuousTestRevisionPoller.cs:307-310`) independently build keys from the whole-cursor identity and `StoreLogSequence`; both must use the new shared cursor construction.

**Approach:** One static helper (e.g. on `CtIndexCursor`) builds the cursor from a snapshot; adapter and poller call it. The poller's rebuild detection (`_lastIdentity` compare at `:311-313`) switches to comparing generation identity — a routine write no longer reads as a rebuild, a promote still does.

**Acceptance criteria:**
- [x] A store write that changes no indexed file leaves the cursor of BOTH intakes identical.
- [x] A file-change delta advances the cursor revision but not the identity, at both intakes.
- [x] A simulated rebuild changes the identity; the poller reports `Rebuild: true` for it and does NOT for a routine write.
- [x] Same numeric revision under two different generations never compares fresh.
- [x] Legacy-format identity strings can never equal a new-format identity (prefix test).
- [x] Legacy (non-store) mode has the same properties.
- [x] Worker-scope verification passes; commit per commit mode. (dec28c1c; family-mode identity = family+view+generation-name per live-store evidence)

### Task 2: Live status projection

**Files:**
- Modify: `src/Miller.Server/Tools/TestsCore.cs` (`SelectedFrom` `:964` and call sites `:203`, `:849`; the `"unspecified", 0` sentinel at `:1129`)
- Modify: `src/Miller.Testing/Parsing/ContinuousTestStatusSummary.cs` (`:88` area — aggregate verdict)
- Modify: `src/Miller.Testing/Contracts/ContinuousTestingModels.cs` (`ContinuousTestFreshness` `:71-…`) as needed for the projection shape
- Test: TestsCore status tests + `ContinuousTestStatusSummary` tests (locate the existing classes with Miller)

**Interfaces:**
- Consumes: Task 1's `CtIndexCursor`.
- Produces: ONE status projection that takes the live cursor plus stored rows and yields verdict/staleness. Foreground status (`TestsCore`), daemon evaluation (`ContinuousTestDaemonHost.cs:563` — consumed in Task 5), and summaries all use it. No live cursor → verdict `unknown`; the `"unspecified", 0` sentinel is removed. Stored rows never supply the selected key.

**Contract inputs:** `ContinuousTestDurableFreshness.IsCommittedFreshAt`; Task 5 later extends the projection with `IsWatermarkFreshAt` — design the projection signature to accept watermark rows from the start (empty until Task 5).

**File ownership:** Modify `src/Miller.Server/Tools/TestsCore.cs`, `src/Miller.Testing/Parsing/ContinuousTestStatusSummary.cs`, `src/Miller.Testing/Contracts/ContinuousTestingModels.cs`; test TestsCore + summary tests

**Serialization required:** Yes

**Dependency reason:** Consumes Task 1's cursor; Task 5 extends the projection with watermark data.

**What to build:** Kill the self-referential guard: `SelectedFrom` derives the reported key from stored rows, so uniform stale rows read green forever and consecutive status reads flip keys (observed live: 32424 vs 32161).

**Acceptance criteria:**
- [x] Stored rows at an old key + newer live cursor → stale, never green.
- [x] Two consecutive status reads with no index writes report the same key.
- [x] No cursor available → verdict `unknown` (sentinel removed).
- [x] Foreground status and summary use the same projection (one implementation, verified by tests exercising both paths).
- [x] Worker-scope verification passes; commit per commit mode. (d244986f, 66/66 across 7 classes)

### Task 3: Worktree enablement + tombstone

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestPolicy.cs` (`IsWorkspaceOptedIn`, `EnabledMarkerPath`)
- Modify: `src/Miller.Server/Tools/TestsCore.cs` (Enable `:278` area, Disable `:293` area)
- Test: policy tests under `tests/Miller.Tests/Testing/Daemon/` + TestsCore enable/disable tests

**Interfaces:**
- Consumes: `GitWorktreeLayout` (`src/Miller.Indexing/GitWorktreeLayout.cs`) — `Miller.Testing` already depends on `Miller.Indexing` (the poller opens `WorkspaceReadSessionFactory`), so use it directly.
- Produces: `IsWorkspaceOptedIn(root)` true when the root is opted in OR is a linked worktree whose main checkout is opted in AND no local `.miller/ct.disabled` tombstone exists. `tests disable` on a worktree writes the tombstone (and removes a local `ct.enabled` if present); `tests enable` removes the tombstone and writes the local marker.

**Contract inputs:** `.miller/ct.enabled` marker semantics; a linked worktree's `.git` is a FILE with `gitdir:` (CLAUDE.md). `MILLER_CT=off` beats everything. A non-git root or unreadable link inherits nothing (fail closed).

**File ownership:** Modify `src/Miller.Testing/Daemon/ContinuousTestPolicy.cs`, `src/Miller.Server/Tools/TestsCore.cs` (Enable/Disable ops); test policy + TestsCore enable/disable tests

**Serialization required:** Yes

**Dependency reason:** Shares `TestsCore.cs` with Task 2.

**What to build:** A worktree of a CT-enabled repo counts as enabled, with a working local opt-out. The Codex review caught that a tombstone without `tests enable`/`disable` wiring is dead policy: disable would delete a marker the worktree doesn't have, and inheritance would re-enable it on the next check.

**Acceptance criteria:**
- [x] Worktree of an enabled repo → opted in with zero manual calls.
- [x] Worktree of a never-enabled repo → off.
- [x] `MILLER_CT=off` → off everywhere.
- [x] `tests disable` on an inherited-enabled worktree → that worktree stays off (tombstone), main checkout unaffected; `tests enable` reverses it.
- [x] Worker-scope verification passes; commit per commit mode. (d5f838e2, 83 tests green across 7 classes)

### Task 4: Impacted/stale contract

**Files:**
- Modify: `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs` (`Select` `:84-164`; staleIds `:97-101`; workspace-scope fallback `:145-158`; truncation handling `:547` area)
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs` (`:343` area — all-known-IDs must NOT collapse to `WholeSuite`)
- Modify: `src/Miller.Testing/ContinuousTestCoordinator.cs` (`:207` area — same collapse on the coordinator side)
- Modify: `src/Miller.Server/Tools/TestsCore.cs` (run op `:835` area — explicit run uses the stale set, not workspace scope)
- Test: selector tests, queue tests, coordinator tests, TestsCore run tests

**Interfaces:**
- Consumes: Task 1's cursor; `ContinuousTestImpactOutcome` and truncation flags (`ICtFactSource.cs:59`, `ContinuousTestRevisionPoller.cs:166`).
- Produces: the selection contract every later task builds on: (a) stale set = impacted IDs ∪ already-stale IDs for path-scoped changes; (b) KNOWN-EMPTY impact (delta complete, nothing reachable) → empty stale delta and NO run; (c) UNKNOWN impact (degraded / unavailable / truncated) → fail closed: everything previously fresh goes stale, and NO provider execution and NO full-suite fallback is enqueued; (d) `WorkspaceScope` requests (generation change) still stale everything; (e) a selected set that happens to equal all known IDs still executes as an explicit ID list, never as a `WholeSuite` full run.

**Contract inputs:** Truncation flags are currently dropped (`ContinuousTestImpactSelector.cs:547`); unmatched paths currently fall back to workspace-wide evidence (`:145-158`) — that fallback becomes UNKNOWN handling per (c), except the explicitly workspace-scoped branch.

**File ownership:** Modify `src/Miller.Testing/Selection/ContinuousTestImpactSelector.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs` (selection shaping), `src/Miller.Testing/ContinuousTestCoordinator.cs`, `src/Miller.Server/Tools/TestsCore.cs` (run op); test selector + queue + coordinator tests

**Serialization required:** Yes

**Dependency reason:** Task 5's keep-set is the complement of this contract's impacted set.

**What to build:** The narrow stale set with honest edges. A 3-file edit stales dozens of cases, not 7,690 — and the degraded edges stale MORE, never run more.

**Acceptance criteria:**
- [x] Path-scoped change: stale set == impacted ∪ already-stale.
- [x] Known-empty impact: stale delta empty, no run enqueued.
- [x] Degraded/unavailable/truncated impact: all previously fresh cases stale; no execution enqueued; no `WholeSuite`.
- [x] All-known-IDs selection executes as an ID list, not `WholeSuite`.
- [x] Explicit `tests run` executes exactly the current stale set.
- [x] Green definition unchanged (zero stale + zero red at selected key).
- [x] Worker-scope verification passes; commit per commit mode. (e4ebb5cf; ContinuousTestSelectionOutcome contract; selector 51/51, queue+coordinator 20/20, server 44/44)

### Task 5: Crash-atomic watermark

**Files:**
- Modify: `src/Miller.Testing/Store/ContinuousTestStore.Coverage.cs` (`AdvanceContinuousTestFreshWatermark` `:836-885` — the `:867` predicate carries `red`/`skipped` and MUST become green-only; wrap staleness + advance into ONE transactional store operation)
- Modify: `src/Miller.Testing/Store/ContinuousTestDurableFreshness.cs` (watermark participation in freshness rules)
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs` (the revision-observation point calls the transactional operation)
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs` (`:563` area — daemon verdict path consumes the Task 2 projection with watermark data)
- Test: `tests/Miller.Tests/Testing/Store/Coverage/DurableFreshnessTests.cs` (extend) + queue tests; include crash-injection coverage (fault hook or transaction-abort test proving no state where an impacted case reads fresh)

**Interfaces:**
- Consumes: Task 4's contract (impacted set, known-empty vs unknown); Task 2's projection; Task 1's cursor.
- Produces: one store operation `ApplyRevisionAdvance(workspace, project, cursor R0→R1, impactedIds, outcome)` that, in a single transaction: marks impacted/unknown cases stale AND advances watermarks for the keep-set (currently fresh — committed or watermark — GREEN cases not impacted). Unknown outcome advances nothing and stales everything previously fresh. Status/verdict everywhere treats watermark-fresh green as fresh.

**Contract inputs:** Only greens ride the watermark (design; Global Constraints). Chained edits: watermark-fresh cases re-enter the keep-set. A crash between staleness and advance must leave cases stale, never fresh — hence one transaction, tested.

**File ownership:** Modify `src/Miller.Testing/Store/ContinuousTestStore.Coverage.cs`, `src/Miller.Testing/Store/ContinuousTestDurableFreshness.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs` (verdict path); test `DurableFreshnessTests.cs` + queue tests

**Serialization required:** Yes

**Dependency reason:** Needs Tasks 1, 2, 4. Shares queue files with Task 4.

**What to build:** The production caller for the watermark — as one atomic operation, with the green-only predicate fixed. This is the heart of the plan.

**Acceptance criteria:**
- [x] Unrelated change: all greens watermark to R1; verdict green; stale 0.
- [x] Impacted change: impacted stale; unimpacted greens fresh; red and skipped rows NEVER watermark (explicit test).
- [x] Two chained unrelated changes: greens fresh at R2 via watermark alone.
- [x] Unknown outcome: nothing advances; previously fresh cases stale (fail-safe).
- [x] Generation change: watermark ignored; everything stale.
- [x] Crash/abort between staleness and advance leaves impacted cases stale (injection test).
- [x] Daemon verdict path and foreground status agree (same projection).
- [x] Worker-scope verification passes; commit per commit mode. (1271829b; ApplyRevisionAdvance single transaction, 255 tests green, 2 crash tests)

### Task 6: Debounced auto-run

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`
- Test: queue/poller tests

**Interfaces:**
- Consumes: Task 4's narrow stale set; Task 5's atomic advance.
- Produces: after a change lands and the debounce quiet period elapses, the daemon runs the stale set automatically. `MILLER_CT_DEBOUNCE` (seconds) tunes the quiet period; default in low single digits chosen against the poller cadence and recorded in code and tests. Changes during an executing run queue a follow-up selection; they never kill a healthy run.

**Contract inputs:** Live dogfood 2026-08-20 observed NO auto-run after edits while the daemon was running-idle — first diagnose why (razorback:systematic-debugging; the poller observed the revision, so the break is between observation and execution), then build the debounce on the fixed path. CLAUDE.md's daemon contract already promises change-triggered execution.

**File ownership:** Modify `src/Miller.Testing/Daemon/ContinuousTestDaemonQueue.cs`, `src/Miller.Testing/Daemon/ContinuousTestRevisionPoller.cs`; test queue/poller tests

**Serialization required:** Yes

**Dependency reason:** Shares queue files with Tasks 4-5; meaningful only with narrow stale sets.

**What to build:** The NCrunch loop: edit → quiet period → impacted tests run → verdict updates. Coalesce save bursts (timer resets on each new change).

**Acceptance criteria:**
- [x] The root cause of the missing auto-run is identified and stated in the task report. (old per-write identity made every save read as a rebuild; the rebuild branch returned before the enqueue)
- [x] A change triggers exactly one run after the quiet period; a burst triggers one run.
- [x] A change during execution queues a follow-up; the running suite is not killed.
- [x] `MILLER_CT_DEBOUNCE` honored (0 = immediate); auto-run governed by CT enablement, not the debounce.
- [x] Worker-scope verification passes; commit per commit mode. (b29e2bbc; 238 tests green; default debounce 2s = eight 250ms poll ticks; poller truncation → Unavailable)

### Task 7: Daemon adopts family worktrees

**Files:**
- Modify: `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs` (`:90-136` single-workspace host; `:249` area engine wiring)
- Modify: `src/Miller.Testing/Daemon/CtDaemonProtocol.cs` (`:139` area — command routing must reach the family daemon from a worktree)
- Modify: `src/Miller.Testing/Daemon/CtDaemonLauncher.cs` (`:39` area)
- Modify: `src/Miller.Server/Tools/TestsCore.cs` (worktree status names the serving daemon; start/stop route to it)
- Test: daemon host + protocol tests under `tests/Miller.Tests/Testing/Daemon/`

**Interfaces:**
- Consumes: Task 3's inheritance; `GitWorktreeLayout` for same-repo resolution; the workspace registry via a NON-CREATING read path (`WorkspaceRegistry.Open` at `WorkspaceRegistry.cs:77` creates directories/schema — add or use a read-only open that returns empty when absent).
- Produces: one daemon process, one lease (main root), N per-worktree CONTEXTS — each with its own store (`ct.db`), fact source, queue, poller, and status record; only the execution budget is shared. Dynamic attach on poll cycle for newly registered opted-in worktrees; clean detach when the root disappears. A worktree that runs its OWN daemon (own lease held) is excluded from adoption. Commands issued against a worktree workspace (status/failures/run/stop) route to the family daemon.

**Contract inputs:** The queue keys observations by `WorkspaceId` already. `MILLER_CT=off` is checked BEFORE any registry access. Cross-worktree isolation: context A's staleness or runs must never read or write context B's `ct.db`.

**File ownership:** Modify `src/Miller.Testing/Daemon/ContinuousTestDaemonHost.cs`, `src/Miller.Testing/Daemon/CtDaemonLauncher.cs`, `src/Miller.Testing/Daemon/CtDaemonProtocol.cs`, `src/Miller.Server/Tools/TestsCore.cs`; test daemon host/protocol tests

**Serialization required:** Yes

**Dependency reason:** Builds on Task 3; shares TestsCore and host files with earlier tasks.

**What to build:** The multi-context host. The Codex review's point stands: enumerating worktrees onto a single-workspace host risks cross-workspace reads and dead command routing — the contexts and the routing are the real work.

**Acceptance criteria:**
- [x] Daemon on main + registered enabled-by-inheritance worktree → worktree gets status/selection/runs with zero manual calls beyond `workspace open`.
- [x] A worktree change triggers a debounced impacted run against the worktree's own index and `ct.db` under the shared budget.
- [x] A never-enabled repo's worktree is not adopted; a worktree with its own live daemon is not adopted.
- [x] `tests status` with `MILLER_CT=off` performs zero registry/filesystem creation (filesystem assertion: no registry dir, no `.miller/ct/`, no `ct.db` appear).
- [x] Worktree `tests run`/`stop` route to the family daemon; removing the worktree detaches it; main state untouched.
- [x] Worker-scope verification passes; commit per commit mode.

### Task 8: Docs and contracts

**Files:**
- Modify: `CLAUDE.md` (Continuous testing sidecar section, `:383` area — freshness key semantics, worktree adoption, debounce)
- Modify: `docs/contracts/tests-cli-v1.md` (`:54` area — composite-key description, adopted-worktree status fields)
- Regenerate: `AGENTS.md` via `scripts/sync-agents.ps1`; verify `cmp -s CLAUDE.md AGENTS.md` equivalent on Windows
- Test: none (docs); `AgentInstructionsTests` must stay green if tool descriptions changed (they should not)

**Interfaces:**
- Consumes: the shipped semantics of Tasks 1-7.
- Produces: public docs that describe the watermark freshness key, the impacted/stale contract, the debounced auto-run, and worktree adoption — no doc still describing the old whole-cursor identity or one-workspace daemon.

**Contract inputs:** CLAUDE.md editing rule: edit CLAUDE.md, run the sync script, AGENTS.md must match.

**File ownership:** Modify `CLAUDE.md`, `docs/contracts/tests-cli-v1.md`, regenerate `AGENTS.md`

**Serialization required:** Yes

**Dependency reason:** Documents the semantics Tasks 1-7 shipped.

**What to build:** The contract update the Codex review flagged as missing entirely.

**Acceptance criteria:**
- [x] `CLAUDE.md` CT section states the new freshness key, watermark, debounce, and adoption semantics; `AGENTS.md` regenerated and identical.
- [x] `docs/contracts/tests-cli-v1.md` matches the shipped JSON.
- [x] Worker-scope verification passes; commit per commit mode.

### Task 9: Live acceptance validation

**Files:**
- Create (if gaps found): Scale tests under `tests/Miller.Tests/Testing/` tagged `[Trait("Category","Scale")]` using `CtProviderTestSupport.RequireDotnet()`
- No production files.

**Interfaces:**
- Consumes: everything above.
- Produces: the design doc's two acceptance checklists ticked with evidence, or precise defect reports.

**Contract inputs:** Hard-gate scenarios (each with recorded evidence — status output or `ct.db` query):
1. Edit one source file → only impacted tests run after debounce → verdict green, no full run.
2. Edit an unrelated markdown file → verdict stays green, stale 0, watermark advanced.
3. Full rebuild → everything stale.
4. Red result stays red/stale until rerun; never watermark-fresh.
5. Degraded delta → stale grows, nothing executes.
6. Same numeric revision across two generations never fresh.
7. Pre-change (legacy) `ct.db` reads stale once, then converges.
8. Worktree: adopt, run, disable-tombstone, remove — with cross-context isolation asserted.
9. `MILLER_CT=off` zero-work filesystem assertion.

**File ownership:** No production files; may add Scale tests under `tests/Miller.Tests/Testing/**`

**Serialization required:** Yes

**Dependency reason:** Validates the composed behavior of all prior tasks.

**What to build:** Run the scenarios against a real daemon in the worktree. Any failure is a defect report back into the relevant task, not a criteria edit.

**Acceptance criteria:**
- [ ] Every hard-gate scenario above ticked with recorded evidence.
- [ ] Every design acceptance criterion (freshness list + worktree list) ticked.
- [ ] Branch gate (`scripts/test.ps1 all`) green in the worktree.
- [ ] Verification ledger complete for the branch HEAD.
