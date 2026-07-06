# Miller Standalone Bolstering Assessment

Date: 2026-07-06
Status: consensus reached (Claude + Codex adversarial review, user-directed); implementation started
on the amended priorities below. The original roadmap section is retained below for history.

## Consensus amendments (Codex adversarial review, 2026-07-06)

Codex verdict on the original roadmap: rework. Verified findings that changed the plan:

1. **`metrics hotspots` name is taken.** `hotspots` is an existing CLI alias normalized to
   `complexity` (`MetricsTool.NormalizeOperation`, `CliDispatch`). Reusing it would silently change
   an existing alias. The churn×complexity feature is renamed **`metrics risk`**.
2. **Dead-code cannot be reference-graph-based today.** The live Miller artifact has 90,742
   `identifiers` rows and **0 resolved `target_symbol_id`** rows; the references contract itself
   says null targets must not be read as absence of usage. Dead-code candidates are demoted to P3
   as an evidence-gated, name-based-liveness prototype (a symbol is a candidate only when its name
   never appears as an identifier outside its own definition — conservative: collisions hide dead
   code rather than flagging live code), scoped to non-public symbols in high-evidence languages,
   with per-language confidence labels. Blocked-on: extractor reference resolution quality.
3. **Signals rollup moves ahead of dead-code.** `miller report` composes only reliable facts
   (health, markers, complexity, clones, churn, risk); dead-code is omitted until P3 earns
   confidence.
4. **Not just a cheap join.** `metrics risk` must join churn and complexity *before* limiting
   (top-N∩top-N misses real hotspots), define file-only/null-symbol tiers, align test filtering,
   and carry a performance budget on git range size.
5. **Contract discipline.** Each new operation is a documented additive update to
   `metrics-json-v1` (or a new contract doc for `report`), advertised in `capabilities --json`,
   with tests.
6. **P4 history must be keyed by `(workspace_id, artifact_id, revision)`** plus extractor version
   — never revision alone, because full rebuilds restart the revision counter.
7. **Dashboard consumes cached/snapshotted facts, CLI first.** No git subprocess on dashboard
   page load; churn/risk reach the dashboard via cached facts or an explicit refresh action.
8. **MCP stance holds by default.** No new MCP tools; if MCP-only clients later need `report`,
   that is an explicit user-approval discussion, not a default.

**Amended priority order: P1 `metrics risk` → P2 `miller report` (no dead-code) → P3 dead-code
candidates prototype (evidence-gated) → P4 history/trends.**

## Context

Eros is not shipping and may never ship. The user wants Miller to be a complete standalone tool,
not one that leaves visible holes labeled "Eros owns this." This assessment inventories what the
dashboard and CLI already deliver, which Eros-deferred capabilities are worth absorbing, and a
prioritized order. The constraint that survives: Miller stays deterministic and local — absorbing
Eros *workflows* does not mean absorbing embeddings or semantic ranking.

## Current state (verified 2026-07-06 against live dashboard + CLI)

Dashboard (index page): all registered workspaces (166 on this machine), aggregate file/symbol
counts, filterable table, JSON feeds (`workspaces.json`, `activity.json`, `telemetry.json`,
`diagnostics.json`).

Dashboard (workspace detail): index transparency (files, symbols, languages, symbol kinds,
freshness, revision, extractor version, sidecar status), health panel with warnings and
recommended actions, pattern inventory, telemetry-derived onboarding (hot targets, misses,
starters), local metrics panel (complexity hotspots + clone groups), context savings, activity
feed, refresh action.

CLI already ships the raw facts Eros was meant to consume:

- `metrics churn|clones|complexity --json` (contract: `docs/contracts/metrics-json-v1.md`);
  churn maps git hunks to current-index symbols.
- `references export --jsonl` (contract: `docs/contracts/references-export-v1.md`); raw usage
  facts, explicitly *not* a dead-code verdict.
