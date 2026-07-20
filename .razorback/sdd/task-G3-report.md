# Task G3 — real-sidecar Scale tests (RC promotion gate)

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-p3`
**Branch:** `worktree-semantic-p3` · base HEAD `af8460a`
**Status:** complete. Gate **PASSES** — but only after a pin correction the gate itself uncovered.

---

## 1. Headline: the gate did its job on its first run

Against the restored RC (`julie-semantic-sidecar 0.1.0-rc.1`) the encoder handshake **failed**, and
the cause was a single field in Miller's pin.

Live `health` envelope from `.tools/julie-semantic-sidecar serve` (probed directly before writing any
C#, so the finding is independent of my test code):

| field | sidecar reports | Miller pin (before) | verdict |
|---|---|---|---|
| `model_id` | `qwen3-0.6b-f16` | same | ✅ |
| `model_sha256` | `421a27e5…c54340` | same | ✅ |
| `dims` | `512` | `512` | ✅ |
| `pooling` | `last` | `last` | ✅ |
| `normalization` | `l2` | `l2` | ✅ |
| `model_revision` | `main` | `Qwen3-Embedding-0.6B-f16.gguf` | ❌ |

`SemanticEmbeddingSession.FirstDisagreement` (`SemanticEmbeddingSession.cs:700`) compares
`model_revision` non-empty-vs-pin, so `MatchEncoder` returned a stated refusal → `CircuitOpen` → all
three tests red including the fingerprint gate.

Diagnosis reported to the lead: Miller's pin was wrong, not the sidecar. `ModelRevision` had been set
to the GGUF **file name**, but that identity is already carried by `ModelSha256`;
`eval/model-bench/bench-pins.json` records `"file": "Qwen3-Embedding-0.6B-f16.gguf"` alongside the URL
`…/resolve/**main**/…`, so `main` is the revision and the `.gguf` is the file. The lead applied the fix
in `MillerSemanticContract.cs` (both `DefaultEncoder` and `FallbackEncoder` → `ModelRevision: "main"`).
That file is outside my ownership; I did not touch it.

**Note for the lead:** this change alters `encoder_fingerprint` (it is one of the nine canonical
composition fields). Any `vectors.db` already stamped with the old fingerprint classifies as
`incompatible` — correct behaviour per the invalidation matrix, but worth a release-note line.

After the fix, all three tests pass against the real binary.

---

## 2. What was built

### `tests/Miller.Tests/ScaleTestSupport.cs`
- `LocateSemanticSidecar()` / `RequireSemanticSidecar()` — mirrors `LocateJulieServer`/`RequireJulieServer`
  exactly: repo-root `.tools/` lookup, platform-correct name, `Assert.SkipWhen` (skip, never fail).
- Kept as a **separate** signal from the julie one rather than widening it: the two binaries are restored
  by different scripts, and a skip message naming the wrong script sends the reader to a command that
  cannot help. Class remarks say so.
- The skip message names `scripts/restore-semantic-sidecar.sh` **and** the one-time
  `.tools/julie-semantic-sidecar prepare` model-cache step (see §4).

### `tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs`
- Signals split into `JulieLaunchSignals` and `SemanticLaunchSignals`; the scan now trips on either.
- **Per-family non-vacuity assertions** (`AssertSignalFamilyIsCovered`). One combined counter would let a
  rename of the semantic signal pass silently as long as some julie test still existed — exactly the
  coverage hole the counter exists to close. This is a strengthening, not a rewrite: comment-stripping,
  the bin/obj filter, the >10-file sanity floor, the exempt set and the whitespace-insensitive trait
  match are all untouched.
- Test renamed `EveryJulieSpawningTest_…` → `EveryPinnedBinarySpawningTest_…` to match the widened scope.

### `tests/Miller.Tests/Indexing/SemanticSidecarScaleTests.cs` (new)
`[Trait("Category","Scale")]` at class level; binary obtained **only** via `RequireSemanticSidecar()`.

1. `Handshake_AgreesWithThePinnedDefaultEncoder` — **the RC promotion gate.** Launches through
   `SemanticEmbeddingSession` + `ProcessSemanticSidecarLauncher`, asserts the returned
   `SemanticEncoderHandshake.EncoderFingerprint` equals
   `MillerSemanticContract.EncoderFingerprint(DefaultEncoder)`, plus dims and `State == Ready`. The
   failure message surfaces `session.UnavailableReason`, which is what made §1 diagnosable in one run.
2. `EmbedBatch_ReturnsThePinnedDimsAndQuantizesIntoThePinnedInt8Lane` — a real `SymbolCardBuilder.Build`
   card embeds to 512 floats with no flagged indices, then `SemanticVectorQuantizer.ToInt8` lands
   `lane.Dims` int8 components in the `vec0-int8-512-cosine-v1` lane, at least one non-zero.
3. `PlantedSymbol_ConvergesThenAnswersASemanticallySimilarProseQuery` — three symbol cards
   (`FullRebuildPromotion` + two semantic distractors: a dashboard colour swatch, a market-hours
   calendar) embed through the real session, commit through a real `VectorStore.CommitBatch` over real
   vec0, then query through `SemanticSearchArm` with prose that shares **no** lexical token with the
   target (*"swap the freshly built index over the one being served, all at once"*). Asserts the planted
   symbol comes back at rank 1 with the right path. The distractors are what make this a semantic
   assertion rather than a plumbing assertion.

**Collection membership:** deliberately **none**. The class sets no environment variable — it passes the
sqlite-vec path explicitly through a private `RealOpener : IVectorStoreOpener`, exactly as
`VectorStoreTests` does. `SqliteVecEnvironment` exists to serialize classes that *point*
`MILLER_SQLITE_VEC_PATH` (`VectorSidecarOpenTests.cs:11`); this class does not, so joining it would add
serialization cost for a hazard it does not have. Documented in the class remarks.

**Timeouts:** `InitTimeout` 15 min, `RequestTimeout` 5 min — far above production defaults, because a
cold machine's first `serve` downloads the model before it can answer `health`, and a gate that flakes
on a slow download proves nothing.

---

## 3. TDD evidence

**Guard extension — genuine red via temporary mutation, both directions, state restored:**

1. Created a throwaway `tests/Miller.Tests/Indexing/TempGuardProbe.cs` calling
   `ScaleTestSupport.RequireSemanticSidecar()` **without** the Scale trait → guard **FAILED**:
   > `These tests spawn a real pinned binary but are MISSING [Trait("Category","Scale")] … Indexing/TempGuardProbe.cs`

   File deleted; guard green again.
2. `sed`-renamed `SemanticLaunchSignals` to a name no test references → guard **FAILED** on the new
   per-family assertion:
   > `The convention guard found NO test referencing the julie-semantic-sidecar launch signal (RequireRenamedSidecarSignal) … Refusing to pass with zero coverage.`

   Restored from the `.bak`; `git diff --stat` confirms the file is the intended 38+/19− edit only.

**Scale tests — run green with the binary present AND skip cleanly when absent, state restored:**

- Present: `Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 13 s`
- `mv .tools/julie-semantic-sidecar .tools/julie-semantic-sidecar.aside` →
  `Skipped! - Failed: 0, Passed: 0, Skipped: 3, Total: 3` (all three, 1 ms each — no process launched)
- Binary moved back; `ls -la .tools/` confirms `julie-semantic-sidecar` + `vec0.dylib` present and
  executable.

---

## 4. Model-cache prerequisite (investigated, not guessed)

`julie-semantic-sidecar --help` is not a verb; the real usage is
`serve [--model <id>] | prepare [--model <id>] | --version`. The binary downloads and verifies its
~1.2 GB GGUF into a shared cache on first use, so a cold first `serve` pays a download before it can
answer `health`. On this machine the cache was already warm (a warm run handshakes and embeds in ~13 s
for all three tests), so **no manual prepare was needed and none was run**. The tests therefore do not
gate on a cache probe — they carry a 15-minute `InitTimeout` that covers a cold download, and the skip
message names `prepare` so a reader on a cold machine knows the cheaper path.

---

## 5. Verification

| check | result |
|---|---|
| `dotnet build Miller.slnx -c Release` | **0 warnings, 0 errors** |
| `scripts/test.sh` (fast suite) | **4153 passed, 2 skipped, 4155 total, 27s** |
| fast-suite growth | **none** — 4155 total before and after; all three new tests are Scale |
| `scripts/test.sh scale` | 36 passed, 46 skipped, **1 failed — not mine, see §6** |
| new Scale class, binary present | **3/3 passed** |
| new Scale class, binary absent | **3/3 skipped** |

Two transient/known issues, both handled per instructions:

- **Foreign compile break (impl-f5).** First Release build failed with three `CS0246: CliSearchArm` errors
  in `src/Miller.Server/Cli/CliDispatch.cs` — f5's in-flight file, explicitly flagged as theirs. Retried;
  green on the first retry. I did not touch it.
- **Known flake (IndexerService leadership).** One fast-suite run failed
  `IndexerServiceScanTests.StartAsync_AsLeader_RecordsLeaderIdentity_AndRemovesItOnStop` under parallel
  load. Retried once as instructed; green (`4153 passed, 0 failed`). The same run tripped the 30s wall-clock
  tripwire at 37s under that load; clean runs are 27s, inside the ceiling.

---

## 6. Pre-existing Scale failure surfaced by G1+G2 — **needs an owner, not mine**

`scripts/test.sh scale` fails one test I did not write and cannot fix under my file ownership:

```
Miller.Tests.Server.VectorConvergePortScaleTests.TryOpen_WithoutThePinnedExtension_ReturnsNullRatherThanThrowing
  tests/Miller.Tests/Server/VectorConvergeServiceTests.cs:111
  Assert.Null() Failure — Actual: SqliteVectorConvergePort { … }
