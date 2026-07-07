# Task 1 Report — Core evaluator (`Miller.Core/DeadCode`)

**Status:** DONE
**Branch:** `feat/dead-code-candidates` · **Repo root:** `/Users/murphy/source/miller` (verified before and after commit)
**Commit:** see final line / returned message.

## What was built

Pure-logic dead-code candidate evaluator (ZERO I/O) implementing the load-bearing public contract exactly as
specified in the brief. Tasks 2–3 consume these shapes verbatim.

Owned files (only these were created/committed):
- `src/Miller.Core/DeadCode/DeadCodeRows.cs` — `DeadCodeSymbolRow` (18 fields, order per contract, incl.
  `StartByte`/`EndByte` carried faithfully) and `LanguageCoverageRow`.
- `src/Miller.Core/DeadCode/DeadCodeCandidates.cs` — `DeadCodeCandidate`, `DeadCodeResult`, and the static
  `DeadCodeCandidates` evaluator: `CandidateKinds`, `SuppressionRuleIds`, `IsSyntaxInvokedName`,
  `ResolvedPercent`, `Evaluate`, `ApplyLiteralScan`.
- `tests/Miller.Tests/Core/DeadCodeCandidatesTests.cs` — 51 test cases (new `Core/` test dir + `Miller.Tests.Core`
  namespace).

## Miller-first orientation (Miller search/inspect down — read exemplars directly with Read tool)

Miller `search`/`inspect` fail closed on a stale sidecar (older-extractor permanent reader), so I read the named
exemplar files directly:
- `src/Miller.Core/Contracts/StructuralFactRecord.cs` — confirmed row-record style: file-scoped namespace,
  `public sealed record` with positional params, `IReadOnlyDictionary` for maps. Matched for `DeadCodeSymbolRow`.
- `src/Miller.Core/Contracts/SymbolDetail.cs` — confirmed positional-record + XML-doc-per-param convention and
  nullable reference fields (`string?`). Matched for the row records.
- `src/Miller.Core/Graph/BridgeGraphBuilder.cs` — confirmed evaluator-returns-result style: `public static`
  entry point, `ArgumentNullException.ThrowIfNull` guards, `IReadOnlyList`/`IReadOnlyDictionary` results,
  `StringComparer.Ordinal` dictionaries, file-scoped namespace, nullable enabled. Matched for `Evaluate`/result types.
- `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` — confirmed test style: `using Xunit;`,
  `public sealed class`, static factory helpers with defaulted params, `namespace Miller.Tests.<Area>`. Matched for
  the test file (`Row(...)`/`Coverage(...)` factories, `Miller.Tests.Core` namespace).
- `CLAUDE.md` "Project seam" + `src/Miller.Core/Miller.Core.csproj` — confirmed Core is ZERO-I/O (no SQLite, no
  System.IO). The new files use only `System`/`System.Collections.Generic` primitives — no I/O deps added.

No other symbol/signature was relied upon; all shapes come from the brief's contract, not invented.

## Public-contract fidelity (Tasks 2–3 depend on these)

- `DeadCodeSymbolRow` / `LanguageCoverageRow` field names + order: exact.
- `DeadCodeCandidate` (12 fields) / `DeadCodeResult` (4 fields): exact.
- `CandidateKinds` = {function, method, class, struct, interface, enum, delegate, property, constant} (Ordinal set).
- `SuppressionRuleIds` (table order, single-sourced from private const ids): public_api, visibility_unknown,
  test_symbol, entry_point, framework_bound, annotated, generated_path, low_evidence_language,
  string_literal_match.
- `IsSyntaxInvokedName(name, kind)` / `ResolvedPercent(int, int)` / `Evaluate(...)` / `ApplyLiteralScan(...)`
  signatures: exact.

## Decision-logic notes / decisions taken

- **Exclusion** (`kind ∉ CandidateKinds` OR `IsSyntaxInvokedName`) drops before `examined++`; alive-by-evidence
  drops silently after `examined++`.
- **First-match-wins**: `FirstSuppressionRule` returns the first matching rule id in table order; each suppressed
  symbol bumps exactly one count.
