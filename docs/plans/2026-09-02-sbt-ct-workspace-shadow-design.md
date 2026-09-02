# sbt CT build-root shadow design

## Status

Approved adaptation for Task 7 of
`2026-09-01-ct-providers-jvm-ruby-php-gdscript-implementation-plan.md`.

## Problem

sbt 1.x writes both `target/` and `project/target/` while loading a build. The official sbt 1.13.0
probe recorded in `docs/findings/2026-09-ct-runner-surfaces.md` proved that launcher-cache
redirection and a session `target` override do not stop those writes. Running sbt in the source
workspace would violate CT's requirement that provider writes remain under the supervised Miller
build root.

Task 7 must also avoid replacing source safety with a performance regression. A fresh full copy and
cold sbt target for every CT generation would discard Zinc's incremental compiler state and repeat
the most expensive part of each run.

## Requirements

- Keep framework value `sbt` and the existing `IJvmTestBackend` seam.
- Run sbt with a canonical working directory owned by Miller, never the user source directory.
- Preserve the current sbt build-root state, including uncommitted and untracked source files below
  that root.
- Support ordinary single- and multi-project sbt 1.x builds; fail closed on ambiguous duplicate
  fully-qualified test class names.
- Use one sbt process for discovery and one for a run, never one process per class.
- Keep dependency and compiler caches warm across CT generations.
- Store verdict evidence under the immutable generation results directory.
- Do not add MCP surface or change public CT JSON contracts.
- Work on Windows, Linux, and macOS with path-safe file operations.
- Measure cold sync, warm no-op sync, changed-file sync, bytes copied, and total provider latency.

## Considered approaches

### Session or global-plugin remapping

Copy only build definitions, then remap sources and outputs after sbt loads.

Rejected. sbt compiles the meta-build and allocates target state before session settings run. A
global plugin cannot universally replace project bases, nested builds, `user.dir`, or load-time file
access.

### Per-generation full build-root snapshot

Copy the sbt build-root subtree into every generation and run sbt there.

Rejected. It contains writes correctly, but repeats source copying and loses warm Zinc target state
on every discovery generation. Dependency-cache reuse alone does not prevent full recompilation.

### Generic CT workspace-shadow service

Add a provider-independent filesystem abstraction under `Providers/Shared`.

Rejected for this slice. sbt is the only proven caller, its exclusion and Git-barrier rules are
tool-specific, and a generic filesystem port would expose more interface than the current plan
needs. The implementation can be promoted later only after a second provider proves the seam.

### Provider-private persistent mirror

Maintain one sbt build-root mirror under the existing project-stable CT cache and reconcile it
before each sbt operation.

Chosen. It contains sbt's unavoidable writes, keeps its incremental build state warm, and leaves the
public provider and generation contracts unchanged.

## Architecture

### Modules

- `SbtWorkspaceShadow` lives under `src/Miller.Testing/Providers/Jvm/`. Its internal `Sync` entry
  point reconciles the directory containing `workspace.ProjectPath` into the project-stable sbt
  shadow and returns the shadow root, shadow project path, and bounded sync metrics.
- `SbtTestBackend` implements `IJvmTestBackend`. It owns sbt command construction, class discovery,
  selected execution, report collection, and aggregation through `JUnitXmlResultParser`.
- `JvmTestProvider` and `ContinuousTestProviderFactory` only register the new backend/key. They do not
  learn shadow policy.
- `CtGenerationPaths` remains unchanged. The source/build mirror lives beneath
  `CtGenerationPaths.CacheDirectory(workspace, "sbt-workspace")`; launcher and dependency caches
  live beneath `CtGenerationPaths.CacheDirectory(workspace, "sbt-deps")`; immutable report copies
  live beneath `paths.ResultsDirectory`.

### Architecture Quality

**Affected modules:** JVM CT backend, provider registration, Scale tool support, and Task 7 docs.

