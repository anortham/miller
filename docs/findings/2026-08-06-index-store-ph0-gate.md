# Ph0 prototype gate — versioned index store go/no-go

**Status:** COMPLETE — all 13 verdicts recorded; overall gate **GO** with amendments. Codex
audit recorded below.
**Program:** [`docs/plans/2026-08-06-index-store-views-program.md`](../plans/2026-08-06-index-store-views-program.md)
**Plan:** [`docs/plans/2026-08-06-index-store-ph0-plan.md`](../plans/2026-08-06-index-store-ph0-plan.md)
**Evidence:** `spike/index-store-ph0/<instrument>/results.md` per task (committed on this branch).

Verdict scale: **GO** (assumption holds as designed), **GO-WITH-AMENDMENT** (holds after a named
change that Ph1 must carry), **NO-GO** (assumption refuted; program does not proceed on it).

## Verdict summary

| # | Assumption | Task | Verdict |
|---|---|---|---|
| 1 | Extraction rows are pure functions of (path, content, extractor) | 1 | **GO-WITH-AMENDMENT** |
| 2 | P1a (scope-independent resolution) landed and gated | 1 | **GO** (oracle caveat) |
| 3 | Level composition final (membership, ordering, identifier cap) | 2 | **GO-WITH-AMENDMENT** |
| 4 | Read-path overhead: manifest join vs temp visibility table | 3 | **GO** (hybrid shape) |
| 5 | Eight-view byte projection ≤1.2× with shared bases | 3 | **GO** (1.027×; retention amendment) |
| 6 | Composite-key amplification acceptable on biggest tables | 3 | **GO** (net −11.3%) |
| 7 | Filtered-retrieval equivalence (FTS arms + vector KNN, adversarial histories) | 4 | **GO-WITH-AMENDMENT** |
| 8 | DocId + BM25 per-view projection economics | 4 | **GO** |
| 9 | New-view resolution binding cost (base + delta vs full) | 5 | **SPLIT: storage GO / mechanism NO-GO** (redesign owed, with its own Ph1 proof) |
| 10 | Store growth model under churn (retention sizing) | 5 | **GO-WITH-AMENDMENT** (L1-demoted history) |
| 11 | Import transaction granularity (per-version commits vs snapshot) | 6 | **GO-WITH-AMENDMENT** |
| 12 | GC physical reclamation (auto_vacuum + FTS merge) | 6 | **GO-WITH-AMENDMENT** |
| 13 | Promotion capacity formula + migration peak-disk model | 6 | **GO-WITH-AMENDMENT** |

## 1. Purity — GO-WITH-AMENDMENT

**Assumption:** every extraction row is a pure function of (relative path, content hash,
extractor fingerprint), so identical file versions dedup to one stored copy.

**Evidence** (`spike/index-store-ph0/purity-audit/results.md`, commit `0ec78eec`):

- Complete 24-table inventory classified: **13 PURE/PURE\***, **2 MUTATED**
  (`identifiers.target_symbol_id`; `files.indexed_at/last_revision_id/status`), **5 GLOBAL-repo**,
  and **4 GLOBAL-fingerprint** (`parser_inventory` + the three `language_capability*` tables —
  compiled-in, shareable fleet-wide, not per-view). PURE\* marks tables whose bytes are unstable
  only through the metadata_json defect below; semantically pure.
