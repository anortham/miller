# Continuous testing

Status: current operating doc. This page is the authoritative list of the languages and test
frameworks Miller's continuous testing (CT) can run, and of the rules it uses to find them.
[`README.md`](../README.md#continuous-testing) carries the short public summary and
[`contracts/tests-cli-v1.md`](contracts/tests-cli-v1.md) carries the CLI and MCP JSON contract.

CT is opt-in per workspace and off until you enable it. It keeps a verdict for every test case,
stamped with the index generation and revision the verdict was proved at. A file change stales only
the cases the change can reach; a run executes that stale set as an explicit test-ID list.

`tests status` reports a verdict row per project — case, stale, and red counts, a per-project
verdict projected at the same live key the workspace verdict uses, and when that project last
reported a result (`never` when it has not) — so a project with a green baseline is
distinguishable from one that never ran. The JSON shape is in the contract.

## Supported languages and frameworks

The `framework` value is what `tests status --json` and `tests enable --json` report for a project.

| Ecosystem | Framework value | Discovered from | How CT runs it | Selection |
|---|---|---|---|---|
| .NET | `xunit` | `.csproj`, `.fsproj`, `.vbproj` naming an xunit package | builds the project, then runs the built self-executing test executable | per test method (`-method`), plus trait exclusions (`-trait-`) |
| .NET | `nunit`, `mstest`, `dotnet` | project file naming NUnit or MSTest, or `Microsoft.NET.Test.Sdk` / `Microsoft.NET.Sdk.Test` / `Microsoft.Testing.Platform` / `MSTest.Sdk` | VSTest uses `dotnet test <TargetPath> --filter ...`; MTP uses direct `dotnet exec <TargetPath>` with a proven TRX extension | one conjunctive filter per invocation when the runner proves filter support |
| Rust | `cargo` | `Cargo.toml` | `cargo test -p <package>` with `--exact` name filters | per libtest test name |
| Python | `pytest` | `pytest.ini`, `pyproject.toml`, `tox.ini`, `setup.cfg`, `setup.py` | `python -m pytest --junitxml=...` | per pytest node id |
| JavaScript and TypeScript | `vitest` | `package.json` naming vitest | local `node_modules/.bin/vitest run --reporter=json` | per file, from the JSON report |
| JavaScript and TypeScript | `jest` | `package.json` naming jest | local `node_modules/.bin/jest --json` | per file, from the JSON report |
| JavaScript and TypeScript | `node-test` | `package.json` with a script that runs node's own test runner | `node --test` with the JUnit reporter | per file |
| QML and Qt | `qt-quick-test` | `CMakeLists.txt` or `.pro` with Qt Quick Test evidence | CMake/CTest configure/build/run, or qmake configure/build/`make check`, with a generation-scoped XML report | per CTest name or qmake target |
| Go | `go` | one `go.mod` module | `go list -json` / `go test -list` discovery, then package-grouped `go test -json` | top-level `TestXxx` names, grouped by package |
| JVM | `gradle` | `build.gradle` or `build.gradle.kts` | project/workspace wrapper before PATH `gradle`; `test --test-dry-run` discovery and JUnit XML results | per test method (`Class.method`) |
| JVM | `maven` | `pom.xml` | project/workspace wrapper before PATH `mvn`; `test-compile`, then Surefire class scanning | per test class |
| JVM | `sbt` | `build.sbt` | persistent CT shadow; `show Test/definedTestNames` discovery and isolated `testOnly`/`test` runs | per test class |
| Ruby | `rspec` | `Gemfile` containing `rspec` | `bundle exec rspec` with `Gemfile.lock`, otherwise direct `rspec`; JSON reports | per example |
| PHP | `phpunit` | `composer.json` containing `phpunit/phpunit` | local `vendor/bin/phpunit`; XML listing and JUnit result reports | per class/method |
| PHP | `pest` | `composer.json` containing `pestphp/pest` | local `vendor/bin/pest`; XML listing and JUnit result reports | per class/method |
| Godot / GDScript | `gut` | `project.godot` plus `addons/gut/plugin.cfg` | isolated project mirror and Godot home; GUT JUnit reports | per script (`res://`) |

Sources: [`ContinuousTestProviderFactory.CreateDefault`](../src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs),
[`ContinuousTestProjectInventory`](../src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs), and the
provider implementations under `src/Miller.Testing/Providers/`.

The default factory also accepts the explicit compatibility aliases `rust` and `python`; inventory
reports those projects as `cargo` and `pytest`. No other aliases are registered.

Field evidence, by ecosystem:

- .NET runs on Miller's own suite every day.
- `cargo` ran the `julie-extractors` suite (4,173 cases) and `pytest` ran `more-itertools` (736
  tests); `vitest`, `node-test`, and `jest` ran on live JavaScript repositories. See
  [`findings/2026-08-21-ct-cross-repo-dogfood.md`](findings/2026-08-21-ct-cross-repo-dogfood.md).
  CT invokes jest once under its default environment, so a repository that runs jest under two
  environments gets one of them.
- `qt-quick-test` shipped in v1.22.0 and is proven by fixtures and focused tests. The real Qt
  fixture is NOT VERIFIED on a machine with the Qt Quick Test development package, because neither
  available host had it. See
  [`findings/2026-08-24-qml-continuous-testing-verification.md`](findings/2026-08-24-qml-continuous-testing-verification.md).
  CMake/CTest and qmake/QTest backends are proven by focused fixtures; qmake Scale is guarded by
  qmake availability. CMake requires 3.21 or newer.
- VB.NET MSTest and Microsoft.Testing.Platform are covered by guarded real fixtures, including
  selected parameterized execution and TRX parsing. xUnit v3, NUnit, and VSTest continue to use
  their existing .NET lanes.
- Go single-module and in-root multi-module fixtures pass with Go 1.26.6; the provider gate is
  Go 1.24 or newer.

### Provider bounds

These floors and runner choices are part of the v1 contract. JVM wrapper lookup checks the project
or module directory, then the workspace root, then PATH. sbt checks the wrapper in its persistent
CT shadow before falling back to PATH.

- Gradle requires 8.3 or newer. Miller accepts `build.gradle` and `build.gradle.kts`, discovers
  with `test --test-dry-run`, records one case per JUnit method, and selects methods with
  `--tests Class.method`.
- Maven itself has no version floor, but its Surefire selection surface must be 2.7.3 or newer.
  Discovery runs `test-compile`, scans `target/test-classes` using Surefire's default class-name
  patterns, and records one case per class. Selected runs use class-level `-Dtest` selectors.
- sbt requires 1.x. Miller mirrors the project into a persistent CT shadow, discovers with
  `show Test/definedTestNames`, records one case per class, and uses `testOnly <Class>` for
  selected runs or `test` for a whole-suite run.
- RSpec requires 3.x and a `Gemfile` containing the `rspec` token. Discovery runs
  `rspec --dry-run --format json` and records one case per example. With `Gemfile.lock`, runs use
  `bundle exec rspec`; without it, Miller invokes `rspec` directly.
- PHPUnit requires 10 or newer; Pest requires 2 or newer. Detection reads `composer.json`, with
  `pestphp/pest` taking precedence when both framework tokens are present. Miller invokes the
  local `vendor/bin/phpunit` or `vendor/bin/pest` runner, uses `--list-tests-xml` for discovery,
  and writes `--log-junit` results. If the runner is missing, run `composer install`.
- GUT requires Godot 4, GUT 9, `config_version=5` in `project.godot`, and the GUT addon. Miller
  uses an isolated project mirror and Godot home, enforces a 2 GiB candidate budget, imports once,
  then uses the warm metadata fast path. Cases are scripts with `res://` selectors.

### Support for more languages is ongoing

The table above is the whole supported CT set today. Swift, Dart, Elixir, Erlang, native C/C++/CTest,
Zig, Lua, R, and Bash remain unprovided. F# can be discovered as a .NET project, but Miller does not
claim F# source coverage without Julie extractor rows. QML remains supported through its
`qt-quick-test` CMake/CTest and qmake/QTest lanes; that does not add a native C/C++/CTest provider.
`miller tests enable` on a repository with no supported test project refuses with exit `3` and writes
nothing: no opt-in marker, no `ct.db`, no `.miller/`. The refusal names the supported toolchains, so an
unsupported repository cannot end up permanently enabled with zero projects. See the enablement section of
[`contracts/tests-cli-v1.md`](contracts/tests-cli-v1.md).

## How Miller finds test projects

`tests enable` and `tests status` run the same walk. The walk reads project and manifest files, not
build output.

- Skipped directories: `.git`, `.miller`, `bin`, `obj`, `node_modules`, `dist`, `vendor`, `.vs`,
  `TestResults`, `target`, `__pycache__`, `.venv`, `venv`, `packages`.
- Skipped fixture directories: `fixtures`, `__fixtures__`, `testdata`. A manifest below one of them
  is parser test data, not a suite. The rule prunes the walk only; a path you name yourself with
  `--project` is still accepted, because that carries your intent.
- Directory symlinks, junctions, and every other reparse point are not descended into. One physical
  project reached by several logical paths would otherwise be enabled several times.
- A nested separate checkout is skipped: a `.git` directory, or a `.git` file pointing into a
  `worktrees` admin directory. A git SUBMODULE is kept, because its source is in this working tree
  and this workspace's index covers it.
- One pytest project per directory. A package that carries `pyproject.toml`, `setup.cfg`, and
  `tox.ini` side by side enables one project, named by the file pytest itself would read first
  (`pytest.ini`, then `pyproject.toml`, then `tox.ini`, then `setup.cfg`, then `setup.py`).
- Cargo workspace members are dropped when the workspace root proves membership, because one
  `cargo test` at the root already runs them. Any doubt keeps the crate.
- A CMake QML project needs a `CMakeLists.txt` with a `project()` call, Qt Quick Test evidence
  (`Qt6::QuickTest`, `Qt5::QuickTest`, `Qt::QuickTest`, `QUICK_TEST_MAIN`,
  `QUICK_TEST_MAIN_WITH_SETUP`, or `QUICK_TEST_OPENGL_MAIN` in the CMake graph or a C, C++, or
  Objective-C source), at least one `.qml` file named `tst_*` or containing `TestCase`, and
  resolvable static `add_test(...)` registration. Nested roots collapse only when the outer graph
  statically includes the child; independent nested projects remain separate.
- A qmake QML project needs a bounded literal `.pro`/`.pri` graph with `CONFIG += qmltestcase`, or
  both `QT += qmltest` and `CONFIG += testcase`, QML test evidence, and a generated `check` target.
  qmake and make/Qt capabilities are probed before generation; dynamic, malformed, or incomplete
  project evidence is refused.
- A .NET project needs a real test signal: an xunit, NUnit, or MSTest reference, or
  `Microsoft.NET.Test.Sdk`, `Microsoft.NET.Sdk.Test`, `Microsoft.Testing.Platform`, or
  `MSTest.Sdk`. A test-like file name alone does not qualify. Effective MTP/VSTest properties are
  evaluated when static evidence does not decide the runner. `tests enable --project <file>.csproj`
  accepts a project whose contents name no test package, because it still runs under `dotnet test`.
- A Go project is one `go.mod`; an in-root `go.work` supplies context only, and nested modules stay
  separate. External `GOWORK` is not inherited. Go 1.24+ and complete module/package metadata are
  required before enablement.
- When a .NET project sets `VSTestTestCaseFilter` to a pure conjunction of `Name!=Value` terms (for
  example `Category!=Scale`), enable seeds the project's trait exclusions from it, so a continuous
  run honors the same default suite as a bare `dotnet test`. Any other filter shape seeds nothing.

A `tests status` read on a workspace that never decided about CT runs this same walk and reports
`projects_discovered`, so a recorded count of 0 never reads as "no test projects exist". Status
writes nothing: it never creates `ct.db`, never creates `.miller/ct/`, and never starts the daemon.

## How Miller finds test cases

Case discovery is per ecosystem. A suite named some other way reports no cases rather than a false
green.

- .NET, Rust, QML, and Go enumerate cases from the runner itself (xUnit `-list`, VSTest/MTP
  discovery, `cargo test --no-run` plus libtest, CTest/qmake target discovery, and Go
  `go test -list`).
- vitest and jest match their own documented defaults: a file whose stem ends in `.test` or
  `.spec` (including `.mjs` / `.cjs` / `.mts` / `.cts`). Jest also takes every JS/TS file under
  `__tests__/`, which is jest's own default. A literal `testMatch` (jest, including `package.json`
  `"jest"`) or `test.include` (`vitest.config` only) array of strings replaces those defaults.
  A vitest `include` on its own keeps vitest's default excludes (`node_modules`, `dist`, `cypress`,
  dot-directories, tool config files), exactly as the runner does; an explicit `test.exclude`
  replaces them. Config is read, never executed — unsupported, truncated, malformed, interpolated, or
  `testRegex`-based discovery is refused rather than silently changing the suite. Component
  extensions and runner-owned directories require an explicit supported config. `vite.config`
  `include` is ignored because that is the library source set.
