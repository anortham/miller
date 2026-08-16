# Coordinated Performance Recovery Releases Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Publish the verified Julie performance-recovery producer as `julie-extract 2.33.3`, adopt that exact live release in Miller, and publish the verified Miller recovery as `Miller 1.19.2`.

**Architecture:** This is a serialized two-repository release. Julie must merge, version, verify, publish, and expose final archive hashes before Miller can update its pin, restore the exact producer, verify the integrated package, and publish. Each release uses the repository's existing workflows and live GitHub facts; no contract, schema version, MCP tool, or semantic pin changes are introduced.

**Tech Stack:** Rust/Cargo, .NET 10, Git/GitHub Actions, Bash/PowerShell release scripts, Goldfish memory.

**Architecture Quality:** No Architecture Impact. The release preserves the already-reviewed producer/consumer boundaries and changes only release metadata, pin data, documentation, and evidence around the approved recovery implementation.

## Global Constraints

- Julie target version is `2.33.3`; Miller target version is `1.19.2`.
- Preserve Store Contract v1, schema versions, SQLite schema versions, report schemas, hash algorithms, nine-tool MCP surface, semantic default-on/off-switch behavior, and Linux/Windows compatibility.
- Do not mutate the protected Linux family store or its Miller/broker processes.
- Do not overwrite an existing tag or release; all pushes are fast-forward and all release workflows use overwrite disabled.
- Julie must be live and its four archive hashes verified before Miller pin files change.
- Miller marketplace manifests must not advertise `1.19.2` on `origin/main` without publishing `v1.19.2` in the same working session.
- Public current-release claims and verification findings must use live GitHub release facts.
- Preserve unrelated dirty files in `/home/murphy/source/miller`.

## Verification Strategy

**Project source of truth:** Julie `AGENTS.md` and `docs/release.md`; Miller `AGENTS.md` and `docs/release-process.md`.

**Worker red/green scope:** Release metadata consistency, format/diff checks, release preflight, package lists, manifest tests, focused pin/version tests, and restore/version probes.

**Worker ceiling:** Workers may run focused metadata, restore, format, Clippy, manifest, and version checks assigned below. The lead owns full branch, package, publication, and live-asset acceptance.

**Worker gate invariant:** Every metadata surface names the same version; every pin hash matches the live archive; release-note and documentation maps exist; no public contract changes.

**Lead affected-change scope:** Review every release/pin diff, run `git diff --check`, verify worktree state, and prove local/remote/tag identities.

**Branch gate:** Julie: `cargo fmt --all -- --check`, strict all-target/all-feature Clippy, `cargo test -p xtask`, `cargo xtask test default`, `cargo xtask test contract`, release preflight/package-list, agent-doc sync. Miller: restore both pinned toolsets, Release build, `scripts/test.sh all`, `scripts/test-plugin.sh`, manifest/version assertions, doc sync, and diff check.

**Security scope:** none declared.

**Replay/metric evidence:** Existing exact-current Linux performance replay and native Windows acceptance remain hard evidence for the recovery implementation. Release runs hard-gate build/package/smoke/checksum/tag/asset correctness; historical latency metrics are report-only.

**Escalation triggers:** Any contract/schema/version-gate change, failed exact-current branch gate, missing platform asset, checksum mismatch, workflow failure, remote-main drift, or tag mismatch stops publication until repaired.

**Assigned verification failure:** Workers stop and report when assigned verification fails unless this plan explicitly owns that metadata correction.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the release evidence/checkpoints. Reuse exact-HEAD evidence only where repository rules permit; never rerun a green unchanged scope.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Prepare and verify Julie 2.33.3 | None - serial | Julie version manifests/lockfile, release notes/maps, stale v2.33.2 publication wording, whitespace corrections, release checkpoint | Yes | Must produce a verified Julie release candidate before integration and publication. |
| Task 2: Integrate and publish Julie 2.33.3 | None - serial | Julie Git refs, GitHub workflow/release, live evidence and publication pointers | Yes | Requires Task 1's exact verified candidate; Miller needs the resulting live hashes. |
| Task 3: Pin Julie and prepare Miller 1.19.2 | None - serial | Miller Julie pins/contracts/assertions/notices, version/plugin manifests/assertions, release notes/maps, whitespace correction, release checkpoint | Yes | Requires Task 2's live version, assets, hashes, and tag provenance. |
| Task 4: Integrate and publish Miller 1.19.2 | None - serial | Miller Git refs, GitHub package/promotion workflows, live release body/evidence/current-release docs | Yes | Requires Task 3's exact integrated pin and green branch gate. |

