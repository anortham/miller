# Continuous-test runner surfaces

This finding records the runner commands that the JVM, Ruby, PHP, and GDScript providers are
expected to use. The commands were checked against the official documentation on 2026-09-01 and
against this machine's PATH. Godot 4.7.2 and GUT 9.7.1 were installed for the 2026-09-02 Scale
probe; the initial captured report exposed a provider attribution defect, which was corrected in
Task 2. The follow-up run exposed a Godot-generated import-metadata churn issue recorded below.
The other runtime statuses remain as listed. The report fixtures in
`tests/Miller.Tests/Testing/Providers/Fixtures/` are docs-derived shape fixtures, not captured
runtime output; they contain no machine-specific paths or timestamps.

## Product support floors

These are Miller's product floors, separate from the versions of the official documentation
consulted below. An absent runner still requires runtime confirmation at or above its product floor.

| Backend | Product support floor | Documentation version consulted |
| --- | --- | --- |
| Gradle | Gradle 8.3+ | Current guide plus Gradle 8.3 release/API pages |
| Maven Surefire | Maven runtime floor not separately constrained; Surefire 2.7.3+ for the documented method-selection surface | Current Surefire goal and plugin guide |
| sbt | sbt 1.x | sbt 1.x reference/manual |
| RSpec | RSpec 3.x | RSpec Core 3.12 feature pages and v3.12.3 formatter source |
| PHPUnit | PHPUnit 10+ | PHPUnit 12.5 CLI manual |
| Pest | Pest 2+ | Current Pest CLI API reference |
| GUT | Godot 4 + GUT 9 | GUT 9.3.1 command-line and 9.6.0 export pages |

## Runtime evidence

The following probes were run from a temporary directory outside the repository. Missing commands
returned shell exit code 127. The original probe found Java 25.0.4.1 and Ruby 4.0.6 while their
build/test runners were absent; the 2026-09-02 Godot/GUT probe is recorded below.

| Runner | `--version` / help probe | Runtime status |
| --- | --- | --- |
| Gradle | `gradle --version`, `gradle --help`, `gradle test --test-dry-run` | not installed |
| Maven | `mvn --version`, `mvn --help`, `mvn test-compile` | not installed |
| sbt | `sbt --version`, `sbt --help`, `sbt 'show Test/definedTests'` | not installed |
| RSpec | `rspec --version`, `rspec --help`, `rspec --dry-run --format json` | not installed |
| PHP / PHPUnit | `php --version`, `phpunit --version`, `phpunit --help`, `phpunit --list-tests-xml <file>` | not installed |
| Pest | `pest --version`, `pest --help`, `pest --list-tests-xml <file>` | not installed |
| Godot / GUT | `godot --version`, `godot --headless --help`, GUT command probe | Godot 4.7.2/GUT 9.7.1 installed; warm mirror gate fails on generated `.import` churn |

## Gradle

