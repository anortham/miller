# CT Providers: JVM, Ruby, PHP, GDScript — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when
> subagent delegation is available. Fall back to razorback:executing-plans for single-task,
> tightly-sequential, or no-delegation runs.

**Goal:** Add four continuous-testing provider families — JVM (Maven/Gradle/sbt for Java, Kotlin,
Scala), Ruby (RSpec), PHP (PHPUnit/Pest), and GDScript (GUT) — so Miller CT can discover, enable,
run, and report verdicts for those ecosystems.

**Architecture:** Each ecosystem gets a provider under `src/Miller.Testing/Providers/<Eco>/`
implementing `IContinuousTestProvider` (contract: `src/Miller.Testing/Contracts/ProviderContracts.cs:140`),
registered in `ContinuousTestProviderFactory.CreateDefault`, detected by
`ContinuousTestProjectInventory`, and mapped in `ContinuousTestLanguageFamily`. The JVM provider
uses a backend interface (precedent: `Providers/Qml/IQtQuickTestBackend.cs`) with one backend per
build tool. JVM backends, PHPUnit, and GUT all emit JUnit XML, so a shared JUnit XML result parser
lands first in `Providers/Shared/`. Every provider runs children through the shared
`ITestProcessRunner` (never spawns directly) and reads results from report files on disk, not from
stdout, so output truncation cannot corrupt verdicts.

**Tech Stack:** .NET 10 / C# (Miller), external toolchains: Maven (Surefire), Gradle 8.3+,
sbt, RSpec 3, PHPUnit 10+, Pest 2+, Godot 4 + GUT 9.

**Architecture Quality:** Approved shape: one provider class per ecosystem + tooling class for
command construction + parser class per report format; JVM composes per-build-tool backends behind
`IJvmTestBackend`. Main risk: discovery quality varies by toolchain (Maven and GUT have no cheap
per-test listing), so case granularity is per-class or per-file where per-method listing is
unavailable — the same precedent the Python provider set (one case per file). Workers who find code
reality contradicting this shape report a plan mismatch instead of redesigning locally.

## Prior evidence

- `docs/findings/2026-08-27-continuous-testing-language-readiness-audit.md` §5: Java, Kotlin,
  Scala, PHP, Ruby, and GDScript all have grouped julie test facts (`is_test`, `test_container`,
  `test_lifecycle`). The blocker is the missing Miller provider, not extraction.
- The mapping fixes that audit ranked first are done (`ContinuousTestLanguageFamily.cs` now maps
  `.vb`, `.mjs`, `.cjs`, jsx/tsx families).
- Go and QML providers landed after that audit; they are the freshest implementation templates.

## Global Constraints

- Target `net10.0`; `dotnet build Miller.slnx -c Release` must stay 0 warnings / 0 errors.
- No new MCP tools, no new MCP tool parameters. Framework values flow through the existing
  `tests` contracts (`ct.db`, `tests status --json`, `tests enable --json`) untouched.
- Framework values are API contract. Fixed for this plan: `maven`, `gradle`, `sbt`, `rspec`,
  `phpunit`, `pest`, `gut`; recognized-but-refused: `minitest`, `gdunit4` (via
  `ContinuousTestFrameworkSupport`, pattern: `xunit-v2` at
  `src/Miller.Testing/ContinuousTestFrameworkSupport.cs:26-42`).
- Every provider process goes through the shared `ITestProcessRunner` from
  `ContinuousTestProviderFactory.CreateDefault` (`ContinuousTestProviderFactory.cs:70-110`).
- Result parsing reads report files (JUnit XML, RSpec JSON) written by the runner, never a
  truncatable stream. Any stdout a parser must read goes through
  `TestProcessResult.RequireCompleteStandardOutput` (`ProviderContracts.cs:93`).
- Any test that spawns a real toolchain is `[Trait("Category","Scale")]` at class level and uses a
  `CtProviderTestSupport.Require*` signal; `CtScaleTraitConventionTests` must know every new
  `Require*` name. Missing toolchains skip, never fail.
- An empty selection must throw, not report green (Go precedent: `GoTestProvider.cs:107-109`;
  F6 lesson in `ProviderContracts.cs:214-227`). Honor `WholeSuite` by dropping per-case argv, not
  the case list.
