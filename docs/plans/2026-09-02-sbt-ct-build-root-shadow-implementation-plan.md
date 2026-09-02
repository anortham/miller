# sbt CT Build-Root Shadow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Complete Task 7 by shipping class-level sbt 1.x continuous testing from a Miller-owned,
project-stable build-root mirror without source-tree writes or cold-build regressions.

**Architecture:** A provider-private `SbtWorkspaceShadow` reconciles only the discovered sbt
build-root subtree into two existing CT cache candidates: `sbt-workspace` for mirrored sources and
warm target state, and `sbt-deps` for launcher/dependency caches. `SbtTestBackend` runs exclusively
from that mirror, discovers complete class names from plain batch stdout, and copies contained JUnit
XML into immutable generation results before parsing.

**Tech Stack:** .NET 10, xUnit, sbt 1.x, JVM/JUnit XML, existing `ITestProcessRunner`,
`CtGenerationPaths`, `JUnitXmlResultParser`, and `IJvmTestBackend`.

**Architecture Quality:** Keep `IJvmTestBackend`, `JvmTestProvider`, `CtGenerationPaths`, and all
public CT contracts unchanged. The only new internal seam is `SbtWorkspaceShadow.Sync`; filesystem
policy remains local to the sbt backend. Architecture risk is high because reconciliation touches
cross-platform filesystem semantics and CT latency.

## Global Constraints

- The approved source of truth is
  `docs/plans/2026-09-02-sbt-ct-workspace-shadow-design.md`.
- Mirror only the directory containing `ContinuousTestWorkspace.ProjectPath`; never duplicate the
  whole workspace for each discovered `build.sbt`.
- Use `cache/sbt-workspace` and `cache/sbt-deps` as separate janitor candidates. The mirrored source
  copy must fit below `ContinuousTestCoordinatorOptions.DefaultBuildCacheBudgetBytes`.
- Exclude `.miller`, every `.git` entry at any depth, and every conventional sbt `target` subtree.
  The mirror's isolated unborn `.git` barrier is build-owned.
- Never hardlink user source. Copy files atomically; preserve read-only/executable metadata; recreate
  only relative links contained by the source and shadow roots.
- Enforce `ContinuousTestProjectInventory.WindowsPathBudget` before copy/launch; pass ordinary
  canonical paths to sbt/JVM.
- Builds using source outside the sbt build root (`../`, absolute paths, external build URIs), live
  Git metadata, user global sbt plugins, or unavailable Windows symlink privilege are documented v1
  limits and fail without weakening isolation.
- Framework key is `sbt`, provider source is `ct-provider:jvm`, support floor is sbt 1.x, and cases
  are class-level using `JvmTestBackendIds.ClassCaseSentinel`.
- Run one sbt process per phase. Use batch/plain output (`-batch`,
  `-Dsbt.supershell=false`, `-Dsbt.color=false`, `-Dsbt.log.noformat=true`) and complete stdout.
- Enumerate contained `target/test-reports/*.xml`; accept `<testsuite>` or `<testsuites>` roots rather
  than a filename prefix. Copy reports into the generation results tree without flattening relative
  paths.
- Duplicate fully-qualified class names across sbt projects are refused; the existing JVM ID has no
  sbt project dimension.
