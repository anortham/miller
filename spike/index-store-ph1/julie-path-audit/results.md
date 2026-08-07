# Ph1 Task 2 — Miller watcher converge path + julie-extractors fix surfaces (read-only audit)

**Scope:** evidence audit. No Miller or julie-extractors source was modified. Every claim below
carries a current `path:line` citation, re-verified against the trees named in the ledger.

**Trees**

| | |
|---|---|
| Miller | `/Users/murphy/source/miller/.claude/worktrees/index-store-ph1`, branch `worktree-index-store-ph1` |
| julie-extractors | `/Users/murphy/source/julie-extractors`, `main` @ `ab7b16a` (`release: prepare v2.27.0`), clean, READ-ONLY |
| julie-extract binary | `2.27.0` — matches `scripts/julie-pins.json:2` |

**Anchor moves recorded** (Ph0 anchors that no longer resolve as written — §0).

---

## 0. Anchor re-verification

| Ph0 anchor | Status | Current location |
|---|---|---|
| `julie-extract-cli/src/writer.rs:1421` — `is_full_scan: structure_changed \|\| force` | **MOVED (crate)** | `crates/julie-extract-artifact/src/writer.rs:1421`. Text is `is_full_scan: structure_changed \|\| revision.mode == Some(WriteMode::Force)` — the `force` term is a `WriteMode` comparison, not a bool parameter. |
| `julie-extract-cli/src/writer.rs:1417` — `structure_changed = …` | **MOVED (crate + line)** | `crates/julie-extract-artifact/src/writer.rs:1416` (the `let`; the expression body is on `:1417`). Text unchanged. |
| `julie-extract-cli/src/resolution.rs:2922` — `delta_scope_files` | **HOLDS** | `crates/julie-extract-cli/src/resolution.rs:2922` |
| `julie-extract-cli/src/resolution.rs:2674` — `DELTA_SCOPE_CROSSOVER = 0.7` | **HOLDS** | `crates/julie-extract-cli/src/resolution.rs:2674` |
| `base/types.rs:78,124,178,294,449,487,496` — seven `Option<HashMap>` | **HOLDS (all seven, exact lines)** | `crates/julie-extractors/src/base/types.rs` |
| `artifact_is_unwritten` requires zero files AND zero revisions | **HOLDS** | `crates/julie-extract-artifact/src/writer.rs:1671-1679` |

There is no `julie-extract-cli/src/writer.rs`. The writer moved to the `julie-extract-artifact`
crate; `resolution.rs` stayed in `julie-extract-cli`. Anything citing a bare
`julie-extract-cli/src/writer.rs` path (including Ph0 `results.md` §1.1 and §1.4) needs the crate
prefix corrected.

**One environment note:** this worktree has **no `.tools/` directory** — julie-extract was never
restored here. The probes below used the main checkout's pinned binary
`/Users/murphy/source/miller/.tools/julie-extract`, verified `2.27.0` against
`scripts/julie-pins.json:2` before use.

---

## 1. Section A — what Miller's watcher pays today

### 1.1 The path, end to end

Miller's single-file save does **not** issue a whole-repo `scan`. It issues `update --file`.

| # | Step | Citation |
|---|---|---|
| 1 | Watcher attached (file + directory watchers, always both) | `src/Miller.Server/Hosting/IndexerWatcherSet.cs:24` |
| 2 | Debounce tick — **250 ms**, collects a burst then drains once | `src/Miller.Server/Hosting/IndexerService.cs:32` (`DebounceInterval`), loop at `:476` |
| 3 | `RunDrainTick` → `IndexerCore.DrainAndProcess` | `src/Miller.Server/Hosting/IndexerService.cs:588`; `src/Miller.Server/Hosting/IndexerCore.cs:229` |
| 4 | Events routed to ops; an existing changed path → `UpdateOp` | `src/Miller.Core/Freshness/WatchEventRouter.cs:36` (`Route`), `:80-81` (`RouteExisting` → `UpdateOp`) |
| 5 | Ops executed one at a time, in routed order, under one gate | `src/Miller.Server/Hosting/IndexerCore.cs:376` (`ExecuteIsolated`), dispatch `UpdateOp u => _ops.Update(u.Path)` at `:382` |
| 6 | Path canonicalized, then handed to the runner | `src/Miller.Server/Hosting/JulieExtractOps.cs:83-88` |
| 7 | argv built | `src/Miller.Indexing/JulieExtractRunner.cs:696` (`Update`) → `:315` (`BuildUpdateArgs`) → `:340-347` (`BuildFileOpArgs`) |
| 8 | Invariant ignore file attached when a scan already wrote it | `src/Miller.Indexing/ScanIgnorePolicy.cs:96` (`ForFileUpdate`) |

**The exact argv Miller issues per changed file** (`JulieExtractRunner.cs:346`, plus the
`--ignore-file` pairs appended at `:319-324`):

