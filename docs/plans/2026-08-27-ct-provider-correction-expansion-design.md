# Continuous-testing provider correction and expansion design

**Status:** Approved direction, pending implementation-plan review

## Goal

Correct JavaScript test-file discovery, close the supported Qt Quick Test gaps and add a qmake backend,
make Visual Basic a first-class .NET CT language including Microsoft.Testing.Platform execution, and add
a first-class Go provider.

The work also closes the shared Miller contract gaps that would otherwise make the new providers report
false `KnownEmpty` selections, discard Julie test roles, or lose provider-to-symbol identity.

## Constraints

- Preserve the current dirty Miller checkout as the source of the JavaScript work. Do not strand or replace
  those uncommitted changes in another worktree.
- Keep CT opt-in, supervised, generation-scoped, and write-free outside Miller-owned paths.
- Keep `Miller.Core` free of I/O dependencies.
- Add no MCP tools.
- Keep raw Julie language labels and test evidence intact. Miller may normalize them only at query time.
- Never persist a Julie symbol ID without its matching index identity. Prefer stable name/path identity and
  resolve against the current snapshot.
- Missing, stale, truncated, ambiguous, or unsupported evidence must fail closed. It must not become
  `KnownEmpty` or green.
- Real extractor and provider processes belong in Scale tests with the repository's shared guards.
- Qbs, PySide6 Qt Quick Test, native Qt Test, Go benchmarks, Go fuzzing, and child-level `t.Run` selection
  are not part of this implementation.

## Architecture quality

**Affected modules:** CT project inventory, fact adaptation, impact selection, provider contracts, the Node,
QML, .NET, and new Go providers, provider factory wiring, documentation, and focused/Scale tests.

**Caller-facing interface:** `IContinuousTestProvider` remains the provider boundary. `ICtFactSource` gains
typed file-completeness evidence and trailing optional test-role evidence. Provider cases gain stable
symbol name/path identity without a durable foreign key to Julie artifacts.

**Depth and locality:** each runner or build backend owns its command construction, discovery format, result
parser, and diagnostics. Shared CT code owns only freshness, identity, selection, storage, and provider
resolution. JavaScript config parsing remains behind one internal matcher. CTest and qmake do not share a
parser merely because both run Qt Quick Test.

**Test surface:** behavior is proved through project inventory, `IContinuousTestProvider.DiscoverAsync`,
`IContinuousTestProvider.RunAsync`, `ContinuousTestImpactSelector.Select`, and the existing CLI/tool result
contracts. Private parsers receive focused unit tests only where malformed external data needs direct proof.

**Rejected shortcuts:** executing JavaScript config during discovery, treating all fileless .NET cases as
C#, name-only joins across packages, persisting unversioned Julie IDs, interpreting absent symbol rows as an
empty file, reusing CTest parsing for qmake, source-only Go discovery, running Go subtests as independently
selectable cases, and widening the provider interface for backend-specific details.

**Architecture risk:** high overall because selection facts and three provider families change. Risk stays
bounded by landing the shared contracts first and each provider as an independently verified vertical slice.

### Backend-selection interface

Three shapes were considered:

- Separate framework keys such as `qt-quick-test-qmake` or `dotnet-mtp` would keep factory wiring simple but
  leak a build/execution backend into the public test-framework identity.
- Teaching `ContinuousTestProviderFactory` to resolve on arbitrary project metadata would widen a shared
  caller-facing interface and spread backend knowledge through every provider resolution path.
- One provider per test framework with internal backend adapters keeps the public framework stable and puts
  command, discovery, and result differences beside the backend that owns them.

Use the third shape. `ContinuousTestProject` records a typed backend discriminator in its existing metadata.
`QtQuickTestProvider` delegates to CTest or qmake adapters. `DotnetTestProvider` delegates to VSTest, xUnit v3
self-executable, or MTP adapters. The second concrete adapter proves each internal seam is real. Factory
resolution, CT storage framework values, and external status contracts remain stable.

## Shared CT facts and selection

### Language families

Create one Miller-owned language-family mapper used by changed paths, Julie facts, and provider identities.
It preserves the raw label while comparing these families:

