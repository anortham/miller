# Task F2 report — Semantic retrieval arm

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-p3`, branch `worktree-semantic-p3`,
base commit `6c25789`. Commit mode **parallel-lead-commit** — nothing staged or committed; the diff is handed
to the lead.

## Files changed

| File | Change |
| --- | --- |
| `src/Miller.Indexing/Semantic/SemanticSearchArm.cs` | NEW — the arm, `SemanticHit`/`SemanticQueryResult`, the `IVectorSearchPort` seam + production adapter, and the relocated `SemanticVectorQuantizer` |
| `tests/Miller.Tests/Indexing/SemanticSearchArmTests.cs` | NEW — 16 fast tests + 2 Scale tests |
| `src/Miller.Server/Hosting/VectorConvergeService.cs` | sanctioned narrow touch — `QuantizeToInt8` body replaced by a one-line delegation to `SemanticVectorQuantizer.ToInt8` |

## Implementation

Public surface (`Miller.Indexing.Semantic`):

- `SemanticHit(string? SymbolId, string? DocId, string FilePath, int Rank, double Cosine)` — exactly one id is
  populated per corpus, so a chunk hit can never be rendered as a symbol card.
- `SemanticQueryResult(IReadOnlyList<SemanticHit> Hits, string? UnavailableReason)` with `Served =>
  UnavailableReason is null`. An empty **served** result ("no allowed neighbours") is a different fact from an
  empty **unavailable** result ("the arm could not run"), and F3 needs to tell them apart.
- `IVectorSearchPort : IDisposable` (`Lane`, `Search(kind, ReadOnlySpan<sbyte>, k)`) + the
  `VectorSearchPortFactory` delegate, shaped exactly like `VectorSidecar.TryOpen` (`out string? reason`).
- `SemanticSearchArm(workspaceRoot, VectorSidecar, Func<SemanticEmbeddingSession?>)` — production ctor; the
  internal ctor takes `(enabled, portFactory, sessionFactory)` for the fast tests.
- `QuerySymbolsAsync` / `QueryChunksAsync(query, k, Func<VectorMatch,bool>? allow, ct)`.
- `SemanticSearchArm.ProcessSession(toolsRoot)` — the locator mirrored from `VectorConvergeService.ProcessSession`
  (`julie-semantic-sidecar[.exe]` beside the binary; null when absent).
- `SemanticVectorQuantizer.ToInt8` — the shared lane quantizer.

Query path: **off short-circuit → artifact gate (`TryOpen`) → session → embed → lane checks → recall → map**,
with the store disposed in a `finally` on every path.

Bounded refill (`MaxCandidates = 500`, mirroring the lexical 500-candidate escalation named in design §6.2):
fetch `k`, then double, stopping on (a) `k` allowed hits, (b) the store returning fewer rows than requested
(corpus exhausted — the difference between "no more allowed hits" and "did not look deep enough"), or (c) the
ceiling. Each fetch re-reads from scratch: vec0 KNN has no cursor, so a deeper fetch is a superset of a
shallower one.

Cosine mapping — **evidence:** the pinned `storage_schema` is `vec0-int8-512-cosine-v1`
(`MillerSemanticContract.DefaultEncoder`), and `VectorStore.SchemaDdl` renders the lane's metric straight into
the vec0 declaration `distance_metric={lane.Metric}` (`VectorStore.cs:432`). sqlite-vec cosine distance is
`1 - cos`, so `Cosine = clamp(1 - distance, -1, 1)`; the clamp exists because int8 quantization can push the
value a hair outside the range. A non-cosine lane returns a reason rather than a fabricated similarity.

## Judgment calls

1. **`Async` suffix on the query methods.** The brief writes `QuerySymbols(...)`; `EmbedQueryAsync` makes the
   path genuinely asynchronous and every async member in this codebase carries the suffix, so the methods are
   `QuerySymbolsAsync`/`QueryChunksAsync`. F3 should expect those names.
2. **`SemanticVectorQuantizer` lives in `SemanticSearchArm.cs`, not its own file.** The brief sanctioned a
   shared home under `src/Miller.Indexing/Semantic/` but the owned-file list names only the arm file; a new
   `SemanticVectorQuantizer.cs` would have been outside it. Same namespace and assembly, so it is functionally
   the shared home — the lead may lift it into its own file at merge with no code change.
3. **`VectorConvergeService.QuantizeToInt8` was kept as a one-line delegation** rather than deleted. Its only
   other caller is `VectorConvergeServiceTests.Quantize_MapsUnitNormFloatsIntoThePinnedInt8Lane`
   (`trace` refs: `VectorConvergeService.cs:618` + that test), which I do not own; deleting the member would
   have broken a file outside my ownership. Zero behaviour change either way.
4. **The port is opened and disposed per query.** A reader connection held across queries pins a generation's
   inode across a promote — the same hazard `FreshnessService` re-opens per poll to avoid. If F3 measures this
   as too costly, caching belongs at the executor with promote-aware invalidation, not inside the arm.
5. **The artifact gate runs before the session.** A workspace with no vectors never pays for a child process,
   and an incompatible generation is a reason rather than a wasted embed.
6. **Off short-circuits before both seams.** `TryOpen` is itself provably zero-work when off (B1), but
   returning before calling it makes "zero work" an assertable observable in the arm's own tests.
7. **The arm re-sorts by (distance, rowid)** even though `VectorStore.Search` already does. Rank is the arm's
   output contract; it must not depend on which port implementation answered.
8. **The predicate is `Func<VectorMatch, bool>`** — `VectorMatch` already carries rowid/distance/unit id/path,
   which is everything a `ToolSearchFilters` adapter needs. F3 supplies the adapter.
9. **Fail-open catch is narrow** (`VectorStoreException`, `InvalidOperationException`, `IOException`) plus the
   embed outcome's own non-throwing failure path. A genuine programming error still surfaces.

## Verification

| Gate | Command | Result |
| --- | --- | --- |
| worker-red-green | `dotnet test … --filter "FullyQualifiedName~SemanticSearchArm"` | **18/18 pass** (16 fast + 2 Scale; extension present locally) |
| Guards | `--filter "FullyQualifiedName~SemanticOffGuarantee\|FullyQualifiedName~VectorConverge"` | **47/47 pass** — the quantizer relocation disturbs nothing |
| Scale skip path | `env -u MILLER_SQLITE_VEC_PATH SPIKE_CACHE_DIR=<empty> … --filter "~SemanticSearchArmScale"` | **2 skipped, 0 failed** |
| worker-ceiling | `scripts/test.sh` | **4070 passed, 2 skipped, 0 failed** (21s wall, under the 30s ceiling) |
| worker-ceiling | `dotnet build Miller.slnx -c Release` | **0 warnings, 0 errors** |

TDD: tests were written first; the first run was 17 pass / 1 fail — the refill test asserted the fetch ladder
stopped at `[4,8,16,32]`, and the arm's actual `[4,8,16,32,64]` was correct (at fetch=32 the store still
returned a full page, so stopping there would have silently dropped two allowed hits — exactly the failure mode
the acceptance criterion names). The expectation was corrected, not the implementation.

**Flake observed, not mine:** two `scripts/test.sh` runs recorded a single failure in
`IndexerServiceScanTests.StartAsync_AsLeader_RecordsLeaderIdentity_AndRemovesItOnStop`, and two runs recorded
58–59s wall time. Both correlate with concurrent worker load on this machine (the other P3 worker builds and
tests in the same worktree). That test passes in isolation and on repeated full runs, and touches nothing this
task changed (leader-lock timing vs. a query-time arm). Flagged for the lead rather than worked around.

Invariants asserted by the fast tests: zero work when off (neither seam invoked); the gate's reason is passed
through verbatim on no-artifact and the sidecar is never launched; missing sidecar binary, embed failure,
circuit-open, lane-dims mismatch, non-cosine lane and an unexpected store fault each yield empty + reason with
no KNN run; rank/cosine mapping from the store's distance; rowid tie-break with run-to-run equality; chunk
routing carrying `DocId`; bounded refill returning every allowed hit; early stop on an exhausted corpus; the
500 ceiling under a hostile predicate; query quantization byte-equal to the writer's; store disposed per query.

## Miller calls used

| Call | Purpose |
| --- | --- |
| `context query="semantic vector sidecar search arm: VectorSidecar.TryOpen, VectorStore.Search, SemanticEmbeddingSession query embedding"` | orientation — surfaced the seed set and the `ISemanticSidecarLauncher` seam |
| `inspect target=VectorSidecar depth=full` | the `TryOpen` contract (null + reason, caller owns the store, classification may serve a retained generation) |
| `trace target=QuantizeToInt8 mode=refs` | pre-relocation reference check — 2 refs, one of them a test file I do not own (drove judgment call 3) |

`VectorStore.Search`, `SemanticEmbeddingSession`, `MillerSemanticContract` and the fixture/collection patterns
were read directly after Miller located them.
