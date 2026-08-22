# Generated Ignore Policy Hygiene Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Preserve Miller's scan/update/watcher exclusion parity without creating a top-level generated baseline/vendor `.julieignore` in ordinary newly registered workspaces.

**Architecture:** User-authored root `.julieignore` files remain authoritative and in-tree. When neither the workspace nor a linked worktree's main checkout supplies user policy, Miller materializes generated baseline/vendor rules under `$MILLER_HOME/.miller/ignore-policies/<workspace-id>.julieignore`, wires that path into full scan, file update, and watcher behavior in one compilable slice, and never writes a root generated file. Linked worktrees continue copying inherited user policy in-tree because malformed external `--ignore-file` content hard-fails instead of warning; removing that exceptional write requires an upstream soft-external-ignore contract and is outside this plan.

**Tech Stack:** .NET 10 filesystem policy, `julie-extract` scan/update argv, Miller watcher, workspace registry/removal.

**Architecture Quality:** The current `JulieIgnoreSeeder` is deep policy but its storage choice leaks into the user's checkout. The replacement seam is one effective-policy descriptor consumed by scan, update, and watcher paths; this is earned by three real consumers and is recorded in ADR-0006. Root policy remains user-owned, inherited user policy keeps warning-only in-tree semantics, generated policy becomes Miller-owned, and removal follows validated workspace/Miller-home boundaries. Rejected shortcuts are disabling vendor detection, header-based deletion, using `.miller/invariant.julieignore` without watcher/update parity, or passing malformed user files as external hard-error inputs. Architecture risk is medium because ignore disagreement can silently reinsert excluded files.

## Global Constraints

- Never create a workspace-root baseline/vendor `.julieignore`; never delete, overwrite, append, or migrate a user-authored root policy.
- An existing root `.julieignore` remains authoritative and retains julie-extract's warning behavior; do not pass it as an external `--ignore-file`.
- A linked worktree may retain the existing in-tree copy of its main checkout's user policy, including malformed content, until julie-extract offers equivalent warning-only external policy semantics.
- Generated policy lives under `MillerHome.ResolveMillerDirectory()` and is keyed by canonical `WorkspaceId`.
- Full scan, single-file update, watcher filtering, linked-worktree inheritance, and vendor detection consume the same effective rules.
- Preserve the invariant `.miller/invariant.julieignore` and operational `.miller` lifecycle; this plan addresses only top-level generated `.julieignore` hygiene.
- Preserve exclusive/atomic generation and never expose partial policy content.
- Workspace removal may delete only the exact global generated-policy path resolved from its validated registry row.
- Apply TDD per task. Serialized tasks use `serial-worker-commit` after lead review.
- Cross-plan acceptance tasks run in this fixed order to avoid `docs/README.md` conflicts: CT Task 6, sidecar Task 5, generated-ignore Task 3. Each later task preserves earlier map entries.

---

## Verification Strategy

**Project source of truth:** `AGENTS.md`, `JulieIgnoreSeeder` API docs, `ScanIgnorePolicy` API docs, and workspace-removal safety tests.

**Worker red/green scope:** One focused filter for `JulieIgnoreSeederTests`, `ScanIgnorePolicyTests`, `WatchPathFilterTests`, `JulieExtractRunnerTests`, or `WorkspaceRemovalTests`.

**Worker ceiling:** Focused fast classes only. Workers do not run a real extractor or the full suite.

**Worker gate invariant:** Task 1 proves ownership/storage and scan/update/watcher parity in one compilable slice; Task 2 proves removal never touches user policy; Task 3 proves real extractor parity and checkout behavior.

**Lead affected-change scope:** Run the union of focused filters and `dotnet build Miller.slnx -c Release` after each task.

**Branch gate:** Run `scripts/test.sh all` because the plan changes real extractor scan/update argv and watcher behavior.

**Security scope:** none declared.

**Replay/metric evidence:** Hard gates are byte-identical user root policy, no generated root file, exact external-policy argv parity, excluded-file absence after scan and update, and safe validated cleanup. Vendor-walk time is report-only.

