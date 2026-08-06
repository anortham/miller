# Task 4 report — Carry the julie-extract exit code on `IncompatibleExtractException` into W8

## Worktree state

- Path: `/Users/murphy/source/miller/.claude/worktrees/p4-findings-fixes`
- Branch: `p4-findings-fixes`
- Base HEAD: `bc808b26` (unchanged — commit mode is `parallel-lead-commit`; no `git add` / `git commit` run)

## Miller orientation (calls used)

| Call | Purpose | Result |
| --- | --- | --- |
| `trace target=IncompatibleExtractException mode=refs workspace_id=miller-b275269b2d7c limit=40` | Enumerate construction/usage sites | 16 exact refs, page-truncated; definition at `src/Miller.Indexing/IncompatibleExtractException.cs:9` |
| `trace target=ExitCodeOf mode=refs workspace_id=miller-b275269b2d7c limit=30` | Enumerate the W8 journal call sites | 6 exact refs, definition at `src/Miller.Indexing/JulieExtractExceptions.cs:39` |

Both trace results were confirmed against the worktree files before editing (the worktree itself is not
indexed; `miller-b275269b2d7c` is the main checkout at the same base commit).

### `ExitCodeOf` callers (all 6, from `trace refs`)

- `src/Miller.Indexing/RebindBootstrap.cs:516` in `Run`
- `src/Miller.Server/Hosting/IndexBootstrapService.cs:1490` in `RunRecordedScan`
- `src/Miller.Server/Hosting/IndexerCore.cs:343` in `RecordScanFailure`
- `src/Miller.Server/Hosting/IndexerService.cs:1198` in `RecordScanFailure`
- `src/Miller.Server/Tools/WorkspaceTool.cs:1289` in `Open`
- `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs:319` in `Refresh`

None were modified. The signature `static int? ExitCodeOf(Exception?)` is unchanged, so every caller keeps
compiling and keeps its current behavior except that a runner exit-3 refusal now yields `3` instead of `null`.

### `IncompatibleExtractException` construction sites (repo-wide, `src/`)

| Site | Kind | Exit code after this change |
| --- | --- | --- |
| `JulieExtractRunner.cs:473` (`Interpret` case 3) | julie-extract subprocess exit 3 | **3 (changed)** |
| `JulieSchemaGate.cs` (7 sites: 41, 51, 62, 79, 93, 116, 140) | read-path schema gate | null (unchanged) |
| `ExtractVersionMismatch.cs` (6 sites: 65, 73, 78, 87, 96, 101) | version cross-check gate | null (unchanged) |
| `ReferenceEvidenceReader.cs` (314, 355) | read-path table gate | null (unchanged) |
| `DeadCodeCandidateReader.cs:87` | read-path table gate | null (unchanged) |
| `WorkspaceHealthReader.cs:112` | read-path table gate | null (unchanged) |
| `ContentCorpusExportReader.cs:239` | read-path schema gate | null (unchanged) |

No gate site was touched; each still binds to the pre-existing `(string)` or `(string, Exception)` constructor.

## API-shape evidence

- `JulieExtractException.ExitCode` is already `int?` with the same null-means-no-observed-exit semantics
  (`JulieExtractExceptions.cs:20`). The new `IncompatibleExtractException.ExitCode` mirrors it exactly, so the
  W8 journal's `exit_code` field needs no schema change.
- `IncompatibleExtractException` is `sealed` and derives from `Exception`, **not** from `JulieExtractException` —
  which is precisely why `ExitCodeOf`'s single `as JulieExtractException` cast returned null for rebind refusals.
- `JulieExtractRunner.Interpret(int, string, string)` is `public static` and pure (no subprocess), so the rebind
  refusal mapping is unit-testable without `julie-extract`. No Scale test was added.

## What changed

1. `src/Miller.Indexing/IncompatibleExtractException.cs` — additive `public int? ExitCode { get; }` plus a
   `(string message, int? exitCode)` constructor. Both existing constructors are byte-unchanged.
2. `src/Miller.Indexing/JulieExtractExceptions.cs` — `ExitCodeOf` became a switch expression that also reads
   `IncompatibleExtractException.ExitCode`. Signature and `JulieExtractException` behavior are unchanged.
3. `src/Miller.Indexing/JulieExtractRunner.cs` — the exit-3 throw in `Interpret` passes `exitCode: 3`.
4. `tests/Miller.Tests/Indexing/JulieExtractExceptionExitCodeTests.cs` (new) — 6 tests.

## Plan-mismatch note (reported, not redesigned)

The plan says "the rebind exit-3 refusal throw **sites**" in `JulieExtractRunner.cs`. There is exactly **one**
exit-3 throw site in that file (`Interpret` case 3, line 473), and it is shared by the two rebind
artifact-identity refusals (`fingerprint_mismatch`, `no_committed_revision`) and by the schema/contract/root
exit-3 codes. Splitting it by error code would need the runner to sniff the diagnostic string, and would make a
`schema_incompatible` refusal that came from a real subprocess exit report `null` — dishonest by the plan's own
stated rationale ("no subprocess exit is involved there" is why the gates stay null). So the single site carries
`3` for every exit-3 refusal it maps. Every construction site the plan names as "do not touch" (schema gate,
version gate) is in a different file and stays null. Flagging this for the lead rather than changing the design.

