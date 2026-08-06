# Task 3 report — RebindEligibility pure decisions

**Status:** DONE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/rebind-p3-miller-wiring`
**Branch:** `rebind-p3-miller-wiring`
**HEAD:** `b0d96b75` at task start (verified), `4d15f108` at report time — the lead committed another task's work mid-run. My three files were never committed by me.
**Commit SHA:** none — parallel-lead-commit

## What I implemented

`src/Miller.Indexing/RebindEligibility.cs` holds every rebind go/no-go decision as I/O-free statics in the
`LeadershipEligibility` style. Six types, one file:

| Type | Role |
|---|---|
| `RebindDecision(bool Eligible, string Reason)` | The verdict record. `Allow`/`Refuse` internal factories. Every refusal names the condition that decided it. |
| `RebindPrefilterInputs` | Registry-level facts (design §6.1-5). `required` init properties. |
| `RebindPrefilter.Evaluate` | Stage one — cheap, provisional, runs BEFORE the backup copy. |
| `RebindSnapshotInputs` | Snapshot facts (design §6.6-8). `required` init properties. |
| `RebindSnapshotValidation.Evaluate` | Stage two — authoritative, runs against the copied `.rebuild`. |
| `RebindExtractorVersion` (internal) | The numeric-triple version equality both stages share. |

**Prefilter conditions, in evaluation order:** kill switch (`RebindDisabled`) → linked worktree →
`!TargetArtifactExists` → `!RootReplacementDetected` → registered main-checkout sibling →
sibling `symbols.db` exists → numeric-triple version equality with the pin → no standing scan-failure
record → `MILLER_FULL_REBUILD_INPLACE` unset.

**Snapshot conditions, in evaluation order:** schema/contract compatible (carries the gate's own detail into
the reason) → `hash_algorithm = blake3` → `ArtifactRootIdentity.Matches(RecordedRootPath, SourceRoot)` →
`HasCommittedRevision` → numeric-triple version equality re-check → level policy
(`IndexLevels.IsSymbolsLevel(level) && policy == Full` ⇒ refuse; everything else satisfies).

No I/O anywhere in the file. Environment variables and filesystem probes arrive as booleans; the caller
(Task 6) gathers every fact. No registry or DB types cross the boundary — only bools, strings, and
`IndexLevelPolicy`.

## Verification

| Field | Value |
|---|---|
| Scope label | worker-red-green (worker ceiling: fast suite) |
| Invariant proved | Each of the eight §6 conditions flips its stage's decision independently from an otherwise-eligible baseline; extractor versions compare as numeric triples, not raw strings; a metadata-only crash shell is refused at snapshot validation despite passing every `ServableFor`-style fact. |
| TDD red | `dotnet build tests/Miller.Tests/Miller.Tests.csproj` → 2 errors, `CS0246: RebindPrefilterInputs / RebindSnapshotInputs could not be found`. |
| Command (targeted) | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~RebindEligibilityTests"` |
| Result | **Passed — 33/33, 0 failed, 33 ms**. 2026-08-05 19:01:03 CDT |
| Command (ceiling) | `scripts/test.sh` (fast suite, `Category!=Scale`) |
| Result | **Passed — 6054 passed, 0 failed, 2 skipped**, Release build 0 warnings / 0 errors. 2026-08-05 19:03:30 CDT. `LeadershipEligibilityTests` green and untouched. |
| Build | `dotnet build src/Miller.Indexing/Miller.Indexing.csproj` → Build succeeded, 0 warnings. |

### Note on the fast-suite wall-clock tripwire

`scripts/test.sh` reported all tests green but tripped its own budget guard: `fast suite wall time: 137s
(ceiling 30s)`. **This is not caused by this task.** Evidence, from a TRX run of the same suite:

- `RebindEligibilityTests`: 33 tests, **0.003 s** total, all Passed.
- Suite test-time sum 1809.5 s across 6056 tests; the fifteen slowest are all pre-existing
  (`MetricSnapshotAggregatesTests.ReadConvergeMetrics_MarkerCountsAreExactAboveSearchLimit` 31.2 s,
  `CanaryGateReportTests.SuccessRate_SeparatedArmsWithEnoughUnitsPass` 18.4 s,
  `SearchGoldenParityTests` 17.2 s, `MarkerSearchTests` 17.0 s, …). None belong to any P3 task.
- A second run of the identical binary took 91 s wall against 137 s — a 1.5x swing with no code change,
  which is machine contention from the four parallel P3 workers building and testing at once.

Flagged for the lead: re-check the tripwire on a quiet machine after the batch merges. If it still fires,
the slow tests above need Scale traits, and that is out of this task's file ownership.

### Parallel-batch interference encountered

