### Task 2: Bootstrap lineage capture + replacement consumption rule

**Files:**
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs` (`UpsertSeen` call sites; the
  decision fold around `DecideBootstrapScan` :907-926 / `EscalateForReplacedRoot` :938-946)
- Test: `tests/Miller.Tests/Server/BootstrapReplacedRootTests.cs`

**Interfaces:**
- Consumes: Task 1's `WorkspaceLineage` record + extended `UpsertSeen` + row fields;
  `WorkspaceRootIdentity.Capture`/`IsReplacement`
  (`src/Miller.Indexing/WorkspaceRootIdentity.cs:40,69-79`); the existing in-memory replacement
  escalation path.
- Produces: a pure fold `static bool DisqualifiesRebind(WorkspaceRegistryRow? stored, WorkspaceRootIdentity current)`
  (name at implementer's discretion, but pure and fast-suite-tested) that Task 6 calls: true when
  the stored persisted identity is known and `IsReplacement(stored, current)`; the bootstrap
  escalates to `EscalateForReplacedRoot` in exactly that case, BEFORE any rebind attempt.

**Contract inputs:** contract design §5 consumption rule (load-bearing): a replaced root both
escalates the scan decision to `ScanIntent.RootRebind` AND disqualifies rebind for that open —
the on-disk artifact and registry row describe a different checkout generation. Columns refresh
via the normal `UpsertSeen` afterward. Missing/unknown stored identity NEVER counts as a
replacement (missing evidence must not cost a rebuild).

**File ownership:** Modify `src/Miller.Server/Hosting/IndexBootstrapService.cs`; Test `tests/Miller.Tests/Server/BootstrapReplacedRootTests.cs`

**Serialization required:** Yes

**Dependency reason:** Consumes Task 1's row fields and `UpsertSeen` shape.

**What to build:** Persist lineage at every bootstrap `UpsertSeen`, and make the persisted
identity feed the existing replacement escalation so `git worktree remove`+`add` while no Miller
runs is detected on the next open (today the identity sample is in-memory only).

**Approach:** Capture `GitWorktreeLayout.Resolve` + `WorkspaceRootIdentity.Capture` once per
bootstrap (they are already resolved nearby for the presence monitor — reuse, don't re-probe).
Compare stored-vs-current BEFORE the first `UpsertSeen` refreshes the row. Extend the existing
`BootstrapReplacedRootTests` scenario style: persisted-identity replacement (no live monitor
involvement) escalates and would-disqualify.

**Acceptance criteria:**
- [ ] A registry row carrying a different known persisted identity escalates the bootstrap
      decision to `RootRebind` (via `EscalateForReplacedRoot`) with no live
      `WorkspaceRootPresenceMonitor` involvement.
- [ ] Unknown stored identity or unknown current identity never escalates and never disqualifies.
- [ ] Lineage is persisted on bootstrap and refreshed after the decision (stored generation is
      the CURRENT one post-open).
- [ ] Worker-scope verification passes and the worker commits per serial-worker-commit.

