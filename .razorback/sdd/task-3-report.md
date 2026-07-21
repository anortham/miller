# Task 3 Report — Semantic query diagnostics (P5 Canary Stage)

> Replaces a stale `task-3-report.md` from a different plan (P4 `miller semantic prepare`). This path
> collides across plans; this content is the P5 Canary Task 3 report.

Status: **DONE**. Commit SHA: none — parallel-lead-commit.

## What I implemented

The measurement layer every canary fact needs. Every consultation of the semantic arm — served,
abstained, or failed — now yields exactly one `SemanticQueryDiagnostics`. No rendered output changed; no
orchestration or telemetry write was added.

New types (in `Miller.Indexing.Semantic`, assembly `Miller.Indexing`):

- `SemanticFallbackKind` enum — mirrors the contract's 13 `fallback_reason` values one-for-one:
  `None, VectorsMissing, VectorsStale, VectorsIncompatible, VectorsBuilding, ModelNotPrepared, CircuitOpen,
  EmbedTimeout, EmbedError, KnnError, DiskBlocked, Disabled, Unknown`.
- `SemanticQueryDiagnostics` record — `(SemanticFallbackKind Fallback, string Backend, bool ColdEmbed,
  long? EmbedMs, long? KnnMs, SemanticGenerationIdentity? Identity, string? FusionProfile)`.

Threading:

- `SemanticQueryResult` gained `Diagnostics { get; init; }` (nullable, default null). Kept the positional
  ctor unchanged so all existing `new(hits, null)` / `Unavailable(reason)` callers still compile. Non-null
  on every result the arm itself returns; null on results synthesized without consulting the arm
  (`Unavailable` factory used by `SemanticTextArm.NotServing`, CLI, rescue).
- `IVectorSearchPort` gained `SemanticGenerationIdentity? Identity => null;` (default surface). Production
  `VectorStoreSearchPort.Identity => store.Identity`. Test doubles inherit the null default → zero behavior
  change, existing callers ignore the new member.
- `SemanticSearchArm.QueryAsync` / `Retrieve` build diagnostics at each abstention site and on the served
  path. Embed RPC and KNN are timed with separate `Stopwatch`es (integer-ms floor via
  `(long)Elapsed.TotalMilliseconds`). Warmth captured **before** the embed
  (`State != Ready || Handshake is null`). Backend from `session.Handshake.ResolvedBackend` on a successful
  embed, `"none"` otherwise. A `KnnError` catch now lives **inside** `Retrieve` so the KnnError row keeps the
  real embed context (backend/warmth/embedMs); the outer catch remains a fail-open backstop.
- `SemanticSymbolFusionArm` gained `LastDiagnostics { get; private set; }`, set right after
  `QuerySymbolsAsync`. When the arm served, it is augmented with `FusionProfile = RrfFusion.FusionProfile`;
  when the arm abstained, the raw arm diagnostics are exposed; when the arm was never consulted (off/shadow
  mode, lexical-only route) it stays null. Exposed on the concrete DI-transient class only — NOT on the
  `ISymbolFusionArm` interface, so `ForcedHybridFusionArm` (CliDispatch, not owned) is untouched.

## Abstention-site → SemanticFallbackKind map

| Site (SemanticSearchArm) | Kind | Signal |
|---|---|---|
| `!_enabled` | `Disabled` | env off |
| `port is null` (artifact gate) | `VectorsMissing` | gate returned null + string; see judgment call |
| `_openSession() is null` (binary missing) | `ModelNotPrepared` | closest available kind |
| embed fail + `State == CircuitOpen` | `CircuitOpen` | session state |
| embed fail otherwise | `EmbedError` | outcome fail |
| dims mismatch (Retrieve) | `VectorsIncompatible` | lane.Dims |
| non-cosine metric (Retrieve) | `VectorsIncompatible` | lane.Metric |
| store fault during KNN | `KnnError` | VectorStoreException |
| served | `None` | — |

Kinds `VectorsStale`, `VectorsBuilding`, `DiskBlocked`, `EmbedTimeout` are defined in the enum (contract
mirror) but not produced from the query arm: staleness/building/disk are the converge/gate layer's facts,
and a timeout is not distinguishable from a transport error without a typed embed outcome the session does
not expose. See judgment calls.

## Verification

- **worker-red-green** — invariant: every abstention site maps to its kind; a served call carries the full
  facts. `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~SemanticQueryDiagnosticsTests"`
  → **Passed 14, Failed 0** (83 ms). 2026-07-21.
- **worker-ceiling** — invariant: no rendered-output regression; P3 determinism + all existing semantic/hybrid
  tests stay green. `scripts/test.sh` → **Passed 4304, Failed 0, Skipped 2** (16 s wall). No Canary* failures.
  2026-07-21.
