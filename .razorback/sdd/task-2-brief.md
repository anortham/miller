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
- [ ] An admission-timeout bootstrap failure re-runs the bootstrap after the (test-shortened) delay; success on the retry binds the workspace.
- [ ] A non-timeout bootstrap failure remains terminal (no retry scheduled).
- [ ] A stale retry (generation advanced by a replaced-root re-run) is a no-op.
- [ ] An `Ineligible` rebind logs one Information line with the reason; `Promoted`/`Failed` logging is unchanged.
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode.