- **Composite-identity audit** (the program's span-folded-ID concern): no extraction table
  carries a cross-file edge — 0 cross-file links across 103,584 linked parents, 477,002
  reference containments, 17,161 relationships, 86,974 pending rows (per-table evidence column
  in the results.md inventory). Cross-file meaning lives only in the resolution overlay and
  `pending_*`, so `(version_id, local_id)` is a valid composite identity for every per-file
  table. Task 3 modeled the composite DDL on the three biggest child tables; **the v4 DDL for
  the remaining smaller tables is Ph1 design work, not yet audited row-by-row.**
- Proven empirically, not just by code audit: adding ONE unrelated file to a fixture leaves
  every original row byte-identical except `identifiers.target_symbol_id` — the exact
  denormalized column the v4 surgery already planned to drop.
- Corrected byte split on the live 808.75MB artifact: **89.42% pure / 10.49% resolution layer /
  0.09% other** (program doc said ~88/12; conservative by ~1.5 points). Re-derived private-overlay
  arithmetic: 0.8942 + 8 × 0.1049 = **1.73×** — still far above the 1.2× criterion, so
  resolution sharing stays **v1-required**.

**Amendments Ph1 must carry:**

1. **metadata_json byte-nondeterminism (determinism/equivalence defect — reclassified).**
   `Option<HashMap<…>>` on seven extractor structs (julie-extractors
   `base/types.rs:78,124,178,294,449,487,496`) serializes in random key order per process;
   1,397 of 1,417 files (98.6%) carry multi-key metadata rows. **Pre-merge review correction
   (finding 4):** this does NOT block dedup — the store's version identity is the input tuple
   `(path, content_hash, extractor_fingerprint)` with a completion marker (program §store
   contract), so an existing complete version is skipped by key before extraction and output
   bytes never participate in identity. Task 1's evidence file frames it as "dedup ≈ 0%"; that
   framing predates this identity-contract check and is superseded here. What the defect DOES
   block: the store's row-level equivalence gate (§2's caveat), byte-identical re-extraction
   proofs, and output reproducibility generally. The BTreeMap fix + byte-stability gate stay
   queued for julie-extractors (Ph2), at reduced criticality.
2. **V-1 sequencing is cross-repo.** Dropping `identifiers.target_symbol_id` breaks Miller's
   `COALESCE` at `SqliteSymbolGraphIndex.cs:295` unless the fallback survives one extractor
   version or both changes land together.
3. **V-5 latent violation.** `writer/rows.rs:1558-1577` joins a whole-artifact SymbolLookup —
   inert today (0 cross-file hits on 703k+ linked rows) but must be narrowed before single-file
   extraction can expect byte-identical blobs.
4. `files` mutable columns move out of pure rows in v4 (view-side state).

## 2. P1a landed status — GO (with an oracle caveat the store must close)

**Evidence** (same results.md):

- Landed in julie-extractors **v2.27.0**, commit `bbbdce2c` (2026-08-05); the live Miller
  artifact already runs it. The spooled path (`writer.rs:1421`) is scope-gated; the surviving
  hard-Full site (`writer.rs:1099`) has no CLI caller.
- Gate `resolution_scope_equivalence.rs` exists and **passes 9/9** (run out-of-tree, checkout
  untouched).
- **Caveat:** the gate's oracle is a full re-derivation over the artifact's existing rows, not a
  from-scratch scan. It proves scope-independence given a fixed row set; the store owes its own
  row-level equivalence gate (incremental-converged rows ≡ from-scratch rows). Mitigation: the
  purity audit shows no extraction table carries a cross-file edge.

## 3. Level composition — GO-WITH-AMENDMENT (levels ALREADY SHIPPED; docs must reconcile)

**Assumption folded from the levels program:** final table-set membership, L2/L3 ordering, and
the per-file identifier cap decision.

**Evidence** (`spike/index-store-ph0/level-composition/results.md`, commit `bfacfe76`):

- **A two-level implementation ALREADY SHIPPED**: `--level symbols|full` in the pinned 2.27.0
  binary, `MILLER_INDEX_LEVELS`, the registry `level_policy` column, and
  `ScanIntent.LevelUpgrade`. The levels program's P1/P2 phase text still reads as future work,
  and the user was told on 2026-08-06 that 1.17.0 shipped rebind-not-levels — **that statement
  was wrong; this doc corrects the record.** Program docs must be reconciled (see Amendments).
- **Corrected byte economics** (measured, real extract pair at `/tmp/level-comp-53909`):
  L1 symbol core = **27.5%** of artifact bytes (program carried 9%); reference layer = **66.9%**
  (program carried 74%). The levels value story survives but with honest numbers: L1-first still
  defers ~2/3 of the bytes.
- **Ordering: L1 → L2 → L3** — traffic ratio 14.4× favors the reference layer over text/facts.
- **Membership calls (final):** `type_facts` and `complexity_metrics` are **L1 — already
  shipped and not relitigated** (`strip_to_symbols_level` clears exactly five families and
  neither of these; the evidence names it "the single authority"). The separate flag on
  `type_facts` is a **v4-store decision**: zero Miller consumers (1.32% of bytes), so it should
  not receive a version-qualified index budget in the store until a consumer exists.
  `pending_relationships` is L1; `reference_sites` SPLITS (5.08% L1 / 18.0% L2);
  `source_regions` `doc_comment` sits on the default search path (0.94%) — the degradation
  matrix owes a line for it.
