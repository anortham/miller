# Task B5 report — Shadow generations, promote, rollback, corruption recovery

Branch `worktree-semantic-p2`, worktree `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`.
Tree was clean at start (HEAD `0c3cd55`); no other workers in flight. No push (2026-07-20 no-push directive).

## What was implemented

**`src/Miller.Indexing/Semantic/VectorGenerationManager.cs` (new).** The generation lifecycle of vectors-v1
§Shadow generations and rollback, split into pure decisions and a thin IO edge:

- **Naming.** `ActivePath` = `.miller/vectors.db`, `ShadowPath` = `vectors.db.rebuild` (fresh sibling ⟹ same
  filesystem ⟹ atomic rename), `RetainedPathFor(tag)` = `vectors.gen-<tag>.db`, and `TagFromRetainedPath` as
  the inverse the reader-side discovery probe needs. Tags come from `MillerSemanticContract.GenerationTag`;
  nothing re-derives the composition.
- **`ClassifyPromote`** (pure, string-tag and identity overloads). Incompatible exactly when the tag differs.
  A `corpus_generation` / `reader_compatibility` / `fusion_profile` change therefore retains nothing — which is
  also how the B2 lead-accepted `writer_version`-only ⟹ `ReaderGate` ruling lands here: a reader-gate change
  never produces a retained file, because no reader could prefer the superseded one.
- **`Promote`** — retain-then-promote under one lock hold. Both renamed files are made self-contained first
  (fold WAL, delete `-wal`/`-shm`); an existing file at the retain target (a tag returning after a revert) is
  deleted first; `SqliteConnection.ClearAllPools()` runs before the promote rename, exactly as
  `FullRebuildPromotion.Promote` does. A retain failure propagates **before** the promote, leaving the active
  artifact untouched and the shadow in place for the next attempt.
- **`PlanGarbageCollection`** (pure) / **`CollectGarbage`**. The three never-delete rules are absolute, checked
  in order: active-not-ready ⟹ `OnlyReadyGeneration`; inside the soak window ⟹ `WithinSoakWindow`; a tag with a
  known live reader ⟹ `LiveReader`. Everything else is `Deleted`, oldest retention time first. GC also reclaims
  a stale shadow trio and **never** targets `vectors.db`.
- **`EvaluateBuildState`** (pure) — the `build_state` / `build_progress_percent` transition that makes a
  converged generation queryable (B4 handoff 2).
- **`ClassifyArtifact`** — pure path classification into `Active` / `Retained` / `Shadow` / `Unknown`, the
  policy input for per-generation corruption recovery. It never stats the filesystem, so it is off-guarantee safe.
- **IO edge.** `IVectorGenerationFiles` (internal seam) + `SystemVectorGenerationFiles`, whose deletes and moves
  retry on `FileOperationRetryOptions.Default` — the same `MILLER_PROMOTE_RETRY_TIMEOUT` policy as the extract
  artifact, as the contract requires — and whose retained-generation enumeration delegates to B1's
  `SystemVectorFileProbe.EnumerateRetainedGenerations` rather than parallel-pathing it.

**`src/Miller.Indexing/Semantic/VectorStore.cs` (B4 handoff 1).** Added the batch-commit surface and the reads
the writer needed: `CommitBatch(kind, vectors, deletes, metaUpdates, revision)` — one short transaction holding
vec0 deletes, vec0 inserts, mapping rows and the meta/cursor advance — plus `AllMeta()`, `ReadIdentity()`,
`MappedUnits()`, `MappedCount()`, and the `VectorBatchEntry` / `VectorMapEntry` records.

**`src/Miller.Server/Hosting/VectorConvergeService.cs` (sanctioned fold-in only).**
`SqliteVectorConvergePort` now holds a `VectorStore` instead of a raw `SqliteConnection`. Deleted from that
file: `OpenVectors` (connection-open + `vec_version()` verification), `NormalizeVecVersion`, `ReadAllMeta`,
`IdentityFrom` (the meta→identity projection), `SetMeta(transaction,…)`, `NextRowId`, `ResolveRowId`,
`Execute`, `VectorLiteral`, `Blob`, and the three table-name helpers — the ~40 duplicated lines B4 flagged, plus
their now-orphaned support. `IVectorConvergePort` is unchanged, so B4's fake-port tests were untouched.
`Commit` additionally composes the `build_state` transition into the same transaction.

