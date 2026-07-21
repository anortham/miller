### Task 7: Identifier shadow population

**Files:**
- Modify: `src/Miller.Server/Tools/SearchTool.cs` (post-finalize shadow hook on the symbol route)
- Modify: `src/Miller.Server/Telemetry/CanaryTelemetry.cs` (add `StampShadow(TelemetryScope, CanaryShadowFacts)` + `CanaryShadowFacts` record)
- Test: `tests/Miller.Tests/Server/CanaryShadowPopulationTests.cs` (new)

**Interfaces:**
- Consumes: Task 5's orchestration (query class, eligibility probe), the production semantic arm + `RrfFusion`, the served lexical page slice from the finalize seam, `CanaryAssignment.Bucket` with `IdentifierExperimentId`.
- Produces: shadow rows per contract §Shadow Population: for `query_class=identifier` calls (ops auto/text/symbol) with canary on and semantic shadow|on, bucket under the noninferiority experiment id; sampled in when `bucket < 10`. Serve-first: lexical output is fully finalized before shadow work runs; shadow executes the hybrid arm under the same per-request embed deadline, compares against the served ranking, records ONLY: `canary_arm=shadow`, `canary_experiment_id=semantic_identifier_noninferiority_v1`, standard version/class keys, `canary_bucket`, `canary_shadow_status`, and when status=ok: `canary_shadow_overlap_at_10`, `canary_shadow_top1_changed`, `canary_shadow_lexical_top1_rank` (1–50, 0 = absent from hybrid top 50), plus `canary_encoder_fingerprint`/`canary_storage_schema`/`canary_corpus_generation` (vectors opened). Backend/warmth/latency-bucket keys are NOT written (field table: "every eligible row" — shadow rows are not eligible). Any failure ⟹ `canary_shadow_status` timeout/error/skipped and no counters; never affects the served result or the row's `outcome`.

**Contract inputs:** Contract §Shadow Population steps 1–5 verbatim; the served comparison uses the hybrid top 50 for `lexical_top1_rank` and top 10 for overlap. Neither ranking is persisted.

**File ownership:** `src/Miller.Server/Tools/SearchTool.cs` (post-serve hook), `src/Miller.Server/Telemetry/CanaryTelemetry.cs` (StampShadow), `tests/Miller.Tests/Server/CanaryShadowPopulationTests.cs`

**Serialization required:** Yes

**Dependency reason:** Same files as Tasks 5/6; needs the served lexical list from Task 5's finalize seam.

**What to build:** The non-inferiority measurement for the highest-volume query class, which can never be canary-eligible. This is the last serving-path piece; after it, every field in the contract's Field Reference is writable by some real code path.

**Approach:** Shadow work runs synchronously after finalization, bounded by the embed deadline (the row's own `duration_ms` includes it — acceptable: shadow rows are excluded from the latency gate by construction). Unsampled identifier calls record the ordinary `arm=ineligible` row (Task 5 behavior, `eligibility=ineligible_query_class`); sampled calls upgrade to the shadow row (same eligibility value, arm=shadow, hybrid-experiment keys replaced by the noninferiority experiment id).

**Acceptance criteria:**
- [ ] Sampling honors `bucket < 10` under the noninferiority experiment id (pinned test vectors).
- [ ] Ok path records exactly the shadow key set above; overlap/top1/rank values verified against a fixture with known lexical and hybrid rankings.
- [ ] Timeout/error/skipped paths record status only; served output and `outcome` provably untouched (fault-injection tests on the fake arm).
- [ ] Worker-scope verification passes and the change is committed per commit mode.