- **No per-file identifier cap:** the generated-code density claim was refuted across 7 repos.

**Amendments:** doc reconciliation (levels program status, store program levels-fold premise,
the 1.17.0 correction); the `type_facts` exclusion decision; the doc_comment degradation line.

## 4. Read-path overhead — GO (hybrid shape, a Ph3 requirement recorded)

**Assumption:** visibility filtering (view manifest) costs little enough that views can share
one store without a dedicated per-view database.

**Evidence** (`spike/index-store-ph0/read-path/results.md`, commit `478e5f9b`): both shapes are
cheap at v1 scale, but neither wins everywhere — the answer to the program's open question is
**hybrid, routed by key**:

- Path-keyed reads enter through `view_manifest(view_id, path)` — a PK seek that returns the
  version (−0.5% vs a dedicated db; the only shape flat at 3.15× retained history, −0.9%).
- Name-keyed / candidate-set reads filter through a per-connection temp visibility table
  (+2.4% vs the manifest join's +17.8% at 1×; +17.7% vs +43.1% at 3.15×).
- Result-set equivalence: **0 mismatches** across 164,413 compared rows (views 1 and 8).
- Query plans for all twelve shape/class pairs are index seeks; nothing degrades to a scan.

**Amendments for Ph1/Ph3:** the temp visibility table (0.23ms to build for 1,417 versions) must
be built **once per read session**, not per query — Miller's open-per-query readers need the
view-aware read session from program §3. The delta-precedence `NOT EXISTS` is the reference
path's cost (+8.6–15.1% over no-visibility); the named fallback if it regresses at
dotnet/runtime scale is a materialized per-view effective-resolution index.

## 5. Eight-view bytes — GO (1.027× ≤ 1.2×; retention is the real boundary)

**Evidence** (same results.md; ratio re-verified by the lead from the raw `bytes-store.json` /
`bytes-single.json`): the 8-view family store costs **1.027×** a single index at real sampled
task-branch divergence (25 merge commits; median 1.20%, p90 6.07%) vs **8.000×** for eight
dedicated copies. **Scope of the measurement** (audit finding 3): the prototype models 70.2% of
the live artifact's bytes (symbols, identifiers, reference_sites, files, resolution + all their
indexes); the unmodeled per-file tables dedup by the same mechanism, sidecars are excluded
(Task 4 covers `search.db`/`vectors.db` separately), and a divergent file's rows are modeled as
re-extracted copies under a new version — this is a validated projection, not an end-to-end
measurement of eight real worktrees; Ph5 owes that measurement. **Pre-merge review correction
(finding 3): the modeled resolution deltas are additionally a lower bound** — the instrument
emits delta rows only for changed files' own identifiers and rows targeting changed files; it
cannot represent added/deleted-path deltas, missing-to-resolved flips, or tombstones from
name collisions. And no composed full-family footprint (store + `search.db` + `content.db` +
`vectors.db` after GC) exists anywhere in Ph0. The 1.027×→1.2× headroom absorbs substantial
model error, so the GO stands as a projection, but Ph5's success-criterion measurement must be
the composed, real-delta number. The resolution base is 11.5% of store bytes (program's ≈12% confirmed); the
seven view deltas add 1.9% total. A private resolution copy per view would cost ≈1.80× — the
shared base is what makes the gate pass, confirming **resolution sharing is v1-required**.

**Amendments — the two boundaries the gate does not clear:**

1. **Retention dominates bytes.** Two retained history generations cost **2.563×**; visibility
   read cost also scales with the retained-version multiple, not view count. The retention
   window + GC must be a Ph1 **byte contract** with measured reclamation, not a tuning knob.
2. **Divergence headroom is ~2×, not infinite.** Fit from measured points:
   ratio ≈ 0.900 + 0.0081 × summed-divergence-points; the 1.2× budget is ≈37 points (~5.3%
   average across seven siblings). Seven siblings all at p90 = 1.252× (FAIL).

## 6. Composite-key amplification — GO (net byte-positive)

**Evidence** (same results.md): composite `(version_id, local_id)` keys alone cost **+4.4%**,
but the real v4 shape — where the integer `version_id` **replaces** the 37-char `file_id` TEXT
on 981,710 child rows — lands **−11.3%** vs today's schema (symbols −7.0%, identifiers −10.7%,
reference_sites −11.7%). The one growing group is resolution rows (+9.4%: gains `base_id` +
`target_version_id`) — the accepted price of the shared base. The v4 shape's own saving is what
funds the divergence headroom in §5 (zero-divergence store = 0.890×).

