### Task 8: fast-suite wall-ceiling fix

**Files:**
- Modify: offending test files (discovered by profiling); `scripts/test.sh` only if the comment/target text needs updating — the 30s ceiling value itself does not move up

**Interfaces:**
- Consumes: `scripts/test.sh` tripwire (`FAST_BUDGET_SECONDS=30`, target <10s); current fast wall ~28s (4,168 tests) with observed ambient-load trips at 33s/63s this week.
- Produces: fast suite comfortably under the ceiling (target: ≤20s cold on this machine) via the profile's top offenders — typical moves: retag genuinely heavy tests `Scale`, collapse per-test artifact builds into shared fixtures (`IClassFixture`/collection fixtures), and cut redundant real-SQLite churn in hot test classes. No production code changes.

**Contract inputs:** CLAUDE.md testing rules: the split is load-bearing; guards must not be weakened; a "fast" test doing real I/O belongs in Scale. Raising `FAST_BUDGET_SECONDS` is NOT an accepted fix.

**File ownership:** Modify: offending test files (discovered), possibly `scripts/test.sh` comment; Test: full fast suite

**Serialization required:** Yes

**Dependency reason:** Profiles and edits test files other lanes own; runs after Lanes 1–2 complete.

**What to build:** Headroom. The suite trips its own tripwire under ambient load, which erodes trust in the gate.

**Approach:** `dotnet test --logger "console;verbosity=normal"`-level durations or `trx` report to rank test classes by wall time; fix the top ~5; re-run 3× to confirm stability. Every retag to Scale must satisfy the convention guard (spawns-julie ⟹ Scale stays one-directional; Scale-for-weight is allowed).

**Acceptance criteria:**
- [ ] Fast suite ≤20s cold on this machine, 3 consecutive clean runs, 0 failures, test count accounted for (moved tests still run in `scripts/test.sh all`).
- [ ] No guard weakened; no production code changed; ceiling still 30s.
- [ ] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

