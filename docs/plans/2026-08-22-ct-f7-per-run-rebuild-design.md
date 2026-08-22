# CT F7 — a fresh build generation per run — design

Date: 2026-08-22. Status: **design pass only, not approved, nothing implemented.**
Source finding: F7 in [`docs/findings/2026-08-21-ct-cross-repo-dogfood.md`](../findings/2026-08-21-ct-cross-repo-dogfood.md).

## Problem

An explicit `tests run` on a large cargo workspace (`julie-extractors`, 4,173 cases) spends about
3.5 minutes and writes about 8 GB before one test can execute. The finding named
`ContinuousTestDaemonQueue.cs:226` as the cause. That line is only half of it, and it is the smaller
half. The doc below records what the code actually does, verified against the current tree.

### The chain, verified

1. Both explicit-run entry points enqueue with `WorkspaceScope: true`:
   `TestsCore.cs:1102-1109` (the one-shot CLI / MCP path) and `ContinuousTestDaemonHost.cs:904-911`
   (the daemon `run` command). A grep of `src/` finds exactly three `WorkspaceScope: true` sites, and
   the third (`ContinuousTestDaemonQueue.cs:762`) is the queue's own re-selection after discovery. So
   **workspace scope means explicit run and nothing else** — the automatic poller path never sets it.
2. `ContinuousTestDaemonQueue.cs:226` sets `RefreshInventory: change.WorkspaceScope` on the pending run.
3. `DrainReadyAsync` calls `RefreshInventoryIfNeededAsync` first (`ContinuousTestDaemonQueue.cs:352`).
   The short-circuit at `:736-741` only fires when `RefreshInventory` is false, so an explicit run always
   falls through to `_coordinator.DiscoverAsync` (`:745-747`).
4. `ContinuousTestCoordinator.DiscoverInsideProjectGateAsync` (`ContinuousTestCoordinator.cs:135-171`)
   calls the provider's `DiscoverAsync`. This is the ONLY production call site of coordinator discovery
   (`grep DiscoverAsync src/` — the other hits are the interface, the factory wrapper, and the providers).
5. `RustTestProvider.DiscoverAsync` (`RustTestProvider.cs:45-61`) calls
   `_generations.AllocateForDiscovery(workspace)` (`:52`), which calls `CtGenerationPaths.Allocate`
   (`CtGenerationHandoff.cs:28-34` → `CtGenerationPaths.cs:37-69`). `Allocate` reads the highest ordinal
   under the build output root and creates the NEXT one. It is an unconditional new empty directory.
6. The generation directory IS the cargo cache. `WorkspaceEnvironment` sets
   `CARGO_TARGET_DIR = <generation>/target` (`RustTestProvider.cs:1305`, `TargetDir` at `:1292`), and
   every cargo argv also passes `--target-dir <generation>/target` explicitly
   (`BuildGateCommand:527`, `ListCommand:544`, `RunCommand:565`).
7. Discovery then runs, against that empty cache: `cargo metadata --no-deps` (`:513-519`),
   `cargo test --no-run --workspace` (`:521-530`), and one `cargo test -p <pkg> … -- --list` per
   test-capable target (`:532-550`, driven from `:87-111`). The `--no-run` gate compiles the whole
   workspace and all of its dependencies from scratch. **That is the 3.5 minutes and the 8 GB.**
8. The run does not pay it twice. `CtGenerationHandoff.TakeForRun` (`CtGenerationHandoff.cs:43-53`)
   hands the discovery's generation to the run that follows, so the run's own build gate
   (`RustTestProvider.cs:291`) finds a warm directory. The handoff was added for exactly this reason
   (its own doc comment, `CtGenerationHandoff.cs:8-11`).
9. After the run, `MarkCtGenerationComplete` (`ContinuousTestCoordinator.cs:274-276`) and
   `RunMaintenanceTail(active)` (`:279`) run. `ReapSupersededGenerations` (`:403-442`) retains only the
   active generation and the newest `complete` one (`:439-441`) and reaps everything else. So the
   PREVIOUS run's warm cache is deleted as soon as the current one has finished being rebuilt.