- Windows is first-class: providers use `Path`-safe command construction, kill via the runner's
  process-tree containment, and gate Unix-only assertions with `#[cfg]`-equivalent `#if` /
  `OperatingSystem` checks.
- Do not modify `julie-extractors`. Extraction facts are sufficient (see Prior evidence).
- `docs/continuous-testing.md` framework table and `docs/known-limits.md` must be updated in the
  same plan that ships the behavior.

## Verification Strategy

**Project source of truth:** `/home/murphy/source/miller/CLAUDE.md` § "Testing — read this before
running tests" and § "Build".

**Worker red/green scope:** `dotnet test --filter "FullyQualifiedName~<TestClassName>"` for the
test class the task adds or changes.

**Worker ceiling:** the fast suite — bare `dotnet test` (auto-filters `Category!=Scale`). Workers
do not run the Scale suite on their own; the lead owns it.

**Worker gate invariant:** each provider task's fast tests prove command construction, report
parsing, case-id round-trips, and failure classification against a stub `ITestProcessRunner` and
committed report fixtures — no real toolchain in the fast suite.

**Lead affected-change scope:** bare `dotnet test` after each ecosystem task lands.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh scale`
(this plan touches the CT-provider path, so Scale is mandatory; record which provider smokes
skipped for missing toolchains) + Windows fast suite on the win-test guest:
`win-test sync miller`, then
`win-test run miller -- powershell -Command "dotnet test --filter 'Category!=Scale'"`.

**Security scope:** none declared.

**Replay/metric evidence:** Scale provider smokes are hard gates where the toolchain is installed;
a skip for a missing toolchain is report-only and must be listed in the final report.

**Escalation triggers:** touching `TestProcessRunner`, `CtGenerationPaths`, or any
`Providers/Shared/` file → run the full Scale suite; touching path handling or process
supervision → run the Windows fast suite before the branch gate, not only at it.

**Assigned verification failure:** Workers stop and report when assigned verification fails,
unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp
per task. Reuse passing evidence for an unchanged HEAD instead of rerunning expensive gates.

## Parallel Execution Contract

Tasks 3–8 all edit the same four shared files (`ContinuousTestProviderFactory.cs`,
`ContinuousTestProjectInventory.cs`, `ContinuousTestLanguageFamily.cs`,
`CtProviderTestSupport.cs`), so the ecosystem tasks serialize. Commit mode:
`serial-worker-commit`.

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Runner-surface spike | None - serial | Create: `docs/findings/2026-09-ct-runner-surfaces.md`, fixture report files under `tests/Miller.Tests/Testing/Providers/Fixtures/` | Yes | Every later task consumes its verified commands and sample reports. |
| Task 2: JUnit XML parser | None - serial | Create: `src/Miller.Testing/Providers/Shared/JUnitXmlResultParser.cs`, `tests/Miller.Tests/Testing/Providers/Shared/JUnitXmlResultParserTests.cs` | Yes | Consumes Task 1 sample reports; Tasks 4–8 consume the parser. |
| Task 3: Ruby provider | None - serial | Create: `src/Miller.Testing/Providers/Ruby/*`, `tests/.../Providers/Ruby/*`; Modify: the four shared files + `ContinuousTestFrameworkSupport.cs`, `WholeSuiteProviderContractTests.cs`, `ContinuousTestProjectInventoryTests.cs` | Yes | Shared-file edits conflict with every other ecosystem task. |
| Task 4: PHP provider | None - serial | Create: `src/Miller.Testing/Providers/Php/*`, `tests/.../Providers/Php/*`; Modify: same shared files as Task 3 | Yes | Shared-file edits; consumes Task 2 parser. |
| Task 5: JVM core + Gradle backend | None - serial | Create: `src/Miller.Testing/Providers/Jvm/*`, `tests/.../Providers/Jvm/*`; Modify: same shared files | Yes | Shared-file edits; consumes Task 2 parser. |
| Task 6: JVM Maven backend | None - serial | Create/Modify inside `Providers/Jvm/` + its tests; Modify: `CtProviderTestSupport.cs` | Yes | Extends Task 5's backend seam. |
| Task 7: JVM sbt backend | None - serial | Create/Modify inside `Providers/Jvm/` + its tests; Modify: `CtProviderTestSupport.cs` | Yes | Extends Task 5's backend seam. |
| Task 8: GDScript provider | None - serial | Create: `src/Miller.Testing/Providers/Godot/*`, `tests/.../Providers/Godot/*`; Modify: same shared files as Task 3 | Yes | Shared-file edits; consumes Task 2 parser. |
| Task 9: Docs + closeout | None - serial | Modify: `docs/continuous-testing.md`, `docs/known-limits.md`, `docs/site/index.html` | Yes | Documents the final shipped surface of Tasks 3–8. |

## Tasks

### Task 1: Runner-surface verification spike

**Files:**
- Create: `docs/findings/2026-09-ct-runner-surfaces.md`
- Create: sample report fixtures under `tests/Miller.Tests/Testing/Providers/Fixtures/` (one
  JUnit XML per JVM backend and PHPUnit and GUT; one RSpec `--dry-run` JSON and one run JSON)

**Interfaces:**
- Consumes: nothing.
- Produces: per-backend verified facts every later task copies verbatim: minimum tool version,
  discovery command, selection argv syntax, report artifact path pattern, exit-code semantics,
  and "not installed on this machine" where true.

**Contract inputs:** razorback:grounding-in-current-docs applies — verify against the installed
tool's own `--help`/behavior and the official docs; record doc URLs in the findings file.

**File ownership:** Create: `docs/findings/2026-09-ct-runner-surfaces.md`, fixture report files
under `tests/Miller.Tests/Testing/Providers/Fixtures/`

**Serialization required:** Yes

**Dependency reason:** Every later task consumes its verified commands and sample reports.

**What to build:** For each backend, build a minimal throwaway project in a temp directory, run
the real tool, and record what actually works. Surfaces to verify (expected shape, to confirm or
correct):
- **Gradle:** discovery via `gradle test --test-dry-run` (8.3+) writing JUnit XML with every test
  enumerated under `build/test-results/test/`; selection via `--tests "Class.method"`.
- **Maven:** no dry run exists; verify `mvn test-compile` + Surefire default includes
  (`*Test`, `Test*`, `*Tests`, `*TestCase`) for per-class discovery from
  `target/test-classes/`; selection via `-Dtest=Class` and `-Dtest=Class#method`; reports at
  `target/surefire-reports/TEST-*.xml`.
- **sbt:** `Test/definedTests` (or `show Test/definedTests`) for class-level listing; selection
  via `testOnly <Class>`; JUnit XML at `target/test-reports/`.
- **RSpec:** `rspec --dry-run --format json` for per-example listing (id = `file:line`);
  run via `rspec --format json --out <file>`; selection by example id list.
- **PHPUnit:** `--list-tests-xml <file>` for discovery; run with `--log-junit <file>`;
  selection via `--filter`.
- **Pest:** whether the Pest binary accepts the same `--list-tests-xml`/`--log-junit`/`--filter`
  surface; if not, record the deviation and its consequence for Task 4 granularity.
- **GUT:** `godot --headless -s addons/gut/gut_cmdln.gd` with `-gdir`, `-gexit`, and JUnit XML
  export (`-gjunit_xml_file`); per-script selection (`-gselect`) and per-test selection
  (`-gunit_test_name`).
Sanitize each captured report into a small committed fixture (strip machine paths).

**Approach:** One temp project per backend under the scratchpad; do not commit the projects, only
the findings doc and sanitized fixtures. A tool that is not installed is recorded as such — later
Scale smokes will skip for it, and the findings doc is the honest record.

**Acceptance criteria:**
- [x] Findings doc has one section per backend with the six facts above and doc URLs.
- [x] Every claimed command was executed against the real tool, or the section says
      "not installed — surface taken from docs at <URL>, needs runtime confirmation".
- [x] Sanitized sample reports committed as fixtures.
- [x] Worker-scope verification passes (no product code changed; `dotnet build Miller.slnx -c Release` still clean) and the change is committed per commit mode.

### Task 2: Shared JUnit XML result parser

**Files:**
- Create: `src/Miller.Testing/Providers/Shared/JUnitXmlResultParser.cs`
- Test: `tests/Miller.Tests/Testing/Providers/Shared/JUnitXmlResultParserTests.cs`

**Interfaces:**
- Consumes: Task 1 fixture XML files.
- Produces: a parse API the JVM, PHP, and GDScript providers call. Shape:
  `JUnitXmlResultParser.Parse(string xml)` (plus a `ParseFile(string path)` convenience) returning
  a result record exposing, per test case: suite/class name, test name, status
  (`passed`/`failed`/`skipped`/`errored` — error maps to `failed` at the provider layer), duration
  seconds, and failure message+text. Malformed XML and testsuite-level `errors`/`failures`
  attributes that disagree with case rows must be detectable by the caller.

**Contract inputs:** JUnit XML schema variance is real (Surefire, Gradle, sbt, PHPUnit, and GUT
each dialect it); the committed fixtures from Task 1 are the acceptance corpus.

**File ownership:** Create: `src/Miller.Testing/Providers/Shared/JUnitXmlResultParser.cs`,
`tests/Miller.Tests/Testing/Providers/Shared/JUnitXmlResultParserTests.cs`

**Serialization required:** Yes

**Dependency reason:** Consumes Task 1 sample reports; Tasks 4–8 consume the parser.

**What to build:** A tolerant, dialect-aware JUnit XML reader: nested `<testsuites>`/`<testsuite>`,
`<testcase>` with `<failure>`, `<error>`, `<skipped>`, missing `time` attributes, and CDATA bodies.
Pure logic, zero I/O beyond the file convenience wrapper.

**Approach:** TDD against one fixture per dialect. Follow the existing parser style
(`Providers/Qml/QTestResultParser.cs` is the closest sibling — inspect it first).

**Acceptance criteria:**
- [x] One passing test per Task 1 XML dialect fixture.
- [x] Malformed XML surfaces as a detectable parse failure, not an empty result.
- [x] Worker-scope verification passes and the change is committed per commit mode.

### Task 3: Ruby provider (RSpec)

**Files:**
- Create: `src/Miller.Testing/Providers/Ruby/RubyTestProvider.cs`,
  `src/Miller.Testing/Providers/Ruby/RubyTestTooling.cs`,
  `src/Miller.Testing/Providers/Ruby/RspecJsonParser.cs`
- Modify: `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs:44-48,87-110,131-173`,
  `src/Miller.Testing/Daemon/ContinuousTestProjectInventory.cs:1443-1456` (IsCandidateFileName),
  `:1458-1541` (TryIdentify), `:1760-1772` (FrameworkFallback),
  `src/Miller.Testing/ContinuousTestFrameworkSupport.cs:38-42`,
  `src/Miller.Testing/Selection/ContinuousTestLanguageFamily.cs`,
  `tests/Miller.Tests/Testing/CtProviderTestSupport.cs`,
  `tests/Miller.Tests/Conventions/` CT scale-trait guard,
  `tests/Miller.Tests/Testing/Providers/WholeSuiteProviderContractTests.cs`
- Test: `tests/Miller.Tests/Testing/Providers/Ruby/RubyTestProviderTests.cs` (fast, stub runner),
  `tests/Miller.Tests/Testing/Providers/Ruby/RubyTestProviderScaleTests.cs` (Scale),
  new cases in `tests/Miller.Tests/Testing/Daemon/Engine/ContinuousTestProjectInventoryTests.cs`

**Interfaces:**
- Consumes: `IContinuousTestProvider`, `ITestProcessRunner`, `CtGenerationHandoff`/
  `CtGenerationPaths` (model: `GoTestProvider.cs`), Task 1 RSpec facts.
- Produces: framework values `rspec` (runnable) and `minitest` (recognized+refused); factory
  registration key `"rspec"` → `ct-provider:ruby`; `RubyTestProvider.IsRubyProjectFile(path)`
  (Gemfile check) used by the factory's project-file fallback chain.

**Contract inputs:** Detection: `Gemfile` containing token `rspec` → `rspec`; `Gemfile` with
`minitest` and no `rspec` → `minitest` (refused with reason "Minitest has no per-test
machine-readable runner surface CT can consume" and remedy "Add rspec, or run the suite directly
with rake test"). Token matching mirrors the package.json path (`ContainsToken`, 64KiB head —
`ContinuousTestProjectInventory.cs:1480-1503`).

**File ownership:** Create: `src/Miller.Testing/Providers/Ruby/*`, `tests/.../Providers/Ruby/*`;
Modify: the four shared files + `ContinuousTestFrameworkSupport.cs`,
`WholeSuiteProviderContractTests.cs`, `ContinuousTestProjectInventoryTests.cs`

**Serialization required:** Yes

**Dependency reason:** Shared-file edits conflict with every other ecosystem task.

**What to build:** Full vertical slice: discovery (inventory + `Identify` fallback), provider
(DiscoverAsync via `rspec --dry-run --format json` under `bundle exec` when `Gemfile.lock`
exists; RunAsync via `--format json --out <artifact>` with per-example-id selection; `WholeSuite`
drops the id argv), factory registration, refusal row for `minitest`, language family
(`.rb` → `ruby`, own family), Scale smoke gated by new `RequireRuby`/`RequireRspec` signals.

**Approach:** Clone the Go provider's structure: tooling class builds `TestProcessCommand`s,
parser class owns JSON reading, provider owns orchestration + case-id encode/decode
(id embeds workspace id, project path, spec file path, example id — ownership check on decode as
in `GoTestProvider.Groups`). Case results carry `passed`/`failed`/`skipped` statuses and RSpec's
exception message as `FailureSummary`. Read the run JSON from the `--out` artifact file, not
stdout.

**Acceptance criteria:**
- [x] Inventory tests prove: rspec Gemfile discovered as `rspec`; minitest-only Gemfile listed
      with the refusal reason; `tests enable` refusal path covered by the existing
      framework-support machinery.
- [x] Fast provider tests cover discovery parse, run parse, selection argv, empty-selection
      throw, `WholeSuite` argv, and truncated-stdout refusal on any stdout parse path.
- [x] Scale smoke discovers and runs a 2-example fixture spec (1 pass, 1 fail) when rspec is
      installed; skips otherwise.
- [x] `ContinuousTestLanguageFamily` maps `.rb` and label `ruby`.
- [x] Worker-scope verification passes and the change is committed per commit mode.

### Task 4: PHP provider (PHPUnit + Pest)

**Files:**
- Create: `src/Miller.Testing/Providers/Php/PhpTestProvider.cs`,
  `src/Miller.Testing/Providers/Php/PhpTestTooling.cs`
- Modify: same shared files as Task 3 (factory `:44-48,87-110,131-173`; inventory
  `IsCandidateFileName`/`TryIdentify`/`FrameworkFallback`; language family;
  `CtProviderTestSupport.cs`; scale-trait guard; `WholeSuiteProviderContractTests.cs`)
- Test: `tests/Miller.Tests/Testing/Providers/Php/PhpTestProviderTests.cs`,
  `tests/Miller.Tests/Testing/Providers/Php/PhpTestProviderScaleTests.cs`,
  inventory test cases

**Interfaces:**
- Consumes: Task 2 `JUnitXmlResultParser`, Task 1 PHPUnit/Pest facts.
- Produces: framework values `phpunit`, `pest`; registration keys `"phpunit"`, `"pest"` →
  `ct-provider:php`; `PhpTestProvider.IsPhpProjectFile(path)` (composer.json check).

**Contract inputs:** Detection: `composer.json` containing token `pestphp/pest` → `pest`, else
token `phpunit/phpunit` → `phpunit`. Runner binary: `vendor/bin/phpunit` / `vendor/bin/pest`
relative to the composer.json directory (`.bat` shims on Windows); missing vendor binary is a
provider error with remedy "run composer install".

**File ownership:** Create: `src/Miller.Testing/Providers/Php/*`, `tests/.../Providers/Php/*`;
Modify: same shared files as Task 3

**Serialization required:** Yes

**Dependency reason:** Shared-file edits; consumes Task 2 parser.

**What to build:** Discovery via `--list-tests-xml` into a generation-paths temp file; run via
`--log-junit <artifact>` + `--filter '<Class::method>'` selection (escape regex metacharacters;
chunk long selections with `CtArgvChunking` — see `Providers/Shared/CtArgvChunking.cs`). Pest
routes through the same provider with the pest binary; if Task 1 recorded a Pest listing
deviation, Pest cases fall back to per-file granularity and the findings doc says so.

**Approach:** Same skeleton as Task 3. Case id embeds workspace id, project path, class, method.
Parse results only from the `--log-junit` artifact via `JUnitXmlResultParser`.

**Acceptance criteria:**
- [x] Inventory tests prove pest wins over phpunit when both tokens are present; phpunit-only
      composer.json → `phpunit`.
- [x] Fast tests cover both binaries' argv, selection escaping, chunking, empty-selection throw,
      `WholeSuite`, and missing-vendor-binary error text.
- [x] Scale smoke runs a 2-test PHPUnit fixture when phpunit is installed; skips otherwise.
- [x] `ContinuousTestLanguageFamily` maps `.php` and label `php`.
- [x] Worker-scope verification passes and the change is committed per commit mode.

### Task 5: JVM provider core + Gradle backend

**Files:**
- Create: `src/Miller.Testing/Providers/Jvm/JvmTestProvider.cs`,
  `src/Miller.Testing/Providers/Jvm/IJvmTestBackend.cs`,
  `src/Miller.Testing/Providers/Jvm/GradleTestBackend.cs`,
  `src/Miller.Testing/Providers/Jvm/JvmTestTooling.cs`
- Modify: same shared files as Task 3
- Test: `tests/Miller.Tests/Testing/Providers/Jvm/JvmTestProviderTests.cs`,
  `tests/Miller.Tests/Testing/Providers/Jvm/GradleTestBackendTests.cs`,
  `tests/Miller.Tests/Testing/Providers/Jvm/JvmTestProviderScaleTests.cs`,
  inventory test cases

**Interfaces:**
- Consumes: Task 2 `JUnitXmlResultParser`, Task 1 Gradle facts.
- Produces: `IJvmTestBackend` seam Tasks 6–7 implement — shape it like
  `Providers/Qml/IQtQuickTestBackend.cs`: the backend owns discovery command(s), run command(s),
  and report-file location; the provider owns case-id encode/decode, result mapping, and status
  aggregation. Framework value `gradle`; registration keys `"maven"`, `"gradle"`, `"sbt"` all →
  `ct-provider:jvm` (Task 5 registers `gradle`; 6 and 7 add theirs).
  `JvmTestProvider.IsJvmProjectFile(path)` covering `pom.xml`, `build.gradle`,
  `build.gradle.kts`, `build.sbt`.

**Contract inputs:** Detection: `build.gradle`/`build.gradle.kts` → `gradle`; `pom.xml` → `maven`;
`build.sbt` → `sbt` (inventory detects all three in this task so `tests status` shows the
projects; `maven`/`sbt` resolve to the unsupported provider until Tasks 6–7 register theirs —
that is the factory's existing behavior for an unregistered framework key, verify with a test).
Wrapper preference: use `./gradlew` (`gradlew.bat`) when present beside the build file or at the
workspace root, else `gradle` from PATH. Discovery: `test --test-dry-run` per Task 1; selection:
`--tests` filters; reports: `build/test-results/test/TEST-*.xml`.

**File ownership:** Create: `src/Miller.Testing/Providers/Jvm/*`, `tests/.../Providers/Jvm/*`;
Modify: same shared files as Task 3

**Serialization required:** Yes

**Dependency reason:** Shared-file edits; consumes Task 2 parser.

**What to build:** The JVM provider shell (case ids embed workspace id, project path, backend,
class, method; per-method granularity for Gradle since dry-run enumerates methods) plus the
Gradle backend. Language family: `.java`, `.kt`, `.kts`, `.scala` → labels `java`, `kotlin`,
`scala`, one shared `jvm` family (kotlin/java/scala co-compile in one module; the vbnet-style
carve-out does not apply — record this decision in the code where the family is defined only if
the existing file already documents families; otherwise in the plan ledger).

**Approach:** Multi-module Gradle repos: one CT project per build file that declares a test
source, running with `-p <module-dir>` (verify in Task 1; if module-scoped invocation through the
root wrapper is required instead, the spike records the working form). Skip `settings.gradle`-only
roots.

**Acceptance criteria:**
- [x] Inventory detects all three JVM build files with their framework values.
- [x] Fast tests cover wrapper-vs-PATH resolution, dry-run discovery parse (via fixtures),
      selection argv, empty-selection throw, `WholeSuite`, and unregistered-framework refusal for
      `maven`/`sbt` at this task's state.
- [x] Scale smoke runs a 2-test JUnit 5 Gradle fixture when gradle is installed; skips otherwise.
- [x] Worker-scope verification passes and the change is committed per commit mode.

### Task 6: JVM Maven backend

**Files:**
- Create: `src/Miller.Testing/Providers/Jvm/MavenTestBackend.cs`
- Modify: `src/Miller.Testing/Providers/Jvm/JvmTestProvider.cs` (register backend),
  `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs` (add `"maven"` key),
  `tests/Miller.Tests/Testing/CtProviderTestSupport.cs`
- Test: `tests/Miller.Tests/Testing/Providers/Jvm/MavenTestBackendTests.cs` + Scale smoke case

**Interfaces:**
- Consumes: `IJvmTestBackend` from Task 5; Task 1 Maven facts.
- Produces: runnable framework value `maven`.

**Contract inputs:** Discovery is per-class (Surefire has no dry run): `mvn -q test-compile`, then
scan `target/test-classes/**` for Surefire's default include patterns; one `ProviderTestCase` per
test class. Runs select with `-Dtest=<Class>` (comma-joined, chunked); results parse from
`target/surefire-reports/TEST-*.xml`, which yields per-method rows the provider aggregates to the
per-class case verdict (any failed method → class case failed, its messages joined into
`FailureSummary`). `mvnw` wrapper preferred over PATH `mvn`.

**File ownership:** Create/Modify inside `Providers/Jvm/` + its tests; Modify:
`CtProviderTestSupport.cs`

**Serialization required:** Yes

**Dependency reason:** Extends Task 5's backend seam.

**What to build:** The Maven backend behind the existing seam; no provider-shell changes beyond
backend registration. Per-class granularity is a documented v1 bound (Python's per-file precedent,
`docs/continuous-testing.md` gets the row in Task 9).

**Acceptance criteria:**
- [x] Fast tests cover class scanning (fixture dir tree), include patterns, selection argv +
      chunking, report aggregation to class verdicts.
- [x] Scale smoke runs a 2-class Maven fixture when mvn is installed; skips otherwise.
- [x] Worker-scope verification passes and the change is committed per commit mode.

### Task 7: JVM sbt backend

**Files:**
- Create: `src/Miller.Testing/Providers/Jvm/SbtTestBackend.cs`
- Modify: `src/Miller.Testing/Providers/Jvm/JvmTestProvider.cs`,
  `src/Miller.Testing/Daemon/ContinuousTestProviderFactory.cs` (add `"sbt"` key),
  `tests/Miller.Tests/Testing/CtProviderTestSupport.cs`
- Test: `tests/Miller.Tests/Testing/Providers/Jvm/SbtTestBackendTests.cs` + Scale smoke case

**Interfaces:**
- Consumes: `IJvmTestBackend`; Task 1 sbt facts.
- Produces: runnable framework value `sbt`.

**Contract inputs:** The approved child design and implementation plan are
`docs/plans/2026-09-02-sbt-ct-workspace-shadow-design.md` and
`docs/plans/2026-09-02-sbt-ct-build-root-shadow-implementation-plan.md`. Discovery uses complete
stdout from `show Test/definedTestNames`, accepting sbt's single-line list and pretty/multi-project
forms as class-level cases. Runs use one `testOnly <classes>` or `test` command. Results are any
contained `target/test-reports/*.xml` whose root is `testsuite` or `testsuites`. Discovery and run
each use one sbt process, never one process per class.

**File ownership:** Create/Modify inside `Providers/Jvm/` + its tests; Modify:
`CtProviderTestSupport.cs`

**Serialization required:** Yes

**Dependency reason:** Extends Task 5's backend seam.

**What to build:** The sbt backend at per-class granularity, running exclusively from the approved
project-stable build-root shadow with separate source/target and dependency-cache janitor candidates.
The child plan and ADR-0007 record the implementation and supported boundary.

**Acceptance criteria:**
- [x] Fast tests cover listing parse (fixtures), `testOnly` argv, report parse.
- [x] Scale smoke runs a 2-class ScalaTest or munit fixture when sbt is installed; skips
      otherwise.
- [x] Worker-scope verification passes and the change is committed per commit mode.

### Task 8: GDScript provider (GUT)

**Files:**
- Create: `src/Miller.Testing/Providers/Godot/GodotTestProvider.cs`,
  `src/Miller.Testing/Providers/Godot/GutTooling.cs`
- Modify: same shared files as Task 3 (+ `ContinuousTestFrameworkSupport.cs` for `gdunit4`)
- Test: `tests/Miller.Tests/Testing/Providers/Godot/GodotTestProviderTests.cs`,
  `tests/Miller.Tests/Testing/Providers/Godot/GodotTestProviderScaleTests.cs`,
  inventory test cases

**Interfaces:**
- Consumes: Task 2 `JUnitXmlResultParser`; Task 1 GUT facts.
- Produces: framework value `gut` (runnable), `gdunit4` (recognized+refused with remedy "run it
  with its own runner; CT support is planned"); registration key `"gut"` → `ct-provider:godot`;
  `GodotTestProvider.IsGodotProjectFile(path)` (project.godot check).

**Contract inputs:** Detection: `project.godot` + sibling `addons/gut/plugin.cfg` → `gut`;
`project.godot` + `addons/gdUnit4/plugin.cfg` → `gdunit4` (refused); bare `project.godot` with
neither → not a CT project. Runner: `godot` on PATH (also honor `GODOT` env var, the common
convention for headless CI), invoked headless with GUT's cmdline script and JUnit XML export per
Task 1; per-script selection for focused runs (per-script case granularity, GUT's selection
surface permitting per-test only as a refinement if Task 1 proved it stable).

**File ownership:** Create: `src/Miller.Testing/Providers/Godot/*`, `tests/.../Providers/Godot/*`;
Modify: same shared files as Task 3

**Serialization required:** Yes

**Dependency reason:** Shared-file edits; consumes Task 2 parser.

**What to build:** Full vertical slice like Task 3. `project.godot` joins `IsCandidateFileName`;
inventory reads the addons directory beside it. Language family: `.gd` → `gdscript`, own family.
The provider mirrors the project root into the supervised `godot-workspace/project` candidate,
excluding `.godot` and other build-owned entries. It runs import only in that mirror with
`godot --headless --path <mirror> --import`, then publishes an atomic source-metadata import stamp;
the user project remains untouched and warm runs skip import when the stamp digest matches.

**Acceptance criteria:**
- [x] Inventory tests prove: GUT project → `gut`; gdUnit4 project listed with refusal reason;
      bare Godot project ignored.
- [x] Fast tests cover argv construction, XML parse (fixture), empty-selection throw,
      `WholeSuite`, and PATH/`GODOT` resolution order.
- [x] Scale smoke runs a 2-test GUT fixture when godot+GUT are available; skips otherwise.
- [x] `ContinuousTestLanguageFamily` maps `.gd` and label `gdscript`.
- [x] Worker-scope verification passes and the change is committed per commit mode.

### Task 9: Docs and closeout

**Files:**
- Modify: `docs/continuous-testing.md` (framework table at line 21 + per-provider bounds
  sections), `docs/known-limits.md` (CT framework limits), `docs/site/index.html` (framework
  mentions, if the shipped list appears there), `README.md` (public provider matrix), and
  `docs/release-notes/v1.27.0.md` (new provider release summary)

**Interfaces:**
- Consumes: the shipped surface of Tasks 3–8, including any recorded adaptations.
- Produces: user-facing truth for `tests` consumers.

**Contract inputs:** The docs must state each provider's v1 bounds explicitly: Maven and sbt
per-class cases, GUT per-script cases, `minitest`/`gdunit4` refusal reasons and remedies, wrapper
resolution order, and minimum tool versions from Task 1.

**File ownership:** Modify: `docs/continuous-testing.md`, `docs/known-limits.md`,
`docs/site/index.html`, `README.md`, `docs/release-notes/v1.27.0.md`

**Serialization required:** Yes

**Dependency reason:** Documents the final shipped surface of Tasks 3–8.

**What to build:** Extend the supported-languages table with rows for `gradle`, `maven`, `sbt`,
`rspec`, `phpunit`, `pest`, `gut`; document refusals beside the existing `xunit-v2` precedent.
Update the remaining-gap list (swift, dart, elixir, erlang, c/c++/ctest, zig, lua, r, bash
remain unprovided — keep the honest inventory).

**Acceptance criteria:**
- [x] Framework table matches `ContinuousTestProviderFactory.CreateDefault` exactly.
- [x] Every v1 bound and refusal is documented with its remedy.
- [ ] Branch gate runs here: Release build, `scripts/test.sh scale` (skips recorded), win-test
      fast suite. Ledger updated.
- [x] Worker-scope verification passes and the change is committed per commit mode.

## Estimate

Agent-effort estimate: Task 1 is one focused session (tool installs may dominate); Tasks 2–4 and 8
are roughly one session each; Tasks 5–7 together about two sessions; Task 9 under one session.
Human time: plan approval now, and the branch-gate/win-test result review before merge.