- `node --test` uses node's own documented default patterns, which also take every runnable file
  under a `test` directory: `**/*.test.{cjs,mjs,js}`, `**/*-test.{cjs,mjs,js}`,
  `**/*_test.{cjs,mjs,js}`, `**/test-*.{cjs,mjs,js}`, `**/test.{cjs,mjs,js}`,
  `**/test/**/*.{cjs,mjs,js}`, and the same six patterns for `{cts,mts,ts}`. When the project's own
  test script names paths or globs, those replace the defaults, exactly as they do on node's command
  line.
- pytest takes `test_*.py` and `*_test.py`.
- Go lists only top-level `TestXxx` functions. Examples, benchmarks, fuzz targets, and child
  `t.Run` cases are excluded in V1. A selected run groups cases by package, anchors escaped test
  names in `-run`, and uses `-count=1`; incomplete `go list` or `go test -json` output refuses
  rather than becoming an empty or green result.

## Where CT builds

The default build output root is INSIDE the workspace:
`<workspace>/.miller/ct-<project segment>` (a fixed 12-hex segment per project, with the
per-run generation directories below it). Building inside the workspace is what makes repo-root
discovery work with zero project-side settings: a test that walks up from its own binary
(`TestContext.TestDirectory` and the like) to find the repository root finds it, because the
binary IS under the repository root. Under the old machine-temp root that walk failed only under
CT — 87 of 140 baseline failures in one dogfood repository.

