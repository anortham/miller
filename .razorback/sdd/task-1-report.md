# Task 1 — Release the machine-wide admission before sidecar convergence

**Status:** DONE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/p4-findings-fixes`
**Branch:** `p4-findings-fixes` · **Base HEAD:** `bc808b26` · **Commit mode:** parallel-lead-commit (nothing staged or committed)

---

## 1. What changed, per file

### `src/Miller.Server/Hosting/IndexerService.cs`

Five governed sites. Three of them (`leader-upgrade`, `leader-ondemand`, `leader-requested-full`) do NOT
converge at the call site — they converge **inside** `ScanAsLeaderUnderGate` (`:1171-1172`). So the release
point for those three is inside that helper, not at the `using`.

| Site | Where the release now happens |
|---|---|
| `RunDrainTick` (`leader-drain-rescan`) | `finally` around `_core.DrainAndProcess(...)`, before `TryConvergeSidecarToLatest` |
| `RunStartupDeltaScan` (`leader-startup`) | `finally` around `ops.Scan(...)`, before `TryConvergeSidecar` |
| `RunExtractorUpgradeRescan` (`leader-upgrade`) | inside `ScanAsLeaderUnderGate` |
| `TryScanAsLeader` (`leader-ondemand`) | inside `ScanAsLeaderUnderGate` |
| `TryProcessLeaderFullScanRequests` (`leader-requested-full`) | inside `ScanAsLeaderUnderGate` |

- `ScanAsLeaderUnderGate` gained a `ScanGovernorAdmission? admission` parameter (positional, third, before the
  optional `decision`) so no caller can forget it. It disposes the admission in a `finally` around
  `ops.Scan(...)` — so a **throwing** scan also releases early, before the failure-recording path.
- Every `using` declaration/statement stayed in place as the exception-safety net. Because `Dispose` is
  idempotent, the epilogue is a no-op.
- New narrow test seam `internal Action? BeforeSidecarConvergeForTest`, invoked at the top of
  `TryConvergeSidecar(string?, long, bool)` — the single funnel every current-workspace convergence passes
  through. Justification in §4.
- Comment corrections: the `_governor` field comment and the `TryAcquireScanAdmission` comment both claimed
  admission covered "the sidecar convergence that follows"; both now state the new boundary and cite the finding.

### `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`

Verified the plan's open question: **yes**, `TryConvergeSidecar` (`:290`) sat inside the admission scope.
Same ordering applied — `admission.Dispose()` in a `finally` around `_scan(...)`. `scanClock.Stop()` stays
inside the inner `try` so the reported scan duration still measures only the extract.

Safety after release: this method holds the workspace `SingleWriterLock` (`:209`) for its whole body, so
convergence keeps its per-workspace exclusion once the machine-wide lease is gone. Comment updated to say so.

### `src/Miller.Indexing/ScanGovernor.cs`, `ScanGovernorState.cs`

**No behaviour change.** `ScanGovernorAdmission.Dispose` was ALREADY idempotent (`_disposed` guard,
`ScanGovernorState.cs:229-236`), and so is `ScanGovernorLease.Dispose` (`ScanGovernor.cs:50-61`). Only the two
doc comments that asserted the lease "covers the synchronous sidecar convergence that follows" were corrected —
leaving them would have made a load-bearing class doc actively wrong about the invariant it documents.

### `tests/Miller.Tests/Server/IndexerSidecarAdmissionTests.cs` (new)

Six tests: the five leader sites plus `Dispose` idempotency. Fast suite, no `julie-extract`, no `[Trait Scale]`
needed (fake `IExtractOps`, no subprocess). Each site test asserts, via one shared assertion:

1. during the scan — governor position is `holding` **and** a second `ScanGovernor` over the same temp miller
   home is refused the OS lease (the one-extractor invariant is intact);
2. convergence actually ran (`ConvergeObserved`, so a silently-skipped converge cannot pass);
3. during convergence — the published position is gone **and** the OS lease is free.

Both facts are checked, not just the bookkeeping: the lease probe is the ground truth, the
`ScanGovernorState.Shared` snapshot is what `workspace status`/`health` render.

### `tests/Miller.Tests/Server/CrossWorkspaceRefreshServiceTests.cs` — **scope note, please read**

`Refresh_HoldsMachineScanAdmission_AcrossTheScanAndTheSidecarConvergence` (`:989`) **pinned the exact behaviour
this task removes**. It could not survive the change. I rewrote it in place as
`Refresh_ReleasesMachineScanAdmission_AsSoonAsTheScanReturns_NotAfterTheSidecarConverges`, inverting the
assertions and adding a `readLatestRevision` probe (the service calls it after the scan and before
`TryConvergeSidecar`, so it observes the exact gap). This file was **not** in my declared ownership list, but
leaving it would have left the fast suite red, and it is not owned by any sibling worker (they hold
`IndexBootstrapService`, `RebindBootstrap`, and the exception/runner files). Flagging rather than hiding it.

---

## 2. Judgment calls

1. **Release inside `ScanAsLeaderUnderGate`, not at the three call sites.** The task text says "insert
   `admission?.Dispose();` after the scan/drain call". For three of the five sites the scan call is not at the
   call site — the convergence is inside the helper. Disposing at the call site would have released *after*
   convergence and changed nothing. This is the same approved shape (explicit idempotent `Dispose()` between
   "extract returned" and "converge"), applied at the place where those two things actually adjoin. No new
   abstraction, no governor API change.
2. **`finally`, not a straight-line call.** A throwing scan should release the machine-wide lease too; the
   failure paths (`RecordScanFailure`, registry error write) are not extraction work.
3. **Kept every `using`.** Belt and braces, free because `Dispose` is idempotent — and the idempotency test now
   pins that this is safe.
4. **Did not touch `WorkspaceTool.Open` or `IndexBootstrapService`.** Miller `trace` surfaced them as admission
   holders; I checked both and **neither builds a sidecar inside the admission scope**, so there is no leak to
   fix there. Details in §3.
5. **Cross-workspace observed via `readLatestRevision`, not a new hook.** That existing injected seam sits
   strictly between the scan and convergence, so no production surface was added to that file.

---

## 3. Miller evidence

The worktree itself was mid-first-index for the early orientation, so the first two calls were refused; I
re-ran the structural queries against the main checkout once it converged. Calls used:

| Call | What it confirmed |
|---|---|
| `context query="scan governor admission acquire and sidecar convergence in IndexerService" workspace_id=miller-b275269b2d7c` | refused — worktree bootstrap in progress (reported honestly, not silently skipped) |
| `inspect target=ScanGovernorAdmission depth=full` | same refusal; re-derived by reading `ScanGovernorState.cs` in the worktree |
| `trace target=ScanGovernorAdmission mode=refs limit=20 workspace_id=miller-b275269b2d7c` | **17 exact references, 0 fallback.** Enumerated EVERY admission site in the repo: `IndexerService` `:538/:617/:813/:959/:1025/:1036/:1091`, `CrossWorkspaceRefreshService` `:245/:442`, plus `IndexBootstrapService` `:572/:685/:1108` and `WorkspaceTool` `:1245` — the two the plan did not name. That is how I found the extra holders and checked them. |
| `impact target=ScanAsLeaderUnderGate workspace_id=miller-b275269b2d7c` | 8 impacted symbols (the three call sites at `:947/:1061/:530`, plus `RunDrainTick`, `RunLeadershipSessionAsync`, and the two `*ForTest` seams) and 22 likely tests — which is how I knew to run `IndexerServiceScanTests` and `IndexerServiceLeadershipTests` alongside the new class. |

Follow-up on the two extra holders (read-only checks in the worktree):

- `WorkspaceTool.Open` (`workspace-open-prime`, `:1245`): holds admission through `_registry.MarkScanned` only;
  no `EnsureBuilt`/`Converge` inside the scope. **No leak.**
- `IndexBootstrapService` (`bootstrap`, `bootstrap-auto-rebuild`, `:572/:685/:1108`): no sidecar convergence
  inside the admission blocks. **No leak.** (That file is Task 2's; untouched either way.)

Exact line content was confirmed by reading each region in the worktree before editing, per the brief.

---

## 4. API-shape evidence

- **The type in the plan does not exist under that name in `ScanGovernor.cs`.** `ScanGovernor.cs` defines
  `ScanGovernor`, `ScanGovernorRequest`, `ScanGovernorOwner`, `ScanGovernorLease`. `ScanGovernorAdmission` lives
  in `src/Miller.Indexing/ScanGovernorState.cs:155`. No plan mismatch in substance — just where to look.
- **`ScanGovernorAdmission.Dispose` (`ScanGovernorState.cs:229-236`)** — `if (_disposed) return; _disposed = true;
  _lease.Dispose(); _state?.Exit(...)`. Already idempotent; **no code change was required** by acceptance
  criterion 3, and the new test proves it rather than assuming it.
- **`ScanGovernorLease.Dispose` (`ScanGovernor.cs:50-61`)** — same `_disposed` guard, so the inner release is
  idempotent too.
- **`ScanGovernorAdmission.TryAcquire(ScanGovernor, ScanGovernorState?, ScanGovernorRequest, TimeSpan,
  CancellationToken)`** returns `ScanGovernorAdmission?`; a null `state` or a disabled governor takes the lease
  without publishing a position. The test therefore injects an **enabled** `ScanGovernor.ForMillerHome(tempHome)`
  — a disabled one would publish nothing and the position assertions would pass vacuously.
- **`ScanGovernorState.Shared.Snapshot(workspaceRoot)`** returns `null` when idle; the key is
  `ScanGovernorKey.For(workspace)` = `CanonicalRoot` (`ScanGovernorKey.cs:16-23`). The test keys on
  `workspace.CanonicalRoot`, matching the writer.
- **Re-entrancy**: `ScanGovernor.TryAcquire` throws `InvalidOperationException` when the calling thread already
  holds admission **for that instance** (`_threadHeld.Contains(this)`, `:309`). The probe constructs a *fresh*
  `ScanGovernor` instance, so only the OS `FileShare.None` handle can refuse it — which is exactly the fact
  under test.
- **All governed scan paths are synchronous** (`ops.Scan`, `_scan`, `DrainAndProcess`; `ScanOutcome.Result` is a
  property, not `Task.Result`), so the early `Dispose` runs on the acquiring thread — the thread-static
  `LeaveThread()` bookkeeping stays correct.
- `ExtractReport.Revision => RevisionBlock?.LatestRevisionId` (`ExtractReport.cs:27`) — used to drive the
  cross-workspace test down the `readLatestRevision` branch via `RevisionBlock = null`.

---

## 5. Gate invariants held

- **One extractor machine-wide:** unchanged. Every scan still runs fully inside the admission; the drain path's
  `wholeRepoScanAdmitted: admission is not null` argument is still evaluated before the release. Each site test
  asserts `LeaseFreeDuringScan == false`.
- **No concurrent extract per workspace:** unchanged. Convergence stays inside `_opsGate` (leader) / the
  `SingleWriterLock` (cross-workspace). No lock was moved, added, or reordered.
- **Lock order** `SingleWriterLock -> ScanGovernor -> _opsGate` unchanged; releasing a lease early cannot
  introduce a cycle.
- **Fast suite purity:** the new class spawns nothing, uses a per-test temp miller home, and never touches the
  real user-global `~/.miller/scan`. No `Category=Scale` needed and none added.
- **Build:** `dotnet build Miller.slnx -c Release` → **0 warnings / 0 errors** (with
  `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1`, the documented offline hatch — this
  worktree has no restored `.tools`; without them the pin guard fails before compilation, and compilation itself
  was clean either way).

---

## 6. Test results

**Red first (proof, not assertion).** After the implementation I temporarily neutralised the four
`admission[?].Dispose();` lines and re-ran:

```
Failed!  - Failed: 6, Passed: 1, Skipped: 0, Total: 7
```

All five leader-site tests and the cross-workspace test failed with, e.g.
`Assert.Null() Failure … ScanGovernorSnapshot { State = holding, Reason = leader-drain-rescan }`. The one pass
was the `Dispose` idempotency test, which is behaviour-independent by design. The production files were then
restored byte-for-byte from copies (verified: no `RED-PROBE` markers remain, all four `Dispose` calls present).

**Green — the required command:**

```
dotnet test --filter "FullyQualifiedName~IndexerSidecarAdmissionTests"
Passed! - Failed: 0, Passed: 6, Skipped: 0, Total: 6, Duration: 349 ms
```

**Invariant it proves:** sidecar convergence never runs while this process holds machine-wide scan admission —
neither the OS lease nor the published `ScanGovernorState` position — while the extract itself always does.

**Neighbouring suites** (`IndexerSidecarAdmissionTests`, `IndexerServiceScanTests`,
`CrossWorkspaceRefreshServiceTests`): `Passed! - Failed: 0, Passed: 111`.

**Worker ceiling — full fast suite** (`scripts/test.sh`): `Passed! - Failed: 0, Passed: 6145, Skipped: 2`.
Scale/all NOT run, per the brief.

---

## 7. Concerns

1. **`scripts/test.sh` wall-clock tripwire fired once at 75s (ceiling 30s) — machine contention, not this
   change.** Load average was 25 with three sibling workers building and testing concurrently. A re-run at load
   14 gave **28s, under the ceiling**. The new class contributes 349ms. Worth re-checking on a quiet box before
   the lead's final gate.
2. **`CrossWorkspaceRefreshServiceTests.cs` was edited outside my declared file ownership** (§1). One test,
   inverted in place because it pinned the removed behaviour. Please confirm no sibling touched that file.
3. **One new production member:** `IndexerService.BeforeSidecarConvergeForTest`. Existing seams could observe
   *during the scan* (`RecordingScanOps.WhileScanning`) but nothing could observe *at convergence time*, which
   acceptance criterion 1 explicitly requires ("test observes governor state from within a convergence hook").
   It is `internal`, null in production, and a single `?.Invoke()`.
4. **Scale coverage not exercised.** The real 8-worktree fleet effect (the ~235s ladder in the finding) is not
   measurable from the fast suite. A Scale/real-repo re-run of the P4 validation is the honest confirmation that
   the ladder actually collapses; that is above my ceiling and is the lead's call.