**Caller-facing interface:** `IJvmTestBackend` remains the only provider-facing seam. The backend
calls the smaller internal `SbtWorkspaceShadow.Sync` interface.

**Depth/locality check:** sbt-specific traversal, exclusions, link policy, manifest recovery, and
metrics stay inside the JVM sbt folder. Neither the provider shell nor shared generation model gains
filesystem policy.

**Test surface:** provider/backend tests prove observable commands, cases, verdicts, and workspace
immutability. Focused shadow tests prove reconciliation and crash recovery through its single
internal entry point.

**Seams/adapters:** no broad `IFileSystem` abstraction. Tests use real temporary directories, which
faithfully exercise the local filesystem dependency.

**Rejected shortcuts:** in-place sbt, post-load target overrides, hardlinks to user source, source
directory symlinks, per-generation cold targets, and a generic shadow service.

**Architecture risk:** high. The design adds cross-platform file reconciliation on a release path,
so Scale, Windows, disk-budget, and performance gates are mandatory.

## Shadow layout

For one discovered sbt project:

```text
<BuildOutputRoot>/cache/sbt-workspace/
  build/                     reconciled sbt build-root subtree and warm target trees
  manifest.json              last complete source-owned entry manifest

<BuildOutputRoot>/cache/sbt-deps/
  boot/                      sbt launcher cache
  global/                    sbt global base
  ivy/                       Ivy cache
  coursier/                  Coursier cache

<generation>/TestResults/sbt/
  *.xml                      immutable copies used for verdict parsing
```

The existing CT build-root operation lease serializes discovery/run work for this project and keeps
other processes' cache janitors out while the mirror is being reconciled or sbt is running.

The janitor sees `cache/sbt-workspace` and `cache/sbt-deps` as separate candidates. It measures each
whole directory, reads "last used" from the candidate directory's own last-write time, and may
delete either candidate when the workspace build root exceeds its budget
(`CtBuildCacheJanitor.DefaultWorkspaceBudgetBytes`, 2 GB) or a candidate is idle past the inactivity
window. The atomic `manifest.json` replace refreshes the mirror candidate on every sync; the backend
touches a build-owned last-used marker at the dependency candidate root after every sbt operation.
The same process runs the janitor in its maintenance tail after each operation, so the initial sync
measures and refuses a build-root source copy that cannot fit by itself beneath the workspace budget.
The Scale measurement records each candidate and their steady-state total against that budget. The
split lets dependency caches be reaped without also discarding the reconciled source mirror.

