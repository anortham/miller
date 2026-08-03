# Progressive indexing levels — P3 scale validation on dotnet/runtime (2026-08-03)

The levels program's decisive measurement: does a `symbols`-level first open make a large repo
servable in minutes instead of the ~24 the full extraction costs, with the full layer converging
behind it? Control:
[`2026-08-03-dotnet-runtime-v2231-baseline.md`](2026-08-03-dotnet-runtime-v2231-baseline.md)
(76.3 min at 2.23.1 → 23.7 min at the shipped 2.24.0 package).

## Setup

- Same box (M2 Ultra class, 64 GiB), same repo (dotnet/runtime @ `a2f953fe266`, 41,406 indexed
  files), same argv as the 2.24.0 production validation (`--jobs 4`, production supervision flags),
  concurrent with four AI review processes (real-world load, slightly pessimistic vs the idle
  baseline).
- Binary: from-source `julie-extract` off the `levels` branch @ `79190eb` (reports 2.24.0; adds
  `--level`).
- Sequence: (1) fresh non-force scan with `--level symbols` — exactly the argv Miller's bootstrap
  emits under progressive policy; (2) SIGKILL a full-level upgrade rebuild mid-extraction; (3)
  full-level force rebuild into `symbols.db.rebuild` to completion + promote — the LevelUpgrade
  shape.

## Headline numbers

| Metric | symbols first-open | full baseline (2.24.0) | delta |
| --- | --- | --- | --- |
| Wall clock | **234 s = 3.9 min** | 1,422 s = 23.7 min | **6.1× faster first serve** |
| Artifact size | 5.47 GiB | 20.41 GiB | 73% smaller |
| Symbols | 2,576,001 | 2,576,001 | identical |
| Files | 41,406 | 41,406 | identical |
| Exit | 1 / `partial` | 1 / `partial` | same 8 known non-UTF-8 files |

The design target was <10 min; the measured first serve is 3.9 min. Phase split: extraction of
all 41,406 files completed at ~110 s; the rest is artifact write. The identifier-resolution phase
— 70% of the pre-2.24.0 baseline and still the dominant full-level cost — does not run at all.

Level contract at scale: `index_level = symbols`; identifiers, identifier_resolutions, literals,
source_regions, structural_facts, type_argument_usages all **0**; relationships (442,123),
reference_sites (2,935,704 — relationship/pending arms), type_facts (1,925,243),
complexity_metrics (622,360) all populated. `RepositoryIndexLoader` loads it with
`IndexLevel == "symbols"`, which is what arms every converging diagnostic.

## Upgrade (LevelUpgrade shape)

Full-level force rebuild into `symbols.db.rebuild` while the symbols artifact stayed the served
file: **1,301 s = 21.7 min**, producing a 20.4 GiB full artifact — `index_level = full`,
identifiers 12,856,606 (baseline-identical), source_regions 2,189,454, symbols unchanged. The
served `symbols.db` answered reads throughout (verified during and after the rebuild); the
promote is a rename, per `FullRebuildPromotion`.

Total convergence 234 + 1,301 ≈ 25.6 min vs 23.7 min for one full scan: **~8% total-work
overhead buys a 6× faster first serve.**

## SIGKILL mid-upgrade

`kill -9` at t+90 s into the upgrade rebuild (extraction_spool phase):

- Served artifact untouched: `PRAGMA integrity_check` ok, `index_level = symbols`, all 2,576,001
  symbols present, identifiers still 0.
- No rebuild debris the next scan cannot reap (the killed scan died pre-writer; its spool file is
  lock-reaped by the next scan per the 2.22.0 supervision contract).
- The upgrade stays owed by construction: `LevelUpgrade` is DERIVED from
  `index_level = symbols` + progressive policy on every leader claim and post-drain tick, never
  persisted — a crashed upgrade needs no journal replay to re-arm.

## Verdict

P3 passes all three gates: first-open wall 3.9 min (<10 target), background upgrade converges to
a baseline-identical full artifact while the symbols artifact serves, and SIGKILL mid-upgrade
leaves a healthy served artifact with the upgrade still owed. The known limitation stands
unchanged: the upgrade runs under a leading session; leaderless (cross-workspace-only) workspaces
surface `upgrade owed` honestly but need `workspace full` or a resident session to converge (see
the design doc's leaderless-workspaces note).

Raw evidence (scan reports, timing files, SIGKILL transcript) in the session scratchpad
(`p3-symbols-scan.*`, `p3-sigkill.result`, `p3-upgrade-scan.*`); this document records everything
decision-relevant.
