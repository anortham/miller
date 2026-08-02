# Multi-Worktree Fleet Safety — Consensus Fix Plan

> **For agentic workers:** this is a two-repo program plan, not a single-session execution plan. Each
> workstream below becomes its own razorback implementation plan (razorback:writing-plans →
> razorback:subagent-driven-development) in its owning repo when picked up. Do not begin
> implementation from this document without explicit user approval.

**Goal:** make Miller safe by default under multi-worktree agent fleets — N concurrent worktree
agents on one machine must converge to ready indexes without OOM cascades, spool leaks, ignore-rule
loss, or rescan storms.

**Provenance:** consensus outcome of a two-round cross-model convergence on
[`docs/findings/2026-08-01-multi-worktree-fleet-triage.md`](../findings/2026-08-01-multi-worktree-fleet-triage.md)
(user field report, 2026-08-01). Round 1: independent read-only reviews by Codex and Grok over both
repos; every disputed claim was re-verified in code by the lead before adoption. Round 2: both models
voted on the synthesized ten-item plan — Grok 10/10 AGREE; Codex 9/10 with one objection (Unix
process groups are not a kill-on-parent-death mechanism), which was adopted verbatim as the amended
item 9; both then voted AGREE on the amendment. Final: **unanimous**.

**Architecture:** all control-plane fixes land in Miller (`Miller.Indexing` / `Miller.Server`);
julie-extractors gains three small opt-in contract flags (`--spool-dir`, `--progress-file`,
`--parent-pid`) plus a dead-PID spool reaper, shipped and pin-bumped before Miller wires them.
Nothing changes in parser/extraction semantics; the engine is explicitly kept.

**Status:** awaiting user approval. Written while the 1.15.0 release session was in flight — nothing
here is implemented.

## Global Constraints

- Miller stays a read-only consumer of julie-extract output; extraction semantics stay in
  julie-extractors, added across all supported languages when extractor changes are needed.
- No new MCP tools; all new state surfaces through existing `workspace status`/`health` JSON, CLI
  verbs, or the dashboard (MCP-stinginess rule).
- `Miller.Core` remains I/O-free; new I/O lives in `Miller.Indexing`/`Miller.Server`.
- Lexical-only search output stays byte-identical; nothing here touches ranking or fusion.
- Fast suite stays fast: argv/policy/backoff/lease-decision logic gets pure unit tests; anything
  spawning julie-extract or killing real processes is `[Trait("Category","Scale")]` via
  `ScaleTestSupport.RequireJulieServer()`.
- julie-extract flag additions are opt-in with unchanged defaults, so older Millers and direct CLI
  users are unaffected; Miller adopts them only behind a `scripts/julie-pins.json` bump.
- No release/publish/pin-bump without explicit user approval.

## Workstreams

### P0 — stop destructive fleet behavior

**W1. Close the bootstrap lease hole** — Miller. ~1 agent session.
- Modify: `src/Miller.Server/Hosting/IndexBootstrapService.cs` (`RunBootstrap`, ~lines 369–398):
  acquire the per-workspace `SingleWriterLock` *before* the bootstrap scan decision; re-check DB
  existence + recorded root after acquisition; if another process holds the lock, poll/wait and load
  whatever it produced instead of executing the stale pre-lock decision (mirror the existing
  `AcquireWriteLockForAutoRebuild` skip-and-retry shape at line 422).
- Modify: `src/Miller.Indexing/SingleWriterLock.cs` doc comment — remove the stale
  "julie's `<db>.julie-extract.lock` 30s flock" backstop claim (verified: no flock exists in current
  julie-extractors).
- Test: fast injected-concurrency tests (two in-process bootstrap decisions racing a fake lock);
  live two-process coverage is Scale.
- Acceptance:
  - [ ] No bootstrap path reaches `runner.Scan` without holding the workspace writer lock.
  - [ ] Loser of the race loads the winner's artifact rather than scanning.

**W2. Cap extraction parallelism** — Miller. ~0.5 agent session.
- Modify: `src/Miller.Indexing/JulieExtractRunner.cs` (`BuildScanArgs`, ~line 126): always pass
  `--jobs <n>`; default `min(4, max(1, Environment.ProcessorCount / 2))`; `MILLER_EXTRACT_JOBS`
  overrides; `0` is an explicit opt-in to rayon auto. (julie-extract already has `--jobs`/`-j`,
  crates/julie-extract-cli/src/args.rs:49 — no extractor change needed.)