Net effect: the cargo compilation cache is created empty, filled, kept for exactly one run, and deleted.
No incremental state ever survives from one explicit run to the next.

### The important consequence: the queue line is not the fix

`ContinuousTestDaemonQueue.cs:226` decides whether **discovery** runs. It does not decide whether a
**build** runs. If discovery were skipped, `RustTestProvider.RunAsync` would call
`CtGenerationHandoff.TakeForRun` with nothing pending (`CtGenerationHandoff.cs:46-52`), fall through to
`CtGenerationPaths.Allocate`, and get the same empty directory — and the run's own build gate
(`RustTestProvider.cs:289-297`) would compile the whole workspace there. The 3.5 minutes would move
from discovery to the run and stay the same length.

So F7 is two defects sharing one symptom:

- **F7a — forced rediscovery.** Every explicit run re-runs `cargo metadata` plus one `--list` process
  per test-capable target, whether or not the test inventory could have changed.
- **F7b — cold build cache per operation.** Every CT operation allocates a fresh generation, and for
  cargo the generation IS `CARGO_TARGET_DIR`.

F7b dominates and F7a cannot be fixed without it.

### Why the dogfood run attributed all 3.5 minutes to discovery

The F6 investigation proved the generation's `TestResults/` directory was empty, and concluded the time
was spent in discovery. That reading was correct **for that run**: F6 meant the run started no cargo
process at all, so discovery was the only thing that built anything. With F6 fixed, the same 3.5 minutes
is still paid — the build gate simply moved inside the logged run path when discovery is skipped.

### Cost split, by provider

| Provider | Discovery cost | Generation cost per operation |
|---|---|---|
| cargo | `cargo metadata` + `cargo test --no-run --workspace` + one `--list` per target (`RustTestProvider.cs:73-111`) | whole `CARGO_TARGET_DIR`, ~8 GB on julie-extractors |
| dotnet | one `dotnet build` + one discovery invocation (`DotnetTestProvider.cs:128-160`) | **only `OutDir`** — `--artifacts-path <BuildOutputRoot>` (`DotnetTestProvider.cs:1094-1095`) keeps the intermediate tree SHARED across generations, so compilation stays incremental and only the output copy repeats |
| pytest | filesystem walk, no process (`PythonTestProvider.cs:27-56`) | temp/results only |
| vitest / jest / node-test | filesystem walk, no process (`JavaScriptTestProvider.cs:36-71`) | `<generation>/cache` for `--cache.dir` / `--cacheDirectory` / `NODE_COMPILE_CACHE` (`JavaScriptTestProvider.cs:1176-1177`, `684-692`, `1172`) — cold on every run |

The dotnet row is the point. **Miller already ships the split this design proposes** — a project-stable
incremental cache plus a per-generation output directory. Rust and Node are the outliers.

## Invariants a fix must not break

1. **Freshness is the composite `(IndexGenerationIdentity, revision)`.** Build artifacts are not part of
   the key and must never become part of it. A reused build directory must not make any result look
   fresher than the index says it is.
2. **An identity change makes every result stale.** The rebuild fail-safe is absolute. Reuse of a build
   directory must not be readable as reuse of a verdict.
3. **Green requires complete results at the selected key.** A build shortcut that lets a run report a
   verdict for a case it did not execute is the F6 failure class again.
4. **Truncated or unknown impact means nothing executes** — never a whole-suite fallback.
5. **Providers write only under supervised CT paths.** `ValidateBuildOutputRoot`
   (`ContinuousTestDaemonQueue.cs:1016-1030`) refuses a build output root inside the workspace root, and
   `MaterializeProjectWorkItems` (`ContinuousTestProjectInventory.cs:178-182`) puts it at
   `<os-temp>/miller-ct/build/<workspace hash>/<project hash>`. Any shared cache must live there too —
   never in the workspace's `bin`/`obj`/`target`.