- C#: `.cs`, `csharp`, and Razor's C# execution relationship.
- Visual Basic: `.vb` and `vbnet`.
- JavaScript/TypeScript: `.js`, `.jsx`, `.mjs`, `.cjs`, `.ts`, `.tsx`, `.mts`, `.cts`, plus the Julie dialect
  labels that belong to this execution family.
- QML: `.qml` and `qml`.
- Go: `.go` and `go`.

The exact extractor labels in the pinned real language report are `csharp`, `razor`, `vbnet`, `javascript`,
`jsx`, `typescript`, `tsx`, `qml`, and `go`. The JavaScript/TypeScript family groups only those four published
dialect labels. Unknown labels remain incompatible. The mapper does not infer a project's language from its
manifest when current symbol name/path evidence exists.

### Detailed test evidence

`CtFactAdapter` copies Julie's typed test evidence into trailing optional fields on `CtSymbolFact` and
`CtImpactedSymbol`. The selector preserves case, container, lifecycle, role, status, reason, and evidence
currency. Only case evidence schedules a provider case. Container and lifecycle symbols remain useful for
reachability without becoming runnable cases.

Role precedence is explicit:

| Evidence | Scheduling rule |
|---|---|
| Current detailed `test_case` or `parameterized_test` | Schedulable case. |
| Current detailed `test_container` | Reachability evidence only; never a runnable case. |
| Current detailed lifecycle role | Reachability evidence only; never a runnable case. |
| Detailed evidence with unknown currency | Selection is `Unknown`; never schedule or carry green. |
| Legacy artifact with `IsTest=true` and no detailed fields | Preserve the old case behavior for compatibility. |
| No detailed role and `IsTest=false` | Not a runnable case. |

The pinned extractor's `apply_test_role` sets `is_test` for cases and lifecycle roles, sets
`test_container` without `is_test` for containers, and always writes the exact `test_role`. Miller still
enforces the table instead of relying on those booleans remaining accidental aliases.

### File completeness and `KnownEmpty`

Add a typed file fact to `ICtFactSource` with current status, language, content hash, parse-diagnostic state,
and evidence availability. Selection may return `KnownEmpty` only for a current, accounted file that proves
no applicable tests, or an explicitly harmless document. Missing, stale, diagnostic, unindexed, or
unavailable file evidence returns `Unknown`.

### Provider identity

Add trailing optional `SymbolName` and `SymbolPath` identity to provider discovery results and copy it into
the existing CT case fields. Resolve that identity against the current Julie snapshot at selection time.
Exact path/name joins may enrich a case with the current symbol ID in memory. Ambiguous, missing, or stale
joins must not guess.

Existing `ct.db` rows may contain a Julie symbol ID in the legacy `SymbolName` field. Treat that value as a
legacy opaque identity, never as a current name or ID. Provider rediscovery replaces it with typed name/path
identity. If impact reports a reachable test in this project and name/path resolution is ambiguous or fails,
selection becomes `Unknown`; it must not silently skip the case and watermark a prior green verdict.

## JavaScript and TypeScript discovery

Keep `JavaScriptTestProvider.DiscoverAsync` and the public provider contract unchanged. Replace the raw-string
scanner with an internal `JsTestFileDiscovery` backed by a typed `JsTestPatternSet`.

### Defaults

- Jest follows its documented JS/TS `testMatch` defaults, including `__tests__`, `test`/`spec` stems, and
  optional `m`/`c` and `x` extensions.
- Vitest follows `**/*.{test,spec}.?(c|m)[jt]s?(x)`.
- `.vue`, `.svelte`, and `.astro` are accepted only when explicit config names them.
- The supported discovery contract is Jest 29-30 and Vitest 0.34-4. An installed runner outside those
  proven ranges is refused with its detected version instead of inheriting unverified defaults.

### Bounded literal config

- Read at most the configured byte bound and loop until the bound or EOF. A config that reaches the bound
  before EOF is refused with an actionable diagnostic; it never silently falls back.
