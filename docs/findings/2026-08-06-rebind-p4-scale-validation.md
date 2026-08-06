# 2026-08-06 — Rebind P4: scale validation on the 74k-file fixture

**Verdict: the copy-and-rebind mechanics PASS every P4 criterion they own — 15.3× vs the program's
W10 baseline for a fresh worktree open, source untouched under SIGKILL, per-language parity exact.
The 8-worktree fleet run exposed one real product gap that is NOT in the rebind path: leaders hold
the machine-wide scan admission through sidecar convergence, which serializes a fleet bring-up and
starved the 8th bootstrap past its 10-minute admission timeout.**

Setup: the W10 fixture regenerated from its spec (74,000 files, 234 MB, TS/C#/Py/JS/Go/Rust/Java +
docs, seeded generator — not byte-identical to the 2026-08-02 original, same shape); julie-extract
**2.27.0** (pinned), miller `1.16.1+759a8d3a`; isolated `HOME`, `MILLER_SEMANTIC=off`, default
`--jobs` (4); 24-core/64 GB Mac. Readiness = committed revision + fresh + search AND content
sidecars current, polled via `workspace status --json` every 2 s.

## 1. Baseline — server-bootstrap full scan at 2.27.0

| Phase (from server start) | t |
|---|---|
| Extraction + artifact write (`Scan complete`) | 110 s |
| Bootstrap ready (symbol tools usable) | 140 s |
| Content corpus converged | 170 s |
| Search sidecar converged (**fully ready**) | **327 s** |

Artifact 4.58 GB; peak spool 1.33 GB; peak WAL **25 KB**. Sidecars: search.db 3.76 GB, content.db
2.43 GB (~10.8 GB per converged workspace).

The W10 numbers are history at the current binary: 3,677 s → 330 s wall (11×), 9.28 GB WAL → 25 KB,
5.3 GB spool → 1.33 GB. The 2.25.0 bulk-cache/savepoint fixes hold at 74k-file scale.

## 2. Fresh worktree open via rebind (single)

| Phase (from server start) | t |
|---|---|
| Rebind complete: 4.58 GB online-backup copy + `rebind` + no-change delta | **12.0 s** |
| Bootstrap ready (symbol tools usable) | 44 s |
| Content corpus converged | 76 s |
| Search sidecar converged (**fully ready**) | **240 s** |

`rebound_from` provenance present in `workspace status --json` and the bootstrap log line names the
source workspace.

Against the baselines:

| Comparison | Full scan | Rebind open | Speedup |
|---|---|---|---|
| Program target: W10 baseline (2.21.0) | 3,677 s | 240 s | **15.3× — PASS (target ≥10×)** |
| Current binary, extraction phase | 110 s | 12 s | 9.1× |
| Current binary, bootstrap ready | 140 s | 44 s | 3.2× |
| Current binary, fully converged | 327 s | 240 s | **1.37×** |

The honest structural read: at 2.27.0 the copy-and-rebind wins the extraction phase 9×, but
end-to-end both paths are bounded by the ~160 s search-sidecar FTS5 build over 3.26 M symbols,
which v1 deliberately does not copy ("sidecars converge from the rebound artifact"). The design
doc §10 `clonefile` trigger did NOT fire — the copy is 12 s of a 240 s open; the dominant follow-on
lever is sidecar convergence (copy/rebind the sidecars too, or build them without holding
machine-wide admission — see §3).

## 3. 8-worktree fleet — 7/8 converged on a serialized ladder; 1 bootstrap starved: PARTIAL

8 fresh worktrees, 8 servers launched in the same second:

- 7 of 8 rebound and converged at 365 / 623 / 860 / 1,076 / 1,308 / 1,542 / 1,777 s — a clean
  ~235 s ladder. Last success ≈ **29.6 min**: within "minutes, not hours", but only just, and only
  for 7.
