# julie-extract 2.32.0 Adoption and Miller v1.18.2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Adopt released julie-extract 2.32.0, reconcile the completed family-store consumer regressions, dogfood the full integration, and prepare a verified local Miller v1.18.2 release candidate without publishing it.

**Architecture:** Keep the released producer/consumer boundary unchanged: julie-extract owns schema-v2 family-store writes and exact resolution publication; Miller reads pinned views and owns derived sidecars. The adoption changes the bundled producer and consumer regression coverage, then validates existing recovery, sidecar, performance, and release interfaces end to end.

**Tech Stack:** .NET 10, xUnit, SQLite, Rust-produced julie-extract release archives, GitHub release assets, shell/PowerShell release scripts.

**Architecture Quality:** No new module or caller-facing interface. Architecture risk is low-to-medium because exact-resolution recovery and Windows file replacement are load-bearing, but the public store schema, format epoch, standalone artifact, resolver epoch, CLI vocabulary, and MCP surface remain unchanged.

## Global Constraints

- Pin julie-extract exactly to stable `2.32.0` and the four published archive digests.
- Preserve family-store schema `2`, format epoch `1`, standalone schema `6`, semantic policy `2`, and hash algorithm `blake3` unless live contract evidence proves otherwise.
- Preserve unset/blank `MILLER_INDEX_STORE` as family-store-on and explicit `0|false|off|disabled` as legacy compatibility mode.
- Do not add an MCP tool or absorb producer-owned store writes into Miller.
- Preserve the current local `main` history and all related dirty documents; do not stash or overwrite them.
- Keep fast and Scale suites separate during development; run `scripts/test.sh all` once at the branch gate because the extractor/index path changes.
- Do not push, tag, publish, deploy, release, or update live marketplace state without explicit user approval after the final clean-state report.
- Each task owns its acceptance-checkbox update and Goldfish checkpoint alongside its declared files so execution state is committed with the slice.

## Architecture Quality

**Affected modules:** Release pin contract, `FamilyStoreReadSession` regression coverage, `StoreWorkspaceIndexProvider` recovery coverage, store/sidecar lifecycle, release manifests and evidence.

**Caller-facing interface:** Existing `julie-extract scan/update/store` process contract, validated family-store read session, Miller workspace lifecycle, and existing CLI/MCP read surfaces.

**Depth/locality check:** Producer fixes remain behind the pinned executable boundary. Miller adds no store-write logic and validates behavior through the same session/provider interfaces used in production.

**Test surface:** Pin contract tests, `FamilyStoreReadSessionTests`, `StoreWorkspaceIndexProviderScaleTests`, focused store/recovery suites, public CLI dogfood, fast/Scale/build/package gates.

**Seams/adapters:** Reuse `MillerExtractContract`, restore scripts, `FamilyStoreReadSession`, and `StoreWorkspaceIndexProvider`; no new adapter.

**Rejected shortcuts:** Do not suppress partial resolution, fall back to stale legacy artifacts, force full resolution by default, skip real released binaries, or treat Linux as proof of Windows runtime behavior.

**Architecture risk:** low-to-medium.

## Verification Strategy

**Project source of truth:** `AGENTS.md`, especially Testing, Build, Release packaging, version-aware leadership, and versioned family-store rules; `docs/release-process.md` for release preparation.

**Worker red/green scope:** Run the exact focused xUnit class or group covering each changed contract with `dotnet test --filter "FullyQualifiedName~<TestClassName>"`. Pin-only assertions must fail on 2.31.4 before the bump and pass on 2.32.0 after restore.

**Worker ceiling:** Focused test classes and a focused project build. Workers do not run the full fast or Scale suites.

**Worker gate invariant:** Pin tests prove the advertised/bundled version and checksums; read-session tests prove atomic base rotation; Scale recovery tests prove partial resolution triggers honest `RootRebind` recovery against the released producer.

**Lead affected-change scope:** Run the pin/contract classes, `FamilyStoreReadSessionTests`, `StoreWorkspaceIndexProviderScaleTests`, and any additional classes returned by Miller impact after edits.

**Branch gate:** Run `scripts/test.sh all` once, `dotnet build Miller.slnx -c Release`, plugin/site checks required by `docs/release-process.md`, and release-candidate smoke commands against the restored 2.32.0 binary.

**Security scope:** Run repository gitleaks and vulnerable-package checks defined by `docs/release-process.md`; verify the upstream v2.32.0 release's dependency/secret gates from its live workflow evidence.

