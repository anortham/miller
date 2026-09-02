# Task 2 report: sbt JVM continuous-testing backend

## Status

Implemented the approved sbt provider packet on `feature/ct-providers-jvm-ruby-php-gdscript`.
The implementation consumes Task 1's synchronized shadow result, registers sbt through the shared
JVM provider, adds the Scale guard/support signal, documents the approved boundary in ADR-0007, and
adds focused fixtures/tests. The commit is pending at report-generation time; the pre-change HEAD was
`a5a5cb0a2b54ce94e7a9b49ec6c63db47f54da1c`.

## Changes

- Added `SbtTestBackend` with serialized discovery/run phases, shadow synchronization before each
  provider operation, mirrored launcher/PATH resolution, isolated sbt cache arguments, and
  dependency-candidate `.last-used` touching after every sbt process.
- Discovery runs one `show Test/definedTestNames` command and requires complete stdout. It parses the
  official single-line `[info] List(...)` form, pretty `[info] * name` rows, and multi-project headers;
  malformed rows, duplicate methods, and duplicate class attribution across projects fail closed.
- Runs use one whitespace-separated `testOnly` selection or one whole-suite `test` command. Stale
  mirror reports are removed first. Contained `target/test-reports/*.xml` reports are copied atomically
  into generation results with relative subproject paths preserved, then parsed from those immutable
  copies. Empty, malformed, aggregate-mismatched, duplicate, missing, and unexpected results fail
  closed; method rows aggregate to class-level verdicts.
- Added `JvmTestBackendIds.Sbt = "sbt"` without changing `IJvmTestBackend` members; the public JVM
  runner constructor includes the sbt backend, and the default factory registers
  `["sbt"] = new(jvm, "ct-provider:jvm")`.
- Added `LocateSbt`/`RequireSbt`, convention ownership for both signals, factory assertions, committed
  stdout fixtures, and the Scale smoke with source hashing, warm-sync zero-copy assertions, storage
  boundary checks, and timing/cache-byte output.
- Added [ADR-0007](../../../docs/adr/ADR-0007-sbt-ct-build-root-shadow.md) covering mirror scope,
  cache split, trust boundary, and known limits.

## Miller evidence

Miller workspace: `ct-providers-jvm-ruby-php-gdscript-f1a61a828444`, root
`/home/murphy/source/miller/.worktrees/ct-providers-jvm-ruby-php-gdscript`, final indexed revision
`78967`; refresh completed with content/search sidecars repaired/current.

The following indexed symbols were searched, inspected at full depth, and traced before editing;
impact was run before and after the change:

- `SbtTestBackend` (`src/Miller.Testing/Providers/Jvm/SbtTestBackend.cs`, class symbol
  `056f83a33463d5f848b7220645523007`) implements `IJvmTestBackend` and exposes the existing
  `DiscoverAsync`, `RunAsync`, `BuildDiscoveryCommand`, and `BuildRunCommands` contracts. Its
  `DiscoverAsync` and `RunAsync` call Task 1's `SbtWorkspaceShadow.Sync(ContinuousTestWorkspace,
  CancellationToken)` (`Sync` symbol `5dadc5f692070ac6f223a625ecb15b62`) and use the returned
  `WorkspaceCandidateRoot`, `DependencyCandidateRoot`, `ShadowRoot`, and `ShadowProjectPath`.
- Task 1's `SbtWorkspaceShadowResult` is the accepted internal record with the four roots/paths,
  scan/copy/update/delete/byte/hash/elapsed metrics, and workspace/dependency candidate-byte counts;
  its full indexed definition was inspected before use.
- `JvmTestBackendIds` (`ab1eea569a99450b89830a478584354f`) now contains only the added public constant
  `Sbt = "sbt"` beyond the existing IDs; `IJvmTestBackend` remained unchanged.
- `JvmTestProvider` (`130272a710d9f1dbb19f75357607a9e7`) retains its existing public and internal
  constructors and backend dispatch; only the public `JvmTestProvider(ITestProcessRunner)` backend
  list gained `SbtTestBackend`.
- `ContinuousTestProviderFactory.CreateDefault` (`5dab74e2eb11662dc15f8e85a1f43dba`) retains one
  shared `ITestProcessRunner` and now maps `sbt` to the JVM provider with source `ct-provider:jvm`.
- `CtProviderTestSupport.LocateSbt` (`9ba4a947593781bf061da6ec672814d4`) resolves the platform
  launcher names; `RequireSbt` (`ef89e398a2f536d2f7498fbee21258e9`) skips only when the tool is
  absent. `CtScaleTraitConventionTests` owns both signal names and has a separate sbt non-vacuity
  count.
- `ContinuousTestWorkspace` (`f618aceb4d7cec5a523dfe076f3df887`) supplies the canonical workspace,
  project, and build-output paths; `CtGenerationPaths` supplies immutable generation/results/temp
  paths. `TestProcessCommand` (`8411c8169f1afaa840b3d852a6a36688`) supplies file name, argument array,
  working directory, and environment. `TestProcessResult` supplies exit code, complete/truncated
  output flags, and `RequireCompleteStandardOutput` (`93` in
  `src/Miller.Testing/Contracts/ProviderContracts.cs`) was used for discovery attribution.
- `JvmTestTooling.IsInside` (`src/Miller.Testing/Providers/Jvm/JvmTestTooling.cs:131`) is the existing
  lexical containment contract used for shadow/result boundaries. `JUnitXmlResultParser.ParseFile`
  (`src/Miller.Testing/Providers/Shared/JUnitXmlResultParser.cs:110`) is the existing immutable-copy
  report parser used for contained JUnit XML.

Official sbt evidence used for the command/output contract:

- Inspecting settings: https://www.scala-sbt.org/1.x/docs/Howto-Inspect-the-Build.html documents
  `show Test/definedTestNames` and the single-line `List(...)` output.
- Testing: https://www.scala-sbt.org/1.x/docs/Testing.html documents `test`, whitespace-separated
  `testOnly`, and the default `target/test-reports` report location.
- Command-line reference: https://www.scala-sbt.org/1.x/docs/Command-Line-Reference.html documents
  `-Dkey=val`, boot/Ivy/coursier/plain-output properties, and the relevant batch/log controls.
- Batch arguments: https://www.scala-sbt.org/1.x/docs/Running.html documents command arguments such as
  `testOnly TestA TestB`.

## TDD and focused verification

Focused behavior was added one failure at a time, with the expected red observed before the minimal
production change and a green rerun after each slice. The slices covered list/bullet/multi-project
discovery, duplicate/malformed attribution, command shape/cache paths, stale-report clearing,
shadow resynchronization, report copying/aggregation, duplicate/missing/unexpected reports, truncated
stdout, nonzero discovery, factory registration, and Scale-trait convention coverage.

Hard-gate invariants and results:

- `SbtTestBackendTests`: 16 passed. Proves parser attribution, one-process command construction,
  source-shadow refresh before discovery/run, stale-report removal, contained immutable result mapping,
  class aggregation, and fail-closed result handling.
- `ContinuousTestProviderFactoryTests`: 7 passed. Proves `sbt` and null-framework `build.sbt` resolve
  to `ct-provider:jvm` and the shared factory behavior remains intact.
- `CtScaleTraitConventionTests`: 1 passed. Proves tests using `LocateSbt`/`RequireSbt` are accounted for
  by the real-provider Scale guard and that the sbt signal family is non-vacuous.
- `git diff --check`: passed.

Report-only metrics/verification:

- The Scale smoke records cold/warm shadow elapsed time, entries, copied bytes, candidate bytes, and
  run time; it also asserts warm sync copies zero files/bytes and source-tree hashes/targets stay
  unchanged. It was not executed in this worker because `sbt` is absent; the lead owns the Scale gate.
- No Release build, bare test suite, Windows suite, performance gate, or Scale suite was run, per the
  task boundary. Focused `dotnet test --no-restore` needed the repository's permitted
  `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1` environment because the pinned
  extractor/semantic artifacts are unavailable in this checkout.

## Concerns and preserved state

- No sbt executable is installed in this lane, so live sbt behavior and Scale timings remain lead-owned
  verification. The Scale test is guarded by both `RequireJava` and `RequireSbt` and will skip honestly
  when either toolchain is unavailable.
- The worktree already contained unrelated modifications to
  `docs/plans/2026-09-02-sbt-ct-build-root-shadow-implementation-plan.md` and
  `docs/plans/2026-09-02-sbt-ct-workspace-shadow-design.md`; they were not edited or staged.
- The provider intentionally does not sandbox build code that writes to hard-coded external paths, and
  duplicate fully-qualified classes across sbt projects remain unsupported because the shared JVM class
  identity has no project dimension; both limits are recorded in ADR-0007.