- wt7's bootstrap threw at exactly **+10:00** (`DefaultBootstrapScanLockWait`): "Timed out waiting
  for machine-wide scan admission… owner is miller pid … scanning fleet-wt4 (reason
  leader-drain-rescan)". It fell to no index at all, and **nothing retried for the remaining 50
  minutes** — the process served not-ready until restarted. A restart on the quiet machine rebound
  and converged in 202 s.

Mechanism (confirmed in code, not inferred): each new leader's first drain tick takes the
machine-wide governor admission for `leader-drain-rescan` and holds it through
`TryConvergeSidecarToLatest` (`IndexerService.cs:617-640`) — so the ~220 s sidecar build of every
worktree serializes the whole fleet, and queued bootstraps behind it hit the 10-minute cap. The
rebind sequence itself occupies the governor for only ~12 s per worktree.

This is a governor-policy gap, not a rebind defect, and it predates P3 (any 8 concurrent
first-bootstraps of large workspaces would starve the same way — a full-scan fleet would starve
*worse*). Candidate fixes, for a follow-up decision: release admission after the scan and before
sidecar convergence (sidecar build is single-workspace I/O, not an extractor); or give bootstrap
admission priority over drain-rescan; or make the bootstrap wait adaptive with a liveness probe
instead of a fixed 10 minutes. A failed bootstrap should also self-retry once admission frees.

## 4. SIGKILL mid-rebind — PASS

SIGKILL at 508 MB into the staging copy:

- Source `symbols.db` AND `symbols.db-wal`: byte-identical (SHA-256) before the kill, after the
  kill, and after recovery.
- Debris after the kill: `symbols.db.rebuild` (522 MB) + its journal, exactly the staging trio.
- Relaunch: debris cleaned by the next attempt, rebind succeeded, converged in 197 s, spool 0 bytes.

## 5. Language parity — PASS

Rebound worktree artifact vs the source artifact (a fresh 2.27.0 full scan of the byte-identical
tree): `files` by language (10 rows), `symbols` by language+kind (39 rows), `identifiers` by
language (7 rows — doc formats emit none) all MATCH exactly. `relationships` matched on zero rows
in both — the generated fixture has no inheritance edges, so that dimension is vacuous here; the
P2 crate gate (`tests/rebind_equivalence.rs`) remains the row-level authority. Provenance keys
(`rebound_from_root`, `rebound_from_artifact_id`, `rebound_at`) present; artifact ids differ, so
sidecars converge per the revision-keyed paths.

## 6. Standing-note confirmations

- **The 30 s source-heartbeat window is real and costs exactly what the P3 review note predicted:**
  a worktree opened seconds after the source scan finished full-scanned silently (ineligible), and
  the same open after the window rebound in 1.2 s (mini fixture). At fleet scale the window did not
  fire because the source had been idle for minutes.
- An `Ineligible` rebind logs nothing. Fine for the everyday non-worktree case, but it made the
  heartbeat suppression above diagnosable only by code reading; a single debug-level reason line
  would have named it.

## 7. Scorecard against the program's P4 section

| Criterion | Result |
|---|---|
| Fresh worktree open seconds-to-low-minutes, ≥10× vs the W10 full-scan baseline | **PASS** — 240 s vs 3,677 s (15.3×); symbol tools usable at 44 s |
| 8-worktree fleet converges in minutes, not hours | **PARTIAL** — 7/8 in ≤29.6 min on a governor-serialized ladder; the 8th starved at the 10-min bootstrap admission cap and needed a restart (§3) |
| SIGKILL mid-rebind leaves recoverable state | **PASS** — source intact to the byte, debris cleaned, no spool leak |
| Language parity on a real multi-language artifact | **PASS** — exact per-language counts (relationships vacuous, noted) |

Harness and scripts: session scratchpad `p4/` (`gen_fixture.py`, `harness.sh`, `baseline.sh`,
`wt-open.sh`, `fleet.sh`, `sigkill.sh`, `parity.sh`); one-off measurement rig, not committed, per
the W10 precedent.