6. **Per-run scoping of results and coverage must survive.** The rust coverage scan walks the
   operation's own `GenerationRoot` precisely so a run cannot adopt an older run's lcov file
   (`RustTestProvider.cs:1143-1158`). Run logs are already per-run by name
   (`ResultArtifactPath`, `:1180-1184`), but coverage is scoped by directory.
7. **A dying test process must not corrupt the next run's build.** This is the written reason for the
   per-generation target directory (`RustTestProvider.cs:1290-1291`).
8. **Generation disk stays accounted and bounded.** `MeasureGenerationDisk` /
   `MeasureRootBytes` / `IsGenerationContent` (`ContinuousTestCoordinator.cs:488-608`) count only
   directories that pass `CtGenerationPaths.IsGenerationId` plus reap remnants, against a 20 GB default
   budget (`ContinuousTestCoordinator.cs:12`). A cache that sits outside that set becomes invisible to
   the budget.
9. **The reap must not delete a directory a future run depends on.** `ReapSupersededGenerations`
   currently reaps any generation-shaped directory that is not the active one, the newest complete one,
   or the newest on disk (`:415-435`).
10. **Concurrency is already bounded.** `ProjectGates` serializes discovery and runs per build output
    root inside one process (`ContinuousTestCoordinator.cs:54, 106-133, 357-358`), and the user-global
    CT budget allows one workspace to execute at a time. Cross-process contention on one project is
    possible (a one-shot CLI run beside a daemon), and already exists today through the ordinal
    allocator.

## Options

### Option A — split the build cache out of the generation

Keep per-operation generations for everything that must be per-run — results, coverage, logs, temp —
and give the compiler a **project-stable** cache directory beside them, at
`<BuildOutputRoot>/cache/<tool>`. For cargo that directory becomes `CARGO_TARGET_DIR` and the
`--target-dir` argument. For vitest/jest/node the same directory replaces `<generation>/cache`.

The name `cache` cannot collide with a generation: `CtGenerationPaths.IsGenerationId`
(`CtGenerationPaths.cs:140-155`) accepts only `g` plus twelve lowercase hex characters.

- **What invalidates it.** Nothing routine. Cargo and MSBuild own their own invalidation by file
  fingerprint, exactly as they do in a developer's own checkout — that is the mechanism this option
  leans on, and it is the same one the dotnet provider has leaned on since `--artifacts-path` landed.
  A coarse guard sits on top: a recorded input fingerprint (cargo/rustc version, lockfile hash,
  provider version) whose mismatch wipes the cache, plus "two consecutive build-gate failures wipe the
  cache and retry once".
