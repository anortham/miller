# Autonomous run report — index store Ph0 prototype gate

**Status:** Complete on branch; merge to `main` pending (one command, below)
**Plan:** `docs/plans/2026-08-06-index-store-ph0-plan.md` (program:
`docs/plans/2026-08-06-index-store-views-program.md`)
**Branch:** `worktree-index-store-ph0`, fully gated and reviewed. This session runs
worktree-isolated and its sandbox refuses git operations against the shared checkout, so the
local fast-forward merge is left to the main checkout:
`git merge --ff-only worktree-index-store-ph0` (from `/Users/murphy/source/miller`; no push —
user approval boundary).
**Tasks:** 7/7 complete (6 parallel Opus workers + lead synthesis)
**PR:** none — push and PR require user approval; local merge per repo convention.

## What shipped

The Ph0 prototype gate for the versioned index store: six measurement instruments under
`spike/index-store-ph0/` with committed evidence, and the gate findings doc
`docs/findings/2026-08-06-index-store-ph0-gate.md` — 13 go/no-go verdicts, codex completeness
audit (9 findings) and codex pre-merge review (5 findings) both recorded with dispositions.
Program doc reconciled: Ph0 boxes ticked, three open questions answered, the refuted binding
mechanism removed from every normative section, promotion formula corrected.

**Overall verdict: GO to Ph1, with the §9 red proof carried as Ph1's entry gate.**

Headline results:
- **Storage hard gate PASS:** 8-view store = **1.027×** a single index (target ≤1.2×; dedicated
  copies = 8.000×). Lower-bound-model caveats recorded; Ph5 owes the composed real-delta number.
- **v4 composite keys are byte-positive:** −11.3% vs today's schema.
- **Binding mechanism NO-GO as designed:** the P1a scoped pass re-derives 74.5% of the corpus
  for a 1-file change; a real sibling bind ran 32.4% slower than rebuilding. Storage model
  intact. Ph1's first deliverable = redesigned mechanism + measured proof (freeze precondition).
- **Trigram ordering key must change** (`rank` → stored `collapsed_len`): FTS5 bm25's table-wide
  avgdl makes the shipped ordering corpus-contaminated in a shared store. Contract change with
  its own equivalence gate; `content.db` inherits the finding (uninstrumented gap).
- **Retention is the central Ph1 contract:** 7-day default with L1-demoted history + byte
  ceiling + per-path cap. It is simultaneously the byte lever, latency lever, and growth guard.
- **Durability contract:** single-transaction import refuted (0 reuse per SIGKILL trial);
  completion marker proven load-bearing as an observed defect; per-chunk + marker +
  `synchronous=FULL`.
- **GC works bounded** with a new schema rule (version_id-leading secondary indexes) that
  tensions against the read path's index direction — per-index reconciliation owed in Ph1.
- **metadata_json nondeterminism** found (7 HashMap structs in julie-extractors) — reclassified
  by pre-merge review from dedup blocker to determinism/equivalence defect (store identity is
  input-keyed); fix still queued.
- **Levels already shipped** (two-level `--level symbols|full`) — program docs corrected; the
  record from earlier ("1.17.0 shipped rebind not levels") was wrong and is corrected in the
  gate doc.

## External review

Two codex passes, single-pass rule each:

1. **Findings-doc completeness audit:** 9 findings (1 critical, 6 major, 2 minor) — all
   verified and folded; the critical one reclassified §9 from GO-WITH-AMENDMENT to a split
   verdict (storage GO / mechanism NO-GO).
2. **Pre-merge branch review:** verdict `needs-attention`, 5 findings (3 high, 2 medium) — all
   verified and folded at commit `ff506b3c`: red-gate posture made explicit (Ph1 entry
   condition), seven surviving P1a-binder references rewritten, 1.027× lower-bound caveat,
   metadata_json reclassification, verification ledgers appended to five results.md files.

No finding was dismissed; none required flagging for blocking human judgment. One posture
recommendation is surfaced for the user below. Codex does not report per-request token counts.

## Flagged for your review

- **Gate posture (codex pre-merge finding 1):** the program says "does not proceed past a red
  gate," and §9 (view binding) is a red proof. The recorded posture: Ph1 starts, but the
  binding-mechanism proof is Ph1's FIRST deliverable and a contract-freeze precondition. The
  alternative — holding Ph0 open until a binding mechanism passes — is yours to choose; the
  gate doc recommends the carried-gate posture because the proof's inputs (Task 5's instrument,
  the measured full-pass fallback) already exist.

## Judgment calls

- Interpreted "keep pushing" as keep driving, not git-push authorization; merged locally only.
- Did not warn in-flight workers about the metadata_json finding (verified no instrument
  re-extracts and re-hashes — none was poisoned), which pre-merge review later validated by
  reclassifying the finding.
- Lead-applied the pre-merge fixes inline (docs + ledger appends) rather than dispatching fix
  workers — all were documentation corrections.
- Left `/tmp/level-comp-53909/` (Task 2's 1GB extract pair) in place during execution for
  sibling reuse; safe to delete now.

## Tests

Fast suite (`scripts/test.sh`): **6149 passed / 0 failed** at `ff506b3c`, two consecutive clean
runs. Transient failures appeared only while six concurrent agents loaded the machine (1
failure once, 7 once — different sets, all clean on re-run; branch contains zero code changes,
so regressions are impossible). Scale suite not run — no indexing/extract code touched.

## Blockers hit

None terminal. The worktree sandbox rejected compound shell commands (worked around with
prompt/script files); one flaky-under-load test noted above.

## Files changed

11 commits, ~48k insertions (mostly spike evidence): `docs/findings/2026-08-06-index-store-ph0-gate.md`,
program + plan doc updates, `spike/index-store-ph0/{purity-audit,level-composition,read-path,retrieval,resolution-growth,write-mechanics}/`.

## Next steps

1. **User:** from `/Users/murphy/source/miller`: `git merge --ff-only worktree-index-store-ph0`
   (then optionally `git worktree remove .claude/worktrees/index-store-ph0` and
   `git branch -d worktree-index-store-ph0`); push `main` when satisfied (also carries the
   earlier program-plan commit `7447b913`); rule on the gate posture above if you disagree.
2. **Ph1 (next session):** binding-mechanism design + measured proof first (Task 5's
   instrument reusable), then the store contract doc; cross-model gate = reserved doubt
   cycle 3.
3. **julie-extractors queue:** metadata_json BTreeMap + stability gate; symbol-name scope
   widening; bulk-path eligibility for populated artifacts.
4. **Cleanup:** `/tmp/level-comp-53909/` can be deleted.
