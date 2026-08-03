# 2026-08-01 — Multi-worktree agent-fleet failure: user feedback triage

**Status:** findings verified against `miller@main` (2026-08-01, post-1.15.0-prep, `7220c15d`) and the
local `julie-extractors` checkout (`173ae45`, 2.21.0 era). Cross-model review COMPLETE (2026-08-01):
Codex and Grok independently verified the claims, forced four verdict revisions (§2 claims 1/2/4/6, §5),
and contributed the additional failure modes in §6. Consensus fix plan (both models AGREE on all items):
[`docs/plans/2026-08-01-multi-worktree-fleet-safety-plan.md`](../plans/2026-08-01-multi-worktree-fleet-safety-plan.md).

**Context:** the reporter installed Miller roughly 2026-07-30, two days before the report — a current
release (v1.14.x line, julie-extract 2.20+ pin). Every confirmed finding below reproduces on current
code; no "stale version" explanation applies.

---

## 1. The feedback (verbatim)

> @allan I have some feedback. I took your product to scale, but it crashed my workflow, no one could
> resolve when I had multiple worktree agents processing. The SQL indexes just thrashed and never could
> converge. I have kind fo a triage report from gpt..
>
> Short answer: Miller/Julie are useful, but their current orchestration is not safe for automatic
> multi-worktree use. Do not replace the language-analysis engine wholesale; replace or wrap the unsafe
> workspace lifecycle.
>
> What the evidence shows:
>
> | Area | Finding | Verdict |
> |---|---|---|
> | Workspace identity | State lives under .miller per checkout, but multiple clients can target the same absolute worktree without a reliable shared writer lease. | Unsafe |
> | Multi-worktree behavior | Separate worktrees duplicate indexes; shared or stale server registrations can point at the wrong checkout. | Risky |
> | Ignore handling | miller workspace open --full did not propagate .julieignore to Julie. | Correctness bug |
> | Resource limits | Full root indexing touched roughly 74k files; all-core extraction exited 137, while a two-worker run approached 50 minutes and produced ~14 GB of WAL. | Unsafe by default |
> | Cleanup | Julie staging spools remained in temporary storage; historical spools accumulated to roughly 130 GB. | Operationally unsafe |
> | Parser quality | A focused ct-meta extraction produced valid symbols and passed health checks. | Engine is viable |
>
> So the problem is mainly the control plane: scope, locking, lifecycle, cleanup, and resource
> governance—not necessarily Julie's language extraction.

---

## 2. Claim-by-claim verification

