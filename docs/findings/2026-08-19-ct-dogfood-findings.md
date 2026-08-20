# CT dogfood on the Miller repo — findings (2026-08-19)

First real dogfood of the `tests` tool and CT daemon on the Miller workspace itself
(miller 1.20.1+f955e1de). The session found nine defects. Six are fixed in this change set,
test-first. Three are open design questions.

## Fixed in this change set

1. **Discovery accepted any `.csproj` with "Test" in the name.**
   `ContinuousTestProjectInventory.TryIdentify` used a `namedTest` file-name heuristic. It enabled
   `src/Miller.Testing` (a class library) and `tests/Miller.SharedBrokerTestHost` (a helper host).
   Both produced `ct_discovery_failure` runs (exit 134 on a missing `--endpoint` argument; a
   missing executable) and turned the aggregate verdict red on an otherwise green repo.
   Fix: a dotnet project is a test project only on a real signal — an xunit/NUnit/MSTest
   reference, `Microsoft.NET.Test.Sdk`, `Microsoft.NET.Sdk.Test`, or `Microsoft.Testing.Platform`.

2. **Enable seeded no trait exclusions, so CT ran the Scale suite.**
   The xunit provider runs the built test executable directly, so the csproj's
   `VSTestTestCaseFilter=Category!=Scale` (a `dotnet test` default) did not apply. The first CT
   run executed the full suite, Scale tests included.
   Fix: enable parses `VSTestTestCaseFilter` and maps pure `Name!=Value` conjunctions onto
   `exclude_traits` (`Category!=Scale` → `Category=Scale`). Any other filter shape seeds nothing.
   Verified live: the run argv carries `-trait- Category=Scale` and no Scale tests.

3. **Discovery storage committed one row at a time.**
   `ContinuousTestStoreApplier.ApplyDiscovery` called `PutTestCase` outside a transaction: each
   row paid a file lock + fresh connection + schema guard + autocommit (~35 rows/s observed,
   minutes of write-lock hold for ~6,000 cases; concurrent readers saw "database is locked").
   Fix: the prune+insert now runs in one `ContinuousTestStore.Transaction`, which also makes
   discovery apply atomic.

4. **The daemon heartbeat never refreshed.**
   `daemon.heartbeat.json` was written once at lease acquisition; `CtDaemonLease.Heartbeat()` had
   no production caller. Fix: `ContinuousTestDaemonHost` runs a `PulseHeartbeatAsync` background
   task (`HeartbeatInterval`, default 15s) that keeps the file fresh even while a long drain
   blocks the main loop. Verified live during a run.

5. **A stale stop request killed the next daemon at startup.**
   A `tests stop` left unacknowledged by a dead daemon was consumed by the NEXT daemon start,
   which acked it and threw ("ct-daemon failed: The operation was canceled") ~170ms after launch.
   Observed live. Fix: stop requests older than the daemon's start are acked as
   `stale-stop-ignored` and skipped.

6. **CT running Miller's own suite poisoned CT CLI tests (the 36-minute hang).**
   The provider exports `MILLER_CT_WORKSPACE_ROOT` into every spawned test process, and the
   `ct-daemon` verb preferred that env var over the explicit CLI workspace context. Under CT,
   `TestsCliTests.CtDaemonVerb_WhenNotEnabled_ReturnsWithoutCreatingState` therefore bound the
   REAL repo (CT-enabled, lock free), acquired the real daemon lease, and became a live daemon
   loop inside the test — forever. Diagnosed with a live managed-stack dump.
   Fix: the daemon spawn now uses a dedicated `MILLER_CT_DAEMON_WORKSPACE_ROOT` variable and the
   verb never consults the provider-facing one (`CtEnvironment.ResolveDaemonWorkspaceRoot`).

Also: the compact `tests status` line `selected:` printed the full store cursor (300+ chars); it
now prints `rev N (24-char-prefix…)`. JSON is unchanged.

## Open findings (design decisions, not fixed)

7. ~~**No run-level stall policy.**~~ **FIXED 2026-08-20.** A test process that produces no output
   for 10 minutes is now treated as wedged: its process tree is killed and the run FAILS with a forced
   non-zero exit code. The bound is on SILENCE rather than total duration, so a slow suite survives and
   a wedged one does not. `MILLER_CT_STALL_TIMEOUT` overrides the bound; `off` disables it. See
   [`contracts/tests-cli-v1.md`](../contracts/tests-cli-v1.md#run-stall-bound).

   Still open in this area: the drain blocks the loop, so a stop command is not processed until the run
   ends. The stall bound now caps how long that can last, but it does not make stop responsive.

8. ~~**Unbounded per-method argv.**~~ **FIXED 2026-08-20**, in two parts. `CtArgvChunking` bounds every
   selection so no invocation can exceed the command-line limit (the `.cmd` shim used by npm/pnpm/yarn
   caps far lower — a measured ~8,331 characters exits 1 with "The command line is too long" and no
   output, which a provider reads as a failed run). And a run that covers every known case now sends an
   EMPTY selection, so the whole assembly runs once under its seeded trait exclusions instead of
   chunking ~6,000 `-method` pairs across ~50 processes. See
   [`2026-08-19-ct-checkin-workspace-scope-selection.md`](2026-08-19-ct-checkin-workspace-scope-selection.md).

9. **Enable does not reconcile stored rows.** Projects that discovery no longer returns stay in
   `ct_test_projects` (this session left them disabled by hand). Rows carry no provenance, so an
   automatic prune cannot tell an auto-discovered row from an explicit `--project` enable.

## Operational notes

- Status reads under discovery-write load returned "database is locked" to a plain
  `sqlite3` reader; Miller's own reads retry, but the write-lock hold windows in (3) were the
  real cause and are now gone.
- `tests run` via MCP with no daemon degrades to a foreground one-shot inside the MCP server
  process and blocks the tool call (>120s observed). Worth a contract note or an async handoff.
- The `tests failures` next-step hint says "inspect — open a failing test" even when the
  failures are discovery failures with nothing to inspect.
