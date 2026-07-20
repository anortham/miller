# P2 pre-merge review fixes

Three verified defects from the codex pre-merge review of the P2 semantic lifecycle. Branch
`worktree-semantic-p2`, worktree `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`.

Miller-first orientation before editing: `inspect VectorSidecar depth=overview`,
`trace TryOpen scope=src/Miller.Indexing/VectorSidecar.cs mode=refs`,
`trace OpenRequired scope=src/Miller.Indexing/VectorSidecar.cs mode=refs`,
`inspect SemanticEmbeddingSession depth=overview`. API-shape evidence that drove the fixes:

- `VectorSidecarFacts.Path` is the classified serving generation and `Identity` carries the
  `SemanticGenerationIdentity` classification approved — both already existed, so the open path had the facts it
  needed and simply was not using them.
- `VectorStore.Identity` is a property on the opened store, so the post-open identity check is a field read, not
  a second meta round-trip.
- `VectorSidecar.OpenRequired` has no production caller yet (`trace` shows only `TryOpen` + tests); the
  `CliDispatch`/`WorkspaceIndexProvider` hits are `SymbolSearchSidecar.OpenRequired`. So Finding 1 is a latent
  defect on the reader path, fixed before it is wired.
- `SemanticEmbeddingSession` exposes `State`, `RestartCount`, and `UnavailableReason`, which is what makes
  "circuit state survived the wake boundary" an assertable fact rather than an inference.

---

## Finding 1 — the open followed the active path, not the classified serving generation

**What changed** (`src/Miller.Indexing/VectorSidecar.cs`, `TryOpen`)

`Classify` may resolve the serving generation to a retained `vectors.gen-<tag>.db` (active missing, or active
built by an encoder this reader cannot interpret). `TryOpen` then opened `PathFor(workspaceRoot)` — the active
artifact — regardless. Two failure modes: a workspace with no active artifact but a ready retained generation
failed to open at all, and an incompatible active artifact could hand back vectors from an encoder
classification had already refused.

The open now targets `facts.Path`, and after the open the store's own `encoder_fingerprint` is compared against
the one classification promised. A mismatch — the TOCTOU a promote between classify and open opens — disposes
the store and refuses with a reason naming both fingerprints, rather than answering from the wrong embedding
space. The failed-open reason now names the serving generation too.

**Tests** (`tests/Miller.Tests/Indexing/VectorSidecarOpenTests.cs`, new file)

Fast (recording opener, no real store):
- `TryOpen_ActiveMissingButRetainedServes_OpensTheRetainedGeneration`
- `TryOpen_ActiveIncompatibleButRetainedServes_NeverOpensTheActiveArtifact`
- `TryOpen_ActiveServes_OpensTheActiveArtifact` (the fix does not invert the ordinary case)
- `TryOpen_FailedOpen_NamesTheServingGenerationInTheReason`

Scale (`VectorSidecarOpenScaleTests`, real sqlite-vec, skip-not-fail via `SqliteVecTestSupport.RequireExtension`):
- `TryOpen_ActiveMissing_ReturnsAUsableStoreFromTheRetainedGeneration` — a real usable store comes back and the
  active artifact is still absent afterwards.
- `TryOpen_ActiveBuiltByAnotherEncoder_ReturnsTheCompatibleRetainedGeneration`
- `TryOpen_GenerationReplacedBetweenClassifyAndOpen_RefusesWithAStatedReason` — an opener that reads meta from
  the classified file but opens a different one models the promote race; the refusal names both fingerprints.

The first two fast tests were confirmed red against the pre-fix implementation (opened `/ws/.miller/vectors.db`
where the retained path was expected).

**Invariant** — the generation that answers a query is the generation classification approved, verified at open
time, not merely the one at a fixed path. A generation this reader cannot interpret still degrades to lexical
with a stated reason and is never rebuilt, deleted, or re-embedded (vectors-v1 §Status vocabulary).

---

## Finding 2 — corruption recovery consumed the only wake

**What changed** (`src/Miller.Server/Hosting/VectorConvergeService.cs`, `OpenPortWithRecovery`)

After `_recoverCorrupt` rebuilt the artifact the method returned null on the reasoning that "the next wake
converges into it". The only production `StampTarget` caller is index convergence, so on a quiet workspace there
is no next wake: the rebuilt artifact sat at `building 0%` until an unrelated source change.

Recovery now falls through to a reopen and returns the live port, so THIS drain continues into the rebuilt
artifact. The reopen sits outside the catch, so a second corruption-shaped throw propagates to the drain's own
retry instead of recovering in a loop.

**Tests** (`tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`)

- `OpenPort_AfterRecovery_ReturnsTheRebuiltGenerationSoThisDrainContinuesIntoIt` — the rebuilt port is returned,
  not null.
- `Drain_CorruptArtifactOnOneWake_ConvergesIntoTheRebuiltGenerationWithNoLaterStamp` — one wake, corruption on
  the first open, and the drain ends with the card committed and the cursor advanced on the rebuilt port. No
  second stamp is involved.
