# julie-extract 2.12.0 test-role evidence dogfood

Date: 2026-07-10
Implementation/test commits: `5290c85660beb5cd5453c73f2d0ff8bc7d33dda0`,
`d535881031b9b09b0dfeb7375d78db6b2da05a8c`

## Boundary

This verifies Miller's deterministic consumption of positive `julie-extract` test-role facts. It does not
claim runnable-test completeness. False flags and zero counts mean no positive candidate was emitted in the
observed artifact; absence remains unknown. Miller owns extraction consumption, local graph traversal, export,
and candidate presentation. Eros owns runner inventory, freshness policy, scheduling, execution results, and
verdicts.

## Binary and contract

- Command: `.tools/julie-extract --version`
- Result: `julie-extract 2.12.0`
- Host: macOS arm64
- Restored binary SHA-256: `71b4867b5fe43c372b5add8b4d84eea9453e7704887d32f2ddc7c26aeebbb438`
- Miller pin/version/build guards agree on `2.12.0`; extract SQLite schema remains 4, extract contract 3, and
  report schema 3.

## Live fixture evidence

The Scale fixture scans three files using the released binary:

- Razor: `[Fact] public void RazorCase()` plus an ordinary method named `Fact`.
- Vue regular `<script>`: `suite`, `afterAll`, and `it`, plus an ordinary member call named `it`.
- Vue `<script setup lang="ts">`: `describe`, `beforeEach`, and `test`, plus a function named
  `testNamedButOrdinary` and an ordinary member call named `test`.

Grouped SQLite role counts from the successful extract:

| Language | Test cases | Containers | Lifecycle hooks |
|---|---:|---:|---:|
| `razor` | 1 | 0 | 0 |
| `vue` | 2 | 2 | 2 |

Negative controls remained unmarked:

- `RazorRoles.razor::Fact`: `is_test=false`
- `Setup.vue::testNamedButOrdinary`: `is_test=false`
- Vue ordinary member-call labels did not appear as positive test candidates.

Miller's `SqliteSymbolReader`, schema-1 `symbols export --jsonl`, and normal impact JSON agreed with the typed
SQLite flags. The reached impact row carried current case evidence, and the envelope reported
`test_evidence_scope.status=candidate_only` with `absence=unknown`.

## Published language matrix

`julie-extract languages --json` reported 36 languages. For each language, each of `test_case`,
`test_container`, and `test_lifecycle` appeared exactly once across `supported`, `not_applicable`, and
structured `open_gaps`:

| Matrix fact | Count |
|---|---:|
| Languages | 36 |
| Role cells | 108 |
| `supported` | 60 |
| `not_applicable` | 6 |
| `open_gaps` | 42 |
| Duplicate, omitted, or unknown role cells | 0 |

Every open gap carried non-empty `reason`, `required_closure`, and `planned_closure_task` fields.

## Verification ledger

| Command | Result |
|---|---|
| Focused `LiveTestRoleEvidenceScaleTests` + `JulieExtractLanguagesScaleTests` | PASS, 3/3, no skips |
| Reader fallback + bridge regression scope | PASS, 32/32 |
| Full Scale suite: `scripts/test.sh scale` | PASS, 50/50, no skips |
| Fast suite: `scripts/test.sh` | PASS, 3,181/3,181 in 22 s (30 s ceiling) |
| Release build: `dotnet build Miller.slnx -c Release` | PASS, 0 warnings, 0 errors |

The first full Scale run exposed a current-schema synthetic writer missing `parse_diagnostics`; it was repaired
to model schema 4 accurately. The fast gate then exposed intentionally minimal artifact fixtures with neither
`files` nor `parse_diagnostics`; `SqliteSymbolReader` now preserves their historical reader behavior by emitting
unknown/default role currency rather than failing, while current released artifacts use their complete tables.
