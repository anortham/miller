# Miller Standalone Bolstering Assessment

Date: 2026-07-06
Status: assessment for user review — no direction approved yet, nothing implemented.

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

## Proposed roadmap (priority order)

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
- [ ] User picks direction (which priorities to green-light) — pending
