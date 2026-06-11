# Version-aware leadership — design

**Date:** 2026-06-11
**Status:** approved design, pre-implementation
**Motivation:** Twice in one day the packaged plugin server (bundling an older `julie-extract`) won the
indexer-lock race for this dev workspace and would have re-extracted newer data with an older binary.
The same failure hits end users on every upgrade: a running old instance keeps leading until it exits,
and the only remedy today is manually killing processes. julie's daemon era proved how long this class
of bug festers when left unguarded. Leadership must become version-aware, self-healing, and never
require a user to kill a process — on any OS.

## Invariant

**The artifact's `artifact_metadata.binary_version` never goes backwards.** No code path may replace
extract data produced by extractor version X with data produced by version Y < X, except through the
explicit escape hatch below.

## Context (what exists today)

- Leadership = first process to open `.miller/indexer.lock` exclusively
  ([`SingleWriterLock`](../../src/Miller.Indexing/SingleWriterLock.cs)); readers retry every 5s
  ([`IndexerService`](../../src/Miller.Server/Hosting/IndexerService.cs)). No fitness check, no handoff,
  no preemption.
- [`LeaderIdentityFile`](../../src/Miller.Server/Hosting/LeaderIdentityFile.cs) (`leader.json`) records
  pid / miller version / process path / start time, with a liveness probe (pid-reuse guard, Windows
  elevation tolerance).
- [`LeaderScanRequestQueue`](../../src/Miller.Server/Workspaces/LeaderScanRequestQueue.cs)
  (`.miller/requests/`) carries `full-scan` and `file-converge` requests with 10-minute TTL and
  claim-by-rename; the leader drains every 250ms.
- [`JulieExtractVersionProbe`](../../src/Miller.Indexing/JulieExtractVersionProbe.cs) can read the
  bundled binary's version; today it is diagnostic only.
- `artifact_metadata.binary_version` records which extractor produced the artifact; **nothing consults
  it before indexing** — that is the hole.

## Decisions

### D1 — Fitness axis is the extractor version, not the miller version

Data quality tracks `julie-extract`. Each process probes its bundled binary's version once at startup
(reusing `JulieExtractVersionProbe`); that value is its fitness. Miller's own version stays in
`leader.json` for diagnostics only. Comparison is semver-style on `major.minor.patch` numeric fields
(prerelease/build metadata ignored; the pin never uses them).

### D2 — Claim gate: ineligible instances never claim leadership

Before attempting `TryAcquire`, an instance evaluates eligibility:

| Condition | Verdict |
|---|---|
| own extractor version ≥ artifact `binary_version` | eligible |
| own extractor version < artifact `binary_version` | **ineligible** — permanent reader |
| no artifact yet (first scan) | eligible (any version may bootstrap) |
| artifact `binary_version` missing/unparseable (pre-contract artifact) | eligible (cannot prove a downgrade) |
| own probe fails / binary missing | **ineligible** (cannot index anyway; existing restore-script message applies) |
| `MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1` | eligible regardless (explicit escape hatch) |

Consequence accepted by design: when ONLY older instances are running, the index **freezes** —
stale-but-correct reads, never silent regression — and status/health say exactly why. The eligibility
decision is a pure function (`LeadershipEligibility.Evaluate`) so the verdict matrix is fast-suite
testable.

The same gate guards the CLI's one-shot lock acquisition (`refresh --full` path): an outdated CLI
refuses a rebuild with the same message instead of regressing the artifact.

### D3 — Auto-upgrade rescan

On claiming leadership, if own extractor version is **strictly greater** than the artifact's
`binary_version`, the leader schedules a forced full scan immediately (same mechanism as a drained
full-scan request). Upgrades self-heal with zero user action. The rescan is logged and visible in
status while running.

### D4 — Yield protocol: graceful preemption of a live older leader

New request type in the existing queue: `<stamp>-<pid>-<id>.yield.json` carrying
`{ schema_version, operation: "yield", request_id, workspace_id, requester_pid,
requester_extractor_version, created_at_utc }`. Same TTL (10 min) and claim-by-rename semantics as the
other request types.

- **Requester side:** a reader that is eligible AND has a strictly greater extractor version than the
  live leader's `leader.json.ExtractorVersion` enqueues a yield request (at most one outstanding;
  re-enqueue only after TTL or leader change).
- **Leader side (drain, 250ms tick):** if the request's version is strictly greater than its own, the
  leader finishes the current extract step, deletes `leader.json`, releases `indexer.lock`, and
  continues as a reader.
