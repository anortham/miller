### Task 1: Release the machine-wide admission before sidecar convergence

**Files:**
- Modify: `src/Miller.Server/Hosting/IndexerService.cs` (admission sites: `:538` leader-upgrade, `:617-647` leader-drain-rescan, `:813-843` leader-startup, `:959` leader-ondemand, `:1091` leader-requested-full; convergence helpers `:1117-1230`)
- Modify: `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs` (admission `:442-459`; verify whether its `TryConvergeSidecar` call sits inside the admission scope and apply the same release-early ordering if it does)
- Modify (only if needed): `src/Miller.Indexing/ScanGovernor.cs` / `ScanGovernorAdmission` for idempotent `Dispose`
- Test: `tests/Miller.Tests/Server/IndexerSidecarAdmissionTests.cs` (new)

**Interfaces:**
- Consumes: `ScanGovernorAdmission` (IDisposable), `TryConvergeSidecar`/`TryConvergeSidecarToLatest`, `_opsGate` discipline.
- Produces: the invariant "sidecar convergence never runs while this process holds a machine-wide scan admission" on every leader path and the cross-workspace refresh path. No signature changes.

**Contract inputs:** P4 finding §3 (`docs/findings/2026-08-06-rebind-p4-scale-validation.md`): the observed ~235 s fleet ladder and the starvation exception naming `leader-drain-rescan`. The comment at `IndexerService.cs:835-837` proves convergence needs `_opsGate`, not the governor.

**File ownership:** Modify: `src/Miller.Server/Hosting/IndexerService.cs`, `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`, `src/Miller.Indexing/ScanGovernor.cs` (only if Dispose idempotency needs enforcing). Test: `tests/Miller.Tests/Server/IndexerSidecarAdmissionTests.cs` (new)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** At every site that acquires a scan admission and later converges a sidecar, dispose the admission as soon as the extract subprocess has returned (or, on the drain path, as soon as `DrainAndProcess` has returned), BEFORE `TryConvergeSidecar`/`TryConvergeSidecarToLatest` runs. Convergence stays inside `_opsGate` exactly as today. `using` declarations that would hold to scope end are restructured (explicit early `Dispose()` on the declared variable is fine — verify and, if necessary, make `ScanGovernorAdmission.Dispose` idempotent so the `using` epilogue is a no-op).

**Approach:** Verify Dispose idempotency first (read `ScanGovernorAdmission`). Then per site: insert `admission?.Dispose();` after the scan/drain call, with the one comment stating the constraint ("the governor bounds extractors; the sidecar build is per-workspace work under _opsGate"). Test seam: `IndexerService` already exposes `DrainForTest` (`:1329`) and `BetweenScanPeekAndDrainForTest` (`:620`); add a narrow internal test hook only if the existing seams cannot observe admission state at convergence time — prefer asserting through `ScanGovernorState.Shared`'s owner record (the advisory owner file names holder + reason) captured by a fake convergence callback. Do not weaken the one-extractor invariant: the scan itself stays fully inside the admission.

**Acceptance criteria:**
- [ ] On the drain path, a whole-repo scan still runs only under admission, and `TryConvergeSidecarToLatest` runs after the admission is released (test observes governor state from within a convergence hook).
- [ ] Same ordering on leader-startup, leader-ondemand, leader-requested-full, leader-upgrade, and (if applicable) cross-workspace refresh.
- [ ] `ScanGovernorAdmission.Dispose` is provably idempotent (test).
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode.

