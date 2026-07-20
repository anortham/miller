# P3 Track 1 — Sidecar Pins, Packaging, and Real-Sidecar Scale Tests

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make the published `julie-semantic-sidecar v0.1.0-rc.1` and the pinned sqlite-vec extension first-class pinned dependencies of Miller: restorable, build-guarded, resolvable at runtime from the packaged layout, and exercised by real-sidecar Scale tests.

**Architecture:** Mirror the julie-extract pin architecture exactly — `scripts/semantic-pins.json` (RC + sqlite-vec pins with sha256s), restore scripts into `.tools/`, an optional-presence csproj copy + stale-version build guard, and a packaged-layout fallback in `VectorStore.ResolveExtensionPath`. Semantic stays OPTIONAL: a missing sidecar/extension never fails the build or the runtime — it yields the stated fail-open reason (P2 contract). Only a *stale* restored binary fails the build.

**Tech Stack:** MSBuild targets, POSIX sh + PowerShell, xUnit Scale category.

**Architecture Quality:** No new seams — extends `VectorStore.ResolveExtensionPath` (one method), `Miller.Server.csproj` (Content + guard target, mirroring `VerifyPinnedJulieExtractVersion`), `ScaleTestSupport` (one new `RequireSemanticSidecar()` launch signal). Risk: guard semantics must be missing⟹builds / stale⟹fails / current⟹silent, same as julie-extract; inverting that breaks the optionality contract.

## Global Constraints

