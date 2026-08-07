# Index Store Ph1 — Binding Proof + v4 Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when
> subagent delegation is available. Fall back to razorback:executing-plans for single-task,
> tightly-sequential, or no-delegation runs.

**Goal:** discharge the Ph0 §9 binding red gate with a measured proof of the replacement
mechanism, then produce the freeze-ready v4 store contract with the cross-model re-attack folded.

**Architecture:** the binding candidate is **serve-base + background convergence**: a new view
serves the sibling base's resolution immediately (foreground cost = manifest work only) and
converges its exact per-view delta in the background via a **fresh-output full resolution pass
diffed against the base on natural keys**. The contract doc drafts binding-independent sections
in parallel with the proof; the resolution state machine folds the proven mechanism; nothing
freezes until the proof passes and the codex+grok re-attack is folded (program red-gate rule).

**Tech Stack:** Python 3 + SQLite spike instruments (Ph0 conventions), pinned
`.tools/julie-extract` 2.27.0, markdown contract doc. No production code changes.

**Architecture Quality:** design-doc phase. No Miller/julie-extractors production code changes;
the contract records the Ph2/Ph3 module shape (store verbs as the inter-repo seam, view-aware
read session, coordinator queue). Program risk stays high (new persistent format); this phase's
code risk is zero — docs and `spike/` only.

**Program context (read first):**
[`docs/plans/2026-08-06-index-store-views-program.md`](2026-08-06-index-store-views-program.md)
(Ph1 section + §9 correction), the Ph0 gate
[`docs/findings/2026-08-06-index-store-ph0-gate.md`](../findings/2026-08-06-index-store-ph0-gate.md)
(§9 refutation, amendments 1–7 in the overall verdict), and the Task 5 evidence
[`spike/index-store-ph0/resolution-growth/results.md`](../../spike/index-store-ph0/resolution-growth/results.md).

## Global Constraints

- **No Miller production code changes in Ph1.** Docs + `spike/index-store-ph1/` only. The fast
  suite must remain green and untouched.
- **julie-extractors checkout (`/Users/murphy/source/julie-extractors`) is READ-ONLY** this
  phase — evidence gathering only; Ph2 owns the fixes.
- **The contract cannot freeze until Task 1's proof gate passes** and Task 5's cross-model
  findings are folded. Red criteria are never tuned after measurement to force a pass.
- Go/no-go criteria G1–G5 are FIXED in this plan before measurement. Workers report numbers;
  the lead records verdicts.
- Instruments follow Ph0 conventions: `spike/index-store-ph1/<instrument>/` with `run.sh`,
  committed JSON evidence under `output/` or `out/`, and a `results.md` ending in a
  **verification ledger** (commands, worktree + commit, invariants, result, timestamp).
- Wall clocks are reported ±15% under load; deterministic row counts are the result axis
  (Ph0 Task 5 convention). Scratch artifacts live in `$TMPDIR` and are removed on exit.
