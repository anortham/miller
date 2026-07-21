# Task 3 report — `eval/fusion-arm` adapter (encoder-comparison + fusion-v2 plan)

> This path collides across plans. It previously held the **P5 Canary** "Semantic query diagnostics" Task 3
> report (commit `067c1f7`); git history preserves it. This content is the fusion-v2 plan's Task 3 report.

**Status:** complete. Adapter builds (Release, 0 warnings / 0 errors), tests green (7/7), fixture end-to-end
accepted by `retrieval-eval score` (exit 0).

## What was built

Files created under `eval/fusion-arm/**` (nothing else touched):

- `FusionArm.csproj` — net10.0 console, `OutputType=Exe`, `AssemblyName=fusion-arm`, **outside `Miller.slnx`**,
  references `src/Miller.Core/Miller.Core.csproj` only, mirrors `retrieval-eval`'s `Compile Remove="tests/**"`
  isolation.
- `Model.cs` — `ArmInputRow` (`symbol_id`/`doc_id`/`score`/nullable `rank`), `QueryRow`, `FusedResultRow`, and a
  `Json` helper (JSONL query-set reader that skips `#`/blank lines; per-query array reader; compact single-line
  results serializer).
- `Fuser.cs` — the pure core. `Plan()` routes; `Apply()` fuses + collapses. Routing IS
  `SemanticQueryPolicy.Route`, fusion IS `RrfFusion.Fuse` — no reimplementation.
- `FusionRunner.cs` — file orchestration: per-query load, missing-file skip+count, results write. Thin,
  testable, returns a `FusionRunSummary`.
- `Program.cs` — thin CLI over `FusionRunner.Run` with a small flag-aware arg parser.
- `README.md`, `tests/FusionArm.Tests.csproj`, `tests/FusionArmTests.cs`.

`Program.cs` is thin: it parses args and calls `FusionRunner.Run`. All behavior lives in `Fuser`/`FusionRunner`.

## Test list (`dotnet test eval/fusion-arm/tests/FusionArm.Tests.csproj` → 7 passed)

1. `IdentifierQuery_RoutesLexicalPassthrough_IgnoringSemantic` — identifier query → lexical passthrough; semantic
   `docC` absent (Route honored).
2. `ProseQuery_Fuses_AndConceptualRatioReordersPredictably` (Theory ×2) — hand-computed RRF fixture (2 lexical +
   2 semantic, one overlap on symbol B/docB). ratio 1.0 → `[docB, docA, docC]`; ratio 3.0 → `[docB, docC, docA]`
   (semantic-only docC overtakes lexical-only docA). Exact order asserted for both.
3. `DocCollapse_HappensAfterFusion_DedupingByBestFusedRank` — two symbols → same `docX`; a semantic boost lifts
   `docY` above both. Result `[docY, docX]` proves collapse follows fusion order and `docX` keeps its best
   (symbol A) rank, not lexical array order.
4. `Run_IsDeterministic_ByteIdenticalAcrossRuns` — two runs, `File.ReadAllBytes` equal.
5. `MissingInputFile_EmitsNoRow_AndIsCountedInSummary` — `q2` lacks a lexical file → no row, `MissingCount==1`,
   `MissingQueryIds==["q2"]`, only `q1` emitted.
6. `ForcedHybrid_BypassesRoute_ForIdentifierQuery` — same identifier query: honored → passthrough (`docB`
   absent); `--forced-hybrid` → fused `[docA, docB]` (semantic pulled in). Proves the bypass.

### Hand-computed RRF check (test 2, kConst 60, weights `(1.0, ratio)`)

- A (lex rank 1): `1/61 = 0.016393`
- B (lex 2, sem 1): `1/62 + ratio/61 = 0.016129 + ratio·0.016393`
- C (sem rank 2): `ratio/62 = ratio·0.016129`

ratio 1.0 → B 0.03252 > A 0.01639 > C 0.01613 → `[docB, docA, docC]`.
ratio 3.0 → B 0.06531 > C 0.04839 > A 0.01639 → `[docB, docC, docA]`. ✓ matches asserted output.

## Fixture end-to-end (arm-contract compliance)

Two-query fixture (prose `e1`, identifier `e2`) → `fusion-arm fuse` produced:

```
{"query_id":"e1","ranked":["src/Parser.cs","src/Other.cs","src/Tree.cs"]}
{"query_id":"e2","ranked":["src/Bm25.cs"]}
```