The shared `Miller.Tests` project did not compile for roughly 20 minutes while Task 1's worker was mid-write
on `WorkspaceRegistry.cs` / `WorkspaceRegistryRow.cs` (its tests referenced `WorkspaceLineage`,
`FindMainCheckoutByCommonDir`, and `GitCommonDir`/`GitIsLinked`/`GitDir`/`GitDirCreatedAtUtc` before the
production side existed). I did not touch those files. I proved my own tests green in the meantime with an
isolated scratchpad project referencing only `Miller.Indexing` plus my test file (33/33 passed, 29 ms), then
polled until the shared project compiled and re-ran everything there. Both results agree.

## Files changed

| File | Change |
|---|---|
| `src/Miller.Indexing/RebindEligibility.cs` | **Created.** 6 types, ~215 lines. |
| `tests/Miller.Tests/Indexing/RebindEligibilityTests.cs` | **Created.** 33 tests (13 facts, 20 theory cases). |
| `src/Miller.Indexing/LeadershipEligibility.cs:119` | **One word** — `private` → `internal` on `TryParseTriple`. The sanctioned narrow exception. Public behavior unchanged; its existing tests untouched and green. |

## Miller calls used

| Call | What it confirmed |
|---|---|
| `inspect(target='LeadershipEligibility', depth=full)` | The full pattern: `public sealed record LeadershipVerdict(bool Eligible, …, string Reason)` + `public static class` with an `Evaluate` returning it. Showed `TryParseTriple` is **private** at `LeadershipEligibility.cs:119`, `CompareVersions` is public but **throws** on an unparseable token (so it is unusable for a tolerant gate), and the `Semver` regex `(\d+)\.(\d+)\.(\d+)` first-match-wins normalization that makes `v2.27.0` and `julie-extract 2.27.0` equal. |
| `inspect(target='IndexLevels', depth=overview)` then `depth=full` | `ResolveForWorkspace(string? registryDbPath, string? workspaceId)` reads the environment and the registry, so it is **not** pure — confirming the input record must carry an already-resolved `IndexLevelPolicy`, not re-derive one. Also gave `IsSymbolsLevel(string?)` (ordinal compare against `"symbols"`, tolerant reader defaults everything else to `"full"`) and `UpgradeOwed(level, policy) = policy != SymbolsOnly && IsSymbolsLevel(level)` — the exact §6.8 satisfaction rule. |
| `inspect(target='IndexLevelPolicy', depth=full)` | The three members are `Progressive`, `Full`, `SymbolsOnly` (`IndexLevels.cs:8-21`). |
| `inspect(target='ArtifactRootIdentity', depth=overview)` | `Matches(string? recordedRootPath, string canonicalRoot)` — pure: strips the Windows verbatim prefix and compares with `ComparisonFor(isWindows, isMacOS)` (case-insensitive on Windows/macOS). Empty/null recorded path returns false. Safe to call from a pure evaluator. |
| `inspect(target='MillerExtractContract', depth=overview)` | `public const string PinnedJulieExtractVersion = "2.27.0"` and `public const string ExpectedHashAlgorithm = "blake3"`; the type is `internal`, so it is usable from `Miller.Indexing` but not from tests. |
| `inspect(target='JulieSchemaGate', depth=overview)` | `Verify(SqliteConnection)` **throws** `IncompatibleExtractException` rather than returning a verdict — so the pure input must be a caller-folded `bool` plus the gate's message, which is why `RebindSnapshotInputs` carries `SchemaCompatible` + `SchemaIncompatibilityDetail`. |
| `grep GitWorktreeLayout.cs` (fallback) | `IsLinkedWorktree` is a computed property at line 38. Miller's `inspect` on the record surfaced the type doc but not the member list, so I confirmed the member name directly before putting it in a `<see cref>`. That is the one place Miller could not prove the shape. |

## API-shape evidence summary

Every shape I relied on was proven by a Miller call above, with one exception noted in the table
(`GitWorktreeLayout.IsLinkedWorktree`, confirmed by a one-line grep after `inspect` returned the record's
doc but not its computed members).

## Judgment calls

- **`src/Miller.Indexing/LeadershipEligibility.cs:119` — widened `TryParseTriple` to `internal` rather than
  lifting it into a new shared type.** The brief sanctioned "extract to a shared internal helper"; a
  one-word visibility change *is* that helper, with a strictly smaller diff than moving the method (which
  would also have to move the `Semver` regex and `Compare`, touching four members instead of one). Public
  behavior, ordering, and messages are byte-identical. Rejected alternative: wrapping the public
  `CompareVersions` in a `try/catch (ArgumentException)` — control flow by exception for a routine
  "version is unreadable" case.