- **What happens on a stale reuse.** The compiler rebuilds what changed. It cannot report a verdict,
  so no CT invariant is reachable from here — a wrong cache produces a build failure, which already
  becomes a visible discovery/run failure (`RecordDiscoveryFailure`,
  `ContinuousTestDaemonQueue.cs:831-849`, and the run's `ContinuousTestProviderException` path) and
  self-recovers on the next pass.
- **Failure modes.**
  - *Coverage scoping breaks.* Cargo writes lcov/cobertura under `CARGO_TARGET_DIR`. Move the target
    dir out of the generation and `DiscoverCoverageArtifacts` (`RustTestProvider.cs:1148-1158`) finds
    nothing — or, worse, finds a previous run's file if it is re-pointed naively. This is the real work
    item, and it is solvable: scan the cache dir but accept only files whose write time falls inside
    this run, or keep the coverage profile output redirected into the generation.
  - *Disk accounting goes blind.* `IsGenerationContent` (`:606-608`) would not count the cache, so the
    20 GB budget would stop seeing the largest directory on disk. Must be extended.
  - *A wedged process holds the shared cache.* Today a fresh directory sidesteps a stuck file lock.
    The wipe-and-retry path above is the replacement, and the stall detector already kills the process
    tree before this can persist.
- **Cost after the fix.** The 8 GB is built once per project and then maintained incrementally. Peak
  disk falls too: today up to two full generations coexist (the new one being built plus the newest
  complete one retained by `:439-441`).

### Option B — reuse the newest complete generation when its inputs match

Record a build-input fingerprint in the generation's allocation marker
(`CtGenerationPaths.cs:168-189` already writes an ordinal there). Add
`CtGenerationHandoff.TakeWarm(workspace, fingerprint)`: reuse the newest `complete` generation whose
fingerprint matches, allocate only when none does. The reap gains a "never reap the warm target" rule.

- **What invalidates it.** Fingerprint mismatch: toolchain version, lockfile/manifest hash, provider
  version, a changed `Framework` or custom `Command`.
- **What happens on a stale reuse.** Same as A — the compiler still does the incremental work; the
  fingerprint is a safety net, not the correctness mechanism.
- **Failure modes.** It breaks the "one-shot on purpose" property the handoff was written to guarantee
  (`CtGenerationHandoff.cs:13-15`): two runs would share one directory, so a dying test process from run
  N can now corrupt run N+1's whole tree, not just its compile cache. Coverage scoping
  (invariant 6) also breaks, and worse than in A: two runs in the same directory means run N+1 can find
  run N's lcov with no directory boundary left to separate them. Generation state
  (`allocated`/`complete`) stops describing one operation.
- **Cost after the fix.** Same win as A for cargo, and it additionally warms the dotnet `OutDir` copy.

### Option C — stop forcing rediscovery on every explicit run

Narrow `ContinuousTestDaemonQueue.cs:226` from `RefreshInventory: change.WorkspaceScope` to a real
condition: refresh when the stored inventory is empty, when the index generation identity has moved
since the last discovery, when a changed path matches the project's manifest or test-file shapes, or
when the user asks for it explicitly. Persist `last_discovery_identity` per project in `ct.db`.

- **What invalidates it.** Identity change, empty inventory, manifest/test-file change, explicit request.
- **What happens on a stale reuse.** A newly added test is missing from the run. This is a genuine
  coverage regression, and a sharp one: because `WorkspaceScope` is set on exactly the two explicit-run
  sites, **the explicit run is currently the only path that ever refreshes inventory at all**. The
  automatic path reaches discovery only through the empty-inventory clause at
  `ContinuousTestDaemonQueue.cs:736-738`. Narrowing C without adding a revision-driven rediscovery
  trigger converts a slow-but-correct behaviour into a fast-and-silently-incomplete one.
- **Failure modes.** Silent under-coverage; a green verdict over a suite that no longer matches the tree.
- **Cost after the fix.** For cargo it saves `cargo metadata` plus one `--list` process per
  test-capable target — real, but not the 8 GB. **Option C alone does not fix F7.**

## Recommendation

**Take Option A. Treat Option C as a follow-up that must not ship first.**

Rationale:

1. A puts the fix where the cost is. The compiler's own cache is the thing being thrown away, and
   cargo is designed for that cache to persist across invocations.
2. A leaves every CT invariant untouched by construction. Freshness keys, the staleness rules, whole-suite
   selection, run-artifact scoping and the reap all continue to work on per-operation generations. The
   only thing that stops being per-operation is a compiler cache, which carries no verdict.
3. A copies a split that already ships in this repo and is proven in production — the dotnet provider's
   `--artifacts-path <BuildOutputRoot>` plus per-generation `OutDir`
   (`DotnetTestProvider.cs:1094-1099`). Rust and Node are the outliers, and bringing them into line is a
   smaller change than inventing a new lifecycle.
4. B buys the same thing for cargo but pays with invariant 6 and invariant 7 across the whole
   generation tree, and it makes the `allocated`/`complete` states stop meaning one operation.
5. C is worth doing, but as a second change with its own rediscovery trigger. Sold as the fix for F7 it
   would move the 3.5 minutes rather than remove it.

### Known limits of Option A

- The cache is keyed by `(workspace id, project id)`
  (`ContinuousTestProjectInventory.cs:178-182`). Two worktrees of one repo have different workspace ids,
  so each pays its own first full build. That is correct — they can hold different source states — but
  it should be stated, not discovered.
- The first explicit run on a new project still costs the full 3.5 minutes. A is about the second run
  and every run after it.

### Implementation scope

Files to touch:

- `src/Miller.Testing/Providers/Shared/CtGenerationPaths.cs` — add a project-stable cache-root helper
  (`CacheRoot(workspace)` → `<BuildOutputRoot>/cache`), beside the existing per-generation record.
- `src/Miller.Testing/Providers/Rust/RustTestProvider.cs` — `TargetDir` (`:1292`) keys on the workspace
  rather than the generation; `WorkspaceEnvironment` (`:1294-1312`) and the four argv builders
  (`MetadataCommand:513`, `BuildGateCommand:521`, `ListCommand:532`, `RunCommand:552`, plus
  `BuildCustomCommand` / `WorkspaceCommand`) follow; `DiscoverCoverageArtifacts` (`:1148-1158`) is
  re-scoped with a per-run acceptance rule.
- `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs` — `IsGenerationContent` /
  `MeasureRootBytes` (`:581-608`) count the cache root; `ReapSupersededGenerations` (`:403-442`)
  never reaps it; add the wipe-on-repeated-build-failure path.
- `src/Miller.Testing/Providers/Node/JavaScriptTestProvider.cs` — `CacheDirectory` (`:1176-1177`)
  moves to the same stable root. Second step; independent of the rust change.
- Possibly `src/Miller.Testing/Store/ContinuousTestStore.Generations.cs` — a cache-bytes row if the
  disk accounting wants to report the cache separately from the generations.

Tests needed (all fast-suite unless marked):

- `tests/Miller.Tests/Testing/Providers/Rust/RustTestProviderTests.cs` — `AssertUsesGeneration`
  (`:1136-1140`) currently asserts `CARGO_TARGET_DIR == <generation>/target`. It becomes: the target dir
  is the project-stable cache root, and it is IDENTICAL across two allocations.
- New: two consecutive discover-then-run cycles on one workspace produce the same `CARGO_TARGET_DIR`
  and two different generation ids.
- New: `ReapSupersededGenerations` removes superseded generation directories and leaves the cache root.
- New: the cache root's bytes count against `GenerationDiskBudgetBytes`, so the over-budget diagnostic
  still fires.
- Coverage scoping: re-point the existing stale-coverage test (`RustTestProviderTests.cs:586-600`) at
  the new shape and prove run N+1 does not adopt run N's lcov.
- New: a build-gate failure twice in a row wipes the cache and retries once; a single failure does not.
- `Category=Scale` (opt-in, real cargo): assert the second cycle's target directory is already populated
  before the second discovery starts. Prefer this filesystem assertion over a wall-clock assertion —
  timing tests on a busy machine are the flake source this repo already knows about.

Nothing above changes `ct.db`'s freshness columns, the selection rules, or the tests CLI/JSON contract.

## Implementation status (2026-08-22)

Option A shipped (merge `0b08903d`, branch lane `a09956b7`), test-first; every test above exists
and runs green. Two recorded deviations from this doc:

1. **The input fingerprint guard (toolchain/lockfile) was NOT built.** Cargo's own fingerprints
   invalidate the cache correctly and incrementally on a toolchain or `Cargo.lock` change; a coarse
   guard would discard an ~8 GB cache for changes cargo would absorb by rebuilding a few crates.
   The two-consecutive-failure wipe covers poisoned-cache recovery.
2. **Instrumented (coverage) builds got their own `cache/cargo-coverage` directory**, not named in
   this doc. Coverage builds set different `RUSTFLAGS`, and cargo re-fingerprints the whole graph
   when they change; one shared directory would cold-rebuild on every switch between plain and
   instrumented runs. Two directories keep both modes warm.

The coverage-scoping rule chose the filesystem-stamp variant: a `.run-epoch` marker written into
the generation root before the first cargo process starts supplies the acceptance floor for files
found in the shared cache; both roots share one volume, so the timestamps compare. The optional
`ct.db` cache-bytes row was not built — the cache folds into the existing per-root disk accounting.
F7a (skip forced rediscovery) remains open as a follow-up, gated on a replacement
inventory-refresh trigger per this doc's Option C warning.
