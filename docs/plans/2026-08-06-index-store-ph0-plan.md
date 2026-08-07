# Versioned Index Store — Ph0 Prototype Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when
> subagent delegation is available. Fall back to razorback:executing-plans for single-task,
> tightly-sequential, or no-delegation runs.

**Goal:** run the Ph0 hard go/no-go gate of the
[versioned index store program](2026-08-06-index-store-views-program.md): audits and throwaway
instruments that prove or refute every load-bearing assumption before any contract work.

**Architecture:** six independent worker tasks (two audits, four measurement instruments) feed a
lead-written findings doc with a go/no-go call per assumption. No production code changes in this
phase — instruments are throwaway prototypes under `spike/`, per razorback:prototyping.

**Tech Stack:** SQLite (CLI + any convenient driver), FTS5, sqlite-vec, the pinned
`.tools/julie-extract` binary, bash or small C#/python scratch programs (implementer's choice —
throwaway code, reproducibility is the only bar).

**Architecture Quality:** No production architecture impact in this phase (measurement only).
The program's approved module/interface shape and its high-risk rating are recorded in
`docs/plans/2026-08-06-index-store-views-program.md` §Architecture quality; a task that finds it
needs to touch `src/` or `tests/` reports a plan mismatch instead of proceeding.

## Global Constraints

- Nothing under `src/` or `tests/` changes in this phase. Instruments live in
  `spike/index-store-ph0/<task-dir>/` and must be re-runnable from one entry script per task.
- Commit instrument code + `results.md` only. Generated databases/artifacts are cleaned up by the
  entry script; nothing over ~10 MB gets committed.
- Reads of the real Miller artifact are read-only against the MAIN checkout:
  `sqlite3 "file:/Users/murphy/source/miller/.miller/symbols.db?mode=ro"` (never this worktree's
  own `.miller`, never a writable open).
- dotnet/runtime-scale claims use synthetic generation at the recorded row counts (identifiers
  12.86M, reference_sites 15.5M, symbols 2.58M, pending 2.49M — from
  `docs/plans/2026-08-03-progressive-indexing-levels-program.md`); the real clone was deleted.
  Cap on-disk synthetic data at ~4 GB and project beyond with stated arithmetic.
- julie-extractors investigation is **read-only** in `/Users/murphy/source/julie-extractors` — no
  worktree there, no writes, no builds that mutate the checkout.
- TDD does not apply to throwaway instrument code (razorback:prototyping). It applies to
  production code, of which this plan contains none by design.
- Every claim in a `results.md` carries evidence: `file:line` for code claims, the script +
  captured output for measurements, stated arithmetic for projections.