## 7. Filtered-retrieval equivalence — GO-WITH-AMENDMENT (trigram ordering key must change)

**Assumption:** FTS word/trigram arms and vector KNN return byte-identical results from a
family-shared store with visibility applied inside retrieval, even under adversarial histories.

**Evidence** (`spike/index-store-ph0/retrieval/results.md`, commit `b93fa662`; adversarial bar
cleared at 339,638 invisible trigram matches vs a 200-row window, and 600 hidden-nearer vectors
vs the 500 window):

- **Word arm: PASS** at 1×/5×/20× retained multiples, all three visibility shapes, exact
  recall-set comparison. The naive post-filter starves as the program predicted (112/120 queries
  at 20×) — visibility inside retrieval is confirmed load-bearing.
- **Vector arm: PASS via pre-filter.** sqlite-vec 0.1.9 pre-applies `rowid IN (SELECT …)` and
  returns the dedicated index's exact top-K. Post-filtering has **no correctness guarantee at
  any k**: the engine caps k at 4,096 and a ceiling probe recovers 1 of 500 owed hits even
  there. Vectors stay **family-shared** (the program's open question, now answered).
- **Trigram arm: FAIL with today's ordering key, PASS with the fix.** `ORDER BY rank` fails
  equivalence (4/120 + 1/5 adversarial queries at 5×/20×) because FTS5's bm25 length
  normalization reads the whole table's average document length — hidden versions contaminate
  the ordering key itself, so no filter placement can fix it (a synthetic probe with only
  non-matching hidden rows moves 98 of 200 window members). Ordering by the stored
  `collapsed_len` is corpus-independent: 0 mismatches everywhere, faster at 20×, and it states
  the window's documented intent directly (the hardening test's own comment says rank was chosen
  because "a shorter collapsed name has higher trigram density" — verified at
  `FtsSymbolSearchIndexHardeningTests.cs:46-52`).

**Amendments:** (1) the trigram window's ordering key changes `rank` → `collapsed_len` — a
shipped-contract change needing its own equivalence gate; Ph1 decides whether it ships with the
store or earlier in the per-workspace sidecar. (2) `content.db` inherits the same rank finding
and was **not** instrumented — recorded as a gap. (3) The visibility probe must be an
integer-rowid lookup applied first (session temp table; ~7× vs the manifest join on the word
arm at 20×). (4) Retention has a **latency** price too: 1×→20× costs word 4.6×, trigram 5.4×,
vectors 3.6× (sqlite-vec KNN is brute force over total store rows) — reconcile with Task 5's
retention sizing.

## 8. DocId + BM25 economics — GO (canonical history chosen; per-view state ≈ 256 bytes)

**Evidence** (same results.md): the two shipped DocId histories **disagree today** — replaying
`AssignStableDocIds` over one file replacement vs the fresh-ordinal rule diverges at position 0,
with 888 of 1,824 positions differing. A history-dependent ordinal is fatal in a family store.
Measured options: query-time `ROW_NUMBER()` 133ms (unusable); materialized per-view mapping
2.2ms but 1.82 MB/view and a 193.9ms rebuild per manifest flip (one file change shifts 122,528
of 122,707 ordinals); stored sort key **2.6ms, 0 bytes, 0 maintenance**.

**Verdict:** canonical DocId history = the fresh-ordinal rule **expressed as an order**
(`score DESC, path ASC, start_line ASC, symbol_id ASC`) — view- and history-independent;
`AssignStableDocIds` retires from the store path. BM25: `df` is view-local already; cache
`(doc_count, avgdl)` per `(view_id, manifest_generation)` — 256 bytes for eight views vs
13.8ms/query re-scanning. Fallback if the Eros-facing `doc_id` UNIQUE column truly needs a
dense per-view ordinal: the materialized mapping, budgeted against save frequency.

## 9. New-view binding cost — SPLIT VERDICT: storage GO, mechanism NO-GO (redesign owed)