- **`low_evidence_language`** fires ONLY when the language is PRESENT in coverage with `IdentifierCount == 0`. A
  language ABSENT from coverage does NOT fire it — this is required for consistency with the documented
  evidence-label rule ("language absent from coverage → treat as 0 → `name`"), which would be unreachable if
  absent-language symbols were auto-suppressed. Stated here because the brief left the absent case implicit; this
  is the only interpretation that keeps both documented behaviors reachable. Present-with-0 (css/html) is the
  designed path and is covered by a test.
- **`public_api`** guards null/whitespace before the exported-visibility set lookup (OrdinalIgnoreCase set throws on
  a null probe); null visibility therefore correctly falls through to `visibility_unknown` (rule 2).
- **Two-phase**: `LiteralMatch == null` → provisional candidate in both `Candidates` and `NeedsLiteralScan`;
  `== false` → candidate, not in scan list; `== true` → suppressed under `string_literal_match`.
  `ApplyLiteralScan` removes matched candidates, bumps `string_literal_match`, empties `NeedsLiteralScan`, and
  preserves all nine suppression keys.
- **`ResolvedPercent`** single-sources the 10% label threshold and output rendering (one decimal,
  MidpointRounding.AwayFromZero, identifiers==0 → 0.0).

## Verification (each gate + the invariant it proves)

- **worker-red-green** — `dotnet test tests/Miller.Tests -c Release --filter "FullyQualifiedName~DeadCodeCandidates"`.
  - RED: initial run failed to compile (CS0234/CS0246 — the `Miller.Core.DeadCode` types did not exist).
    Proves the tests exercise the not-yet-built contract.
  - GREEN: `Passed! Failed: 0, Passed: 51, Total: 51` (35 ms). Proves the evaluator satisfies every asserted
    behavior (candidate found, four alive-by-evidence paths, all nine rules counted, first-match-wins,
    finalizer/indexer/operator/`op_`/`Finalize` exclusions, evidence-label 9.9/10.0 split + absent-coverage,
    NULL visibility, parent-only test + structural-fact closures, two-phase null→scan + ApplyLiteralScan,
    ResolvedPercent rounding).
- **worker-ceiling** — `scripts/test.sh` (fast suite). `Passed! Failed: 0, Passed: 2919, Total: 2919`, wall time
  15s (< 30s ceiling). Proves the change did not break the fast suite and stays within the time budget. The known-
  flaky `IndexerServiceScanTests.StartAsync_WhenEnabledLeaderAndSidecarBuildFails_StillMarksRegistryScanned`
  PASSED inside this run — no isolation re-run needed.
- **Build** — 0 warnings / 0 errors (warnings-are-errors; the Release build the test runs through is clean).
  Proves Core stays warning-free and zero-I/O (compiles under `Directory.Build.props` `TreatWarningsAsErrors`).

## Git hygiene

`git status --short` before commit showed ONLY the two owned trees untracked
(`src/Miller.Core/DeadCode/`, `tests/Miller.Tests/Core/`). Committed exactly the three owned files. No push, no
branch switch. This report lives under `.razorback/sdd/` and is intentionally NOT part of the code commit.

## Concerns / handoff notes for Tasks 2–3

- Row-field semantics the reader (Task 2) must honor: `IsTestSelfOrAncestor` and
  `HasStructuralFactSelfOrAncestor` are ANCESTOR-CLOSED closures the reader computes via the `parent_symbol_id`
  walk — Core trusts them. `HasAnnotation` is SELF-only. `NameMatchesOutside`/`ResolvedInbound`/
  `PendingResolvedInbound`/`CallsInbound` must already exclude S's own definition span (the "inside" test).
- `LanguageCoverageRow` for a zero-identifier language (css/html) MUST be emitted with `IdentifierCount == 0` for
  `low_evidence_language` to fire — do not omit zero-identifier languages from the coverage list (see decision note
  above).
- `ApplyLiteralScan` is the only sanctioned way to fold literal-scan results back in; the reader should scan only
  `NeedsLiteralScan` survivors and pass the matched `SymbolId` set.