- Worker dispatches begin with
  `cd /Users/murphy/source/miller/.claude/worktrees/index-store-ph0` and verify
  `pwd`/`git branch --show-current`/`git rev-parse --short HEAD` before any other action.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (this worktree's copy) — testing split, build guards.

**Worker red/green scope:** the task's entry script runs end-to-end cleanly from a fresh clone of
its directory state, and `results.md` states every acceptance item with evidence. No dotnet test
run is required for spike-only changes.

**Worker ceiling:** `scripts/test.sh` (fast suite) — only if a worker suspects it touched
anything tracked outside its owned directory; workers do not run the scale suite.

**Worker gate invariant:** entry script reproducibility proves the measurement is real;
`git status` limited to owned paths proves isolation.

**Lead affected-change scope:** before each commit batch: `git status --short` shows only
owned-path changes; `scripts/test.sh` green (guards against accidental tracked-file damage).

**Branch gate:** `scripts/test.sh` green + the Ph0 findings doc complete with a go/no-go call
per assumption + codex audit of the findings doc recorded.

**Replay/metric evidence:** hard gates — 8-view physical bytes vs the ≤1.2× target;
filtered-retrieval equivalence under adversarial history; GC physical shrinkage; crash-reuse of
complete versions. Report-only — overhead percentages, throughput numbers, growth projections.

**Escalation triggers:** any need to modify `src/`/`tests/`, any julie-extractors write, any
instrument requiring >4 GB disk → stop, report plan mismatch.

**Assigned verification failure:** workers stop and report; they do not redefine their gate.

**Verification ledger:** each task's `results.md` ends with a ledger line: commands run, worktree
SHA, result, timestamp.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Purity audit + P1a status | Batch A | Create: `spike/index-store-ph0/purity-audit/**` | No | None - safe parallel batch. |
| Task 2: Level composition inputs | Batch A | Create: `spike/index-store-ph0/level-composition/**` | No | None - safe parallel batch. |
| Task 3: Read-path + bytes instrument | Batch A | Create: `spike/index-store-ph0/read-path/**` | No | None - safe parallel batch. |
| Task 4: Filtered-retrieval instrument | Batch A | Create: `spike/index-store-ph0/retrieval/**` | No | None - safe parallel batch. |
| Task 5: Resolution binding + growth model | Batch A | Create: `spike/index-store-ph0/resolution-growth/**` | No | None - safe parallel batch. |
| Task 6: Write-side mechanics instrument | Batch A | Create: `spike/index-store-ph0/write-mechanics/**` | No | None - safe parallel batch. |
| Task 7: Go/no-go findings doc (lead) | None - serial | Create: `docs/findings/2026-08-06-index-store-ph0-gate.md`; Modify: `docs/plans/2026-08-06-index-store-views-program.md` (open questions + Ph0 acceptance boxes) | Yes | Synthesizes Tasks 1–6 results; lead work by the execution agreement. |

Commit mode: **parallel-lead-commit** — workers hand verified diffs to the lead; the lead
reviews inline and commits.

---

### Task 1: Purity audit + P1a status (julie-extractors, read-only)

**Files:**
- Create: `spike/index-store-ph0/purity-audit/results.md`

**Interfaces:**
- Consumes: `/Users/murphy/source/julie-extractors` sources (read-only); the program doc's
  purity requirement (§"Relationship to the base+overlay refutation").
- Produces: a complete artifact table inventory classified per-file-pure / global /
  mutated-after-extraction, the v4 schema-surgery list, and a definitive P1a status — Task 7's
  primary input for the purity go/no-go.

**Contract inputs:** known facts to verify and extend, not re-derive: `identifiers.target_symbol_id`
denormalization is written back by the resolution store
(`crates/julie-extract-artifact/src/resolution_store.rs:571` area; 156,953/380,720 rows live);
`RESOLUTION_VERSION = 6` (`crates/julie-extract-cli/src/resolution.rs:1502`);
`DELTA_SCOPE_CROSSOVER = 0.7` (`resolution.rs:2674`); Miller reads the denorm column via
`COALESCE` (`src/Miller.Indexing/SqliteSymbolGraphIndex.cs:295`).

**File ownership:** Create: `spike/index-store-ph0/purity-audit/**`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** an evidence-backed audit answering: which artifact tables are pure functions
of (relative path, file content, extractor fingerprint)? Start from the artifact writer's actual
schema (walk `julie-extract-artifact`'s writer/schema code for the COMPLETE table list —
relationships, annotations, literals, type facts/arguments, complexity, diagnostics, capability
metadata, revision tables included). For each table: pure / global / per-file-but-mutated, with
`file:line` for every write site that violates purity. Specify the v4 surgery per violation
(e.g., strip `target_symbol_id` to the view overlay). Separately: P1a delta-scoped resolution —
landed or not, in which julie-extract version, and whether the N-incremental-steps ≡ one-full-scan
equivalence gate exists and passes (name the test).

**Approach:** read the writer and resolution code paths; query the real Miller artifact
(read-only) to confirm or refute each purity classification empirically where a query can
(e.g., columns whose values change across two scans of identical content). Do not trust doc
claims — the program doc's 85–88% pure-bytes figure is a hypothesis this task confirms or
corrects.

**Acceptance criteria:**
- [x] Every artifact table appears in the inventory with a classification and evidence.
- [x] Every purity violation has a write-site `file:line` and a v4 surgery line.
- [x] Corrected pure-vs-global byte split for the real Miller artifact (dbstat arithmetic shown).
- [x] P1a status definitive: version, mechanism entry points, equivalence-gate test name + result.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 2: Level composition inputs

**Files:**
- Create: `spike/index-store-ph0/level-composition/results.md`

**Interfaces:**
- Consumes: `docs/plans/2026-08-03-progressive-indexing-levels-program.md` (open P0 questions,
  level strawman); the real Miller artifact (read-only).
- Produces: a recommended level assignment for EVERY artifact table with byte shares, plus the
  per-file identifier-cap recommendation — Task 7's input for the level-composition call.

**Contract inputs:** levels strawman (L1 symbol core / L2 reference layer / L3 text+facts);
open questions: where type_facts and complexity_metrics land; whether L3 precedes L2; whether a
per-file identifier cap ships alongside. Byte/usage data: reference layer 74% of bytes / 7% of
calls; generated code ~43 identifiers/KB vs real code ~5–10.

**File ownership:** Create: `spike/index-store-ph0/level-composition/**`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** a decision table: every artifact table → recommended level, byte share
(dbstat on the real artifact, read-only), tool surfaces served, and extraction-cost note.
Answer the three open questions with data: measure the per-file identifier-density distribution
on the real artifact (identifiers per KB by file, flagging the generated-code tail) and
recommend cap-or-not with a concrete threshold if yes.

**Approach:** dbstat + GROUP BY queries against the read-only artifact; join per-file identifier
counts against file sizes from the `files` table. Keep the recommendation table small and
decisive — this feeds a design call, not a report.

**Acceptance criteria:**
- [x] Every artifact table has a level assignment with byte share.
- [x] The three open questions each get a data-backed recommendation.
- [x] Identifier-density distribution reported with the generated-code tail quantified.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 3: Read-path + bytes instrument

**Files:**
- Create: `spike/index-store-ph0/read-path/` (entry script + `results.md`)

**Interfaces:**
- Consumes: the real Miller artifact (read-only source data); program doc §1 (composite
  identity), §3 (visibility shapes).
- Produces: measured read overhead (manifest join vs temp visibility table vs dedicated-db
  baseline), composite-key amplification on the biggest tables, and the 8-view physical-byte
  measurement vs the ≤1.2× target — Task 7's input for the storage and read-path calls.

**Contract inputs:** resolution layer ≈ 12% of store bytes (shared-base model: deltas only per
view); target ≤1.2× a single index for 8 views at typical task-branch divergence; typical
divergence = 0.5–5% of files (sample real branch diffs from this repo's git history to pick the
distribution).

**File ownership:** Create: `spike/index-store-ph0/read-path/**`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** a version-keyed store prototype populated from the real artifact's symbols +
identifiers + reference_sites (transformed copy, composite `(version_id, local_id)` keys), with
8 view manifests at sampled divergences. Measure: (1) representative reads (name lookup, file
symbol listing, refs-by-symbol) under manifest-join vs per-connection temp visibility table vs a
dedicated single-view db — report % overhead; (2) composite-key size amplification: same data
under current keys vs composite keys, per table and total; (3) physical bytes: the 8-view store
(shared rows + shared resolution base + per-view deltas at measured share) vs 8 dedicated
copies, reported against 1.2×.

**Approach:** SQL transformation scripts; `PRAGMA page_count*page_size` and dbstat for bytes;
repeat timed queries with warm cache, median of ≥5 runs. State the divergence distribution used
and why.

**Acceptance criteria:**
- [x] Read overhead numbers for both visibility shapes vs baseline, per query class.
- [x] Composite-key amplification per table + total, with the schema DDL diff shown.
- [x] 8-view bytes vs 1.2× target: explicit PASS/FAIL.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 4: Filtered-retrieval instrument (FTS, vectors, DocId/BM25)

**Files:**
- Create: `spike/index-store-ph0/retrieval/` (entry script + `results.md`)

**Interfaces:**
- Consumes: `src/Miller.Indexing/FtsSymbolSearchIndex.cs` (word arm uncapped, trigram window
  200, BM25 inputs `_documentCount`/`_avgdl`/df), `src/Miller.Indexing/SqliteSymbolReader.cs:45`
  (fresh ordinal DocIds), `src/Miller.Indexing/SearchIndexWriter.cs:436` (stable DocId reuse)
  and `:592` (full-table stats) — read-only, as behavioral reference.
- Produces: the filtered-retrieval equivalence verdict under adversarial history, the sqlite-vec
  pre-filter verdict, and the DocId/BM25 canonical-history recommendation with measured costs —
  Task 7's input for the byte-identical-search call.

**Contract inputs:** adversarial bars from the program doc: >200 hidden (invisible-version)
trigram matches and >500 hidden vector matches crowding the windows; equivalence bar =
recall sets identical to a dedicated per-view FTS.

**File ownership:** Create: `spike/index-store-ph0/retrieval/**`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** an FTS5 prototype with version-keyed rows mirroring the real sidecar's two
arms (word + collapsed trigram). Prove: visibility joined INSIDE the query before
`ORDER BY rank LIMIT` returns recall sets identical to a dedicated per-view index, including
with >200 hidden trigram matches; measure overhead at 1×/5×/20× stored-version multiples.
sqlite-vec: determine whether KNN can apply a visibility filter before top-K (partition key,
metadata filter, or post-filter with over-fetch factor) and at what cost with >500 hidden
matches; if it cannot, recommend per-view vectors. DocId/BM25: measure per-view stat
maintenance (count/avgdl) and the two per-view DocId options — query-time `ROW_NUMBER()` over
the visible set vs a materialized per-view mapping table — cost per query and bytes for 8 views;
recommend the canonical history (must reconcile fresh-ordinal vs stable-reuse behaviors).

**Approach:** build from real sidecar data where convenient (read-only source), synthetic
padding for the adversarial multiples. Equivalence = set comparison, not eyeballing.

**Acceptance criteria:**
- [x] Equivalence proven (identical recall sets) under the adversarial trigram history.
- [x] Overhead at 1×/5×/20× version multiples reported for both arms.
- [x] sqlite-vec pre-filter verdict with mechanism named, or per-view fallback recommended.
- [x] DocId recommendation with measured per-query cost and 8-view bytes for both options.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 5: Resolution binding + growth model

**Files:**
- Create: `spike/index-store-ph0/resolution-growth/` (entry script + `results.md`)

**Interfaces:**
- Consumes: pinned `.tools/julie-extract` (this worktree's copy), a scratch fixture repo it may
  scan (create under this task's directory or `$TMPDIR` — never scan a real checkout with a
  writable artifact path), git history of this repo (read-only).
- Produces: the new-view resolution binding cost curve and the retention growth model — Task 7's
  input for the bootstrap-cost and retention calls.

**Contract inputs:** `DELTA_SCOPE_CROSSOVER = 0.7`; the program's bound: "base + delta up to the
crossover; honest full rebase beyond"; SLO scope = typical task-branch divergence.

**File ownership:** Create: `spike/index-store-ph0/resolution-growth/**`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** (1) binding cost: on a copied fixture repo (e.g., a scratch copy of this
repo's tracked files), measure whole-repo scan resolution time at 1/5/25/120 changed files vs a
full pass with the pinned julie-extract, confirming delta cost tracks delta size below the
crossover (this re-runs the cost-model curve at the current pin — cite
`docs/findings/2026-08-05-rebind-p1-cost-model.md` for the prior shape). (2) growth model: from
git history (`git log --diff-filter=ACMR --name-only` + blob identity) of this repo AND
julie-extractors (busier), count unique (path, blob) versions per 1/2/4/8-week windows; convert
to store bytes using measured bytes-per-version from the real artifact; project dotnet/runtime
by file-count scaling; recommend a retention default.

**Approach:** julie-extract runs go through the worktree's `.tools` binary with `--jobs`
bounded (≤4) and artifact paths inside the task directory; clean up artifacts after measuring.

**Acceptance criteria:**
- [x] Binding cost curve at the four delta sizes vs full pass, crossover behavior confirmed.
- [x] Growth curves for both repos per retention window, with bytes-per-version stated.
- [x] Retention default recommendation with the projection arithmetic shown.
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 6: Write-side mechanics instrument (GC, transactions, promotion capacity)

**Files:**
- Create: `spike/index-store-ph0/write-mechanics/` (entry script + `results.md`)

**Interfaces:**
- Consumes: program doc §5 (GC/purge contract, promotion-capacity formula), §1 (completion
  markers), cycle-2 findings 7–9.
- Produces: proof that GC reclaims physical bytes, the transaction-granularity decision table,
  and measured promotion peak-disk vs the formula — Task 7's input for the durability and GC
  calls.

**Contract inputs:** `auto_vacuum=INCREMENTAL` must be set at creation; FTS5 page-limited
`merge` (not `optimize`) for bounded work; FTS5 `secure-delete` option; formula: peak = old
generation + new generation + sidecars + WAL/temp + reader-retained generations.

**File ownership:** Create: `spike/index-store-ph0/write-mechanics/**`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** on a synthetic version-keyed store (~2–4 GB, generated): (1) GC: delete
version cohorts, run `incremental_vacuum` + page-limited FTS `merge`, prove file-size shrinkage
(`stat`, not freelist counts); verify FTS5 `secure-delete` behavior; time the merges. (2)
transaction granularity: bulk import at per-version / per-chunk(~100 files) / single-transaction
— throughput, WAL peak size, then SIGKILL mid-import per mode and measure how many complete
versions the next run reuses (completion-marker semantics). (3) promotion: rebuild a generation
alongside the live one and record actual peak disk vs the formula.

**Approach:** synthetic rows sized from real-artifact averages (state them); SIGKILL via
`kill -9` on the importer process at randomized points, ≥3 trials per mode; keep everything
inside the task directory and clean up.

**Acceptance criteria:**
- [x] Physical shrinkage proven with before/after file sizes; merge timings recorded.
- [x] Granularity table: throughput, WAL peak, crash-reuse count per mode, ≥3 SIGKILL trials each.
- [x] Measured promotion peak within stated tolerance of the formula (or the formula corrected).
- [x] Worker-scope verification passes and the diff is handed to the lead.

### Task 7: Go/no-go findings doc (lead)

**Files:**
- Create: `docs/findings/2026-08-06-index-store-ph0-gate.md`
- Modify: `docs/plans/2026-08-06-index-store-views-program.md` (Ph0 acceptance boxes, open
  questions answered)

**Interfaces:**
- Consumes: all six `results.md` files.
- Produces: the Ph0 gate verdict — a go/no-go call per load-bearing assumption, feeding the
  user's Ph1 approval decision.

**Contract inputs:** the program doc's Ph0 acceptance list; the execution agreement (codex
audits the findings doc).

**File ownership:** Create: `docs/findings/2026-08-06-index-store-ph0-gate.md`; Modify:
`docs/plans/2026-08-06-index-store-views-program.md`

**Serialization required:** Yes

**Dependency reason:** Synthesizes Tasks 1–6 results; lead work by the execution agreement.

**What to build:** the findings doc: per-assumption verdict (purity, storage arithmetic,
bootstrap cost, filtered-retrieval equivalence, DocId/BM25 economics, GC reclamation,
transaction granularity, promotion capacity, retention default, level composition), each with
the measured evidence and a go / no-go / go-with-amendment call. Codex audit of the doc
(completeness critic: what's unmeasured, what's overclaimed), verdicts folded. Program doc
updated: Ph0 boxes ticked, open questions replaced with answers.

**Acceptance criteria:**
- [ ] Every Ph0 acceptance item from the program doc has a verdict with evidence.
- [ ] Codex audit recorded in the findings doc with dispositions.
- [ ] Program doc Ph0 boxes and open questions updated.
- [ ] Branch gate green (`scripts/test.sh` + findings complete).
