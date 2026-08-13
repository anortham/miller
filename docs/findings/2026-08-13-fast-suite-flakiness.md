# The fast suite is ~30% red on an unchanged tree

- **Date:** 2026-08-13
- **Status:** Open. One of three confirmed instances is fixed.
- **Severity:** This is the top blocker on trusting CI. A gate that fails 30% of the time on good code
  cannot tell a real regression from noise, and every red run costs a human the time to re-read it.

## The measurement

Ten consecutive `dotnet test Miller.slnx -c Debug` runs on Windows, on one unchanged tree, no edits between
runs. Three failed. Each failure was a DIFFERENT test with a DIFFERENT mechanism.

| Run set | Runs | Failures | Test that failed | Elapsed | Mechanism |
|---|---|---|---|---|---|
| A (before the fix below) | 3 | 1 | `BootstrapAdmissionRetryTests.AStoreRollbackFailureRetriesUntilTheBootstrapBinds` | 5 s | Wall-clock deadline |
| B | 4 | 1 | `JulieSchemaGateTests.Verify_MissingRequiredSchemaFiveTable_ThrowsNamingTheTable("pending_resolutions")` | 88 ms | Unknown, NOT a timeout |
| C | 3 | 1 | `SharedSemanticBrokerConnectionFactoryTests.ExitedOwner_IsRetiredBeforeTheReplacementOverwritesItsHandles` | 570 ms | `Assert.NotNull` failed |

A green run proves nothing at this rate. Neither does a red one.

## This matches CI exactly

| CI run | Commit | Failing job |
|---|---|---|
| #342 | `a94d7571` | none |
| #343 | `a94d7571` | `windows-scale-smoke` |
| #344 | `0da4f60a` | `windows-fast` |
| #345 | `3ad8164f` | `build-test` (ubuntu) |

Runs #342 and #343 executed the SAME commit and disagreed. Run #345 differs from #344 only in documentation,
and `windows-fast` went from red to green across them — which is what proved #344's `windows-fast` failure was
noise rather than a regression from the v1.19.0 code.

Do not read a single red CI run on this repo as a regression without reproducing it.

## Instance 1: fixed

`BootstrapAdmissionRetryTests.WaitUntil` used a hardcoded 5-second deadline. The test awaits a thread-pool
continuation behind a 20 ms retry delay while ~6,500 tests run in parallel. Under that contention the pool can
starve the continuation past five seconds, and the test then fails at exactly its deadline with nothing wrong.

Both hardcoded 5-second backstops are now 60 seconds, documented as liveness backstops rather than performance
budgets: `BootstrapAdmissionRetryTests.WaitUntil` and its latent twin
`WorkspaceBindingServiceTests.WaitUntilAsync`.

Raising a backstop cannot slow a passing test — the loop returns the instant the condition holds — and 60 s
stays well under CI's `--blame-hang-timeout 120s`, so a genuinely stuck bootstrap still fails loudly. The test
did not recur in the 7 runs after the change, which is weak evidence at a 1-in-3 base rate, not proof.

**Note on weak evidence:** running the two affected classes alone 10 times also passed 10/10, and that result
is worthless — 29 tests in isolation have none of the thread-pool contention that causes the failure. Only
full-suite runs reproduce the conditions. Do not accept a focused-run green as proof for a contention flake.

## Instances 2 and 3: not diagnosed

Both need evidence before anyone changes code.

- `JulieSchemaGateTests` failed in 88 ms, so it is not a timeout. The class-level fixture is already well
  isolated: `JulieDbFixture` creates a unique GUID temp directory per instance and its `Dispose` calls
  `SqliteConnection.ClearPool` rather than the process-global `ClearAllPools`, with a comment saying that
  choice exists to protect a concurrently running test. So the obvious shared-state suspects are already
  handled and the real mechanism is something else. The failing theory case was `pending_resolutions`, which
  is probably arbitrary.
- `SharedSemanticBrokerConnectionFactoryTests` failed an `Assert.NotNull` at line 307/311 after 570 ms.

## An unproven hazard, recorded so it is not re-discovered as a cause

The fast suite mutates process-global state from classes that xUnit runs in parallel. No
`DisableTestParallelization` exists anywhere, and `test.runsettings` sets only `MILLER_INDEX_STORE=off`.

- `Directory.SetCurrentDirectory` at 7 sites in `WorkspaceBindingServiceTests`, which is not Scale-tagged and
  carries no `[Collection]`. It restores in a `finally`, but the window between set and restore is when other
  concurrent tests resolve relative paths.
- `Environment.SetEnvironmentVariable` at 16 sites in `CliDispatchTests`, 11 in `VectorConvergeServiceTests`,
  and across 10 further fast-suite files.

This is a genuine hazard and worth removing. **It is NOT established as the cause of any failure above.** It
was proposed as the cause during this investigation and then contradicted by the evidence — instance 1 turned
out to be a timeout budget. State it as a hazard, not a diagnosis, until a failure is traced to it.

## Recommended next steps

1. Build a stress harness that runs the fast suite N times and aggregates victims by name and frequency,
   rather than sampling by hand. The base rate is high enough that ~20 runs would rank the offenders.
2. Diagnose instances 2 and 3 from that data.
3. Remove the process-global mutations by injecting a lookup, following the pattern `MillerHome.Resolve`
   already establishes. Do NOT reach for `DisableTestParallelization`: the CLAUDE.md testing rules exist
   because julie's suite grew to 30+ minutes, and serializing ~6,500 tests would recreate that.
4. Only then treat a single red CI run as actionable.