**`src/Miller.Server/Hosting/SidecarCorruptionRecovery.cs`.** New
`TryRecoverCorruptVectorGeneration(failure, artifactPath, rebuild, logger)` registers vectors through the
existing pattern: it reuses `IsSidecarCorruption` and the delete-then-rebuild body, and applies the
per-generation policy — active and shadow are deleted **and** rebuilt, a retained generation is deleted and
**not** rebuilt (historical file, not a convergence target), and a non-vector path is refused. Because recovery
only ever deletes the one path it is given, sibling generations and `symbols.db` are untouched by construction —
proven by test rather than asserted.

## Judgment calls

1. **The `build_state` transition is keyed on the symbol cursor, and never regresses.** Requiring *both* cursors
   caught up would leave `build_state = building` forever in a workspace with no `content.db` (chunk target
   stays 0), so a converged artifact would never become queryable. The rule is: ready once the symbol cursor has
   caught up with a target it was actually given (`completed > 0 && completed >= target`), and once ready it
   stays ready. Later lag is `ready (updating; N files pending)` in the status vocabulary — an explicitly
   separate concern from `building 42% (not queryable)`.
2. **The transition is applied inside the port's `Commit`, not the drain loop.** `Commit` is the single
   chokepoint that already owns the one short transaction, and it is one of the lines the fold-in replaced. The
   alternative — editing the drain loop — is outside my ownership. Cost: the readiness meta is written on every
   commit rather than once; it is two rows in a transaction that is already open.
3. **"Across a process restart" is proven by fresh handles, not a child process.** The Scale test builds both
   generations, disposes every store, promotes, then constructs a brand-new `VectorSidecar` and opens the
   retained file through a fresh `VectorStore`. Nothing survives from the build phase, so discovery genuinely
   goes through the named sibling file. Clause 6 constrains *discoverability across restarts*, not the OS
   process boundary; a literal child process would prove nothing extra here and would cost Scale wall time.
4. **The retention cap is reported, not enforced.** The three never-delete rules are absolute, so a protected
   generation is never deleted to satisfy the cap. `VectorGcPlan.OverRetentionCap` surfaces the condition
   instead — a status fact for B6 rather than a rule that could delete the file a live reader is serving from.
   Defaults: `DefaultSoakWindow` 24h, `DefaultRetentionCap` 2. The contract pins neither; both are inputs on
   `VectorGcInputs` so policy can move without touching the rules.
5. **`ClassifyArtifact` lives on the manager, in `Miller.Indexing`.** `SidecarCorruptionRecovery` is
   `Miller.Server`-internal, so the policy has to be readable from the layer that owns the lifecycle;
   `Miller.Server` already depends on `Miller.Indexing`, never the reverse.