| # | Claim | Verdict | Evidence |
|---|---|---|---|
| 1 | No reliable shared writer lease for one worktree | **Partially right** (revised after cross-model review) | Steady-state: `SingleWriterLock` (src/Miller.Indexing/SingleWriterLock.cs:16) takes an OS-exclusive `FileShare.None` lock on `.miller/indexer.lock`, and it works. **But initial bootstrap scans bypass it** (Codex finding, lead-verified): `RunBootstrap` decides from an unlocked `File.Exists`/root check and calls `runner.Scan` — including the *force* rebind path — with no lock (src/Miller.Server/Hosting/IndexBootstrapService.cs:391); only the auto-rebuild closure locks (line 422), and its own comment states the contract ("force-scan callers hold Miller's single-writer lock") that line 391 violates. Two fresh Miller processes on one worktree can scan the same DB concurrently. Also verified (Grok + Codex): the "julie's own `<db>.julie-extract.lock` 30s flock backstop" in `SingleWriterLock`'s doc comment is **stale** — no flock exists anywhere in current julie-extractors; Miller's lock is the only serialization, and it is **per-workspace**: N worktrees = N legitimate leaders with nothing machine-wide above them. |
| 2 | Worktrees duplicate indexes; stale registrations point at the wrong checkout | **Duplication true by design + hygiene gap; "wrong checkout" unproven** | `workspace_id` = SHA-256 of the canonical root, so every worktree is an independent full index. Miller has zero git-worktree awareness in the indexing path (no `--git-common-dir` use outside a measurement script). Registry cleanup (`workspace prune`) is manual, and CLI `workspace open` upserts the row as `Ready` *before* the first scan runs (src/Miller.Server/Cli/CliDispatch.cs:3130 — verified). "Wrong checkout" routing is unproven: rows retain exact canonical roots; the real adjacent risk is **path reuse** — delete a worktree and create a different one at the same path and the identity (and any live process's in-memory index) silently carries over. Nothing excludes nested worktree dirs (e.g. `.worktrees/` inside the repo — a layout Miller's own repo uses): if not gitignored, the parent workspace indexes them **and** each is opened as its own workspace, multiplying extraction load. |
| 3 | `workspace open --full` did not propagate `.julieignore` | **Real bug, different mechanism** | julie-extract discovery honors `.julieignore` (git-exact since v2.4.0, 2026-06-11; tests `root_julieignore_is_honored`, `nested_julieignore_is_honored`, `julieignore_overrides_gitignore_in_same_directory` in crates/julie-extract-cli/src/discovery.rs:660–730). But: (a) `.julieignore` is **untracked**, so a fresh linked worktree checkout never inherits the one written in the main checkout; (b) Miller's `JulieIgnoreSeeder.EnsureSeeded` (src/Miller.Indexing/JulieIgnoreSeeder.cs:47) seeds only when the file is absent, and its vendor-detection walk silently caps at `MaxEnumeratedFiles = 25_000` (line 24) — on the reporter's 74k-file root, detection saw a third of the tree; (c) Miller never passes julie-extract's repeatable `--ignore-file` flag (which exists, with precedence, crates/julie-extract-cli/src/args.rs:40). |
| 4 | Resource limits unsafe by default (74k files, exit 137, ~14 GB WAL) | **Confirmed** | julie-extract `scan` has `--jobs` (`-j`, default 0 = all cores, args.rs:49, present since v2.0.2). Miller's scan argv is `scan --root --db --strict-schema --json [--force]` — **no jobs cap ever passed** (src/Miller.Indexing/JulieExtractRunner.cs). No cross-workspace scan governor exists, so N worktree agents run N concurrent all-core scans; exit 137 is the OOM killer. Each extractor additionally configures a 128 MiB SQLite cache and `temp_store=MEMORY` (writer.rs), so N concurrent scans multiply worker pools, chunk buffers, and caches. Exit 137 is SIGKILL — *consistent with* the OOM killer but not proven without kernel logs. The 14 GB WAL is **unreproduced**: julie checkpoints (TRUNCATE) after a successful commit, so a huge WAL indicates a killed mid-write scan, not healthy steady state. Mechanism confirmed; field magnitudes are reporter evidence. |
| 5 | Spools accumulate in temp (~130 GB) | **Confirmed** | `create_scan_spool` (crates/julie-extract-cli/src/commands.rs:1446) writes `julie-extract-scan-spool-<pid>-<nanos>.jsonl` into `std::env::temp_dir()`. Cleanup is `impl Drop for SpooledExtractedFiles` (commands.rs:1058) — `Drop` never runs on SIGKILL/OOM. No startup reaper exists. Every OOM-killed scan (claim 4) leaks its spool: the resource bug *causes* the cleanup bug. |
| 6 | Engine viable (ct-meta extraction passed) | **Credible, not source-verifiable** (softened per cross-model review) | The reporter's ct-meta run is external evidence we cannot reproduce from source review. It is consistent with the extractor's test coverage and with `docs/findings/2026-07-29-miller-vs-bare-agent-calibration.md`, but "verified" would overstate it. |

## 3. Reconstructed failure chain (all on current code)

1. Big root (74k files after ignore gaps) + N worktree agents → N workspaces → N per-workspace
   leaders, each legitimately holding its own `indexer.lock`.
2. Each leader spawns an uncapped all-core julie-extract scan → memory pressure → OOM killer (exit
   137) takes out scans or whole Miller processes.
3. Every killed scan leaks its temp spool (`Drop` skipped on SIGKILL, no reaper) → ~130 GB over two
   days of retries.
