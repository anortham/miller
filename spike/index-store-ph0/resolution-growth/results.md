# Ph0 Task 5 — resolution binding cost + store growth model

**Instrument:** `./run.sh` (binding curve → growth model). Raw evidence in `output/`:
`binding-results.json`, `growth-results.json`, and julie-extract's own scan reports under
`output/reports/`. Scratch lives in `$TMPDIR` and the entry script removes it on exit.

**Setup**

| | |
|---|---|
| julie-extract | **2.27.0** (this worktree's pinned `.tools/julie-extract`) |
| argv | `julie-extract scan --root <fixture> --db <scratch db> --jobs 4 --json` |
| Fixture | `git archive` of this repo at `0ec78eec`, extracted to `$TMPDIR` — **1,420 indexed files**, 122,778 symbols, 380,720 identifiers, 732 MiB artifact |
| Box | Apple Silicon, macOS/APFS, `--jobs 4`, **other Ph0 workers running concurrently** |
| Prior curve | [`docs/findings/2026-08-05-rebind-p1-cost-model.md`](../../../docs/findings/2026-08-05-rebind-p1-cost-model.md) — read first; its method (clone base artifact, modify N files, whole-repo scan, read `profile.phases`) is re-run here on 2.27.0 |

**Two method corrections vs. the naive instrument**, both load-bearing:

1. **Full-vs-Delta discriminator.** `artifact_metadata.reference_resolution_last_full_revision`
   *cannot* tell the two apart on a whole-repo scan: every whole-repo write sets
   `whole_corpus: true`, which makes `corpus_current` true and stamps the current revision even on
   a scoped pass (`julie-extract-cli/src/resolution.rs:1718`, `:1733`). The instrument's first
   draft used it and reported "Full" for every row — wrong. The real discriminator is
   `languages.reference_resolution.by_language` in the scan report: julie computes that
   workspace-wide aggregate **only** on a pass that re-derived the whole workspace
   (`resolution.rs:1707`), so a null means the scoped Delta branch ran.
2. **`reference_resolution.counts.identifier_resolutions` is the honest cost axis.** It is the
   number of resolution rows the pass re-derived, it is **deterministic** (byte-identical across
   repeat runs), and it does not move with machine load. Wall clock on this box varied ±15%
   between runs because other Ph0 workers were live; the row counts did not vary at all. Read the
   row counts as the result and the milliseconds as ±15%.

---

## 1. Binding cost curve

Each row: clone the fixed base artifact (`cp -c`), rewrite N indexed files (append a newline —
a pure rewrite, no path added or deleted), run one whole-repo scan, restore the fixture.

| Row | changed | total ms | resolution ms | extract ms | pass | resolutions re-derived | share of corpus | vs full |
|---|---:|---:|---:|---|---|---:|---:|---:|
| **full build, from scratch** (bulk path) | 1,420 | **18,646** | 5,324 | 4,822 | Full | 380,723 | 100.0% | 1.00× |
| delta, 0 changed | 0 | 242 | 0 | 49 | — | 0 | 0.0% | 0.01× |
| **delta, 1 changed** | 1 | 15,562 | 14,147 | 44 | Delta | 283,806 | **74.5%** | 0.83× |
| delta, 1 changed (repeat) | 1 | 14,588 | 12,990 | 47 | Delta | 283,806 | 74.5% | 0.78× |
| **delta, 5 changed** | 5 | 20,055 | 16,783 | 56 | Delta | 349,470 | **91.8%** | 1.08× |
| **delta, 25 changed** | 25 | 22,473 | 15,051 | 193 | Delta | 353,115 | **92.7%** | 1.21× |
| **delta, 120 changed** | 120 | 28,663 | 14,863 | 302 | Delta | 357,257 | **93.8%** | 1.54× |
| delta, 1 changed *markdown* | 1 | 6,320 | 5,873 | 49 | Delta | 8,748 | 2.3% | 0.34× |
| delta, 737 changed (all `.cs`) | 737 | 64,593 | 26,020 | 3,750 | Delta | 379,577 | 99.7% | 3.46× |
| delta, 993 changed (mixed) | 993 | 38,033 | 11,616 | 1,895 | **Full** | 378,129 | 99.3% | 2.04× |
| delta, 994 changed (mixed) | 994 | 38,957 | 10,741 | 1,952 | **Full** | 378,141 | 99.3% | 2.09× |
| full pass, `--force` on the populated artifact | 1,420 | 71,768 | 24,050 | 4,851 | Full | 380,723 | 100.0% | 3.85× |
| delta, 1 rewrite **+ 1 added file** | 2 | 17,907 | 16,507 | 62 | **Full** | 370,649 | 97.4% | 0.96× |

### 1.1 P1a delta-scoped resolution has landed, and it does not deliver a delta-sized cost

The dispatch works: a whole-repo scan of 1–737 changed files takes the **scoped Delta** branch
(`writer.rs:1421` computes `is_full_scan: structure_changed || force`, no longer hard-coded
`true` — the fix the prior cost-model doc named as the prerequisite).

**But the delta scope is widened by symbol name, and on a C# corpus the widening saturates
immediately.** One changed `.cs` file re-derives **283,806 of 380,720 identifier resolutions —
74.5% of the corpus**. Five files reach 91.8%. Twenty-five reach 92.7%. One hundred and twenty
reach 93.8%. The curve is not linear in delta size; it is a step to ~75% at the first file and a
slow crawl after that.

The mechanism is in source: `delta_scope_files` (`resolution.rs:2922`) seeds the scope with the
changed files, then unions in **every file holding an identifier, pending row, type declaration
or import matching any touched symbol name**. A single Miller `.cs` file contributes 47 symbol
names; those names alone appear as identifiers in 383 of 1,420 files, and the pending/type/import
unions carry it the rest of the way.

**Delta cost tracks *widened scope*, not delta size.** The markdown control proves the scoping is
real and the widening is what costs: one changed `.md` file re-derives 2.3% and finishes in 6.3 s.
Markdown declares almost no identifiers, so its name set widens to nearly nothing. Code files are
the case the program cares about, and they are the bad case.

### 1.2 A delta pays ~3× per row, because a populated artifact cannot take the bulk path

| Pass | rows re-derived | resolution ms | rows/second |
|---|---:|---:|---:|
| Full build, from scratch (bulk path) | 380,723 | 5,324 | **71,500** |
| Delta, 1 changed file | 283,806 | 14,147 | **20,100** |
| Full pass on a populated artifact (`--force`) | 380,723 | 24,050 | 15,800 |

This reproduces the prior doc's §3 Cause 2 unchanged on 2.27.0: `artifact_is_unwritten` requires
zero files *and* zero revisions, so only a from-scratch build gets `journal_mode=MEMORY`,
`synchronous=OFF` and the drop-and-rebuild-indexes-once path. A bound view's artifact is populated
by definition and is permanently ineligible. **The 3.6× per-row penalty is the reason a 74.5%
delta costs more wall clock than a 100% from-scratch build.**

### 1.3 Crossover behaviour — confirmed, and it fires on the widened scope

`DELTA_SCOPE_CROSSOVER = 0.7` (`resolution.rs:2674`). `delta_scope_crosses_over` compares the
**widened scope's file count** against `COUNT(*) FROM files × 0.7` = 1,420 × 0.7 = **994 files**.

- 737 raw changed files → widened scope still under 994 → stayed **Delta**, 26.0 s resolution.
- 993 and 994 raw changed files → widened scope ≥ 994 → promoted to **Full**, 11.6 / 10.7 s
  resolution.

The promotion is protective and correct: at ~99% coverage the Full pass resolves in **11.6 s**
where the scoped path at 99.7% coverage takes **26.0 s** — 2.2× worse, exactly the chunked-`IN`
and per-file-bookkeeping overhead the constant's doc comment predicts. Crossover confirmed
working. Note it is never reached by *raw* delta size on a typical branch; it exists to catch the
widened scope.

### 1.4 The structure-change escalation is the case a real view hits

`is_full_scan` is forced true when any path appeared or vanished
(`writer.rs:1417`: `structure_changed = !deleted.is_empty() || planned_files.values().any(|e| e.is_none())`).
Measured: **1 rewritten file + 1 added file → Full pass, 97.4% re-derived, 16.5 s resolution.**

A branch switch almost always adds or deletes at least one file. So the *typical* view bind does
not get the scoped path at all.

### 1.5 The real sibling-branch bind — measured, and it loses to a rebuild

The program's central claim is "a new view binds a sibling base + delta in seconds". Tested
directly on a real merged task branch of this repo (`b0d96b75` → `425f995d`, 10 commits,
47 paths in the diff): build the base artifact at the merge base, `rsync` the tree to the branch
tip, run one whole-repo scan.

| Step | total ms | resolution ms | pass | re-derived | artifact files |
|---|---:|---:|---|---:|---:|
| Build the base view's artifact from scratch | 17,791 | 4,736 | Full | 373,903 | 1,396 |
| **Bind that artifact to the branch tip** | **24,390** | 12,219 | **Full** | 370,857 | 1,408 |
| Build the branch tip from scratch, for comparison | 18,426 | 5,166 | Full | 379,315 | 1,408 |

The bind's own scan report: **28 indexed files changed, 0 deleted, 12 of them new paths**
(1,396 → 1,408 files). Those 12 additions set `structure_changed`, which forced the Full pass;
the Full pass then ran on a populated artifact at ~⅓ the from-scratch row rate.

**Binding is 32.4% slower than throwing the base away and rebuilding the tip** — on a 10-commit,
47-path task branch, which is the median case the program is designed around.

### 1.6 Where a real view sits on the curve

Sibling-branch divergence, measured over every merge in each repo's history (merge-base → branch
tip, indexed files only):

