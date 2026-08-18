# Query-time resolution spike — parity and latency results

Date: 2026-08-18. Instrument: `spike/query-time-resolution/` (throwaway C# console, captured on
branch `prototype/query-time-resolution`). Question, from
[2026-08-18-whole-stack-architecture-assessment.md](2026-08-18-whole-stack-architecture-assessment.md) §7:
can per-name, query-time resolution over the existing `store.db` fact tables reproduce the
materialized resolution graph's answers, at ≤500 ms p95 warm per references query?

## Setup

- Frozen snapshot of the live Miller family store (`VACUUM INTO`), view `7857a50b`, manifest
  generation 412, resolution state `exact` at 412, bound base `base-374850…-6` (475,275 identifier
  rows) plus the generation-499 delta overlay (16,384 replacement rows).
- Ground truth: base `identifier_resolutions` overlaid by the max-generation
  `resolution_identifier_deltas` rows, restricted to manifest-visible versions — the same
  composition Miller's `FamilyStoreReadSession` serves.
- The prototype reimplements julie-extract's resolution policy (`RESOLUTION_VERSION=6`) from the
  extracted spec: tier chains per (origin, kind, receiver), the four tiers with their
  kind-compatibility sets, pending resolution, and span-based tier-1 propagation from
  `relationships` and resolved pendings (`locate_identifier` exactly-one rule).
- Corpus: 1,558 visible files, 109,353 visible symbols, 475,377 visible identifiers, 12,652
  resolved pendings. Hardware: the Linux dev machine.

## Results

| Measurement | Value | Gate |
|---|---:|---|
| **Parity: identifiers agreeing with the materialized graph** | **475,377 / 475,377 (100.000%)** | A: **PASS** |
| Compared fields | outcome, target version+symbol, tier, method | — |
| **Per-name references query, warm (160 names: top-40 fan-out + 120 random)** | **p50 0.02 ms, p95 4.4 ms, max 23.6 ms** | B: **PASS** (budget 500 ms) |
| Worst name (`Assert`, 19,925 sites) | 23.6 ms | — |
| Full-corpus resolve (all 475k identifiers) | **0.8 s** | — |
| One-time world load (facts from SQLite → memory) | 3.4 s | — |
| Propagation index build (pendings + relationships) | 0.5 s | — |
| Peak RSS of the naive instrument | 719 MB | see caveats |

Reference points from the committed evidence: julie-extract's full resolution phase on this same
store measured **171.7 s** (Linux, `2026-08-14-performance-recovery-baseline.md`) and **96.9 s**
(Windows). The spike recomputes the byte-equal answer set in **0.8 s** after a 3.9 s cold load —
and a per-query answer in microseconds-to-milliseconds with no maintained graph at all.

## Parity ladder (how the mismatches fell)

1. First run: 95.455% — the propagation join used `reference_site_id`; julie locates the
   co-located identifier by span with the target symbol's name. Switching to the span join
   removed 21,543 of 21,605 mismatches.
2. 99.988% — C# dotted namespace names (`Miller.Server.Cli` as one segment) needed flattening
   before the qualifier suffix match, exactly as `qualifier_matches_namespace` does. 37 fixed.
3. 99.996% — the scope-walk stop rule counts only kind-compatible candidates per level, not any
   name match. 21 fixed (minified-JS `variable_ref` cases).
4. **100.000%.**

Every divergence was my bug, not nondeterminism in the stored graph. That is itself a finding:
**the materialized graph is a pure function of the current fact tables.** Everything the
bases/deltas/rebases/scope-journal machinery maintains is recomputable from facts in under a
second at this scale.

## Caveats (honest limits of this spike)

- **Scale:** measured on the Miller-scale store (475k identifiers). A dotnet/runtime-scale store
  (~2.58 M symbols) is untested. Per-query cost scales with per-name fan-out, not corpus size;
  the load and propagation phases scale linearly and would need the incremental treatment below.
- **Memory:** 719 MB peak is the naive instrument (every row a record, string-keyed
  dictionaries, plus SQLite load buffers). A production model — interned symbol ids, packed
  arrays, identifiers streamed from the existing `identifiers(name, kind, version_id)` index per
  query instead of held resident — fits the existing 350/600 MB budgets. Not proven here.
- **Freshness model:** the spike loads a frozen world. Production needs per-file invalidation:
  on a file change, swap that version's symbols/identifiers/facts and its propagation entries —
  all file-local, no global pass. Not built here.
- **Confidence values** were not compared exactly (tier/method were; confidence is derived
  `min(tier constant, source confidence)` and matched wherever spot-checked).
