# Miller — agent working notes

Miller is a read-only .NET 10 SQLite/MCP consumer of `julie-server extract` output. It does not parse
source or use embeddings; extraction is delegated to the pinned `julie-server` binary. See
[README.md](README.md) for the architecture and [docs/miller-mvp-plan.md](docs/miller-mvp-plan.md) for
the milestone plan.

## Testing — read this before running tests

The suite is split into two categories. **Keep them separate; this is load-bearing.** julie's suite once
grew to 30+ minutes because slow integration tests ran on every change — Miller's split exists to prevent
that, and there are guards that will fail the build if the split erodes.

- **Default = fast suite.** A bare `dotnet test` runs ONLY `Category!=Scale` (pure logic + contract
  tests, no subprocess). This is enforced by `VSTestTestCaseFilter=Category!=Scale` in
  [`Miller.Tests.csproj`](tests/Miller.Tests/Miller.Tests.csproj) (the MSBuild default for `--filter`; a
  command-line `--filter` overrides it). Target <10s. **Run this on every change.** (A well-formed
  `.runsettings` `<TestCaseFilter>` works too; the csproj property is preferred because it needs no extra
  file and fails the build loudly on a typo instead of silently running everything.)
- **Scale suite is opt-in.** `Category=Scale` tests spawn the real `julie-server` or build large
  fixtures. Run them with `scripts/test.sh scale` (or `all`) before a commit/PR, or when you touch the
  indexing/extract path. They **skip** (not fail) if `.tools/julie-server` is missing.

Use the wrapper, not raw `dotnet test`, unless you have a reason:

```bash
scripts/test.sh         # fast suite + a wall-clock budget tripwire (<30s)
scripts/test.sh scale   # scale suite only
scripts/test.sh all     # both
```

### Rules when adding or changing tests

- **A test that spawns `julie-server` MUST be tagged `[Trait("Category","Scale")]`** at the class level,
  and MUST obtain the binary via `ScaleTestSupport.RequireJulieServer()` (the single launch signal). Do
  not re-add a private `LocateJulieServer()`/`RepoRoot()` copy — those were deduplicated into
  [`ScaleTestSupport`](tests/Miller.Tests/ScaleTestSupport.cs) precisely so the guard has one signal to
  trust.
- The convention guard
  [`ScaleTraitConventionTests`](tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs) source-scans
  the tests and FAILS if any file referencing the launch signal is not tagged Scale. If it fails, you
  added a julie-spawning test without the trait — add the trait, don't weaken the guard.
- A test may be Scale for other reasons (e.g. a 50k-symbol fixture build with no julie). That's fine; the
  guard is one-directional (spawns julie ⟹ Scale), not the converse.
- Keep the fast suite genuinely fast and pure. If a "fast" test starts doing real I/O or heavy work, it
  belongs in Scale.

## Build

- `dotnet build Miller.slnx -c Release` — warnings are errors (`Directory.Build.props`,
  `TreatWarningsAsErrors`). The build must be 0 warnings / 0 errors; analyzer warnings (e.g. CA1416,
  xUnit1051) are build errors.
- Project seam: `Miller.Core` is pure logic with ZERO I/O deps. Keep it that way — it's why the core is
  unit-tested in milliseconds.