**Assumption:** a new view binds cheaply by computing a resolution delta against the shared base
(P1a's scoped pass), rather than paying a full resolution pass.

**Evidence** (`spike/index-store-ph0/resolution-growth/results.md`, commit `982dcfd7`) — the
assumption is **refuted as stated**, on three measured legs:

1. **Scope widening saturates.** `delta_scope_files` widens by symbol name; on a C# corpus one
   changed file already re-derives **74.5%** of resolution rows (a markdown control at 2.3%
   proves the scoping works — identifier density is what widens it).
2. **A populated artifact cannot take the bulk path**, so it resolves at 20.1k rows/s vs 71.5k
   from scratch. Net: a 1-file delta costs **2.7×** a from-scratch resolution pass.
3. **Real branch binds force Full anyway.** A measured sibling bind (28 indexed files, 12 new
   paths) ran **32.4% slower** than rebuilding the tip, because added/deleted paths set
   `structure_changed`. Median sibling divergence (miller 16 files, julie-extractors 28) lands in
   the flat 74–93% region of the cost curve. The 0.7 crossover fires correctly and is protective,
   but is unreachable by raw delta size on any real branch.

The program's "binds in seconds" claim fails by ~10× at 1,420 files, and the dominant term
scales with **artifact size, not delta size** — the gap widens at dotnet/runtime scale (9.6M
rows re-derived, minutes at best; inference, flagged as unmeasured).

**Split verdict, stated honestly** (audit finding 1): the storage model (shared base + per-view
deltas, §5) is untouched — this refutes the *derivation route*, and that refutation is a
**mechanism NO-GO**, not an amendment. The replacement candidate — serve the base's resolution
immediately and converge the exact per-view delta in the background — is **unmeasured**, and
during its serve window a view's resolution differs from exact by that view's delta rows
(measured at ~2.3% of base rows for a median sibling, Task 3's fan-out data; the *serving
experience* of that gap is unverified). It also relaxes the program's exact-equivalence bar for
resolution-derived reads during convergence, which the program text must state rather than
imply. **Ph1 therefore owes a binding-mechanism design with its own proof gate** — the program
does not carry a proven cheap-bind story out of Ph0. Queue for julie-extractors regardless of
mechanism choice: symbol-name scope widening, and bulk-path ineligibility for populated
artifacts. The program's "binds in seconds" claim is rewritten, not annotated.
**Lead-added Ph1 check:** determine which julie path Miller's watcher-driven single-file
converge takes today — if it is this whole-repo delta path, shipped incremental converge is
already paying near-full resolution per save on identifier-dense repos.

## 10. Growth + retention — GO-WITH-AMENDMENT (7 days, L1-demoted, double-guarded)

**Evidence** (same results.md; bytes/version measured on real artifacts — miller full 540,715 B,
miller L1 148,953 B = 27.5%, julie-extractors 502,715 B, dotnet/runtime 529,300 B):

- **Full-level retention blows the ≤1.2× budget at any window**: 7 days alone costs 1.39×
  (miller) / 1.25× (julie-extractors), leaving nothing for the views.
- **L1-demoted history makes it fit**: 7 days = 1.11× / 1.07×, leaving ~0.09–0.13× for view
  deltas. 14 days (1.28× / 1.16×) already breaches on the busier history.
- dotnet/runtime anchored on its real 20.41 GiB index: 1 week = 28.3 GiB all-full vs **22.6 GiB**
  L1-history.

**Verdict:** default retention = **7 days with retained non-live versions demoted to L1**, plus
two guards the window cannot provide: a **byte ceiling** (suggested: prune oldest-first past
~1.25× the live index) and a **per-path version cap** (git history is a *lower bound* on store
versions — the watcher indexes uncommitted states, and agent fleets churn hot files). 14 days
documented as tunable-up; >4 weeks opt-in. Note the cross-task convergence: Task 2's L1 split
(27.5%) is what makes retention affordable, Task 3 shows retention dominates bytes, Task 4 shows
retention costs read latency — **the retention contract is the central Ph1 design item.**

## 11. Import transaction granularity — GO-WITH-AMENDMENT (single-transaction refuted)

**Assumption:** the store can import with commit units that make partial work crash-reusable,
replacing julie's single-transaction snapshot write.

**Evidence** (`spike/index-store-ph0/write-mechanics/results.md`, commit `7b367a13`; 6 modes ×
3 SIGKILL trials each, `quick_check` ok and 0 orphan child rows after all 18 kills):

- **Today's `single` mode loses everything, every time** — zero reusable versions in each of
  its three SIGKILL trials (18 kills total across the six modes) — and needs a WAL at 100.6% of
  the database, ~15.9 GB projected at dotnet/runtime scale.
- **Per-commit-unit modes reuse exactly what they committed**: `marked_complete == reusable ==
  resume_skipped` in all 15 non-single trials.
- **The completion marker is load-bearing as a matter of observed fact**: in a no-marker trial a
  truncated version survived into the final store with `quick_check = ok` and became
  dedup-visible. Doubt-pass finding 7 reproduced as a real defect, not a theory.
- **Durability is cheap**: `synchronous=FULL` measured inside noise; `wal_autocheckpoint=8000`
  buys back 1.7× of per-version's throughput for a 38.5 MB WAL.

**Ph1 contract:** per-chunk commits with the completion marker in the same transaction as the
last child row; chunk size derived from a WAL budget (8.6 MB per version, 142 MB per 100);
`synchronous=FULL`; dedup reads only `complete = 1`. Caveat: measured throughput is DB-insert
only — extraction dominates real imports, which raises the value of crash-reusable work.

## 12. GC physical reclamation — GO-WITH-AMENDMENT (one new schema rule, one index tension)

**Evidence** (same results.md): deleting 40% of versions from a 2 GB store returned **31.9%** of
the file via create-time `auto_vacuum=INCREMENTAL` + staged `incremental_vacuum` — 79 stages,
worst stage 0.104s, stage cost set by the page budget rather than store size (background GC can
hold any latency bound). The negative control (`auto_vacuum=NONE`, identical deletes) reclaimed
**0% silently**. FTS5: bounded `merge` compacted 23 → 2 segids in 0.251s total; the **delete
dominates GC cost** (58.1s vs 0.25s) — budget on the delete. Secure-delete matrix: **both**
switches (core pragma + FTS5 option) are required — the FTS5 delete otherwise *writes a fourth
copy* of the term into a tombstone segment; core `secure_delete` is per-connection and never
stored, so every writer must re-assert it.

**Amendments:** (1) **NEW v4 schema rule** — every secondary index on a version-keyed table must
**lead** with `version_id`, or its pages strand on delete (the five non-conforming indexes are
the entire 220.4 MB gap between incremental vacuum and full VACUUM). This **tensions against
Task 3's read-path finding** (in-index visibility filtering wants `version_id` *last*); Ph1 must
reconcile per-index before the contract freezes. (2) `auto_vacuum` set after creation is a
silent no-op — the migration preflight must verify `PRAGMA auto_vacuum` on every created file.
(3) The sidecar chain is delete → merge → incremental_vacuum and only the last step moves the
file — the dashboard must measure reclaimed bytes there, not after the merge.

## 13. Promotion capacity + migration peak — GO-WITH-AMENDMENT (formula corrected)

**Evidence** (same results.md): the formula's terms are real — measured peaks landed at
**−0.03% / −0.02%** on the two arms whose terms genuinely coexist, and a pinned reader adds
exactly its retained generation (926.2 MB; peak 2.13× the family baseline). Retention-first
rebuilds validated: peak −29%, final store −40%.

**Two corrections the program doc must fold (edit, not annotate):**

1. The formula is a **max over phases, not a sum over the operation** — the retention sweep's
   peak and the rebuild's peak never coexist (the sweep's WAL checkpoints away first). A
   summing preflight over-reserves.
