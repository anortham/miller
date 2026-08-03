# Progressive Indexing Levels — program plan

**Status:** program plan, user-approved to draft 2026-08-03. Implementation gated on the
scale-fixes release landing (julie-extractors branch `scale-fixes`, proposed v2.23.0) and separate
user approval to begin. Each phase becomes its own razorback execution plan when picked up.

**One-line goal:** first open of any repo builds a small **symbol-core index that serves the
dominant tools immediately**; the expensive reference layer converges in the background, and tools
that need it say "converging" instead of blocking the first impression.

## Why (measured this session, 2026-08-02/03)

The default index pays the large majority of its cost for a small minority of usage:

- **Usage (live telemetry, all-time):** `inspect` 21,620 + `search` 12,269 = **83% of all Miller
  tool calls**. The reference-layer consumers — `trace` (1,385) + `impact` (1,569) — are **7%**.
- **What powers the top two:** `search` reads symbols only (FTS sidecar from name+signature;
  content modes read source text via `content.db`). `inspect`'s core is symbols + spans; only its
  bounded refs/callers enrichment touches the reference layer, and it can degrade gracefully.
- **Bytes (Miller's own artifact, 934 MB):** reference layer (identifiers, reference_sites,
  resolutions, pending + indexes) = **688 MB (74%)**; regions/facts/literals = 158 MB (17%);
  **symbol core = 87 MB (9%)**. ~86% of calls run on ~9% of the artifact.
- **Rows at scale (dotnet/runtime, 58,366 files):** identifiers 12.86M + reference_sites 15.5M +
  pending 2.49M vs **symbols 2.58M**. The write-phase economics follow the rows: post-scale-fixes,
  the remaining ~65–70 min clean write at that scale is dominated by the resolution pass and
  child-row volume — all reference-layer work
  (julie-extractors `docs/findings/2026-08-02-scale-fixes-validation.md`).
- **Extraction cost too, not just write:** generated code runs ~43 identifiers/KB (real code
  ~5–10). At L1 the identifier walkers never run, so the pathological tail is never extracted on
  first open, not merely written faster.

## Level strawman (P0 design decides the final composition)

| Level | Tables (strawman) | Serves | When |
|---|---|---|---|
| **L1 — symbol core (default first open)** | files, symbols, symbol_annotations, relationships, parse_diagnostics (+ capability/metadata tables) | search, inspect (core), context, workspace health | blocking first open — target minutes even at 58k files |
| **L2 — reference layer** | identifiers, reference_sites, pending_relationships, resolutions overlay + resolution pass | trace, impact, references candidates, inspect refs/callers counts, edit rename-safety | background convergence after L1 serves |
| **L3 — text/facts layer** | source_regions, literals, structural_facts, type_argument usages | region search, patterns, deeper metrics | background, after or alongside L2 (P0 decides ordering) |

Open P0 questions: where type_facts and complexity_metrics land (both are cheap and inspect/metrics
-adjacent — likely L1); whether L3 precedes L2 (patterns usage is small but extraction is cheap);
whether a per-file identifier cap / generated-code damping lever ships alongside levels or stays a
separate julie-extractors task.

## Ownership split (unchanged rules)

- **julie-extractors owns extraction levels:** a scan-level flag (shape TBD in P0 — e.g.
  `--level`/`--tables`) gating which extraction passes run and which table-sets import.
  Level gates are **table-set gates, uniform across all 38 languages** (language-parity rule —
  never per-language). Artifact metadata records the level state per table-set so consumers can
  distinguish "empty because L1" from "empty because nothing found".
- **Miller owns orchestration:** bootstrap runs an L1 scan first (serving as soon as it promotes),
  then schedules the L2/L3 upgrade scan in the background under the existing governor/lease
  machinery from the fleet-safety plan. Upgrade failure falls back cleanly (L1 keeps serving).
  Tool-level degradation: `trace`/`impact`/`references candidates` and inspect's refs enrichment
  return an actionable "reference layer converging (started <t>, ~<n> files remaining)" instead of
  empty results. `workspace status`/`health` + dashboard surface per-level state. **No new MCP
  tools** (stinginess rule); everything rides existing surfaces.
- **Off-switches:** the default is L1-then-background-L2. A pinned "minimal-only" mode (never
  build L2) and a "full-first-open" mode (today's behavior) both exist, analogous to
  `MILLER_SEMANTIC=off`'s permanent zero-work guarantee.

## Phases

- **P0 — design doc** (~1 session, cross-model review gate): final level composition from the
  byte/usage/row data above; artifact metadata + freshness contract per level (converge semantics,
  `artifact_id` interaction with promote-not-merge); julie CLI flag shape; upgrade-scan mechanics
  (targeted L2-only extract+import vs full re-extract with promote — the spool header/body split
  from T4 is the natural seam: L1 import reads headers; the upgrade pass replays bodies); Miller
  tool degradation matrix; interaction with the worktree delta-rebind program (an L1 artifact
  rebinds proportionally faster — the programs compound).
- **P1 — julie-extractors implementation** (~2 sessions + release approval): extraction-pass gates,
  import table-set gates, level metadata, upgrade scan path, JSONL/report contract notes. Language
  parity verified on a real multi-language extract per the load-bearing rule.
- **P2 — Miller wiring** (~2 sessions): bootstrap L1-first under the W1 lock + W3 governor;
  background upgrade job; tool degradation; status/health/dashboard surfacing; contract tests.
- **P3 — scale validation** (~1 session): dotnet/runtime @ `a2f953fe266` — L1 first-open target
  **under ~10 minutes** (extraction ~3.5 min + symbol-core write); upgrade completes in background
  without degrading serving; typical repos (Miller-sized) unchanged or faster; hermes-agent clean.
- Releases + Miller pin bump at the end, both approval-gated as always.

**Estimate:** ~6–8 agent sessions across both repos; two releases (julie-extractors, Miller); user
time only at approval points and the P0 design review.

## Non-goals (with triggers)

- **Per-language or per-directory levels** — table-set gates only; revisit only if real dogfood
  shows a language-local need.
- **Query-triggered / on-demand extraction** (extract a file's L2 when someone traces into it) —
  attractive but a different machine; trigger: background convergence proves too slow on the
  monorepo tier even after #15.
- **Removing any capability** — every current table and tool keeps full power once converged; the
  program changes *when* costs are paid, never *whether*.
- **Resolver optimization** — that is julie-extractors backlog #15 (savepoint sub-journal decay is
  the named first target); levels reduce first-open exposure to it but do not replace it.

## Related

- Predecessor evidence: `docs/findings/2026-08-02-dotnet-runtime-scale-baseline.md` (this repo) and
  julie-extractors `docs/findings/2026-08-02-scale-fixes-validation.md` + branch `scale-fixes`.
- Sibling program: `docs/plans/2026-08-02-worktree-delta-rebind-program.md` — its
  progressive-levels candidate section is superseded by this plan; rebind + levels compound
  (smaller artifacts rebind faster).
- Fleet-safety plan (`docs/plans/2026-08-01-multi-worktree-fleet-safety-plan.md`): the upgrade scan
  is a governed background write and inherits its lease/governor/reaper machinery.