- Consumed by W8: after an exit-137/SIGKILL failure the next automatic attempt runs `--jobs 1`.
- Test: pure argv unit tests.
- Acceptance:
  - [ ] Every scan argv carries `--jobs`; default matches the formula; env override honored.

**W3. Machine-wide scan governor** — Miller. ~1–2 agent sessions.
- Create: `src/Miller.Indexing/ScanGovernor.cs` (+ registration in
  `src/Miller.Server/Hosting/MillerServiceRegistration.cs`): user-global OS-exclusive lease file
  under `~/.miller/` (same pattern as the semantic accelerator lease), capacity 1.
- Held for the full extractor subprocess lifetime **and** the synchronous content/search sidecar
  convergence that follows a successful scan (`ScanAsLeaderUnderGate` →
  `TryConvergeSidecar`, `src/Miller.Server/Hosting/IndexerService.cs:672–676`), so scan+sidecar
  storms cannot overlap across workspaces. Per-file `update`/`delete` converges are exempt.
- Crash recovery via OS handle release; adjacent owner-diagnostics JSON (pid, workspace, reason,
  jobs, started-at) is informational only, never authority. Jittered polling; no FIFO tickets in v1.
- Surfaced in existing `workspace status`/`health` output ("scan waiting on machine governor");
  no new MCP tool.
- Test: pure lease-decision units fast; multi-process contention in Scale.
- Acceptance:
  - [ ] N concurrent `workspace open --full` on sibling worktrees run ≤1 extractor at a time.
  - [ ] Kill -9 of the holder frees the lease without manual cleanup.
  - [ ] Waiting state visible in `workspace status --json`.

**W4. Spool containment + reaping** — julie-extractors first, then Miller. ~1 session (julie) +
~0.5 session (Miller wiring after pin bump).
- julie-extractors: add `--spool-dir <path>` to `ScanArgs` (crates/julie-extract-cli/src/args.rs);
  `create_scan_spool` (crates/julie-extract-cli/src/commands.rs:1446) uses it, defaulting to the
  current `std::env::temp_dir()`. On scan startup, reap `julie-extract-scan-spool-*-*.jsonl` in the
  spool dir whose embedded PID is provably dead (access-denied probe = alive; never reap by age
  alone). Keep the `Drop` cleanup (commands.rs:1058).
- Miller: pass `--spool-dir <workspace>/.miller/tmp`; after observing child exit/kill, delete the
  exact child PID's spool files best-effort.
- Why `.miller/tmp`: shared `/tmp` is a container tmpfs/memory trap and a multi-user disk-fill
  hazard (findings §6.8), and workspace-local spools are trivially discoverable.
- Acceptance:
  - [ ] SIGKILL mid-scan leaves at most one dead-PID spool, removed by the next scan's reaper.
  - [ ] Spools live under `.miller/tmp` when Miller drives the scan.

**W5. Extraction progress observability** — julie-extractors first, then Miller. ~1 session (julie)
+ ~0.5–1 session (Miller).
- julie-extractors: opt-in `--progress-file <path>`, written at most once per second and only when
  discovery/extraction/spool/artifact-write counters advance.
- Miller: pass a nonce-named file under `.miller/tmp`; fold its counter/mtime into `ProgressStamp`
  (`src/Miller.Indexing/JulieExtractRunner.cs:629` — currently blind to the pre-DB extraction/spool
  phase, which is how healthy large scans get killed as "stalled" at the 10-minute window); delete
  it in `finally`. Keep the 10-minute no-progress stall window; raise the absolute hard cap
  (`ExtractWaitPolicy`, `src/Miller.Indexing/ExtractWaitPolicy.cs` — currently stall × 6 = 60 min)
  to 4 hours, env-overridable.
- Acceptance:
  - [ ] A synthetic long-spool-phase scan (Scale) survives past the old stall window.
  - [ ] A genuinely hung extractor is still killed at the stall window.

**W6. Orphan-child containment (amended item 9 mechanism)** — julie-extractors + Miller.
~0.5–1 session (julie) + ~0.5–1 session (Miller).
- julie-extractors: parent-liveness watchdog — Miller passes `--parent-pid <pid>`; the extractor
  polls parent aliveness (~2s) and also treats closure of its stdout pipe as parent death, exiting
  promptly when orphaned.
