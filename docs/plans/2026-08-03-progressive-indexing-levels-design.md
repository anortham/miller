# Progressive Indexing Levels — P0 design (final)

**Status:** decided 2026-08-03, implementing in the same working session (P1 julie-extractors +
P2 Miller). Executes the program plan
[`2026-08-03-progressive-indexing-levels-program.md`](2026-08-03-progressive-indexing-levels-program.md);
this document records the decisions the program plan left open, and is the authority when they
differ (the program plan's strawman had three levels; the shipped design has two).

## Decisions at a glance

| Question | Decision |
| --- | --- |
| How many levels | **Two.** `symbols` (first-open core) and `full` (everything). The strawman's L3 text/facts layer folds into `full`: its consumers are ~0.3% of tool calls (patterns ≈ 138 calls all-time, `search regions=` 5), and the heavily-used source/content search modes ride Miller's own `content.db`, which needs only the `files` table. |
| Where the gate lives | **Extraction, not import.** Gated passes produce empty result families; the writer writes what the spool carries. No schema change, no import table-set machinery, catalog sha unchanged. |
| Level identity | `artifact_metadata.index_level` = `symbols` \| `full`; **absent = full** (old artifacts and old binaries stay compatible both directions). Level is **immutable for an artifact's lifetime**. |
| Upgrade mechanics | **Full re-extract into `symbols.db.rebuild` + promote** (`FullRebuildPromotion`). Never in-place L2 writes under served readers (the 2026-06-11 7 KB/s WAL-collapse field report), never retained-spool replay (the 130 GB orphan history; extraction is only ~5% of scan wall). |
| Miller policy | `MILLER_INDEX_LEVELS` = `progressive` (default) \| `full` (today's behavior) \| `symbols-only` (never upgrade), plus a per-workspace registry override. `full` is the permanent zero-behavior-change escape hatch, like `MILLER_SEMANTIC=off`. |
| Degradation shape | Reference-needing tools return an actionable "reference layer converging" result (never a bare empty result, never an exception); each such call stamps `degraded`/`degraded_reason` into telemetry `metadata_json` — the demand counter that decides whether on-demand extraction is ever worth building. (The telemetry `outcome` column is CHECK-constrained to ok\|empty\|error, so the counter rides metadata, not a new outcome value.) |

## Level composition (measured, not guessed)

At `symbols` level julie-extract skips exactly two things: the per-language **identifier walk**
(a separate full-tree pass in every language's `identifiers.rs`, invoked from the shared registry
macros) and the **text/facts collectors** (`collect_source_regions`, all `structural_facts`
families). Everything else runs.

| | Populated at `symbols` | Empty at `symbols` (converges with upgrade) |
| --- | --- | --- |
| Tables | files, symbols, symbol_annotations, parse_diagnostics, relationships, pending_relationships, pending_resolutions, reference_sites (relationship/pending arms only), type_facts, complexity_metrics, + bookkeeping/capability tables | identifiers, identifier_resolutions, literals, type_argument_usages, type_arguments, source_regions, structural_facts |

Byte/row evidence (dotnet/runtime @ 22.84 GiB pre-2.24.0): the empty set above is ~74% of
artifact bytes and the entire resolution-phase row volume (12.86 M identifiers, 15.5 M reference
sites); the populated set serves `search`, `inspect`'s core, `context`, `workspace`, and all
`metrics`/`report` surfaces — ~86% of live tool calls.

Two subtleties the composition depends on:

- **The resolution hook still runs at `symbols` level.** Identifier resolution is a no-op (no
  rows), but `pending_relationships` resolve, so cross-file inheritance/import edges exist at L1
  and `reference_resolution_status` stays `complete` — no special-casing in the resolution
  version-upgrade gate.
- **Uniformity is enforced, not assumed.** Three languages (sql, markdown, regex) record literals
  outside their identifier walks. `ExtractionResults::strip_to_symbols_level` is the single
  authority on the gated set, applied in the shared registry dispatch after collectors — so a
  `symbols` artifact can never carry a silent three-language literals subset (language-parity
  rule).

## julie-extract surface (implemented in P1)

- `scan --level <symbols|full>`; default `full`. **The flag sets the level only for a NEW
  (never-written) artifact.** An existing artifact always inherits its recorded level; a
  conflicting explicit `--level` — with or without `--force` — is `usage_error` (exit 2) with
  `artifact_index_level`/`requested_index_level` in details. `update` takes no flag and extracts
  at the artifact's recorded level.
- `artifact.index_level` appears on every report (scan/update/info), read from metadata with
  absent→`full`.
- The bulk-load first-build path, spool format, delta hash-skip, supervision flags, and JSONL
  export are all untouched. No new verbs; no schema DDL change.

## Miller orchestration (P2)

**Scan level selection.** `JulieExtractRunner.Scan` gains a level argument. Full-level scans
**omit `--level` entirely** (default-full), so every scan Miller runs today is argv-identical —
and a released 2.24.0 binary only fails on the one new thing (a `symbols` bootstrap), never on
routine scans. Delta scans never pass the flag (inherit).

**Bootstrap under `progressive` policy.** Fresh workspace: the bootstrap's first scan is a
non-force `IncrementalReconcile` against an ABSENT DB (julie creates and root-binds the artifact on
first scan), so it carries `--level symbols` explicitly and builds the L1 artifact directly — then
the leader latches a `LevelUpgrade` intent. The
upgrade scan is an ordinary full-level force rebuild into `.rebuild` + promote under the existing
governor/backoff machinery; freshness detects it as an `artifact_id` change like any promote.
Deltas against the served `symbols` artifact keep it fresh during convergence (julie inherits the
level per file), so the upgrade rebuild captures current state with zero drift bookkeeping.

**`LevelUpgrade` is derived from artifact state, not persisted.** On every leader
bind/claim: `artifact index_level != full && policy wants full` ⟹ upgrade owed. That makes the
owed upgrade restart-proof for free (the finding-9 "owed rebuild" problem class), with no new
journal. Intent rules:

- `Satisfies`: any completed **full-level** force scan discharges `LevelUpgrade`
  (`UserFullRebuild` and `ExtractorUpgrade` rescans always run full-level under any policy —
  a person or a version bump asked for the whole index). A completed `symbols`-level force never
  discharges it — and can't, structurally, because the latch re-derives from the artifact.
- **Never downgraded to a delta** — a delta cannot add a layer. Only `UserFullRebuild` keeps its
  existing downgrade permission.
- Repairs (`RootRebind`/`SchemaHeal`/`CorruptionHeal`) under `progressive` rebuild at `symbols`
  level — restore serving fast, converge in background — and the upgrade re-latches from artifact
  state afterward.
- Failure handling rides `ScanFailureJournal` unchanged (30s→30m backoff, exit-137 jobs clamp);
  the `symbols` artifact keeps serving; `workspace status`/`health` show the owed upgrade.
- `MILLER_FULL_REBUILD_INPLACE=1` disables progressive (bootstrap at full): an in-place
  environment can never promote an upgrade, and julie refuses in-place level changes by design.

**Policy resolution.** Env `MILLER_INDEX_LEVELS` > per-workspace registry column > default
`progressive`. `symbols-only` pins the workspace at L1 forever (the "fast lean search machine"
mode, reversible by policy change + `workspace full`).

**Tool degradation matrix.**

| Surface | Needs | At `symbols` level |
| --- | --- | --- |
| search (all modes), context, workspace, content, metrics churn/complexity/clones/risk, report | L1 (+content.db/search.db sidecars) | full function |
| inspect summary | L1 | full function |
| inspect overview/full — refs/callers/callees sections | reference layer | core sections serve; refs sections carry a "reference layer converging" note instead of counts |
| trace refs/path/bridge, impact (all ops) | reference layer | actionable converging result: what's missing, that the upgrade is running/owed, and that `workspace status` shows progress |
| edit replace_text / replace_symbol_body | L1 | full function |
| edit rename (safety proof) | reference layer | refuses with converging message (an unproven rename is worse than a delayed one) |
| references candidates CLI | reference layer | converging message |
| patterns, search regions= | structural_facts / source_regions | converging message (facts arrive with the same upgrade) |

Every degraded response is data-bearing (a `reference_layer_converging` diagnostic attached to
both compact and JSON output) and stamped `degraded`/`degraded_reason` in telemetry
`metadata_json` — the demand counter. If degraded-call volume on
converging workspaces stays as low as the historical shares suggest (trace+impact ≈ 7%), the
levels default is validated; if it spikes, that is the trigger recorded in the program plan for
evaluating query-triggered extraction.

**Level-proportional loading (revised from "lazy `SymbolGraphReader`").** The P0 draft proposed
lazy reference hydration; implementation showed it is unnecessary at L1 and hazardous in general.
The eager loader's reference cost is proportional to the tables it reads: at `symbols` level the
identifier/literal/facts tables are EMPTY, so the existing eager pass costs ~zero by construction,
while the relationship-arm edges (which L1 DOES carry) still load — they must, since inheritance/
import edges are part of the L1 contract. A lazy read would instead re-open the artifact at first
tool call, which races the upgrade promote (reading a DIFFERENT artifact's edges against this
index's symbol ids) and on Windows would pin a handle that blocks the promote rename. The
post-upgrade reload pays the same eager cost every full-artifact load pays today — pre-existing,
not a levels regression.

**Surfacing.** `workspace status`/`health` JSON gain `index_level` and a conditional
`level_upgrade` object (state running/owed/failed + timing); dashboard shows both per workspace.
Per-workspace policy is settable via CLI (`workspace` verb) and visible on the dashboard;
dashboard POST config is deferred until someone actually wants it (MCP surface unchanged —
stinginess rule).

## Compatibility and rollout

- Old artifacts (no `index_level` key) read as `full` everywhere; a new binary rescanning one
  stamps `full` explicitly. Old binaries reading a `symbols` artifact see a valid v5 schema with
  empty reference tables (schema/catalog untouched).
- Version-aware leadership already prevents a pre-levels binary from serving/writing a `symbols`
  artifact built by a newer one (binary_version never goes backwards).
- Sequencing: Miller's levels wiring requires a julie-extract release carrying `--level`
  (2.25.0). Until that release + pin bump, miller main must not be pushed (standing hold covers
  this); local dev runs on a from-source binary.

## Validation plan (P3)

On dotnet/runtime @ `a2f953fe266` (same box/argv as the 2.24.0 baseline): `symbols` first-open
wall target **<10 min** (vs 23.7 min full at 2.24.0); upgrade completes in background with the
L1 artifact serving throughout; SIGKILL mid-upgrade leaves the served artifact intact and the
upgrade re-owed. Typical repos (Miller-sized): first serve faster, total convergence within
noise. Fast/Scale suites green; a Scale E2E covers L1 scan → serve → upgrade → converged.

## Non-goals (unchanged from the program plan)

Per-language/per-directory levels; query-triggered extraction (trigger: sustained degraded-call
demand); removing any capability (levels change *when* costs are paid, never *whether*);
resolver-internal optimization (julie #15/#17 ride their own track).