**Replay/metric evidence:** Hard gates are zero canonical row differences for recovery/rotation fixtures, exact resolution after repair, current search/content/vector sidecars, successful public read commands, all documented test/build gates, and correct packaged versions. Report-only metrics are resolution phase timings, cold/warm context latency, sidecar convergence time, store bytes, and restart timing on this machine.

**Escalation triggers:** Any schema/epoch change, output vocabulary change, new fallback behavior, Windows-only failure, stalled convergence, or unexpected sidecar rebuild requires focused diagnosis before release preparation. A changed remote branch, commit, worktree, or dirty state invalidates release approval and requires a fresh state check.

**Assigned verification failure:** Stop only the affected implementation lane, diagnose with `razorback:systematic-debugging`, and keep the overall plan active unless a real approval/access blocker remains.

**Verification ledger:** Record invariant, command, scope, commit SHA, result, timestamp, hard-gate facts, and report-only timings in the adoption/release evidence document. Reuse green evidence only when HEAD and the relevant restored binaries are unchanged.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Reconcile current local work | None - serial | Existing dirty docs; `.memories/`; this execution plan; `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs`; `tests/Miller.Tests/Server/StoreWorkspaceIndexProviderScaleTests.cs`; `TODO.md` | Yes | The current checkout and clean consumer branch must be reconciled before pin and verification edits. |
| Task 2: Adopt released 2.32.0 | None - serial | `scripts/julie-pins.json`; `src/Miller.Indexing/MillerExtractContract.cs`; pin/version tests; `THIRD-PARTY-NOTICES.md`; adoption evidence | Yes | Requires Task 1's final tree and published asset facts. |
| Task 3: Prove focused consumer compatibility | None - serial | Focused test fixes only if released-binary evidence exposes a real compatibility defect | Yes | Requires the restored 2.32.0 binary and reconciled regressions. |
| Task 4: Dogfood store, sidecars, and performance | None - serial | Dogfood evidence and related TODO/plan status only; production fixes follow TDD if failures are found | Yes | Requires focused compatibility green; may reveal defects that must land before release prep. |
| Task 5: Prepare and gate Miller v1.18.2 | None - serial | Version/plugin manifests; `docs/release-notes/v1.18.2.md`; docs map; version tests; final evidence | Yes | Requires all implementation and dogfood fixes to be stable before versioning and the one branch gate. |

### Task 1: Reconcile current local work and family-store consumer regressions

**Files:**
- Modify: `TODO.md`
- Modify: `docs/plans/2026-08-11-user-relief-bugfix-program.md`
- Preserve/add: `.memories/2026-08-11/*.md`
- Preserve/add: `.memories/2026-08-12/*.md`
- Modify: `.memories/briefs/prove-the-value-then-show-it-2026-07-28-strategy.md`
- Add: `docs/findings/2026-08-04-worktree-rebind-plan-reassessment.md`
- Add: `docs/plans/2026-08-11-julie-extract-2.32-miller-v1.18.2-plan.md`
- Modify: `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs`
- Modify: `tests/Miller.Tests/Server/StoreWorkspaceIndexProviderScaleTests.cs`

**Interfaces:**
- Consumes: Current `main` at `470cfc1d` plus clean commits `9bf6bc26` and `382f654c` from `feature/store-incremental-resolution-consumer`.
- Produces: One intentional local continuation tree containing the merged user-relief/context work, durable local docs/memory, exact base-rotation coverage, and partial-resolution `RootRebind` coverage.

**Contract inputs:** Existing user changes are authoritative. The two feature commits add tests and memory; their `TODO.md` delta must be reconciled with the newer dirty `main` file rather than overwriting it.

**File ownership:** Existing dirty docs; `.memories/`; this execution plan; `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs`; `tests/Miller.Tests/Server/StoreWorkspaceIndexProviderScaleTests.cs`; `TODO.md`.

**Serialization required:** Yes.

**Dependency reason:** Current local state must be preserved and integrated before later changes can produce trustworthy diffs and gates.

**What to build:** Capture the current related dirty documentation/memory state, then integrate the two clean consumer regression commits onto current `main`. Resolve only related overlap and leave both source worktrees clean and attributable.

**Approach:** Use explicit commits rather than stash. Before and after integration, record path, branch, commit, dirty state, and all related worktrees. Review the resulting tests through `FamilyStoreReadSession` and `StoreWorkspaceIndexProvider`, not private producer details.