- Miller: Windows uses a kill-on-close Job Object; POSIX process groups are used **only** for
  descendant cleanup on Miller's own kill/timeout paths, NOT as the kill-on-parent-death mechanism
  (`PR_SET_PDEATHSIG` is Linux-only; macOS has no equivalent — the watchdog is the portable
  primitive). This amendment was Codex's round-2 objection, adopted verbatim; both models AGREE.
- Acceptance:
  - [ ] Kill -9 of the Miller host leaves no julie-extract running beyond the watchdog interval
        (Scale, POSIX) / Job Object close (Windows).

### P1 — make failure recovery deliberate

**W7. Worktree ignore propagation + seeder rework** — Miller. ~1–1.5 sessions.
- Create: a git-worktree metadata adapter (resolve `.git`-file worktrees to their real git dir,
  common dir, and main checkout root) in `Miller.Indexing`.
- Modify: `src/Miller.Indexing/JulieExtractRunner.cs` `Scan`/`BuildScanArgs`: when the root is a
  linked worktree with no user-authored `.julieignore`, pass the main checkout's `.julieignore` via
  repeatable `--ignore-file` (already supported with caller precedence, args.rs:40). ALWAYS pass a
  Miller-owned invariant ignore file last (generated under `.miller/`, containing at minimum
  `.miller/`, `.worktrees/`, `.claude/worktrees/`) — this also closes the nested-worktree
  double-indexing hole generically.
- Modify: `src/Miller.Indexing/JulieIgnoreSeeder.cs` — keep seeding green-field roots; raise or
  restructure the `MaxEnumeratedFiles = 25_000` detection cap (name-based vendor-dir detection
  instead of full enumeration, or a much higher bound with a warning when hit).
- Modify: `src/Miller.Server/Hosting/WatchPathFilter.cs` — add `.worktrees` alongside the existing
  `.claude/worktrees` special case.
- Acceptance:
  - [ ] A fresh linked worktree scan applies the main checkout's `.julieignore` rules.
  - [ ] A >25k-file root no longer silently truncates vendor detection.
  - [ ] `.worktrees/` under a repo root is neither indexed by the parent workspace nor watched.

**W8. Persisted scan-failure policy** — Miller. ~1–2 sessions.
- Create: per-workspace scan-failure record under `.miller/` (intent, exit code, consecutive count,
  jobs used, `next_attempt_at`); jittered backoff 30s / 2m / 10m / 30m-max. Explicit
  `workspace full` bypasses the timer once but still records.
- Replace the orchestration-boundary `bool force` with a scan-intent enum: user-hygiene and
  watcher-overflow forces may downgrade to an incremental retry against a readable, root-matching
  live artifact; root-mismatch, schema-incompatibility, corruption, and extractor-upgrade rebuilds
  never downgrade. Serve a prior readable artifact with degraded health rather than claiming fresh.
- Exit-137/SIGKILL failures set the next automatic attempt to `--jobs 1` (consumes W2).
- Registry hygiene rider (lead addition, consistent with the vote — no auto row deletion in v1):
  CLI `workspace open` stops upserting `Ready` before the first scan completes
  (`src/Miller.Server/Cli/CliDispatch.cs:3130`); failed opens leave an error-state row, not `Ready`.
- Acceptance:
  - [ ] A failed force scan does not immediately re-force on any automatic path.
  - [ ] Kill→retry cycles show monotonically increasing spacing in the failure record.
  - [ ] Schema/corruption/upgrade rebuilds still always run force.

**W9. Linked-worktree watcher + identity fixes** — Miller. ~1–1.5 sessions.
- Modify: `src/Miller.Server/Hosting/IndexerWatcherSet.cs:69–70` — when `.git` is a file, resolve
  the real worktree git dir and watch its `HEAD` there, restoring the checkout-collapse signal
  (one rescan instead of a 64 KiB buffer overflow → forced-rescan storm per branch switch).
- Detach watchers and mark the workspace missing when the root disappears; on reappearance compare
  a git admin-dir generation/epoch and re-bootstrap instead of silently serving the old in-memory
  index (path-reuse identity risk, findings §6.6).
- Acceptance:
  - [ ] Branch switch in a linked worktree produces one reconcile scan, not an overflow rescan.
  - [ ] Deleting and recreating a different worktree at the same path triggers re-bootstrap.