- Recognize `jest.config.{js,mjs,cjs,ts,mts,cts,json}` and
  `vitest.config.{js,mjs,cjs,ts,mts,cts}`.
- A dedicated Vitest config wins. Otherwise read nested `test.include` from
  `vite.config.{js,mjs,cjs,ts,mts,cts}`. Ignore top-level `include`.
- Read direct exported Jest `testMatch`/`rootDir` and direct exported Vitest/Vite `test.include` only.
  Never recursively match `coverage.include`, project arrays, or unrelated objects.
- Support package JSON's object-form Jest config and a package-relative referenced JSON config. Reject paths
  that escape the package root.
- Preserve runner-specific negative semantics. Jest applies patterns in order and the last matching entry
  wins. Vitest evaluates positive `include` patterns and negative ignore patterns with tinyglobby semantics;
  it does not reuse Jest's ordered matcher.
- Resolve `<rootDir>` to an in-package normalized prefix. Reject roots outside the package.
- Accept quoted strings and non-interpolated template literals. Reject interpolation, spreads, identifiers,
  calls, regexes, computed properties, and malformed input.
- An explicit empty array is an intentional empty suite.
- Defaults apply only when no supported discovery property is declared. A declared but unsupported
  `testMatch`/`include`, a truncated config, or Jest `testRegex` produces an actionable unsupported-config
  diagnostic. It must not run a different default suite. Every fallback records which config files were
  examined and why defaults apply.
- Miller always excludes operational directories that cannot contain project cases: `.git`, `.miller`,
  `.claude`, and `node_modules`. Runner-owned exclusions such as `build`, `dist`, `e2e`, `cypress`, and
  `playwright` come from defaults/config. An explicit supported runner pattern may include them.

## QML and Qt Quick Test

### Supported matrix after this work

- CMake plus CTest, with one provider case per CTest target.
- qmake `.pro`/`.pri` projects using `CONFIG += qmltestcase` or `QT += qmltest`, executed through the generated
  `check` target with JUnit XML under the generation result directory.
- Qt 5 and Qt 6 Quick Test library names and the supported macro family:
  `QUICK_TEST_MAIN`, `QUICK_TEST_MAIN_WITH_SETUP`, and the already-supported Qt 5
  `QUICK_TEST_OPENGL_MAIN` compatibility form.

Qbs, PySide6 Quick Test, native Qt Test, device runners, function-level QML selection, and native QML
coverage remain explicitly unsupported.

### CMake and CTest corrections

- Recognize the complete supported macro family without substring or token-boundary mistakes.
- Require static CTest registration evidence or a zero-write enable-time capability probe. A project that
  cannot produce CTest targets is refused before CT state is written.
- Do not collapse a nested independent CMake project merely because its files sit below another CMake root.
  Collapse only when the parent demonstrably includes the child; otherwise keep separate projects.
- Fill bounded reads to the byte limit or EOF. Evidence beyond the supported bound remains unsupported and
  is reported honestly.
- Preserve current configure/build isolation, `QT_QPA_PLATFORM=offscreen` defaulting, exact CTest selection,
  JUnit parsing, and no-coverage refusal.

### qmake backend

Add a qmake-specific internal backend behind `QtQuickTestProvider` rather than branching qmake behavior
through the CTest implementation. Both backends satisfy one small internal discovery/run interface; the
public framework remains `qt-quick-test`.

- Inventory recognizes `.pro` and included `.pri` evidence for Qt Quick Test and rejects native Qt Test-only
  projects. Enabling requires `CONFIG += qmltestcase`, or the equivalent proven combination of the Quick Test
  library plus `CONFIG += testcase`, so a generated `check` target is guaranteed. `QT += qmltest` by itself
  is library evidence, not an executable CT project.
- Configure/build runs in the generation build directory using the discovered `qmake`, `qmake6`, or
  platform-equivalent tool and an explicit make program.
- Discovery produces stable project/target cases from the qmake project structure. The initial granularity
  is the qmake test target, matching the CTest target-level contract.
- Execution uses `make check` with `TESTARGS` pointing QTestLib XML into the generation result directory.
  Tooling detects the Qt major version and uses `xunitxml` for Qt 5 and `junitxml` for Qt 6.