| Repo | merges | median | p90 | max |
|---|---:|---:|---:|---:|
| miller | 26 | **16** files | 77 | 106 |
| julie-extractors | 11 | **28** files | 369 | 976 |

Both medians are far below the 994-file crossover — and both land in the flat 74–93% region of
the curve, where the delta costs as much as a rebuild.

### 1.7 Scaling to dotnet/runtime — inference, flagged as such

Not measured (no clone on this box). The measured baseline
([`2026-08-03-dotnet-runtime-v2231-baseline.md`](../../../docs/findings/2026-08-03-dotnet-runtime-v2231-baseline.md))
gives 41,406 indexed files, **12.86 M identifiers** (33.8× this fixture's 380,720), and a 23.7 min
cold scan of which resolution is the dominant phase. A bind that re-derives 74.5% of identifiers
there is 9.6 M rows. At the *from-scratch* rate that repo achieved it is minutes; at the ~⅓ rate a
populated artifact pays it is worse. **Nothing in the measured data supports "seconds" at 41 k
files**, and the dominant term scales with artifact size, not with delta size — so the gap widens
with scale rather than closing.

---

## 2. Growth model

### 2.1 Method

For each retention window `W`:

```
versions(W) = { (path, blob) in the tree at the newest commit before now-W }
            ∪ { (path, post-image blob) for every A/C/M/R change since now-W }
```

- Walks the analysed tip's full history (`git log --since --no-renames --diff-filter=ACMR --raw
  --no-abbrev`), so commits that arrived on merged branches count; merge commits contribute no
  diff of their own.
- Only paths julie-extract actually indexes are counted. The filter mirrors
  `julie-extract-cli/src/discovery.rs:570-594` — the extension set comes from
  `julie-extract languages --json`, minus `HARD_EXCLUDE_DIRS` (which is why this repo's 825
  `.memories/*.md` files are excluded) and `HARD_EXCLUDE_SUFFIXES`.
- **Calibration:** the filter predicts **1,420** indexed files for this repo at `0ec78eec`; the
  real artifact built from the same tree holds **1,420**. Exact.
- The baseline tree is the floor: a store must hold one version of every indexed file just to
  serve the oldest checkout in the window.
- julie-extractors is read-only throughout (`git -C … log/ls-tree/diff`, and a `git archive` into
  `$TMPDIR` for its bytes probe).

### 2.2 Bytes per version — measured, with the arithmetic

| Corpus | artifact bytes | indexed files | **bytes/version** |
|---|---:|---:|---:|
| miller, full artifact | 767,815,680 | 1,420 | **540,715** |
| miller, `--level symbols` (L1 only) | 211,513,344 | 1,420 | **148,953** = **27.5% of full** |
| miller, `.cs` subset only | 599,617,536 | 737 | **813,592** |
| julie-extractors (Rust), full artifact | 915,947,520 | 1,822 | **502,715** |
| dotnet/runtime (measured 2026-08-03, julie-extract 2.24.0) | 20.41 GiB | 41,406 | **529,300** |

Two facts worth carrying forward: the L1 level is **27.5%** of full artifact bytes (the levels
fold's byte lever, measured), and dotnet/runtime's real 529 KB/version sits close to miller's
541 KB/version — so file-count scaling of *bytes* is sound even though file-count scaling of
*churn* is not.

### 2.3 Growth curves

`overhead ×` = `versions(W) / indexed files at the tip` — the store's size as a multiple of one
plain index.

**miller @ `0ec78eec`** — 1,420 indexed files, one index = 0.72 GiB

| window | commits | baseline versions | new versions | total versions | store GiB | overhead × | overhead × with L1-only history |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 week | 111 | 1,395 | 574 | 1,969 | 0.99 | **1.39×** | **1.11×** |
| 2 weeks | 255 | 1,284 | 1,570 | 2,854 | 1.44 | 2.01× | 1.28× |
| 4 weeks | 601 | 878 | 3,164 | 4,042 | 2.04 | 2.85× | 1.51× |
| 8 weeks | 922 | 580 | 4,996 | 5,576 | 2.81 | 3.93× | 1.81× |

**julie-extractors @ `ab7b16a`** — 1,822 indexed files, one index = 0.85 GiB

| window | commits | baseline versions | new versions | total versions | store GiB | overhead × | overhead × with L1-only history |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 week | 102 | 1,728 | 553 | 2,281 | 1.07 | **1.25×** | **1.07×** |
| 2 weeks | 145 | 1,675 | 1,194 | 2,869 | 1.34 | 1.57× | 1.16× |
| 4 weeks | 272 | 1,473 | 1,850 | 3,323 | 1.56 | 1.82× | 1.23× |
| 8 weeks | 429 | 1,176 | 3,247 | 4,423 | 2.07 | 2.43× | 1.39× |

"L1-only history" prices the live tip's versions at full level and every older retained version at
L1 only: `1 + (overhead − 1) × 0.275`. Worked for miller at 1 week:
`1 + (1.387 − 1) × 0.2754 = 1.107×`. This is what the levels fold buys on the retention axis.

### 2.4 dotnet/runtime projection

Two projections, both shown because they answer different questions.

**(a) Anchored on the measured dotnet/runtime artifact** — apply each repo's measured
versions-per-indexed-file multiplier to the real 20.41 GiB single index:

| window | miller churn: all-full | miller churn: L1-history | julie-extractors churn: all-full | L1-history |
|---|---:|---:|---:|---:|
| 1 week | 28.3 GiB | **22.6 GiB** | 25.6 GiB | **21.8 GiB** |
| 2 weeks | 41.0 GiB | 26.1 GiB | 32.1 GiB | 23.6 GiB |
| 4 weeks | 58.1 GiB | 30.8 GiB | 37.2 GiB | 25.0 GiB |
| 8 weeks | 80.1 GiB | 36.9 GiB | 49.5 GiB | 28.4 GiB |

Arithmetic, miller 1 week: `20.41 GiB × 1.387 = 28.3 GiB` all-full; `20.41 × 1.107 = 22.6 GiB`
with L1-only history.

**(b) The task's file-count scaling to 58,500 files at the C#-only 813,592 B/version** — a
deliberate upper bound, since 58,500 is dotnet/runtime's *scanned* count (41,406 were indexed) and
the C#-only per-version figure is 1.54× the repo's real one:

| window | scale | projected versions | all-full | L1-history | one index |
|---|---:|---:|---:|---:|---:|
| 1 week | ×41.2 | 81,117 | 61.5 GiB | 49.0 GiB | 44.3 GiB |
| 2 weeks | ×41.2 | 117,577 | 89.1 GiB | 56.7 GiB | 44.3 GiB |
| 4 weeks | ×41.2 | 166,519 | 126.2 GiB | 66.9 GiB | 44.3 GiB |
| 8 weeks | ×41.2 | 229,715 | 174.1 GiB | 80.1 GiB | 44.3 GiB |

Arithmetic: `58,500 × 813,592 B = 44.3 GiB` for one index; `× 1.387` for miller's 1-week
multiplier = 61.5 GiB.

Both projections transfer a small repo's *churn multiplier* to a large repo. That is the stated
method and it is the pessimistic direction: a 41 k-file repo's weekly commits touch a similar
absolute number of files, not a proportional one, so its true multiplier is lower. Treat (a) as
the planning number and (b) as a ceiling.

### 2.5 Retention recommendation

**Default: 7 days, with retained non-live versions demoted to L1, plus a byte ceiling that prunes
oldest-first before the window expires.**

Why 7 days:

- The plan's byte budget is **≤ 1.2× a single index for eight views**, before retention is
  counted. Retention has to fit inside that, and at full level it does not fit at *any* window:
  7 days alone costs **1.39× (miller) / 1.25× (julie-extractors)**, which blows the whole budget
  on retention with nothing left for the views.
- With L1-only history, 7 days costs **1.11× / 1.07×** — inside 1.2×, leaving ~0.09–0.13× for the
  eight views' deltas. 14 days costs **1.28× / 1.16×** and already breaches it on the busier
  history. So 7 days is the largest window that is defensible against the stated criterion, and
  the L1 demotion is what makes even that possible.
- 7 days covers the median task branch in both repos (miller merges: 10 commits / 16 indexed
  files; julie-extractors: 28) so the common "come back to a paused branch" case still finds a
  bindable base.
- Document 14 days as the tunable-up setting (1.16–1.28×) and treat anything past 4 weeks
  (1.51–1.82× even with L1 history) as opt-in for a machine with disk to spare.

Two guards the window alone cannot provide:

- **A byte ceiling** (suggested default: prune when the store exceeds 1.25× the current single
  index). The window is a proxy for bytes; a burst of large-file churn breaks the proxy.
- **A per-path version cap.** Git history is a *lower bound* on store versions: the store holds a
  version per indexed content change, and Miller's watcher indexes uncommitted working-tree
  states that never become commits. An agent fleet produces many more distinct versions per day
  than the commit counts above. The window and the byte ceiling are both blind to a single
  hot file churning hundreds of versions.

---

## 3. Limits of this measurement

- **One box, one platform, concurrent load.** Other Ph0 workers ran throughout. Wall clocks varied
  ±15% run to run (delta at 1 file: 12.5 s / 13.3 s / 14.6 s / 15.6 s across four runs). The
  re-derived row counts were identical in every run — conclusions rest on those.
- **One corpus for the curve.** 1,420 files, C#-dominant. The markdown control shows the widening
  is identifier-density-driven, so repos with different density sit elsewhere.
- **dotnet/runtime binding cost is not measured**, only inferred from the existing baseline (§1.7).
- **Growth is modelled from commit history**, which undercounts a real agent fleet's version
  production (§2.5). It also assumes every commit's tree was indexed by some view — true for a
  busy single-worktree user, and an undercount when worktrees diverge.
- The 1 MiB `MAX_SOURCE_FILE_BYTES` discovery cap is not modelled in the growth filter; no tracked
  file in either repo reaches it, and the filter's file-count prediction matched the real artifact
  exactly.
- The crossover was located by its *rule* (`scope ≥ files × 0.7`) plus two rows either side of
  994 raw changed files; no row isolates the widened-scope count directly, because julie does not
  report it.