The layout guarantees a bounded depth: the deepest test-assembly directory,
`.miller/ct-<project>/g<generation>/out/<ProjectName>`, sits exactly 5 levels below the workspace
root. Walk-up helpers commonly cap at 8 ascents and burn one on a trailing path separator; 5
levels clears that pattern with margin. A pre-flattening `<workspace>/.miller/ct/build` tree left
by an older Miller is reclaimed by run maintenance once no live process holds its roots.

`.miller/**` is invisible to Miller's file watcher and to the extractor, so building there adds no
index churn, no watcher events, and no rescans.

One bounded fallback: a workspace root longer than the derived Windows MAX_PATH budget (260
characters minus the deepest composed provider artifact path below the workspace root) falls back
to the legacy machine temp root `<os-temp>/miller-ct/build/<workspace segment>/<project segment>`,
and the reason is carried on the work item. Repo-root-walking tests are broken for such a project
either way; MAX_PATH breakage is worse. The daemon's enqueue validation accepts exactly these two
shapes and nothing else.

## Where the daemon logs

`<main checkout>/.miller/ct/daemon.out.log` and `daemon.err.log` are the daemon's RAW STDIO only.
On a healthy run `daemon.out.log` holds exactly one startup breadcrumb line — the daemon's
version, pid, and the path of the shared daily log — and `daemon.err.log` stays empty. A 0-byte
`daemon.err.log` is a healthy daemon, not missing diagnostics.

