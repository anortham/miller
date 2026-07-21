### Task 3: `eval/fusion-arm` adapter

**Files:**
- Create: `eval/fusion-arm/FusionArm.csproj` (net10.0, references `src/Miller.Core/Miller.Core.csproj`, outside Miller.slnx), `eval/fusion-arm/Program.cs`, `eval/fusion-arm/README.md`, `eval/fusion-arm/tests/FusionArm.Tests.csproj` + one test file
- Test: `eval/fusion-arm/tests/`

**Interfaces:**
- Consumes: `Miller.Core.Search.RrfFusion.Fuse(IReadOnlyList<SymbolCandidate>, IReadOnlyList<SemanticRankedCandidate>, FusionWeights, int rankConstant)`, `SemanticQueryPolicy.Route(string?, LexicalEvidence?)` → `SemanticQueryRoute(IsHybrid, HybridClass, Reason)`, `LexicalEvidence(int HitCount, double TopScore, double RunnerUpScore)`.
- Produces: CLI `fusion-arm fuse --queries <dev queries.jsonl> --lexical <dir> --semantic <dir> --k-const <int> --conceptual-ratio <r> --out results.jsonl [--forced-hybrid]`. Input formats: lexical per-query file `<query_id>.json` = array of `{symbol_id, doc_id, score}` (rank = array order); semantic per-query file same shape plus `rank`. Output: retrieval-eval `results.jsonl` (`{"query_id", "ranked": [doc_id...]}`), post-fusion top-10, doc_id collapse AFTER fusion, dedupe doc_ids preserving best rank. `--forced-hybrid` bypasses `Route` (identifier diagnostic arm); default honors Route — LexicalOnly queries emit lexical order untouched.
- LexicalEvidence built from the lexical file itself: `HitCount = rows.Length, TopScore = rows[0].score, RunnerUpScore = rows[1]?.score ?? 0`.

**Contract inputs:** spec R2/R3; Conceptual ratio r maps to `new FusionWeights(1.0, r)` (RRF is scale-invariant — verified by RrfFusionTests determinism suite); SymbolLookup/Mixed always `RrfFusion.WeightsFor(class)` v1 values.

**File ownership:** Create: `eval/fusion-arm/**` (csproj, Program.cs, tests); Modify: none

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The offline fused-arm runner that IS production fusion — same assemblies, no reimplementation.

**Approach:** TDD on the tests project: routing honored (identifier query → lexical passthrough), fusion parity with a hand-computed RRF fixture, doc-collapse-after-fusion, deterministic output. Keep Program.cs thin over a testable `FusionRunner` class.

**Acceptance criteria:**
- [ ] Adapter builds and tests green (`dotnet test eval/fusion-arm/tests/FusionArm.Tests.csproj`).
- [ ] Fixture run produces contract-valid results.jsonl (`retrieval-eval score` accepts it, exit 0).
- [ ] Parity smoke DEFERRED to Task 4 start (needs live vectors): 5 dev prose queries, adapter at fusion-v1 vs `miller search --arm hybrid --json` — ranks must match.