- **`src/Miller.Indexing/RebindEligibility.cs:105` — the kill switch is evaluated first, ahead of §6.1.**
  Design §6 does not order the kill switch (the brief lists it last). An explicit operator "off" should
  produce the clearest possible reason, and it must not be masked by an incidental refusal such as "not a
  linked worktree". No condition's outcome changes; only which reason a multiply-ineligible target reports.
- **`RebindPrefilterInputs`/`RebindSnapshotInputs` use `required` init properties, not positional records.**
  Ten same-typed booleans and strings in a positional constructor is a silent-wrong-answer trap for Task 6:
  transposing two `bool`s compiles and inverts a safety gate. `required` forces every fact to be named at
  every construction site, and `with` expressions still give the tests their one-condition-at-a-time table
  shape. Still plain records — no I/O types, no behavior.
- **An unreadable version on either side refuses (`RebindExtractorVersion.Reject`), it does not pass.**
  `LeadershipEligibility` deliberately stays *eligible* on an unparseable artifact version because it
  cannot prove a downgrade. Rebind is the opposite posture: it must prove an extractor match before
  copying an artifact, so "cannot prove" means "do not rebind — take the plain bootstrap scan", which is
  always correct, just slower.
- **`RebindDecision.Allow`/`Refuse` are `internal`.** Task 6 lives in `Miller.Server` and cannot call them,
  but the record's public primary constructor covers any decision it needs to synthesize. Widening later
  is non-breaking; I kept the surface minimal.
- **The two `Evaluate` methods reject a null `inputs` with `ArgumentNullException.ThrowIfNull`** rather
  than returning an ineligible decision. A null input record is a caller bug, not a rebind condition, and
  silently reporting "ineligible" would hide it.

## Self-review findings

- **Reason strings are assertable, not decorative.** Every ineligible test asserts on a substring of the
  reason, so a future edit that keeps the boolean but breaks the explanation fails the suite. Task 7 will
  surface these in provenance, so they are contract-ish.
- **Version rendering is invariant-culture.** First draft used a bare interpolated string for the numeric
  triple; changed to `string.Create(CultureInfo.InvariantCulture, …)` to match `LeadershipEligibility`
  and to stay safe under CA1305 (warnings are errors in this repo).
- **Level check reads `IsSymbolsLevel`, never a raw `== "full"`.** `ExtractIndexLevelReader` reports
  `"full"` for absent keys, absent tables, and read failures, so "not symbols" is the only sound spelling
  of "full-level". A raw equality would refuse a legitimate pre-levels artifact.
- **`hash_algorithm` compares ordinally against `MillerExtractContract.ExpectedHashAlgorithm`,** not a
  local `"blake3"` literal, so a future contract bump moves both together.
- **Root-mismatch path handles the null case explicitly.** `ArtifactRootIdentity.Matches` returns false for
  a null recorded root, which would otherwise render as `records root path '', not …`; the reason now says
  "records no root path".
- Coverage check against the acceptance criteria: all eight §6 conditions have an independent flip test
  (10 for the prefilter, 6 for snapshot validation); the crash shell is `Snapshot_CrashShellWithNoCommitted
  Revision_IsIneligible`; the string-equality trap is covered five ways
  (`v2.27.0` vs `2.27.0`, `2.27.0` vs `julie-extract 2.27.0`, `2.27.0+build.9` vs `v2.27.0`) — all of which
  a raw `string.Equals` would refuse.

## Concerns for the lead

1. **The `scripts/test.sh` 30 s tripwire fires on this branch** (137 s, then 91 s on a re-run). Not from
   this task — my 33 tests total 3 ms — and the fifteen slowest tests are all pre-existing. Most likely
   four-worker machine contention plus genuinely slow pre-existing tests. Worth one quiet-machine run after
   the batch merges; if it persists, those tests need Scale traits, which is outside every P3 task's file
   ownership.
2. **Task 6 must pass `MILLER_WORKTREE_REBIND` as "is it `off`", not "is it set".** The input is named
   `RebindDisabled` to make that explicit; rebind is default-on and only the literal `off` disables it,
   unlike `MILLER_FULL_REBUILD_INPLACE`, which is a set/unset hatch (`InPlaceRebuildEnabled`).
3. **Task 6 must resolve the level policy before calling stage two.** `IndexLevels.ResolveForWorkspace`
   reads the environment and the registry, so it cannot run inside the pure evaluator; the input record
   takes an already-resolved `IndexLevelPolicy`.
4. **`ScanFailureRecorded` is deliberately "any record"** per §7.4, not "the backoff timer has not
   elapsed". If Task 6 folds the journal to "is a retry currently throttled", the conservative rule is lost.
