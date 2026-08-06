# P4 Findings Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Fix the four product findings from the P4 scale validation (`docs/findings/2026-08-06-rebind-p4-scale-validation.md`): the machine-wide scan admission held through sidecar convergence, the terminal bootstrap after an admission-wait timeout, the silent rebind ineligibility, and the two small diagnosability gaps (heartbeat-window full-scan miss; null exit code in W8 for exit-3 refusals).

**Architecture:** The scan governor exists to bound concurrent EXTRACTORS, not per-workspace SQLite sidecar builds — every leader path releases its admission the moment the extract subprocess is done, while sidecar convergence stays under the per-process `_opsGate` exactly as today. Bootstrap admission-timeout failures become retryable: a typed exception triggers a delayed background re-run through the existing `StartRunLocked`/`RunBootstrapInBackground` machinery (the replaced-root path already re-runs this way). The rebind prefilter waits out a fresh source heartbeat (bounded) instead of instantly falling back to a full scan.

**Tech Stack:** .NET 10, xUnit, SQLitePCL.raw; no new dependencies.

**Architecture Quality:** Approved shape: admission release is an explicit idempotent `Dispose()` call placed between "extract subprocess returned" and "converge sidecar", per site — no new abstraction, no governor API change. Bootstrap retry reuses the existing re-run entry (`StartRunLocked` + `RunBootstrapInBackground`); no new hosted service, no second timer system (W8 remains the only scan-retry timer — the bootstrap retry is for ADMISSION timeouts where no scan ran). Main risk: releasing admission early must not let two extract subprocesses run concurrently against one workspace — that guarantee comes from `_opsGate`, not the governor, and every touched site must keep convergence inside `_opsGate`. If code reality contradicts this shape, report a plan mismatch; do not redesign locally.

## Global Constraints

- Fast suite stays fast and pure: any test that spawns `julie-extract` is `[Trait("Category","Scale")]` and uses `ScaleTestSupport.RequireJulieServer()`.
- `dotnet build Miller.slnx -c Release` must stay 0 warnings / 0 errors (warnings are errors).
- The governor's ONE-extractor-machine-wide invariant is untouched: no scan (`ops.Scan`, `runner.Scan`, extract subprocess of any kind) may run outside an admission. Only post-scan sidecar convergence moves out.
- W8 discipline: automatic paths never pass `bypassBackoff: true`; the bootstrap retry must not add a second scan-retry timer or bypass the persisted backoff for scan-level failures. Admission-timeout retries are allowed because no scan attempt was recorded.
- `MILLER_SEMANTIC=off` zero-work guarantee and lexical-only byte-identical output are unaffected (no search/semantic surface changes).
- No new MCP tools; no MCP tool description changes.
- Log lines added are single-line, structured (message template + properties), and match the surrounding style.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (fast/scale split, wrapper scripts), `scripts/test.sh`.

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<TestClassName>"` for the classes named in each task.

**Worker ceiling:** the fast suite via `scripts/test.sh`. Workers do not run `scale`/`all`.

**Worker gate invariant:** each task's tests state which finding they close (stated per task below).

**Lead affected-change scope:** `scripts/test.sh` after each batch commit.

**Branch gate:** `dotnet build Miller.slnx -c Release` + `scripts/test.sh all` at the branch head.

**Expensive-specialist (lead-run, after branch gate):** re-run the P4 harness from the session scratchpad against a rebuilt binary — (a) mini-fixture smoke: a worktree opened seconds after the source scan must now rebind after waiting out the heartbeat (was: silent full scan); (b) regenerated 74k fixture, 8-worktree fleet: the ladder must collapse to scan-length spacing, all 8 must converge, none may hit the admission timeout. Hard gates: 8/8 rebound + converged; no `Timed out waiting for machine-wide scan admission` in any log. Report-only: total fleet wall clock.