### Task 1: Prepare and verify Julie 2.33.3

**Files:**
- Modify: `crates/julie-extractors/Cargo.toml`
- Modify: `crates/julie-extract-artifact/Cargo.toml`
- Modify: `crates/julie-extract-cli/Cargo.toml`
- Modify: `Cargo.lock`
- Create: `docs/release-notes/v2.33.3.md`
- Modify: `docs/release.md`
- Modify: `docs/README.md`
- Modify: `docs/release-notes/README.md`
- Modify: `docs/release-notes/v2.33.2.md`
- Modify: `.memories/2026-08-15/095405_ab6c.md`
- Modify: `.memories/2026-08-15/095935_8cf4.md`

**Interfaces:**
- Consumes: verified producer HEAD `152f51e4`, live `v2.33.2`, Store Contract v1.
- Produces: one internally consistent `2.33.3` release-prep commit that passes every Julie branch/release gate.

**Contract inputs:** No public contract or schema version moves. `2.33.3` is a compatible reliability/performance patch.

**File ownership:** Julie version manifests/lockfile, release notes/maps, stale v2.33.2 publication wording, whitespace corrections, release checkpoint.

**Serialization required:** Yes.

**Dependency reason:** Must produce a verified Julie release candidate before integration and publication.

**What to build:** Bump the three publishable crates and lockfile to `2.33.3`; write release notes for bounded incremental resolution, unchanged imports, recovery/fencing, and cross-platform acceptance; reconcile stale v2.33.2 publication wording; remove existing whitespace gate failures.

**Approach:** Follow recent patch-release format. Keep current-published pointers truthful until `v2.33.3` is live, while making the candidate discoverable. Run the full Julie branch gate once on the exact prep tree.

**Acceptance criteria:**
- [x] All crate/lockfile versions and release-note paths consistently name `2.33.3`.
- [x] v2.33.2 docs no longer claim the already-live release is pending.
- [x] All Julie branch/release gates pass on the exact prep commit.
- [x] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

### Task 2: Integrate and publish Julie 2.33.3

**Files:**
- Modify after live publication: `docs/release.md`
- Modify after live publication: `docs/README.md`
- Modify after live publication: `docs/release-notes/README.md`
- Create after live publication: `docs/release-evidence/2026-08-16-v2-33-3-release.md`

**Interfaces:**
- Consumes: Task 1 release-prep commit and GitHub `Release Binaries` workflow.
- Produces: live stable `v2.33.3`, four verified platform archives, tag provenance, final hashes, and reconciled `origin/main`.

**Contract inputs:** Publish without overwrite. Release body comes from `docs/release-notes/v2.33.3.md`.

**File ownership:** Julie Git refs, GitHub workflow/release, live evidence and publication pointers.

**Serialization required:** Yes.

**Dependency reason:** Requires Task 1's exact verified candidate; Miller needs the resulting live hashes.

**What to build:** Fast-forward the recovery work and release-prep commit to `origin/main`, run and monitor the release workflow, verify tag/assets/checksums/body, then commit and push live publication evidence and current-release pointers.

**Approach:** Use workflow dispatch/tag behavior documented in `docs/release.md`; never clobber. Re-run the release-state tripwire and reconcile local/remote/tag identities after publication.

**Acceptance criteria:**
- [x] `v2.33.3` is stable, non-draft, and targets the verified prep commit.
- [x] Four platform archives exist and their calculated SHA-256 values are recorded.
- [x] GitHub release body matches the committed notes.
- [x] `origin/main`, publication docs, and release-state checks are reconciled.