- qmake/make selection, import paths, offscreen environment, cancellation, incomplete output, missing report,
  and nonzero exit diagnostics remain backend-local.
- Real qmake processes are Scale-only and use new shared `CtProviderTestSupport.Require*` signals.

## Visual Basic .NET

Visual Basic uses the existing .NET provider. There is no separate VB provider and no Julie extractor work.

- Keep `.vbproj` project discovery and add complete inventory tests for MSTest, NUnit, xUnit v3, xUnit v2,
  Test SDK, and Microsoft.Testing.Platform signals.
- Add `.vb`/`vbnet` to the shared language-family mapper.
- Remove the unconditional fileless .NET equals C# fallback. Prefer provider symbol name/path identity and the
  current Julie join; unresolved mixed-language projects remain unknown.
- Prove VSTest discovery, selected execution, TRX parsing, qualified names, and parameterized cases with real
  VB-shaped cases.

### Microsoft.Testing.Platform

Treat MTP as a distinct internal .NET execution backend selected from the effective .NET 10 runner
configuration and package evidence. Keep the test framework value separate from the backend. Do not route
generic `dotnet` projects through xUnit's self-executable command shape.

- Resolve backend evidence in this order: the nearest applicable `global.json` `test.runner` value; evaluated
  project/MSBuild properties; then framework defaults. Evaluated properties include `UseVSTest`,
  `EnableMSTestRunner`, `EnableNUnitRunner`, `UseMicrosoftTestingPlatformRunner`, and
  `TestingPlatformDotnetTestSupport`. `MSTest.Sdk` defaults to MTP unless `UseVSTest=true`.
- Use a bounded, no-build MSBuild property query during enable/provider capability probing so inherited
  `Directory.Build.props` values are honored. Status inventory may record static hints but must not claim an
  effective backend it has not evaluated.
- Discovery uses the MTP `--list-tests` contract. Use JSON only when the installed MTP version proves the
  supported format; retain a bounded text parser for older supported versions.
- Runs pass test-application arguments after `--`, use the framework-appropriate filter contract, set
  `--results-directory` to the generation result directory, and request TRX only when the project registers
  the TRX extension.
- Missing report support, unsupported filter providers, unknown MTP versions, or ambiguous runner selection
  produce an actionable refusal, never an xUnit parse error or false empty suite.
- Existing VSTest and xUnit v3 lanes remain byte-for-byte stable outside the new routing decision.

## Go provider

Add `GoTestProvider` behind `IContinuousTestProvider` and register framework `go`.

### Project and environment model

- One CT project per `go.mod`. Nested modules are separate projects.
- `go.work` supplies execution context only. Associate in-root modules listed by its `use` directives and run
  each module separately. Do not inherit an unrelated external `GOWORK`.
- Use `go list -json ./...` from the module root for package and build-tag resolution.
- Record the effective Go toolchain, module, `GOWORK`, `GOOS`, `GOARCH`, `CGO_ENABLED`, and selection-affecting
  `GOFLAGS` as discovery metadata.
- Put `GOCACHE` in a project-stable Miller cache and `GOTMPDIR`, reports, coverage, and response files under
  generation-owned paths. Never call `go env -w`.

### Cases and selectors

- `go test -list` is authoritative for top-level `TestXxx` cases under the current environment.
- Stable case identity includes workspace/project, module, import path, case kind, and top-level name.
- Julie facts enrich cases with current role, source path, symbol name, and symbol identity when exact.
- V1 schedules top-level `TestXxx` cases only. Examples are compiled but excluded because `go test -list`
  does not prove the `Output:` contract that makes an example executable.
- Subtest and subbenchmark events roll up to their top-level parent. They are not independently selectable.
- Benchmarks and fuzz targets are not automatic or explicit V1 cases.
- Group selections by package and use anchored, escaped top-level `-run` expressions with `-count=1`.

### Results

- Run `go test -json` and parse newline-delimited test events separately from build JSON.
- Require Go 1.24 or newer for the structured build/test JSON contract. Older toolchains are refused before
  discovery with the detected version and the minimum requirement.
