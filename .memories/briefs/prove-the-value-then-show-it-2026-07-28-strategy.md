---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: active
created: 2026-07-29T00:13:39.439Z
updated: 2026-08-07T11:34:13.852Z
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
   (`docs/findings/2026-07-29-miller-vs-bare-agent-calibration.md`): Miller 2.2x correct tasks over a
   bare agent, 0% vs 27% wrong actions, strict dominance. Lexical core carries it; semantic +1..2.
   This is the marketing story. Honest frame: exact identity + act-on-evidence discipline at higher
   per-task cost.
3. **Distribution write-ups DONE 2026-07-29, all live** (miller benchmark.html + method.html,
   julie-extractors Pages). Breadth-first positioning per user direction. Remaining: external venue
   posting (HN/lobste.rs/r/dotnet) is the USER'S call.
4. Standing rule: extractor grinding frozen except experiment-driven gaps + resolution tiers.
5. **DONE 2026-07-29** — Eros archived; salvage note: continuous-testing daemon is the novel piece.

## Addendum — fleet architecture line (2026-08-01 → 2026-08-06)

- **Fleet-safety plan** — LANDED (shipped through the 1.16.x/1.17.0 line).
- **Worktree delta-rebind program** — SHIPPED in v1.17.0 (P4 validated 2026-08-06; julie-extract
  2.27.0 pinned). Rebind fixed worktree TIME, not BYTES — each worktree still gets a full physical
  copy. Its copy path becomes the compat/fallback once the store program (below) ships.
- Architecture verdicts from 2026-08-02, updated: SQLite STAYS (store is plain SQLite files;
  `store export` preserves artifact-as-copyable-file). Per-workspace swarm + lease coordination is
  AMENDED by the store program: store writes go through one family coordinator
  (lease-holder-executes-queued-requests); read swarm unchanged. Standing triggers (central daemon,
  LanceDB, Eros) unchanged.

## Addendum — versioned index store + views (APPROVED 2026-08-06; Ph0 COMPLETE — GO to Ph1)

- Program plan: `docs/plans/2026-08-06-index-store-views-program.md`. Replaces copy-per-worktree
  indexing: one content-addressed store per repo family (`~/.miller/stores/<family-id>/`,
  version-keyed by blake3 content hash + extractor fingerprint) + per-checkout views (manifest +
  resolution base/delta). Dedup across worktrees AND time (branch switch = manifest repoint).
  Motivation: ~19 GB of `.miller` across main checkouts; 21.9 GB dotnet/runtime artifact per
  worktree; 512 GB work SSD with ~30 projects.
- **Progressive levels program is FOLDED IN — both programs approved 2026-08-06.** The v4 contract
  is level-aware from day one (per-level completeness stamps, L1-first import,
  serve-while-converging per level). `docs/plans/2026-08-03-progressive-indexing-levels-program.md`
  stays the levels design source.
- Naming: NO codename (Ceres rejected 2026-08-06); plain "versioned index store" nomenclature.
- The deferred 1.17.0 FTS5 sidecar-copy follow-up is CANCELLED (never started).
- Doubt pass: two codex cycles run 2026-08-06 (22 findings, all verified in code and
  accepted/folded — see the doc's doubt-pass records); the third cycle is reserved as the Ph1
  contract-freeze re-attack. Ph0 was a hard go/no-go prototype gate; it has now run.
- **BLANKET EXECUTION APPROVAL (user, 2026-08-06): "the whole plan is approved, you don't need to
  wait on me for anything."** Phases proceed without per-phase check-ins — Ph0 → findings → Ph1
  contract → implementation branches. The ONLY remaining user stops: git push, releases,
  julie-pin bumps, marketplace/publish actions, and the store default-on decision.
- **Ph0 COMPLETE (2026-08-07), merged to main** (ff to 62d9c2ee, 12 commits; branch + worktree
  removed; fast suite 6149/0 on merged main). Gate doc
  `docs/findings/2026-08-06-index-store-ph0-gate.md`; run report
  `.memories/autonomous-run-2026-08-07-index-store-ph0.md`. Verdict **GO to Ph1**:
  - Storage hard gate PASS: 8 worktree views share one store at 1.027× a single index (budget
    1.2×; today's copies cost 8×). The v4 composite-key shape is itself 11% smaller.
  - View binding NO-GO as designed — the one red proof. The scoped pass re-derives 74.5% of the
    resolution corpus for a one-file change; a real sibling bind measured 32% slower than
    rebuilding. Ph1's FIRST deliverable is a redesigned binding mechanism with its own measured
    proof — recorded as the Ph1 ENTRY gate and a contract-freeze precondition. (Codex challenged
    proceeding past a red gate; recorded posture: Ph1 starts, nothing freezes without the binding
    proof. The user may instead hold Ph0 open — that changes sequencing, not content.)
  - Trigram search ordering must change (rank → stored collapsed_len): FTS5 rank bakes in
    whole-table statistics, so a shared store contaminates the current ordering key. Measured
    faster and matches documented intent, but it is a shipped-contract change needing its own gate.
  - Retention is the central Ph1 contract: 7-day default, history demoted to L1, a byte ceiling,
    a per-path cap — the byte lever, latency lever, and growth guard at once.
  - Also proven: crash-reusable per-chunk import (single-transaction refuted; the completion
    marker caught a real truncated-version bug), bounded GC reclamation with a new
    index-direction schema rule, corrected promotion-capacity formula.
- Queued for julie-extractors: metadata_json BTreeMap fix + determinism gate (blocks equivalence
  gating, not dedup), symbol-name scope widening, bulk-path eligibility for populated artifacts.
- Execution model: razorback phase plans in dedicated worktrees per repo; Opus implementer
  subagents (forced cwd verification); Fable lead (writes contracts itself, inline review); codex
  adversarial at gates; parallel fan-out for independent instruments. Estimate ~13–19 sessions
  across miller + julie-extractors.
- Ownership rules softened by user 2026-08-06: julie/Miller boundary is performance-driven, not
  law (julie writes store.db because the tuned Rust bulk writer lives there; Miller writes
  sidecars; CLAUDE.md language amended when the program is picked up).

## Pending approvals

- Git push of miller main (ahead 13: program-plan commit + Ph0 merge) and of phase branches when
  ready.
- julie-extractors release/pin bumps, Miller release, store default-on decision.
- Everything else: pre-approved per the blanket execution approval above.

## Constraints

- MCP stinginess rule stands; no new Miller MCP tools.
- Pushes/releases stay approval-gated.
- Windows/Linux semantic runtime gates remain on their own track.