- **Anti-flap cooldown:** after yielding, the old leader suppresses claim attempts for 60s AND while
  the requester pid is alive (liveness probe) — so the newer instance's 5s retry wins the re-race.
  If the requester dies without claiming, the cooldown expires and the old leader may resume
  (it is still version-eligible relative to the artifact).
- **Equal versions never yield** — swarms of same-version agents (subagents, parallel sessions)
  cannot thrash leadership.
- **Old binaries** ignore unknown request patterns; a yield request against a pre-feature leader rots
  until the TTL sweep removes it. No worse than today; documented limitation.

### D5 — leader.json gains ExtractorVersion (back-compat)

`LeaderIdentity` gains nullable `ExtractorVersion`. Files written by older builds deserialize with
`null`; a `null` leader version is treated as *unknown* — readers may still send a yield (the old
leader simply won't drain it), and health reports the leader's extractor as unknown rather than
guessing.

### D6 — Surfaces: nobody ever needs Task Manager

- `workspace status` role string: `leader`, `reader`, or `reader (extractor outdated: own 2.1.3 < index 2.3.0)`.
- `workspace health` warnings: `leader_extractor_older_than_artifact` (live leader is outdated),
  `index_frozen_extractor_outdated` (no eligible writer exists), plus a yield-in-progress fact when a
  handoff is pending.
- All transitions logged with `pid`/`role` properties in the shared daily log.

### D7 — Swarm behavior is unchanged by design

Many same-version instances on one workspace keep today's proven model: one leader, N readers,
converge-queue write-through. All new logic activates only on version *inequality*.

## Module shape

| Piece | Location | Why |
|---|---|---|
| `LeadershipEligibility` (pure verdict) + version compare | `Miller.Indexing` (beside `JulieExtractVersionProbe`) | locality with the probe; ms-fast tests |
| artifact `binary_version` reader (tolerant, like `ReadRootPath`) | `Miller.Indexing` | same read conventions |
| claim-loop changes, cooldown, auto-rescan trigger | `Miller.Server/Hosting/IndexerService.cs` | orchestration only — no decision logic inline |
| `ExtractorVersion` field | `Miller.Server/Hosting/LeaderIdentityFile.cs` | existing identity record |
| `RequestYield` / `DrainYieldRequests` | `Miller.Server/Workspaces/LeaderScanRequestQueue.cs` | reuses TTL + claim-by-rename machinery |
| status/health facts + renders | existing health/render files | additive fields only |

No MCP/CLI signature changes; all surface changes are additive output fields.

## Testing

- **Fast suite:** eligibility verdict matrix (older/equal/newer/no-artifact/missing-version/probe-failure/
  escape-hatch); yield request round-trip, TTL expiry, claim-skip, dedup (mirroring existing queue
  tests); `leader.json` back-compat (old file without `ExtractorVersion`); auto-rescan decision;
  cooldown state machine (pure).
- **Scale suite:** one end-to-end handoff test — process A (older fitness, simulated) leads, process B
  (newer) enqueues yield, A abdicates, B claims and force-rescans; assert artifact `binary_version`
  advances and never regresses. Tagged `[Trait("Category","Scale")]` per repo rules.

## Acceptance criteria

- [ ] An instance whose bundled extractor is older than the artifact never acquires the indexer lock
      (verified by fast tests over `LeadershipEligibility` and by the claim loop honoring the verdict).
- [ ] With only outdated instances running, reads keep working, writes freeze, and
      `workspace status`/`health` state the reason and remedy.
- [ ] A newer-extractor leader auto-runs a forced full scan when the artifact predates it; artifact
      `binary_version` advances without user action.
- [ ] A newer-extractor reader displaces an older live leader via the yield protocol with no process
      kills, on a timescale of seconds; the old leader cannot immediately re-claim (cooldown).
- [ ] Equal-version instances never yield to each other (no leadership thrash under agent swarms).
- [ ] `MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1` permits an intentional downgrade and is the only path that does.
- [ ] CLI one-shot lock paths honor the same gate.
- [ ] All existing fast+scale tests stay green; new tests cover the matrix above.

## Out of scope

- Teaching pre-feature (already-shipped) binaries to drain yield requests — impossible retroactively;
  documented limitation, mitigated by the claim gate once those instances restart.
- Dev-machine plugin-vs-source double-install hygiene (user action: don't install the plugin where the
  source server is configured; see also `leader_extractor_older_than_artifact` warning which makes the
  collision visible).
- Multi-workspace registry changes — this design is per-workspace.
