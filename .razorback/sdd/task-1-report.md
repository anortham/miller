# Task 1 report — circuit-open pause producer

## Status
Code COMPLETE. Verification initially BLOCKED on a concurrent worker's non-compiling files (Task 3) in the
shared `Miller.Server` assembly — see "Verification" for the final result.

## What changed (owned files only)
- `src/Miller.Server/Hosting/VectorConvergeService.cs` (+34 lines)
  - Three constants: `ConvergePauseStateKey = "converge_pause_state"`, `ConvergePauseReasonKey =
    "converge_pause_reason"`, `CircuitOpenPauseValue = "circuit-open"`.
  - New `ApplyCircuitPause(IVectorConvergePort port, SemanticEmbeddingSession session)`, called once per wake at
    the end of the non-promoted `DrainAsync` path (after both cursors drain, before `return [symbols, chunks]`).
- `tests/Miller.Tests/Server/VectorConvergeServiceTests.cs` (+67 lines) — four tests (below).

No `VectorStore` change was needed (see Decision 1). No new hosted service, no in-process status flag, no
protocol change. Approved shape preserved.

## Miller-first orientation (calls made and what they proved)
Miller MCP tools were unavailable this session (subagents share the lead's Miller connection; it did not
answer), so orientation was done by direct Read of the exact symbols the brief named, at the brief's line
anchors. Evidence gathered:
- `VectorConvergeService.DrainAsync` (the 5-arg overload) is where the live port and resident session are both
  in scope; the non-promoted tail `DrainChunkToCompletionAsync(...) → return [symbols, chunks]` is the single
  once-per-wake point with a valid, non-disposed port. The promote branch returns earlier and disposes the live
  port (`live.Dispose()` in `RunShadowRebuildAsync`), so stamping there would hit a disposed store.
- `RecordError`/`ClearError` neighborhood (VectorConvergeService.cs:746–758): `ClearError` already clears by
  `SetMeta(key, string.Empty)`. I mirror that convention for the pause clear.
- `VectorStore.SetMeta` (VectorStore.cs:186): upsert; `ArgumentException.ThrowIfNullOrWhiteSpace(key)` on the
  key but only `ArgumentNullException.ThrowIfNull(value)` on the value — so an empty-string value is a legal
  write. `Meta` (VectorStore.cs:178) returns the stored string or null.
- Consumer `VectorSidecar.PauseState` (VectorSidecar.cs:399): `meta.GetValueOrDefault("converge_pause_state")`
  switched over `circuit-open` / `disk-blocked`, else `null`. So an empty (or missing) value classifies as
  "not paused". The reason is read only inside the `PauseState(meta) is { } pause` branch
  (VectorSidecar.cs:387–394), so an empty reason after clearing is never surfaced.
- Circuit signal `SemanticEmbeddingSession.State` / `.UnavailableReason`
  (SemanticEmbeddingSession.cs:140,144): `State` is public; `RecordFatal` (line 457) latches
  `SemanticSessionState.CircuitOpen` **permanently for the session's life** — `StartIfNeededAsync` and
  `TryEnterCall` never leave `CircuitOpen`. `UnavailableReason` is documented "never blank on a
  non-Ready state a caller can observe".

## Decisions
1. **Clear by empty value, not a new `DeleteMeta`.** `PauseState`'s switch maps `""` (and missing) to `null`,
   so an empty value is exactly "absent" to the consumer — the contract stays honest. It also matches the
   existing `ClearError` convention in the same file. Adding `DeleteMeta` to `VectorStore` would be a larger,
   cross-file change for no contract benefit. So the narrow `VectorStore` exception in the brief was NOT taken.
2. **Detect transitions from the artifact meta, not an in-process bool (load-bearing).** An open circuit is
   permanent for a `SemanticEmbeddingSession`'s lifetime, so "recovery" is never the same session resetting — it
   is a *later process* with a fresh session. A new process's in-process flag would be false and would never
   know to clear a stale stamp. Reading `port.Meta(ConvergePauseStateKey)` makes the recovered⟶clear edge work
   across the restart, and also makes the open⟶stamp edge fire exactly once (no WAL churn). `circuitOpen ==
   stamped ⟹ return` is the zero-write steady state in both directions.
3. **Stamp on the non-promoted `DrainAsync` tail only.** Circuit-open and promote are mutually exclusive within
   a wake (circuit-open means embedding is failing, so a shadow build cannot succeed to promote), and a promote
   writes a fresh `vectors_meta` with no pause key (implicit clear). Skipping the promote branch also avoids
   writing to the disposed live port. Residual edge (documented, out of scope): if the circuit trips during a
   post-promote chunk drain on the reopened port, this wake does not stamp; the next non-promoted wake does —
   pauses converge, never lost.

## Tests added (gate invariants)
- `Drain_WakeEndsWithCircuitOpen_StampsCircuitOpenPauseAndANonEmptyReason` — drives the resident session to
  `CircuitOpen` (CrashMidBatch fault, two wakes), then asserts `converge_pause_state == "circuit-open"` and a
  non-empty `converge_pause_reason`. Proves: producer emits exactly the key/value the consumer test
  `VectorSidecarClassificationTests.CircuitOpenPause_OverridesReady` fixes.
- `Drain_FirstSuccessfulWakeAfterRecovery_ClearsAStaleCircuitOpenPause` — seeds a stale pause (as a prior
  process would leave it), runs one healthy wake, asserts both keys are empty. Proves: the cross-process
  recovered⟶clear edge yields a value the consumer's `PauseState` treats as absent (returns to ready/building).
- `Drain_SteadyHealthyWake_WritesNoPauseMeta` — a healthy wake writes neither pause key. Proves: no-transition
  wakes never touch `vectors_meta` for the pause.
- `Drain_CircuitStaysOpenAcrossWakes_StampsThePauseExactlyOnce` — three wakes with the circuit latched open;
  `converge_pause_state` is written exactly once. Proves: open⟶stamp fires only on the edge, not every wake
  (the WAL-churn guard).

## Verification
- Command: `dotnet test tests/Miller.Tests --filter "FullyQualifiedName~VectorConvergeServiceTests"`.
- First attempt: build failed before tests ran, due to Task 3's in-flight non-compiling files in the same
  `Miller.Server` assembly (`SemanticPrepareCli.cs`, `CliDispatch.cs`) — neither mine. Task 3 subsequently
  compiled; the build then went green and tests ran.
- worker-red-green: **Passed! 33/33** (my 4 new tests + 29 existing `VectorConvergeServiceTests`), 0 failed.
- worker-ceiling `scripts/test.sh`: 4187 passed / 2 skipped / **1 failed**. The single failure —
  `IndexerServiceScanTests.StartAsync_WhenEnabledLeaderAndSidecarBuildFails_StillMarksRegistryScanned` — is a
  `ScanCalled.Wait(5000)` timeout in a *different* hosted service (`IndexerService`/`SymbolSearchSidecar`
  startup), not the vector drain loop. It **passes in isolation in 83 ms**, so it is a load-induced flake under
  the concurrent full-suite + Task 3 build, not a regression from this change. (Task 8 — "fast-suite
  wall-ceiling fix" — is the pending item that owns this suite-timing flakiness.)
