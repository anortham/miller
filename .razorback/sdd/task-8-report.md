# Task 8 report — fast-suite wall-ceiling fix

## Status
COMPLETE. Fast suite green, comfortably under the 20s target with 3 consecutive clean runs; the
previously-flaky `IndexerServiceScanTests` timing is hardened. No production code changed, no guard
weakened, ceiling stays 30s.

## Root cause
`JulieDbFixture.Create` — the shared read-contract harness used by ~30 test classes — built a ~30-table
WAL SQLite DB per test with **every DDL/INSERT auto-committed as its own transaction**. Under WAL that is
one WAL-frame flush per statement (~50+ per fixture). With ~16 fixtures building in parallel across the
suite, those flushes serialize on the disk, so the fast suite was disk-fsync-bound, not CPU-bound. That is
exactly why it amplified so violently under ambient load (33s/63s/102s/104s trips): parallel fsyncs
contend super-linearly.

## Moves made (test-only)
1. **`tests/Miller.Tests/Indexing/JulieDbFixture.cs` — batch the fixture build.** Added
   `PRAGMA synchronous=OFF` and wrapped the whole build (all DDL + all INSERTs) in a single raw
   `BEGIN;`/`COMMIT;`. Throwaway test DBs need no durability; one commit replaces ~50 flushes. Raw
   BEGIN/COMMIT (not a `SqliteTransaction`) leaves every `CreateCommand` call site untouched. **The final
   DB file is byte-for-identical** — same tables, same rows, same WAL mode — so every reader's coverage is
   unchanged. This one change is the whole wall-time win because it removes the fsync-contention amplifier,
   which is also the load-fragility the task was chartered to fix.
2. **`tests/Miller.Tests/Server/IndexerServiceScanTests.cs` — de-flake the scan-signal waits.** The six
   `ScanCalled.Wait(5000)` / `acquireAttempted.Wait(5000)` sites already use event-based
   `ManualResetEventSlim` (correct primitive); the fragility was purely the 5s ceiling being too tight when
   the thread pool is starved under load. Introduced `private const int ScanSignalTimeoutMs = 30_000` and
   routed all six waits through it. The event fires in ~90ms on a quiet box, so the happy path is unchanged;
   the ceiling only extends patience under scheduler starvation. Assertion semantics unchanged (still
   `Assert.True(...Wait(...))`).

No tests retagged to Scale — the transaction fix made retagging unnecessary, so fast/scale coverage
placement is untouched.

## Profile — before (baseline plain run: 23s test duration / 26.8s wall)
Ranked by summed CPU (xUnit parallelizes collections; summed CPU >> wall):

| class | sum_s | n | avg_ms |
|---|---:|---:|---:|
| WorkspaceToolTests | 19.46 | 58 | 335.6 |
| SmartTargetResolverTests | 16.77 | 36 | 465.9 |
| EditToolTests | 16.15 | 116 | 139.2 |
| WorkspaceIndexProviderTests | 15.08 | 49 | 307.8 |
| BlazorComponentGraphReaderTests | 14.79 | 27 | 547.7 |
| CliDispatchTests | 14.06 | 157 | 89.5 |
| BlazorNamespaceCatalogTests | 14.03 | 36 | 389.6 |
| MetricsToolTests | 13.95 | 28 | 498.1 |
| ContentToolTests | 11.83 | 82 | 144.3 |
| IndexerServiceScanTests | 11.36 | 29 | 391.6 |

Total CPU across all classes: **414s**. (A follow-up trx profile ran under heavy ambient load and reported
inflated per-class numbers — e.g. InspectTool 4× its quiet-box avg, 42s duration — which is the very
load-sensitivity this task targets; it is noted here as noise, not signal.)

## After — wall times
- Plain `dotnet test --filter Category!=Scale` (no build): **13–14s** test duration; **15.98 / 15.61 /
  16.49s** wall across 3 runs.
- Official ceiling `scripts/test.sh` (includes the incremental build the tripwire measures): **18s / 19s /
  18s** across 3 consecutive runs — all under the 20s target, 11–12s of headroom below the 30s ceiling. All
  runs: `Failed: 0, Passed: 4223, Skipped: 2`.
- Before→after: **~27s → ~18s** ceiling wall (**~23s → ~14s** pure test duration).

## Verification
- **worker-red-green:** touched classes (`IndexerServiceScanTests`, `SmartTargetResolverTests`,
  `EditToolTests`, `MetricsToolTests`, `WorkspaceIndexProviderTests`, `InspectToolTests`) — 335 passed, 0
  failed.
- **Flaky-test stress:** `StartAsync_WhenEnabledLeaderAndSidecarBuildFails_StillMarksRegistryScanned` run
  10× consecutively — 10/10 passed.
- **worker-ceiling:** `scripts/test.sh` ×3 → 18s / 19s / 18s, all green (evidence above).
- **Scale suite:** `scripts/test.sh scale` (binaries present in `.tools/`) — 86 passed, 0 failed. Confirms
  the shared-fixture change did not break Scale consumers.
- **ScaleTraitConventionTests** (runs in the fast suite): green — no julie-spawning test lost its trait.

## Inventory arithmetic (before vs after)
- Fast suite run count: 4225 total (4223 passed + 2 skipped) — **unchanged**.
- Scale suite run count: 86 passed — **unchanged**.
- Distinct listed methods (Theories collapse): fast 4193, scale 86.
- Zero tests moved between suites (no retags), so `scripts/test.sh all` runs the identical inventory; the
  win is purely faster fixture construction. before(fast 4225 / scale 86) == after(fast 4225 / scale 86).

## Miller-first orientation calls used
- `mcp__miller__inspect tests/Miller.Tests/Server/IndexerServiceScanTests.cs` — enumerated the class's
  symbols (fields/methods/the `ScanCalled` event property) before editing.
- Profiling itself was bash/trx work (trx parse via python), as expected for this task.

## Concerns
- Ceiling wall is 18–19s: meets the ≤20s target but the ~4–5s incremental build inside `dotnet test`
  (which the tripwire measures by design, and which the task forbids removing via `--no-build`) is a fixed
  floor. Pure test execution is ~14s. If more margin is ever wanted, the next lever is `IClassFixture`
  sharing for the heavy read-only classes (SmartTargetResolver/Edit/Metrics/Inspect build an identical
  immutable fixture per test) — deliberately not done here because the target is met and per-class
  rewrites carry more risk than the transaction fix's zero-semantic-change win.
- `PRAGMA synchronous=OFF` is safe only because these are throwaway per-test DBs deleted on Dispose; it
  must never migrate to production fixture/DB code.
