# Task 2 Report — `miller telemetry canary` (frozen export + local gate calculator)

Status: **DONE**. Commit SHA: none — parallel-lead-commit (no `git add`/`commit` run).

## What I implemented

Five surfaces, matching the approved shape (pure math in Core; reader/export/report in Server; CLI wiring
in the existing `Telemetry` method). No new abstraction layers, no new MCP tool.

- **`src/Miller.Core/Telemetry/CanaryGateMath.cs`** (new, pure, zero I/O). The frozen estimators:
  - `WelchInterval(a, b) → (Lower, Upper, Effect)` — two-sample two-sided 95% t-interval,
    `effect = mean(a) − mean(b)`, Welch–Satterthwaite df. `a` = treatment per-unit rates, `b` = control.
  - `OneSampleInterval(xs) → (Lower, Upper, Mean)` — 95% t-interval for the shadow margins.
  - `NearestRankP95(ascending) → long` — value at 1-based `ceil(0.95×n)`, no interpolation.
  - `BucketedP95(bucketCounts, calls) → string` — first ladder rung whose cumulative reaches `ceil(0.95×calls)`.
  - `StudentTCritical(twoTailedAlpha, df)` — Hill (1970) Algorithm 396 seeded by Acklam's inverse-normal.
  - `LatencyLadder` — the frozen ascending bucket ladder (shared by export ordering + bucketed p95).
- **`src/Miller.Server/Telemetry/CanaryLedgerReader.cs`** (new). Read-only ledger access mirroring
  `TelemetryExportReader.OpenReadOnly`: `ReadCanaryRows` (typed `CanaryRow` with parsed `canary_*` metadata incl.
  the three hash arrays), `ReadFollowUps` (`inspect`/`content read`, `ok`, non-null `target_hash`), and
  `AttributedRowIds` — the frozen §Matching-rule join (latest-C, ≤600 s, workspace match, membership in any of the
  three arrays, binary per row).
- **`src/Miller.Server/Telemetry/CanaryExport.cs`** (new). Builds the frozen §Aggregate-Export envelope with
  `Utf8JsonWriter` (compact, deterministic). `unit_id` = first 12 hex of
  `SHA256(experiment_id|1|workspace_id|utc_date|query_class)`; <5-call units suppressed with
  `suppressed_unit_count`; count maps omit zero keys; `units`/`shadow_units` ordered by
  `(utc_date, query_class, unit_id)`; `total_latency_bucket_counts` from raw `duration_ms`; no hashes / workspace
  ids / raw ms leave the machine. `generated_at_utc` is an injected instant so an unchanged window re-exports
  byte-identically.
- **`src/Miller.Server/Telemetry/CanaryGateReport.cs`** (new). Local-authoritative gate per exact `miller_version`
  cohort: success-rate (per-unit rates, ≥5 calls/unit, ≥30 units/arm else underpowered, Welch lower > 0),
  warm-latency (warm-treatment vs all-control nearest-rank p95 ≤ 1.20×, ≥100 rows each else indeterminate),
  identifier-shadow (per-unit `top1_changed` 95% upper ≤ 0.05 AND mean `overlap_at_10` 95% lower ≥ 8.0, ≥30 units).
  Renders a per-clause human verdict (value, threshold, pass/fail/underpowered/indeterminate) or JSON.
- **`src/Miller.Server/Cli/CliDispatch.cs`** — `Telemetry` method relaxed to branch `export` vs `canary`; new
  `Canary` helper adds `telemetry canary [--json] [--from --to]` (default window last 30 days) and
  `telemetry canary --gate [--json]`. Gate/export exit 0 when computed; usage/bad-date exit 2. Help line updated.
  No other part of the file touched.

## Verification

- **worker-red-green** — invariant: the math estimators, the six attribution conformance cases, the export
  envelope, and the gate clauses compute the frozen contract values.
  `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~CanaryGateMathTests|FullyQualifiedName~CanaryExportTests|FullyQualifiedName~CanaryGateReportTests"`
  → **Passed 42 / Failed 0** (2026-07-21).
- **worker-ceiling** — invariant: the whole fast suite still passes and stays pure. `scripts/test.sh`
  → **Passed 4284, Skipped 2, Failed 0**, wall 19s (<30s ceiling) (2026-07-21).
- **Diagnostic** — `dotnet build Miller.slnx -c Release` → **0 Warning(s) / 0 Error(s)**.
- **CLI smoke** — `telemetry canary --json`, `--gate`, `--gate --json` render on an empty ledger; exit codes:
  gate 0, export 0, bad-op 2, bad-date 2.
- **`telemetry export` unchanged** — existing `CliDispatchTests.Telemetry_Export_WritesJsonLinesAndSupportsExactWorkspaceFilter` still passes.

## Tests (through the interfaces callers use)

- `tests/Miller.Tests/Core/CanaryGateMathTests.cs` — Student-t against table values within Hill's 1e-3 tolerance
  (df=4 2.77645, df=10 2.22814, df=30 2.04227, df→∞ 1.95996), hand-computed Welch interval + df, hand-computed
  one-sample interval, nearest-rank p95, bucketed p95.
