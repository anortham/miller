## Task 4: Make telemetry attribution and promotion gates causal

**Depends on:** Tasks 1 and 2 contracts.

**Owns:**

- `src/Miller.Server/Telemetry/CanaryGateReport.cs`
- `src/Miller.Server/Telemetry/CanaryExport.cs`
- `src/Miller.Server/Telemetry/CanaryLedgerReader.cs` when required by the export shape
- `src/Miller.Server/Telemetry/CanaryTelemetry.cs` when required by typed attribution
- `src/Miller.Server/Tools/ContentTool.cs`
- telemetry contract/runbook docs and focused tests

**Red tests:**

1. `gate_passes` is false or indeterminate when identifier shadow is underpowered or regresses.
2. Export never pools different exact Miller versions, encoders, revisions, schemas, quantization, dimensions, or fusion profiles.
3. Export does not select an arbitrary first identity.
4. Content read hashes the resolved served path and records the resolved workspace, including cross-workspace reads.
5. A served semantic-only content hit can receive follow-up attribution.
6. Typed vector fallbacks reach ledger/export unchanged.

**Implementation:**

- Include the identifier-shadow verdict in the overall promotion verdict.
- Key analysis units by exact version plus complete semantic identity; emit explicit null/unknown strata instead of mixing.
- Stamp the content-read scope after resolution, using the same canonical path hash as search telemetry.
- Keep privacy properties unchanged.
- Correct the runbook so shadow is non-serving and the pinned default encoder is named once and accurately.

**Worker verification:** focused canary gate/export/ledger/content telemetry tests.

