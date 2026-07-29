---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: active
created: 2026-07-29T00:13:39.439Z
updated: 2026-07-29T00:13:39.439Z
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
benchmark compares Miller to Julie instead of to a bare agent.

## Steps

1. **DONE 2026-07-28** — takeover closed: sealed gate cancelled as superseded
   (`docs/findings/2026-07-28-sealed-gate-disposition.md`), migration doc operative, Julie retired at
   v7.17.0 (retirement note in julie README, TODO bugs wontfix by policy).
2. **NEXT — the decisive experiment**: Miller-on vs bare-agent (and optionally MILLER_SEMANTIC=off arm)
   visible calibration, reusing the 2026-07-23 harness/rubric, tasks from real work. Deliverable
   `docs/findings/<date>-miller-vs-bare-agent-calibration.md`. Decision rule: decisive delta ⇒ it becomes
   the marketing story; weak delta ⇒ stop generic-capability investment, narrow positioning to exact-refs
   and the .NET niche.
3. Distribution month (writing, not code): publish the experiment, the "hand-written extractors vs
   tree-sitter query files" depth piece (also the personal-achievement artifact), and the takeover-matrix
   method piece; lead README/site with the Microsoft-stack wedge; surface julie-extractors as a standalone
   consumable (push its 11 unpushed main commits, cut a release).
4. Standing rule: freeze extractor percentage-grinding except experiment-driven gaps and the in-flight
   resolution-tier stream.
5. Archive Eros formally; salvage note: the continuous-testing daemon is the one novel piece.

## Constraints

- MCP stinginess rule stands; no new Miller MCP tools.
- Pushes/releases stay approval-gated.
- Windows/Linux semantic runtime gates remain on their own track, not part of this plan.
