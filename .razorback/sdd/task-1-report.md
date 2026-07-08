# Task 1 Report: Dashboard survives incompatible artifacts

**Status:** DONE
**implementation commit SHA:** 79ba1d5

## Summary

`ReadSnapshot` panel readers degrade on schema-incompatible workspace artifacts instead of letting `IncompatibleExtractException` escape as a Kestrel 500. Health panel state is `"unavailable"` with the schema/rebuild guidance (`workspace full`) in `Error`.

## Miller orientation

1. **`context(query=…IncompatibleExtractException degrade…)`** — located panel readers, `IncompatibleExtractException`, and existing `DashboardRegistryReadTests` fixtures.
2. **`inspect(target=WorkspaceHealthReader.Read)`** — confirmed `JulieSchemaGate.Verify` throws `IncompatibleExtractException` on schema mismatch.

## Root cause

Panel catch filters listed `KeyNotFoundException | SqliteException | IOException | InvalidOperationException | UnauthorizedAccessException` only. Schema-3 artifacts throw `IncompatibleExtractException` from `JulieSchemaGate.Verify` via `WorkspaceHealthReader.Read`, which escaped the filters → 500.

A second trap: catching that exception only inside `ReadExtractionHealthOrUnavailable` and continuing through `WorkspaceHealthFacts.Create` maps unavailable extraction sections to `usable_with_warnings`, so the health panel never got `Error`/rebuild text.

## Approach (final)

1. Add `IncompatibleExtractException` to the exception filters of:
   - `ReadLocalMetricsPanel`
   - `ReadPatternInventoryPanel`
   - `ReadWorkspaceHealthPanel`
   - `ReadWorkspaceOnboardingPanel`
   - `ReadExtractionHealthOrUnavailable`
2. `ReadWorkspaceHealthPanel` calls `WorkspaceHealthReader.Read` **directly** (not via `OrUnavailable`) so the exception reaches the panel catch and returns `"unavailable"` + `Error: ex.Message` (rebuild guidance).
3. `ReadExtractionHealthOrUnavailable` still lists `IncompatibleExtractException` (plan requirement) and prefers `ex.Message` for that type over the caller hint — used by pattern inventory / other callers that go through the helper.
4. No blanket `catch (Exception)`.

## Test

`ReadSnapshot_IncompatibleSchemaArtifactReturnsHealthUnavailableNotCrash` seeds temp registry + schema-3 `symbols.db` via `JulieDbFixture.Create(schemaVersion: 3, contractValue: "3", …)` and sets `binary_version=2.8.1`. Asserts:

- `ReadSnapshot` returns (no throw)
- health panel `State == "unavailable"`
- `Error` contains `"workspace full"` and `"3"`

## Files changed

| File | Change |
|------|--------|
| `src/Miller.Dashboard/DashboardData.cs` | Five catch sites include `IncompatibleExtractException`; health panel uses direct `WorkspaceHealthReader.Read` |
| `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs` | New schema-3 `ReadSnapshot` degrade test |

## Judgment calls

| Decision | Rationale |
|----------|-----------|
| Health panel bypasses `OrUnavailable` | Helper degrade → `WorkspaceHealthFacts` → `usable_with_warnings` would hide rebuild guidance from `Error` |
| Keep `IncompatibleExtractException` on `OrUnavailable` filter | Matches plan acceptance; pattern path still degrades safely; prefers gate message over caller hint |
| Prefer `ex.Message` for `IncompatibleExtractException` in helper | Caller hint is often a stale freshness string, not rebuild guidance |

## Verification

| Scope | Command | Result |
|-------|---------|--------|
| worker-red-green | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~DashboardRegistryReadTests"` | **Passed: 31, Failed: 0** |

## Acceptance criteria

- [x] New test: `ReadSnapshot` over schema-3 artifact returns snapshot (no throw); health `Error` contains schema/rebuild message
- [x] All four panel readers and `ReadExtractionHealthOrUnavailable` list `IncompatibleExtractException`
- [x] Existing `DashboardRegistryReadTests` still pass (31/31)
- [x] Worker-scope verification passed; handed to lead (no commit per `parallel-lead-commit`)

## Concerns

- Health panel intentionally does not use `ReadExtractionHealthOrUnavailable` for the schema-gated read; other IO failures still degrade via the panel catch. Pattern inventory still uses the helper (and its panel catch as backup).
- Index facts reader (`DashboardIndexFactsReader`) still does not catch `IncompatibleExtractException` — out of Task 1 ownership (Task 5 / ReadIndex). Schema-3 fixtures that only lack gate verification on the facts path may still show non-unreadable facts while health is unavailable; that is acceptable for this task’s AccessIQ-500 fix.
