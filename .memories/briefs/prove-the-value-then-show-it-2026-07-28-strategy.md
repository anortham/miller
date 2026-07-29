---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: active
created: 2026-07-29T00:13:39.439Z
updated: 2026-07-29T05:01:02.233Z
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
3. **NEXT — distribution month (writing, not code):** publish (a) the calibration, (b) the
   "hand-written extractors vs tree-sitter query files" depth piece (the personal-achievement artifact),
   (c) the adversarial-audit method piece; lead README/site with the Microsoft-stack wedge; surface
   julie-extractors standalone (push its 11 unpushed commits, cut a release).
4. Standing rule: extractor grinding frozen except experiment-driven gaps + resolution tiers.
   NEW fix candidates from the calibration prep: static-workspace chunk-cursor hold (restart re-embeds
   all cards, chunks never converge) and .julieignore seeding surprise — both are Miller bugs, fair game.
5. Archive Eros formally; salvage note: continuous-testing daemon is the one novel piece.

## Pending approvals

- Push miller main (ahead 4: takeover close, harness budget/schema fixes, calibration evidence).
- Push julie main (ahead 1: retirement note).

## Constraints

- MCP stinginess rule stands; no new Miller MCP tools.
- Pushes/releases stay approval-gated.
- Windows/Linux semantic runtime gates remain on their own track.
