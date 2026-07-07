# Task 3 Report — CLI surface + capabilities + Scale test

**Status:** DONE
**Branch:** `feat/dead-code-candidates`
**Commit mode:** serial-worker-commit (owned files only; not pushed)

## What was built

Wired the Task 2 `DeadCodeCandidateReader` to a new `miller references candidates` CLI verb (compact +
`--json`), advertised the surface in `capabilities`, and added a real-binary Scale test. TDD: failing tests
first (7 red), then implementation (green).

### Files changed (ONLY owned files)
- `src/Miller.Server/Cli/CliDispatch.cs` — route `references candidates` inside `case "references":` on
  `rest[0]` (else the existing `ArtifactExport(... ReferenceExportReader.WriteJsonLines)` export path is
  unchanged); `ReferencesCandidates` handler; `RenderCandidatesCompact` / `RenderCandidatesJson`
  (`Utf8JsonWriter` manual snake_case, mirroring `ReferenceExportReader`); help text updated.
- `src/Miller.Server/Cli/CliCapabilities.cs` — `optional_features.references_candidates` (JSON + compact),
  `references candidates --json` in `json_commands`, `references_candidates` entry in `json_contracts`.
- `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` — 8 new fast tests (compact, JSON field-complete,
  `resolved_pct==10.0`, `--limit`, v3-schema exit 3, v4-missing-table exit 3, export-path-unchanged control,
  capabilities).
- `tests/Miller.Tests/Server/Cli/DeadCodeCandidatesScaleTests.cs` — NEW, `[Trait("Category","Scale")]`,
  binary via `ScaleTestSupport.RequireJulieServer()`.

### Routing
`--limit` (default 50) bounds ONLY the candidate list (sorted by `path` then `start_line`, stable take N).
`examined`, `suppressions`, `literal_scan`, `language_coverage` are always full totals. The reader's
`IncompatibleExtractException` (schema gate OR a missing resolution table) propagates to `Run`'s catch →
`references failed: …` + exit 3.

---

## VERBATIM artifacts for Task 4 (copy these exactly)

### 1. Exact JSON field names emitted (`references candidates --json`)

Top-level keys, in emit order:
```
schema_version
candidates          (array)
suppressions        (object)
literal_scan        (object)
language_coverage   (array)
examined
artifact            (object)
```

`candidates[]` object fields (in emit order):
```
symbol_id
name
kind
language
path
start_line
visibility            (JSON string, or JSON null when absent)
evidence_label        ("name" | "name+resolver")
evidence              (object)
```
`candidates[].evidence` object fields (in emit order):
```
name_matches
resolved_inbound
pending_resolved_inbound
calls_inbound
```

`suppressions` object — all nine keys ALWAYS present, in `DeadCodeCandidates.SuppressionRuleIds` order:
```
public_api
visibility_unknown
test_symbol
entry_point
framework_bound
annotated
generated_path
low_evidence_language
string_literal_match
```

`literal_scan` object fields:
```
files_scanned
files_skipped_stale
```

`language_coverage[]` object fields:
```
language
identifiers
resolved_pct        (0–100 number, one decimal; 1-of-10 → 10.0, NOT 0.1)
```

`artifact` object fields (in emit order):
```
artifact_id                    (JSON string, or JSON null when absent)
revision                       (JSON number, or JSON null when absent)
reference_resolution_status    (JSON string; falls back to "unknown" in the reader)
reference_resolution_version   (JSON string, or JSON null when absent)
```

`schema_version` value = `1`.

### 2. Exact compact resolver header string (VERBATIM, incl. trailing period)

Full header line format:
```
candidates: <candidateCount> of <examined> symbols examined · resolver: <status> — candidates are facts to check, not deletions to make.
```
The clause after `· ` is verbatim (`<status>` = `report.Artifact.ReferenceResolutionStatus`, e.g. `partial`):
```
resolver: <status> — candidates are facts to check, not deletions to make.
```
(Separator between the two clauses is a middle-dot `·`; the dash before "candidates are facts" is an
em-dash `—`.)

Compact candidate line format (one per shown candidate):
```
<name> <kind> <language> <path>:<start_line> <visibility> evidence=<label> [name_matches=0 resolved_in=0 pending_in=0 calls_in=0]
```
(Note the compact evidence keys `resolved_in` / `pending_in` / `calls_in` differ from the JSON
`resolved_inbound` / `pending_resolved_inbound` / `calls_inbound`.)

