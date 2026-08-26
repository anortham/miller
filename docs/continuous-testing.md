# Continuous testing

Status: current operating doc. This page is the authoritative list of the languages and test
frameworks Miller's continuous testing (CT) can run, and of the rules it uses to find them.
[`README.md`](../README.md#continuous-testing) carries the short public summary and
[`contracts/tests-cli-v1.md`](contracts/tests-cli-v1.md) carries the CLI and MCP JSON contract.

CT is opt-in per workspace and off until you enable it. It keeps a verdict for every test case,
stamped with the index generation and revision the verdict was proved at. A file change stales only
the cases the change can reach; a run executes that stale set as an explicit test-ID list.

## Supported languages and frameworks

The `framework` value is what `tests status --json` and `tests enable --json` report for a project.

| Ecosystem | Framework value | Discovered from | How CT runs it | Selection |
|---|---|---|---|---|
| .NET | `xunit` | `.csproj`, `.fsproj`, `.vbproj` naming an xunit package | builds the project, then runs the built self-executing test executable | per test method (`-method`), plus trait exclusions (`-trait-`) |
| .NET | `nunit`, `mstest`, `dotnet` | project file naming NUnit or MSTest, or `Microsoft.NET.Test.Sdk` / `Microsoft.NET.Sdk.Test` / `Microsoft.Testing.Platform` | `dotnet test <TargetPath> --filter ...`, reading a TRX report | one conjunctive vstest filter per invocation |
| Rust | `cargo` | `Cargo.toml` | `cargo test -p <package>` with `--exact` name filters | per libtest test name |
| Python | `pytest` | `pytest.ini`, `pyproject.toml`, `tox.ini`, `setup.cfg`, `setup.py` | `python -m pytest --junitxml=...` | per pytest node id |
| JavaScript and TypeScript | `vitest` | `package.json` naming vitest | local `node_modules/.bin/vitest run --reporter=json` | per file, from the JSON report |
| JavaScript and TypeScript | `jest` | `package.json` naming jest | local `node_modules/.bin/jest --json` | per file, from the JSON report |
| JavaScript and TypeScript | `node-test` | `package.json` with a script that runs node's own test runner | `node --test` with the JUnit reporter | per file |
| QML and Qt | `qt-quick-test` | `CMakeLists.txt` with Qt Quick Test evidence | `cmake` configure, `cmake --build`, then `ctest` with a JUnit report | per CTest test name |

Sources: [`ContinuousTestProviderFactory.CreateDefault`](../src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs),
[`ContinuousTestProjectInventory`](../src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs), and the
provider implementations under `src/Miller.Testing/Providers/`.

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
  The provider is limited to CMake/CTest Qt Quick Test projects and needs CMake 3.21 or newer.
  qmake projects are not supported.

### Support for more languages is ongoing

The table above is the whole supported set today. Go, Ruby, Java, PHP, and every other toolchain are
not supported yet, and more are planned. `miller tests enable` on a repository with no supported
test project refuses with exit `3` and writes nothing: no opt-in marker, no `ct.db`, no `.miller/`.
The refusal names the supported toolchains, so an unsupported repository cannot end up permanently
enabled with zero projects. See the enablement section of
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
- A QML project needs three things in one CMake subtree: a `CMakeLists.txt` with a `project()` call,
  Qt Quick Test evidence (`Qt6::QuickTest`, `Qt5::QuickTest`, `Qt::QuickTest`, `QUICK_TEST_MAIN`, or
  `QUICK_TEST_OPENGL_MAIN` in a CMake file or a C, C++, or Objective-C source), and at least one
  `.qml` file named `tst_*` or containing `TestCase`. Nested configure roots collapse to the
  outermost one.
- A .NET project needs a real test signal: an xunit, NUnit, or MSTest reference, or
  `Microsoft.NET.Test.Sdk`, `Microsoft.NET.Sdk.Test`, or `Microsoft.Testing.Platform`. A test-like
  file name alone does not qualify. `tests enable --project <file>.csproj` accepts a project whose
  contents name no test package, because it still runs under `dotnet test`.
- When a .NET project sets `VSTestTestCaseFilter` to a pure conjunction of `Name!=Value` terms (for
  example `Category!=Scale`), enable seeds the project's trait exclusions from it, so a continuous
  run honors the same default suite as a bare `dotnet test`. Any other filter shape seeds nothing.

A `tests status` read on a workspace that never decided about CT runs this same walk and reports
`projects_discovered`, so a recorded count of 0 never reads as "no test projects exist". Status
writes nothing: it never creates `ct.db`, never creates `.miller/ct/`, and never starts the daemon.

## How Miller finds test cases

Case discovery is per ecosystem. A suite named some other way reports no cases rather than a false
green.

- .NET, Rust, and QML enumerate cases from the runner itself (xunit `-list`, vstest discovery,
  `cargo test --no-run` plus libtest, and CTest discovery).
- vitest and jest match their own convention: a file whose stem ends in `.test` or `.spec`.
- `node --test` uses node's own documented default patterns, which also take every runnable file
  under a `test` directory: `**/*.test.{cjs,mjs,js}`, `**/*-test.{cjs,mjs,js}`,
  `**/*_test.{cjs,mjs,js}`, `**/test-*.{cjs,mjs,js}`, `**/test.{cjs,mjs,js}`,
  `**/test/**/*.{cjs,mjs,js}`, and the same six patterns for `{cts,mts,ts}`. When the project's own
  test script names paths or globs, those replace the defaults, exactly as they do on node's command
  line.
- pytest takes `test_*.py` and `*_test.py`.

## Known limits

**xUnit v2 projects fail discovery.** CT builds a .NET test project and runs the built
self-executing test assembly, which only xUnit v3 and Microsoft.Testing.Platform produce. An xUnit
v2 project builds a dll plus `testhost.exe` and no such executable, so CT fails late with a raw
process error such as:

```
ct-discovery-failure: An error occurred trying to start process '...\<Project>.Tests.exe'
```

The message names a missing file, so it reads like a broken build. It is not. The workaround is to
migrate the project to xUnit v3. `dotnet new xunit` still scaffolds v2 on SDK 10.0.400, so a
freshly scaffolded project hits this. A fix to classify the runner generation at enable time and to
name the real cause in the failure message is in the backlog (field report 2026-08-25).

**One environment per jest project.** CT invokes jest once under its default environment. A
repository whose own `npm test` runs jest twice under two environments is covered for one of them.

**QML is CMake/CTest only.** qmake projects, function-level QML coverage, and native QML coverage
are out of scope for the v1.22.0 provider.

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
- Providers write build, result, and temp artifacts only under supervised CT paths, never into your
  workspace `bin` or `obj`.
- Green means complete results at the current index key. When impact data is truncated, degraded, or
  unavailable, Miller marks everything stale and runs nothing. There is no whole-suite fallback and
  no optimistic green.

## Related docs

- [`contracts/tests-cli-v1.md`](contracts/tests-cli-v1.md) - the `tests` CLI and MCP JSON contract.
- [`findings/2026-08-21-ct-cross-repo-dogfood.md`](findings/2026-08-21-ct-cross-repo-dogfood.md) -
  the cross-repository provider evidence behind the table above.
- [`findings/2026-08-24-qml-continuous-testing-verification.md`](findings/2026-08-24-qml-continuous-testing-verification.md) -
  QML fixture, CMake/CTest contract, and the honest toolchain gaps.
- [`plans/2026-08-18-ct-sidecar-migration-design.md`](plans/2026-08-18-ct-sidecar-migration-design.md) -
  the `ct.db` sidecar and explicit-start daemon design.