- **Diagnostic** — invariant: 0W/0E, warnings-as-errors. `dotnet build Miller.slnx -c Release`
  → **Build succeeded, 0 Warning(s) 0 Error(s)**. 2026-07-21.

## Files changed (owned only)

- `src/Miller.Indexing/Semantic/SemanticSearchArm.cs` — enum + diagnostics record; `Diagnostics` on result;
  `Identity` on port interface + production port; diagnostics in QueryAsync/Retrieve; `Abstain` helper +
  `EmbedContext` + `NoBackend`.
- `src/Miller.Server/Tools/SearchRouteExecutor.cs` — `LastDiagnostics` accessor on `SemanticSymbolFusionArm`.
- `tests/Miller.Tests/Indexing/SemanticQueryDiagnosticsTests.cs` — new; table-driven abstention theory +
  served/warmth/pre-embed/embed-failure/fusion facts.
- `src/Miller.Server/Tools/SearchTool.cs` — **not modified.** Diagnostics ride the returned
  `SemanticQueryResult`, so the content arm (`SemanticTextArm`) surfaces them with zero code change; editing
  it would be gold-plating. Its `NotServing` constant correctly carries null diagnostics (arm not consulted).

## Miller calls used + what each confirmed

- `context(query='semantic search arm abstention fallback fusion')` — entry points: SemanticSearchArm,
  SemanticSymbolFusionArm, SemanticTextArm, SemanticQueryResult.
- `inspect SemanticSearchArm/SemanticQueryResult/SemanticSymbolFusionArm/VectorStoreSearchPort depth=full` —
  proved abstention sites, `SemanticQueryResult(Hits, UnavailableReason)` shape + `Served`/`Unavailable`,
  fusion `Fuse` body, and that `VectorStoreSearchPort` already exposes `Lane`/`Tag` (add `Identity` alongside).
- `inspect SemanticEmbeddingSession depth=full` — `State` (`SemanticSessionState`), `Handshake`
  (`SemanticEncoderHandshake.ResolvedBackend`), circuit semantics; `SemanticEmbedOutcome` (only `Succeeded` +
  `FailureReason`, no typed timeout/circuit flag).
- `inspect SemanticSidecarHealth depth=full` — `ResolvedBackend` field origin (`resolved_backend` health key).
- `inspect VectorSidecar.TryOpen depth=full` — the artifact gate returns only a string reason + null port; the
  typed `VectorSidecarFacts.State` is NOT exposed through `VectorSearchPortFactory`.
- `inspect SemanticGenerationIdentity depth=full` — the 6 identity fields incl. `FusionProfile`; `VectorStore`
  exposes `Identity`.
- `trace SemanticQueryResult mode=refs` / `trace IVectorSearchPort mode=refs` — enumerated every consumer
  before changing the public shapes; confirmed init-property + interface-default are additive.
- `MillerServiceRegistration` (read) — confirmed `services.AddTransient<ISymbolFusionArm>` → the fusion arm is
  DI-transient, so per-call `LastDiagnostics` state is safe (matches the approved plan assumption).
- `RrfFusion` (grep; index stale for one inspect) — `FusionProfile = "fusion-v1"` const is the profile the
  fusion arm applies.

## API-shape evidence (proven, not inferred)

- `SemanticSidecarHealth.ResolvedBackend` — real field; `MatchEncoder` copies it into
  `SemanticEncoderHandshake.ResolvedBackend`. Fake sidecar reports `"cpu"` (health `resolved_backend`).
- `SemanticSessionState` — enum `NotStarted, Ready, Restarting, CircuitOpen, Stopped`. Used for warmth
  (`!= Ready`) and CircuitOpen classification.
- `VectorStore.Identity` — `SemanticGenerationIdentity` property; threaded through
  `VectorStoreSearchPort.Identity`.
- Abstention sites — the six `SemanticQueryResult.Unavailable(...)` returns in QueryAsync/Retrieve plus the
  served `new SemanticQueryResult(hits, null)`, all confirmed in the worktree file.
- DI lifetime — `AddTransient<ISymbolFusionArm>` in `MillerServiceRegistration.AddMillerServices`.

## Judgment calls

- `SemanticSearchArm.cs` port-null gate → chose `VectorsMissing` over per-reason classification
  (stale/building/incompatible/disk_blocked) because the gate (`VectorSidecar.TryOpen`) collapses all
  unavailability into a null port + free-text string; the typed `VectorSidecarFacts.State` is not exposed
  through `VectorSearchPortFactory`, and threading it would change the delegate signature and every test
  double (P3 risk). Faithful finer classification belongs where the state already exists, not here.