```
update --root <ABS_ROOT> --db <ABS_DB> --file <ABS_CANON_FILE> --strict-schema --json [--ignore-file <ABS_PATH>]...
```

One `UpdateOp` is emitted per changed path (`WatchEventRouter.cs:80-81`) and each runs as its own
subprocess (`IndexerCore.cs:376-386`). There is no batching of several changed files into one
extract call.

### 1.2 Which julie branch that argv takes

| # | Step | Citation |
|---|---|---|
| 1 | `update` verb runs the resolution hook — it is not a no-op path | `crates/julie-extract-cli/src/commands.rs:842-849` — `write_update_with_resolution(…, \|tx, scope\| resolve_workspace(tx, scope))` |
| 2 | `write_update_with_resolution` delegates straight to `write_files` | `crates/julie-extract-artifact/src/writer.rs:558`, body at `:571-572` |
| 3 | `write_files` builds the scope | `crates/julie-extract-artifact/src/writer.rs:721` |
| 4 | `touched_symbol_names` = OLD names of the rewritten file ∪ NEW names in the incoming file | `writer.rs:800` (`collect_existing_symbol_names`), `:801-803` (extend with new symbol names) |
| 5 | **Scope is hard-coded `is_full_scan: false, whole_corpus: false`** | `writer.rs:850-855` (`is_full_scan: false` at `:853`) |
| 6 | Hook dispatch: `requested_full = scope.is_full_scan \|\| prior.is_none()` → **false** | `crates/julie-extract-cli/src/resolution.rs:1644` |
| 7 | Delta branch computes the widened scope | `resolution.rs:1664` → `delta_scope_files` at `:2922` |
| 8 | Crossover check against 0.7 | `resolution.rs:1665` → `delta_scope_crosses_over` at `:2681`, constant at `:2674` |
| 9 | Scoped pass runs | `resolution.rs:1847` (`resolve_delta`) |

**What `structure_changed` evaluates to on a pure single-file rewrite through Miller's argv:
it is never evaluated.** `structure_changed` lives at `writer.rs:1416`, inside
`write_scan_spooled_snapshot_in_mode` (`writer.rs:1178`), which is reachable only from the `scan`
verb. Miller's `update --file` reaches `write_files` (`writer.rs:721`), which sets
`is_full_scan: false` unconditionally (`writer.rs:853`). The effective value on this path is a
constant `false`, arrived at by a different code path than the one Ph0 cited.

This is a correction to the lead's framing: the watcher does **not** take the whole-repo delta
path. It takes the single-file delta path — and, as measured below, **pays exactly the same
resolution cost.**

### 1.3 VERDICT — measured

**YES. One save on an identifier-dense repo re-derives the widened scope today. Miller's shipped
incremental converge already pays near-full resolution per save, and the cost is worse than Ph0's
headline figure.**