**Status:** not installed — surface taken from [Gradle Java testing documentation](https://docs.gradle.org/current/userguide/java_testing.html), [Gradle 8.3 release notes](https://docs.gradle.org/8.3/release-notes.html), and [the `Test.dryRun` API](https://docs.gradle.org/current/kotlin-dsl/gradle/org.gradle.api.tasks.testing/-test/get-dry-run.html); needs runtime confirmation.

- **Product support floor:** Gradle 8.3+. The test dry-run property and `--test-dry-run` option
  are documented as since 8.3. **Documentation consulted:** current Java testing guide plus the
  Gradle 8.3 release notes and `Test.dryRun` API.
- **Discovery:** `gradle test --test-dry-run`. Dry-run skips test execution but still generates
  reports, so the testcases in `build/test-results/test/` enumerate the selected tests.
- **Selection:** `gradle test --tests "fully.qualified.Class.method"`; simple class/method names
  and wildcard patterns are also accepted.
- **Report artifact:** JUnit XML, one file per test class, under
  `build/test-results/test/*.xml` by default. The output location is configurable.
- **Exit semantics:** a successful task, including a successful dry-run, exits 0; a failed test
  or build exits nonzero. In dry-run mode JUnit 4/5 tests are reported as skipped by the API.
  Exit behavior was not run locally and needs runtime confirmation.
- **Fixture:** `gradle-junit.xml` is a sanitized ordinary Gradle JUnit report shape containing
  passed, failed, and skipped cases. It is docs-derived because Gradle is absent.

## Maven Surefire

**Status:** not installed — surface taken from [the Surefire test goal reference](https://maven.apache.org/surefire/maven-surefire-plugin/test-mojo) and [the current Surefire plugin guide](https://maven.apache.org/components/surefire-archives/surefire-LATEST/maven-surefire-plugin/); needs runtime confirmation.

- **Product support floor:** Maven's runtime floor is not separately constrained by the cited
  official docs; require Surefire 2.7.3+ for the documented `-Dtest=Class#method` method-selection
  surface. **Documentation consulted:** current Surefire test-goal reference and plugin guide.
- **Discovery:** Maven has no Surefire dry-run/list command in the cited surface. Run
  `mvn test-compile`, then enumerate compiled test classes under `target/test-classes/` using the
  default include suffixes `Test*`, `*Test`, `*Tests`, and `*TestCase` (relative to that directory).
- **Selection:** `mvn -Dtest=Class test` for a class and
  `mvn -Dtest=Class#method test` for a method. The `test` property overrides the default includes.
- **Report artifact:** `target/surefire-reports/TEST-*.xml` by default; Surefire's
  `reportsDirectory` changes the base directory.
- **Exit semantics:** a passing Maven test phase exits 0; test, compilation, or build errors exit
  nonzero. This was not run locally and needs runtime confirmation.
- **Fixture:** `maven-surefire.xml` is a sanitized Surefire report shape with properties, output,
  pass/fail/skip cases, and a CDATA failure body. It is docs-derived because Maven is absent.

## sbt

**Status:** sbt 1.13.0 launcher/load probe completed from the official universal package; test
compilation and execution remain unavailable because this host has a Java runtime but no `javac`.
The command surface still comes from [the sbt testing guide](https://www.scala-sbt.org/1.x/docs/Testing.html)
and [the sbt keys API](https://www.scala-sbt.org/1.x/api/sbt/Keys%24.html).

- **Product support floor:** sbt 1.x. **Documentation consulted:** sbt 1.x testing guide and
  keys API; no narrower floor for the `Test` keys is stated. The provider must record the runtime
  sbt version when a scale host is available.
- **Discovery:** `show Test/definedTestNames` is the documented command that returns the detected
  test names after test compilation. `show Test/definedTests` is also a valid lower-level query
  (`Seq[TestDefinition]`) and is the shape named by the implementation plan.
- **Selection:** `sbt testOnly fully.qualified.Class`; multiple class names and wildcards are
  whitespace-separated. Framework arguments follow a `--` separator.
- **Report artifact:** JUnit XML under `target/test-reports/*.xml` by default; the report plugin
  can be disabled.
- **Exit semantics:** a successful `test`/`testOnly` task exits 0 and a failed test/build exits
  nonzero. This was not run locally and needs runtime confirmation.
- **Output-isolation blocker:** an official sbt 1.13.0 disposable probe redirected
  `sbt.boot.directory`, `sbt.global.base`, `sbt.ivy.home`, and `sbt.coursier.home`, disabled the
  server and generated build properties, and applied a session `ThisBuild / target` override.
  Build loading still created both workspace `target/` and `project/target/` before the session
  setting could take effect. A runnable provider therefore needs a generation-owned build/source
  shadow or a narrower product decision; cache redirection alone cannot satisfy CT's write-isolation
  contract.
- **Fixture:** `sbt-junit.xml` is a sanitized nested JUnit report shape with an error case and a
  skipped case. It is docs-derived because sbt is absent.

## RSpec

**Status:** not installed — surface taken from [RSpec command-line documentation](https://rspec.info/features/3-12/rspec-core/command-line/), [`--dry-run`](https://rspec.info/features/3-12/rspec-core/command-line/dry-run/), [`--format`](https://rspec.info/features/3-12/rspec-core/command-line/format-option/), [line-number selection](https://rspec.info/features/3-12/rspec-core/command-line/line-number-appended-to-path/), [exit status](https://rspec.info/features/3-12/rspec-core/command-line/exit-status/), and [the JSON formatter](https://rspec.info/features/3-12/rspec-core/formatters/json-formatter/); needs runtime confirmation.

- **Product support floor:** RSpec 3.x. **Documentation consulted:** RSpec Core 3.12 feature pages
  and the v3.12.3 JSON formatter source. The installed RSpec version must be captured before
  enabling the provider.
- **Discovery:** `rspec --dry-run --format json`. The JSON object has `version`, `examples`,
  `summary`, and `summary_line`; each example includes `id`, `file_path`, `line_number`, `status`,
  and `run_time`. Dry-run prints formatter output without running examples or hooks.
- **Selection:** `rspec path/to/spec.rb:<line>` selects by source location. The raw JSON `id` is
  an internal hierarchical value such as `./spec/calculator_spec.rb[1:1]`, not `file:line`; derive
  the stable location selector from `file_path` and `line_number`, falling back to raw `id` when
  multiple examples share a location.
- **Report artifact:** the caller chooses the path with
  `rspec --format json --out <file>`; RSpec has no fixed report directory.
- **Exit semantics:** exit 0 when all examples pass (and when no examples run by default), exit 1
  when an example fails; `--failure-exit-code` can override the failure value.
- **Fixtures:** `rspec-dry-run.json` captures the docs-derived dry-run shape with passed examples;
  `rspec-run.json` captures passed, failed, and pending examples plus a failure exception. Both
  are docs-derived because RSpec is absent.

## PHPUnit

**Status:** not installed — surface taken from [PHPUnit 12.5 CLI options](https://docs.phpunit.de/en/12.5/cli-options.html); needs runtime confirmation.

- **Product support floor:** PHPUnit 10+. **Documentation consulted:** PHPUnit 12.5 CLI manual.
- **Discovery:** `phpunit --list-tests-xml <file>`. It writes the selected test list as XML and
  exits without executing tests.
- **Selection:** `phpunit --filter <pattern>`; the pattern may be a PCRE expression or the
  documented individual-data-set shortcut.
- **Report artifact:** `phpunit --log-junit <file>` writes JUnit XML to the caller-selected path.
- **Exit semantics:** a normal pass exits 0 and defects exit nonzero; PHPUnit's `--fail-on-*`
  options can make warnings, skips, incomplete tests, or an empty suite fail. This was not run
  locally and needs runtime confirmation.
- **Fixture:** `phpunit-junit.xml` is a sanitized nested PHPUnit report shape with `class`,
  `file`, `assertions`, and pass/fail/skip cases. It is docs-derived because PHP is absent.

## Pest

**Status:** not installed — surface taken from [Pest's CLI API reference](https://pestphp.com/docs/cli-api-reference); needs runtime confirmation.

- **Product support floor:** Pest 2+. **Documentation consulted:** current Pest CLI API reference;
  the installed Pest version must be captured before enabling it.
- **Discovery:** `pest --list-tests-xml <file>`.
- **Selection:** `pest --filter <pattern>`.
- **Report artifact:** `pest --log-junit <file>` writes JUnit XML to the caller-selected path.
- **Exit semantics:** Pest documents the same PHPUnit-style defect and `--fail-on-*` controls;
  treat a normal pass as 0 and a defect as nonzero, subject to runtime confirmation.
- **Deviation:** none in the documented CLI surface: Pest accepts the same list-tests XML,
  JUnit logging, and filter options. It produces the PHPUnit-compatible JUnit dialect, so the
  PHPUnit fixture is the parser acceptance shape; no separate Pest fixture is required by Task 1.

## GUT (Godot Unit Test)

**Status:** Godot 4.7.2 and GUT 9.7.1 runtime probe executed on 2026-09-02. Task 2 now
normalizes the relative inner-class JUnit row, and the final fixture passes the focused, whole-suite,
and warm mirror gates. The runtime-only fixture omits the 26 generated `.import` sidecars present in
the downloaded GUT archive while retaining its own SVG asset so Godot import behavior remains real.
The documented surface comes from [GUT 9.3.1
command-line documentation](https://gut.readthedocs.io/en/9.3.1/Command-Line.html) and [GUT 9.6.0
JUnit export documentation](https://gut.readthedocs.io/en/v9.6.0/Export-Test-Results.html).

- **Product support floor:** Godot 4 + GUT 9. **Documentation consulted:** GUT 9.3.1 command-line
  guide and GUT 9.6.0 export guide; 9.6.0 is the cited baseline for the JUnit export surface.
- **Discovery:** `godot --headless -s addons/gut/gut_cmdln.gd -gdir=res://tests -gexit`.
  `-gdir=<dir>` discovers scripts; `-gtest=<path>` can name scripts directly. GUT options require
  `-gname=value` with no spaces around `=`.
- **Selection:** `-gselect=<substring>` selects a script containing the substring;
  `-gunit_test_name=<substring>` runs tests whose names contain the substring. These are
  substring filters, not exact IDs.
- **Report artifact:** add `-gjunit_xml_file=<file>`; optionally add
  `-gjunit_xml_timestamp` to avoid overwriting the previous report.
- **Exit semantics:** command-line GUT returns 0 when all tests pass and 1 when any test fails;
  pending tests do not affect the return value.
- **Fixture:** `gut-junit.xml` is a sanitized export shape with GUT's `status="pass|fail|pending"`
  testcase attributes and `res://` virtual paths. It remains a parser fixture; the real report below
  uses relative paths.

### 2026-09-02 runtime probe

- **Tools:** `Godot_v4.7.2-stable_linux.x86_64` reported `4.7.2.stable.official.ed1daf0bf`; the copied addon `addons/gut/plugin.cfg` reported GUT `9.7.1`.
- **Command:** `GODOT=/home/murphy/source/miller/.worktrees/ct-providers-jvm-ruby-php-gdscript/.tools/ct-godot/Godot_v4.7.2-stable_linux.x86_64 MILLER_GUT_ROOT=/home/murphy/source/miller/.worktrees/ct-providers-jvm-ruby-php-gdscript/.tools/ct-gut/Gut-9.7.1 dotnet test --filter "FullyQualifiedName~GodotTestProviderScaleTests" --no-restore`.
- **Fixture:** two GUT scripts, a `TestInner` inner class, a `class_name` dependency, an SVG asset, and the GUT addon. The downloaded archive contains 26 `.import` sidecars; the runtime-only copy omits those generated entries and keeps the fixture SVG. The provider ran a cold version/import/GUT sequence and wrote a contained JUnit artifact.
- **Captured report:** GUT emitted `classname="tests/test_primary.gd.TestInner"` and suite `tests/test_primary.gd.TestInner`; Task 2 commit `3bfada4559d3ec0ed0b4398baf3aafff65816b41` now normalizes that contained relative path to the selected `res://` script.
- **Result:** the corrected provider returned the focused one-script result and warm whole-suite two-script results. Hard warm gates passed with `imported=false`, `entries_copied=0`, `entries_updated=0`, `entries_deleted=0`, `bytes_copied=0`, `files_hashed=0`, and `bytes_hashed=0`. Cold import ran with copied entries and bytes greater than zero; source and global-path immutability, contained report attribution, and contained writes also passed.
- **Raw report-only metrics:** cold `mirror=60.6ms`, `version=30.9ms`, `import=2578.7ms`, `gut=1095.8ms`, `report_copy=2.1ms`, `project_candidate=3634363B`; warm `mirror=19.5ms`, `version=20.7ms`, `import=0.0ms`, `gut=1092.6ms`, `report_copy=0.2ms`, `project_candidate=3619910B`, `godot_home=3291836B`. These are same-fixture/same-runtime observations, not percentage gates.

## Follow-up runtime gate

The next provider tasks must rerun the documented commands on a host that has each toolchain,
capture real version/help output, and replace any fixture whose schema differs. The committed
fixtures are sufficient as a parser-shape corpus but are not evidence that an absent executable
can run.
