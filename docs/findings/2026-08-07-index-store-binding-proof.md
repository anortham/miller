# Binding-mechanism proof — gate closed by user acceptance (G3b marginal accepted)

**Status:** Measurements COMPLETE. **Gate CLOSED 2026-08-07: the user explicitly accepted
the marginal G3b measurement** (path (a) below), which discharges the §9 gate and unblocks
the contract freeze. The measured record stands unedited: the gate ran RED on G3b — one of
three runs measured the overhead ratio at 0.5069 against the fixed 0.50 ceiling, and the
plan's rule is "any FAIL → the gate is red… the lead records NO-GO and the contract freeze
blocks." G1, G2 (scoped below), G3a, G3c, G4, and G5 passed. An earlier revision of this
document recorded the mechanism verdict as GO with G3b "marginal"; **that construction was
retracted at the cycle-3 cross-model gate** (codex finding C1, grok finding 5 — contract
§17): it softened a fixed binary criterion after measurement, which the plan forbids. The
decision was then escalated to the user with two unblock paths: (a) the user explicitly
accepts the marginal G3b measurement, or (b) a fresh re-proof passes under a policy
predeclared before it runs (store-shaped base read, all pairs in all runs, ceiling unchanged
at 0.50). **The user chose (a).** The acceptance carries a condition: Ph2 re-measures the
G3b ratio in the Rust implementation with own-file resolution output as a store equivalence
gate (carried condition 1 below).
**Program:** [`../plans/2026-08-06-index-store-views-program.md`](../plans/2026-08-06-index-store-views-program.md)
**Refuted predecessor:** Ph0 gate §9
([`2026-08-06-index-store-ph0-gate.md`](2026-08-06-index-store-ph0-gate.md)) — the P1a scoped
pass as view binder: NO-GO as designed.
**Evidence:** [`../../spike/index-store-ph1/binding-proof/results.md`](../../spike/index-store-ph1/binding-proof/results.md)
(instrument, 3 full runs, raw JSON + 22 scan reports committed) and
[`../../spike/index-store-ph1/julie-path-audit/results.md`](../../spike/index-store-ph1/julie-path-audit/results.md)
(the shipped-path audit).
**Criteria:** G1–G5 were FIXED in
[`../plans/2026-08-07-index-store-ph1-plan.md`](../plans/2026-08-07-index-store-ph1-plan.md)
before any measurement ran. None was moved afterwards.

## The mechanism

**Serve-base + background convergence.** A new view of an indexed family:

1. **Foreground:** writes its manifest rows and binds the nearest ready sibling base — no
   resolution work on this path. Measured: 1,081–1,700 manifest rows in 2.0–3.4 ms (~2 µs/row),
   **0 identifier rows written, 9/9 pairs**.