The real diagnostics are the `role:ct` lines in the shared daily pair
`<workspace>/.miller/logs/miller-<yyyyMMdd>.log` (and `.jsonl`) — the same files every other
Miller process on the workspace appends to. That is where the breadcrumb points. A drain that
selects zero cases logs `ct drain skip … reason=no_selection` there, so a project that keeps
draining without running anything is visible rather than silent.

## Known limits

**xUnit v2 projects are detected and refused, not run.** CT builds a .NET test project and runs the
built self-executing test assembly, which only xUnit v3 and Microsoft.Testing.Platform produce. An
xUnit v2 project builds a dll plus `testhost.exe` and no such executable, so CT cannot run it.
`dotnet new xunit` still scaffolds v2 on SDK 10.0.400, so a freshly scaffolded project hits this.

Discovery classifies the generation from the project's package ids: an `xunit.v3` reference is v3
and reports `framework: "xunit"`, while a v2-only reference (`xunit`, `xunit.core`, `xunit.assert`,
`xunit.abstractions`, `xunit.extensibility.*`) reports `framework: "xunit-v2"` plus
`unsupported_reason`. `xunit.runner.visualstudio` and `xunit.analyzers` ship for both generations
and decide nothing; a project carrying both generations reads as v3.

What each command does with that:

