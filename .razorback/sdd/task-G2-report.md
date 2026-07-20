# Task G2 — packaged extension resolution + csproj copy/guard

**Status:** COMPLETE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-p3`, branch `worktree-semantic-p3`, base `7b8b52d`, clean at start.

## Miller-first evidence

| Call | Finding |
|---|---|
| `trace target=ResolveExtensionPath mode=refs` | 5 references, all zero-arg: `VectorSidecar.cs:46` (TryReadMeta), `VectorSidecar.cs:69` (OpenStore), `VectorConvergeService.cs:738` (TryOpen), `:792` (TryOpenAt), `VectorStoreTests.cs:27` (Locate). Adding an overload rather than changing the signature therefore keeps every caller compiling and behaviour-compatible. |
| `context query="sqlite-vec extension path resolution and semantic fail-open reason"` | Surfaced `ExtensionPathEnvVar` (`VectorStore.cs:46`), `OpenConnection` (`:519`), the `SqliteVecEnvironment` collection (`VectorSidecarOpenTests.cs:11`), and `SemanticEmbeddingSession.Fail` — confirming the fail-open reason path is the contract a null return feeds. |
| `impact --git` | 38 impacted symbols / 62 likely tests, dominated by `VectorSidecar`/`VectorConvergeService`/`SemanticSearchArm`; the csproj has no indexed symbols. All covered by the fast suite, which is green. |

API-shape evidence: `SqliteVecEnvironment` is a plain `public static class` with a `Name` const and **no** `[CollectionDefinition]` — existing members (`VectorConvergeServiceTests.cs:73`, `SemanticSearchArmTests.cs:374`, `VectorSidecarOpenTests.cs:153`) just apply `[Collection(SqliteVecEnvironment.Name)]`. The new test class follows that pattern exactly.

## What changed

1. **`src/Miller.Indexing/Semantic/VectorStore.cs`** (`ResolveExtensionPath` region only)
   - New `public static string PackagedExtensionFileName` → `vec0.dll` / `vec0.dylib` / `vec0.so` by host OS.
   - New `ResolveExtensionPath(string baseDirectory)` overload: env override (absolute precedence) → `<baseDir>/.tools/<PackagedExtensionFileName>` when the file exists → `null`.
   - Zero-arg `ResolveExtensionPath()` now delegates with `AppContext.BaseDirectory`, matching the `WorkspaceContext.ToolsRoot` convention. Doc comment corrected — it previously asserted "this build packages no extension".

2. **`src/Miller.Server/Miller.Server.csproj`**
   - Content items copying `.tools/julie-semantic-sidecar`, `.tools/julie-semantic-sidecar.exe`, and `.tools/vec0.*` to `<out>/.tools/`.
     The vec0 item uses a **wildcard**, not the three literal names: a literal `Include` creates the item even when the file is absent and the copy then fails, which would convert "semantic not restored" into a build error and break optionality.
   - `EnsureSemanticSidecarExecutable` reasserts `+x` on the copied Unix binary (MSBuild `Copy` drops it), mirroring `EnsureJulieExtractExecutable`.
   - `VerifyPinnedSemanticSidecarVersion` (BeforeTargets Build;Publish), gated on the same `Exists` conditions as the copy.

3. **`tests/Miller.Tests/Indexing/VectorStoreResolutionTests.cs`** (new) — 6 fast tests in the `SqliteVecEnvironment` collection, temp base dirs only, zero dependence on the repo's real `.tools/`.

### Guard design notes (deliberate divergences from the julie-extract template)

- **Prerelease-safe version regex.** `[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.\-]+)?` instead of julie's bare triple. The stale simulation below proves this matters: the bare regex would have read both `0.1.0-rc.1` and `0.1.0-rc.2` as `0.1.0` and false-passed.
- **Pin regex anchored on the `sidecar` object** — `"sidecar"\s*:\s*\{[^}]*?"version"\s*:\s*"([^"]*)"` — so the `sqliteVec.version` (`0.1.9`) below it can never be read as the sidecar pin. julie's first-`"version"`-wins regex is unsafe against this two-section file.
- **`MILLER_SEMANTIC_PINS` honoured** as a pin-path override, matching both restore scripts, so the guard is exercisable without editing committed pins. A non-existent override path raises an explicit `Error` rather than an unhandled `ReadAllText` throw.
- Semantics preserved: **missing ⟹ silent pass** (runtime fails open), **stale ⟹ build error naming `scripts/restore-semantic-sidecar.sh`**, **current ⟹ silent pass**.