- `SemanticSearchArm.cs` embed-failure branch → `CircuitOpen` when `session.State == CircuitOpen`, else
  `EmbedError`. Chose not to synthesize `EmbedTimeout` because `SemanticEmbedOutcome` carries only a string
  `FailureReason`; distinguishing a timeout from other transport faults would require fragile string parsing
  (explicitly warned against in project memory) or a typed outcome the session does not yet emit.
- `SemanticSearchArm.cs` no-binary branch → `ModelNotPrepared` over `Unknown` because `Unknown` is documented
  as an instrumentation-bug signal; a missing sidecar binary is a real, expected "embedding capability not
  prepared" state.
- Backend on a failed/abstained embed → `"none"` (contract: backend `none` = no embed executed). `EmbedMs`
  is still reported for a failed attempt (it is a real measurement); Task 5 decides bucket mapping.
- `KnnError` catch moved inside `Retrieve` so the row keeps embed context; the outer QueryAsync catch stays as
  a fail-open backstop mapped to `KnnError`. Existing `AnUnexpectedStoreFailure` test still green (message
  preserved).
- `SearchTool.cs` left unmodified (see Files changed) — plan-consistent minimal option.
- Fusion `LastDiagnostics` exposed on the concrete class only (no new interface), per the approved shape and
  to avoid forcing a change to the non-owned `ForcedHybridFusionArm`.

## Self-review

- All 13 enum values defined (contract mirror); every abstention site covered by a table-driven theory case
  (8 kinds reachable from the arm) + served/warmth/pre-embed/embed-failure/fusion facts.
- Asserts on real values (backend `"cpu"`, non-null EmbedMs/KnnMs, identity value-equality, cold→warm
  transition, `FusionProfile == RrfFusion.FusionProfile`), not just non-null.
- Zero rendered-output change confirmed by the full fast suite (P3 determinism + HybridSearch + SemanticSearchArm
  all green). No P3 test edited.
- No overbuild: one record + one enum, additive threading, one instance accessor. No decorator, no new
  interface, no callback framework.
- Tests carry contract-faithful metadata (real `PinnedIdentity`, real lane schemas, real fake-sidecar
  handshake reporting `resolved_backend=cpu`).

## Issues / concerns

- None blocking. Note for Task 5: `VectorsStale`, `VectorsBuilding`, `DiskBlocked` are enum members with no
  producer in the query arm by design — they are converge/gate-layer facts, not query-time facts. `EmbedTimeout`
  IS now produced (see Fix round 1). Port-null gate stays `VectorsMissing` (the gate exposes only a string
  reason, not the typed `VectorSidecarFacts.State`).

## Fix round 1 — `EmbedTimeout` now producible via the existing typed signal

Lead review found the transport layer already carries the timeout/error distinction typed — it was only
dropped before reaching the arm. Fixed by propagating the existing flag (no string parsing, no
retry/circuit behavior change).

Changes (all owned files):

- `SemanticEmbeddingSession.cs`
  - `SidecarTransportException` gained `bool TimedOut` (ctor `timedOut = false`). Set `true` only at the
    read-null site (`:~525`) from `reader.EndedByTimeout`; the stdin-write catch and every parse/handshake
    throw keep the default `false` (none are response-timeout-caused).
  - `SemanticEmbedOutcome` gained `bool TimedOut = false` (record) and `Fail(string reason, bool timedOut = false)`.
    `Ok` unchanged.
  - `CallAsync` tracks `bool lastTimedOut`, updated from `ex.TimedOut` at both `SidecarTransportException`
    catches (transport exchange + `ReadVectors` parse), and threads it into every post-loop `Fail(...)`
    return. Reports the FINAL attempt's character; a parse fault on the last attempt correctly resets it to
    `false`. Circuit-open returns still carry it, but the arm classifies `CircuitOpen` first, so it never
    masks a circuit.
- `SemanticSearchArm.cs` — embed-failure mapping is now
  `State == CircuitOpen → CircuitOpen; else outcome.TimedOut → EmbedTimeout; else EmbedError`.
- Tests — added `Scenario.EmbedTimeout` to the table-driven theory: a `StallForever` fake with a 300ms
  `RequestTimeout` yields `Fallback=EmbedTimeout`; the existing `Scenario.EmbedError` (ErrorEnvelope) still
  yields `EmbedError`, proving the two are distinguished. No real 30s waits; the fake surfaces the typed flag.

Re-verification (2026-07-21):

- worker-red-green: `--filter FullyQualifiedName~SemanticQueryDiagnosticsTests` → **Passed 15, Failed 0** (676 ms).
- worker-ceiling: `scripts/test.sh` → **Passed 4305, Failed 0, Skipped 2** (18 s). No Canary* failures.
- Diagnostic: `dotnet build Miller.slnx -c Release` → **0 Warning(s) 0 Error(s)**.
