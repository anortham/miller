# Task B2 report — Generation identity + vectors.db storage schema

Worktree: `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`, branch `worktree-semantic-p2`.

## What I implemented

**`src/Miller.Indexing/Semantic/MillerSemanticContract.cs` (new, pure/no-I/O).**
Pinned initial values (`contract_version=1`, `hash_algorithm=blake3`, `corpus_generation=cards-v1-chunks-v1`,
the two lane strings), both encoder pins as `SemanticEncoderPin` records sourced verbatim from
`eval/model-bench/bench-pins.json`, the `encoder_fingerprint` composition
(`CanonicalEncoderString` → `sha256:<64 hex>`), the generation tag (16 hex over fingerprint + lane only),
`SemanticGenerationIdentity` (five fields; `reader_compatibility` is two properties because it is two meta
keys), the invalidation matrix as `ClassifyChange`, `RequiresEmbeddingWork`, the semver reader gate
`SatisfiesMinReaderVersion`, and `ParseStorageSchema` decomposing the lane.

`InvalidationAction` members are ordered weakest→strongest (`None < QueryTimeOnly < ReaderGate <
TargetedReEmbed < ShadowRebuild`) so a multi-field change resolves to the strongest mechanism by ordinal
comparison rather than a hand-maintained precedence table.

**`src/Miller.Indexing/Semantic/VectorStore.cs` (new).**
The physical artifact: `vectors_meta`, both vec0 tables with the declaration **derived from the lane string**
(`int8[512]` / `int8[384]`), both mapping tables with `path`, all four indexes. `Create` stamps every meta key
in the contract table including both cursors and `chunk_source_artifact_id`. `Open`/`ReadMetaAt` verify
`vec_version()` against the pin *before* any meta read. `Upsert` writes the vec0 row and its mapping row in
one transaction; `Search` sorts by distance then integer rowid in C# (deterministic ties);
`ResolveGlob` is the mapping-table glob-resolution surface. Connections open with `Pooling=false`.

**`src/Miller.Indexing/VectorSidecar.cs` (modified).**
Added an `IVectorStoreOpener` seam beside the existing `IVectorFileProbe`, and replaced the placeholder
`UnavailableReason` with a real `Classify` that walks: presence → open (which verifies `vec_version()`) →
`contract_version` → required meta keys → encoder fingerprint → `min_reader_version` → `build_state`. States
come from the frozen §Status vocabulary: `disabled | unavailable | incompatible | building | ready`.
`TryOpen` now returns **true** for a ready, compatible generation; `OpenRequired` throws only when not ready.

## Judgment calls

1. **`writer_version`-only change classifies `ReaderGate`.** The matrix row is the composite field
   `reader_compatibility`, so I implemented it verbatim: any difference in either constituent yields
   `ReaderGate`. This means a rebuild with a new gitsha reports `ReaderGate`, which is harmless — the contract
   guarantees this mechanism never re-embeds and never gates vectors, and `RequiresEmbeddingWork` returns
   false for it. The narrower reading (only `min_reader_version` gates readers) would have been a local
   redesign of a frozen row, so I did not take it. **Flagging for the lead** in case B5/B6 want the narrower
   split.
2. **`fusion_profile` and `min_reader_version` pinned values are not literal in the contract.** vectors-v1
   pins the encoder, lane, and corpus values but specifies `fusion_profile` only as "reader-side profile id at
   build time" and `min_reader_version` only as "semver". I pinned `FusionProfile = "fusion-v1"` and
   `MinReaderVersion = "1.13.0"` (the current `Directory.Build.props` `<Version>`, i.e. the first Miller able
   to read a v1 artifact). Lane C owns fusion, so it may want to own that string — **flagging**.
3. **Sidecar states beyond `unavailable`.** B6 owns status/health facts, but a correct open path cannot report
   a still-building or encoder-mismatched generation as `unavailable` without contradicting the frozen
   vocabulary. I emit `incompatible` and `building` too. Rendering (the compact `building 42%` line) is
   untouched and remains B6's.
4. **Sidecar tests live in my `VectorStoreTests.cs`.** `VectorSidecarTests.cs` is B1's file and not in my
   ownership list, so the open-path tests are a `VectorSidecarOpenPathTests` class in my own file. They still
   match the assigned `FullyQualifiedName~VectorSidecar` filter.
5. **No packaged extension in this build.** `VectorStore.ResolveExtensionPath()` reads
   `MILLER_SQLITE_VEC_PATH` and otherwise returns null, so production reports a stated reason instead of
   silently degrading. Packaging the per-RID extension is a later task.

## Two real defects the Scale tests caught