- `tests/Miller.Tests/Telemetry/CanaryExportTests.cs` — envelope top-level + unit field set/order, `unit_id`
  digest, total-latency sum + zero-key omission, suppression, privacy (no hashes/workspace/raw-ms — scans parsed
  JSON numbers), byte-identical re-export, `(utc_date, query_class, unit_id)` ordering, window filtering, shadow
  unit shape, `attributed_success_calls`. Contains the shared `CanarySeeder` (raw INSERTs of contract-faithful
  rows into a `TelemetryLedger`-created schema, real `canary_*` keys, real SHA-256 digests).
- `tests/Miller.Tests/Telemetry/CanaryGateReportTests.cs` — the six §Matching-rule conformance cases
  (bare / qualified / path-via-content-read / top-level / deeper-spelling-not-attributed / double-count-once),
  plus qualified-omitted-loses and window-exceeded; success underpowered + pass, warm-latency indeterminate + pass
  + regression-fail, shadow underpowered + pass, cohort split by exact version, exclusion of null-version and
  contract-version≠1 rows, JSON + human render.

## Miller calls used (API-shape evidence)

- `context(query="canary telemetry stamping ledger export")` — surfaced `CanaryTelemetry`, `TelemetryLedger`,
  `TelemetryScope`, and the existing `CanaryTelemetryTests` seeding pattern.
- `inspect(CanaryTelemetry, depth=full)` — confirmed the exact `canary_*` key names, enum classes
  (`CanaryArm`/`CanaryEligibility`/`CanaryLatencyBucket`/`CanaryAssignment`), `ShortFingerprint`, and the SHA-256
  `Digest` derivation the export's `unit_id` and the tests reuse.
- `inspect(TelemetryExportReader, depth=overview)` + `Read` — the `OpenReadOnly(Mode=ReadOnly, Pooling=false)` and
  `TableExists` pattern `CanaryLedgerReader` mirrors, and the `Utf8JsonWriter`/`UnsafeRelaxedJsonEscaping` render
  pattern.
- `inspect(TelemetryLedger, depth=full)` — the `STRICT tool_telemetry` DDL (columns, `miller_version`, outcome
  CHECK), `Open` (creates schema + WAL), proving the seeder must raw-INSERT to control `ts`/`miller_version`.
- `Read TelemetryOnboardingReader` — the `ReadEvents`/`MetadataString` row-iteration + JSON-parse pattern and the
  `DateTimeOffset.TryParse(AssumeUniversal|AdjustToUniversal)` timestamp parse the attribution join reuses.
- `inspect(CliOptions, depth=full)` + `Read CliDispatch.Telemetry` + help block — the boolean-flag parse contract,
  the original `export` guard, and the help-line location.
- `inspect(WorkspaceContext)` — `ctx.TelemetryDbPath` is the machine-global ledger the CLI passes in.

## Judgment calls

- `CanaryGateMath.cs:8` — duplicated `LatencyLadder` in Core rather than referencing `CanaryLatencyBucket` (Server),
  because Core must stay I/O/Server-free; the labels are frozen contract, so duplication is safe.
- `CanaryExport.cs` (`suppressed_unit_count`) — counts suppressed **experiment units AND shadow units** in the one
  frozen counter, because the envelope has a single field and the contract applies the same 5-call floor to both;
  silently hiding shadow suppression would be worse than pooling it.
- `CanaryExport.cs` (`semantic_contribution_calls`) — interpreted as the count of unit calls whose
  `canary_semantic_contribution_count > 0` (the `_calls` naming), not a sum of per-row contribution counts.
- `CanaryExport.cs` (count-map key order) — enum-declaration order for enum maps (`CanaryFallbackReason.All` etc.)
  and ladder order for latency maps, reproducing the frozen example's ordering and guaranteeing byte-identical
  re-export.
- `CanaryExport.cs` (`generated_at_utc`) — injected `DateTimeOffset` (CLI passes `UtcNow`) so the byte-identical
  re-export guarantee is honest: identical window + instant ⇒ identical bytes.
- `CanaryExport` unit sets — only hybrid-experiment `control`/`treatment` units go in `units`, identifier-experiment
  `shadow` rows go in `shadow_units`, and `ineligible` rows appear in neither (the frozen envelope has no field for
  them; adding one would be a v2).
- `CanaryGateReport.cs` — cohort = one exact `miller_version` string (never a string `≥`); `GatePasses` = success
  pass AND warm-latency pass; the shadow clause is reported separately and never merged into the gate verdict.
- Tests seed via raw INSERT (schema created by `TelemetryLedger.Open`) because `Record` cannot set `ts` or
  `miller_version`, both of which the window/cohort/attribution logic depends on.

## Self-review / notes

- Every acceptance criterion is covered by a test; verification asserts real computed values (hand-computed Welch
  and one-sample intervals, table t-values, real SHA-256 digests), not mocks.
- Attribution is exposed as `CanaryLedgerReader.AttributedRowIds` and tested directly against the six frozen
  conformance cases — the gate and the export both consume it, so the export's `attributed_success_calls` rides the
  same tested path.
- `Miller.Core.Telemetry` is a new namespace/dir under `Miller.Core`; `CanaryGateMath` has no I/O, SQLite, or env
  reads.
- The shared `CanarySeeder` lives inside `CanaryExportTests.cs` (a second internal class) rather than a fourth file,
  to respect the exact file-ownership list.
- Unrelated present change: `.razorback/sdd/progress.md` shows modified in `git status`; I did not touch it (left
  alone per worktree discipline). Task-brief files were already modified before I started.
