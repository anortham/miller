# Version-Aware Leadership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Indexer leadership becomes extractor-version-aware: outdated instances never claim, newer leaders auto-upgrade the artifact, and a newer instance gracefully displaces an older live leader — no manual process kills on any OS.

**Architecture:** Pure eligibility verdict in `Miller.Indexing` (beside the existing version probe); a new `yield` request type on the existing TTL/claim-by-rename queue; `IndexerService` orchestrates claim gate, auto-rescan, yield drain, and anti-flap cooldown via its existing injectable-func test pattern. Additive status/health fields only.

**Tech Stack:** .NET 10, xUnit, SQLite (read-only artifact metadata), file-based coordination (FileStream locks, atomic renames).

**Architecture Quality:** Approved shape per design doc ([2026-06-11-version-aware-leadership-design.md](2026-06-11-version-aware-leadership-design.md)): decision logic pure and fast-testable; orchestration-only changes in `IndexerService`; queue machinery reused, not duplicated. Risk: medium (leader-election hot path) — mitigated by the verdict matrix in the fast suite and one Scale handoff test.

**Spec:** [docs/plans/2026-06-11-version-aware-leadership-design.md](2026-06-11-version-aware-leadership-design.md) — D1–D7 and the acceptance criteria there are the contract. On any conflict between this plan and the spec, the spec wins; report mismatches rather than redesigning locally.

---

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section) + `scripts/test.sh`.

**Worker red/green scope:** `dotnet test Miller.slnx -c Release --filter "FullyQualifiedName~<NewTestClass>"` for the task's test class(es). Build first: `dotnet build Miller.slnx -c Release` (0 warnings — warnings are errors).

**Worker ceiling:** `scripts/test.sh` (fast suite, <30s budget). Workers do not run the scale suite except Task 5's owner.

**Worker gate invariant:** each task's acceptance criteria below; tests prove behavior through the same entry points production uses (`LeadershipEligibility.Evaluate`, queue `Request*/Drain*`, `IndexerService` `*ForTest` hooks, render/facts functions).

**Lead affected-change scope:** `scripts/test.sh` after each task review.

**Branch gate:** `scripts/test.sh all` (fast + scale) before finishing; scale suite skips (not fails) if `.tools/julie-extract` is missing, but on this machine it is present and must pass.

**Escalation triggers:** any change to `SingleWriterLock` semantics, lock acquisition ordering, or watcher lifecycle beyond the plan → stop and report. A test that needs weakening → stop and report.

**Assigned verification failure:** Workers stop and report when assigned verification fails.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, timestamp in the task report. Reuse same-HEAD passing evidence rather than rerunning expensive gates.

## Model Routing

**Project source of truth:** none (`RAZORBACK.md` absent; no harness policy) — using `inherit` everywhere.

**Strategy tier / Implementation tier / Mechanical tier / Gate-interpretation reviewer / Escalation tier:** inherit (Claude Code session model).

**Worker eligibility:** all tasks below are implementation-tier; Task 6 docs half is mechanical but bundled with contract-test ownership, so it stays implementation-tier.

**Mechanical exclusion:** none dispatched purely mechanical.

**Unsupported harness behavior:** n/a — single-model session, `inherit` noted.

---

## TDD expectation

Every task is test-first: write the failing test mirroring the named existing test file's conventions, watch it fail, implement minimally, watch it pass, commit. Fast-suite purity rules apply (no subprocess, no real julie-extract) except Task 5's Scale test, which MUST be tagged `[Trait("Category","Scale")]` and obtain the binary via `ScaleTestSupport.RequireJulieServer()`.

---

### Task 1: Pure eligibility verdict + artifact binary_version reader

**Files:**
- Create: `src/Miller.Indexing/LeadershipEligibility.cs`
- Create: `src/Miller.Indexing/ExtractBinaryVersionReader.cs`
- Test: `tests/Miller.Tests/Indexing/LeadershipEligibilityTests.cs`
- Test: `tests/Miller.Tests/Indexing/ExtractBinaryVersionReaderTests.cs`

