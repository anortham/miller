# Test-suite defense plan (T1–T4) — APPROVED, ready to build after M8 lands

Goal (Alan): keep Miller's default test suite fast forever so agents can run it on every
change. Avoid julie's trap (30+ min suite). NOT a current problem — default suite is
**780 tests in ~1s** today. Job is to KEEP it that way as the suite grows.

## Verified current state (2026-05-30, read-only audit)
- ONE test project: tests/Miller.Tests/Miller.Tests.csproj (xunit v3).
- **NO `.runsettings` anywhere** → bare `dotnet test` runs EVERYTHING incl. the 12 Scale
  tests (which spawn the julie-server subprocess). THIS is the only real foot-gun today.
- Scale tag applied at CLASS level via `[Trait("Category","Scale")]` (consistent, not
  scattered) on ~20 files. 647 [Fact]/[Theory] total; ~12 Scale tests.
- ~12 fast tests legitimately use temp SQLite/files and still finish sub-second
  (TelemetryLedgerTests, EditApplierTests, etc.). So a blunt "I/O ⇒ must be Scale" rule
  is WRONG for Miller — it would gut meaningful default coverage. The real enemy is the
  **julie-server subprocess**, not I/O.

## The plan (T2 corrected from the original blunt-I/O idea)
- **T1 — `.runsettings` default filter** `TestCategory!=Scale`. Makes zero-arg
  `dotnet test` safe-by-default. #1 defense. (Verify: dotnet auto-detects a single
  .runsettings in the test project dir / via <RunSettingsFilePath> in csproj; an explicit
  `--filter` on the CLI overrides it, which is what the scale wrapper will use.)
- **T2 — drift guard (convention test, DEFAULT suite):** assert every test type that
  touches the julie-server subprocess (uses JulieDbFixture / JulieExtractRunner /
  `.tools/julie-server`) carries [Trait("Category","Scale")]. Reflection over the test
  assembly. NOT a blunt I/O classifier. This is what actually prevents the 30-min slide.
- **T3 — budget tripwire as a CI/script shell step (NOT an in-suite xunit timing test —
  those flake under load, cf. the M5 latency test).** Time the default suite; fail if it
  exceeds a generous ceiling (~60s = 60x current headroom). Mechanism-agnostic.
- **T4 — CLAUDE.md docs + `scripts/test.sh {fast|scale|all}` wrapper.** Agents reach for
  documented commands; a one-word `fast` default is what they'll use. Directly addresses
  the "agents run the whole suite" worry.

## Sequencing / guardrails
- DO NOT start until the M8 workflow (wf_b689aaba-83e) finishes — its fix phase edits test
  files; collision would corrupt both.
- After M8 lands: (1) independently verify M8 (clean rebuild + BOTH suites — never trust
  the workflow self-report), (2) confirm/fix the M8 review findings, THEN (3) build T1–T4.

## M8 review findings to resolve during M8 verification (from the workflow's review phase)
- **HIGH (multi-lens):** LogFileReaper can unlink a *live peer* process's log files on
  macOS/Linux (recency-only policy, no liveness check; File.Delete unlinks open files
  silently; catch only handles IOException/UnauthorizedAccess). Fix: liveness gate
  (Process.GetProcessById in try/catch) before delete + a test.
- **MEDIUM:** `role` (leader/reader) log property documented in MillerLoggingSetup XML-doc
  + required by D2, but never emitted (no WithProperty/PushProperty/{role} token). Fix:
  emit role on leader-election transitions + add {role} to template, OR strip the doc claim
  if deferring. (Honesty: don't advertise a field that isn't written.)
- LOW: reap is count-only not age-aware (D6 said age too); pre-M8 legacy miller-<date>.log
  never reaped; generic catch logs empty "julie stderr:" label for non-julie exceptions;
  ReapStaleLogs discovery step (EnumerateExisting) not wrapped → could throw out of startup.