4. A force scan has no resume — `FullRebuildPromotion.PrepareRebuildTarget`
   (src/Miller.Indexing/FullRebuildPromotion.cs:85) deliberately deletes any partial `.rebuild` trio
   before every attempt (correct for promote-not-merge, fatal under crash-looping). The retry engine is
   leader **failover** itself: when an OOM-killed leader's process dies the OS releases `indexer.lock`,
   another instance's 5s claim poll (`RunLeadershipSessionAsync`,
   src/Miller.Server/Hosting/IndexerService.cs:282) wins it and starts the force scan again from zero.
   No cross-session crash-loop detection or backoff exists. The artifact never reaches ready; every
   read tool returns not-ready after the `MILLER_BOOTSTRAP_GRACE_SECONDS` window. That is "the SQL
   indexes thrashed and never could converge," observed from the outside.
   *(Refinement from cross-model review, verified: this failover loop runs only when the Miller
   process itself dies. When only the julie-extract child is killed, the leader keeps the lock, logs
   "keeping the prior index", and nothing retries — a workspace with no prior index silently stays
   not-ready (src/Miller.Server/Hosting/IndexerService.cs:650–666). A fleet under memory pressure
   gets both modes: dead Miller processes → from-zero failover loops; dead children → permanently
   not-ready workspaces.)*
5. Ignore gaps amplify step 1: worktree checkouts lack the main checkout's untracked `.julieignore`;
   the seeder's 25k-file detection cap under-seeds large roots; nested worktree dirs (if not
   gitignored) are double-indexed by the parent workspace.

## 4. What the GPT triage got wrong

- "No reliable shared writer lease" — the lease exists and is correct for its scope. The gap is one
  level up: no machine-wide governance across workspaces. The prescription ("wrap the lifecycle")
  is still right.
- ".julieignore not propagated to Julie" — julie-extract reads it fine; the file simply never exists
  in fresh worktree checkouts, and Miller does nothing to bridge that.
- Framing Miller and Julie as one "orchestration" — the retired julie server was not involved;
  every control-plane surface here is Miller's (which is good news: Miller *is* the control plane,
  and owns every fix except the spool reaper and any WAL chunking, which are julie-extractors').

## 5. Notable non-bugs confirmed along the way

- Version-aware leadership, promote-not-merge rebuilds, and the sensitive-root guard all behaved as
  designed; none contributed to the failure.
- **Correction:** this doc originally listed `ExtractWaitPolicy`'s progress-based wait as a non-bug
  ("long healthy scans are not killed"). Cross-model review disproved that — see §6 item 2. The
  policy protects only the artifact-write phase.

## 6. Cross-model review: additional confirmed failure modes

Codex and Grok each reviewed this doc and both repos independently (2026-08-01); the items below
survived lead verification against the code. Verdict revisions they forced are already folded into
§2 (claims 1, 2, 4, 6) and §3.

1. **Bootstrap lease bypass** (Codex; verified) — folded into claim 1. The single most severe
   correction: initial bootstrap scans, including the force rebind path, run with no
   `SingleWriterLock`.
2. **Progress-stamp blindness** (Codex + Grok; verified) — `ProgressStamp` samples only artifact
   db/wal/shm sizes plus output lines (src/Miller.Indexing/JulieExtractRunner.cs:627–639), but
   julie-extract spends its long extraction phase growing a temp spool and opens the artifact DB
   late. A healthy large scan therefore reads as "stalled", is killed at the 10-minute stall window
   ("likely hang / wrong binary"), and leaks its spool. The 60-minute absolute hard cap
   (`ExtractWaitPolicy` stall × `HardCapMultiplier`) compounds it for genuinely long scans.
3. **Orphaned all-core extract children** (Grok + Codex) — Miller spawns julie-extract with no
   kill-on-parent-death containment; `Kill(entireProcessTree: true)` runs only on Miller's own
   timeout path (JulieExtractRunner.cs:584). If the Miller host dies (OOM), the child keeps
   extracting on all cores and growing its spool. Note: a Unix process group alone does NOT fix
   this, and `PR_SET_PDEATHSIG` is Linux-only — the portable fix is an extractor-side parent-liveness
   watchdog (see plan).