2. **The retention sweep's own WAL is an unnamed term**: purging 40% of an 825.8 MB store
   produced a 466.0 MB WAL (56% of store size) before any rebuild began. A preflight modeling
   only the rebuild under-reserves for purge-heavy operations.

dotnet/runtime projections (arithmetic, not measured): promotion peaks ~37.6 GB (no reader) /
~55.3 GB (pinned reader) / ~26.6 GB (retention-first); staged vacuum ~38s in 0.104s steps.
Crash safety is proven against process death (the realistic Miller failure: OOM, exit 137), not
power loss; `synchronous=FULL` being free closes most of that gap.

## Overall gate verdict — GO to Ph1, with the §9 red proof carried as Ph1's entry gate

Thirteen of thirteen assumptions have verdicts. Twelve are GO or GO-WITH-AMENDMENT; **one — the
view-binding mechanism (§9) — is a NO-GO as designed** (a red proof), with the storage half of
that assumption intact.

**Gate posture, stated precisely** (pre-merge review finding 1): the program's rule is "does
not proceed past a red gate." This verdict honors it as follows — the red proof is **not
waived**: the binding-mechanism design + measured proof is Ph1's FIRST deliverable and a hard
**freeze precondition** for the store contract (recorded as a Ph1 entry condition and
acceptance box in the program doc). Contract sections independent of binding (schema,
identity, levels, durability, GC, retention, promotion, concurrency) are green-lit by their
own proofs and may be drafted meanwhile. The alternative posture — holding Ph0 open until a
binding mechanism passes — is the user's to choose at the merge boundary; this document
recommends the carried-gate posture because every input to the binding proof (Task 5's
instrument, the measured full-pass fallback) already exists and blocks nothing else.