**Escalation triggers:** any change to `ScanGovernor`/`ScanGovernorState` semantics beyond call-site release ordering escalates to the lead before commit.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in `.razorback/sdd/progress.md`. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Release admission before sidecar convergence | Batch A | Modify: `src/Miller.Server/Hosting/IndexerService.cs`, `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`, `src/Miller.Indexing/ScanGovernor.cs` (only if Dispose idempotency needs enforcing). Test: `tests/Miller.Tests/Server/IndexerSidecarAdmissionTests.cs` (new) | No | None - safe parallel batch. |
| Task 2: Bootstrap self-retry + ineligible-rebind logging | Batch A | Create: `src/Miller.Server/Hosting/ScanAdmissionTimeoutException.cs`. Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs`. Test: `tests/Miller.Tests/Server/BootstrapAdmissionRetryTests.cs` (new) | No | None - safe parallel batch. |
| Task 3: Wait out the source-scan heartbeat window | Batch A | Modify: `src/Miller.Indexing/RebindBootstrap.cs`. Test: `tests/Miller.Tests/Indexing/RebindBootstrapTests.cs` | No | None - safe parallel batch. |
| Task 4: Exit code on IncompatibleExtractException in W8 records | Batch A | Modify: `src/Miller.Indexing/IncompatibleExtractException.cs`, `src/Miller.Indexing/JulieExtractExceptions.cs`, `src/Miller.Indexing/JulieExtractRunner.cs` (rebind exit-3 throw sites only). Test: `tests/Miller.Tests/Indexing/JulieExtractExceptionExitCodeTests.cs` (new) | No | None - safe parallel batch. |

---

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

### Task 2: Bootstrap self-retry after an admission-wait timeout + ineligible-rebind logging

**Files:**
- Create: `src/Miller.Server/Hosting/ScanAdmissionTimeoutException.cs`
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs` (throw site `:574-579`; failure marking `:872-910`; re-run entry `:355-384`; rebind fallback arm `:606-644`)
- Test: `tests/Miller.Tests/Server/BootstrapAdmissionRetryTests.cs` (new)