### Task 3: Pin Julie and prepare Miller 1.19.2

**Files:**
- Modify: `scripts/julie-pins.json`
- Modify: `src/Miller.Indexing/MillerExtractContract.cs`
- Modify: pin/version assertions under `tests/Miller.Tests/`
- Modify: `THIRD-PARTY-NOTICES.md`
- Create: `docs/findings/2026-08-16-julie-extract-2.33.3-adoption.md`
- Modify: `Directory.Build.props`
- Modify: `miller-plugin.json`
- Modify: `.claude-plugin/plugin.json`
- Modify: `.claude-plugin/marketplace.json`
- Modify: `.cursor-plugin/plugin.json`
- Modify: `.codex-plugin/plugin.json`
- Create: `docs/release-notes/v1.19.2.md`
- Modify: `docs/README.md`
- Modify: `README.md` only after live publication facts are available
- Modify: `docs/adr/ADR-0005-validated-store-pointer-adoption.md`

**Interfaces:**
- Consumes: Task 2 live Julie tag, archives, SHA-256 values, and recovery branch `041f305b`.
- Produces: a Miller `1.19.2` candidate whose package pin restores the exact published Julie `2.33.3` on all four targets.

**Contract inputs:** Semantic sidecar remains `0.1.0`; no MCP/schema/contract change; marketplace versions stay aligned.

**File ownership:** Miller Julie pins/contracts/assertions/notices, version/plugin manifests/assertions, release notes/maps, whitespace correction, release checkpoint.

**Serialization required:** Yes.

**Dependency reason:** Requires Task 2's live version, assets, hashes, and tag provenance.

**What to build:** Update all Julie pin surfaces and four hashes, restore the live binary, bump Miller/plugin/test metadata to `1.19.2`, document the recovery and remaining cold-index cost, and remove the existing diff-check failure.

**Approach:** Keep all schema and semantic constants unchanged. Run the complete Miller branch gate on the exact prep tree with the published Julie binary restored.

**Acceptance criteria:**
- [x] Every Miller pin/version/plugin/assertion surface consistently names Julie `2.33.3` and Miller `1.19.2`.
- [x] Restored Julie binary and all pin hashes match the live release.
- [x] Release notes distinguish recovered hot/read paths from still-expensive cold full indexing.
- [x] Release build, fast/Scale, plugin, restore, doc-sync, and diff gates pass on the exact prep commit.
- [x] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

### Task 4: Integrate and publish Miller 1.19.2

**Files:**
- Modify after live publication: `README.md`
- Modify after live publication: `docs/README.md`
- Create after live publication: `docs/findings/2026-08-16-v1.19.2-release-verification.md`

**Interfaces:**
- Consumes: Task 3 release-prep commit and the package-only/promote workflow.
- Produces: live stable `v1.19.2`, four archives plus four checksum sidecars, release notes body, marketplace-compatible main, and reconciled evidence.

**Contract inputs:** Use `publish=false` package validation first, then promote that exact successful run with `publish=true`; overwrite remains false.

**File ownership:** Miller Git refs, GitHub package/promotion workflows, live release body/evidence/current-release docs.

**Serialization required:** Yes.

**Dependency reason:** Requires Task 3's exact integrated pin and green branch gate.

**What to build:** Fast-forward the recovery and release-prep commits to `origin/main`, validate packages, promote the exact run, set the release body, verify assets/checksums/plugin URLs, then commit and push live release evidence and public current-release links.

**Approach:** Do not rebuild during promotion. Keep the dirty primary Miller checkout untouched; all release work runs from the clean recovery worktree and every outstanding worktree is reported at closeout.

**Acceptance criteria:**
- [x] Package-only and promotion runs succeed for the exact prep commit.
- [x] `v1.19.2` is stable with four archives, four matching `.sha256` sidecars, and the committed release notes body.
- [x] Marketplace manifests and download URLs resolve to live assets.
- [x] Local HEAD, remote main, tag, and release facts are reconciled; unrelated main-checkout changes remain untouched and reported.