**What is proven vs conditional** (audit finding 6 — stated precisely):

- **Proven now:** extraction rows are *semantically* pure with one denormalized column (§1);
  eight views share one modeled store at **1.027×** (§5, a 70.2%-coverage projection); the v4
  row shape is 11% smaller (§6); word-arm FTS and pre-filtered vector KNN return exact results
  with visibility inside recall (§7); per-commit-unit import is crash-reusable and GC
  physically reclaims in bounded stages (§11–12).
- **Conditional on named, unlanded fixes:** *byte* purity — and therefore any real dedup —
  waits on the metadata_json BTreeMap fix (98.6% of files affected today, §1); trigram-arm
  equivalence waits on the `collapsed_len` ordering change (§7); `content.db` retrieval was
  not instrumented at all (§7); cheap view binding has **no proven mechanism** (§9).

The gate's honest price list — what changed from the program as written:

1. **metadata_json determinism blocker** (§1): julie-extractors BTreeMap fix + stability gate
   before any dedup measurement is credible.
2. **Trigram ordering key changes** (§7): `rank` → stored `collapsed_len`; a shipped-contract
   change with its own equivalence gate; `content.db` inherits the finding (uninstrumented gap).
3. **Binding mechanism NO-GO** (§9): the scoped-pass-as-binder claim is dead; the candidate
   replacement (serve-base + background convergence) is unmeasured and Ph1 owes it a design
   with its own proof gate; two julie-extractors fixes are queued regardless.
4. **Retention is the central Ph1 contract** (§5, §7, §10): 7-day L1-demoted default, byte
   ceiling, per-path cap — it is simultaneously the byte lever, the latency lever, and the
   growth guard.
5. **Durability contract** (§11): per-chunk + completion marker + `synchronous=FULL`.
6. **Index-direction reconciliation** (§12 vs §4): `version_id`-leading for reclamation vs
   trailing for in-index visibility — per-index decisions owed in Ph1.
7. **Promotion preflight** (§13): max-over-phases with the retention-sweep WAL term.

Ph1 (store contract design) proceeds on these amendments plus the §9 redesign obligation.

## What Ph0 did NOT prove (read before citing this gate)

- **Scale:** every instrument ran at Miller scale (~1,420 files / 123k–554k rows per table) or
  synthetic row shapes. All dotnet/runtime figures — binding (§9), growth (§10), WAL/promotion
  peaks (§11, §13) — are stated arithmetic projections, never measurements. Ph5 owes the real
  runs.
- **Platform:** one machine — macOS/Apple Silicon/APFS, SQLite 3.53.4 via Python — not Miller's
  own SQLite build or Windows/Linux filesystem semantics.
- **Crash model:** process death (SIGKILL) only; power loss untested (§13).
- **Retrieval gaps:** `content.db` uninstrumented (§7); real-edit row-shape drift approximated
  by copied shapes (§5).
- **Dedup rate on real data:** unmeasurable until the metadata_json fix lands (§1) — every
  current dedup claim is structural, not observed on a live corpus.

## Codex audit — recorded, all findings dispositioned

Adversarial completeness audit run per the execution agreement (codex, read-only, xhigh
reasoning; full transcript hash in `.razorback/sdd/ph0-audit-result.txt` on the branch's
working tree). Nine findings; every one verified against the evidence before disposition:

