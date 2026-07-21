# Task 1 Report — Encoder pin registry + `MILLER_SEMANTIC_MODEL` swap seam

## Status
DONE. commit SHA: none - parallel-lead-commit.

## What I implemented
A registry + selection seam so swapping the embedding model is one env var, built on top of the existing
`MillerSemanticContract` (no new abstraction layers, no decorator).

- `MillerSemanticContract.KnownEncoders : IReadOnlyList<SemanticEncoderPin>` = `[DefaultEncoder, FallbackEncoder]`
  (qwen3 first, bge-small second).
- `MillerSemanticContract.FindEncoder(string modelId) : SemanticEncoderPin?` — exact ordinal `ModelId` match over
  `KnownEncoders`; null-safe (a null argument returns null, preserving the old `FindPin` behavior).
- `SemanticEncoderResolution(SemanticEncoderPin Pin, string? UnknownModelId)` — the pure resolution outcome;
  `UnknownModelId` is the single signal that a fallback-to-default warning is warranted.
- `SemanticEncoderSelection` (same file, `Miller.Indexing.Semantic`):
  - `EnvVar = "MILLER_SEMANTIC_MODEL"`.
  - `Resolve(string? raw) : SemanticEncoderResolution` — pure, side-effect free. Unset/empty/whitespace →
    `DefaultEncoder` (no unknown); trimmed exact match → that pin; unrecognized → `DefaultEncoder` + `UnknownModelId`.
  - `Active` / `FromEnvironment()` → the process-wide pin, resolved **once** via a `Lazy` reading `MILLER_SEMANTIC_MODEL`,
    so the fallback warning fires at most once for the process lifetime (matches "read once").
  - `ResolveAndWarn(string? raw, Action<string> warn)` — internal seam: resolves and emits exactly one warning when
    the value is unrecognized. The `Lazy` uses it with `Console.Error.WriteLine` as the default sink.