- Extractor argv convention: `julie-extract scan --root <fixture> --db <scratch db> --jobs 4
  --json` (Ph0 Task 5's exact shape).
- No new MCP tools; no release/publish/pin-bump; no push without user approval.
- Commit mode: **parallel-lead-commit** — workers never commit; the lead stages and commits
  after inline review.
- Subagent dispatch rule (memory: subagent-worktree-cwd-guard): every dispatch forces
  `cd /Users/murphy/source/miller/.claude/worktrees/index-store-ph1` and verifies
  `pwd` + branch `worktree-index-store-ph1` + HEAD as step 1.

## Verification Strategy

**Project source of truth:** Miller `CLAUDE.md` (fast/Scale split, build guards); the program
doc's Verification strategy section.

**Worker red/green scope:** instrument self-checks — determinism repeats, equivalence
mismatch counts, ledger-recorded JSON outputs. Task 2 is read-only evidence with file:line
citations for every claim.

**Worker ceiling:** workers run no `dotnet` commands and never the Scale suite. If an
instrument seems to require changing Miller or julie-extractors source, STOP and report a plan
mismatch.

**Worker gate invariant:** Task 1 — every G1–G5 number recorded with raw JSON evidence on
disk; Task 2 — every claim carries a file:line citation from the current checkouts.

**Lead affected-change scope:** `scripts/test.sh` (fast suite) after each lead commit batch —
proves docs/spike changes did not touch code paths.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh` before
merge-readiness. Ledger reuse: a passing entry for the same HEAD carries.

**Replay/metric evidence:** G1–G5 are hard gates for the binding verdict. Scale projections to
dotnet/runtime are report-only (arithmetic, flagged as inference — Ph5 owes the real runs).

**Escalation triggers:** any need to modify production source (plan mismatch); any instrument
result contradicting a Ph0 gate verdict (report to lead before proceeding — it may reopen a
gate entry).

**Assigned verification failure:** workers stop and report when assigned verification fails.

**Verification ledger:** each results.md records invariant, command, scope label, commit SHA,
result, timestamp. The lead's run report records the branch-gate entries.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: binding-proof instrument | Batch A | Create: `spike/index-store-ph1/binding-proof/**` (run.sh, bind.py, results.md, output/) | No | None - safe parallel batch. |
| Task 2: julie-path audit | Batch A | Create: `spike/index-store-ph1/julie-path-audit/results.md` only | No | None - safe parallel batch. |
| Task 3: binding proof verdict doc | None - serial | Create: `docs/findings/2026-08-07-index-store-binding-proof.md` | Yes | Consumes Task 1 measurements + Task 2 evidence; lead-written. |
| Task 4: v4 contract doc | None - serial (4a may draft while Batch A runs) | Create: `docs/plans/2026-08-07-index-store-v4-contract.md` | Yes | 4b's resolution state machine consumes Task 3's verdict; lead-written. |
| Task 5: cross-model freeze gate | None - serial | Modify: `docs/plans/2026-08-07-index-store-v4-contract.md` (review records + folds) | Yes | Reviews the completed Task 4 draft; codex + grok. |
| Task 6: reconcile + wrap | None - serial | Modify: `docs/plans/2026-08-06-index-store-views-program.md`, `.memories/**`; Create: `.memories/autonomous-run-2026-08-07-index-store-ph1.md` | Yes | Records outcomes of all prior tasks; branch gate + pre-merge review. |

## Task 1: Binding-mechanism proof instrument

**Files:**
- Create: `spike/index-store-ph1/binding-proof/run.sh`
- Create: `spike/index-store-ph1/binding-proof/bind.py`
- Create: `spike/index-store-ph1/binding-proof/results.md`
- Create: `spike/index-store-ph1/binding-proof/output/` (JSON evidence + julie scan reports)

**Interfaces:**
- Consumes: `spike/index-store-ph0/resolution-growth/binding.py` (clone-base/modify/scan/report
  machinery — copy what is useful, do not import across spike dirs), the pinned
  `.tools/julie-extract` (2.27.0), Ph0 divergence data
  (`spike/index-store-ph0/resolution-growth/results.md` §1.6 merge stats).
- Produces: `output/binding-proof-results.json` (per-pair, per-stage numbers for G1–G5) and
  `results.md` with the measured tables Task 3 will cite verbatim.

**Contract inputs:** Ph0 measured anchors — from-scratch bulk resolution 380,723 rows in
5,324 ms (71.5k rows/s); populated-artifact full pass 24,050 ms (15.8k rows/s); refuted real
sibling bind 24,390 ms total; markdown control 2.3% scope.

**File ownership:** Create: `spike/index-store-ph1/binding-proof/**` (run.sh, bind.py, results.md, output/)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** a throwaway instrument (razorback:prototyping applies) that measures the
serve-base + background-converge candidate end to end on real sibling pairs. The candidate's
model: extraction rows for unchanged files are deduped by the store, so the background
convergence pays (a) one **fresh-output full resolution pass** over the tip corpus at the bulk
rate, (b) a **natural-key diff** of that output against the base's resolution set, and (c) a
**delta write** (replacements + tombstones). The measurable proxy for (a) with today's binary
is a from-scratch full scan into a fresh `$TMPDIR` artifact, reading `profile.phases` to
isolate the resolution phase (Ph0 Task 5's method) and reporting the extract share separately
— state both the measured total and the store-real number (resolution + diff + delta write).

**Approach:**

1. **Pair selection.** From each repo's real merge history (method of Ph0 §1.6): miller —
   one median pair (~16 changed indexed files) and one p90 pair (~77); julie-extractors — one
   median (~28) and one p90 (~369). At least one pair per corpus MUST add or delete paths
   (the `structure_changed` case that killed the scoped pass). Record each pair's SHAs and
   divergence stats. julie-extractors is read via `git archive` to `$TMPDIR` (Ph0 growth.py
   pattern) — its checkout is read-only.
2. **G1 determinism probe (run first; everything else depends on it).** Build the same tree
   from scratch twice; extract each artifact's resolution set keyed by **natural keys** (source
   identifier: file path + byte span + name; target: file path + symbol name + span — NOT raw
   `symbol_id`/`identifier_id`, which are not comparable across builds). The two sets must be
   exactly equal. If they are not: record the differing rows, report, and STOP — the diff-based
   producer is unsound as designed and the gate is red on G1.
3. **Per-pair measurement.** Base = from-scratch build at the merge base. Candidate pipeline =
   from-scratch tip build (resolution phase isolated) + natural-key diff vs base + delta
   materialization (replacement rows + tombstones for base rows absent at tip). Time each
   stage. Also record the per-pair delta: row count, % of base rows, distinct files and
   distinct target symbols touched.
4. **G2 equivalence check.** Apply the produced delta to the base set (replace + tombstone
   precedence) and compare to the tip set on natural keys: 0 mismatches required, on every
   pair including the structure-changed ones.
5. **Serve-window quantification (G4).** For each pair, the delta measured in step 3 IS the
   serve-window gap: report rows, % of base, files touched, and the cost of enumerating it
   (must not exceed the diff cost itself). This is the honesty budget `trace`/`impact` status
   will cite.
6. **Foreground serve cost (G5).** Demonstrate that the foreground bind is O(manifest):
   model the bind as manifest rows + base pointer flip in a scratch SQLite store (Ph0
   read-path store shape is a reference); measure it. No resolution work may sit on the
   foreground path.
7. **Scale projection (report-only).** dotnet/runtime arithmetic at the measured resolution
   and diff rates (12.86M identifiers), flagged as inference.
8. Repeat-run the full pipeline on one pair to confirm wall-clock variance (±15% expected)
   and row-count determinism.

**Go/no-go criteria — FIXED before measurement:**

- **G1 Determinism:** two from-scratch builds of the same tree yield natural-key resolution
  sets with **0 differing rows**, per corpus.
- **G2 Exactness:** base + produced delta ≡ tip resolution set, **0 mismatches**, on every
  measured pair, including structure-changed pairs.
- **G3 Cost:** the resolution phase of the fresh-output pass sustains **≥ 50k rows/s** on the
  miller fixture (escapes the 15.8–20.1k populated-artifact rates); diff + delta write add
  **≤ 50%** over the resolution phase; total background time-to-exact at miller scale
  **≤ 30 s** under load.
- **G4 Serve-window honesty:** the delta is enumerable at ≤ the diff's own cost; gap recorded
  per pair (rows, % of base, files).
- **G5 Dominance:** background pipeline (store-real number) beats the refuted bind's 24,390 ms
  on the equivalent corpus, and the foreground bind does no per-identifier work.

Any FAIL → the gate is red. Report the numbers honestly; do not tune criteria; the lead
records NO-GO and the contract freeze blocks.

**Acceptance criteria:**
- [ ] G1–G5 each measured and recorded with raw JSON evidence committed under `output/`.
- [ ] ≥2 pairs per corpus measured, ≥1 per corpus with added/deleted paths.
- [ ] Extract-vs-resolution phase split reported; store-real background number stated.
- [ ] `results.md` ends with the verification ledger; scratch cleaned from `$TMPDIR`.
- [ ] Worker-scope verification passes and the diff is handed to the lead (parallel-lead-commit).

## Task 2: Watcher converge path + julie fix surfaces (read-only audit)

**Files:**
- Create: `spike/index-store-ph1/julie-path-audit/results.md` (no code, no other files)

**Interfaces:**
- Consumes: Miller sources in this worktree (use Miller tools: `search`/`inspect`/`trace` on
  `JulieExtractRunner`, `IndexerService`, the watcher single-file update path);
  `/Users/murphy/source/julie-extractors` sources read-only (`julie-extract-cli/src/writer.rs`,
  `resolution.rs`, `base/types.rs`).
- Produces: `results.md` with three sections (A/B/C below) that Task 3 and the Ph2 spec cite.

**Contract inputs:** gate §9's lead-added check; queued julie items (gate: metadata_json
BTreeMap + determinism gate; symbol-name scope widening; bulk-path eligibility). Known
anchors: `writer.rs:1421` (`is_full_scan: structure_changed || force`), `writer.rs:1417`
(`structure_changed`), `resolution.rs:2922` (`delta_scope_files`), `resolution.rs:2674`
(`DELTA_SCOPE_CROSSOVER = 0.7`), `base/types.rs:78,124,178,294,449,487,496`
(`Option<HashMap>` metadata). Verify each anchor still holds; do not trust it blindly.

**File ownership:** Create: `spike/index-store-ph1/julie-path-audit/results.md` only

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** a cited evidence document, three sections:

- **A. What Miller's watcher pays today.** Trace the single-file save path: watcher event →
  IndexerService converge → the exact julie-extract argv issued → which writer/resolution
  branch 2.27.0 takes for a 1-file change with no structure change. Verdict with evidence:
  does one save on an identifier-dense repo re-derive the ~74.5% widened scope today (i.e.
  shipped incremental converge already pays near-full resolution per save)? Include measured
  or code-derived reasoning for what `structure_changed` evaluates to on a pure single-file
  rewrite through Miller's argv.
- **B. Ph2 fix surfaces.** For each queued fix, the smallest sound change and its blast
  radius: (1) symbol-name scope widening — what a sound narrowing looks like (per-name file
  sets? qualified-name filtering?) and which tests gate it; (2) bulk-path eligibility —
  `artifact_is_unwritten`'s exact conditions and what makes a **fresh-output resolution pass**
  (new tables, no prior revisions) bulk-eligible by construction in the store model; (3) the
  three-state answer for whether a resolution-only verb (no re-extraction) is feasible in
  today's architecture: what it needs as inputs, what blocks it, what the v4 contract should
  require of it.
- **C. metadata_json determinism.** Confirm the seven `Option<HashMap>` sites, the fix shape
  (`BTreeMap`), and the byte-stability gate julie needs so Ph2 can land it with proof.

**Approach:** read-only; every claim cited `path:line` from current sources. Where Ph0
anchors moved, record the new location and say so. No timing runs required (Task 1 owns
measurement); a small illustrative scan against a scratch DB in `$TMPDIR` is permitted if it
settles section A's verdict, using the standard argv convention.

**Acceptance criteria:**
- [ ] Section A verdict stated with file:line evidence end to end (Miller argv → julie branch).
- [ ] Section B covers all three surfaces with smallest-sound-change and blast radius each.
- [ ] Section C confirms sites + fix + gate shape.
- [ ] `results.md` ends with the verification ledger (evidence-audit scope).
- [ ] Diff handed to the lead (parallel-lead-commit).

## Task 3: Binding proof verdict + findings doc (lead)

**Files:**
- Create: `docs/findings/2026-08-07-index-store-binding-proof.md`

**Interfaces:**
- Consumes: Task 1's `results.md` + JSON evidence; Task 2's section A verdict.
- Produces: the recorded §9 discharge (or NO-GO) that the program doc, contract §resolution,
  and Task 6's reconciliation cite.

**Contract inputs:** gate §9's split verdict text; program doc's Ph1 entry condition; G1–G5
as fixed above.

**File ownership:** Create: `docs/findings/2026-08-07-index-store-binding-proof.md`

**Serialization required:** Yes

**Dependency reason:** Consumes Task 1 measurements + Task 2 evidence; lead-written.

**What to build:** the findings doc recording: the mechanism design (serve-base + background
fresh-output resolution + natural-key diff → delta, with tombstone precedence); the measured
proof against G1–G5 with a verdict per criterion and overall; the SLO the contract may cite
(time-to-exact at measured scale + flagged projection); the serve-window honesty budget; Task
2's section-A verdict (and its implication for today's shipped converge, called out to the
user); explicitly what remains unproven (Ph2's real verb vs the proxy, dotnet/runtime scale —
Ph5). If any criterion failed: the doc records NO-GO, what failed and by how much, and the
freeze stays blocked — do not soften.

**Acceptance criteria:**
- [ ] Verdict per G1–G5 + overall, each citing committed evidence.
- [ ] The proxy-vs-real-verb gap stated (what Ph2 must reproduce to keep the proof valid).
- [ ] §9 discharge status stated in the exact red-gate language (discharged / still red).

## Task 4: v4 store contract doc (lead)

**Files:**
- Create: `docs/plans/2026-08-07-index-store-v4-contract.md`

**Interfaces:**
- Consumes: program doc Design §1–5 + every gate amendment (1–7 in the gate's price list);
  Ph0 evidence docs; Task 3's verdict (for §resolution).
- Produces: the freeze-candidate contract Ph2/Ph3 implement against; Task 5 reviews it.

**Contract inputs:** gate amendments — per-chunk commits + completion marker +
`synchronous=FULL` (§11); `auto_vacuum=INCREMENTAL` + FTS5 + core secure-delete from creation,
per-index version_id direction reconciliation (§12 vs §4); promotion formula max-over-phases +
sweep-WAL term (§13); retention 7-day L1-demoted + byte ceiling + per-path cap (§10); trigram
`rank` → `collapsed_len` decision (§7); hybrid read-path shape (§4); canonical DocId order +
cached (doc_count, avgdl) (§8); two-epoch compatibility (§1/program); coordinator-queue
durable protocol + lock order (program §2).

**File ownership:** Create: `docs/plans/2026-08-07-index-store-v4-contract.md`

**Serialization required:** Yes (4a sections may be drafted while Batch A runs; 4b folds
Task 3).

**Dependency reason:** 4b's resolution state machine consumes Task 3's verdict; lead-written.

**What to build:** the v4 contract, one document, sections:

- **4a (binding-independent):** store layout + family-id derivation (git common-dir lineage,
  edge cases, path reuse via registry); v4 schema — full table inventory with composite
  `(version_id, local_id)` identity, the §12 index-direction decision per index, completion
  markers, per-level completeness stamps + L1-first import gates, the purity surgeries (drop
  `identifiers.target_symbol_id` with V-1 sequencing; `files` mutable columns to view-side;
  V-5 narrowing); verb shapes (`store import/update/delete/gc/export`, `--from-artifact`) with
  failure semantics per verb; commit granularity (per-chunk + WAL budget numbers from §11);
  WAL/checkpoint policy + durability pragmas; two-epoch compatibility + reader/writer floors +
  serve-while-converging; retention contract (the central item: 7-day L1-demoted default, byte
  ceiling ~1.25×, per-path cap, demotion mechanics, latency interaction from §7); GC +
  secure-purge contract (staged incremental_vacuum, page-limited FTS merges, purge
  escalation); promotion-capacity formula (max-over-phases incl. sweep WAL) applied to every
  promotion; concurrency contract — durable coordinator-queue execution protocol (request IDs,
  idempotency keys, claim states, successor recovery, chunked long ops, result delivery,
  requester timeouts), global lock order with the starvation/deadlock analysis, fairness;
  migration + rollback (preflight, export-on-rollback, three-domain reconciliation);
  sidecar re-key contract (idempotent cursors, stamps, freshness token) + the trigram
  `collapsed_len` ship decision with its equivalence-gate requirement; rebase policy lean for
  long-diverged views; the Ph2 julie work list (from Task 2 section B/C).
- **4b (after Task 3):** resolution base/delta **state machine** — keys (manifest hash +
  resolver epoch), the proven binding mechanism as the delta producer, precedence + tombstone
  scope, CAS rebase against (manifest generation, delta head) with abort/retry, GC roots
  (bases, deltas, pinned readers, in-progress rebases), serve-window honesty posture + SLO
  from the proof, bootstrap semantics (first view = base build at bulk rate).
- Header carries: **Status: DRAFT — freeze blocked on** the binding proof + cycle-3 fold;
  flipped only in Task 5.

**Acceptance criteria:**
- [ ] Every gate amendment (price-list 1–7) has a contract section that resolves it, not
      restates it.
- [ ] Doubt-pass held-open items addressed in contract terms: cycle-1 #2/#9/#11, cycle-2
      #2/#4/#7.
- [ ] The §12-vs-§4 index-direction tension resolved per index with rationale.
- [ ] 4b exists only with Task 3 GO; otherwise the doc records the block honestly.

## Task 5: Cross-model freeze gate (lead orchestrates codex + grok)

**Files:**
- Modify: `docs/plans/2026-08-07-index-store-v4-contract.md` (review records + folds)

**Interfaces:**
- Consumes: the completed Task 4 draft + Task 3 findings.
- Produces: recorded cycle-3 re-attack + grok review with dispositions; the freeze decision.

**Contract inputs:** program doc — cycle 3 is reserved as the freeze re-attack; repo
convention adds grok (memory: cross-model review is the default, re-verify corrections in
code/evidence).

**File ownership:** Modify: `docs/plans/2026-08-07-index-store-v4-contract.md` (review records + folds)

**Serialization required:** Yes

**Dependency reason:** Reviews the completed Task 4 draft; codex + grok.

**What to build:** run codex (adversarial, the cycle-3 re-attack: attack the contract's
weakest sections, verify cycle-1 #2/#9/#11 + cycle-2 #2/#4/#7 now close) and grok
(independent review) against the contract + binding findings. Apply
razorback:receiving-code-review: verify every finding against evidence/code before accepting;
fold accepted findings into the contract; record all dispositions in a review table. Then flip
Status to FROZEN only if the binding proof is GO and no accepted finding remains unfolded;
otherwise record precisely what blocks.

**Acceptance criteria:**
- [ ] Codex cycle-3 + grok reviews run and recorded with per-finding dispositions.
- [ ] Held-open doubt-pass items explicitly closed or carried with reasons.
- [ ] Freeze status flipped (or block recorded) per the rule above.

## Task 6: Reconciliation, branch gate, pre-merge review, wrap (lead)

**Files:**
- Modify: `docs/plans/2026-08-06-index-store-views-program.md` (Ph1 acceptance boxes, §9
  claim state, open-questions updates)
- Modify: `.memories/briefs/prove-the-value-then-show-it-2026-07-28-strategy.md` (via the
  goldfish brief tool)
- Create: `.memories/autonomous-run-2026-08-07-index-store-ph1.md` (+ checkpoint before the
  final commit)

**Interfaces:**
- Consumes: all prior task outputs.
- Produces: merged-ready branch + the user's morning report.

**Contract inputs:** program Ph1 acceptance boxes; razorback:verification-before-completion;
razorback:pre-merge-review (codex, single-pass fixes, per the Ph0 convention).

**File ownership:** Modify: program doc + `.memories/**`; Create: run report

**Serialization required:** Yes

**Dependency reason:** Records outcomes of all prior tasks; branch gate + pre-merge review.

**What to build:** tick the program's Ph1 acceptance boxes with pointers; update the
open-questions list (rebase-policy lean → contract section; binding SLO now set or still
unset); update the brief (Ph1 status + what Ph2 needs); checkpoint; run the branch gate
(`dotnet build Miller.slnx -c Release` + `scripts/test.sh`); run the pre-merge codex review of
the full branch diff, verify + fold findings single-pass; write the run report; report
merge-readiness to the user (merge/push stay approval-gated).

**Acceptance criteria:**
- [ ] Program doc Ph1 boxes ticked truthfully (binding box only on GO).
- [ ] Branch gate green and ledger-recorded.
- [ ] Pre-merge review recorded with dispositions; fixes folded single-pass.
- [ ] Run report written; brief + checkpoint updated.