- **`vec_version()` returns `v0.1.9`, not `0.1.9`.** A literal equality check against the pin rejected every
  artifact. Fixed with `NormalizeVecVersion`; the error message still reports the raw value.
- **int8 vec0 columns reject raw BLOBs.** sqlite-vec reads an untagged BLOB as float32
  (`'Inserted vector ... expected int8, but a float32 vector was provided'`). Inserts and KNN now wrap the
  bound parameter in `vec_int8(...)`, derived from the lane element. This is exactly the "verify `int8` vec0
  tables on the exact pinned extension is a P2b implementation gate" note in vectors-v1 §Storage schema —
  **that gate is now met on `osx-arm64`**; the other three RIDs remain unverified.

## Verification

| Scope | Invariant proved | Command | Result |
|---|---|---|---|
| worker-red-green | Invalidation matrix, identity composition, lane parsing, reader gate, schema shape, sidecar open path | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~MillerSemanticContract\|FullyQualifiedName~VectorStore\|FullyQualifiedName~VectorSidecar"` | **PASS** — 92 passed, 0 failed |
| worker-red-green (Scale, real extension) | Create/write/read round-trip against real sqlite-vec 0.1.9; `vec_version()` matches pin; contract column names asserted | same filter, extension present via spike cache | **PASS** — the 12 Scale tests ran, not skipped |
| Scale skip path | Scale tests SKIP (never fail) when the extension is absent | `SPIKE_CACHE_DIR=/tmp/nonexistent dotnet test --filter "FullyQualifiedName~VectorStoreTests"` | **PASS** — 12 skipped, 0 failed |
| worker-ceiling | Release build, warnings-as-errors | `dotnet build Miller.slnx -c Release` | **PASS** — 0 warnings, 0 errors |
| worker-ceiling | Fast suite | `dotnet test --filter "Category!=Scale&FullyQualifiedName!~EditToolTests"` | **PASS** — 3706 passed, 1 failure in `MetricsToolTests` (E2's file, see below) |

**Fast-suite failures are not mine.** A bare `scripts/test.sh` reports 3–6 failures, all in
`EditToolTests` (D2's uncommitted `EditTool.cs` / `EditService.cs` / `TextReplaceMatcher.cs` /
`EditToolTests.cs`) and `MetricsToolTests.RunClones_CandidateScanTruncated_*` (E2's uncommitted
`NearDuplicateFixtures.cs` / `MetricsTool.cs`). Excluding `EditToolTests` leaves exactly one E2 failure and
3706 passes. The Release build and the fast suite were each broken mid-run several times by those workers'
in-flight edits (`NearDuplicateScan` missing, `NearDuplicateFixtures.CreateCandidateCapOverflow` missing,
`EditToolTests` deconstruction errors); per the brief I waited and retried rather than touching their files,
and both gates went clean once their trees settled. **I touched none of their files.**

## Miller calls used

| Call | What it confirmed |
|---|---|
| `context query="vectors.db semantic sidecar vector store generation identity invalidation"` | Located the frozen contract (`docs/contracts/vectors-v1.md`), B1's `VectorSidecar` seams, and the existing `VectorSidecarTests` / `WorkspaceFactsAssemblerTests` consumers before I wrote anything |
| `inspect target="src/Miller.Indexing/VectorSidecar.cs"` | Failed — search sidecar stale at revision 313 vs 314. Fell back to reading the file directly and noted the staleness rather than trusting a stale index |
| `trace target=VectorSidecar mode=refs` | The consumer set before modifying a public class: `WorkspaceFactsAssembler` (5 sites, all passing `Inspect(...)` through), `SemanticOffGuaranteeTests`, `VectorSidecarTests`. This is what told me the off-guarantee test drives the probe seam directly, so my new opener seam had to short-circuit before it under `Off` |

## API-shape evidence (nothing inferred from memory)

- **Contract strings** — table/column/meta names, the nine-line canonical encoder string, the tag rule, the
  status vocabulary, the invalidation matrix: `docs/contracts/vectors-v1.md` §§Generation identity, Pinned
  initial values, Invalidation matrix, Storage schema, Shadow generations, Status vocabulary (read in full).
- **Model pins** — `eval/model-bench/bench-pins.json` (`sha256`, `file`, `native_dims`, `pooling`,
  `query_instruction` read verbatim; the contract names this file as the single source).
- **sqlite-vec pin `0.1.9` + per-RID asset/member names** — `scripts/spike-pins.json`.
- **Extension load pattern** (`EnableExtensions`/`LoadExtension`/`SELECT vec_version()`, pooling, cleanup) —
  `spike/SqliteVec.AotSpike/Program.cs`; cache path `${SPIKE_CACHE_DIR:-$TMPDIR}/miller-sqlite-vec-spike/$VERSION/$RID` —
  `scripts/spike-sqlite-vec.sh:94`.
- **`SemanticMode` / `SemanticActivation.FromEnvValue` / `IVectorFileProbe` / `VectorSidecarFacts`** — read
  directly from B1's committed `SemanticActivation.cs` and `VectorSidecar.cs`.
- **`ContentCorpusSchema.SchemaVersion = 2`** — `src/Miller.Indexing/ContentCorpusSchema.cs`, used to stamp
  `chunk_content_schema_version` (contract §Cursors rule 2 pins it to the content-corpus contract's value).
- **Skip idiom `Assert.SkipWhen`** — `tests/Miller.Tests/ScaleTestSupport.cs:71-81`.
- **`<Version>1.13.0`** — `Directory.Build.props:11`.

## Files changed

Created:
- `src/Miller.Indexing/Semantic/MillerSemanticContract.cs`
- `src/Miller.Indexing/Semantic/VectorStore.cs`
- `tests/Miller.Tests/Indexing/MillerSemanticContractTests.cs`
- `tests/Miller.Tests/Indexing/VectorStoreTests.cs`

Modified:
- `src/Miller.Indexing/VectorSidecar.cs`

Staged with explicit paths only. `ScaleTraitConventionTests` untouched and green (it keys on
`RequireJulieServer`; my Scale class spawns no subprocess and funnels through its own
`SqliteVecTestSupport.RequireExtension` signal).

## Follow-up commit — API evolution per lead guidance (`189b96d`)

After the initial commit the lead relayed B1's author guidance: evolve
`TryOpen`/`OpenRequired` to return the opened store while keeping `out string? reason`.

- `TryOpen` now returns `VectorStore?`, `OpenRequired` returns `VectorStore`. The `out string? reason`
  parameter is preserved and always populated on the null path.
- `IVectorStoreOpener` gained `OpenStore`, called **only** after classification already found the generation
  serviceable — a failure there is an unexpected race, not a routine state.
- **`Inspect` still opens no store**, so the status/health path stays cheap. This was the main design
  pressure: `WorkspaceFactsAssembler` calls `Inspect` on every status render.
- **I had to touch two files outside my ownership list**: `tests/Miller.Tests/Indexing/VectorSidecarTests.cs`
  and `tests/Miller.Tests/Indexing/SemanticOffGuaranteeTests.cs`. The return-type change breaks their call
  sites (`Assert.False(TryOpen(...))`, `Assert.Throws<...>(() => OpenRequired(...))`) regardless of the
  preserved reason parameter, so the build could not stay green without adapting them. Both were **committed
  and clean** (no in-flight edits to clobber), and the changes are mechanical: `Assert.False` → `Assert.Null`,
  statement-bodied throw lambdas, one added `using`. **No assertion semantics were weakened** — in particular
  B1's `RecordingProbe` zero-call off-guarantee assertions are untouched and green.
- **Coverage moved, not lost.** A fake opener cannot manufacture a sqlite-vec-backed store, so the
  "ready generation returns a usable store" assertion is now a Scale test
  (`TryOpen_ReadyGeneration_ReturnsAUsableStore`) that creates a real artifact, opens it through the sidecar,
  and runs a KNN through the returned store. The fast suite still covers every classification state.
- Confirmed the lead's ruling: I added **no** rendering for the `disabled` state anywhere.

Re-verified after the change: 98/98 on the assigned filter plus `SemanticOffGuarantee`;
`dotnet build Miller.slnx -c Release` 0 warnings / 0 errors; **full fast suite now 3823 passed / 0 failed**
(D2 and E2 have since landed, so the earlier cross-worker failures are gone).

## Concerns for the lead

1. **`fusion_profile` / `min_reader_version` literals are mine, not the contract's** (judgment call 2). If
   lane C or B6 wants different strings, they are single constants in `MillerSemanticContract`.
2. **int8 vec0 is verified on `osx-arm64` only.** The contract's §Storage schema gate asks for all four RIDs.
   `linux-x64`, `osx-x64`, `win-x64` remain unproven; the `vec_int8(...)` fix is the thing to re-verify there.
3. **`writer_version` classifying as `ReaderGate`** (judgment call 1) — verbatim-matrix reading; worth a
   second opinion before B5 builds promote logic on `ClassifyChange`.
4. **The search sidecar in this worktree is stale** (revision 313 vs 314), so Miller `inspect` failed once
   mid-task. Worth a `workspace refresh` before the next worker relies on it.
5. **`VectorStore` has no `EnsureCurrent` yet** — vectors-v1 §File placement names it as part of the writer
   path. That is B4's convergence work; B2 deliberately stopped at create/open/write/read.
