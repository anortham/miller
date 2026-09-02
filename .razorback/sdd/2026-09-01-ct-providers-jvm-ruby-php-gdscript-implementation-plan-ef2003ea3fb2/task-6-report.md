### Task 6 worker report

- Worktree: `/home/murphy/source/miller/.worktrees/ct-providers-jvm-ruby-php-gdscript`
- Branch: `feature/ct-providers-jvm-ruby-php-gdscript`
- Base at dispatch: `eccc2023`
- Scope: Maven backend, JVM registration/seam support, Maven toolchain test helper, focused tests, Scale smoke, and Scale convention coverage.
- Implementation: `MavenTestBackend` compiles for discovery, scans generation-local Surefire class patterns, resolves `mvnw` module/workspace wrappers before PATH, redirects Maven/Surefire/build/repository/temp paths, chunks class selectors, aggregates method reports to class verdicts, and rejects unsafe or partial artifacts.
- Identity: Maven class cases use the stable internal class sentinel; provider-facing selectors and Maven `-Dtest=` arguments contain class names only.
- Tests added: wrapper/PATH resolution, class include/exclude scanning, class selection chunking, whole-suite command shape, report aggregation, partial-attribution refusal, and a two-class Maven Scale fixture with no-target/tree-hash assertions.
- Verification: `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1 dotnet test --filter "FullyQualifiedName~MavenTestBackendTests|FullyQualifiedName~JvmTestProviderTests|FullyQualifiedName~ContinuousTestProviderFactoryTests|FullyQualifiedName~CtScaleTraitConventionTests"` — 23 passed.
- Verification: Maven Scale smoke filter — 1 skipped because Maven is not installed.
- Verification: `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1 dotnet build Miller.slnx -c Release` — 0 warnings, 0 errors.
- Verification: `git diff --check` — clean.
- External evidence: Maven/Surefire official docs confirm `test-compile`, `-Dtest`, `surefire.reportsDirectory`, and compiler output-directory properties; no local Maven runtime was available.
- Blockers: none for this bounded packet; live Maven runtime evidence remains unavailable until a Maven toolchain host runs the Scale fixture.

### Correction pass

- `MavenTestBackend.BuildCommand` no longer emits `-DargLine` or overrides `MAVEN_OPTS`; it retains the CLI `-Djava.io.tmpdir` and `TMPDIR`/`TEMP`/`TMP` environment paths.
- `ValidateSelections` now validates every selected class name and the class identity sentinel before the `wholeSuite` return; whole-suite method selections are rejected.
- Surefire report containment is checked against the generation-local `surefire-reports` directory.
- `CtProviderTestSupport.RequireJava` now requires both the Java launcher and `javac`, while returning the launcher path.
- Focused verification: `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1 dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter 'FullyQualifiedName~MavenTestBackendTests|FullyQualifiedName~JvmTestProviderTests' --no-restore` — 17 passed.
- Scale verification without a Maven toolchain — 1 skipped as expected. With official Apache Maven 3.9.16 on `PATH`, the pre-correction smoke reached Maven but failed because this host has Java 25 without `javac` (`release version 17 not supported`); after the `RequireJava` guard, the exact Maven filter skips cleanly.
- Release verification: `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1 dotnet build Miller.slnx -c Release --no-restore` — 0 warnings, 0 errors.
- `git diff --check` — clean. Official Maven 3.9.16 was downloaded to `/tmp` only and SHA-512 verified; no runtime artifacts were added to the repository.
- Correction implementation commit: `9ea26a8e` (`fix(ct): require compiler for Maven Scale`).

### Correction completion

- Added `CtProviderTestSupportTests.LocateJava_requires_a_compiler_and_returns_the_launcher_path`, proving a Java launcher without `javac` returns no toolchain and both tools return the Java launcher path.
- Red/green evidence: the regression failed against the old Java-only locator, then passed with the compiler-aware locator.
- Focused verification: `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1 dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter 'FullyQualifiedName~MavenTestBackendTests'` — 8 passed.
- Focused verification: `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1 dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter 'FullyQualifiedName~JvmTestProviderTests'` — 9 passed.
- Focused verification: `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1 dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter 'FullyQualifiedName~CtProviderTestSupportTests.LocateJava_requires_a_compiler_and_returns_the_launcher_path'` — 1 passed.
- Maven Scale verification with `PATH=/tmp/miller-maven-probe-MmBXCI/apache-maven-3.9.16/bin:$PATH` and both documented missing-tool escape hatches — 1 skipped honestly because `javac` is absent; `/usr/bin/java` and the Maven probe are present.
- Release verification: `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1 dotnet build Miller.slnx -c Release` — 0 warnings, 0 errors.
- `git diff --check` — clean.
- Correction implementation commit: `9ea26a8e` (`fix(ct): require compiler for Maven Scale`).