| # | Severity | Finding | Disposition |
|---|---|---|---|
| 1 | critical | §9 GO-WITH-AMENDMENT was a NO-GO in disguise (replacement unmeasured; serve-window breaks exact equivalence) | **Accepted** — §9 reclassified to a split verdict (storage GO / mechanism NO-GO); overall verdict reframed; Ph1 owes the redesign its own proof gate |
| 2 | major | Composite-identity audit not surfaced as complete | **Accepted (doc fix)** — Task 1's zero-cross-file-edge evidence now stated as the identity result in §1, with the smaller-table v4 DDL gap named |
| 3 | major | 1.027× stated without its 70.2%-coverage / sidecar / row-shape caveats | **Accepted** — caveats added to §5 and the overall verdict ("validated projection, not end-to-end") |
| 4 | major | Scale + platform proofs unmeasured but readable as proven | **Accepted** — "What Ph0 did NOT prove" section added |
| 5 | major | §3 misquoted the level evidence on `type_facts` (final call is L1-stays; flag is v4 index budget) | **Accepted (verified in level-composition/results.md §2)** — §3 rewritten |
| 6 | major | Overall summary converted prospective fixes into present proof | **Accepted** — proven-vs-conditional split added to the verdict |
| 7 | major | `Status: COMPLETE` self-contradicted (audit/program updates pending) | **Accepted** — status was premature when written; resolved by this recording and the program-doc update landing in the same commit |
| 8 | minor | Purity counts double-counted (15+2+5+4=26 vs the evidence's 24) | **Accepted (verified: 13 PURE/PURE\* + 2 MUTATED + 5 GLOBAL-repo + 4 GLOBAL-fingerprint = 24)** — §1 corrected |
| 9 | minor | "0/18" conflated all-mode kills with single-mode trials | **Accepted** — §11 corrected to per-trial phrasing |

No finding was disputed. The audit's overall challenge — "not sound as a completed GO gate" —
was correct against the draft it read; this recorded version is the post-audit document.

## Program-doc updates

Applied in the same commit as this recording: Ph0 acceptance boxes ticked with pointers here;
the open-questions section's Ph0 items replaced with answers (read-path shape → hybrid §4;
vector sharing → family-shared §7; retention default → §10); the §9 binding-claim rewrite and
promotion-formula edit (§13) applied to the program text.

## Pre-merge external review (codex) — 5 findings, all verified and folded

Adversarial branch review per the execution agreement (codex, read-only, full-branch diff with
evidence JSONs on disk; verdict `needs-attention`). Every finding verified before disposition:

| # | Severity | Finding | Disposition |
|---|---|---|---|
| 1 | high | Overall GO proceeds past the program's own red-gate rule (§9 is a red proof; its replacement unmeasured) | **Accepted (posture fix + user flag)** — the red proof is now carried explicitly: Ph1 entry condition + contract-freeze precondition recorded in the program doc; the overall verdict restates the posture and names the alternative (hold Ph0 open) as the user's call at the merge boundary |
| 2 | high | The program doc still mandated the refuted P1a binder in seven normative places (§model, bounds, carries-forward, verbs, event table, success criterion, Ph2 scope) | **Accepted (verified by grep)** — every normative reference rewritten to "Ph1-proven mechanism"; the event-cost table and serve SLO re-scoped |
| 3 | high | 1.027× is a partial lower-bound model (deltas omit added/deleted paths + tombstones; no composed store+sidecars footprint) | **Accepted** — lower-bound caveat added to §5; Ph5's success measurement defined as the composed real-delta number |
| 4 | medium | metadata_json nondeterminism wrongly classified a dedup blocker — store identity is input-keyed; imports skip by key | **Accepted (verified against the program's `file_versions` identity)** — §1 reclassified to a determinism/equivalence defect at reduced criticality; correction note appended to the Task 1 evidence file |
| 5 | medium | Ticked acceptance boxes vs deliverables: five results.md files lacked the plan-mandated verification ledger; Task 4's box read as a clean equivalence pass | **Accepted** — ledgers appended to all five (sourced from worker reports); Task 4's box rewritten to record the rank refutation + replacement pass |

Single-pass rule honored: fixes applied, fast suite re-run, no second review round. Codex does
not report per-request token counts.