## Verification

**Red:** `dotnet test --filter "FullyQualifiedName~VectorStoreResolution"` → 7 compile errors (`CS1501` ×5 no 1-arg overload, `CS0117` ×2 no `PackagedExtensionFileName`).

**Green:** same filter → `Passed! - Failed: 0, Passed: 6, Skipped: 0, Total: 6, Duration: 23 ms`.

**Guard evidence (a) — build WITH the restored sidecar (current pin):**
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
=== packaged layout ===
-rwxr-xr-x  julie-semantic-sidecar   (exec bit reasserted)
-rwxr-xr-x  vec0.dylib
```

**Guard evidence (b) — optionality, both binaries moved aside:**
```
=== .tools now ===   (empty)
Build succeeded.
    0 Warning(s)
    0 Error(s)
=== output .tools ===
ls: src/Miller.Server/bin/Release/net10.0/.tools/: No such file or directory
```
Both binaries were restored to `.tools/` immediately afterward (verified by `ls`); `.tools/` is back to `julie-semantic-sidecar` + `vec0.dylib`.

**Guard evidence (c) — stale simulation via `MILLER_SEMANTIC_PINS`.** Copied `scripts/semantic-pins.json` to a temp file with `sidecar.version` bumped `0.1.0-rc.1` → `0.1.0-rc.2` (committed pins untouched):
```
Miller.Server.csproj(207,5): error : Bundled julie-semantic-sidecar is v0.1.0-rc.1 but
  <tmp>/semantic-pins-stale.json pins v0.1.0-rc.2. The build copies .tools/julie-semantic-sidecar
  as-is (no version check), so a stale sidecar would emit vectors under an encoder fingerprint the
  pinned generation identity refuses. Re-run scripts/restore-semantic-sidecar.ps1 (Windows) or
  scripts/restore-semantic-sidecar.sh, then rebuild.
Build FAILED.  1 Error(s)
```
Immediately re-running without the override: `0 Warning(s) 0 Error(s)`.

**Worker ceiling:**
- `scripts/test.sh` → `Passed! - Failed: 0, Passed: 4108, Skipped: 2, Total: 4110, Duration: 20 s`; wall **25s** (ceiling 30s).
- `dotnet build Miller.slnx -c Release` → 0 warnings / 0 errors.

**Known flake, reported not suppressed.** The first two fast-suite runs each failed ONE test in `IndexerServiceScanTests` — run 1 `StartAsync_WhenEnabledLeader_BuildsSearchSidecarAfterStartupScan`, run 2 a *different* test, `StartAsync_AsLeader_RecordsLeaderIdentity_AndRemovesItOnStop`. Both are 5s `Wait(...)` timeouts. Isolated, the class is `29/29 passed in 454 ms`. Third full run was fully green. Diagnosis: CPU saturation from the parallel Track 2 worker (impl-f4) building concurrently — the flake named in the task brief. Not caused by this change: nothing here touches `IndexerService`, and no `.tools/` content reaches the test output directory (`ls tests/Miller.Tests/bin/Debug/net10.0/.tools/` → No such file or directory), so `ResolveExtensionPath()`'s new fallback still returns null in the test process exactly as before.

## Acceptance criteria

- [x] Env var wins over a present packaged file; env unset + packaged present ⟹ packaged path; neither ⟹ null. Temp dirs, no `.tools/` dependence. (Plus: empty-string env falls through, and the platform suffix is asserted.)
- [x] Release build clean both WITH and WITHOUT `.tools/julie-semantic-sidecar`.
- [x] Pin-bump simulation fails the build with the restore-script message.
- [x] Worker-scope verification passes; committed per serial-worker-commit.

## Concerns / hand-off to G3

- `VectorStoreTests.Locate` (`:27`) uses the zero-arg form. Once G3/G4 arrange for `.tools/` to land in a test output directory, that call will start resolving a real packaged path where it previously got null. That is the intended G3 behaviour, but G3 should assert it explicitly rather than inherit it silently.
- Windows exec-bit and `.exe` copy paths are structurally mirrored from the julie-extract items but were not runnable on this host (macOS).
- Untouched, as instructed: `.razorback/sdd/progress.md`, plan checkboxes, and all Track 2 files.