- `OpenPort_RebuiltGenerationAlsoCorrupt_PropagatesRatherThanRecoveringAgain` — the no-loop clause.
- The existing `OpenPort_CorruptArtifact_RecoversTheGenerationAndLeavesSymbolsDbUntouched` kept its corruption
  and `symbols.db`-untouched assertions; its `Assert.Equal(2, opens)` was dropped because the open count encoded
  the defect (recovery stopping at the rebuild) rather than a contract fact.

**Invariant** — a corruption-triggered wake ends with a converged, queryable artifact, and recovery still never
touches `symbols.db` or any sibling generation (vectors-v1 §Corruption recovery).

---

## Finding 3 — the embedding session was created and disposed per wake

**What changed** (`src/Miller.Server/Hosting/VectorConvergeService.cs`)

`_openSession(workspace)` ran on every drain and the session was disposed at drain end, which defeated the
resident-child-process design: model startup was paid per wake, and `RestartCount` / circuit-open state reset
between wakes so repeated sidecar failures could never trip the circuit.

The hosted service now owns one `_session` field, created lazily on the first drain that needs it (never in the
constructor, so the host-lifecycle rule holds — the constructor still reads no `IndexBootstrapService` getter),
kept across wakes, and disposed in `ExecuteAsync`'s `finally`, which `BackgroundService.StopAsync` drives. A
circuit-open session is kept and keeps stating its reason on later drains rather than being replaced by a fresh
one that has forgotten why. Disposal is idempotent: the field is nulled before the await, and
`SemanticEmbeddingSession.DisposeAsync` is itself `_disposed`-guarded. The drain loop is a single `ExecuteAsync`
loop, so no additional synchronization is involved. The private per-wake `DrainAsync(CancellationToken)` became
`internal DrainOnceAsync` so a test can drive exactly one wake.

**Tests** (`tests/Miller.Tests/Server/VectorConvergeServiceTests.cs`)

- `Drain_AcrossTwoWakes_ReusesOneResidentEmbeddingSession` — the counting session factory is invoked once across
  two wakes.
- `Drain_AcrossTwoWakes_KeepsSidecarFailureStateSoRepeatedFailuresTripTheCircuit` — with a `CrashMidBatch`
  sidecar, `RestartCount` strictly increases across the wake boundary and the circuit reaches `CircuitOpen`,
  which a per-wake session can never reach.
- `Stop_DisposesTheResidentSessionExactlyOnceAndStartsNoOther` — a real `StartAsync` + stamped wake; the session
  is still alive after the drain completes, is `Stopped` with `UnavailableReason == "session disposed"` after
  stop, a second `StopAsync` is a no-op, and no second session was created.
- `OffMode_TheServiceNeverOpensAPortOrLaunchesASession` (existing) still passes: the off-guarantee short-circuit
  is before the loop, so the resident session is never constructed under `MILLER_SEMANTIC=off`.

All five behavioral tests for Findings 2 and 3 were confirmed red against the pre-fix semantics (verified by
temporarily restoring the old bodies behind the new API, then restoring the fix from a patch file — no stash).

**Invariant** — one child process per service lifetime; failure and circuit state accumulate across wakes rather
than resetting; the session is disposed exactly once, at service stop.

---

## Test isolation note

`VectorSidecarOpenScaleTests` and the existing `VectorConvergePortScaleTests` both point the process-global
`MILLER_SQLITE_VEC_PATH` at a located extension. Run in parallel they observe each other's value, which broke
`TryOpen_WithoutThePinnedExtension_ReturnsNullRatherThanThrowing`. Both classes now share the
`SqliteVecEnvironment.Name` xUnit collection so they serialize. This is a test-isolation fix, not a weakened
assertion — the test asserts exactly what it did before.

---

## Verification

| Gate | Result |
|---|---|
| `--filter VectorSidecar\|VectorConverge\|SemanticOffGuarantee\|HostStartupRegistration` | 91 passed, 0 failed |
| `scripts/test.sh` (fast) | 4014 passed, 2 skipped, 0 failed; 28s wall (30s ceiling) |
| `SPIKE_CACHE_DIR=… scripts/test.sh scale` | 78 passed, 0 failed, 0 skipped |
| `VectorSidecarOpenScaleTests` with real sqlite-vec | 3 passed (extension present, so exercised not skipped) |
| `dotnet build Miller.slnx -c Release` | 0 warnings, 0 errors |

Guards green: `SemanticOffGuarantee`, `HostStartupRegistration`, `ScaleTraitConvention`, `AgentInstructions`
(all inside the fast suite). No gate or existing assertion was weakened; the one assertion removed
(`Assert.Equal(2, opens)`) encoded the Finding 2 defect.

The first `scripts/test.sh` run reported 47s and tripped the wall-clock tripwire; that run included a cold
Release-to-Debug rebuild. The steady-state run is 28s wall / 23s test time, and the new tests contribute 120ms
across 28 tests.