Compact footer lines:
```
showing top <limit> of <candidateCount> by path      (only when candidateCount > shown count)
suppressed: public_api=0 visibility_unknown=0 test_symbol=0 entry_point=0 framework_bound=0 annotated=0 generated_path=0 low_evidence_language=0 string_literal_match=0
literal_scan: files_scanned=0 files_skipped_stale=0
coverage: <lang>: <pct>% resolved; <lang>: <pct>% — name-evidence only
```
Coverage per-language: `<pct>% resolved` when `resolved_pct >= 10.0`, else `<pct>% — name-evidence only`.

### 3. Exact capabilities keys/strings added

- `optional_features.references_candidates` — JSON boolean `true`.
- Compact optional-feature line (next to `reference_aware_context: enabled`):
  ```
  references_candidates: enabled
  ```
- `json_commands` new entry (exact string):
  ```
  references candidates --json
  ```
- `json_contracts` new entry tuple:
  ```
  ("references_candidates", "references candidates --json", 1, "docs/contracts/references-candidates-v1.md")
  ```
  JSON object shape: `{ "name":"references_candidates", "command":"references candidates --json",
  "schema_version":1, "doc":"docs/contracts/references-candidates-v1.md" }`.
  Compact rendering:
  ```
  - references_candidates v1: `references candidates --json` (docs/contracts/references-candidates-v1.md)
  ```

> Task 4 creates `docs/contracts/references-candidates-v1.md`; naming it now in `json_contracts` is correct
> per the brief. Task 4 also amends the stale Eros-ownership sentences in `references-export-v1.md` and
> `cli-eros-v1.md`.

---

## Verification

- **worker-red-green** (`dotnet test … --filter FullyQualifiedName~CliDispatchTests`): RED first (7 new
  tests failed; the export-path control passed), then GREEN — **132 passed, 0 failed, Skipped: 0**.
- **Scale** (`scripts/test.sh scale`): full scale suite **47 passed, Skipped: 0**; the new
  `DeadCodeCandidatesScaleTests` runs green in isolation (**1 passed, Skipped: 0**). It scans a tiny temp
  workspace (public class with two private methods: `InvokeSecretly`, referenced only by the reflection
  string `"InvokeSecretly"`, and `DeadPrivateHelper`, referenced by nothing) with the real 2.9.0 binary,
  runs the CLI end-to-end, and asserts: `DeadPrivateHelper` surfaces as a candidate, `InvokeSecretly` is
  suppressed under `string_literal_match` (two-phase literal scan over real `source_regions`), and
  per-language coverage renders (compact + JSON). **CLI `references candidates` wall-clock: 23 ms** (7
  symbols extracted). The real string-literal suppression path works against the live binary.
- **worker-ceiling** (`scripts/test.sh`, fast suite): **2951 passed, 0 failed, Skipped: 0**, 12–16s.
  (First run had ONE transient failure in `IndexerServiceLeadershipTests.StartAsync_ArtifactOlderThanOwn_…`
  — a leadership/scan timing test in the same known-flaky family, unrelated to this CLI-only change; it took
  5s under parallel load, passed in isolation (73 ms) and on a clean full re-run.)
- **Build** (`dotnet build Miller.slnx -c Release`): **0 Warnings, 0 Errors** (warnings-as-errors).

### Gate invariants
- red-green: the candidates handler + capabilities entries did not exist → tests fail; after implementation
  they pass, proving the tests exercise the new behavior.
- Scale: `Skipped: 0` for the new test confirms the pinned 2.9.0 binary was present and the end-to-end path
  ran with the real extract.
- ceiling: the fast suite stays fast and pure; no new subprocess in the fast path.
- build: zero warnings/errors under `TreatWarningsAsErrors`.

## Concerns / notes
- **Design-doc §Output vs Global Constraints (resolved):** design §Output (line 105–106) prints the header
  clause WITHOUT a trailing period, but the implementation-plan Global Constraints (line 20) and design line
  101 require the trailing period. Followed Global Constraints (with period) per the brief; the design
  §Output example is the only spot missing it. Not a redesign — a doc example inconsistency.
- **String-literal suppression depends on real `source_regions`:** the Scale test proves julie-extract 2.9.0
  emits C# `string_literal` regions and the two-phase scan suppresses the reflection-named member. If a
  future language/extractor stops emitting these, that language's reflection-only symbols would surface as
  candidates — the honest signal Task 5's dogfood gate exists to catch.
- **Perf:** the reader runs 4 indexed subqueries per candidate-kind symbol. On the tiny Scale fixture the CLI
  is 23 ms; Task 5 must judge this on the real ~38k-symbol repo.