**What to build:** The pure decision core (spec D1/D2). `LeadershipEligibility.Evaluate(ownExtractorVersion, artifactBinaryVersion, allowDowngrade)` returns a verdict record `(bool Eligible, bool ArtifactOlderThanOwn, string Reason)` implementing the D2 matrix exactly, including: no artifact → eligible; missing/unparseable artifact version → eligible; own probe null → ineligible; `allowDowngrade` → always eligible. Include a numeric `major.minor.patch` comparer (`CompareVersions`) that ignores prerelease/build suffixes (reuse `JulieExtractVersionProbe.ParseVersion`'s regex approach for normalization). `ExtractBinaryVersionReader.TryRead(dbPath)` reads `artifact_metadata.binary_version` read-only, returning null on missing file/table/key/unreadable — mirror the tolerance pattern of `ExtractReader.ReadRootPath` (`src/Miller.Indexing/ExtractReader.cs:273`).

**Approach:** Pure statics, no I/O in `LeadershipEligibility` (the reader is its only I/O neighbor). The `Reason` string is user-facing (it lands in status/health), so phrase it like `"extractor 2.1.3 is older than the index artifact 2.3.0; this instance serves reads only"`. Use `JulieDbFixture` for reader tests (`tests/Miller.Tests/Indexing/JulieDbFixture.cs` already writes `artifact_metadata`).

**Acceptance criteria:**
- [ ] Verdict matrix covered: older / equal / newer / no-artifact / unparseable-artifact / null-own-version / allowDowngrade — one test each minimum.
- [ ] `CompareVersions("2.10.0","2.9.9") > 0` (numeric, not lexicographic) pinned by test.
- [ ] Reader returns null (never throws) for: missing db file, missing table, missing key.
- [ ] Worker-scope verification passes, committed.

### Task 2: leader.json ExtractorVersion + yield request type on the queue

**Files:**
- Modify: `src/Miller.Server/Hosting/LeaderIdentityFile.cs` (record at :13, json context at :161)
- Modify: `src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs`
- Test: extend the existing queue/identity test files (locate via `LeaderScanRequestQueue` / `LeaderIdentityFile` references in `tests/Miller.Tests/`)

**What to build:** Spec D4 (transport) + D5. (a) `LeaderIdentity` gains `string? ExtractorVersion` (last positional param; old files deserialize to null — pin with a back-compat test that parses a JSON literal without the field). (b) New queue operation `yield`: `YieldRequest` record (`schema_version, operation:"yield", request_id, workspace_id, requester_pid, requester_extractor_version, created_at_utc`), suffix `.yield.json`, `RequestYield(millerDir, workspaceId, requesterPid, requesterExtractorVersion)`, and `DrainYieldRequests(millerDir)` returning `YieldDrainResult(string? MaxRequesterVersion, int RequesterPid, bool Requested, int ExpiredDiscarded, int ClaimSkipped)` — when multiple yield requests exist, drain all claims and surface the max version (highest bidder wins). Reuse the existing stamp/TTL/claim-by-rename/sweep helpers verbatim (`TryClaim` :242, `SweepExpiredClaims` :261, `IsExpired` :279).

**Approach:** Mirror `RequestFullScan`/`DrainFullScanRequests` structure 1:1 including atomic temp→move write, TTL discard counting, claim-skip counting, and the `.claimed` sweep registering the new suffix. Add the new records to `LeaderScanRequestJsonContext` (source-generated serializer).

**Acceptance criteria:**
- [ ] Yield round-trip: enqueue → drain returns version+pid; file removed.
- [ ] Expired yield (TTL) discarded and counted, not serviced.
- [ ] Claim-contention skip path covered (mirror existing claim-skip test).
- [ ] Two yields with different versions → drain reports the max version.
- [ ] `LeaderIdentity` JSON without `extractorVersion` deserializes with null (back-compat pinned).
- [ ] Worker-scope verification passes, committed.

### Task 3: IndexerService orchestration — claim gate, auto-rescan, yield, cooldown

**Files:**
- Modify: `src/Miller.Server/Hosting/IndexerService.cs` (`ExecuteAsync` :144 claim/retry loop; drain wiring near `TryProcessLeaderFullScanRequests` :388; internal ctor :99 for injectable funcs)
- Test: `tests/Miller.Tests/Server/IndexerServiceLeadershipTests.cs` (new; follow the existing `*ForTest` hook pattern used by current IndexerService tests)

**What to build:** Spec D2 (gate), D3 (auto-rescan), D4 (requester + leader sides, cooldown). In the claim/retry loop:
1. **Gate:** before `_tryAcquireLeadership`, evaluate `LeadershipEligibility` (own version from a new injectable `Func<string?>` probing the bundled binary via `JulieExtractRunner.QueryVersion` (:283) once and caching; artifact version via `ExtractBinaryVersionReader`; `allowDowngrade` from env `MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1`). Ineligible → never claim; log once at Information with the verdict reason; expose the verdict via an internal property for status/health (Task 4).
2. **Auto-rescan:** on successful claim, write `leader.json` including `ExtractorVersion`; if verdict says artifact is strictly older than own → force a full scan through the existing `ScanAsLeaderUnderGate(force:true, source:"extractor-upgrade")` path.
3. **Requester side:** in the reader retry tick (5s), if eligible AND `LeaderIdentityFile.TryRead` shows a live leader whose `ExtractorVersion` is non-null and strictly lower than own → `RequestYield` (guard: at most one outstanding — remember request id/time; re-enqueue only after `LeaderScanRequestQueue.RequestTtl` or observed leader change).
4. **Leader side:** drain yield requests on the 250ms tick alongside the other drains; if drained max version strictly greater than own → finish current tick's work, delete `leader.json`, dispose the lease, enter cooldown, continue as reader.
5. **Cooldown:** suppress claim attempts for 60s AND while the recorded requester pid is alive per `LeaderIdentityFile.IsProcessAlive(pid)`; expired/dead → resume normal retry. Keep cooldown state as a small pure-testable helper (e.g. internal `YieldCooldown` class with injected clock).

**Approach:** Follow the existing injectable-func constructor pattern (`_drainFullScanRequests` etc.) so every branch is fast-testable without real locks. Add `*ForTest` hooks mirroring `ProcessLeaderFullScanRequestsForTest` (:689). Equal versions must never yield (strictly-greater everywhere). Do not touch `SingleWriterLock` itself.

**Acceptance criteria:**
- [ ] Ineligible instance never invokes the acquire func (asserted via injected spy).
- [ ] Claim with older artifact triggers exactly one forced full scan.
- [ ] Reader with strictly greater version than live leader enqueues yield once (no repeat spam within TTL).
- [ ] Leader abdicates on strictly-greater yield: lease disposed, leader.json deleted; equal version → no abdication.
- [ ] Cooldown blocks re-claim while requester alive and <60s; resumes after requester death or expiry (injected clock + probe).
- [ ] Worker-scope verification passes; fast suite (`scripts/test.sh`) green; committed.

### Task 4: CLI gate + status/health surfaces

**Files:**
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` (one-shot lock path used by `refresh --full` / `WorkspaceRefresh`)
- Modify: `src/Miller.Server/Tools/WorkspaceHealthFacts.cs` (`LeaderHealthFacts` :23, `Read` :29, `AddLeaderWarnings` :145)
- Modify: `src/Miller.Server/Tools/WorkspaceRender.cs` (`LeaderLabel` :458, `WriteLeaderJson` :468, status role string)
- Modify: `docs/contracts/workspace-health-v1.md` (additive fields)
- Test: extend `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`, `tests/Miller.Tests/Server/CliDispatchTests.cs`, and the WorkspaceHealthFacts test file

**What to build:** Spec D2 (CLI) + D6. (a) CLI: before any one-shot lock acquisition that leads to a scan, run the same `LeadershipEligibility` check; ineligible → refuse with the verdict reason and a remedy hint (exit non-zero for `--full`), honoring the env escape hatch. (b) `LeaderHealthFacts` carries the leader's `ExtractorVersion` plus this process's own version and verdict; new warnings `leader_extractor_older_than_artifact` and `index_frozen_extractor_outdated` (no eligible writer: this process ineligible AND (no leader or leader ineligible)) with recommended actions ("upgrade miller / restore the pinned extractor; set MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1 only for intentional downgrades"). (c) Status role label: `leader` / `reader` / `reader (extractor outdated: own X < index Y)`; health JSON `indexer_leader` block gains `extractor_version`.

**Approach:** All additive — never rename or remove existing JSON fields (Eros contract). Update the contract doc in the same commit as the fields. Keep warning-construction logic in `WorkspaceHealthFacts` (pure, testable) like `AddLeaderWarnings` does today.

**Acceptance criteria:**
- [ ] Outdated CLI `refresh --full` refuses with reason + non-zero exit; allowed with env hatch (both pinned by tests).
- [ ] Health JSON exposes leader `extractor_version`; warnings fire in the right states (outdated live leader; frozen index) and not otherwise.
- [ ] Status role string renders the outdated-reader form.
- [ ] `docs/contracts/workspace-health-v1.md` documents the additive fields.
- [ ] Worker-scope verification passes, committed.

### Task 5: Scale handoff test (end-to-end)

**Files:**
- Test: `tests/Miller.Tests/Server/VersionAwareLeadershipScaleTests.cs` (new, `[Trait("Category","Scale")]` at class level)

**What to build:** The spec's acceptance scenario in one process with two `IndexerService` instances on a shared temp workspace (FileStream locks contend in-proc the same as cross-proc): instance A configured (via injected version func) as older-fitness leader; instance B newer. Assert: B enqueues yield → A abdicates (lease released, leader.json gone) → B claims → B's forced rescan runs the REAL `julie-extract` (via `ScaleTestSupport.RequireJulieServer()`) → `artifact_metadata.binary_version` equals the real binary's version and never regressed during the sequence. Also assert A respects cooldown (does not re-claim while B holds).

**Approach:** Use the internal ctor's injectable funcs for versions/probes; only the final rescan touches the real binary. Drive ticks via the `*ForTest` hooks rather than wall-clock waits wherever possible; bound any waits tightly.

**Acceptance criteria:**
- [ ] Full handoff sequence asserted, including binary_version never going backwards.
- [ ] Test skips (not fails) when `.tools/julie-extract` is missing.
- [ ] `scripts/test.sh scale` green; committed.

### Task 6: Docs + CLAUDE.md + final gate

**Files:**
- Modify: `CLAUDE.md` (Server host & startup section — short paragraph on version-aware leadership: claim gate, auto-rescan, yield, escape hatch; pointer to the design doc), then run `scripts/sync-agents.sh`
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` only if `AgentInstructionsTests` requires it (no new tools — likely untouched)
- Verify: full branch gate

**What to build:** Document the invariant where future agents will see it; confirm `cmp -s CLAUDE.md AGENTS.md`. Run `scripts/test.sh all` as the branch gate and report the ledger.

**Acceptance criteria:**
- [ ] CLAUDE.md updated + AGENTS.md regenerated and identical.
- [ ] `scripts/test.sh all` fully green at final HEAD.
- [ ] All eight spec acceptance criteria check off against implemented behavior; any gap reported, not papered over.

---

## Task ordering

Task 1 → Task 2 (independent of 1, may run in parallel) → Task 3 (needs 1+2) → Task 4 (needs 1; parallel with 3 except shared health files — serialize 4 after 3 to avoid conflicts on IndexerService-exposed verdict) → Task 5 (needs 3) → Task 6 (last).

Parallel-safe pairing: dispatch Tasks 1 and 2 concurrently; then 3; then 4; then 5; then 6.
