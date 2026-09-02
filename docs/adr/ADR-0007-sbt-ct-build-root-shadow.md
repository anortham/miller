# ADR-0007: sbt continuous-testing build-root shadow

- Status: accepted
- Date: 2026-09-02
- Scope: Miller's JVM continuous-testing provider for sbt 1.x

## Decision

Miller runs sbt from a provider-owned mirror of the directory containing the selected `build.sbt`.
The mirror is maintained by `SbtWorkspaceShadow` under the project-stable
`CtGenerationPaths.CacheDirectory(workspace, "sbt-workspace")` cache. It preserves source files,
untracked files, relative links, executable bits, and the build definition while excluding `.miller`,
`.git`, and source-owned `target` trees. The mirror has an isolated unborn Git barrier at its root.

`SbtTestBackend` synchronizes the mirror before discovery and execution. It runs one sbt process per
phase, uses plain batch output, and maps the project path and launcher into the mirror. Discovery reads
complete stdout from `show Test/definedTestNames`. It accepts sbt's single-line `List(...)` output,
pretty-printed bullet rows, and multi-project headers. Miller emits one class-level case per unique
fully-qualified class. Duplicate classes across sbt projects are refused because the existing JVM case
identity has no project component.

Partial execution uses one `testOnly` command with a whitespace-separated class list. Whole-suite
execution uses `test`. The backend clears stale mirror reports before execution, accepts contained
`target/test-reports/*.xml` files with a `testsuite` or `testsuites` root, copies them atomically under
the immutable generation results directory while preserving relative subproject paths, then parses the
copies. Method rows aggregate to class verdicts. Missing, unexpected, duplicate, malformed, or
unattributed results fail closed.

## Cache split

The source mirror and sbt launcher/dependency caches remain separate:

```text
<BuildOutputRoot>/cache/sbt-workspace/build/
<BuildOutputRoot>/cache/sbt-workspace/manifest.json
<BuildOutputRoot>/cache/sbt-deps/boot/
<BuildOutputRoot>/cache/sbt-deps/global/
<BuildOutputRoot>/cache/sbt-deps/ivy/
<BuildOutputRoot>/cache/sbt-deps/coursier/
<generation>/TestResults/sbt/
```

The backend passes the four stable dependency paths through `sbt.boot.directory`, `sbt.global.base`,
`sbt.ivy.home`, and `sbt.coursier.home`. It sets `-batch`, `-Dsbt.supershell=false`,
`-Dsbt.color=false`, `-Dsbt.log.noformat=true`, and `-Dsbt.server.autostart=false`. It does not
overwrite `SBT_OPTS`, `JAVA_OPTS`, project settings, or user source files. After each sbt process it
touches the build-owned `.last-used` marker at the dependency candidate root for the existing cache
janitor.

## Trust boundary

The mirror protects the ordinary sbt launcher, meta-build, target, report, temporary, and inherited Git
write paths. Miller does not sandbox build code that deliberately opens an absolute or external path,
and v1 does not provide live Git metadata or external build-root duplication. Unsafe source links,
case collisions, path escapes, unsupported file types, and path-budget violations are rejected by the
shadow before sbt starts. The existing CT operation lease serializes provider work and the backend
serializes its own sbt phases.

## Known limits

- Builds that require live repository objects, user-global sbt plugins, or files outside the selected
  build root are outside the v1 mirror contract.
- A build that writes through a hard-coded external path is outside the filesystem protection boundary.
- Duplicate fully-qualified test classes in separate sbt projects are unsupported until the shared JVM
  case identity grows a project dimension.
- Discovery and run attribution require complete sbt stdout and contained JUnit XML reports.

## Evidence and verification

The command and output shapes follow the official sbt 1.x Testing, Inspecting settings, Command-Line
Reference, and Directory Structure documentation. Committed fixtures cover single-line and
multi-project discovery. Focused tests cover command isolation, class aggregation, report copying,
duplicate and malformed attribution, stale-report clearing, factory registration, and Scale-trait
guarding. The Scale smoke records cold and warm shadow metrics, copied bytes, cache bytes, and run time;
the warm no-change sync must copy zero files and zero bytes.