- `todos` (marker audit), `patterns`, `impact --json`, `trace --json`, `telemetry export`,
  `content export`, `capabilities --json`.

## Gap inventory — what is deferred to Eros today

| Capability | Facts already in Miller? | Deterministic? | Verdict |
|---|---|---|---|
| Hotspots (churn × complexity) | Yes — both metrics exist separately | Yes | Absorb — cheap join |
| Dead-code candidates | Yes — `references export` raw facts | Yes, with explicit heuristics | Absorb — needs careful suppression design |
| Signals rollup (one repo report) | Yes — all inputs exist | Yes — composition only | Absorb |
| History / trends | No — everything is point-in-time | Yes — needs a snapshot sidecar | Absorb later — real design work |
| Semantic/vector retrieval, embeddings | No | No | Keep out (or revisit as an explicit opt-in later) |
| Confidence/evidence views, commercial orchestration | No | Partially | Keep out — onboarding panel covers the useful subset |

## Original proposed roadmap (superseded by the consensus amendments above)

### P1 — Hotspots: `metrics hotspots`

Join churn rows with complexity rows on current-index symbol id; rank by a stated deterministic
formula (e.g. normalized churn rank × complexity rank). Surface as `miller metrics hotspots
[--json]` and a dashboard panel row in the local metrics panel. Smallest slice, immediate "where
is the risky code" value. Additive to metrics-json-v1.

### P2 — Dead-code candidates: `references candidates`

Deterministic candidate list from the `identifiers`/references facts: symbols with zero inbound
references, minus explicit suppression classes (entry points, exported/public API surface flagged
as such, test symbols, generated paths, framework-bound symbols discoverable via structural facts
like routes/handlers). Every suppression is a named, listed rule — output states what was
suppressed and why, so it reads as facts, not a verdict. CLI (`--json`) + dashboard panel.
This is the highest-value absorb but needs the most design care to avoid false-positive noise.

### P3 — Signals rollup: `miller report`

One command composing existing facts into a single repo quality report: health warnings, marker
counts, top complexity hotspots, top clone groups, top churn/hotspot rows, dead-code candidate
count. Markdown (human) + JSON (agent/CI) output, plus a dashboard summary section. Pure
composition — no new extraction.

### P4 — Metric history and trends

A revision-keyed snapshot sidecar (same derived-index pattern as `telemetry.db`/`search.db`)
recording per-revision metric aggregates; dashboard trend sparklines (symbol count, complexity
distribution, clone count, marker count over time). Needs design: when snapshots are taken
(leader convergence vs explicit), retention, and schema. Do after P1–P3 prove out.

### Explicitly out (for now)

- Embeddings/semantic retrieval — changes Miller's deterministic character, heavy deps
  (model + vector store). Revisit only as a deliberate, separate decision.
- Fleet/cross-workspace ranking, suppression persistence, task orchestration — no demonstrated
  standalone need yet.

## Boundary housekeeping (required alongside any absorb)

- CLAUDE.md "1.0 replacement boundary" section, README replacement-story section, public site
  copy, and the Eros-boundary paragraphs in `docs/contracts/*.md` all say Eros owns these
  workflows. Update them in the same slice that ships each capability, or the docs will
  contradict the product.
- MCP surface stays stingy: all of the above land as CLI verbs + dashboard surfaces (the
  established `metrics` pattern), **no new MCP tools** — `search`/`inspect`/`context` remain the
  agent-facing surface, and agents can shell out to the CLI for reports.

## Acceptance criteria for this assessment

- [x] Dashboard functionality inventoried from live instance + code
- [x] CLI fact surfaces verified against actual binary and contracts
- [x] Eros-deferred capabilities enumerated with absorb/keep-out verdicts
- [x] Prioritized roadmap with rationale
- [x] User picks direction — Codex adversarial review requested, consensus amendments folded in,
      implementation green-lit on the amended P1→P2 order (2026-07-06)