6. **`RetainedGeneration.RetainedAt` comes from file mtime**, exactly as the contract states ("measured from its
   retention time (file mtime)") — a rename preserves it, so a retained file's mtime is its retention time.

## Verification

| Scope | Invariant proven | Command | Result |
|---|---|---|---|
| Naming | `vectors.gen-<tag>.db` / `vectors.db.rebuild` composition and the tag round-trip; the active and shadow paths are never read as retained | `--filter ~VectorGenerationManager` | passed |
| Promote kind | Tag change ⟹ incompatible; corpus/reader-gate/fusion-only change ⟹ compatible; no active generation ⟹ compatible | ″ | passed |
| Promote mechanics | Incompatible retains the superseded file under **its own** tag then promotes; compatible overwrites and retains nothing; both renamed files are self-contained (WAL folded, `-wal`/`-shm` gone); a returning tag replaces the existing retained file (one file per tag); **retain failure ⟹ active untouched, shadow left as `.rebuild`**; no shadow ⟹ refuses; no active ⟹ promotes without retaining | ″ | passed |
| GC rule 1 | Never deletes the only ready generation (active absent/not ready ⟹ everything off-limits regardless of soak) | ″ | passed |
| GC rule 2 | Never deletes a generation inside its soak window | ″ | passed |
| GC rule 3 | Never deletes a generation with a known live compatible reader | ″ | passed |
| GC other | Eligible generation past soak is deleted, oldest-mtime first; over-cap is reported without overriding a protection; **never targets `vectors.db`**; a stale shadow trio is reclaimed | ″ | passed |
| `build_state` | Stays `building` with a percent until the symbol cursor catches up; flips to `ready` at catch-up; an unstarted cursor is never ready; never regresses once queryable | ″ | passed |
| Corruption recovery | Corrupt `vectors.db` ⟹ deleted + rebuilt with the retained sibling **and `symbols.db` intact**; corrupt retained ⟹ deleted, never rebuilt, `vectors.db` intact; corrupt shadow ⟹ deleted, build restarts; a non-vector path is refused | `--filter ~SidecarCorruptionRecovery` | passed |
| Worker scope | — | `--filter "~VectorGenerationManager\|~VectorStore\|~VectorConverge\|~SidecarCorruptionRecovery"` | **95 passed, 0 failed (147 ms)** |
| Guards | `HostStartupRegistrationTests`, `SemanticOffGuaranteeTests`, `ScaleTraitConventionTests`, `AgentInstructionsTests`, `RegistryIsolationConventionTests` | `--filter …` | **62 passed, 0 failed** |
| Scale (real sqlite-vec 0.1.9) | Incompatible promote leaves the old generation discoverable and queryable by an old-fingerprint reader through fresh handles; the new generation serves from `vectors.db`; `CommitBatch` round-trips vectors + deletes + meta | `SPIKE_CACHE_DIR=… --filter "Category=Scale&(~VectorGenerationManager\|~VectorStore\|~VectorConverge)"` | **17 passed, 0 failed** |
| Scale suite | Whole scale suite incl. julie-extract paths | `SPIKE_CACHE_DIR=… scripts/test.sh scale` | **75 passed, 0 failed (20 s)** |
| Scale skip path | Skips, never fails, without the extension | `env -u MILLER_SQLITE_VEC_PATH SPIKE_CACHE_DIR=/nonexistent … ~VectorGenerationManagerScale` | **2 skipped, 0 failed** |
| Fast suite | Whole fast suite + wall budget | `scripts/test.sh` | **3954 passed / 2 skipped, 23 s run, 29 s wall** |
| Build | 0 warnings / 0 errors | `dotnet build Miller.slnx -c Release` | **Build succeeded** |

The extension was fetched locally for the Scale runs with
`SPIKE_CACHE_DIR=$CLAUDE_JOB_DIR/tmp/vec scripts/spike-sqlite-vec.sh` (10/10 stages PASS, sqlite-vec `v0.1.9`).

Wall-clock note: the fast suite ran 29 s against a 30 s ceiling. My additions are 31 pure-logic fast tests
costing ~40 ms; the headroom problem is the pre-existing load-sensitive variance B4 investigated and documented
(21 s–56 s spread at `b7cfc7a`), not this slice.

## Miller calls used

| Call | What it confirmed |
|---|---|
| `context "vectors.db shadow generation promote rollback GC corruption recovery"` | The four contract anchors (`vectors-v1.md` §428 Shadow generations, §554 Corruption recovery, §464 Lifecycle, §259 Escalation) and that no existing Miller symbol already owned generation lifecycle — this is new surface, not a duplicate |
| `trace VectorStore refs` | Every consumer before I widened it: `VectorSidecar`'s two opener methods, `SqliteVectorConvergePort.TryOpen`, and the test classes — so the fold-in's blast radius was known before the first edit |
| `inspect CommitBatch depth=overview` | After the fold-in: `SqliteVectorConvergePort.Commit` is the **only** production caller, so the batch surface has exactly one production seam and the port kept its atomicity invariant |
| `trace TryRebuildCorruptSidecar refs` | The existing registration pattern reaches production only through `IndexerSidecarConverger` (`:40`) for search.db/content.db — which is what pinned the vectors call site as a wiring concern outside my owned files rather than something I had silently skipped |

## API-shape evidence

- Generation-tag composition, `vectors.gen-<tag>.db` filename, and the deliberate exclusion of
  `corpus_generation`/`reader_compatibility`/`fusion_profile`: vectors-v1 §Generation tag (lines 436–450); the
  hash itself is `MillerSemanticContract.GenerationTag` (B2), not re-derived.
- Compatible vs incompatible retention table: vectors-v1 lines 452–462.
- Lifecycle steps 1–7 verbatim — shadow at `vectors.db.rebuild`, self-containment before rename, retain-then-
  promote ordering and its failure atomicity, discovery order, rollback-is-do-not-GC, and the three never-delete
  GC rules: vectors-v1 lines 464–518. Conformance clause 6: lines 630–633.
- Promote-retry policy, `ClearAllPools` before the rename, WAL-fold-before-move: `FullRebuildPromotion`
  (`src/Miller.Indexing/FullRebuildPromotion.cs:103–145`) and `FileOperationRetryOptions` (`:12`), reused rather
  than re-specified.
- `build_state` / `build_progress_percent` semantics and the `ready` vs `ready (updating; N files pending)` vs
  `building 42% (not queryable)` split: vectors-v1 §`vectors_meta` (line 329) and §Status vocabulary (586–595).
- Per-generation recovery policy (active rebuilt, retained deleted-not-rebuilt, shadow restarted, `symbols.db`
  never touched): vectors-v1 §Corruption recovery lines 554–576.
- Retained-generation enumeration seam and the off-guarantee that forbids it: `VectorSidecar.RetainedGenerations`
  / `IVectorFileProbe` (`src/Miller.Indexing/VectorSidecar.cs:14, 246`).
- Existing corruption-recovery registration shape: `SidecarCorruptionRecovery.TryRebuildCorruptSidecar` and its
  `IndexerSidecarConverger` call site.

## Files changed

Created: `src/Miller.Indexing/Semantic/VectorGenerationManager.cs`,
`tests/Miller.Tests/Indexing/VectorGenerationManagerTests.cs`, this report.
Modified: `src/Miller.Indexing/Semantic/VectorStore.cs`,
`src/Miller.Server/Hosting/SidecarCorruptionRecovery.cs`,
`src/Miller.Server/Hosting/VectorConvergeService.cs` (fold-in only),
`tests/Miller.Tests/Server/SidecarCorruptionRecoveryTests.cs`.

Commit: `8ecabc1` — `feat(semantic): shadow generations, promote, rollback, corruption recovery (P2 B5)`.

## Concerns

### What B6 needs from me (status facts + the build_state transition)

1. **`build_state` now flips to `ready`.** B4's handoff 3 is closed: the commit that catches the symbol cursor
   up stamps `build_state=ready` / `build_progress_percent=100` in the same transaction, so
   `VectorSidecar.Classify` starts returning `ready` instead of `building` and the semantic arm becomes
   offerable. B6's compact line should read `build_progress_percent` for the `building 42% (not queryable)`
   form and derive `ready (updating; N files pending)` from the cursor lag, **not** from `build_state`.
2. **Generation / soak / GC status facts are ready to render.** `VectorGenerationManager.Retained()` returns
   `RetainedGeneration(Tag, Path, RetainedAt)` newest-first; `PlanGarbageCollection` returns a
   `VectorGcDecision` per generation with a `VectorGcOutcome` (`Deleted` / `OnlyReadyGeneration` /
   `WithinSoakWindow` / `LiveReader`) plus `OverRetentionCap`. Per vectors-v1 §Status vocabulary the serving
   generation's tag, whether it is active or retained, and the retained inventory belong in **JSON facts only**,
   never the compact line; the compact line still says `ready` when a retained generation is serving.
3. **`TagsWithLiveReaders` has no producer yet.** The GC rule is implemented and tested, but nothing currently
   registers a live reader against a tag. Until B6 (or a later slice) supplies that set, GC is protected by the
   soak window alone. That is fail-safe — an unregistered reader is never *more* likely to lose its file than
   the soak window allows — but it should be an explicit B6 decision, not an assumption.

### Other

4. **The vectors corruption-recovery call site is unwired.** `TryRecoverCorruptVectorGeneration` is registered
   and tested, but the production trigger belongs where a corrupt open surfaces — `SqliteVectorConvergePort.TryOpen`
   / the drain loop, both inside B4's `VectorConvergeService.cs` beyond the sanctioned fold-in. Per the brief I
   stopped rather than widening the touch. It is a `catch` clause of the same shape as
   `IndexerSidecarConverger.ConvergeSearch:189`. Nothing silently breaks meanwhile: a corrupt artifact already
   classifies `unavailable` with a stated reason.
5. **Promote has no production caller yet either.** `VectorConvergePlanner` surfaces
   `VectorConvergeDecision.ShadowRebuild` and the cursor holds on it (B4); executing that decision means calling
   `PrepareShadow` → build → `Promote` from the drain loop, same file, same boundary. The lifecycle is complete
   and proven end-to-end against real sqlite-vec; only the drain-loop invocation is missing.
6. **No `julie-semantic-sidecar` binary is pinned or packaged** (carried forward from B4 concern 2) — vector
   convergence is still not live end-to-end regardless of this slice.

---

# B5 follow-up — shadow-rebuild execution + corruption-recovery wiring

Lead-sanctioned scope completion on `src/Miller.Server/Hosting/VectorConvergeService.cs` closing follow-up
concerns 4 and 5. Tree was clean at start (HEAD `8ecabc1`); sole worker; no push.

## What was implemented

**Escalation is now executed, not just surfaced.** When `VectorConvergePlanner` returns
`VectorConvergeDecision.ShadowRebuild`, `RunShadowRebuildAsync` runs the lifecycle B5 built: open a fresh shadow
generation (`PrepareShadow` → `VectorStore.Create` at `vectors.db.rebuild`), fill it with the whole symbol
corpus, dispose both ports, then `VectorGenerationManager.Promote`. The cursor advance inside the shadow's
commit is what makes the promoted artifact `ready` — the `build_state` transition from the first commit.

- **`IVectorShadowRebuilder`** is the seam (`OpenShadow` / `Promote`), so escalation execution is testable with
  no sqlite-vec and no files. Production impl `SqliteVectorShadowRebuilder` composes `VectorGenerationManager`
  with the new `SqliteVectorConvergePort.TryOpenAt(workspace, path)` — the existing `TryOpen` is now a one-line
  call into it against the active path, so the shadow and the live artifact are created by identical code.
- **Bounded to one attempt per wake.** A per-drain `DrainState` carries `ShadowAttempted` / `Promoted`. A second
  escalating cursor on the same wake holds with `"a shadow rebuild was already attempted on this wake"` rather
  than starting a second build — escalation can never spin a hot loop, and the drain only runs on a signal.
- **Failure holds the cursor.** Open failure, embed failure, a partially embedded corpus, or a promote failure
  all record the cursor's `*_last_error` / `*_last_error_at` and leave the cursor exactly where it was; the live
  generation stays queryable and the next wake retries. A stale shadow left behind is reclaimed by the next
  `PrepareShadow`.
- **A promote ends the drain.** The live port is disposed before the rename (a promote over an open handle fails
  on Windows), so `DrainAsync` returns after the symbol cursor rather than using a disposed port. The chunk
  cursor converges on the promoted artifact at the next wake.

**Corruption recovery is wired.** `OpenPortWithRecovery` wraps `_openPort` and, on a corruption-shaped failure,
calls `SidecarCorruptionRecovery.TryRecoverCorruptVectorGeneration` — the same shape as
`IndexerSidecarConverger.ConvergeSearch:189`, with the rebuild action reopening the port (which recreates the
artifact). A non-corruption failure still propagates to the drain's own retry rather than being swallowed.

**Incidental de-duplication.** The bounded-batch inference loop is now one `EmbedAsync` helper shared by the
incremental path and the shadow build, replacing a copy that would otherwise have existed twice.

## Judgment calls

1. **The shadow build fills the symbol corpus only.** Stamping a chunk cursor into the shadow would advance it
   past what `content.db` has proven under the four preconditions — the exact thing vectors-v1 §Cursors forbids
   and conformance clause 2 tests. The chunk cursor stays at zero in the shadow and converges through its own
   gate on the promoted artifact. Covered by its own test.
2. **A partially embedded shadow is a failure, not a promote.** The incremental path tolerates poison units
   (leave them unwritten, retry next drain); a shadow generation is the whole corpus by definition, so promoting
   one with missing units would publish a generation with silent recall holes. It holds instead.
3. **`state.Promoted` is set before `Promote`, not after.** If the rename itself throws, the live port is
   already disposed and there is nothing to record an error onto. The contract calls that state recoverable —
   the retained generation is a complete `ready` generation — so the drain logs and returns, and the next wake
   rebuilds. Setting the flag first is what keeps the catch clause from touching a disposed port.
4. **`OpenPortWithRecovery` is `internal`, not folded into `DrainAsync`.** The corruption path needs a real
   workspace and real files to prove "symbols.db untouched"; reaching it through `ExecuteAsync` would need a
   bound `IndexBootstrapService`, which the host-lifecycle rule keeps unavailable in a fast test.

## Verification

| Scope | Invariant proven | Command | Result |
|---|---|---|---|
| Shadow execution | `ShadowRebuild` builds the whole symbol corpus into the shadow, advances its cursor to the target (⟹ `EvaluateBuildState` = `ready`), promotes, disposes both ports, and ends the drain | `--filter ~VectorConverge` | passed |
| Chunk cursor | The shadow build never commits or advances the chunk cursor — it converges through its gate post-promote | ″ | passed |
| Failure holds | A failed `OpenShadow` holds the cursor at its revision, records the last error, leaves the live port undisposed and unpromoted; the second escalating cursor on the same wake reports "already attempted" and `OpenShadow` is called **once** | ″ | passed |
| Embed failure | An unavailable sidecar during the shadow build leaves the live generation unpromoted and the shadow disposed | ″ | passed |
| Recovery wiring | A corruption-shaped open failure invokes recovery, deletes `vectors.db`, runs the rebuild, and leaves `symbols.db` byte-identical | ″ | passed |
| No-rebuilder path | Existing behaviour preserved: the decision is surfaced and nothing is embedded | ″ | passed |
| Worker scope | — | `--filter "~VectorGenerationManager\|~VectorStore\|~VectorConverge\|~SidecarCorruptionRecovery"` | **100 passed, 0 failed (135 ms)** |
| Guards | `HostStartupRegistration`, `SemanticOffGuarantee`, `ScaleTraitConvention`, `AgentInstructions`, `RegistryIsolationConvention` | `--filter …` | **62 passed, 0 failed** |
| Scale suite | Real sqlite-vec + real julie-extract paths | `SPIKE_CACHE_DIR=… scripts/test.sh scale` | **75 passed, 0 failed (19 s)** |
| Fast suite | Whole fast suite | `scripts/test.sh` ×4 | **3959 passed / 2 skipped, 21–23 s** |
| Build | 0 warnings / 0 errors | `dotnet build Miller.slnx -c Release` | **Build succeeded** |

The five new fast tests cost ~5 ms; the suite came in at 21–23 s, slightly better than the 29 s measured in the
first B5 pass.

**One transient fast-suite failure, not reproduced.** The first `scripts/test.sh` run after the implementation
reported `Failed: 1` (3958 passed) with a truncated stack trace; the failing test name did not survive in the
captured output. Four subsequent full-suite runs and five targeted runs of the suspect classes
(`IndexerServiceScan`, `IndexerServiceLeadership`, `VectorConverge` — 88 tests each) were clean. This matches
the load-sensitive 5 s-timeout flake B4 investigated and reproduced at baseline `b7cfc7a`. Reported rather than
assumed away: I did not capture the name, so it is a known-shape flake, not a confirmed one.

## Miller calls used

| Call | What it confirmed |
|---|---|
| `inspect CommitBatch depth=overview` (from the first pass, re-read) | `SqliteVectorConvergePort.Commit` is still the only production caller after the shadow arm was added — the shadow builds through the same commit chokepoint rather than a second write path |
| `trace TryRebuildCorruptSidecar refs` (from the first pass) | The registration's only production call site is `IndexerSidecarConverger:40`, which is the exact `catch` shape `OpenPortWithRecovery` now mirrors for vectors |

## Files changed

Modified: `src/Miller.Server/Hosting/VectorConvergeService.cs`,
`tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`.

## Concerns

1. **The shadow arm has no Scale coverage.** It is proven against fakes; an end-to-end Scale test would need a
   real generation-identity change (e.g. the fallback encoder) driven through the drain against a real
   `symbols.db`. The two halves are each proven against real sqlite-vec — B5's Scale test covers real
   build → promote → retained-generation discovery, and `VectorConvergePortScaleTests` covers real converge —
   but nothing exercises the join. Worth a Scale test in a later slice; noted rather than silently skipped.
2. **`TagsWithLiveReaders` still has no producer** (unchanged from the first pass): GC is protected by the soak
   window alone until B6 supplies the live-reader set.
3. **GC has no caller yet.** `CollectGarbage` is implemented and tested but nothing schedules it, so retained
   generations currently accumulate. It is leader-only work that belongs on the converge path — a natural fit
   for B6 alongside the retained-generation status facts.
4. **No `julie-semantic-sidecar` binary is pinned or packaged** (carried forward): convergence, and therefore
   the shadow arm, is not live end-to-end regardless of this slice.