```

**Cause — this is the hand-off caveat, landing on a different file than predicted.** G2's
`Miller.Server.csproj` `Content` items (`ccdc20c`) flow transitively to referencing projects, so
`tests/Miller.Tests/bin/Release/net10.0/.tools/` now contains `vec0.dylib` and
`julie-semantic-sidecar` (verified by `ls`). The test clears `MILLER_SQLITE_VEC_PATH` and expects
`ResolveExtensionPath()` to return null — but the zero-arg overload now falls back to
`AppContext.BaseDirectory/.tools/vec0.dylib`, which exists. The test's premise ("without the pinned
extension") is no longer true in the test output directory.

This is **pre-existing in HEAD**, not caused by my edits: my three files touch neither the csproj nor
`VectorConvergeServiceTests.cs`, and the failure is fully determined by the packaged copy G2 added
plus the `vec0.dylib` G1 restored. Before G1 the wildcard matched nothing and the test passed — the
regression needs *both* commits to surface, which is why G2 did not see it.

Suggested fix (owner's call): point that test at a base directory known to have no `.tools/`, using
G2's own `ResolveExtensionPath(baseDirectory)` overload — the overload exists precisely for this.

The related hand-off worry — `SqliteVecTestSupport.Locate` (`VectorStoreTests.cs:27`) calling the
zero-arg overload — is *benefited* rather than broken: it now resolves the packaged path instead of
falling through to the spike cache, and `VectorStoreTests` is green.

**Also worth knowing:** `.tools/julie-extract` is not restored in this worktree, so 46 Scale tests
skip. Scale coverage of the julie-extract paths is currently unverified here.

---

## 7. Miller usage (directive compliance)

| call | what it bought |
|---|---|
| `context "semantic sidecar embedding session handshake encoder fingerprint vector store quantizer search arm"` | 63-symbol bundle; located `SemanticSearchArm:87/99`, `SemanticEmbeddingSession:133`, `Handshake:147`, `SemanticVectorQuantizer:43`, `SemanticEncoderHandshake:33` in one call |
| `trace RequireJulieServer mode=refs` | 30 references across 8 Scale files — proved the convention-guard coupling and that the signal is referenced only from Scale-tagged classes, so mirroring it was safe |

API shapes were read from source, never assumed: `SemanticEncoderHandshake(Pin, EncoderFingerprint,
Dims, Accelerated, ResolvedBackend, DegradedReason)`, `EnsureStartedAsync`/`EmbedBatchAsync` returning
`SemanticEmbedOutcome(Succeeded, Vectors, FlaggedIndices, FailureReason)`, `VectorBatchEntry(UnitId,
Path, SymbolKind, IsTest, Embedding, EmbedTextHash)`, `CommitBatch(kind, vectors, deletes, metaUpdates,
revision)`, `VectorSidecar(SemanticMode, IVectorFileProbe, IVectorStoreOpener?, SemanticReaderIdentity?)`,
`SemanticSearchArm(workspaceRoot, VectorSidecar, Func<SemanticEmbeddingSession?>)`, and
`SymbolCardInput(SymbolId, Name, Kind, Path, IsTest, Signature?, DocComment?, Container?)`. The
`SqliteVecEnvironment` collection convention was read from `VectorSidecarOpenTests.cs:11` and its three
declaring members before deciding not to join it.

---

## 8. Files changed (ownership respected)

- `tests/Miller.Tests/ScaleTestSupport.cs` — modified
- `tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs` — modified
- `tests/Miller.Tests/Indexing/SemanticSidecarScaleTests.cs` — created

Nothing else staged. `MillerSemanticContract.cs`, `CliDispatch.cs` and the f5 test files were left to
their owners.