- Package names disambiguate interleaved events.
- Terminal parent events map to passed, failed, or skipped provider results.
- Package build failures fail every selected case in that package with the package diagnostic.
- Unknown actions remain forward-compatible, but malformed records, truncated stdout, missing terminal events,
  incomplete discovery, or nonzero exits cannot produce green.

## Documentation and Julie handoff

- Update `README.md` and `docs/continuous-testing.md` with the exact supported matrix and explicit limits.
- Copy the Julie language-readiness finding into Miller as the authoritative ownership audit.
- Replace the stale Julie implementation plan with a dated backlog note that names the current flattened
  artifact shape and existing helper vocabulary. Mark it not executable as written.
- Preserve the Julie Goldfish checkpoints and verify destination content before requesting approval to remove
  the audit worktree. Worktree removal is not part of implementation without that approval.

## Delivery order

1. Land shared language, role, file-completeness, and provider-identity contracts.
2. Correct JavaScript discovery on those contracts.
3. Correct the existing CMake/CTest QML backend, then add the qmake adapter.
4. Complete VB selection and split the .NET provider into VSTest, xUnit v3, and MTP adapters.
5. Add Go inventory and provider behavior after language/identity semantics are stable.
6. Reconcile public docs, preserve the Julie audit, and run the branch gates.

Each numbered item is independently reviewable and keeps its focused tests green before the next starts. The
implementation plan may parallelize test and parser work inside a numbered item, but it must not run provider
slices ahead of their shared contract dependencies.

## Verification strategy

Every behavior change follows red-green-refactor. Each new test must fail for the intended missing behavior
before production code changes.

### Focused fast tests

- JavaScript provider and discovery/config matcher classes.
- QML inventory, CTest tooling/provider, new qmake provider, and provider factory.
- .NET inventory/provider/MTP routing and VB selection.
- Go inventory/provider/parser/factory.
- `CtFactAdapter`, `ContinuousTestStoreApplier`, and `ContinuousTestImpactSelector` completeness, role, language,
  and identity matrices.

### Scale tests

- Existing CMake/CTest Qt Quick Test fixture, when the Qt development package is present.
- A guarded real qmake Qt Quick Test fixture.
- Real VB NUnit or MSTest discovery, selected execution, TRX, MTP routing where supported, and Julie-backed
  selection.
- Real Go single-module and `go.work` fixtures with pass, fail, skip, build failure, build tags, selected parent
  runs, JSON parsing, and source-tree immutability.

### Branch gate

- One bare `dotnet test` after all coherent slices land.
- `scripts/test.sh scale` because provider and CT paths change.
- `dotnet build Miller.slnx -c Release` with zero warnings and errors.
- Windows fast suite through `win-test` because .NET/MTP and qmake command routing are platform-sensitive.
- No green suite is rerun on an unchanged tree.

## Acceptance criteria

- [ ] Jest and Vitest defaults and supported literal configs discover exactly the files their documented
      contracts name, with no component-file overclaim and no false empty fallback.
- [ ] CMake/CTest Qt Quick Test accepts the supported macro family, refuses unusable CTest projects before
      state writes, and preserves independent nested projects.
- [ ] qmake Qt Quick Test projects discover and run target-level cases through supervised CT paths.
- [ ] Qbs, PySide6, native Qt Test, device runners, and QML coverage are reported as unsupported rather than
      misclassified.
- [ ] VB `.vbproj` projects work through the correct VSTest, xUnit v3, or MTP lane and `.vb` changes select
      `vbnet` cases without C# guessing.
- [ ] Go modules and in-root Go workspaces discover stable top-level test cases and produce honest
      selected verdicts from complete `go test -json` streams.
- [ ] Detailed Julie roles, file completeness, language families, and provider identity survive the Miller
      fact and selection path.
- [ ] Missing, stale, ambiguous, malformed, unsupported, or truncated evidence never becomes `KnownEmpty` or
      green.
- [ ] Public docs match the implemented provider matrix and limits.
- [ ] Focused tests, fast suite, relevant Scale suite, Release build, and Windows fast gate pass.