## Verification

**Invariant:** a julie-extract exit-3 refusal — including `rebind`'s `fingerprint_mismatch` /
`no_committed_revision` — reaches the W8 scan-failure journal carrying `exit_code: 3`; the read-path gates that
never ran a subprocess still report null.

- Red first: with the test file written and no implementation, the build failed with `CS1739` (no `exitCode`
  parameter) and `CS1061` (no `ExitCode` member) on my file.
- Green: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~JulieExtractExceptionExitCodeTests"`
  → **Passed! Failed: 0, Passed: 6, Skipped: 0, Total: 6** (52 ms).
- `dotnet build Miller.slnx -c Release` → **Build succeeded, 0 Warning(s), 0 Error(s)** (retried until the
  siblings' in-flight edits settled; `src/Miller.Indexing` alone also built 0/0 throughout).
- Worker ceiling `scripts/test.sh` (fast suite) → **Passed! Failed: 0, Passed: 6145, Skipped: 2, Total: 6147**.
  The 2 skips are the environment guards (`SemanticEmbeddingSessionTests.AbsentSidecarExecutable…`,
  `BlazorNamespaceCatalogTests.QualifiedNames_ExtendedLengthWorkspaceRoot…`) — this worktree has no `.tools/`.
- No Scale test was added; `ScaleTraitConventionTests` passes inside the fast suite above.

The worktree has no `.tools/` directory, so every build/test invocation set
`MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1` (the documented offline escape hatches).
This does not weaken the check: the fast suite spawns no subprocess and the new tests are pure.

### Shared-assembly interference from siblings

An intermediate Release build failed with 6 errors, all in `src/Miller.Indexing/RebindBootstrap.cs`
(`SourceScanWait`, `WaitOutSourceScan`, `Format`) — sibling task 3's in-flight edits, not my files. Likewise an
intermediate Debug run showed `RebindBootstrapTests.cs` errors that cleared once task 3 landed its
implementation. Final results are recorded below.

## Pre-existing issue found, NOT caused by this branch (for the lead)

`scripts/test.sh` reports `Passed!` but then **fails its own wall-clock tripwire**:

```
    fast suite wall time: 73s (local target <10s, ceiling 30s)
ERROR: fast suite took 73s (> 30s ceiling).
```

Attribution (TRX per-test durations, 1363s of test time across 6147 tests running in parallel):

| Test | Duration |
| --- | --- |
| `MetricSnapshotAggregatesTests.ReadConvergeMetrics_MarkerCountsAreExactAboveSearchLimit` | 44.49s |
| `MarkerSearchTests.FindMarkers_AppliesMarkerFilterBeforeLimit` | 43.28s |
| `SearchGoldenParityTests.SymbolRoute_CandidateShapesThatBypassTheArm_StillRenderTheGoldenLexicalBytes` | 14.82s |
| `CanaryGateReportTests.SuccessRate_SeparatedArmsWithEnoughUnitsPass` | 14.79s |

Both dominant tests were last touched by `4b3ff371` ("fix: harden marker and paging contracts"), long before
this branch, and `git diff --name-only` shows neither file is modified here. No task-1/2/3/4 test appears in the
20 slowest; my six new tests total **52 ms**. So the ceiling breach is pre-existing and outside task 4's scope —
raising it rather than silently absorbing or silently ignoring it. The CLAUDE.md rule the tripwire enforces
("keep the fast suite genuinely fast") is being violated by those two marker/metric fixture tests, which look
like Scale candidates.

## Files touched

- `src/Miller.Indexing/IncompatibleExtractException.cs`
- `src/Miller.Indexing/JulieExtractExceptions.cs`
- `src/Miller.Indexing/JulieExtractRunner.cs`
- `tests/Miller.Tests/Indexing/JulieExtractExceptionExitCodeTests.cs` (new)

Nothing else. `RebindBootstrap.cs`, `RebindBootstrapTests.cs`, `IndexerService.cs`,
`CrossWorkspaceRefreshService.cs`, and `IndexBootstrapService.cs` were read only, never edited.

## Acceptance criteria

- [x] `ExitCodeOf` returns 3 for a rebind-refusal `IncompatibleExtractException` and null for legacy sites.
- [x] No existing construction site changed behavior.
- [x] Worker-scope verification passes — Release build 0/0, fast suite 6145 passed / 0 failed. Handed to the
      lead per `parallel-lead-commit` (no `git add`, no `git commit`). One pre-existing, unrelated issue is
      flagged above: `scripts/test.sh`'s 30s wall-clock tripwire fails at 73s on tests this branch never touched.