### P2 — measure before deeper changes

**W10. Scale repro + WAL measurement** — julie-extractors (xtask) or Miller Scale fixture.
~1 session.
- Build a 74k-file fixture; kill scans during extraction and during artifact write; measure spool,
  RSS, DB, and WAL sizes separately. Only if a healthy force-to-`.rebuild` shows multi-GB WAL do we
  consider chunked commits (safe only for new/rebuild artifacts behind an explicit building/ready
  marker; live incremental scans stay transactionally atomic).
- Also validates W2–W6 end to end and produces the numbers for the reporter follow-up.

**Deferred / cut (unanimous):**
- Shared-artifact sibling-worktree bootstrap (clone/rebind + delta) — deferred; future shape is a
  julie-extractors-owned artifact contract; Miller never rewrites extractor metadata privately.
- Speculative WAL chunking without the W10 repro; parser changes; new MCP tools; automatic registry
  row deletion in v1; global hard-exclusion of `.worktrees` inside julie-extractors (Miller's
  invariant ignore file covers it).
- `MILLER_FULL_REBUILD_INPLACE=1` gains a loud startup warning (or removal — decide at W8 time).
- Flipping `MILLER_SEMANTIC` default — rejected; the governor absorbs the fleet cost instead.

## Sequencing

| Order | Item | Depends on |
|---|---|---|
| 1 | julie-extractors: `--spool-dir` + reaper (W4), `--progress-file` (W5), `--parent-pid` watchdog (W6) — one release, one pin bump | user approval to release |
| 2 (parallel with 1) | Miller: W1 bootstrap lock, W2 jobs cap, W3 governor — none need the new extractor flags | — |
| 3 | Miller wiring of W4/W5/W6 flags | pin bump from 1 |
| 4 | W7 ignore propagation, W8 failure policy, W9 watcher fixes | W2 (jobs-1 retry), W3 (status surface) |
| 5 | W10 scale repro; revisit WAL chunking and the deferred shared-artifact idea with data | W2–W6 landed |

Estimated total: **~9–12 agent sessions** across both repos, plus two human approval points (the
julie-extract release/pin bump, and each Miller release) and the reporter follow-up (versions,
root layout, worktree count — findings §7).

## Verification Strategy

**Project source of truth:** Miller `CLAUDE.md` (testing split + build guards); julie-extractors
workspace `cargo test` conventions.

**Worker red/green scope:** Miller — `scripts/test.sh` (fast suite, <30s tripwire) for every
change; pure unit seams for argv/lease/backoff/policy logic. julie-extractors — targeted
`cargo test -p julie-extract-cli` / `-p julie-extract-artifact`.

**Worker ceiling:** fast suite (Miller) / crate tests (julie). Broader gates belong to the lead.

**Lead affected-change scope:** `scripts/test.sh scale` whenever the extract/indexing path is
touched (W1–W9 all touch it); julie-extractors full `cargo test` + `xtask dogfood`.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh all` before
any PR; julie-extractors release checklist before the pin bump.

**Expensive tier / escalation:** W3 and W6 need real multi-process Scale tests (governor
contention, orphan reaping); W10 is itself the expensive measurement gate. Semantic broker soak
(`scripts/semantic-broker-soak.*`) only if governor work touches broker lease code paths.

**Success criteria for the program (from the consensus round):**
- [ ] N concurrent sibling-worktree `workspace open --full`: ≤1 extractor at a time, each with
      bounded `--jobs`, all N eventually ready — on the same root size that previously OOM'd.
- [ ] SIGKILL anywhere (child, leader, host) leaves no unbounded orphan extractor and at most one
      dead-PID spool, reaped on the next scan.
- [ ] A fresh worktree without a local `.julieignore` still applies the main checkout's rules.
- [ ] No automatic path immediately re-forces after a force failure.

## Architecture Quality

Approved shape (Codex proposal, adopted): a single scan-coordination seam — callers hand
`JulieExtractRunner`/the indexing layer a root, DB path, and scan *intent*; workspace locking,
machine admission (governor), jobs policy, ignore inputs, progress wiring, spool cleanup, and
backoff live behind that seam, never in callers. Main risk: the governor adds a cross-process wait
inside paths that today assume local-only locking — mitigated by surfacing wait state in
status/health and keeping per-file converges exempt.