Windows path length: the inventory's build-root bound assumes the deepest provider path is about
five levels below the build root. The mirror adds the sbt build-root source depth beneath
`cache/sbt-workspace/build/`, so that bound no longer protects it. Before copying or starting sbt,
the sync computes every final mirrored path and refuses with the offending relative path when it
would exceed `ContinuousTestProjectInventory.WindowsPathBudget` (260 characters). Miller passes
ordinary canonical paths to sbt and the JVM; it does not expose a `\\?\`-prefixed path that those
toolchains may interpret differently.

## Reconciliation policy

1. Resolve the sbt build root as the directory containing `workspace.ProjectPath`, then walk only
   that subtree without following directory links. A build definition that reaches outside this
   root through `../`, an absolute path, or an external build URI is a documented v1 limitation;
   Miller does not duplicate the rest of a monorepo into every project cache.
2. Exclude `.miller`, every `.git` entry at ANY depth (a submodule or nested checkout carries a
   `.git` file that points at live administrative state), and every conventional sbt `target`
   subtree, including `project/target`. These entries are never source-owned and are never deleted
   from the mirror's build-owned target trees. The shadow's own barrier `.git` (below) is
   build-owned: reconciliation never creates, updates, or deletes it.
3. Normalize relative paths and reject traversal, platform case collisions, device/special files,
   and paths that would escape the shadow root.
4. Copy regular files atomically; never hardlink them to user source. Preserve last-write time,
   read-only state, and Unix executable bits.
5. Recreate only relative symbolic links whose resolved source and destination both remain inside
   the build root/shadow. Reject absolute, external, looping, or unsupported reparse points with an
   actionable provider error.
6. Compare source kind, length, timestamp, permissions, and link target with the committed manifest
   and destination. Hash when metadata is ambiguous. Copy only changed entries and remove stale
   source-owned entries; never remove build-owned target/cache entries.
7. Recheck source metadata around each copy and retry a bounded number of times. If the source keeps
   changing, fail discovery rather than publish a mixed snapshot.
8. Write the new manifest last through an atomic replace. After interruption, the prior manifest
   causes the next sync to repair incomplete changes.

The mirror is intentionally project-stable so Zinc and plugin build outputs remain warm. Sync runs
before discovery and before execution; missing or changed selected classes fail closed through the
existing JVM result-attribution rules.

## Git boundary

The source checkout's `.git` entry is never copied or referenced from the shadow. A linked-worktree
`.git` file points at live administrative state, and passing it through would allow build plugins to
write outside Miller storage.

The shadow creates an isolated, unborn Git repository barrier at its root: a private `.git`
directory with a minimal local config and `HEAD` pointing at an uncreated `miller-shadow` branch.
This makes Git discovery stop inside the shadow instead of walking upward from
`<workspace>/.miller/...` and attaching to the real checkout. It contains no source-repository
objects, refs, index, hooks, or alternates. Builds that require live Git metadata at load or test
time are a documented v1 limitation and fail normally without compromising source safety. A private
read-only Git metadata view is a separate design, not an implicit exception.

## sbt execution

- Map `workspace.ProjectPath` into the mirrored build root and use its directory as the canonical
  working directory.
- Prefer a copied launcher inside the sbt build root when present; otherwise use PATH `sbt`.
- Disable server autostart and generated build-property writes.
- Point `sbt.boot.directory`, `sbt.global.base`, `sbt.ivy.home`, and `sbt.coursier.home` at the stable
  sbt cache root. Do not overwrite `SBT_OPTS`, `JAVA_OPTS`, or project test settings. Redirecting
  `sbt.global.base` also drops the user's global plugins (`~/.sbt/1.0/plugins`); builds that depend
  on them are a documented limitation.
- Run sbt in batch mode with plain output: `-batch`, `-Dsbt.supershell=false`, `-Dsbt.color=false`,
  `-Dsbt.log.noformat=true`, so stdout rows are parseable without ANSI stripping.
- Discovery uses complete stdout from `show Test/definedTestNames`. Accept the official sbt 1.10
  single-line `[info] List(...)` form and the newer pretty-printed `[info] * <name>` rows. In a
  multi-project build, also parse the per-project header rows sbt prints before each project's list.
  Reject malformed/duplicate output and emit one class-level case per unique fully-qualified name.
- Partial runs invoke one `testOnly` command with whitespace-separated selected classes. Whole-suite
  runs invoke `test` while retaining the selected case list for attribution.
- Clear old shadow reports before a run (every subproject's `target/test-reports`). After sbt exits,
  enumerate only contained `target/test-reports/*.xml` files across the mirror's subprojects, copy
  them atomically into the generation results directory, and parse the immutable copies. sbt's
  JUnit listener names each file `<suite name>.xml` with a `<testsuite>` root, not the Surefire
  `TEST-*.xml` shape (the findings doc records `target/test-reports/*.xml`); accept a file by its root
  element, not its filename prefix, and confirm the exact name on the Scale host.
- Aggregate method rows to class verdicts. Any failed/error method fails the class; missing selected
  classes, malformed reports, and unexpected partial-run classes fail closed.

Multi-project builds are supported through root aggregation when class names are unique. Duplicate
fully-qualified class names across projects are refused because the existing JVM case identity has
no project dimension and silently merging them would corrupt selection and verdict attribution.

## Failure behavior

- The safety guarantee covers Miller's launch directory, standard sbt/meta-build outputs, caches,
  reports, temporary files, and inherited Git discovery. Like the existing Gradle and Maven
  providers, it does not sandbox adversarial build code that hard-codes the original workspace or
  deliberately writes through an absolute/external path. Builds that require such paths are outside
  the supported CT boundary and must be refused or documented when detected; portable OS-level
  filesystem confinement is not introduced by this task.
- Unsafe or unsupported filesystem entries: refuse before sbt starts and name the relative path.
  On Windows, symbolic-link creation needs Developer Mode or a privilege; when it is denied the
  refusal names the link and the remedy instead of silently copying the link target.
- Source changes repeatedly during sync: refuse with a retryable discovery error.
- Missing sbt/JDK compiler: Scale/support guard skips or enable refuses through existing framework
  support behavior.
- Disabled/missing JUnit reports, empty discovery, missing selected results, or nonzero exit without a
  failed case: fail closed using existing JVM provider rules.
- Interrupted sync: leave the prior manifest; the next operation repairs the shadow.
- Cache budget pressure: rely on the existing CT build-cache janitor and operation lease; do not add a
  second retention system.

## Performance gates

The implementation records internal sync metrics: entries scanned, entries copied/updated/deleted,
bytes copied, hash fallbacks, and elapsed time.

Hard gates:

- A warm no-change sync copies zero files and zero bytes.
- A one-file edit copies only that file; a deletion removes only the corresponding source-owned
  mirror entry.
- A second discovery reuses the same sbt target/cache root.
- Source-tree hashing before/after the Scale smoke is identical and no source `target` or
  `project/target` appears.
- The existing full performance-regression campaign remains green at the branch gate.

Report-only measurements:

- Cold and warm sync elapsed time on the committed sbt fixture and a representative build-root
  subtree with a recorded file/byte count.
- Total cold discovery, warm discovery, and selected-run duration when sbt and a JDK are installed.
- Shadow/cache disk bytes before and after the Scale scenario, stated against the 2 GB
  per-workspace janitor budget.

No absolute wall-clock threshold is placed in the fast suite. The release finding records the
machine, file/byte count, and cold/warm values so later releases compare like-for-like.

## Verification

Fast tests:

- initial sync, no-op sync, changed file, delete/rename, file-directory transition, permissions,
  internal link, unsafe link, case collision, interrupted manifest publication, and source mutation;
- nested project path mapping, copied wrapper selection, cache arguments, and source-safe working
  directory;
- `definedTestNames` parsing, duplicate-class refusal, `testOnly`/whole-suite command shapes, report
  containment, immutable result copying, class aggregation, and missing/unexpected result failures;
- factory/inventory registration and Scale convention coverage.

Scale:

- a two-class sbt fixture discovers and runs through the real backend when both sbt and a JDK compiler
  are available;
- the test snapshots the user fixture, asserts zero user-tree changes, confirms all reports and sbt
  writes remain under Miller storage, and records sbt version plus sync/run metrics;
- missing toolchains skip through `RequireJava` and `RequireSbt`.

Branch/release gates remain those in the parent implementation plan: Release build, fast suite,
full Scale suite, Windows fast suite, performance-regression verification, and final release audit.

## Acceptance criteria

- [x] User approves this adaptation; the selected mirror scope is the sbt build-root subtree.
- [ ] `SbtWorkspaceShadow` and `SbtTestBackend` implement the behavior above without widening public
      contracts.
- [ ] Focused fast tests and real-tool Scale smoke pass or honestly skip for missing toolchains.
- [ ] Cold/warm sync and provider measurements are recorded; warm sync copies zero bytes.
- [ ] Linux and Windows verification prove source immutability and path handling.
- [ ] Task 7 docs, known limits, SDD report, and ADR-0007 record the shipped boundary.
