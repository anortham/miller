# Autonomous run report — index store Ph1 (contract + binding proof)

**Status:** Complete — merge, push, and the G3b gate decision await user approval.
**Plan:** `docs/plans/2026-08-07-index-store-ph1-plan.md` (6/6 tasks complete)
**Branch:** `worktree-index-store-ph1` @ final HEAD (10 commits over merge-base 11c15708)
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/index-store-ph1`

## The one decision this run needs from you — G3b

The binding-mechanism proof passed **six of seven** fixed criteria decisively:

- Foreground bind: 2.7 ms, zero identifier work (O(manifest)).
- Background time-to-exact: 4.1–7.6 s at miller scale, **3.4× faster than the refuted Ph0
  bind on the exact pair that refuted it**.
- Exactness: 0 mismatches on 9/9 real sibling pairs; determinism 0 differing rows.

**G3b (diff + delta write ≤ +50% of the resolution phase) FAILED in one of three runs:
0.5069 against the fixed 0.50 ceiling.** The plan's rule, fixed before measurement: "any
FAIL → the gate is red… the lead records NO-GO and the contract freeze blocks." I first
recorded MARGINAL/GO; **both external reviewers flagged that as post-measurement softening,
and I retracted it.** The gate is RED, the v4 contract stays DRAFT, Ph2 is blocked on the
freeze.

Your two unblock paths:

1. **Accept the marginal measurement** (0.5069 vs 0.50; the ceiling's protective intent is
   met by the 30 s bound passing at 4.1–7.6 s, and 95% of the failing term is instrument
   overhead — a CPython artifact re-join the real store does not have; store-shaped
   supplementary evidence puts the true ratio at 0.22–0.31).
2. **Order the predeclared re-proof** (store-shaped base read, ceiling unchanged, all pairs
   in all runs; must also apply the PERSISTED delta and diff `pending_resolutions` — two
   coverage gaps the reviews exposed). Roughly one session; a fail puts the mechanism back
   on the table.

## What shipped (all documentation + spike evidence; zero production code)

- **Binding proof** — `docs/findings/2026-08-07-index-store-binding-proof.md` + instrument
  and raw evidence in `spike/index-store-ph1/binding-proof/` (3 runs, committed JSON incl.
  the failing run).
- **v4 store contract** — `docs/plans/2026-08-07-index-store-v4-contract.md`, 17 sections:
  layout/family identity, version identity + determinism, levels + completeness stamps,
  composite identity + index-direction rule, write path, retention (target ≤1.20× composed
  physical bytes, trigger 1.25× hysteresis), epochs, read session, sidecars, GC/purge,
  migration, promotion + capacity formula, store_log, resolution state machine (§14),
  family coordinator (§15), Ph2 work list (§16), dual review record (§17).
- **julie path audit** — `spike/index-store-ph1/julie-path-audit/` (557-line cited audit +
  recovered probe instruments under `probes/`).
- **Shipped today-problem found:** every save on identifier-dense repos pays ~90% resolution
  re-derivation, 16–18 s, via the watcher's `update --file` (committed named-file evidence:
  92.7%/18.1 s, 90.3%/16.0 s). julie's crossover guard is file-denominated and fired on 0
  sampled saves. The ~5-line re-denomination fix (§16.3) improves shipped Miller immediately
  and needs a julie release (your approval). Worth doing ahead of Ph2 if you want the win now.

## External review (three passes, all findings verified before acceptance)

- **Cycle-3 freeze gate, grok** (needs-attention, 10 findings): §14 exact-state re-entry,
  §15 cross-WAL atomicity claim, scratch reuse, level-share accounting, G3b softening, §17
  placeholder, delta reclamation, deletion fixtures, layout completeness, share labeling.
  **All 10 accepted and folded** (commit 4abfb1db).
- **Cycle-3 freeze gate, codex** (needs-attention, freeze REFUTED, 11 findings): the G3b
  waiver (C1 — accepted, verdict corrected), partial-chunk commit ambiguity, stale-delta
  serving, `pending_resolutions` never diffed, gap-enumeration timing, demotion stamps,
  byte-ceiling enforceability, filesystem/log atomicity, extractor-compatibility gating,
  pin lifecycle, batch starvation. **All 11 accepted and folded** (commit 3478704d).
  Full dispositions: contract §17.
- **Pre-merge codex review of the branch** (needs-attention, 6 findings, **6/6 verified
  real, 6/6 fixed**, commit 2aa9ce68): run.sh rm-rf footgun; G2 persisted-delta round-trip
  gap; results.md still carrying the retracted PASS; run-1 coverage overstated (5 pairs,
  not 9); 120-file distribution not reproducible from committed evidence (probes recovered
  from /tmp and committed; percentile table relabeled unverified); contract byte-target
  conformance hole (target vs trigger split). Zero findings dismissed. Codex does not
  report token costs.

## Tests

Branch gate at final content HEAD 2aa9ce68: `dotnet build Miller.slnx -c Release` 0
warnings/0 errors; `scripts/test.sh` 6149 passed / 0 failed / 2 skipped, 29 s. (Docs +
spike branch; the fast suite guards nothing regressed.) Scale suite not run: no
indexing/extract path code changed.

## Judgment calls

1. **Retracting my own GO verdict.** The plan's fixed rule + two independent reviewers
   outweighed my analysis-backed GO. The analysis survives as labeled analysis; the verdict
   is RED. This is the honest reading of "do not tune criteria."
2. **Not re-running the proof myself.** The store-shaped instrument is known to pass
   (0.22–0.31). Choosing it after seeing that is outcome-known tuning; the re-proof is
   yours to order under a predeclared policy.
3. **Committing recovered /tmp probe evidence** rather than downgrading the whole
   shipped-cost finding: the two named-file measurements survive and carry the 16–18 s
   claim; only the 120-file percentile table is labeled unverified.
4. **grok vs codex disagreement on the freeze** (freeze-with-hard-carry vs no-freeze):
   decided for codex because the plan text decides, not reviewer preference.

## Blockers hit

None unresolvable. The G3b decision is an approval boundary, not a failure.

## Files changed

45 files, +77,952 / −62 over merge-base 11c15708 (10 commits). Bulk is committed raw
evidence JSON (`binding-proof/output/`, `julie-path-audit/probes/out/`).

## Next steps

1. **You:** the G3b decision (accept marginal / order re-proof) — this gates the freeze and
   Ph2.
2. **You:** approve merge of `worktree-index-store-ph1` → main and push (main is also 14
   ahead of origin from Ph0).
3. ~~Optional, independent of the store: approve the §16.3 crossover fix as a julie release
   to kill the 16–18 s per-save cost today.~~ **DONE with a measured correction (2026-08-07,
   julie-extract v2.28.0, user-approved):** the save-shape A/B refuted the predicted save
   win — Full ≈ or slower than the widened delta on single-file saves. v2.28.0 ships the
   identifier-denominated crossover (−13% resolution on the 737-file scan shape), a
   single-changed-file promotion exemption (saves byte-identical), and the corpus-currency
   fix. The 16–18 s save cost STANDS; only row-level scoping (audit §2.1 tier 3) or the
   store's background converge fixes it. Evidence: probes/out/results3.json, results4.json.
4. After the freeze: Ph2 planning in julie-extractors (razorback plan per the program doc).

Note for the merge: the main checkout's working tree carries the updated strategy brief
(byte-identical to this branch's committed copy) — the same benign collision pattern as the
Ph0 merge; reconcile by taking the committed copy.
