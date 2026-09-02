# Continuous-test runner surfaces

This finding records the runner commands that the JVM, Ruby, PHP, and GDScript providers are
expected to use. The commands were checked against the official documentation on 2026-09-01 and
against this machine's PATH. Gradle, Maven, sbt, RSpec, PHP, PHPUnit, Pest, and Godot were not
installed, so no throwaway project could execute a provider command here. The report fixtures in
`tests/Miller.Tests/Testing/Providers/Fixtures/` are docs-derived shape fixtures, not captured
runtime output; they contain no machine-specific paths or timestamps.

## Runtime evidence

The following probes were run from a temporary directory outside the repository. Missing commands
returned shell exit code 127. The only relevant runtimes present were Java 25.0.4.1 and Ruby
4.0.6; their build/test runners were absent.

| Runner | `--version` / help probe | Runtime status |
| --- | --- | --- |
| Gradle | `gradle --version`, `gradle --help`, `gradle test --test-dry-run` | not installed |
| Maven | `mvn --version`, `mvn --help`, `mvn test-compile` | not installed |
| sbt | `sbt --version`, `sbt --help`, `sbt 'show Test/definedTests'` | not installed |
| RSpec | `rspec --version`, `rspec --help`, `rspec --dry-run --format json` | not installed |
| PHP / PHPUnit | `php --version`, `phpunit --version`, `phpunit --help`, `phpunit --list-tests-xml <file>` | not installed |
| Pest | `pest --version`, `pest --help`, `pest --list-tests-xml <file>` | not installed |
| Godot / GUT | `godot --version`, `godot --headless --help`, GUT command probe | not installed |

## Gradle

**Status:** not installed — surface taken from [Gradle Java testing documentation](https://docs.gradle.org/current/userguide/java_testing.html), [Gradle 8.3 release notes](https://docs.gradle.org/8.3/release-notes.html), and [the `Test.dryRun` API](https://docs.gradle.org/current/kotlin-dsl/gradle/org.gradle.api.tasks.testing/-test/get-dry-run.html); needs runtime confirmation.

- **Minimum version:** Gradle 8.3. The test dry-run property and `--test-dry-run` option are
  documented as since 8.3.
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

- **Minimum version:** no Maven runtime floor is stated by the cited pages. Surefire's
  `-Dtest=Class#method` method-selection syntax is documented as available since Surefire 2.7.3;
  the provider should verify the project plugin version before relying on it.
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

**Status:** not installed — surface taken from [the sbt testing guide](https://www.scala-sbt.org/1.x/docs/Testing.html) and [the sbt keys API](https://www.scala-sbt.org/1.x/api/sbt/Keys%24.html); needs runtime confirmation.

- **Minimum version:** the cited documentation is for sbt 1.x; no narrower minimum for the
  `Test` keys is stated. The provider must record the runtime sbt version when a scale host is
  available.
- **Discovery:** `show Test/definedTestNames` is the documented command that returns the detected
  test names after test compilation. `show Test/definedTests` is also a valid lower-level query
  (`Seq[TestDefinition]`) and is the shape named by the implementation plan.
- **Selection:** `sbt testOnly fully.qualified.Class`; multiple class names and wildcards are
  whitespace-separated. Framework arguments follow a `--` separator.
- **Report artifact:** JUnit XML under `target/test-reports/*.xml` by default; the report plugin
  can be disabled.
- **Exit semantics:** a successful `test`/`testOnly` task exits 0 and a failed test/build exits
  nonzero. This was not run locally and needs runtime confirmation.
- **Fixture:** `sbt-junit.xml` is a sanitized nested JUnit report shape with an error case and a
  skipped case. It is docs-derived because sbt is absent.

## RSpec

**Status:** not installed — surface taken from [RSpec command-line documentation](https://rspec.info/features/3-12/rspec-core/command-line/), [`--dry-run`](https://rspec.info/features/3-12/rspec-core/command-line/dry-run/), [`--format`](https://rspec.info/features/3-12/rspec-core/command-line/format-option/), [line-number selection](https://rspec.info/features/3-12/rspec-core/command-line/line-number-appended-to-path/), [exit status](https://rspec.info/features/3-12/rspec-core/command-line/exit-status/), and [the JSON formatter](https://rspec.info/features/3-12/rspec-core/formatters/json-formatter/); needs runtime confirmation.

- **Minimum version:** RSpec Core 3.12 is the cited documentation baseline; no older-version
  floor was established. The installed RSpec version must be captured before enabling the provider.
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

- **Minimum version:** PHPUnit 12.5 is the cited documentation baseline; no older-version floor
  was established by this spike.
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

- **Minimum version:** no minimum version is stated on the cited page; the current Pest CLI
  reference documents the surface. The installed Pest version must be captured before enabling it.
- **Discovery:** `pest --list-tests-xml <file>`.
- **Selection:** `pest --filter <pattern>`.
- **Report artifact:** `pest --log-junit <file>` writes JUnit XML to the caller-selected path.
- **Exit semantics:** Pest documents the same PHPUnit-style defect and `--fail-on-*` controls;
  treat a normal pass as 0 and a defect as nonzero, subject to runtime confirmation.
- **Deviation:** none in the documented CLI surface: Pest accepts the same list-tests XML,
  JUnit logging, and filter options. It produces the PHPUnit-compatible JUnit dialect, so the
  PHPUnit fixture is the parser acceptance shape; no separate Pest fixture is required by Task 1.

## GUT (Godot Unit Test)

**Status:** not installed — surface taken from [GUT 9.3.1 command-line documentation](https://gut.readthedocs.io/en/9.3.1/Command-Line.html) and [GUT 9.6.0 JUnit export documentation](https://gut.readthedocs.io/en/v9.6.0/Export-Test-Results.html); needs runtime confirmation.

- **Minimum version:** GUT 9.6.0 is the documented baseline for the JUnit export surface;
  command-line selection is also shown in the 9.3.1 guide. No older export floor was established.
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
  testcase attributes and `res://` virtual paths. It is docs-derived because Godot is absent.

## Follow-up runtime gate

The next provider tasks must rerun the documented commands on a host that has each toolchain,
capture real version/help output, and replace any fixture whose schema differs. The committed
fixtures are sufficient as a parser-shape corpus but are not evidence that an absent executable
can run.
