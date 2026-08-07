---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: active
created: 2026-07-29T00:13:39.439Z
updated: 2026-08-07T12:54:26.020Z
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

## Addendum — versioned index store + views (APPROVED 2026-08-06; Ph0 COMPLETE; Ph1 COMPLETE except one USER decision)

- Program plan: `docs/plans/2026-08-06-index-store-views-program.md`. Replaces copy-per-worktree
  indexing: one content-addressed store per repo family (`~/.miller/stores/<family-id>/`,
  version-keyed by blake3 content hash + extractor fingerprint) + per-checkout views (manifest +
  resolution base/delta). Dedup across worktrees AND time (branch switch = manifest repoint).
- **Progressive levels program is FOLDED IN.** The v4 contract is level-aware from day one.
- Naming: NO codename; plain "versioned index store" nomenclature.
- **BLANKET EXECUTION APPROVAL (user, 2026-08-06): "the whole plan is approved, you don't need to
  wait on me for anything."** Phases proceed without per-phase check-ins. The ONLY remaining user
  stops: git push, releases, julie-pin bumps, marketplace/publish actions, the store default-on
  decision — and now the Ph1 G3b gate decision (below).
- **Ph0 COMPLETE (2026-08-07), merged to main** (ff to 62d9c2ee). Storage hard gate PASS (8 views
  at 1.027× one index); view binding NO-GO as designed (§9 red gate); trigram rank→collapsed_len
  ships early; retention is the central contract.
- **Ph1 COMPLETE on branch `worktree-index-store-ph1` (2026-08-07), merge pending the G3b
  decision.** Deliverables: binding-mechanism proof (serve-base + background-converge;
  `docs/findings/2026-08-07-index-store-binding-proof.md`), the v4 store contract
  (`docs/plans/2026-08-07-index-store-v4-contract.md`, 17 sections), julie path audit
  (`spike/index-store-ph1/julie-path-audit/`), cycle-3 cross-model freeze gate (grok 10 findings +
  codex 11 findings, ALL verified and folded — contract §17 is the dual review record).
- **THE ONE OPEN ITEM — the G3b gate decision (USER'S):** six of seven fixed criteria passed
  decisively (foreground bind 2.7 ms; time-to-exact 4.1–7.6 s; 3.4× faster than the refuted bind
  on its exact refutation pair; 0 mismatches 9/9 pairs). G3b (diff+write ≤ +50% of resolution)
  FAILED in one of three runs (0.5069 vs 0.50) with no predeclared aggregation policy; the plan's
  fixed rule says any FAIL → gate red → freeze blocks. The earlier MARGINAL/GO verdict was
  RETRACTED at the cycle-3 gate (codex C1). Contract stays DRAFT. Unblock paths: (a) user accepts
  the marginal measurement, or (b) a predeclared store-shaped re-proof (analysis says it lands at
  0.22–0.31 — 95% of the failing term is instrument overhead the real store doesn't have).
- **Shipped today-problem found by the Ph1 audit (worth fixing ahead of schedule):** every save on
  identifier-dense repos pays ~87.3% median resolution re-derivation (16–18 s) via the watcher's
  `update --file`; julie's `DELTA_SCOPE_CROSSOVER=0.7` is file-denominated and fired 0/120 sampled
  saves. ~5-line re-denomination fix identified (contract §16.3); needs a julie release (approval).
- Queued for julie-extractors (Ph2 work list = contract §16): metadata_json BTreeMap + determinism
  gate, crossover re-denomination, bulk-path own-file resolution output, `resolve` verb, store
  verbs, equivalence gates incl. pending_resolutions + synthetic deletions, extractor
  compatibility gate.
- Execution model unchanged: razorback phase plans in dedicated worktrees; Opus implementers;
  Fable lead; codex+grok adversarial at gates.

## Pending approvals

- **The G3b gate decision** (accept marginal / order the predeclared re-proof) — blocks the v4
  contract freeze and the §9 discharge; Ph2 implementation start is blocked on the freeze.
- Git push of miller main (ahead 14) and merge+push of `worktree-index-store-ph1`.
- julie-extractors release/pin bumps (incl. the crossover fix), Miller release, store default-on
  decision.
- Everything else: pre-approved per the blanket execution approval above.

## Constraints

- MCP stinginess rule stands; no new Miller MCP tools.
- Pushes/releases stay approval-gated.
- Windows/Linux semantic runtime gates remain on their own track.