**Interfaces:**
- Consumes: `StartRunLocked`/`RunBootstrapInBackground` (the existing replaced-root re-run path), `BootstrapPhase`, `_runGeneration` guards, `RebindBootstrapOutcome` (`Kind.Ineligible`, `Reason`).
- Produces: `ScanAdmissionTimeoutException : InvalidOperationException` (message unchanged from today's text); an automatic delayed re-run after an admission-timeout bootstrap failure; one `LogInformation` line naming `rebind.Reason` whenever a rebind attempt is `Ineligible`.

**Contract inputs:** P4 finding §3: wt7 threw at exactly `DefaultBootstrapScanLockWait` (`:1134`, 10 min) and never retried for 50 minutes. P4 finding §6: `Ineligible` logs nothing. The existing replaced-root re-run (`:355-370`) is the pattern to reuse: phase-guarded `StartRunLocked` + `Task.Run(RunBootstrapInBackground)`.

**File ownership:** Create: `src/Miller.Server/Hosting/ScanAdmissionTimeoutException.cs`. Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs`. Test: `tests/Miller.Tests/Server/BootstrapAdmissionRetryTests.cs` (new)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** (a) Type the admission-timeout failure: the `InvalidOperationException` thrown when `AcquireBootstrapScanAdmission` returns null becomes `ScanAdmissionTimeoutException`. (b) In `MarkBootstrapFailed`, when the error is that type, schedule ONE delayed background re-run (fixed 60 s delay + up-to-25% jitter): after the delay, if the phase is still `Failed` and the generation unchanged, start a new run via the same `StartRunLocked` + `RunBootstrapInBackground(rootReplaced: false)` shape the replaced-root path uses. Each failed retry re-schedules — unbounded, because every cycle is one bounded admission wait, no scan runs, and the alternative is a permanently dead server. A shutdown token cancels the pending delay. Deterministic (non-timeout) failures stay terminal exactly as today. (c) In the rebind fallback arm (`:606-614`), add an `LogInformation` line for the `Ineligible` outcome carrying `rebind.Reason` — fresh-workspace bootstraps are rare, so one line per bootstrap is acceptable volume; `Failed` keeps its existing warning.

**Approach:** The retry timer is `Task.Delay` with the shutdown token — no new hosted service, no persisted state (the admission timeout is process-local contention, unlike W8's cross-process scan failures). Use the deterministic-clock seam pattern only if a real `Task.Delay(60s)` cannot be short-circuited in tests — prefer an internal `TimeSpan` override field (`TestAdmissionRetryDelay`) defaulting to the production value, matching the existing `TestBootstrapScanAdmissionWait` precedent (`:1114`). Assert: a timeout failure schedules a re-run and flips phase back to Running; a deterministic failure does not; the generation guard prevents a stale retry from clobbering a newer run; jitter stays within bounds.

**Acceptance criteria:**
- [x] An admission-timeout bootstrap failure re-runs the bootstrap after the (test-shortened) delay; success on the retry binds the workspace.
- [x] A non-timeout bootstrap failure remains terminal (no retry scheduled).
- [x] A stale retry (generation advanced by a replaced-root re-run) is a no-op.
- [x] An `Ineligible` rebind logs one Information line with the reason; `Promoted`/`Failed` logging is unchanged.
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 3: Wait out the source-scan heartbeat window instead of falling back to a full scan

**Files:**
- Modify: `src/Miller.Indexing/RebindBootstrap.cs` (`SourceScanHeartbeatWindow` `:342`, `SourceScanLooksLive` `:576-579`, call site `:435`, `RebindBootstrapSeams`)
- Test: `tests/Miller.Tests/Indexing/RebindBootstrapTests.cs`

**Interfaces:**
- Consumes: `RebindBootstrapSeams.ReadSourceHeartbeatUtc`, `seams.UtcNow`, the governor-admission context (`TryRebind` runs inside the bootstrap's admission).
- Produces: a bounded wait-then-rebind: new seam `public Func<TimeSpan, CancellationToken, bool> WaitBeforeRetry { get; init; }` (production default: `Thread.Sleep`-based wait returning false on cancellation; tests inject instant clocks), and a new internal constant `SourceScanWaitBudget` (60 s).

**Contract inputs:** P4 finding §6: a worktree opened within 30 s of the source scan finishing silently full-scanned (mini-fixture smoke, empirically confirmed). Governor context that makes waiting correct: every Miller scan holds the machine-wide admission, and `TryRebind` already holds it — so a fresh heartbeat almost always means a JUST-FINISHED scan, and waiting ≤30 s is strictly cheaper than the full scan the fallback pays (110-1,345 s measured). The 30 s window itself and the heartbeat seam stay unchanged.

**File ownership:** Modify: `src/Miller.Indexing/RebindBootstrap.cs`. Test: `tests/Miller.Tests/Indexing/RebindBootstrapTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** At the `:435` check, when the source heartbeat is fresh, poll (via the injected wait, 1 s steps or a single computed remainder) until the heartbeat leaves the 30 s window, up to a total budget of `SourceScanWaitBudget` (60 s). If the heartbeat goes stale within budget: proceed with the rebind sequence unchanged. If it stays fresh past the budget (a genuinely live long scan, e.g. an external extractor): return `Ineligible` with the existing reason text plus the waited duration. Cancellation aborts the wait and returns `Ineligible` (shutdown semantics unchanged).

**Approach:** Keep the decision pure: compute wait slices from `seams.UtcNow()` and `ReadSourceHeartbeatUtc` each iteration; the injected `WaitBeforeRetry` makes fast tests instant (a fake clock advances per call). Tests: heartbeat stale on entry (no wait, rebinds — existing behavior); fresh-then-stale within budget (rebinds, waited); fresh past budget (ineligible, reason names the wait); cancelled mid-wait (ineligible, no scan started); heartbeat file absent (no wait — existing behavior).

**Acceptance criteria:**
- [x] A heartbeat that goes stale within the budget leads to a completed rebind (test proves the copy ran after the wait).
- [x] A heartbeat still fresh after the budget yields `Ineligible` whose reason includes the waited duration.
- [x] Cancellation during the wait aborts cleanly with no staging debris.
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 4: Carry the julie-extract exit code on IncompatibleExtractException into W8 records

**Files:**
- Modify: `src/Miller.Indexing/IncompatibleExtractException.cs` (add `public int? ExitCode { get; }` + constructor overload; existing constructors unchanged)
- Modify: `src/Miller.Indexing/JulieExtractExceptions.cs` (`ExitCodeOf` `:39` also reads `IncompatibleExtractException.ExitCode`)
- Modify: `src/Miller.Indexing/JulieExtractRunner.cs` (only the rebind exit-3 refusal throw sites: pass exit code 3)
- Test: `tests/Miller.Tests/Indexing/JulieExtractExceptionExitCodeTests.cs` (new)

**Interfaces:**
- Consumes: `JulieExtractException.ExitCodeOf(Exception?)` (`JulieExtractExceptions.cs:39`), the rebind refusal mapping in `JulieExtractRunner.Rebind` (exit 3 = `fingerprint_mismatch`/`no_committed_revision` → `IncompatibleExtractException`).
- Produces: `IncompatibleExtractException.ExitCode` (nullable, additive — every existing construction site compiles unchanged); `ExitCodeOf` returns 3 for rebind refusals, so `ScanFailureJournal` records `exit_code: 3` instead of null.

**Contract inputs:** P3 standing note (progress ledger + P3 morning report): "exit-3 refusals record null exit code in W8 (no Code property on IncompatibleExtractException)". `RebindBootstrap.cs:516` already calls `JulieExtractException.ExitCodeOf(ex)` on the failure path — no change there.

**File ownership:** Modify: `src/Miller.Indexing/IncompatibleExtractException.cs`, `src/Miller.Indexing/JulieExtractExceptions.cs`, `src/Miller.Indexing/JulieExtractRunner.cs` (rebind exit-3 throw sites only). Test: `tests/Miller.Tests/Indexing/JulieExtractExceptionExitCodeTests.cs` (new)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** An additive nullable `ExitCode` on `IncompatibleExtractException`, populated at the rebind exit-3 throw sites in `JulieExtractRunner`, surfaced through `ExitCodeOf` so the W8 journal's `exit_code` field carries 3 for rebind refusals. Do NOT touch other `IncompatibleExtractException` construction sites (schema gate, version gate) — they stay null, which is honest (no subprocess exit is involved there).

**Approach:** Follow the existing exception style in the file. Tests: `ExitCodeOf` returns the code for an `IncompatibleExtractException` built with one, null for one built without, and still works for `JulieExtractException`; plus one test on the runner's rebind refusal mapping if it is reachable without a subprocess (the parse/mapping helpers are internal — use them; if the mapping is only reachable via the real binary, the mapping test is already covered by `JulieExtractRunnerRebindTests` at Scale and the unit tests stop at `ExitCodeOf`).

**Acceptance criteria:**
- [x] `ExitCodeOf` returns 3 for a rebind-refusal `IncompatibleExtractException` and null for legacy construction sites.
- [x] No existing construction site changed behavior (build clean, fast suite green).
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

---

## Out of scope (recorded, not planned)

- **Sidecar copy/rebind for worktree opens** (the ~200 s search-sidecar build dominating the 457 s open) — a feature with its own design questions (revision-keyed identity across artifact ids), not a finding fix. Needs its own plan if pursued.
- **Failed rebind consumes the W8 slot** — intentional design bias (design doc §7.4).
- **SQLITE_BUSY copy branch untested** — unreachable via the production path by design.
- **`DefaultBootstrapScanLockWait` tuning** — with Task 1 the admission holds shrink to scan length; the 10-minute budget becomes generous rather than starvable. Revisit only if the fleet re-validation still shows waits near the cap.