**Escalation triggers:** Any required julie-extract flag/schema change moves to `julie-extractors` and must preserve all supported languages before Miller consumes it. No such producer change is expected because `--ignore-file` already exists.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless the plan explicitly assigns the gate update.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, timestamp, root-policy hash, generated-policy path/hash, and final git status.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Atomic materialization and consumer wiring | None - serial | `src/Miller.Indexing/JulieIgnoreSeeder.cs`; `src/Miller.Indexing/ScanIgnorePolicy.cs`; `src/Miller.Indexing/JulieExtractRunner.cs`; `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs`; `src/Miller.Server/Hosting/WorkspaceIgnorePolicy.cs`; `src/Miller.Server/Hosting/IndexerWatcherSet.cs`; `docs/adr/ADR-0006-generated-ignore-policy-ownership.md`; related tests | Yes | Storage and all consumers must land together so every accepted task compiles and preserves exclusions. |
| Task 2: Safe lifecycle and migration behavior | None - serial | `src/Miller.Server/Workspaces/WorkspaceRemoval.cs`; `docs/known-limits.md`; `docs/cli.md`; removal/policy tests | Yes | Depends on Task 1's final global path/ownership contract. |
| Task 3: Real extractor and clean-checkout acceptance | None - serial | `tests/Miller.Tests/Indexing/WorktreeIgnorePropagationScaleTests.cs`; `docs/findings/2026-08-22-generated-ignore-policy-hygiene-verification.md`; `docs/README.md`; plan status checkboxes | Yes | Runs after implementation and branch gates, and after the other plans' acceptance tasks. |

### Task 1: Atomic materialization and consumer wiring

**Files:**
- Modify: `src/Miller.Indexing/JulieIgnoreSeeder.cs`
- Modify: `src/Miller.Indexing/ScanIgnorePolicy.cs`
- Modify: `src/Miller.Indexing/JulieExtractRunner.cs`
- Modify: `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs`
- Modify: `src/Miller.Server/Hosting/WorkspaceIgnorePolicy.cs`
- Modify: `src/Miller.Server/Hosting/IndexerWatcherSet.cs`
- Create: `docs/adr/ADR-0006-generated-ignore-policy-ownership.md`
- Modify: `tests/Miller.Tests/Indexing/JulieIgnoreSeederTests.cs`
- Test: `tests/Miller.Tests/Indexing/ScanIgnorePolicyTests.cs`
- Test: `tests/Miller.Tests/Indexing/JulieExtractRunnerTests.cs`
- Test: `tests/Miller.Tests/Indexing/JulieExtractRunnerUpdateDeleteTests.cs`
- Test: `tests/Miller.Tests/Server/WatchPathFilterTests.cs`
- Test: `tests/Miller.Tests/Server/IndexerWatcherSetTests.cs`

**Interfaces:**
- Consumes: canonical workspace root, `WorkspaceId`, `MillerHome.ResolveMillerDirectory()`, inherited main-checkout content, baseline rules, and vendor detection.
- Produces: an immutable effective-policy descriptor with `Source=user_root|inherited_root_copy|generated_global`, path, content hash, and whether materialization wrote new bytes; scan, update, and watcher consume it together.

**Contract inputs:** A workspace root file means `user_root` and no write. A linked worktree whose main checkout has user policy retains the existing exclusive in-tree snapshot as `inherited_root_copy`, including malformed warning-only behavior. Only Miller-generated baseline/vendor rules become `generated_global` and may be passed as external `--ignore-file`; the invariant ignore remains separate.

**File ownership:** `src/Miller.Indexing/JulieIgnoreSeeder.cs`; `src/Miller.Indexing/ScanIgnorePolicy.cs`; `src/Miller.Indexing/JulieExtractRunner.cs`; `src/Miller.Server/Workspaces/StoreWorkspaceCoordinator.cs`; `src/Miller.Server/Hosting/WorkspaceIgnorePolicy.cs`; `src/Miller.Server/Hosting/IndexerWatcherSet.cs`; `docs/adr/ADR-0006-generated-ignore-policy-ownership.md`; related tests

**Serialization required:** Yes.

**Dependency reason:** Storage and all consumers must land together so every accepted task compiles and preserves exclusions.

**What to build:** Replace production baseline/vendor root writes with global materialization and thread the typed descriptor through full scan, single-file update, and watcher filtering in the same commit. Preserve the current in-tree path for user-root and inherited-user policy so malformed content keeps warning instead of becoming a hard external-file failure.

**Approach:** Write generated policy to a same-directory temporary file and atomically replace only Miller-owned global policy. Pass `--ignore-file` only for `generated_global`. Keep one policy-preparation call per operation, load the same generated path in `WorkspaceIgnorePolicy`, and continue watching root `.julieignore` creation/change so a later user-authored file forces full policy recomputation and becomes authoritative. Retain testable race hooks and never claim or clean a root file based on its header.