- Threaded the active pin to every prior `DefaultEncoder` hard-code:
  - `VectorSidecar` — added `Encoder : SemanticEncoderPin` (defaults to `SemanticEncoderSelection.Active`, injectable via
    the existing internal ctor's new `encoder` param). `Reader` now derives from `Encoder` via a private
    `ReaderIdentityFor` helper (replaced the static `DefaultReader`, which had a single internal reference), so the
    reader fingerprint tracks the active encoder (old :207 fingerprint site).
  - `VectorConvergeService` :591 and :1139 — `PinnedIdentity(MillerSemanticContract.DefaultEncoder[, …])` →
    `PinnedIdentity(SemanticEncoderSelection.Active[, …])`.
  - `SemanticEmbeddingSession.MatchEncoder` — `FindPin(...)` → `MillerSemanticContract.FindEncoder(...)`; deleted the
    now-redundant private `FindPin`.

## `semantic prepare` trace (no CliDispatch edit needed)
Traced `semantic prepare` in `CliDispatch`: `CliDispatch.Semantic` (:327) parses `--model` and constructs
`SemanticPrepareRequest(o.Value("model"), o.Has("json"))`, then `SemanticPrepareCli.Run` →
`BuildArguments` (:143) passes the raw `--model` string straight through to the pinned sidecar's `prepare` subcommand
(sidecar owns download mechanics). **Neither path references `DefaultEncoder`** — the "fix if hard-coded" condition is
not met, so I left `CliDispatch.cs` untouched (also respecting Task 2's territory). Defaulting an unset `--model` to the
active pin's `ModelId` would be new download-selection behavior, which the task explicitly excludes ("No new download
logic"); not done.

## Verification
- **worker-red-green** — invariant: the selection API resolves the right pin and the swap classifies as a shadow rebuild.
  `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~SemanticEncoderSelectionTests"` →
  **Passed 16/16, 0 failed** (2026-07-21). Confirmed red first (CS0117/CS0103/CS1739 for the missing APIs), then green.
- **worker-ceiling** — invariant: my change leaves the fast suite green and pure.
  `scripts/test.sh` → **4255 passed, 2 skipped, 1 failed**. The single failure is
  `Miller.Tests.Core.CanaryGateMathTests.StudentTCritical_MatchesPublishedTableValues(df:4)` — a Student-t numerical
  tolerance issue in an **untracked sibling-worker file** (`tests/Miller.Tests/Core/CanaryGateMathTests.cs` +
  `src/Miller.Core/Telemetry/`, neither in my ownership) with zero dependency on encoder selection. Isolating my feature's
  surface — `dotnet test --filter "…SemanticEncoderSelectionTests|…MillerSemanticContractTests|…VectorSidecar|…SemanticEmbeddingSessionTests"`
  → **124 passed, 1 skipped, 0 failed** (2026-07-21).
- **Build diagnostic** — invariant: 0 warnings / 0 errors (warnings are errors).
  `dotnet build Miller.slnx -c Release` → **Build succeeded, 0 Warning(s), 0 Error(s)** (2026-07-21).

## Files changed
- `src/Miller.Indexing/Semantic/MillerSemanticContract.cs` — added `KnownEncoders`, `FindEncoder`,
  `SemanticEncoderResolution`, `SemanticEncoderSelection`.
- `src/Miller.Indexing/VectorSidecar.cs` — added `Encoder`; `Reader` derives from it; removed static `DefaultReader`.
- `src/Miller.Indexing/Semantic/SemanticEmbeddingSession.cs` — use `FindEncoder`; deleted `FindPin`.
- `src/Miller.Server/Hosting/VectorConvergeService.cs` — two pinned-identity sites use `SemanticEncoderSelection.Active`.
- `tests/Miller.Tests/Indexing/SemanticEncoderSelectionTests.cs` — new.

## Miller calls used (API-shape evidence)
- `context(query="semantic encoder pin generation identity DefaultEncoder …")` — located seeds `DefaultEncoder`/`FallbackEncoder`
  (:94/:105), `FindPin` (:685), `PinnedIdentity`, and the contract test suite.
- `inspect(MillerSemanticContract, depth=full)` — proved the pin values, `PinnedIdentity`, `EncoderFingerprint`,
  `CanonicalEncoderString`, and `ClassifyChange` (EncoderFingerprint|StorageSchema change ⟹ `ShadowRebuild`); confirmed I
  must not touch pin values / `CanonicalEncoderString`.
- `inspect(SemanticEncoderPin, depth=full)` — proved the 9-field record shape (ModelId, …, StorageSchema).
- `inspect(FindPin, depth=full)` — proved the exact body to generalize into `FindEncoder`.
- `trace(DefaultEncoder, mode=refs)` — proved the complete production call-site set: `SemanticEmbeddingSession` :686/:687,
  `VectorSidecar` :207, `VectorConvergeService` :591/:1139 (rest are tests). Drove the guard test.
- `trace(DefaultReader, mode=refs)` — proved `DefaultReader` had a single internal reference (:198), safe to replace.
- `inspect(SemanticActivation, depth=full)` — mirrored its `EnvVar` + `FromEnvironment`/pure-mapping split.
- `inspect(IVectorFileProbe, depth=full)` — proved the probe interface (`FileExists`, `EnumerateRetainedGenerations`) for the test stub.
- `inspect(src/Miller.Server/Cli/SemanticPrepareCli.cs)` + `search(mode=source, "semantic prepare")` — proved the prepare
  path passes `--model` through with no `DefaultEncoder` reference.

## Acceptance criteria
- [x] `MILLER_SEMANTIC_MODEL=bge-small-en-v1.5-f32` resolves the bge pin; its `PinnedIdentity` differs from qwen3's
  (fingerprint AND storage schema) and `ClassifyChange` yields `ShadowRebuild`
  (`SelectingTheFallbackPin_ClassifiesAsAShadowRebuildAgainstTheDefault`).
- [x] Unset/unknown env values resolve `DefaultEncoder`; unknown logs one warning
  (`Resolve_*`, `ResolveAndWarn_*` tests — one warning for unknown, zero for known/unset).
- [x] `VectorConvergeService`, `VectorSidecar`, and the `semantic prepare` path all consume the resolved pin (prepare never
  referenced `DefaultEncoder`); no remaining direct `DefaultEncoder` reads outside `MillerSemanticContract` and tests —
  guarded by `NoProductionSiteReadsDefaultEncoderDirectlyOutsideTheContractFile` (source-scan of `src/**`, 50+ files).
- [x] Worker-scope verification passes; handed to lead per parallel-lead-commit (no git add/commit).

## Self-review
- Completeness: all acceptance criteria met; every prior `DefaultEncoder` hard-code routed through the seam and enforced by
  a guard test.
- Quality: `Resolve` is pure/testable; process-wide-once via `Lazy`; warning seam injectable so the "one warning" behavior is
  asserted without env mutation or real I/O. `Reader` derives from `Encoder` so an injected encoder can never diverge from the
  reader fingerprint. Zero code/test narration comments; only API doc comments added.
- Discipline: no pin-value or `CanonicalEncoderString` changes (existing artifact fingerprints unmoved); no new MCP tool; no
  new files outside the owned set (selection types live inside `MillerSemanticContract.cs`).

## Judgment calls
- `src/Miller.Server/Cli/CliDispatch.cs` — chose NO edit over editing the prepare call site, because `Semantic`/`SemanticPrepareCli`
  pass `--model` through and never read `DefaultEncoder`; the conditional-edit trigger is unmet and prepare-default selection would
  be out-of-scope new download logic.
- `MillerSemanticContract.cs:117` area — placed `SemanticEncoderSelection`/`SemanticEncoderResolution` inside this file rather than
  a new file, because the owned-file set excludes new files and the guard's "outside MillerSemanticContract" exemption is file-scoped.
- `VectorSidecar.cs:206` — removed static `DefaultReader` (single internal ref) and derived `Reader` from the new `Encoder`, over
  keeping `DefaultReader` as a second source of truth, so the reader fingerprint always matches the active encoder.
- Warning sink — `Console.Error.WriteLine` default, because Miller.Indexing has no logging framework (no Serilog/ILogger package)
  and stderr is the safe channel for the MCP stdio server (stdout is the protocol); it is injectable for tests.

## Issues / concerns
- Fast-suite failure `CanaryGateMathTests.StudentTCritical_MatchesPublishedTableValues(df:4)` is a **sibling worker's untracked
  file**, not mine — flagged for the lead; it does not gate this task.
