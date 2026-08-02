---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: active
created: 2026-07-29T00:13:39.439Z
updated: 2026-08-02T15:45:51.459Z
tags:
  - strategy
  - adoption
  - evaluation
  - julie-retirement
  - eros
---

## Direction (approved 2026-07-28)

Full plan: `~/.claude/plans/i-want-to-talk-parsed-matsumoto.md`. Goals: personal achievement + external
adoption. Diagnosis: engineering quality is not the constraint — adoption is zero and every recorded
benchmark compared Miller to Julie instead of to a bare agent.

## Steps

1. **DONE 2026-07-28** — takeover closed: sealed gate cancelled as superseded
   (`docs/findings/2026-07-28-sealed-gate-disposition.md`), migration doc operative, Julie retired at
   v7.17.0 (retirement note in julie README, TODO bugs wontfix by policy).
2. **DONE 2026-07-29 — decisive delta confirmed.** Four paired runs
   (`docs/findings/2026-07-29-miller-vs-bare-agent-calibration.md`, exports in
   `docs/findings/agent-efficiency/2026-07-29-bare-agent/`): Miller 2.2x correct tasks over a bare agent
   (11 vs 5 /15 frozen, 10 vs 4 raised), 0% vs 27% wrong actions, strict dominance (baseline_only=0),
   doubling the bare budget made it WORSE. Lexical core carries it (off 11/15); semantic +1..2 correct.
   Per the decision rule: **this is the marketing story — Step 3 leads with it.** Honest frame: exact
   identity + act-on-evidence discipline at higher per-task cost, not "grep can't find things."
3. **Distribution write-ups DONE 2026-07-29, all live:** (a) calibration →
   anortham.github.io/miller/benchmark.html; (b) depth piece → the NEW julie-extractors Pages site
   (anortham.github.io/julie-extractors/, extractors.html, workflow-build Pages enabled via API,
   all numbers code-verified: 125,527 LOC, 0 .scm, 195 pattern IDs/60 families, tier1-4 fail-closed);
   (c) adversarial-audit method piece → anortham.github.io/miller/method.html. Sites/READMEs
   cross-linked; julie-extractors README release block refreshed to live v2.20.0 + evidence doc.
   README/site positioning is BREADTH-FIRST per user direction 2026-07-29 ("I don't want the site to
   say 'Built for the Microsoft stack'") — 36 hand-written languages for 95% of users, .NET depth one
   proof point among several. Remaining Step 3: external venue posting (HN/lobste.rs/r/dotnet) is the
   USER'S call, not an agent action.
4. Standing rule: extractor grinding frozen except experiment-driven gaps + resolution tiers.
   NEW fix candidates from the calibration prep: static-workspace chunk-cursor hold (restart re-embeds
   all cards, chunks never converge) and .julieignore seeding surprise — both are Miller bugs, fair game.
5. **DONE 2026-07-29** — Eros archived (banner in its README); salvage note: continuous-testing daemon
   is the one novel piece.

## Addendum — fleet architecture direction (approved 2026-08-02)

The 2026-08-01 multi-worktree field report split into two programs, and an architecture review
(user-approved) settled the long-term questions:

- **Fleet-safety plan** (`docs/plans/2026-08-01-multi-worktree-fleet-safety-plan.md`) — IN
  IMPLEMENTATION (separate session, `.worktrees/fleet-safety`). Fixes crashes: governor, jobs cap,
  spool reaping, backoff, progress visibility, worktree ignore/watcher fixes.
- **Worktree delta-rebind program** (`docs/plans/2026-08-02-worktree-delta-rebind-program.md`) —
  APPROVED SUCCESSOR, gated on safety landing. Fixes cost: fresh worktrees rebind the main
  checkout's artifact + delta-scan instead of full extraction (25–40 min → seconds). Safety makes
  fleets not die; rebind makes them usable. The rebind contract doubles as the future Eros/CI
  artifact-consumption seam (zero-regret).
- **Settled architecture verdicts:** per-workspace swarm + lease coordination STAYS (incident
  validated it — all fixes fit inside); SQLite STAYS (artifact-as-copyable-file is load-bearing for
  rebind and any Eros future). Standing triggers documented in the rebind plan: central daemon only
  at sustained 20+ heavy-churn worktrees (escalation = governor → small broker, semantic-broker
  precedent); LanceDB only at ~100× embedding growth; Eros only on concrete demand for team/CI
  artifacts (framing: Miller = runtime, Eros = artifact supply chain — not addon vs replacement).

## Pending approvals

- Push miller main (unpushed docs commits: fleet triage/plan + delta-rebind program plan).
- Push julie main (ahead 1: retirement note).
- Approval to BEGIN each delta-rebind phase after the safety plan lands.

## Constraints

- MCP stinginess rule stands; no new Miller MCP tools.
- Pushes/releases stay approval-gated.
- Windows/Linux semantic runtime gates remain on their own track.
