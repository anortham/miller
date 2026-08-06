# Task 5 report — JulieExtractRunner rebind verb seams

**Status:** DONE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/rebind-p3-miller-wiring`
**Branch:** `rebind-p3-miller-wiring`
**HEAD at start and at report time:** `b0d96b75`
**Commit SHA:** none - parallel-lead-commit

## What I implemented

All production changes are in `src/Miller.Indexing/JulieExtractRunner.cs`.

- **`BuildRebindArgs(string absDb, string absRoot)`** (`JulieExtractRunner.cs:349`) — pure argv seam emitting
  `rebind --root <ABS_ROOT> --db <ABS_DB> --strict-schema --json`. Rejects null/blank arguments, passes paths
  verbatim (no normalization in the builder, same rule as the other builders), and carries no
  `--file`/`--force`/`--ignore-file` because the verb accepts none of them.
- **`ParseRebindReport(string json) → RebindReport`** (`JulieExtractRunner.cs:373`) — pure parse of the
  additive top-level `rebind` object into the five contract fields. Reads through `JsonDocument` (the same
  AOT-safe idiom `ParseSupportedExtensions` uses) rather than the source-generated `ExtractReport` model,
  because the section is rebind-only and `ExtractReport.cs` / `IndexingJsonContexts.cs` are outside this
  task's file ownership. A missing section or a missing field is a `JsonException`: a refusal never reaches
  this method, because `Interpret`'s exit-code routing throws first.
- **`RebindReport`** record (`JulieExtractRunner.cs:934`) — `PreviousRoot`, `NewRoot`, `PreviousArtifactId`,
  `NewArtifactId`, `Changed`.
- **`Rebind(string dbPath, string newRoot, CancellationToken ct = default) → RebindReport`**
  (`JulieExtractRunner.cs:713`) — the live call. Resolves both paths absolute (same as `Scan`), routes through
  the shared `Run` → `Interpret` → `ExtractVersionMismatch.VerifyReport` path, then parses the section.
- **`Run` raw-stdout overload** (`JulieExtractRunner.cs:756`) — `Run(args)` now delegates to
  `Run(args, out string standardOutput, CancellationToken ct = default)`. The rebind section lives outside the
  shared report model, so the verb needs the raw JSON; every other caller discards it and its behaviour is
  unchanged.
- **Cancellation** — `Run` throws at entry if the token is already cancelled, and the wait loop kills the
  process tree and throws `OperationCanceledException` when the token trips. Existing callers pass no token,
  so their behaviour is byte-identical.

Typed outcomes (all through the existing `Interpret` contract — no new mapping code):

| refusal | exit | outcome |
| --- | --- | --- |
| `fingerprint_mismatch` | 3 | `IncompatibleExtractException`, message names the code |
| `no_committed_revision` | 3 | `IncompatibleExtractException`, message names the code |
| `artifact_changed` | 1 | `JulieExtractFailedException` carrying the recoverable diagnostic + `ExitCode = 1` |

## Verification

| invariant | scope label | command | result | timestamp |
| --- | --- | --- | --- | --- |
| Red first — the seams do not exist | worker-red-green | `dotnet test … --filter "FullyQualifiedName~JulieExtractRunnerRebindTests"` | FAILED to compile (CS0117 `BuildRebindArgs`/`ParseRebindReport`, CS1061 `Rebind`, CS0246 `RebindReport`) | 2026-08-05 18:47 CDT |
| Fast rebind seams green | worker-red-green | `dotnet test … --filter "FullyQualifiedName~JulieExtractRunnerRebindTests"` | Passed 21, Failed 0 (64 ms) | 2026-08-05 18:53 CDT |
| Live rebind on a real artifact copy | worker-red-green (my class only — NOT the scale gate) | `dotnet test … --filter "FullyQualifiedName~RebindVerbScaleTests"` | Passed 3, Failed 0 (550 ms) | 2026-08-05 18:58 CDT |
| Full fast suite | worker ceiling | `scripts/test.sh` | Passed 6053, Failed 1, Skipped 2 (28 s) — the one failure is `SqliteOnlineBackupTests` (Task 4's in-flight untracked file, not mine) | 2026-08-05 19:00 CDT |
| Full fast suite minus Task 4's class | worker ceiling | `dotnet test … --filter "Category!=Scale&FullyQualifiedName!~SqliteOnlineBackupTests"` | Passed 6038, Failed 1 — `SharedSemanticBrokerConnectionFactoryTests.PassiveObservation_HealthSilenceRespectsTheTotalWallClockBound`, a wall-clock-bound test that flaked under load (the run took 2m6s vs 28s); it passes in isolation | 2026-08-05 19:01 CDT |
| That flake is load-only | worker-red-green | `dotnet test … --filter "FullyQualifiedName~SharedSemanticBrokerConnectionFactoryTests"` | Passed 9, Failed 0 | 2026-08-05 19:02 CDT |
| Warnings-as-errors clean | worker ceiling | `dotnet build Miller.slnx -c Release` | Build succeeded, 0 Warning(s), 0 Error(s) | 2026-08-05 19:01 CDT |
| Scale-trait convention guard + existing runner tests still green after the final doc edit | worker-red-green | `dotnet test … --filter "…RebindTests\|…ScaleTraitConventionTests\|…JulieExtractRunnerTests"` | Passed 63, Failed 0 | 2026-08-05 19:04 CDT |

Neither of the two failures observed in the full-suite runs touches my files: one is a sibling task's
untracked in-flight class, the other is a timing-sensitive semantic-broker test that passes on its own.

## Files changed

- `src/Miller.Indexing/JulieExtractRunner.cs` (modified)
- `tests/Miller.Tests/Indexing/JulieExtractRunnerRebindTests.cs` (new, fast — 21 tests)
- `tests/Miller.Tests/Indexing/RebindVerbScaleTests.cs` (new, `[Trait("Category","Scale")]` — 3 tests)

No other file was touched.

## Miller calls used

| call | what it confirmed |
| --- | --- |
| `inspect target='src/Miller.Indexing/JulieExtractRunner.cs'` | Symbol list with line numbers — argv builders at 195/254/264/313/334, `BuildFileOpArgs` 338, `ParseReport` 349, `Interpret` 363, live `Run` 666. |
| `inspect target='JulieExtractRunner' depth=overview` | The class doc's exit-code contract (0/1/2/3), the pure-seams-are-static rule, the 11 dependents, and that `Run` is the single spawn chokepoint. |
| `inspect target='ExtractReport' depth=full` | The nested report model, its `[JsonPropertyName]` mapping, the computed `IsNoChange`/`CreatedRevision` accessors my tests assert on, and that it is source-generated through `JulieExtractJsonContext` (so a new section cannot be added without touching two files I do not own). |
| `inspect target='PathCanonicalizer' depth=overview` | `CanonicalizeRoot` is the symlink-resolving root resolver — this is what fixed the Scale assertion once julie returned `/private/var/...` for a `/var/...` temp root. |

`trace target='IncompatibleExtractException'` was not needed as a separate call: `inspect` on the runner
returned the exit-3 branch verbatim, and I read `IncompatibleExtractException.cs` and
`JulieExtractExceptions.cs` directly to confirm the constructor shapes (`IncompatibleExtractException(string
message)` — message only, no `Code` property; `JulieExtractFailedException(message, errors, stderr)` with
`ExitCode` fixed at 1). `ScaleTestSupport` I read in full to confirm `RequireJulieServer()` is the only
sanctioned launch signal.

## API-shape evidence (fixture provenance)

Every fast-test report fixture is a REAL julie-extract 2.27.0 report, captured on 2026-08-05 by running the
worktree's pinned `.tools/julie-extract` (`--version` → `2.27.0`) against a scratch artifact:

| fixture | how it was produced |
| --- | --- |
| `RebindOkJson` | `scan` a one-file tree → copy the artifact → `rebind` the copy at a second identical tree. Exit 0, `status: ok`, `rebind.changed: true`. |
| `RebindNoChangeJson` | a second `rebind` of the same copy at the root it now records. Exit 0, `status: no_change`, `rebind.changed: false`. |
| `FingerprintMismatchJson` | copy the artifact, overwrite `parser_inventory_fingerprint` with a zero hash, `rebind`. Exit 3, `fingerprint_mismatch`, `recoverable: false`, real `details` (both artifact/expected fingerprints + `action`). |
| `NoCommittedRevisionJson` | copy the artifact, `DELETE FROM extraction_revisions`, `rebind`. Exit 3, `no_committed_revision`, `recoverable: true`, `details.action: julie-extract scan`. |
| `ArtifactChangedJson` | the captured exit-1 rebind refusal envelope (produced with a missing `--db`), carrying the diagnostic julie emits from `crates/julie-extract-cli/src/artifact_access.rs` `check_validated_identity` — message `"artifact changed while rebind was validating"`, `recoverable: true`, `details` = `expected_root_path`/`found_root_path`/`expected_artifact_id`/`found_artifact_id`. |

Only filesystem path strings were shortened (`/repo/checkout-a`, `/repo/checkout-b`,
`/repo/.miller/symbols.db.rebuild`). Every key, null, count domain, fingerprint, `index_level`, and
`report_schema_version: 3` is the extractor's own emission — including the fields Miller does not read. The
one synthesised fixture is `ArtifactChangedJson`, because the refusal requires a writer racing the validation
that no fixture can stage; its diagnostic is copied from the emitting Rust source rather than invented, and
the class doc says so.

Live proof that the fixtures match reality: the Scale test asserts the same five fields off the real binary.

## Self-review findings

- **`Run`'s `out` parameter is unassigned on the throwing paths.** Correct by C# definite-assignment rules
  (only `return` requires assignment) and deliberate: a timeout, a cancel, or a refusal must not hand a caller
  a half-read stdout.
- **Cancellation kills mid-write.** Checked against the contract: the retarget's six metadata writes are one
  SQLite transaction, so a killed child leaves the artifact fully retargeted or metadata-identical. Documented
  on `Rebind` so Task 6 can just ask again.
- **`ExtractVersionMismatch.VerifyReport` runs on the rebind report too.** Wanted: a completing rebind report
  carries the `artifact` block, so a copy whose schema or contract version drifted from
  `MillerExtractContract` is caught at the rebind boundary instead of at the follow-up scan.
- **The Scale no-op test asserts `rebound_from_root` is absent.** The contract says a same-root rebind writes
  not one metadata row; asserting only `changed: false` would pass even if julie had stamped provenance.
- **Scale-trait guard re-run explicitly** (`ScaleTraitConventionTests`, 63 tests green with the runner suites)
  — `RebindVerbScaleTests` carries the class-level trait and takes its binary from
  `ScaleTestSupport.RequireJulieServer()`; no private locator was added.

## Judgment calls

1. **`ct` is optional (`= default`), not required.** The brief specifies `Rebind(dbPath, newRoot, ct)`; an
   optional token compiles for a Task 6 call site that passes one and keeps the guard tests terse. It is
   genuinely honoured (entry check + poll-loop check + kill), not accepted and ignored.
2. **`RebindReport` lives in `JulieExtractRunner.cs`, not `ExtractReport.cs`.** File ownership for this task is
   exactly one production file. The record is namespace-level, matching how `ExtractReport.cs` groups its
   records. If a later task prefers it beside the other report records, it is a pure move.
3. **The rebind section is parsed with `JsonDocument`, not added to `ExtractReport`.** Same ownership reason,
   and it is also the better shape: the section is rebind-only, so bolting it onto the model every verb shares
   would put a permanently-null property on eight other verbs' reports.
4. **The exit-3 code is preserved in the exception MESSAGE.** `IncompatibleExtractException` has no `Code`
   property and its file is not mine to change. Both refusal tests assert the code string is present. If Task 6
   or a later task needs to branch on the code programmatically, adding a `Code` property to that exception is
   the follow-up.
5. **`Rebind` uses `Path.GetFullPath`, not `PathCanonicalizer`.** It matches `Scan` (the follow-up call in the
   flow), and rebind runs no inside-root check, so verified-fact-4 does not apply. julie canonicalizes `--root`
   at its own boundary and records the canonical value — which is why the Scale test compares against
   `PathCanonicalizer.CanonicalizeRoot`, and why a `/var` → `/private/var` temp root round-trips correctly.
6. **A third Scale test beyond the brief's shape.** `Rebind_OnAnArtifactWithNoCommittedRevision_RefusesAsIncompatible`
   proves the exit-3 → `IncompatibleExtractException` mapping against the live binary rather than only against
   a fixture. It costs ~60 ms.

## Concerns / notes for the lead

- The full fast suite currently has ONE unrelated failure from a sibling task's in-flight untracked file
  (`SqliteOnlineBackupTests.Copy_BudgetElapsedBetweenSteps_ReportsExhaustedAndDeletesThePartialDestination`,
  expected `BudgetExhausted`, got `Completed`). It is Task 4's to close; my files are green.
- `SharedSemanticBrokerConnectionFactoryTests.PassiveObservation_HealthSilenceRespectsTheTotalWallClockBound`
  fails when the machine is loaded by parallel workers and passes in isolation. Worth knowing before the lead
  reads a red full-suite run as a regression.
- Task 6 should catch `IncompatibleExtractException` (permanent — needs a fresh scan, never a retry) and
  `JulieExtractFailedException` (recoverable — the artifact is unchanged, the rebind did not happen) around
  `Rebind`, and treat `Changed == false` as success, not as a no-op to be retried.
- The xUnit `xUnit1051` analyzer is warnings-as-errors here: any test calling `Rebind` MUST pass
  `TestContext.Current.CancellationToken`. Task 6's tests will hit this too.