- `tests status` lists a v2 project with its framework and the reason
  `xUnit v2 detected; CT needs the v3 self-executing assembly`. It never hides the project — a
  reader has to be able to tell "unsupported" from "nobody looked". When every project found is
  unsupported, the compact output drops the `enable` suggestion and keeps the direct-run one.
- `tests enable` on a repository whose ONLY test projects are v2 refuses with exit `3` and writes
  nothing — no opt-in marker, no `ct.db`, no `.miller/` — the same refusal a repository with no
  supported toolchain gets. The error names the reason, every affected project path, and the
  migration.
- `tests enable --project <v2 project>` is refused the same way.
- A MIXED repository enables the supported projects and reports the v2 ones under `unsupported:`
  with their reason. They are never silently dropped.

If a v2 project slips classification (its only xunit package is one of the shared runner packages),
the provider still catches it before spawning anything: a build that produced the dll but no
executable beside it fails with the same plain reason instead of the raw OS error for a missing
file. That raw error was the original report — it named a missing path, so it read like a broken
build and sent a user hunting for one (field report 2026-08-25).

The fix is to migrate the project to xUnit v3 (`dotnet new xunit3` scaffolds v3). A v2 suite still
runs normally under `dotnet test`; only continuous testing needs the v3 shape.

**One environment per jest project.** CT invokes jest once under its default environment. A
repository whose own `npm test` runs jest twice under two environments is covered for one of them.

**Some detected frameworks are refused with a repair path.** A `Gemfile` with only `minitest` is
listed as `framework: "minitest"` and refused with reason `Minitest has no per-test machine-readable
runner surface CT can consume`. The remedy is `Add rspec, or run the suite directly with rake test`.
A Godot
project with `gdUnit4` is listed as `framework: "gdunit4"` and refused with reason `gdUnit4 is
detected; Miller CT does not yet support its runner`. The remedy is
`run it with its own runner; CT support is planned`. A Godot project that has GUT evidence below the supported floor is listed as
`framework: "gut-unsupported"` and refused with reason `Godot 4 with GUT 9 was not detected`.
The remedy is `Upgrade or configure Godot 4 with GUT 9, or run GUT directly`.

Inventory ignores a bare `project.godot` with neither test addon. When both addons are present, a
supported GUT project remains runnable as `framework: "gut"`; the `gdunit4` refusal reason and
remedy remain in the project metadata.

**Expensive MSBuild build hooks still run.** CT builds a .NET test project with
`--artifacts-path` pointed at the supervised build root (see "Where CT builds"), which the watcher
and the extractor ignore. But a csproj `Exec` hook the project carries — `npm ci` plus a SPA build
is the common one — runs inside that build and writes wherever it always writes. One measured
field case (2026-08-26): a test project referenced an ASP.NET project whose build hook reinstalled
the SPA's `node_modules` on every build — roughly 40,000 file events in the workspace per CT run,
overflowing the file watcher and forcing rescans, and paying the npm install in every run's wall
time. No project-side setting is needed for CT to go green; this gate is only for trimming hook
cost. Every process CT starts, builds included, carries `MILLER_CT_WORKSPACE_ROOT` in its
environment, and MSBuild reads environment variables as properties — so a repository can skip such
hooks under CT with one property condition:

```xml
<SkipClientAppBuild
    Condition="'$(SkipClientAppBuild)' == '' and '$(MILLER_CT_WORKSPACE_ROOT)' != ''">true</SkipClientAppBuild>
```

Tests that serve or read the built SPA assets should not opt in to CT this way; tests that never
touch them (the normal case for unit suites) lose nothing.

**QML supports CMake/CTest and qmake/QTest.** CMake requires static Qt Quick Test evidence and
CTest registration; qmake requires a generated `check` target and runs at target level. Function-
level QML coverage, native Qt Test, Qbs, PySide6, and device runners remain out of scope.

**MTP is evidence-gated.** Microsoft.Testing.Platform requires version evidence at or above 1.7,
and selected runs require a runner-proven framework filter plus the TRX report extension. MTP
listing and run output that is missing, malformed, truncated, or incomplete is refused. Direct
MTP application execution uses `dotnet exec <TargetPath>` with native MTP options; the `--`
delimiter belongs to the `dotnet test` driver and is not inserted into the direct command.

