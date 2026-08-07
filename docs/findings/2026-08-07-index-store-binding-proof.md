# Binding-mechanism proof — the Ph0 §9 red-gate discharge

**Status:** COMPLETE — mechanism verdict **GO**, with one criterion recorded marginal (not
waived, not re-scored) and named conditions carried to Ph2.
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
   converging" with the enumerated gap (rows AND files — never "N files changed"; the delta
   spills well past changed files by design of the resolution graph).
3. **Background:** one **fresh-output full resolution pass** over the view's corpus at the bulk
   rate (written to its own scratch/base file — the separate-file precondition from the Task 2
   audit), a **natural-key diff** against the base, and a **delta write** (replacements +
   tombstones). Publishing the delta is atomic; the view is then exact.

## Verdict per criterion

| Criterion (fixed threshold) | Measured | Verdict |
|---|---|---|
| **G1 Determinism** — 0 differing rows across two from-scratch builds, per corpus | 0 / 373,900 (miller), 0 / 325,078 (julie-extractors) | **PASS** |
| **G2 Exactness** — base + delta ≡ tip, 0 mismatches, every pair incl. structure-changed | 0 mismatches, 9/9 pairs (8 add paths, 1 deletes; tombstones 2,543–57,101/pair exercise removal everywhere) | **PASS** |
| **G3a Rate** — fresh-pass resolution ≥ 50k rows/s | 71.1k / 73.3k / 84.6k rows/s (at-scale miller pairs) | **PASS** (1.4–1.7×; escapes Ph0's 15.8–20.1k populated-artifact rates — the candidate's premise) |
| **G3b Overhead** — diff + delta write ≤ +50% of the resolution phase | 0.406 / 0.417 / 0.469 in the canonical run; the worst pair across three runs: **0.4961 / 0.5069 / 0.4690 — straddles the ceiling** | **MARGINAL** (see below; the failing run's JSON is committed) |
| **G3c Absolute** — background time-to-exact ≤ 30 s at miller scale | 4.1–7.6 s under load | **PASS** (4–7× under) |
| **G4 Serve-window honesty** — gap enumerable at ≤ the diff's own cost | enumeration 1.7–13.8% of the diff (in-band); gap 2.4–9.7% of resolution rows / 5–104 files typical, worst in-band 29.6% / 170 files | **PASS** |
| **G5 Dominance** — beats the refuted bind's 24,390 ms; foreground does no per-identifier work | **7,271 ms vs 24,390 ms on the exact pair Ph0 measured** (3.4× to exact; ~9,000× to first serve); foreground 2.7 ms, 0 identifier rows | **PASS** |

### G3b, stated honestly

The overhead ratio sits **at the ceiling, ±4%, run-dependent**: one of three runs failed it. The
criterion is recorded MARGINAL, not PASS — the gate was not re-scored, and the failing
measurement is committed (`output/proof-results-run2.json`). Decomposition of the cost: **95.1%
of diff+write is the instrument re-joining the base set out of a julie artifact in CPython**
(1,836 ms); the diff algorithm itself compares 753,212 rows in 96.5 ms (7.8 M rows/s); the
delta write is 167 ms. A store-shaped single-table base read costs 905 ms (byte-equal result,
asserted), putting the ratio at 0.22–0.31 — published as quarantined supplementary evidence
that the verdict arithmetic never reads.

**Why the mechanism verdict is GO despite a marginal G3b:** the ceiling's protective intent —
the diff producer must not balloon time-to-exact — is decisively met by G3c (4.1–7.6 s against
a 30 s bound), and the marginal term is dominated by a proxy artifact the real store does not
have. The ratio is NOT waived: it carries to Ph2 as an acceptance criterion re-proven in the
real store shape (Rust, own-file resolution output, streaming or SQL-side diff), where the
supplementary evidence says it lands near 0.3. If Ph2's real implementation fails the 50%
ceiling, the mechanism goes back on the table.
**Freeze consequence (cross-model gate, grok finding 5, accepted):** the GO licenses design
direction — contract §14 may freeze and be implemented against — but Ph2's implementation is
NOT ACCEPTED until the G3b re-proof passes as a hard gate in the real shape. G3b is a live
criterion carried forward, not a discharged one; only G1/G2/G4/G5 and G3a/G3c are discharged
by this proof.

## Discharge statement

**The Ph0 §9 red gate is DISCHARGED.** The program's "does not proceed past a red gate" rule is
honored as follows: the refuted mechanism (P1a scoped pass as binder) stays refuted and appears
nowhere in the contract; its replacement passed a fixed-criteria measured proof on the exact
pair that refuted the original (G5); the one marginal criterion is recorded as marginal with
its failing run committed, and re-proves in Ph2 under the real store shape. The v4 contract's
§14 (resolution state machine) may now freeze, subject to the cycle-3 cross-model re-attack.

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
percentile. 78% of saves exceed 70%. `miller edit apply=true` pays the same on the leader.
julie's 0.7 crossover guard is denominated in files while the cost is in identifier rows —
**it fired on 0 of 120 sampled saves**, parking every save on the measured-slower path. The
~5-line re-denomination (contract §16.3) improves shipped Miller immediately and is the
cheapest real win found in this phase. This is a today-problem worth fixing ahead of Ph2's
schedule if desired (julie release approval applies).

## Conditions carried to Ph2 (named, gating)

1. **Re-prove G3b in the real shape:** diff + delta write ≤ +50% of the resolution phase,
   measured in the Rust implementation with own-file resolution output — a store equivalence
   gate, not a nice-to-have.
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
| invariants | G1 0-diff both corpora; G2 0 mismatches 9/9 pairs both directions; G3 rate/absolute pass, ratio marginal ±4% at ceiling; G4 enumeration ≤ diff cost; G5 dominance on Ph0's exact refutation pair |
| result | mechanism GO; §9 discharged with carried conditions 1–5 |
| timestamp | 2026-08-07T12:30Z (lead-recorded from worker reports) |