2. **Serve window:** the view serves the base's resolution for shared versions; identifiers in
   versions the base does not cover have no resolution yet. `trace`/`impact` report "resolution
   converging" with the gap in two honest states (contract §14): a manifest-computable lower
   bound before the diff runs, then the exact enumeration (rows AND files — never "N files
   changed"; the delta spills well past changed files by design of the resolution graph). The
   exact enumeration requires the diff, which completes near the end of convergence.
3. **Background:** one **fresh-output full resolution pass** over the view's corpus at the bulk
   rate (written to its own scratch/base file — the separate-file precondition from the Task 2
   audit), a **natural-key diff** against the base, and a **delta write** (replacements +
   tombstones). Publishing the delta is atomic; the view is then exact.

## Verdict per criterion

| Criterion (fixed threshold) | Measured | Verdict |
|---|---|---|
| **G1 Determinism** — 0 differing rows across two from-scratch builds, per corpus | 0 / 373,900 (miller), 0 / 325,078 (julie-extractors) | **PASS** (scoped: `identifier_resolutions` only — see below) |
| **G2 Exactness** — base + delta ≡ tip, 0 mismatches, every pair incl. structure-changed | 0 mismatches, 9/9 pairs (8 add paths, 1 deletes; tombstones 2,543–57,101/pair exercise removal everywhere) | **PASS** (scoped: `identifier_resolutions` only — see below) |
| **G3a Rate** — fresh-pass resolution ≥ 50k rows/s | 71.1k / 73.3k / 84.6k rows/s (at-scale miller pairs) | **PASS** (1.4–1.7×; escapes Ph0's 15.8–20.1k populated-artifact rates — the candidate's premise) |
| **G3b Overhead** — diff + delta write ≤ +50% of the resolution phase | 0.406 / 0.417 / 0.469 in the canonical run; the worst pair across three runs: **0.4961 / 0.5069 / 0.4690 — run 2 FAILED the fixed ceiling** | **FAIL** (one run of three; no aggregation policy was predeclared, so the plan's any-FAIL rule applies — see below; the failing run's JSON is committed) |
| **G3c Absolute** — background time-to-exact ≤ 30 s at miller scale | 4.1–7.6 s under load | **PASS** (4–7× under) |
| **G4 Serve-window honesty** — gap enumerable at ≤ the diff's own cost | enumeration 1.7–13.8% of the diff (in-band); gap 2.4–9.7% of resolution rows / 5–104 files typical, worst in-band 29.6% / 170 files | **PASS** |
| **G5 Dominance** — beats the refuted bind's 24,390 ms; foreground does no per-identifier work | **7,271 ms vs 24,390 ms on the exact pair Ph0 measured** (3.4× to exact; ~9,000× to first serve); foreground 2.7 ms, 0 identifier rows | **PASS** |

### G3b, stated honestly

The overhead ratio sits **at the ceiling, ±4%, run-dependent**: run 2's worst pair measured
0.5069 > 0.50, and no multi-run aggregation policy was predeclared. The plan's rule leaves no
lead discretion — "any FAIL → the gate is red; do not tune criteria; the lead records NO-GO
and the contract freeze blocks." **The gate is therefore RED on G3b.** The failing measurement
is committed (`output/proof-results-run2.json`).

**Analysis (not part of the verdict):** decomposition shows **95.1% of diff+write is the
instrument re-joining the base set out of a julie artifact in CPython** (1,836 ms); the diff
algorithm itself compares 753,212 rows in 96.5 ms (7.8 M rows/s); the delta write is 167 ms. A
store-shaped single-table base read costs 905 ms (byte-equal result, asserted), putting the
ratio at 0.22–0.31. The ceiling's protective intent — the diff producer must not balloon
time-to-exact — is met by G3c (4.1–7.6 s against a 30 s bound). This analysis is why the lead
expects a store-shaped re-proof to pass; **it does not and cannot convert the FAIL into a
pass**. An earlier revision used it to record "MARGINAL, verdict GO"; both cycle-3 reviewers
flagged that as post-measurement softening of a fixed criterion, and it is retracted
(contract §17, codex C1).

**Paths to discharge (exactly two):**

1. The user explicitly accepts the marginal measurement as satisfying the gate (a gate-rule
   change only the user can make).
2. A fresh re-proof under a policy predeclared before it runs: store-shaped base read (the
   single-table shape the contract actually specifies), the unchanged 0.50 ceiling, and an
   explicit aggregation rule — all pairs in all runs pass. The re-proof instrument must also
   close two coverage gaps the original left open (pre-merge review, codex): it applies the
   **persisted** delta (re-read from the written delta database, not the in-memory lists — the
   original wrote, size-checked, and deleted the file without round-tripping it) and diffs
   `pending_resolutions` alongside `identifier_resolutions`. A failure there puts the
   mechanism back on the table.

## Discharge statement

**The Ph0 §9 red gate is DISCHARGED by user acceptance (2026-08-07).** The refuted mechanism
(P1a scoped pass as binder) stays refuted and appears nowhere in the contract. Its replacement
passed six of seven fixed criteria decisively — including dominance on the exact pair that
refuted the original (G5) — and G3b failed in one of three runs (0.5069 vs the fixed 0.50
ceiling); the user explicitly accepted that marginal measurement rather than ordering the
predeclared re-proof, and the acceptance carries condition 1 (Ph2 re-measures the ratio in
the Rust implementation as a store equivalence gate). The scope caveat also
stands: G1/G2 covered `identifier_resolutions` only; `pending_resolutions` — whose
disappearing rows are the reason §14 keeps tombstones — was never diffed (codex C4), and Ph2's
equivalence gates extend to it (contract §16.7).

## The SLO the contract may cite

- **Foreground bind:** O(manifest) — milliseconds, flat in delta size and corpus size.
- **Time-to-exact (background):** the corpus resolution pass at the bulk rate + diff + delta
  write. Measured at ~1,400-file scale: **4.1–7.6 s under load**. Projected at dotnet/runtime
  (12.86 M identifiers): **≈ 232 s** (resolution 169 s + diff 63 s, 33 s store-shaped) —
  arithmetic inference, flagged; assumes the diff does not run in-memory naively (see carried
  condition 3).
- **Serve-window gap budget:** 2.4–9.7% of resolution rows / 5–104 files on typical sibling
  pairs; worst measured in-band 29.6% / 170 files. Honesty copy quotes rows and files.

## The shipped-cost finding (Task 2 §A — independent of the store program)

Miller's watcher-driven single-file converge (`update --file`) lands on the same
`resolve_delta` widened scope the store program refuted: **the median save on this repo
re-derives 87.3% of the resolution corpus (16–18 s)**; Ph0's 74.5% headline is the 36th
percentile. 78% of saves exceed 70%. (Evidence status: the two named-file end-to-end
measurements — 92.7%/18.1 s and 90.3%/16.0 s — are committed
(`spike/index-store-ph1/julie-path-audit/probes/`); the 120-file percentile table is an
unverified observation whose sampling script was not committed — the audit report carries the
correction, and §16.3's `resolution_perf.rs` sweep re-measures it.) `miller edit apply=true` pays the same on the leader.
julie's 0.7 crossover guard is denominated in files while the cost is in identifier rows —
**it fired on 0 of 120 sampled saves**, parking every save on the measured-slower path.
**Measured correction (2026-08-07, shipped as julie-extract v2.28.0):** the re-denomination
was built and A/B-measured before release, and the predicted save win did NOT hold — on the
save shape (1 changed file, 90–93% identifier scope) Full measured equal or slower than the
widened delta, because promotion sheds only per-changed-file worklist overhead and one file
has none. What shipped: identifier denomination for multi-file deltas (a real −13% resolution
win on the 737-file scan shape), a single-changed-file exemption (saves keep the status quo
path, byte-identical), and the corpus-currency fix (a promoted delta no longer advances
`last_full_revision`). **The 16–18 s save cost is NOT fixed by any crossover variant** — only
row-level scoping (Task 2 §2.1 tier 3) or the store program's background converge reaches
delta-sized save cost. Evidence:
`spike/index-store-ph1/julie-path-audit/probes/out/results3.json` (saves) and
`results4.json` (scan).

## Conditions carried to Ph2 (named, gating)

1. **G3b re-measurement (the acceptance condition).** The user accepted the marginal
   measurement (2026-08-07), so the re-proof path was not taken; Ph2 therefore re-measures
   the ratio in the Rust implementation with own-file resolution output as a store
   equivalence gate, against the unchanged 0.50 ceiling.
1b. **`pending_resolutions` equivalence:** Ph2's G1/G2-equivalent gates natural-key, diff,
   apply, and compare `pending_resolutions` alongside `identifier_resolutions`, including
   disappearing shared-version rows (codex C4; contract §16.7).
2. **The extractor dependency is real:** a way to emit resolution output **without writing a
   full artifact** (the `resolve` verb + separate-file output, contract §16.4–16.5). Without
   it the store pays the ~46% of scan wall clock that is artifact child-row/index/FK work it
   does not need.
3. **The diff needs a memory design at scale:** 25.7 M rows in a naive hash diff is ~10 GB.
   The contract requires a streaming merge-join over sorted natural keys or a SQL-side diff
   before Ph5's dotnet/runtime run.
4. **Deletion coverage n=1:** only one merge in either repo's history deletes an indexed path.
   Ph2's equivalence gates must include synthetic deletion fixtures (multi-language, per the
   parity rule).
5. **Scan-report counters are not row totals:** `resolution_rows_rederived` runs 3–13 rows
   above the table's row count (constant across repeat builds). Contracts and gates cite table
   counts as authoritative.

## What this proof did NOT establish

- **Persisted-delta round-trip:** G2 applied the in-memory replacement/tombstone lists; the
  delta database was written and size-checked but never re-read and applied, so
  serialization, column-order, and type-roundtrip defects were outside the proof (pre-merge
  review finding; the re-proof policy above closes it, and Ph2's store equivalence gate
  exercises the real persisted representation by construction).
- The real `resolve`-verb implementation (everything proxied via from-scratch scan with the
  resolution phase isolated; the proxy's separate-file shape matches the required store shape).
- dotnet/runtime scale (projection only; Ph5 owes the measurement).
- Platform breadth (one box: macOS/Apple Silicon/APFS).
- Base staleness policy under heavy watcher churn (rebase thresholds are contract leans, §14,
  validated in Ph5).

## Verification ledger

| item | value |
|---|---|
| instrument | `spike/index-store-ph1/binding-proof/` — 3 full runs, 22 sequential `julie-extract scan --jobs 4` each; raw JSON (canonical + both earlier runs incl. the G3b-failing one) committed |
| worktree | index-store-ph1 @ 1eee221c at measurement (lead commit 51bac3e8) |
| invariants | G1 0-diff both corpora (identifier_resolutions); G2 0 mismatches 9/9 pairs (identifier_resolutions); G3 rate/absolute pass, ratio FAILED in run 2 (0.5069 > 0.50); G4 enumeration ≤ diff cost (in-band with the diff); G5 dominance on Ph0's exact refutation pair |
| result | gate ran RED on G3b per the plan's any-FAIL rule; six of seven criteria passed; §9 discharge CLOSED by explicit user acceptance of the marginal 0.5069 (2026-08-07), carrying the Ph2 re-measurement condition |
| timestamp | 2026-08-07T12:30Z (lead-recorded from worker reports); user acceptance recorded 2026-08-07 |