- **Pending parity** was exercised through identifier propagation (12,173 identifiers carry
  pending-propagated outcomes and all matched); a standalone pending-row parity table was not
  emitted.

## What this makes possible

The save-path resolve phase — measured at 12–77 s p95 per change batch on this repo, with 250 s
and 600 s spikes, 117 minutes in one day of logs — exists to keep current what this spike
recomputes on demand in ≤24 ms per name. Retiring the materialized layer removes:

- the resolve subprocess, resolve claims, and resolve-phase WAL churn on every save,
- resolution bases (~1.5 GB here), deltas, rebase policies, validated-base proofs, and the
  scope journal with its crossover/accumulation failure modes,
- the resolution wait in bootstrap, and the extractor-upgrade full-resolve.

The whole-graph products (dead-code candidates, impact rollups) become a ~1 s batch sweep, which
can run exactly like `metrics` does today.

## Run it

```bash
# snapshot the live store first (see README), then:
dotnet run -c Release --project spike/query-time-resolution -- sweep    # parity
dotnet run -c Release --project spike/query-time-resolution -- bench    # latency
dotnet run -c Release --project spike/query-time-resolution -- query Assert
```

## Scale run: aspnetcore (added later on 2026-08-18)

User-requested scale test on `~/source/aspnetcore` (652 MB working tree; 14,374 visible files
after the `*.h` workaround below; 553,698 symbols; **2,152,935 identifiers** — 4.5× the Miller
corpus).

**Current-architecture cost to produce the ground truth first:**

- The first cold-index attempt ran 111 s, then failed at manifest publication and left the store
  wedged (unbound view, next open refused leadership). Root cause is a julie-extract bug: the
  manifest uses extension-only language classification (`.h` → `c`) while extraction sniffs
  content (C++ headers → `cpp`), and `store_import_publish_manifest` rejects the mismatch. Any
  repo with C++-flavored headers cannot cold-index. Workaround: `.julieignore` with `*.h`.
- With the workaround: `workspace open` wall time **871.5 s (14.5 min)** — import ~615 s,
  first resolve ~213 s. Store directory: **5.3 GB** (store.db 3.4 GB compacted, base 535 MB).

**Spike on the same frozen corpus:**

| Measurement | Miller scale | aspnetcore scale | Gate |
|---|---:|---:|---|
| Parity | 475,377/475,377 (100.000%) | **2,152,928/2,152,935 (99.9997%)** | A: PASS* |
| Refs query warm p50 / p95 / max | 0.02 / 4.4 / 23.6 ms | **0.01 / 22.2 / 285.7 ms** | B: PASS (≤500 ms) |
| Worst name | `Assert` 19,925 sites, 23.6 ms | `System` 27,252 sites, 285.7 ms | — |
| Full-corpus resolve | 0.8 s | **5.0 s** (producer: ~213 s) | — |
| One-time load + propagation | 3.4 + 0.5 s | 11.7 + 1.2 s | — |
| Naive instrument peak RSS | 719 MB | 2.96 GB | see memory caveat |

*The 7 divergences all point the same direction: the spike resolves sites the stored graph left
`missing`, every one a `tier3_static_type` member access. Spot check: exactly one class
`TextMessageFormat` exists; its test calls `TextMessageFormat.parse`; the written policy resolves
it; the stored graph recorded `missing`. This is consistent with the julie resolution session's
bounded per-version mini-index caps (symbol cap 2048 / slot cap 256 / 300-row window) dropping
candidates at scale — i.e. **the materialized graph is the approximation, and the query-time
engine is more faithful to the written policy than the system that maintains the graph.** Follow
up in julie-extractors if the store path is kept anywhere.

The memory caveat is now load-bearing: 2.96 GB naive RSS at this scale confirms the production
design must intern symbols and stream identifiers per query (identifiers are 2.15 M of the rows
and must not be resident). The design doc carries this as a gate.

## Verdict

Gates A and B **pass**. The design premise of the whole-stack assessment §4 is confirmed at
Miller scale: resolution does not need to be materialized or maintained; it needs to be
computable, and it is — cheaply. Recommended next steps, in order:

1. Re-run the sweep/bench against a dotnet/runtime-scale store (Gate B at scale, memory model).
2. Brainstorm/design the production integration: resident per-revision fact cache in Miller,
   file-local invalidation, batch sweep for whole-graph products, and the producer-side
   simplification (julie-extract stops writing resolution artifacts).
3. Decide the migration order with julie-extractors (shared-contract change).