4. **Linked-worktree HEAD watcher gap** (Codex; verified) — `IndexerWatcherSet` watches `.git/HEAD`
   only when `.git` is a *directory* (src/Miller.Server/Hosting/IndexerWatcherSet.cs:69–70). Linked
   worktrees have a `.git` file, so branch switches emit thousands of per-file events instead of one
   collapse signal; the 64 KiB watcher buffers overflow and overflow forces a full rescan — a rescan
   storm generator in exactly the fleet scenario.
5. **Registry rows claim `Ready` before the first scan** (Grok; verified) — CLI `workspace open`
   upserts `Ready` then scans (src/Miller.Server/Cli/CliDispatch.cs:3130); failures mark the row
   error afterward, but a killed process leaves a `Ready` row for an artifact that never existed.
   Error rows have no TTL; prune only handles missing roots.
6. **Path-reuse identity risk** (Codex + Grok) — identity is the canonical-path hash alone, so a
   different worktree recreated at the same path silently inherits the registry row, and a live old
   process can keep serving its stale in-memory index. No git admin-dir generation/epoch check
   exists.
7. **Post-scan sidecar fan-out** (Grok + Codex; verified) — every successful scan synchronously
   converges content/search sidecars and wakes vector convergence per workspace
   (src/Miller.Server/Hosting/IndexerService.cs:672–676). N worktrees multiply this load after
   extraction "finishes"; a scan-only governor would let one workspace's sidecar build overlap the
   next workspace's scan.
8. **Shared-temp spools in containers** (Grok + Codex) — `std::env::temp_dir()` may be tmpfs
   (spool growth becomes memory pressure) or a shared multi-user `/tmp` (cross-tenant disk fill).
9. **`MILLER_FULL_REBUILD_INPLACE=1` hazard** (Grok) — the escape hatch reintroduces the in-place
   merge pathology (reader-pinned WAL, ~7KB/s collapse) if an env pack is copy-pasted across a
   fleet.
10. **Stale flock doc comment** (Grok + Codex; verified) — `SingleWriterLock`'s comment cites a
    julie-side `<db>.julie-extract.lock` 30s flock that no longer exists anywhere in
    julie-extractors. Miller's lock is the only serialization; the comment must be fixed so future
    work doesn't assume a backstop.

**Consensus:** a two-round cross-model convergence (review → synthesized plan vote → amended item 9)
ended with both models voting AGREE on all ten plan items. Codex's sole objection — that a Unix
process group alone cannot kill children on parent death — was adopted verbatim into the plan.

## 6b. Measured WAL datapoint (2026-08-02, partial corroboration of claim 4)

A force scan of the julie-extractors repo itself — **1,786 files** — peaked at a **2.09 GB** combined
`db + wal + shm` total and settled to **1.13 GB** after julie's TRUNCATE checkpoint. Observed while
mapping the extractor for W5, not staged as a measurement, so treat it as an incidental datapoint
rather than a controlled result.

It matters because claim 4's "~14 GB of WAL" was recorded as **unreproduced**. A 1.8k-file repo
transiently holding 2 GB makes a multi-GB transient on the reporter's 74k-file root entirely
plausible, and it means the peak is a *transient* that only survives when the scan is killed before
its checkpoint — which is exactly what the OOM cascade in §3 causes. It does not on its own justify
chunked commits: W10 still owns the controlled measurement, and only a healthy force-to-`.rebuild`
showing multi-GB WAL would reopen that question.

## 7. Open questions for the reporter

1. Exact `miller version` output and platform (macOS/Linux; container? shared temp?).
2. Was the 74k-file root a single repo, a monorepo, or a parent directory holding several checkouts /
   worktrees? Were worktrees nested inside the repo root, and was that dir gitignored?
3. Did they write a `.julieignore` by hand in the main checkout before opening worktrees?
4. Roughly how many concurrent worktree agents?

## 8. Disposition

Consensus fix plan (Codex + Grok, unanimous after one amendment):
[`docs/plans/2026-08-01-multi-worktree-fleet-safety-plan.md`](../plans/2026-08-01-multi-worktree-fleet-safety-plan.md).
