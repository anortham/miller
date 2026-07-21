### Task 2: `miller telemetry canary` — frozen export + local gate calculator

**Files:**
- Create: `src/Miller.Core/Telemetry/CanaryGateMath.cs`
- Create: `src/Miller.Server/Telemetry/CanaryLedgerReader.cs`
- Create: `src/Miller.Server/Telemetry/CanaryExport.cs`
- Create: `src/Miller.Server/Telemetry/CanaryGateReport.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` (`Telemetry` :1091 — relax the `!= "export"` guard :1098, add `canary` op; help line near :2733)
- Test: `tests/Miller.Tests/Core/CanaryGateMathTests.cs`, `tests/Miller.Tests/Telemetry/CanaryExportTests.cs`, `tests/Miller.Tests/Telemetry/CanaryGateReportTests.cs`

**Interfaces:**
- Consumes: `CanaryTelemetry` constants/enums (`CanaryArm`, `CanaryEligibility`, `CanaryLatencyBucket`, `CanaryAssignment.Bucket`), read-only DB open pattern from `TelemetryExportReader.OpenReadOnly` (:55), row-iteration pattern from `TelemetryOnboardingReader.ReadEvents` (:159) + `MetadataString` (:331), `ctx.TelemetryDbPath`.
- Produces: CLI verbs `miller telemetry canary --json [--from YYYY-MM-DD --to YYYY-MM-DD]` (frozen export envelope, default window last 30 days) and `miller telemetry canary --gate [--json]` (local gate report). `CanaryGateMath` (pure, `Miller.Core`): `WelchInterval(IReadOnlyList<double> a, IReadOnlyList<double> b) : (double Lower, double Upper, double Effect)` (95% two-sided, Welch–Satterthwaite df); `NearestRankP95(IReadOnlyList<long> ascending) : long` (index `ceil(0.95×n)`, 1-based, no interpolation); `BucketedP95(IReadOnlyDictionary<string,int> bucketCounts, int calls) : string`.

**Contract inputs:** Contract §Aggregate Export (envelope shape verbatim, `unit_id` = first 12 hex of the assignment digest, <5-call units suppressed with `suppressed_unit_count`, count maps omit zeros, `units` ordered by `(utc_date, query_class, unit_id)` for byte-identical re-export, `total_latency_bucket_counts` from raw `duration_ms` per the `latency_bucket` ladder, **no hashes / workspace ids / raw ms in the export**); §Frozen analysis parameters (per-unit rates, min 5 calls/unit, min 30 units/arm else "underpowered — not a pass", Welch 95% lower bound > 0, warm-treatment vs all-control nearest-rank p95 ≤ 1.20×, 100-row minimums else indeterminate, shadow margins: per-unit `top1_changed` 95% upper ≤ 0.05 AND mean `overlap_at_10` 95% lower ≥ 8.0, min 30 shadow units); §Matching rule (attribution: `F.tool='inspect'` or `content`+`op='read'`, `outcome='ok'`, hash in any of the three arrays, `0 < F.ts−C.ts ≤ 600s`, latest-C rule, one follow-up max per row); cohort = exact `miller_version` set (never string ≥) — `--gate` groups by exact version strings present and reports per-set. Rows lacking `miller_version` or with `canary_contract_version != 1` are excluded.

**File ownership:** `src/Miller.Server/Cli/CliDispatch.cs` (Telemetry method + help), `src/Miller.Core/Telemetry/CanaryGateMath.cs`, `src/Miller.Server/Telemetry/CanaryLedgerReader.cs`, `src/Miller.Server/Telemetry/CanaryExport.cs`, `src/Miller.Server/Telemetry/CanaryGateReport.cs`, the three test files.

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The only sanctioned off-box surface (export) and the local-authoritative gate. Both read `tool_telemetry` rows and parse `canary_*` metadata; neither needs the serving path to exist — tests seed a temp telemetry DB via `TelemetryLedger` with hand-built rows (contract-faithful metadata: real key names, real enum values, real digests via the same SHA-256 derivation).

**Approach:** `CanaryLedgerReader` yields typed rows (columns + parsed canary metadata incl. the three hash arrays); `CanaryExport` aggregates units; `CanaryGateReport` computes attribution then per-unit rates then the three clauses, and renders a human summary (per clause: value, threshold, pass/fail/underpowered/indeterminate) plus `--json`. Welch t-quantile: implement the inverse-t via a small deterministic approximation (e.g. Hill's algorithm) in `CanaryGateMath` with unit tests against known values (df=10 t=2.228, df=30 t=2.042, df→∞ 1.960 within 1e-3). Gate exit code: 0 = computed (regardless of pass/fail; the verdict is in the output), nonzero only for I/O/usage errors.

**Acceptance criteria:**
- [ ] Export envelope matches the contract example shape byte-for-byte on ordering/field names; suppression and truncation-free counters verified; re-export of an unchanged window is byte-identical.
- [ ] Gate report reproduces the contract's six conformance attribution cases (bare/qualified/path/top-level/deeper-spelling/double-count) from seeded rows.
- [ ] Underpowered (<30 units) and indeterminate (<100 latency rows) paths report as such and never as a pass; Welch interval + nearest-rank p95 unit-tested against hand-computed values.
- [ ] `miller telemetry export` behavior unchanged.
- [ ] Worker-scope verification passes and the change is handed to the lead per commit mode.

