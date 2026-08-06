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
- [ ] A heartbeat that goes stale within the budget leads to a completed rebind (test proves the copy ran after the wait).
- [ ] A heartbeat still fresh after the budget yields `Ineligible` whose reason includes the waited duration.
- [ ] Cancellation during the wait aborts cleanly with no staging debris.
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode.