**Acceptance criteria:**
- [x] Current dirty files are preserved byte-for-byte unless this plan explicitly updates them.
- [x] Exact base rotation and partial-resolution recovery regressions exist on current `main`.
- [x] Both source worktrees and commit ancestry are reconciled and reported.
- [x] Focused test discovery/build succeeds and the change is committed locally.

### Task 2: Pin and restore released julie-extract 2.32.0

**Files:**
- Modify: `scripts/julie-pins.json`
- Modify: `src/Miller.Indexing/MillerExtractContract.cs`
- Modify: `tests/Miller.Tests/Indexing/JulieSchemaGateTests.cs`
- Modify: `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Modify: `THIRD-PARTY-NOTICES.md`
- Create: `docs/findings/2026-08-11-julie-extract-2.32.0-adoption.md`
- Modify: `docs/README.md`

**Interfaces:**
- Consumes: Stable GitHub release `v2.32.0` and its four published SHA-256 asset digests.
- Produces: Miller pin/version contracts and restored local tools that identify exactly as julie-extract 2.32.0.

**Contract inputs:** Asset digests: `aarch64-apple-darwin=8643bf19db98af7942785454aa3b774cac300b22650aeae87a86b1bd69ca3648`; `x86_64-apple-darwin=ad0f7e9abde86ce919c01088f551c908425d07f4060d80fb85ce85efc56946bf`; `x86_64-unknown-linux-gnu=aa7280999d561a7a2a6385f416870503fe29aaa54443d2bfeef393b6bdd56fad`; `x86_64-pc-windows-msvc=4d42f077e5f118b31178350b5881e5738b34c9d63ce5e520c98b7fd39884be6b`.

**File ownership:** Pin manifest, extractor contract/version tests, third-party notice, adoption evidence, docs map.

**Serialization required:** Yes.

**Dependency reason:** The restored binary and all downstream verification depend on one aligned pin.

**What to build:** Update every Miller extractor-version assertion and package pin to 2.32.0, document compatibility, restore the released host binary, and verify its digest/version before building.

**Approach:** Treat the public store contract as unchanged and additive telemetry as optional. Follow the prior 2.31.4 adoption shape, but record the new scoped-resolution default, `JULIE_STORE_RESOLUTION_DELTA=off` escape hatch, rebase thresholds, partial-store recovery, and Windows pointer/durability fixes.

**Acceptance criteria:**
- [x] All pin/version tests fail on the old expectation and pass at 2.32.0.
- [x] Restored host binary digest matches the published asset and `--version` reports 2.32.0.
- [x] Schema/epoch compatibility is measured rather than assumed.
- [x] Adoption evidence and docs map are current.
- [x] Focused verification passes and the change is committed locally.

### Task 3: Prove focused consumer compatibility and recovery

**Files:**
- Test: `tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs`
- Test: `tests/Miller.Tests/Server/StoreWorkspaceIndexProviderScaleTests.cs`
- Test: pin/contract test files from Task 2
- Modify only if needed: the smallest existing Miller consumer seam implicated by a failing regression

**Interfaces:**
- Consumes: Restored julie-extract 2.32.0 and the existing validated read-session/provider APIs.
- Produces: Focused proof that atomic base rotation, partial-resolution recovery, refresh/open behavior, and unchanged public contracts work with the released producer.

**Contract inputs:** Use real extractor launches only in `[Trait("Category","Scale")]` classes through `ScaleTestSupport.RequireJulieServer()`.

**File ownership:** Focused test files; a production seam only after a failing regression proves the need.

**Serialization required:** Yes.

**Dependency reason:** Compatibility must be proven before live dogfood can mutate current family-store state.

**What to build:** Run the narrow pin/read-session/provider groups and the new live Scale regressions. If a real incompatibility appears, use systematic debugging and TDD to fix the existing consumer boundary without broadening ownership.

**Approach:** Keep pure tests fast and extractor launches Scale-tagged. Confirm both scoped-default and forced-full escape-hatch behavior where the public process contract permits it.

**Acceptance criteria:**
- [x] Pin/contract focused tests pass.
- [x] Atomic base rotation remains readable without mixed-generation state.
- [x] Partial resolution is classified and repaired through `RootRebind` to exact state.
- [x] Released 2.32.0 works without a Miller schema/tool-surface change.
- [x] Focused verification passes and any required fix is committed locally.

### Task 4: Dogfood the complete integration

**Files:**
- Create: `docs/findings/2026-08-11-julie-extract-2.32.0-dogfood.md`
- Modify: `TODO.md`
- Modify: `docs/plans/2026-08-11-user-relief-bugfix-program.md`
- Modify only after a failing test: existing production/test files implicated by dogfood

**Interfaces:**
- Consumes: The built Miller candidate, released 2.32.0 producer, current Miller family store, existing sidecar/workspace/read commands.
- Produces: Reproducible recovery, readiness, correctness, performance, and platform evidence for release review.

**Contract inputs:** Dogfood must cover status/health, refresh/full or an equivalent repair replay, search/content/vector convergence, semantic broker readiness, inspect/trace/impact/context, edit dry-run, workspace cross-read behavior, legacy off-switch compatibility, and package/version output. Do not claim Windows runtime proof from Linux.

**File ownership:** Dogfood evidence and only test-first fixes demonstrated by failures.

**Serialization required:** Yes.

**Dependency reason:** Live mutation and timings must use the final focused-compatible candidate.

**What to build:** Exercise the full public workflow on real repositories and recorded fixtures, including the currently failed Julie workspace, then compare timings and storage against the prior 2.31.4/2.31.5 evidence. Verify sidecars reach current/ready and that no stale legacy fallback is served.

**Approach:** Capture before/after state and phase timings. Prefer bounded CLI candidate processes so this session's MCP transport remains available until code exploration is complete. Run any required restart/leadership handoff last and re-check all related worktrees afterward.

**Acceptance criteria:**
- [x] The failed Julie workspace recovers from partial resolution and becomes readable/exact.
- [x] Miller workspace reaches exact resolution with search/content/vectors current and semantic broker healthy or honestly degraded.
- [x] Public read surfaces and edit preview return valid compact/JSON output with no stale fallback.
- [x] Legacy compatibility mode exports the current view and never serves an older artifact.
- [x] Scoped/default and forced-full results are canonically equivalent; timings are recorded.
- [x] Windows release asset layout/version and upstream Windows gates are verified; no additional Miller-hosted
  Windows gate is required for Task 4.
- [x] Dogfood fixes pass focused pure and real-producer Scale tests and are committed locally through `47421be3`.

### Task 5: Prepare and gate Miller v1.18.2

**Files:**
- Modify: `Directory.Build.props`
- Modify: `.claude-plugin/marketplace.json`
- Modify: `.claude-plugin/plugin.json`
- Modify: `.cursor-plugin/plugin.json`
- Modify: `.codex-plugin/plugin.json`
- Modify: `miller-plugin.json`
- Modify: `tests/Miller.Tests/Server/MillerVersionTests.cs`
- Modify: `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`
- Create: `docs/release-notes/v1.18.2.md`
- Create: `docs/findings/2026-08-11-v1.18.2-release-candidate.md`
- Modify: `docs/README.md`

**Interfaces:**
- Consumes: Fully dogfooded implementation and 2.32.0 adoption evidence.
- Produces: One clean, locally committed v1.18.2 candidate with aligned manifests, release notes, branch-gate ledger, and an exact list of remaining remote approval gates.

**Contract inputs:** Live latest Miller release is stable v1.18.1. Marketplace manifests must not reach `origin/main` until matching v1.18.2 release assets can be published in the same approved session.

**File ownership:** Version/plugin manifests, version tests, release notes, docs map, candidate evidence.

**Serialization required:** Yes.

**Dependency reason:** Release metadata must describe the final verified tree, not an intermediate candidate.

**What to build:** Align all Miller/plugin versions at 1.18.2, write release notes covering the user-relief, context-cancellation, family-store consumer, and julie-extract 2.32.0 changes, run the one branch gate, and produce a clean-state release recommendation.

**Approach:** Run `scripts/test.sh all` once on the final unchanged tree, then Release build and documented plugin/site/security/package smokes. Reconcile every related Miller and Julie worktree. Stop before push/tag/publication and request the smallest release approval with exact candidate commit and remaining Windows/remote gates.

**Acceptance criteria:**
- [x] All version and plugin manifests are aligned at 1.18.2.
- [x] Release notes and docs map describe the final verified behavior and limitations.
- [x] `scripts/test.sh all` passes on the exact implementation tree; only version/docs metadata changed afterward.
- [x] Release build has zero warnings and zero errors on the exact implementation tree.
- [x] Plugin/site/security/local package smokes pass; the approval-dependent four-target workflow gate is documented.
- [ ] Current path, branch, commit, dirty state, and all related worktrees are reconciled and reported.
- [x] No push, tag, publication, release, or live marketplace change occurs without explicit user approval.