- No new dependencies, MCP tools, tool parameters, or public CT JSON fields.
- Target `net10.0`; Release build stays at zero warnings/errors.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` sections “Testing — read this before running tests”,
“Build”, and “Continuous testing (CT)”; the parent provider plan's verification strategy remains in
force.

**Worker red/green scope:**
`dotnet test --filter "FullyQualifiedName~SbtWorkspaceShadowTests"` for Task 1 and
`dotnet test --filter "FullyQualifiedName~SbtTestBackendTests"` plus focused factory/convention/JVM
classes for Task 2. Workers use the documented missing-pinned-tool environment flags when this
worktree lacks `.tools`.

**Worker ceiling:** focused classes only. Workers do not run the bare fast suite, full Scale suite,
Windows guest, or performance campaign as acceptance evidence.

**Worker gate invariant:** shadow tests prove exact mirror ownership, reconciliation, recovery,
metadata/link/path/budget behavior, and warm no-op metrics. Backend tests prove command construction,
complete discovery parsing, class selection/aggregation, report containment/copying, and fail-closed
attribution through stubbed processes.

**Lead affected-change scope:** bare `dotnet test` after both serialized tasks land; exact sbt Scale
smoke and Miller shadow cold/warm measurement; Miller impact over the task diff.

**Branch gate:** parent plan gate: `dotnet build Miller.slnx -c Release`, `scripts/test.sh scale`,
Windows fast suite through `win-test`, then the release's full performance-regression verification.

**Security scope:** none declared.

**Replay/metric evidence:** Hard gates are zero source-tree changes, no source `target` or
`project/target`, warm sync copying zero files/bytes, one-file sync copying only that file, stable
cache-root reuse, contained immutable report paths, and green existing performance gates. Cold/warm
sync latency, sbt phase latency, candidate bytes, and tool versions are report-only with machine and
fixture counts.

**Escalation triggers:** filesystem/path changes require the Windows fast suite before the branch
gate. Touching `CtGenerationPaths`, `Providers/Shared`, process supervision, or janitor behavior
requires full Scale immediately; this plan avoids those changes.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless
this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp.
For metrics, record hard-gate and report-only values. Reuse passing evidence only for unchanged HEAD.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Build-root shadow | None - serial | Create `src/Miller.Testing/Providers/Jvm/SbtWorkspaceShadow.cs`, `tests/Miller.Tests/Testing/Providers/Jvm/SbtWorkspaceShadowTests.cs` | Yes | Risk-first filesystem seam must be green and reviewed before the backend consumes it. |
| Task 2: sbt backend and registration | None - serial | Create `src/Miller.Testing/Providers/Jvm/SbtTestBackend.cs`, `tests/Miller.Tests/Testing/Providers/Jvm/SbtTestBackendTests.cs`, `docs/adr/ADR-0007-sbt-ct-build-root-shadow.md`; modify JVM/factory/Scale/support/convention files named below | Yes | Consumes Task 1 and shares JVM/factory files with the parent serialized lane. |

## Tasks

### Task 1: Project-stable sbt build-root mirror

**Files:**
- Create: `src/Miller.Testing/Providers/Jvm/SbtWorkspaceShadow.cs`
- Test: `tests/Miller.Tests/Testing/Providers/Jvm/SbtWorkspaceShadowTests.cs`

**Interfaces:**
- Produces internal `SbtWorkspaceShadow.Sync(ContinuousTestWorkspace, CancellationToken)`.
- Returns immutable roots plus metrics: entries scanned/copied/updated/deleted, bytes copied, hash
  fallbacks, elapsed time, workspace candidate bytes, and dependency candidate bytes.
- Consumes `JvmTestTooling.ProjectRoot`, `CtGenerationPaths.CacheDirectory`,
  `ContinuousTestCoordinatorOptions.DefaultBuildCacheBudgetBytes`, and
  `ContinuousTestProjectInventory.WindowsPathBudget` without modifying them.

**Contract inputs:** `workspace.ProjectPath` names the sbt build file; its containing directory is
the mirror boundary. Source-owned manifest entries exclude `.miller`, `.git` at any depth, and
`target` subtrees. `manifest.json` publishes last through atomic replace. `sbt-workspace/build` and
`sbt-deps` are separate cache candidates with independent build-owned last-used markers.

**File ownership:** Only the two files above and the Task 1 SDD report.

**Serialization required:** Yes.

**Dependency reason:** This establishes the reviewed internal seam Task 2 consumes.

**Commit mode:** `serial-worker-commit`.

**What to build:** Implement a provider-local reconciler using real filesystem temporary tests, not
a generic `IFileSystem`. Initial sync materializes the build-root subtree and isolated unborn Git
barrier. Later syncs update only source-owned changes, repair destination mutations, delete stale
manifest-owned entries, preserve build-owned targets/caches, and publish metrics. Copy retries are
bounded and cancellation-aware. Reject unsafe links/reparse points, case collisions, source
mutation, budget overflow, and final Windows paths over 260 characters with the offending relative
path.

**Approach:** TDD through `SbtWorkspaceShadow.Sync`. Start with initial/no-op/one-file/delete cases;
then add type transitions, metadata, internal links, unsafe links, nested `.git`, target preservation,
interrupted manifest recovery, source mutation, budget, and path bounds. Use no narration comments.

**Acceptance criteria:**
- [x] Red/green tests cover initial, no-op, update, deletion, recovery, metadata/link, exclusion,
      collision, mutation, budget, and Windows-path behavior.
- [x] Warm no-op sync reports zero copied files and bytes; one-file update copies only one file.
- [x] Mirror and dependency roots are separate janitor candidates; user source is byte-identical.
- [x] Focused tests and Release build pass; worker commits owned files and report.

### Task 2: sbt JVM backend, registration, and live evidence

**Files:**
- Create: `src/Miller.Testing/Providers/Jvm/SbtTestBackend.cs`
- Modify: `src/Miller.Testing/Providers/Jvm/IJvmTestBackend.cs`
- Modify: `src/Miller.Testing/Providers/Jvm/JvmTestProvider.cs`
- Modify: `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs`
- Modify: `tests/Miller.Tests/Testing/CtProviderTestSupport.cs`
- Modify: `tests/Miller.Tests/Conventions/CtScaleTraitConventionTests.cs`
- Modify: `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProviderFactoryTests.cs`
- Modify: `tests/Miller.Tests/Testing/Providers/Jvm/JvmTestProviderScaleTests.cs`
- Create: `tests/Miller.Tests/Testing/Providers/Jvm/SbtTestBackendTests.cs`
- Create: `tests/Miller.Tests/Testing/Providers/Fixtures/sbt-defined-test-names.txt`
- Create: `tests/Miller.Tests/Testing/Providers/Fixtures/sbt-defined-test-names-multiproject.txt`
- Create: `docs/adr/ADR-0007-sbt-ct-build-root-shadow.md`

**Interfaces:**
- Adds `JvmTestBackendIds.Sbt = "sbt"`; does not change `IJvmTestBackend` members.
- `SbtTestBackend` implements the existing backend interface and consumes Task 1's sync result.
- Factory registration is `["sbt"] = new(jvm, "ct-provider:jvm")`.
- Adds `CtProviderTestSupport.LocateSbt` and `RequireSbt`; the convention guard owns both names.

**Contract inputs:** Official sbt 1.x surfaces:
[Testing](https://www.scala-sbt.org/1.x/docs/Testing.html),
[Inspecting settings](https://www.scala-sbt.org/1.x/docs/Howto-Inspect-the-Build.html),
[Command line](https://www.scala-sbt.org/1.x/docs/Command-Line-Reference.html), and
[directory structure](https://www.scala-sbt.org/1.x/docs/Directories.html). Discovery uses complete
plain stdout from `show Test/definedTestNames`, including multi-project headers and `[info] * name`
rows. Partial runs use one `testOnly <classes>` command; whole-suite uses `test`. Reports are any
contained `target/test-reports/*.xml` with `<testsuite>`/`<testsuites>` root, copied with relative
subproject paths into generation results before parsing.

**File ownership:** Only the files above and the Task 2 SDD report.

**Serialization required:** Yes.

**Dependency reason:** Consumes the accepted Task 1 mirror and modifies the shared JVM/factory lane.

**Commit mode:** `serial-worker-commit`.

**What to build:** Implement wrapper/PATH resolution, cache/plain-output arguments, discovery parser,
class-level IDs, partial/whole-suite commands, stale-report clearing, contained immutable report
copying, JUnit aggregation, duplicate/missing/unexpected refusal, and result artifact mapping. Update
factory tests that currently expect sbt to be unsupported. Add a two-class sbt Scale fixture that
uses both `RequireJava` and `RequireSbt`, hashes the source fixture before/after, asserts all writes
under Miller storage, exercises a warm second discovery, and reports sync/cache/sbt timings and
bytes. Record the approved boundary in ADR-0007.

**Approach:** TDD the parser and command/result contract first with committed stdout/JUnit fixtures;
then register the backend and add the Scale/support guards. Preserve exact report-relative paths so
same-named XML files in separate subprojects cannot collide. Do not refactor Gradle/Maven or widen
the JVM interface.

**Acceptance criteria:**
- [ ] Fast tests cover plain/multi-project discovery, duplicate-class refusal, command/cache paths,
      selection, report-root validation/copying, class aggregation, and all fail-closed cases.
- [ ] Factory and convention tests prove `sbt` resolves to `ct-provider:jvm` and real-tool tests stay
      Scale-tagged.
- [ ] Exact Scale smoke passes when sbt+JDK exist or honestly skips; source and source targets remain
      unchanged, warm sync copies zero bytes, and metrics are recorded.
- [ ] ADR-0007 records the mirror scope, cache split, trust boundary, and known limits.
- [ ] Focused tests and Release build pass; worker commits owned files and report.

## Parent-plan integration

After both tasks pass lead inline review, tick the three Task 7 acceptance criteria in
`docs/plans/2026-09-01-ct-providers-jvm-ruby-php-gdscript-implementation-plan.md`, append the child
plan's real commit ranges to its SDD ledger, run the parent affected-change gate, and continue
immediately to Task 8.
