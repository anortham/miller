---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: active
created: 2026-07-29T00:13:39.439Z
updated: 2026-08-07T01:58:50.458Z
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

## Addendum — versioned index store + views (APPROVED 2026-08-06)

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
  contract-freeze re-attack. Ph0 is a hard go/no-go prototype gate; no contract freeze before it
  passes.
- Execution model (working agreement 2026-08-06): razorback phase plans in dedicated worktrees per
  repo; **Opus implementer subagents** (every dispatch forces cd + pwd/branch verification step 1);
  **Fable as lead** — writes the Ph1 contract itself, inline spec + architecture-quality review on
  every task; **codex adversarial reviews at gates** (Ph1 freeze re-attack + grok per repo
  convention, pre-merge review per phase branch, Ph0/Ph5 findings audits) rather than per task;
  parallel fan-out for Ph0's independent instruments, serialized lanes for coupled implementation.
  Estimate ~13–19 agent sessions across miller + julie-extractors.
- Ownership rules softened by user 2026-08-06: julie/Miller boundary is performance-driven, not
  law (julie writes store.db because the tuned Rust bulk writer lives there; Miller writes
  sidecars; CLAUDE.md language amended when the program is picked up).

## Pending approvals

- Commit (and later push) the program-plan docs + `.memories` checkpoints currently uncommitted on
  miller main.
- Approval to BEGIN Ph0 (prototype gate) — each phase gated individually.
- julie-extractors release/pin bumps, Miller release, and the store default-on decision are
  user-approval points inside the program.

## Constraints

- MCP stinginess rule stands; no new Miller MCP tools.
- Pushes/releases stay approval-gated.
- Windows/Linux semantic runtime gates remain on their own track.