`dotnet run --project eval/retrieval-eval -- score --queries … --results … --out …` → **exit 0** (parsed and
scored cleanly). `e1` fused (Parser lifted by the lexical∩semantic overlap); `e2` identifier passed through
lexical-only (semantic `src/Other.cs` correctly excluded).

## Miller MCP calls used

None. The four API-shape files were read directly (Read/grep) and the shapes were unambiguous, so I did not
incur MCP-hang risk. Evidence below is cited from source lines.

## API-shape evidence (verified against source, worktree checkout)

- `RrfFusion.Fuse(IReadOnlyList<SymbolCandidate>, IReadOnlyList<SemanticRankedCandidate>, FusionWeights, int rankConstant = 60)` — `src/Miller.Core/Search/RrfFusion.cs:56`. Dedupes each arm by `SymbolId`; on overlap the **lexical** candidate is the one rendered (`RrfFusion.cs:84-87`) — so `Candidate.FilePath` carries the correct `doc_id` for the fused row regardless of arm origin.
- `FusedCandidate(SymbolCandidate Candidate, double RrfScore, int? LexicalRank, int? SemanticRank)` — `RrfFusion.cs:21`.
- `SymbolCandidate(int DocId, string SymbolId, string Name, string? Signature, string Kind, string FilePath, int StartLine, double Score)` — `src/Miller.Core/Search/SymbolCandidate.cs:10`. Fuse reads only `SymbolId` (dedup) and `Score` (tie-break); I set `SymbolId`/`Score` from input, `FilePath` = input `doc_id` (used for collapse), and fill the rest with inert defaults (`DocId 0`, `Name` = symbol_id, `Signature` null, `Kind` "", `StartLine` 0).
- `SemanticRankedCandidate(SymbolCandidate Candidate, int Rank)` — `RrfFusion.cs:14`. Built with the input file's explicit `rank`.
- `FusionWeights(double Lexical, double Semantic)` — `RrfFusion.cs:8`.
- `RrfFusion.WeightsFor(SemanticFusionClass)` — `RrfFusion.cs:44` (SymbolLookup `(1.0,0.3)`, Conceptual `(0.5,1.0)`, Mixed `(0.8,0.8)`). Used for SymbolLookup/Mixed hybrid routes.
- `SemanticQueryPolicy.Route(string?, LexicalEvidence?)` → `SemanticQueryRoute(bool IsHybrid, SemanticFusionClass HybridClass, SemanticQueryReason Reason)` — `SemanticQueryPolicy.cs:104` / `:70`.
- `LexicalEvidence(int HitCount, double TopScore, double RunnerUpScore)` — `SemanticQueryPolicy.cs:54`. Built from the lexical file per the brief (`HitCount` = rows, `TopScore` = rows[0], `RunnerUpScore` = rows[1] or 0).

## Judgment calls

- **`doc_id` travels via `SymbolCandidate.FilePath`.** Rather than a parallel `symbol_id → doc_id` map, I store
  the input `doc_id` in `FilePath`. Because Fuse renders the lexical candidate on overlap and the semantic one
  otherwise, `FusedCandidate.Candidate.FilePath` is always the right doc for that fused row. No side map, no
  divergence risk (same `symbol_id` ⇒ same `doc_id` in both files by construction).
- **Semantic file required only when fusion runs.** A lexical-only route never reads the semantic file, so a
  missing semantic file only skips a query when the route (or `--forced-hybrid`) actually fuses. Missing lexical
  file always skips. Both cases count into the summary; neither throws. (A present-but-malformed file — e.g. a
  semantic row without `rank` — fails loud via `InvalidDataException`, mirroring retrieval-eval's reader.)
- **`--k-const` / `--conceptual-ratio` are required, not defaulted.** This is a sweep arm; explicit values keep
  runs reproducible and self-documenting rather than depending on a hidden default that could drift.
- **Output is compact single-line JSONL** (`WriteIndented=false`, explicit `\n`) so retrieval-eval's line reader
  consumes one row per line and runs are byte-identical.

## Deferred (not attempted, per brief)

The 5-query **live parity smoke** (adapter fusion-v1 vs `miller search --arm hybrid --json`) is deferred to
Task 4 — it needs live vectors this task does not produce. Not attempted here.

## Commit

Left unstaged per parallel-lead-commit. Only `eval/fusion-arm/**` is mine; other dirty paths
(`.razorback/sdd/*`, `eval/model-bench/*`, `docs/…`) belong to sibling tasks and were not touched.