- Pinned sidecar version: `0.1.0-rc.1`; release tag `v0.1.0-rc.1`; asset names `julie-semantic-sidecar-0.1.0-rc.1-<triple>.tar.gz` (`.zip` for windows). NOTE: no `v` before the version in asset names (differs from julie-extract's `julie-extract-v{VER}-…`).
- Archive layout: binary at archive ROOT (`julie-semantic-sidecar[.exe]`, plus LICENSE/README) — no `dist/{triple}/` inner path.
- Verified sha256s (computed from run 29764982564 artifacts, checked against the workflow's own sidecars before publish):
  - `aarch64-apple-darwin`: `2957b0c857cf0aa6ff6c5288ae3b2035b99f3540efc51978eb21cc755a59f508`
  - `x86_64-apple-darwin`: `0ddcc5e01b2410a58fde9b42155e1d1433d0ed7849bedc695e4fbff64a07c7a0`
  - `x86_64-unknown-linux-gnu`: `414eaa6039ed98280e3fcbdb7a0fa295ce425c59f14564402e1393d3b16a6af9`
  - `x86_64-pc-windows-msvc` (zip): `b3690b7a7567910a488cc0e5e391ff65868e31e1f84ea1c93f0487e4d1217f4e`
- sqlite-vec pins: reuse the exact `scripts/spike-pins.json` values (v0.1.9, per-RID assets/members/sha256s) — do not re-derive.
- Optionality is load-bearing: missing `.tools/julie-semantic-sidecar` or `.tools/vec0.*` ⟹ build succeeds AND runtime fail-opens with a reason. Stale restored sidecar (version ≠ pin) ⟹ build FAILS. `MILLER_SEMANTIC=off` zero-work guarantee untouched.
- `MILLER_SQLITE_VEC_PATH` env var keeps absolute precedence over the packaged path.
- Scale tests spawn the real sidecar ⟹ `[Trait("Category","Scale")]` + a single launch signal in `ScaleTestSupport`; skip (not fail) when the binary is absent. Extend `ScaleTraitConventionTests` to scan for the new signal — strengthen, never weaken.
- Fast suite stays green and under the 30s wall ceiling; no new fast test may touch `.tools/` or the network.
- NO pushes of miller (standing directive). NO GitHub workflow dispatches — G4 edits `release.yml` but nothing runs until a release is cut with user approval.
- Deferred, NOT in this plan (each spends GH Actions minutes and needs explicit user costing): packaged-AOT smoke ×4 RIDs, Metal/Vulkan CI lanes, swarm RAM + idle-unload gate, pinned-Julie drop-in test.

## Verification Strategy

**Project source of truth:** CLAUDE.md (fast/Scale split, 0-warning Release build, `scripts/test.sh`).

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~<task tests>"`; for restore scripts, running the script against the live release and checking `.tools/` contents + `--version` output IS the red/green evidence (record transcript in the report).

**Worker ceiling:** `scripts/test.sh` + `dotnet build Miller.slnx -c Release`. Workers stop and report on assigned-gate failure.

**Worker gate invariant:** per task below.

**Lead affected-change scope:** `scripts/test.sh` after each task lands (serial lane, cheap).

**Branch gate:** owned by the P3 Track 2 lane — `scripts/test.sh all` + Release build at the combined HEAD before pre-merge review; G3's real-sidecar Scale tests join that `all` run.

**Escalation triggers:** any csproj/guard change ⟹ verify a clean `dotnet build` both WITH and WITHOUT `.tools/julie-semantic-sidecar` present (optionality is the invariant).

**Assigned verification failure:** stop and report; never weaken a gate.

**Verification ledger:** `docs/plans/2026-07-20-p3-verification-ledger.md` (shared with Track 2).

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task G1: pins + restore scripts | None - serial | Create: `scripts/semantic-pins.json`, `scripts/restore-semantic-sidecar.sh`, `scripts/restore-semantic-sidecar.ps1` | Yes | G2's guard and G3's Scale tests need a restorable `.tools/` layout defined here first. |
| Task G2: packaged resolution + csproj guard | None - serial | Modify: `src/Miller.Indexing/Semantic/VectorStore.cs` (ResolveExtensionPath only), `src/Miller.Server/Miller.Server.csproj`; Test: `tests/Miller.Tests/Indexing/VectorStoreResolutionTests.cs` (create) | Yes | Consumes G1's `.tools/` layout and pin file path. |
| Task G3: real-sidecar Scale tests | None - serial | Modify: `tests/Miller.Tests/ScaleTestSupport.cs`, `tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs`; Create: `tests/Miller.Tests/Indexing/SemanticSidecarScaleTests.cs` | Yes | Needs G1's restore to have populated `.tools/` and G2's resolution to locate it. |
| Task G4: release workflow packaging | None - serial | Modify: `.github/workflows/release.yml` (or the release workflow file discovered via Miller), `docs/release-process.md` | Yes | Packages the layout G1–G3 defined; edits must reflect final names. Runs in the Track 2 lane only AFTER F5, because a workflow-touching commit on an unreleased branch is inert but must not land mid-review. |

## Task G1: semantic-pins.json + restore scripts

**Files:**
- Create: `scripts/semantic-pins.json`, `scripts/restore-semantic-sidecar.sh`, `scripts/restore-semantic-sidecar.ps1`

**Interfaces:**
- Consumes: the published release `v0.1.0-rc.1` (asset names/sha256s in Global Constraints), `scripts/spike-pins.json` sqlite-vec values, the `restore-julie-extract.sh`/`.ps1` structure as the template.
- Produces: `scripts/semantic-pins.json` with TWO sections — `sidecar` (version/urlTemplate/assets by triple) and `sqliteVec` (version/urlTemplate/assets by .NET RID with `member`) — and restore scripts that place `.tools/julie-semantic-sidecar[.exe]` and `.tools/vec0.{dylib|so|dll}`, verify sha256 before extraction, and print restored versions. `--from-source` support via `MILLER_SEMANTIC_SIDECAR_SOURCE` mirroring `MILLER_JULIE_SOURCE`.

**Contract inputs:** Global Constraints pins block verbatim; archive layout (binary at root); windows asset is a `.zip` for the sidecar but a `.tar.gz` for sqlite-vec (spike-pins note).

**What to build:** The pin file and both restore scripts, structured like `restore-julie-extract.*` (download to temp, sha256 check, extract member, chmod, version smoke where runnable on this host).

**Acceptance criteria:**
- [x] `scripts/restore-semantic-sidecar.sh` on this mac restores `.tools/julie-semantic-sidecar` (arm64) + `.tools/vec0.dylib`, sha256-verified, and `--version` prints `julie-semantic-sidecar 0.1.0-rc.1`
- [x] Tampered/wrong sha256 aborts without touching `.tools/`
- [x] `.ps1` mirrors the `.sh` flag-for-flag (structural review; not runnable on this host — pwsh absent)
- [x] Worker-scope verification passes; worker commits per serial-worker-commit (`c4c3270`; fast-suite tripwire waived on isolation proof — F3's in-flight TDD code owns the compile failure, G1 touches zero .cs/.csproj/.slnx)

## Task G2: packaged extension resolution + csproj copy/guard

**Files:**
- Modify: `src/Miller.Indexing/Semantic/VectorStore.cs` (`ResolveExtensionPath` + a testable overload taking a base directory), `src/Miller.Server/Miller.Server.csproj`
- Test: create `tests/Miller.Tests/Indexing/VectorStoreResolutionTests.cs`

**Interfaces:**
- Consumes: G1's `.tools/` layout; the `VerifyPinnedJulieExtractVersion` target (`Miller.Server.csproj:94`) as the guard template; `WorkspaceContext.ToolsRoot` convention (`AppContext.BaseDirectory/.tools`).
- Produces: resolution order = `MILLER_SQLITE_VEC_PATH` env var → `<baseDir>/.tools/vec0.<platform ext>` when the file exists → null (reason unchanged); csproj Content items copying `.tools/julie-semantic-sidecar*` and `.tools/vec0.*` to `<out>/.tools/` when present; `VerifyPinnedSemanticSidecarVersion` target reading `scripts/semantic-pins.json` — missing binary ⟹ silent pass, version mismatch ⟹ build error naming the restore script.

**Contract inputs:** optionality invariant (Global Constraints); guard regex/`Exec` pattern copied from the julie-extract target; prerelease versions contain `-` so the guard's version-extraction regex must match `[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.\-]+)?`.

**What to build:** The runtime fallback (pure, tested with a temp base dir) and the build-time copy/guard.

**Acceptance criteria:**
- [ ] Env var set ⟹ wins even when a packaged file exists; env var unset + packaged file present ⟹ packaged path; neither ⟹ null (tests, temp dirs, no `.tools/` dependence)
- [ ] `dotnet build Miller.slnx -c Release` clean BOTH with `.tools/julie-semantic-sidecar` present (current pin) and with it absent
- [ ] Renaming a stale binary into `.tools/` (or pin-bump simulation) fails the build with the restore-script message
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit

## Task G3: real-sidecar Scale tests

**Files:**
- Modify: `tests/Miller.Tests/ScaleTestSupport.cs`, `tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs`
- Create: `tests/Miller.Tests/Indexing/SemanticSidecarScaleTests.cs`

**Interfaces:**
- Consumes: `.tools/julie-semantic-sidecar` (restored by G1), `SemanticEmbeddingSession` (handshake/embed), `VectorStore` + resolution from G2, `SqliteVecEnvironment` collection (env-mutating Scale classes serialize there).
- Produces: `ScaleTestSupport.RequireSemanticSidecar()` — the single launch signal, skip-not-fail; Scale tests proving: real handshake reports the pinned encoder fingerprint matching `MillerSemanticContract.DefaultEncoder`; a real embed of a symbol card is 512-dim and quantizes into the pinned int8 lane; a converge→query round trip through `SemanticSearchArm` returns the planted symbol for a semantically-similar prose query.

**Contract inputs:** Scale trait rule (CLAUDE.md); the convention guard is one-directional (spawns sidecar ⟹ Scale) and must gain the new signal; `FakeSemanticSidecar` stays untouched — these tests are the REAL-binary complement.

**What to build:** The launch signal + 3 Scale tests, in the `SqliteVecEnvironment` collection if they touch `MILLER_SQLITE_VEC_PATH`.

**Acceptance criteria:**
- [ ] With `.tools/` restored: `scripts/test.sh scale` green including the new tests; without: they SKIP with an actionable message
- [ ] `ScaleTraitConventionTests` fails a deliberately untagged file referencing `RequireSemanticSidecar` (prove by temporary mutation in the report, not a committed test)
- [ ] Handshake fingerprint equals `MillerSemanticContract.DefaultEncoder` fingerprint — this is the RC promotion gate evidence
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit

## Task G4: release workflow packaging

**Files:**
- Modify: the GitHub release workflow (discover exact file via Miller; expected `.github/workflows/release.yml`), `docs/release-process.md`

**Interfaces:**
- Consumes: G1's pin file + restore scripts (the workflow restores by pin, never hardcodes URLs); the existing julie-extract restore/package steps as the template.
- Produces: each platform archive additionally carries `.tools/julie-semantic-sidecar[.exe]` and `.tools/vec0.<ext>`; workflow smoke-runs `julie-semantic-sidecar --version`; release-process doc gains the semantic restore/pin-bump step.

**Contract inputs:** release matrix (4 targets, CLAUDE.md); NO dispatch — the edit is inert until a release is cut with user approval; tag-push 403 rule (never push workflow-touching commits to main during a tag-push release run) noted in the doc update.

**What to build:** Workflow steps + doc. No run, no push.

**Acceptance criteria:**
- [ ] Workflow YAML parses (`actionlint` if available, else `python -c "import yaml,sys; yaml.safe_load(open(...))"`) and restore step uses `scripts/semantic-pins.json`
- [ ] `docs/release-process.md` documents the semantic pin-bump + restore flow
- [ ] Worker-scope verification passes; worker commits per serial-worker-commit
