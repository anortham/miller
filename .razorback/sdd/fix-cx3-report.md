# fix-cx3 — forced-arm loudness + `--arm semantic` visibility filters

Worktree `/Users/murphy/source/miller/.claude/worktrees/semantic-p3`, branch `worktree-semantic-p3`.

## Finding 3 — forced hybrid silently succeeded as lexical (real-bug, high)

`ForcedHybridFusionArm.Fuse` collapsed "the arm did not serve" and "the arm served nothing" into the same
`null` the executor reads as abstention, so a handshake/embed failure, an open circuit, a KNN fault, or an
artifact promoted between the pre-query probe and the query rendered lexical output and exited 0.

- The arm now records `Queried` and `UnservedReason`; only a genuinely-unserved query sets the reason.
- `CliDispatch.RunForcedArm` (extracted from `RunSymbolRoute`) buffers the fused output, checks the arm, and on
  an unserved query writes `--arm hybrid could not query the vector artifact: <reason>` to stderr and exits 3
  with no stdout. A served-but-empty query still renders lexical and exits 0 — the arm ran, that is a real answer.
- A candidate set the executor never offers the arm (file-name lookup) is also loud: exit 3 with the reason,
  since a forced evaluation run must never report a retrieval quality it did not measure.
- The production `SemanticSymbolFusionArm` fail-open path is untouched.

## Finding 4 — `--arm semantic` ignored visibility filters (real-bug, medium)

`QuerySymbolsAsync(..., allow: null)` dropped `exclude_tests`, `--file-pattern`, and `--language`, and
unresolvable hits consumed fixed K with no refill.

- The forced-semantic path now collects the lexical candidate set and passes `AdmitsUnder(index, visibility)`,
  mirroring `ForcedHybridFusionArm.Admits`. Rejections are refilled by `SemanticSearchArm`'s own recall
  escalation rather than spending a result slot.

## Tests (`tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`)

- `ForcedHybridArm_WhenTheArmCannotServe_ExitsThreeWithTheReasonAndNoResults`
- `ForcedHybridArm_WhenTheArmServesNoNeighbours_RendersLexicalAndExitsZero`
- `ForcedSemanticArm_ExcludesTestSymbolsAndRefillsThroughTheRejection`
- `ForcedSemanticArm_HonoursTheFilePatternFilter`
- `ForcedSemanticArm_HonoursTheLanguageFilter`

Red proven by reverting both fixes in place: 4 of 5 failed, and the served-but-empty guard passed both ways
(it exists to stop the loud path over-triggering).

## Verification

- `dotnet test --filter "FullyQualifiedName~CliDispatch|FullyQualifiedName~SearchDeterminism"` — 173 passed.
- `scripts/test.sh` — 4159 passed, 2 skipped, 28s. A first run failed one foreign test
  (`JulieDbFixtureCurrentSchemaTests.Fixture_EmitsV4ResolutionTables`, `ObjectDisposedException` on a pooled
  SQLite handle) under parallel-worker load; it passes in isolation and on the clean retry.
- `dotnet build Miller.slnx -c Release` — 0 warnings, 0 errors.

## Concerns

- Treating "the executor never offered the arm the query" as exit 3 is slightly wider than the brief's
  unserved-only wording. It is the same honesty rule (`--arm hybrid --mode file` would otherwise print lexical
  output and exit 0) and no existing test exercised that combination, but it is a behaviour choice worth a look.
- `--arm semantic` now runs the lexical candidate collection to derive the visibility policy. That is an extra
  lexical query per forced-semantic CLI run; it is the only way to apply exactly the filters the lexical arm
  would have applied.