Method: fixture = `git archive` of this repo at `0ec78eec` (Ph0's fixture commit) into `$TMPDIR`,
base artifact built with Ph0's argv `scan --root <fixture> --db <db> --jobs 4 --json`; then, per
row, clone the base artifact (`cp -c`), append one newline to one indexed file, run one command,
restore. Base artifact: **1,420 files, 380,720 identifiers, 122,778 symbols.** Pass discriminator
is Ph0's: `languages.reference_resolution.by_language` null ⟹ scoped Delta branch
(`resolution.rs:1707` computes the workspace aggregate only on a whole-workspace pass). Cost axis
is `reference_resolution.counts.identifier_resolutions` — deterministic, load-invariant.

| Run | Verb / argv | changed file | pass | resolutions re-derived | share of corpus | wall |
|---|---|---|---|---:|---:|---:|
| **A** (Ph0 replication) | `scan --root --db --jobs 4 --json` | `eval/fusion-arm/Fuser.cs` | Delta | **283,806** | **74.5%** | 12,248 ms |
| **B** (**Miller's real argv**) | `update --root --db --file --strict-schema --json` | *same file* | Delta | **283,806** | **74.5%** | 11,687 ms |
| C (control) | `update …` | `.agents/skills/handoff-in/SKILL.md` | Delta | 8,748 | 2.3% | 3,607 ms |
| D | `update …` | `src/Miller.Server/Tools/SearchTool.cs` | Delta | **353,095** | **92.7%** | 18,113 ms |
| E | `update …` | `src/Miller.Indexing/JulieExtractRunner.cs` | Delta | **343,980** | **90.3%** | 16,021 ms |

Row A reproduces Ph0's `delta_cs_1` result to the row (`283,806`, 74.5% — Ph0 `results.md:44`),
confirming the fixture and method match.

**Row B is the finding.** Miller's actual watcher argv, on the same file and the same base
artifact, re-derives an **identical 283,806 resolution rows**. `pending_resolutions` is also
identical (36 in both). The single-file verb and the whole-repo delta verb do the same resolution
work, because both land on `resolve_delta` with the same `touched_symbol_names` seed.

Run A's phase profile attributes **11,099 ms of its 11,984 ms to `artifact_write_resolution`**
(92.6%). Single-file `update` reports omit `profile`, but its identical row count and comparable
wall clock place the cost in the same phase.

**Ph0's 74.5% is not the typical case — it is near the optimistic end.** `Fuser.cs` carries only
47 distinct symbol names. Real Miller source files carry many more, and the scope saturates:

Widened-scope share of corpus identifiers, **120 randomly sampled `.cs` files** (seed 7), computed
from the base artifact by replaying the `delta_scope_files` name-union rule.
**Evidence status (pre-merge review correction, 2026-08-07):** the 120-file sampling SQL and its
per-file output were NOT committed and did not survive their temp paths, so the distribution
below is an **unverified observation** — directionally supported, but not reproducible from this
repo. What IS committed (recovered to `probes/`): the update-path timing instruments
(`probes/probe.py`, `probes/probe2.py`) and their machine-readable outputs (`probes/out/*.json`),
which independently measure two named files end to end — `SearchTool.cs` 18,113 ms at **92.7%**
of corpus identifiers, `JulieExtractRunner.cs` 16,021 ms at **90.3%** (`probes/out/results2.json`)
— plus the A/B/C single-file scan/update baselines. The 16–18 s typical-save claim rests on those
committed measurements; the exact percentile table rests on the uncommitted sample. The §16.3
crossover re-denomination work re-measures the distribution with `resolution_perf.rs` regardless.

| min | p10 | p25 | **median** | p75 | p90 | max | mean |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 13.7% | 48.4% | 72.9% | **87.3%** | 94.1% | 97.3% | 99.6% | 79.4% |

- **78% of single-file saves (94/120) widen past 70% of the corpus.**
- 20/120 widen past 95%.
- Ph0's probe file sits at the **36th percentile**.

So the shipped per-save cost on this 1,420-file repo is **~16–18 s of resolution for a typical
source file**, not the 12 s Ph0's headline suggests. Every save. On the leader, serialized
(`IndexerCore.cs:376-386` runs ops one at a time), every 250 ms tick.

The same machinery is on Miller's **write-through edit path**: `miller edit apply=true` calls
`IExtractOps.Update` per file when the process is the leader (`docs/m6-design.md:131`), so an
agent-driven edit pays this too.

### 1.4 Why it saturates — the two arms, decomposed

`resolve_delta` reads **two** worklist arms and unions them:

- **Name arm** — `worklist_*_by_names(tx, &names)`, matching bare identifier names workspace-wide:
  `resolution.rs:1882`, `:1906`, `:1927`, `:1951`. Backing SQL is `i.name IN (…)`
  (`crates/julie-extract-artifact/src/resolution_store.rs:894-914`) and
  `pr.target_terminal_name IN (…) OR pr.target_receiver IN (…)` (`resolution_store.rs:752-776`).
- **File arm** — `worklist_*_in_files(tx, &scoped_files)` over the widened file set from
  `delta_scope_files`: `resolution.rs:1883`, `:1907`, `:1934`, `:1955`.

Measured decomposition for the 47 names of `Fuser.cs` against the base artifact:

| arm | quantity | share |
|---|---:|---:|
| Name arm — identifier rows matching those 47 names | 6,051 | **1.6%** |
| …but those rows are spread across | 383 files | 27.0% |
| File arm — **all** identifiers living in those 383 files | **305,151** | **80.2%** |
| *measured actual re-derived* | *283,806* | *74.5%* |

**The name arm is not the cost. The file arm is.** Matching 47 bare names selects only 1.6% of
identifier rows, but it seeds 27% of files — and those files are ~3× denser than average, holding
80% of all identifiers. The measured 283,806 sits just under the 305,151 ceiling because
individual worklists apply further filters (e.g. never-attempted excludes rows already carrying an
overlay row, `resolution_store.rs:906`).

Top name fan-out is dominated by generic locals: `row` (1,906 identifier rows), `query` (681),
`doc` (618), `candidate` (484), `plan` (358). **39 of the 47 names are `kind='variable'`.**

### 1.5 The crossover measures the wrong quantity — never fires on a save

`delta_scope_crosses_over` (`resolution.rs:2681-2694`) compares the widened scope's **file count**
against `COUNT(*) FROM files × 0.7` (`resolution.rs:2691-2693`). On this corpus the threshold is
1,420 × 0.7 = **994 files**.

Widened-scope **file count** over the same 120 sampled saves:

| min | p25 | median | p75 | max |
|---:|---:|---:|---:|---:|
| 30 (2.1%) | 348 (24.5%) | **506 (35.6%)** | 618 (43.5%) | 778 (54.8%) |

**0 of 120 single-file saves cross over.** Every one stays on the scoped Delta path.

That is the wrong outcome. The median save's scope is 35.6% of *files* but 87.3% of *identifiers*,
because the name unions preferentially select large files. The guard is denominated in files while
the cost is denominated in identifier rows, so it systematically under-fires — and Ph0 measured the
scoped path as the **slower** one at high coverage (26.0 s scoped @ 99.7% vs 11.6 s Full @ 99.3%,
Ph0 `results.md:48-50`). Miller's saves are parked on the losing side of a guard that never trips.

This is a **new finding**, not on the Ph0 gate's queued list. See §2.1.

---

## 2. Section B — Ph2 fix surfaces

### 2.1 Symbol-name scope widening

**Current shape.** `delta_scope_files` (`resolution.rs:2922-2954`) seeds `files` with
`scope.changed_file_ids` (`:2934`), then unions in, keyed on `touched_symbol_names`:

| union | citation |
|---|---|
| files of resolved pending rows matching a touched name | `resolution.rs:2935-2937` |
| files of unresolved pending rows matching a touched name | `resolution.rs:2938-2940` |
| files of resolved identifiers matching a touched name | `resolution.rs:2941-2943` |
| files of never-attempted identifiers matching a touched name | `resolution.rs:2944-2946` |
| files declaring a type whose `resolved_type` is a touched name (tier 3) | `resolution.rs:2947` → `:488-500` |
| files importing a touched name under local **or** imported name (tier 2) | `resolution.rs:2948` → `:507-520` |
| files whose module candidates bind to a structurally changed path | `resolution.rs:2949-2951` → `:531-545` |

**What a sound narrowing looks like — and what does not work.**

*Tested and rejected: kind-based name filtering.* A local variable can only ever be a resolution
target inside its own file: tier 1 walks the caller's scope chain then file top-level
(`resolution.rs:1062-1093` — `children_named` / `top_level_named(&edge.file_id, …)`, both
same-file), and tier 4 (the only workspace-wide bare-name tier) returns `&[]` for both
`VariableRef` and `MemberAccess` (`resolution.rs:1465-1476`) and never admits
`SymbolKind::Variable` for any reference kind. So dropping `kind='variable'` names from the
cross-file unions is **sound**. It is also **not enough**:

| changed file | distinct symbol names | scope today | scope without `variable` names | gain |
|---|---:|---:|---:|---:|
| `eval/fusion-arm/Fuser.cs` | 47 | 383 f / 80.2% | 131 f / 30.4% | **2.6×** |
| `src/Miller.Server/Tools/SearchTool.cs` | 598 | 776 f / 99.5% | 672 f / 89.2% | 1.1× |
| `tests/…/CliDispatchTests.cs` | 540 | 778 f / 99.6% | 677 f / 89.8% | 1.1× |
| `src/Miller.Server/Cli/CliDispatch.cs` | 409 | 784 f / 99.6% | 667 f / 89.0% | 1.1× |
| `src/Miller.Server/Tools/TraceTool.cs` | 362 | 764 f / 99.2% | 660 f / 89.0% | 1.1× |

The win exists only for atypically small files. At 300–600 symbol names — the normal case for a
real source file — **any** bare-name file-set union saturates the corpus regardless of filtering.
Ph2 should not spend on kind filtering as the primary fix and should not expect it to move the
median.

**Smallest sound change, in priority order:**

1. **Re-denominate the crossover in identifier rows, not files** (`resolution.rs:2681-2694`).
   Replace `COUNT(*) FROM files` with the identifier-row count of the scope versus the corpus.
   This is a ~5-line change to one function plus its constant's doc comment
   (`resolution.rs:2660-2674`). It does not change which rows are correct — it only picks the
   cheaper of two paths that are already contracted to agree. On the measured distribution it
   flips the median save (87.3% of identifiers) from the 26 s scoped path to the ~11 s Full path.
   **Blast radius:** `resolution.rs` only; no schema, no artifact contract, no Miller change.
   Re-measures `DELTA_SCOPE_CROSSOVER` — the perf sweep that sets the constant
   (`crates/julie-extract-cli/tests/resolution_perf.rs:1048`, and `:52` which imports it as
   `CROSSOVER_THRESHOLD`) must be re-run because its x-axis changes meaning.
2. **Narrow the seed to cross-file-referenceable names** — exclude `SymbolKind::Variable` (and
   anything else unreachable at tiers 2–4) from the *cross-file* unions while keeping the changed
   file itself in scope via `changed_file_ids` (`resolution.rs:2934`). Sound per the tier analysis
   above; worth ~2.6× on small files, ~1.1× on typical ones. **Blast radius:** `delta_scope_files`
   and the four `*_by_names` calls; requires `load_candidate_symbols` kind data
   (`resolution.rs:2336-2385`) to reach the scope computation, which today only receives names.
3. **Row-level rather than file-level scoping** — the structural fix. The file arm exists because
   an edge can key on a name the reference row does not carry (an aliased import's
   `imported_name`, a receiver's resolved type — `resolution.rs:1922-1926`). Scoping to *rows*
   reachable by those relations rather than to *whole files* removes the density amplifier. This
   is a redesign, not a patch, and it is the only option that reaches delta-sized cost.

**Which tests gate it (all three options):**

| gate | citation |
|---|---|
| Delta-vs-Full equivalence — the decisive correctness gate: clone, wipe overlay, re-resolve at full scope, assert identical | `crates/julie-extract-cli/tests/resolution_scope_equivalence.rs:166` (`assert_matches_full_rederivation`), comparison helper at `:88` |
| the four named delta-hazard cases (aliased import, receiver ambiguity, module shadowing, restored uniqueness) | `resolution_scope_equivalence.rs:221`, `:241`, `:264`, `:281` |
| crossover sweep that SETS the constant | `crates/julie-extract-cli/tests/resolution_perf.rs:1048`; scope fixtures at `:319`, `:428`, `:1015` |
| writer scope contract — `write_update`/`delete_file` are Delta, `write_scan` is Full | `crates/julie-extract-artifact/tests/writer_contract.rs:2354`, `:2370`, `:2260`, `:3245` |
| determinism of the resolution overlay across two identical scans | `crates/julie-extract-cli/tests/operations_contract.rs:2809` |
| report scope shape | `crates/julie-extract-cli/tests/resolution_report_scope.rs:50`, `:59` |

### 2.2 Bulk-path eligibility

**`artifact_is_unwritten`'s exact conditions** (`crates/julie-extract-artifact/src/writer.rs:1671-1679`):

```sql
SELECT EXISTS (SELECT 1 FROM files) OR EXISTS (SELECT 1 FROM extraction_revisions)
```
returned negated. So eligibility requires **zero `files` rows AND zero `extraction_revisions`
rows**. The doc comment states the reason (`writer.rs:1666-1670`): an artifact whose files were all
deleted by a later scan is still a live served artifact, so files-empty alone is unsafe.

**Lifecycle.** Evaluated once, at `open_path` (`writer.rs:314`), cached on the writer.
`take_bulk_load_eligibility` (`writer.rs:335-338`) is a `mem::replace` — it
fires **at most once per opened artifact**. It is *spent without being used* by
`remove_file_rows` (`writer.rs:641`) and `write_files` (`writer.rs:737`), and *consumed* by
`write_scan_snapshot` (`writer.rs:898`).

**What eligibility buys** (measured by Ph0 at 71,500 rows/s vs 20,100 rows/s — 3.6×,
`results.md:82-88`):

| effect | citation |
|---|---|
| `journal_mode=MEMORY`, `synchronous=OFF` | `writer.rs:1692` (`begin_bulk_load`), pragmas at `:1693-1694`, rationale `:1680-1691` |
| secondary indexes dropped for the insert passes, rebuilt once at the end | `writer.rs:982`, `:1113` (`create_secondary_indexes`); drop helper `crates/julie-extract-artifact/src/schema.rs:46` |
| foreign-key enforcement off during inserts, whole-artifact check once before commit | `writer.rs:1715` (`verify_foreign_keys`), rationale `schema.rs:41-45` |
| durable journal restored after commit; failure poisons the writer | `writer.rs:390-401`, `:1742-1748` |

**Why a bound view's artifact is permanently ineligible.** A view binds against a *populated*
base — non-zero `files` and non-zero `extraction_revisions` by construction — so
`artifact_is_unwritten` is false at open and bulk load can never fire. Ph0 measured the
consequence: a 74.5% delta costs more wall clock than a 100% from-scratch build
(`results.md:90-91`). The doc comment naming the hazard is at `writer.rs:1667-1670`.

**What makes a fresh-output resolution pass bulk-eligible by construction in the store model.**
The predicate is scoped to **the whole connection**, not to the write's target. In the v4 store
model a version's resolution output is a *new* relation — new overlay tables (or a new per-version
table set) with no prior revisions of their own — even though the connection also holds a
populated shared base. So the smallest sound change is:

> Make bulk-load eligibility a property of the **write target**, not of the artifact. Replace the
> connection-wide `artifact_is_unwritten` probe with a predicate over the specific relations the
> write will fill, and gate the unsafe pragmas on "every relation this write touches is empty and
> has no committed history".

**Blast radius — the honest part.** This is *not* a one-line predicate swap, because two of the
three bulk effects are connection-global and would corrupt the populated base if applied
unchanged:

- `journal_mode=MEMORY` / `synchronous=OFF` (`writer.rs:1693-1694`) are **connection-wide
  pragmas**. Applying them while a populated base shares the connection trades away durability for
  data that is *not* disposable — exactly the hazard `writer.rs:1667-1670` and `:1682-1688` exist
  to prevent. Sound only if the fresh output lives in a **separate database file** (attached, or
  written by its own writer/connection) that promote-not-merge discards on a torn write.
- `drop_secondary_indexes` (`schema.rs:46`) reads `sqlite_master` and drops **every** non-implicit
  index in the database. Against a populated base this destroys the base's indexes and forces a
  full rebuild of all of them. Sound only if scoped to the new relations' indexes — which the
  helper's design comment (`schema.rs:38-45`) deliberately avoids, precisely so a new index cannot
  be silently missed.
- `verify_foreign_keys` (`writer.rs:1715`) is a whole-database `foreign_key_check`. Correct but
  now O(base), not O(output) — it becomes a per-bind cost rather than a once-per-build cost.

So the store-model answer is: **fresh-output bulk eligibility is achievable by construction only
if a version's resolution output is written to its own database file.** If the store instead
writes per-version tables into the shared artifact, the pragma-level wins (the dominant term) are
unavailable and only the deferred-index win is scopeable. Ph2 should treat "separate output file"
as a *precondition* of the bulk win, not as an independent design choice — and the v4 contract
should say which it picked.

### 2.3 Is a resolution-only verb feasible? — three-state answer

**State: FEASIBLE, but not reachable today without new plumbing.**

**Does one exist?** No. The verb set is `Scan | Update | Delete | Info | Export | Languages |
Rebind` (`crates/julie-extract-cli/src/args.rs:17-25`). There is no `resolve`.

**What it needs as inputs — all already present:**

- `resolve_workspace(tx: &Transaction, scope: &ResolutionScopeInput)` is **already public** and
  takes nothing but a transaction and a scope (`crates/julie-extract-cli/src/resolution.rs:1541-1546`).
  It performs no extraction, no discovery, no file I/O — it reads `symbols`, `identifiers`,
  `pending_relationships`, `type_facts` and imports from the connection (`resolution.rs:2326-2334`,
  `load_index`) and writes the overlay tables.
- `ResolutionScopeInput` is a plain public struct of two collections and two bools
  (`crates/julie-extract-artifact/src/writer.rs:182-187`) — constructible without a writer.
- `finalize_resolution_metadata` already runs **outside** the write transaction on the committed
  connection (`resolution.rs:1572-1580`, and its doc at `:1564-1571`), so the durable-metadata half
  is already decoupled from the writer.
- `resolve_workspace_with_crossover` (`resolution.rs:1556`) shows the entry point is already
  exercised standalone by the perf sweep.

**What blocks it:**

1. **Every path into the hook goes through a writer that also extracts.** The hook is a closure
   parameter of `write_scan_*_with_resolution` / `write_update_with_resolution` /
   `delete_file_with_resolution` (`writer.rs:453`, `:479`, `:520`, `:558`, `:583`, `:608`), each of
   which demands `&[ArtifactFile]` or an `ArtifactFile` — i.e. the product of a discovery +
   extraction pass. There is no writer method that opens a transaction and calls only the hook.
2. **Revision bookkeeping is entangled with the write.** `write_files` inserts a revision
   (`writer.rs:790`) and revision-file-change rows before the hook runs. A resolution-only pass
   produces no file changes, so it must either create a revision with zero file changes (and every
   consumer keying on `revision_file_changes` must tolerate that — Miller's search-sidecar
   convergence does, per `CLAUDE.md` "Search sidecar", but that is an assumption to verify) or skip
   revision creation and leave `current_revision(tx)` (`resolution.rs:1638`) reading the prior
   revision, which the overlay rows are stamped against.
3. **Freshness/identity semantics are unspecified for a no-extraction write.** The workspace-wide
   language aggregate is computed only on a whole-workspace pass (`resolution.rs:1707`), and
   `corpus_current = effective_full || scope.whole_corpus` (`resolution.rs:1718`) is what stamps
   `last_full_revision` — the discriminator Ph0 relied on (`results.md:22-26`). A resolve-only pass
   hash-checked nothing, so it cannot honestly claim `whole_corpus: true` — yet a Full-scope
   resolve *does* re-derive the whole workspace. The two booleans were split for exactly this
   reason (`writer.rs:172-180`); the verb needs an explicit
   answer for the combination `is_full_scan: true, whole_corpus: false`, which nothing produces
   today.
4. **No report shape.** Every verb returns a typed report (`ReportOperation` variants, e.g.
   `commands.rs:826`); a resolve verb needs its own operation/mode pair and Miller needs a matching
   exit-code contract in `JulieExtractRunner.Interpret`.

**What the v4 contract should require of it:**

- **Verb shape:** `resolve --db <ABS_DB> [--root <ABS_ROOT>] --strict-schema --json`, with an
  explicit scope selector (`--full`, or a scope expressed as changed-file ids / touched names) so
  the caller states the scope rather than inheriting one from a write.
- **It must extract nothing** — no discovery, no file reads, no `--file`. Its inputs are the
  artifact's existing base tables only. That is what makes it composable with the store model, and
  it must be contracted so a future change cannot quietly add a read of the working tree.
- **Freshness honesty:** the verb must NOT set `whole_corpus`/`corpus_current`, because it
  hash-checked nothing. It may set `last_full_revision` only when it ran Full scope AND the
  artifact's files were already current at that revision — otherwise the contract must define a
  third state ("resolution current as of revision N, corpus currency unchanged"). The v4 contract
  should name this state rather than let it be inferred.
- **Idempotence + equivalence:** running `resolve --full` twice must be a no-op on the second run,
  and its output must be byte-identical to what a full `scan` of the same tree would have produced
  in the overlay tables — the existing
  `resolution_scope_equivalence.rs:166` helper is the right gate, promoted to a contract test.
- **Bulk eligibility:** per §2.2, if the store expects the bulk path for a fresh-output resolve,
  the contract must require the output to be a separate database file.

**Recommendation:** this verb is the smallest julie-side change that would let a store version bind
resolution *without* re-extracting a tree it has not changed — it is worth Ph2 spend. But it does
not by itself fix §1: the cost is in `resolve_delta`'s widened scope, and a resolve-only verb that
re-derives 87% of rows is just the same bill with fewer subprocesses.

---

## 3. Section C — `metadata_json` determinism

### 3.1 The seven sites — confirmed, all at the Ph0 line numbers

`crates/julie-extractors/src/base/types.rs`, each declared
`pub metadata: Option<HashMap<String, serde_json::Value>>` behind
`#[serde(skip_serializing_if = "Option::is_none")]`:

| line | struct | struct decl |
|---:|---|---:|
| 78 | `SourceRegion` | `:65` |
| 124 | `StructuralFact` | `:108` |
| 178 | `ComplexityMetric` | `:158` |
| 294 | `Symbol` | `:251` |
| 449 | `Relationship` | `:425` |
| 487 | `TypeInfo` | `:469` |
| 496 | `SymbolOptions` | `:492` |

`HashMap` is `std::collections::HashMap` (`types.rs:8`). Six are row types; `SymbolOptions` is a
builder input that feeds `Symbol.metadata`.

### 3.2 The defect — proven, not inferred

**Serialization boundary:** `optional_json` → `json_string` →
`serde_json::to_string(value)` (`crates/julie-extract-cli/src/extraction.rs:947-960`), called at
`extraction.rs:348` (symbols), `:614` (relationships), `:738` (type_facts), `:842` (source_regions),
`:868` (structural_facts), `:898` (complexity_metrics). `serde_json` is declared without the
`preserve_order` feature in all three crates (`crates/julie-extract-cli/Cargo.toml:59`,
`crates/julie-extract-artifact/Cargo.toml:23`, `crates/julie-extractors/Cargo.toml:73`), so nested
`serde_json::Value::Object`s are `BTreeMap`-backed and already sorted. **The only unordered
container at this boundary is the top-level `HashMap`** — `Serialize for HashMap` emits iteration
order, and Rust's `RandomState` reseeds per process.

**Empirical proof.** Three separate `julie-extract scan` processes over one identical tiny fixture
(3 files) into three separate artifacts, comparing `metadata_json` per primary key:

| table | rows with metadata | differing run1 vs run2 | run1 vs run3 |
|---|---:|---:|---:|
| `symbols` | 118 | **61** | 67 |
| `structural_facts` | 63 | **58** | 60 |
| `source_regions` | 8 | **4** | 5 |
| `relationships` | 1 | 0 | 0 |

Example (`symbols`):
```
run1: {"role":"parameter","variableType":"int","isInferred":false}
run2: {"variableType":"int","role":"parameter","isInferred":false}
```
Same keys, same values, different byte string. On the full 1,420-file artifact the exposure is
~121k order-sensitive rows: `symbols` 59,926 multi-key of 122,771 non-null; `structural_facts`
60,234 of 60,234; `source_regions` 726 of 802; `relationships` 318 of 464.
(`type_facts` and `pending_relationships` carry no `metadata_json`; `complexity_metrics` has 13,100
non-null but 0 multi-key, so it is stable only by accident of arity.)

Note this is invisible to the existing determinism gate
(`operations_contract.rs:2809`), which compares only the **resolution overlay** tables — none of
which carry `metadata_json`. The defect has been shipping unobserved.

### 3.3 Fix shape

Change the seven declarations from `HashMap` to `BTreeMap<String, serde_json::Value>` and update
the `types.rs:8` import. `Serialize for BTreeMap` emits sorted key order, which is deterministic
across processes and machines.

**Blast radius:** every construction site of these seven `metadata` fields across
`crates/julie-extractors/src/*/` (all language extractors) — mechanical, since `BTreeMap` and
`HashMap` share `new`/`insert`/`get`/`extend`/`FromIterator`. Two real constraints:

- `metadata_flag` (`crates/julie-extract-cli/src/extraction.rs:962-968`) takes
  `&Option<std::collections::HashMap<String, Value>>` explicitly — its signature must change with
  the types.
- `serde_json::Value` is not `Ord`, but `String` is, so `BTreeMap<String, Value>` is fine; any
  site relying on `HashMap`'s `Entry` API ports directly.
- Values that are themselves objects are already sorted (no `preserve_order`), so the fix is
  complete at the top level — no recursive normalization needed.

This changes `metadata_json` bytes for existing artifacts, so it is an **artifact-content change**
even though the schema is untouched. Miller compares `files.content_hash`, not row bytes, so it
does not invalidate Miller freshness — but any consumer diffing artifacts byte-wise across the
version boundary will see one-time churn. Worth a note in the julie release notes.

### 3.4 The byte-stability gate julie needs

Model it on the existing determinism test, but over the row tables rather than the overlay:

> **`two_identical_scans_produce_byte_identical_metadata_json`** — scan one fixture into two fresh
> artifacts **in two separate processes** (separate processes are load-bearing: `RandomState`
> reseeds per process, so a same-process double scan can pass while the defect is live), then
> assert equality of `(primary_key, metadata_json)` for every row of `symbols`,
> `structural_facts`, `source_regions`, `relationships`, `type_facts`, `complexity_metrics`. Assert
> the compared set is non-empty **and contains at least one multi-key object**, mirroring the
> non-empty guard at `operations_contract.rs:2822-2825` — without the multi-key assertion the test
> passes vacuously on a fixture whose metadata all has arity ≤ 1.

The fixture must be multi-language: `structural_facts` and `source_regions` metadata come from
markdown/JSON/embedded-language facts, `symbols` metadata from typed languages. A C#-only fixture
would miss 60k of the 121k exposed rows. This is the language-parity rule from Miller's `CLAUDE.md`
applied to the gate.

Place it beside `operations_contract.rs:2809` so the two determinism claims sit together.

---

## 4. Handoff — what Ph2 and the v4 contract should carry

1. **Report the shipped cost to the user.** Miller's watcher pays 16–18 s of near-full resolution
   per save on a 1,420-file C# repo, on the leader, serialized. Ph0's 74.5% understates it; the
   median is 87.3%. This is a today-problem, independent of the store program.
2. **The crossover fix (§2.1 option 1) is small, sound, and helps immediately** — it is the only
   item here that improves shipped Miller behaviour without a redesign, and it is confined to one
   julie function.
3. **Do not budget kind-based name filtering as the scope fix** (§2.1) — measured 1.1× on typical
   files.
4. **Bulk eligibility for populated artifacts requires a separate output database file** (§2.2);
   the v4 contract must state whether the store takes that shape.
5. **A resolution-only verb is feasible and worth building, but is not a cost fix** (§2.3).
6. **`metadata_json` non-determinism is real and shipping** (§3), invisible to current gates.

---

## 5. Verification ledger

| field | value |
|---|---|
| **Assigned scope** | Evidence audit — every claim carries a current `path:line`; Ph0 anchors re-verified; no code changes |
| **Miller tree** | `/Users/murphy/source/miller/.claude/worktrees/index-store-ph1`, branch `worktree-index-store-ph1`, HEAD `1eee221c` (descendant of the briefed `662dcfbe`, verified with `git merge-base --is-ancestor`) |
| **Miller dirty state** | `?? spike/index-store-ph1/` only — this file plus Task 1's sibling `binding-proof/` outputs. No tracked file modified. |
| **julie-extractors tree** | `/Users/murphy/source/julie-extractors`, `main` @ `ab7b16a`, clean, `## main...origin/main`. READ-ONLY: no writes, no builds, no cargo invocations. |
| **julie-extract binary** | `2.27.0`, `/Users/murphy/source/miller/.tools/julie-extract`; matches `scripts/julie-pins.json:2`. This worktree has no `.tools/`. |
| **Anchors re-verified** | 6 of 6 checked; 2 moved (crate path), 1 line shifted by 1; all recorded in §0 |
| **Commands run** | Read-only source reads; `julie-extract scan`/`update` against scratch artifacts in `$TMPDIR` only; read-only SQLite queries against those scratch artifacts |
| **NOT run (out of assigned scope)** | No `dotnet build`/`dotnet test`, no Miller test suite, no `cargo` build/test/clippy, no writes to either source tree |
| **Measurement caveats** | Wall-clock figures are ±15% (other Ph1 workers were live on the box); row counts are deterministic and load-invariant and are the load-bearing axis. Percentile tables are computed by replaying the `delta_scope_files` name-union rule in SQL over the base artifact, not by running 120 extracts — they predict scope, and the 5 rows that WERE extracted (A–E) agree with the prediction. |
| **Scratch** | `$TMPDIR/miller-ph1-task2/`, `$TMPDIR/ph1-t2-det/`; probe scripts under `/tmp/ph1-task2/` (outside the repo — no code committed, per file-ownership) |
| **Files created** | `spike/index-store-ph1/julie-path-audit/results.md` (this file) — nothing else |
| **Commit** | none — parallel-lead-commit |
