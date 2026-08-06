### Task 6: RebindBootstrap orchestration + bootstrap wiring

**Files:**
- Create: `src/Miller.Indexing/RebindBootstrap.cs`
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs` (`!dbExists` arm of the scan path;
  plain-bootstrap fallback entry)
- Test: `tests/Miller.Tests/Indexing/RebindBootstrapTests.cs` (fast — sequence/fallback/recording
  logic against seams) and `tests/Miller.Tests/Server/RebindBootstrapScaleTests.cs` (Scale —
  end-to-end with the real binary)

**Interfaces:**
- Consumes: Task 2's disqualification fold; Task 3's two-stage decisions; Task 4's
  `SqliteOnlineBackup.Copy` + `ResolveBudget`; Task 5's `Rebind` runner call;
  `FullRebuildPromotion.PrepareRebuildTarget`/`RebuildDbPathFor`/`Promote`
  (`src/Miller.Indexing/FullRebuildPromotion.cs:74-113`); `ScanGovernorAdmission.TryAcquire`;
  `IScanFailurePolicy.RecordFailure(ScanIntent, int?, int)` + `Evaluate`
  (`src/Miller.Indexing/ScanFailurePolicyStore.cs:21,44,47`); Task 1's
  `FindMainCheckoutByCommonDir`.
- Produces: `RebindBootstrap.TryRebind(...) → RebindBootstrapOutcome`
  (`Promoted | Ineligible(reason) | Failed(stage, reason)`) that `IndexBootstrapService` calls in
  the `!dbExists` arm when eligible; on anything but `Promoted` the existing plain bootstrap scan
  proceeds unchanged. Also the provenance facts Task 7 renders (the promoted artifact carries the
  metadata keys — no extra plumbing beyond promotion itself).

**Contract inputs:** contract design §7 verbatim. Sequence (§7.1): (1)
`PrepareRebuildTarget(liveDb)` at entry AND on every failure exit AND at plain-bootstrap fallback
entry — a dead rebind must never strand a multi-GB `.rebuild` trio; (2) backup-seed under budget,
with a best-effort skip while the source's `scan.progress` heartbeat is fresh; (3) snapshot
validation (Task 3 stage 2) against the `.rebuild` file; (4) `rebind --db <symbols.db.rebuild>
--root <target root>`; (5) non-force scan against the `.rebuild` path at the snapshot's RECORDED
level; (6) `Promote`; (7) normal `UpsertSeen` + `MarkScanned` with refreshed lineage. Recovery
(§7.2): failure before promote deletes staging and falls back; death after promote is SUCCESS —
on a `Promote` exception, probe the live path (root matches + committed revision) before declaring
failure, because `Promote` can throw after the move. Recording (§7.3): W8
`RecordFailure(ScanIntent.IncrementalReconcile, exitCodeOrNull, jobs)`; no new intent. All under
ONE governor admission + the bootstrap writer lease.

**File ownership:** Create `src/Miller.Indexing/RebindBootstrap.cs`; Modify `src/Miller.Server/Hosting/IndexBootstrapService.cs`; Test `tests/Miller.Tests/Indexing/RebindBootstrapTests.cs` (fast) + `tests/Miller.Tests/Server/RebindBootstrapScaleTests.cs` (Scale)

**Serialization required:** Yes

**Dependency reason:** Consumes Tasks 2, 3, 4, 5 contracts; shares `IndexBootstrapService.cs` with Task 2.

**What to build:** The dedicated bootstrap sequence and its wiring: when a fresh linked worktree
opens with an eligible sibling artifact, run copy → validate → rebind → delta-scan → promote
instead of a full extraction; on any failure, clean staging, record under W8, and fall back to the
plain bootstrap scan.

**Approach:** Keep `RebindBootstrap` I/O-orchestration thin over injectable seams (copier, runner,
validator, promotion) so the fast tests drive every branch — including the promote-exception
probe — without a subprocess. The Scale test builds a real main-checkout artifact via
`ScaleTestSupport`, creates a real linked worktree (`git worktree add`), opens it, and asserts:
rebind ran (provenance keys present, `artifact_id` differs from source), byte-identical tree
produced a `no_change` delta, the SOURCE artifact is byte-identical afterward (hash before/after),
and a killed/failed rebind leaves no `.rebuild` debris after the fallback completes. Never invoke
`JulieExtractRunner.Scan(force: true)` anywhere in this path; the delta scan is a non-force scan
argv pointed at the `.rebuild` file with the recorded level.

**Acceptance criteria:**
- [ ] Fresh linked-worktree open with an eligible sibling artifact runs rebind, not a full scan
      (Scale test; provenance keys present; source untouched by hash comparison).
- [ ] Byte-identical tree → delta scan reports `no_change`.
- [ ] Every failure stage (budget exhausted, snapshot invalid, rebind refused, scan failed,
      promote failed-before-move) cleans staging, records under W8 with
      `ScanIntent.IncrementalReconcile`, and falls back to the plain scan (fast tests).
- [ ] Promote-exception probe adopts a post-move artifact as success.
- [ ] Plain-bootstrap fallback entry runs staging cleanup (debris-free after a simulated dead
      rebind).
- [ ] Worker-scope verification passes and the change is handed to the lead per
      parallel-lead-commit.