**Go V1 is deliberately bounded.** The provider requires Go 1.24+, one project per `go.mod`, and
top-level `TestXxx` cases. Child `t.Run` identity, benchmarks, fuzz targets, examples, and
function-level source paths are not claimed. Nested modules remain separate even when an in-root
`go.work` supplies context.

## Safety posture

CT runs real test processes, so every part of it is explicit and bounded.

- Opt-in per workspace through `.miller/ct.enabled`, off until you enable it. A linked worktree
  inherits the main checkout's opt-in through the git link; a local `.miller/ct.disabled` tombstone
  beats the inheritance; `MILLER_CT=off` beats everything and is a permanent zero-work guarantee
  (no daemon, no `ct.db` writes, honest status).
- `tests status` is a cheap read that creates nothing and starts nothing. `miller tests serve`, the
  dashboard, and MCP `tests operation=start` are the only start paths.
- One workspace executes tests at a time, worktrees included, under a user-global budget. A run that
  finds the budget held reports it and executes nothing.
- One family daemon serves a repository and its registered worktrees. Each worktree keeps its own
  `ct.db` and its own index key.
- The daemon runs from a private per-build copy under `~/.miller/ct-daemon/`, so a running daemon
  never locks the installed binary or your build output.
- A test process that goes silent for 10 minutes is treated as wedged: Miller kills its process tree
  and fails the run. The bound is on silence, not on total duration, so a slow suite survives.
  `MILLER_CT_STALL_TIMEOUT` overrides it; `off` disables it.
- Automatic runs debounce on the trailing edge (2 seconds, `MILLER_CT_DEBOUNCE`). Changes during a
  run queue a follow-up instead of killing the run.
- Providers write build, result, and temp artifacts only under supervised CT paths — the
  workspace-local `.miller/ct-<project>` root (or its bounded temp fallback) and the `miller-ct`
  temp namespace — never into your workspace `bin` or `obj`.
- Green means complete results at the current index key. When impact data is truncated, degraded, or
  unavailable, Miller marks everything stale and runs nothing. There is no whole-suite fallback and
  no optimistic green.
- That stale backlog is not stranded: an idle daemon drains it once the workspace has settled. The
  drain fires only when every guard holds — the queue holds no pending work and no run is executing,
  the last poll was healthy with the poll cursor at the live index revision, automatic runs are not
  paused, the workspace has been quiet for at least the debounce window, and no idle drain ran for
  this workspace in the last 5 minutes (a fixed cooldown, also counted from daemon start, so a
  freshly started daemon stays status-only for its first cooldown). What it runs is the stale set
  plus owed red reruns, selected exactly as an explicit run selects its stale set and executed as an
  explicit test-ID list — never as a whole-suite run, even when every case is stale — under the same
  one-workspace execution budget as any other run. A drain that goes green converges and stops; one
  whose own build re-stales cases repeats at most once per cooldown.

## Watching a session on the dashboard

The workspace detail view has a Tests section. It shows the verdict, the stale and tracked case
counts, the daemon state and activity, the run in flight, the selected index key, the last run, the
test projects with their framework (and the reason for one CT cannot run), and the last red cases in
the same one-line shape `miller tests failures` prints.

The section reads the same status core the CLI and the MCP tool read, so it never disagrees with
them. It creates nothing: no `ct.db`, no `.miller/ct/`, no daemon. It refreshes every 5 seconds only
when CT is enabled for that workspace and the read succeeded — a workspace with CT off has nothing
that can change, so that page stays as rendered until you reload it.

## Related docs

- [`contracts/tests-cli-v1.md`](contracts/tests-cli-v1.md) - the `tests` CLI and MCP JSON contract.
- [`findings/2026-08-21-ct-cross-repo-dogfood.md`](findings/2026-08-21-ct-cross-repo-dogfood.md) -
  the cross-repository provider evidence behind the table above.
- [`findings/2026-08-24-qml-continuous-testing-verification.md`](findings/2026-08-24-qml-continuous-testing-verification.md) -
  QML fixture, CMake/CTest contract, and the honest toolchain gaps.
- [`plans/2026-08-18-ct-sidecar-migration-design.md`](plans/2026-08-18-ct-sidecar-migration-design.md) -
  the `ct.db` sidecar and explicit-start daemon design.
