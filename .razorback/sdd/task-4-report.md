# Task 4 report — `downloading` status state (consumer + producer)

> Replaced a stale `task-4-report.md` from a DIFFERENT plan ("sqlite-vec Native-AOT spike"). This path collides
> across plans; the old content is tracked/recoverable in git. This is the P4 semantic Task 4 report.

## Status
Complete. Classification arm, marker consumer, JSON model surfacing, and the DiskPreflight swap all landed and
tested. Commit deferred (parallel-lead-commit — no `git add`/`commit` run).

## Files changed (all owned)
- `src/Miller.Indexing/VectorSidecar.cs` — new `downloading` state, seam extension, marker mirror + parser,
  `DownloadingModel` fact.
- `src/Miller.Server/Cli/SemanticPrepareCli.cs` — DiskPreflight swap (see below).
- `src/Miller.Server/Tools/WorkspaceRender.cs` — JSON `downloading_model` field.
- `tests/Miller.Tests/Indexing/VectorSidecarClassificationTests.cs` — downloading/stale/malformed/precedence/off +
  mirror-constant tests.
- `tests/Miller.Tests/Server/WorkspaceVectorFactsRenderTests.cs` — JSON model-id test.

## Miller-first orientation (calls + findings)
- `inspect VectorSidecar.cs` — confirmed the 9 state constants and the `Classify`/`ClassifyGeneration` precedence
  heart; `PauseState` already yields `circuit-open`/`disk-blocked`, so a pause is never an `unavailable` result.
- `trace VectorSidecarFacts` — mapped consumers: `WorkspaceRender.WriteVectorsJson`/`VectorsLabel` render the
  facts; `WorkspaceVectorFactsRenderTests` pins the vocabulary. Found the render seam **already** carries a
  `Compact_Downloading_IsBareVocabulary` test (P2's generic `_ => facts.State` fallback), so the compact line
  needed no code change — only the JSON model field did.
- `grep IVectorFileProbe` — 4 implementers (1 prod `SystemVectorFileProbe`, 3 test stubs in unowned files:
  `VectorSidecarOpenTests`, `VectorStoreTests`, `SemanticOffGuaranteeTests`).

## Architecture as built (matches approved shape)
- **Seam-pure classification.** Extended `IVectorFileProbe` with `ReadPrepareMarker(millerDir)` (raw content or
  null) and `IsProcessAlive(pid)`. Both are **default interface methods** (`=> null` / `=> false`) so the 3
  unowned test stubs keep compiling untouched, and the defaults mean "no download in flight" — a probe that
  doesn't know the marker never invents `downloading`. `SystemVectorFileProbe` overrides both (real
  `File.ReadAllText` + `Process.GetProcessById` try/catch). JSON parsing is a **pure** static
  `SemanticPrepareMarker.TryParse` (JsonDocument, no reflection serializer — AOT-safe), so pid-alive and parse
  failure are both exercised through the fake probe.
- **Marker is the only cross-process signal.** No polling, no watchers. It is consulted **only** when
  classification would otherwise be `unavailable` (the `model_not_prepared` window), so: pause states win (they
  are never `unavailable`), a `ready`/`building` reader never touches the marker file, and off-mode short-circuits
  in `Inspect` before `Classify`.
- **Precedence** exactly as specified: `circuit-open`/`disk-blocked` > `downloading` > `unavailable`.
- **Mirror contract.** `SemanticPrepareMarker.FileName` in Miller.Indexing mirrors
  `SemanticPrepareCli.MarkerFileName` (Miller.Indexing cannot reference Miller.Server); a cross-project test pins
  the two strings together so they cannot drift.
- **Model id in JSON.** `VectorSidecarFacts.DownloadingModel` (null unless downloading) → `downloading_model` in
  the vectors JSON block, following the existing write-null-when-absent pattern. `"default"` (the no-`--model`
  label) would surface verbatim.

## DiskPreflight swap — LANDED (not pending)
`src/Miller.Indexing/Semantic/DiskPreflight.cs` did NOT exist when I started, but the Task 2 worker created it
mid-run. Its public API is stable — `DiskPreflight.Check(path, requiredBytes)` →
`DiskPreflightVerdict(Ok, FreeBytes, RequiredBytes)` — so I performed the swap: `SemanticPrepareCli.Production()`
now uses a `SharedDiskPreflight` adapter that delegates to `DiskPreflight`, keeping the `ISemanticPreparePreflight`
seam (stub-injected in tests). The model-footprint floor stays the local `DefaultRequiredBytes` (~1.2 GiB) until
**Task 7's Q8_0 benchmark** refines it. The old in-verb `DefaultPreflight` (private drive-probe copy) was deleted —
the probe/verdict logic now lives once in the shared component. My swap depends only on the stable `Check`/verdict
surface, not on `DiskPreflightVerdict.Reason` (whose formatting the Task 2 worker was still finishing).

## Verification
- **worker-red-green** — `dotnet test --filter VectorSidecarClassificationTests|SemanticPrepareCliTests|WorkspaceVectorFactsRenderTests`
  → **48 passed, 0 failed** (~48ms). Invariant: the `downloading` classification arm, precedence, marker
  parse/pid-alive, off-mode zero-work, the mirror constant, the DiskPreflight swap, and the JSON model field are
  all green together.
- **worker-ceiling** — `scripts/test.sh` → **4209 passed, 0 failed, 2 skipped**. Invariant: no regression across
  the fast suite; the new pure tests add negligible time. Wall-time: an early solo run measured **25s (< 30s
  ceiling)**; a later run during concurrent-worker load measured 102s and tripped the wall tripwire. This is
  machine contention (two workers testing the same worktree) plus Task 8's known <10s/ceiling concern — NOT a
  leaked slow test from Task 4 (its 48 tests run in ~48ms, pure logic, no I/O). No Task-4 test failure at any point.
- Release build (via `scripts/test.sh`) is 0 warnings / 0 errors (warnings-are-errors).

## Concerns
- **DiskPreflight swap: LANDED** (the coordination item from Task 3's report is closed). It consumes only the
  Task 2 worker's stable public `Check` surface.
- **Footprint constant still provisional** (~1.2 GiB) — tracks Task 7's Q8_0 benchmark, as designed.
- **No blockers.** The only non-green signal was the fast-suite wall tripwire under concurrent load (Task 8's lane).