**Acceptance criteria:**
- [x] An ordinary fresh workspace gets no root `.julieignore` and receives one deterministic global generated policy.
- [x] Existing/malformed/racing root policies remain byte-identical and authoritative.
- [x] Linked worktrees retain in-tree inherited user-policy behavior, including malformed warning-only scans; inherited content is never passed as external `--ignore-file`.
- [ ] A vendor/excluded file is absent after full scan and remains absent after a direct update attempt.
- [x] Watcher filtering agrees with extractor behavior for generated, inherited, and user-authored policies.
- [x] Creating a root `.julieignore` after registration disables generated-global authority and forces policy re-evaluation without overwriting either file.
- [x] `MILLER_HOME` isolation and concurrent materializers are deterministic.
- [x] ADR-0006 records ownership, source precedence, malformed inherited compatibility, consumer parity, and cleanup rules.
- [x] Focused materializer/scan/update/watcher tests pass and the serialized worker commit is recorded in `7d499d54`.

### Task 2: Safe lifecycle and migration behavior

**Files:**
- Modify: `src/Miller.Server/Workspaces/WorkspaceRemoval.cs`
- Modify: `docs/known-limits.md`
- Modify: `docs/cli.md`
- Test: `tests/Miller.Tests/Server/WorkspaceRemovalTests.cs`
- Test: `tests/Miller.Tests/Indexing/JulieIgnoreSeederTests.cs`

**Interfaces:**
- Consumes: validated workspace registry row, Task 1 global-policy path, and current removal lease/lock safety.
- Produces: cleanup of only Miller-owned global policy plus documented treatment of pre-existing root files.

**Contract inputs:** Existing root `.julieignore` files from older Miller versions are never automatically removed; users may delete them after reviewing their content. `.miller` removal keeps its existing lock/CT/sidecar safeguards.

**File ownership:** `src/Miller.Server/Workspaces/WorkspaceRemoval.cs`; `docs/known-limits.md`; `docs/cli.md`; removal/policy tests

**Serialization required:** Yes.

**Dependency reason:** Depends on Task 1's final global path/ownership contract.

**What to build:** Delete the exact global generated policy during successful workspace removal. Document the one-time migration behavior and why header-based cleanup of old root files is unsafe.

**Approach:** Resolve cleanup from the validated canonical root/workspace ID under Miller home; refuse traversal/out-of-home paths. A cleanup failure is reported but never expands deletion scope.

**Acceptance criteria:**
- [x] Remove-by-ID and remove-by-path delete only the matching global policy.
- [x] User, inherited, edited, malformed, and legacy root `.julieignore` files remain byte-identical.
- [x] Invalid registrations cannot target arbitrary policy paths.
- [ ] Focused removal/policy tests pass and the serialized worker commit is recorded.

### Task 3: Real extractor and clean-checkout acceptance

**Files:**
- Modify: `tests/Miller.Tests/Indexing/WorktreeIgnorePropagationScaleTests.cs`
- Create: `docs/findings/2026-08-22-generated-ignore-policy-hygiene-verification.md`
- Modify: `docs/README.md`
- Modify: `docs/plans/2026-08-22-generated-ignore-policy-hygiene-plan.md`

**Interfaces:**
- Consumes: completed Tasks 1-2 and the real pinned `julie-extract`.
- Produces: cross-platform scan/update/watcher parity and git-cleanliness evidence.

**Contract inputs:** Use temporary plain and linked-worktree repos with isolated `MILLER_HOME`; do not modify user repos.

**File ownership:** `tests/Miller.Tests/Indexing/WorktreeIgnorePropagationScaleTests.cs`; verification finding; `docs/README.md`; plan status checkboxes

**Serialization required:** Yes.

**Dependency reason:** Runs after implementation and branch gates.

**What to build:** Extend the real-extractor Scale fixture to cover generated global policy, linked-worktree inheritance, direct update refusal, watcher parity, user-root takeover, and removal cleanup. Record `git status --short` before/after Miller onboarding.

**Approach:** Hard-gate absence of root `.julieignore` for ordinary roots with no user or inherited policy; preserve the linked inherited-user copy cases. Report `.miller/` separately as expected operational state. Run on Linux locally and rely on existing Windows Scale CI for path/locking parity.

**Acceptance criteria:**
- [ ] Ordinary fresh registration with no user/inherited policy creates no root `.julieignore` while generated exclusions work end to end.
- [ ] Linked-worktree and user-authored policy behavior is unchanged.
- [ ] Workspace removal cleans only global generated policy and expected Miller-owned state.
- [ ] `scripts/test.sh all`, Release build, and worktree-state checks are recorded on the exact final tree.
- [ ] Verification evidence is mapped in `docs/README.md`, all plan checkboxes are updated, and the serialized worker commit is recorded.
