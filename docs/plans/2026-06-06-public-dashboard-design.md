# Miller Public Dashboard Design

- **Date:** 2026-06-06
- **Status:** Approved design
- **Decision level:** Public-release readiness slice
- **Primary owner:** Miller dashboard

## Purpose

The beta dashboard proves Miller can launch a local loopback UI and show registered workspaces plus tool
telemetry. For a real public-ready release, the dashboard should help a developer understand what Miller knows
about their repositories and what Miller is doing internally without crossing into Eros product territory.

Miller's dashboard should be a local transparency surface: workspace/index health, language and symbol counts,
freshness, sidecar state, telemetry, and context-savings metrics. Eros keeps advanced workflow intelligence,
recommendations, semantic/vector views, confidence narratives, and commercial dashboards.

## Current State

Existing Miller dashboard pieces:

- `src/Miller.Dashboard/Program.cs` hosts a loopback dashboard with static SSR Razor components and htmx
  fragments.
- `src/Miller.Dashboard/DashboardData.cs` reads `~/.miller/workspaces.db` and `~/.miller/telemetry.db`.
- `WorkspacesPanel.razor` renders registered workspaces.
- `TelemetryPanel.razor` renders per-tool call counts, avg/p95/max latency, error counts, last calls, last
  errors, recent errors, and estimated returned tokens.

Useful existing data:

- `tool_telemetry` already stores `source_bytes`, `bytes_returned`, and `est_tokens`.
- Workspace extract DBs already expose cheap aggregate data through `files` and `symbols`, including file
  language, symbol kind, revision, and content byte counts.
- `WorkspaceIndexFactsReader` proves the intended pattern: status/dashboard reads should not hydrate the full
  repository graph, bridge data, or BM25 structures.

Observed gap:

- Current telemetry rows mostly have `source_bytes = 0`, so the dashboard can show returned-token totals now,
  but true "context saved" requires read tools to populate source/examined bytes where the value is already known
  or cheaply computable.

## Product Boundary

Miller includes:

- Workspace registry view.
- Workspace index transparency: file counts, symbol counts, language breakdown, symbol-kind breakdown, index
  revision, freshness, last scan, and sidecar/search health.
- Basic operational actions: refresh, open workspace, copy selector/path, and JSON links.
- Telemetry transparency: calls, p95 latency, error rate, recent errors, returned tokens, and context savings.

Miller does not include:

- Agent recommendations.
- Semantic/vector retrieval dashboards.
- Confidence narratives or evidence scoring.
- Commercial workflow views.
- Eros-style intelligence pages.

## Architecture Quality

**Affected modules:** `Miller.Dashboard`, small telemetry enrichment in `Miller.Server.Tools`, and lightweight
aggregate readers in `Miller.Indexing` if `WorkspaceIndexFactsReader` is extended.

**Caller-facing interface:** The dashboard HTTP routes and JSON endpoints. Tool MCP/CLI output should not change
except for telemetry enrichment side effects.

**Depth/locality check:** Dashboard data should come from registry, telemetry DB, and read-only aggregate SQL
against each workspace's `symbols.db`. It must not call `RepositoryIndexLoader`, hydrate bridge graphs, or build
search indexes.

**Test surface:** Dashboard tests should render components/fragments and assert JSON contracts. Aggregate-reader
tests should use fixture DBs and prove counts without full index hydration. Telemetry tests should prove
`source_bytes`/`bytes_returned`/`est_tokens` roll up into context-savings fields.

**Seams/adapters:** Add a dashboard-specific read model only if it keeps component rendering simple and avoids
large SQL in Razor components. Prefer extending `DashboardData` and a small `DashboardWorkspaceFacts` aggregate
over new services.

**Rejected shortcuts:** Do not load full workspace indexes for language/symbol stats. Do not copy Julie's dark
amber theme. Do not add Eros-owned pages just because the dashboard exists.

**Architecture risk:** Medium. The UI is small, but the dashboard touches shared telemetry and indexed artifacts.
The risk is controlled by keeping reads aggregate-only and preserving existing CLI/MCP behavior.

## UX Direction

Use Julie's useful information shape, not Julie's theme:

- Workspace table with rows that are easy to scan.
- A compact language bar per workspace.
- Selected workspace details in a right-hand or lower detail area.
- Summary metrics at the top of the selected workspace panel.
- Telemetry as supporting operational context.

Miller styling should feel crisp, local, and technical:

- Light default theme.
- Restrained teal/green accent with neutral grays.
- Dense dashboard layout, not a marketing page.
- Cards only for repeated metrics or bounded panels, not nested decorative cards.
- No Eros-like intelligence navigation.

## Data Model

Add dashboard read models similar to:

- `DashboardWorkspaceIndexFacts`
  - `workspace_id`
  - `file_count`
  - `symbol_count`
  - `language_count`
  - `content_bytes`
  - `top_languages`
  - `top_symbol_kinds`
  - `index_revision`
  - `freshness`
  - `sidecar_status`

- `DashboardContextSavings`
  - `source_bytes`
  - `bytes_returned`
  - `saved_bytes`
  - `estimated_returned_tokens`
  - `savings_ratio`
  - per-tool breakdown

Language and symbol-kind breakdowns should come from aggregate SQL over the workspace DB:

```sql
SELECT language, COUNT(*) FROM files GROUP BY language ORDER BY COUNT(*) DESC;
SELECT kind, COUNT(*) FROM symbols WHERE name IS NOT NULL GROUP BY kind ORDER BY COUNT(*) DESC;
```

Context savings should come from telemetry:

```sql
SELECT
  COALESCE(SUM(source_bytes), 0),
  COALESCE(SUM(bytes_returned), 0),
  COALESCE(SUM(est_tokens), 0)
FROM tool_telemetry
WHERE workspace_id IS $ws;
```

Saved bytes are `max(0, source_bytes - bytes_returned)`. If `source_bytes` is zero, render the field as
"not yet tracked" instead of implying zero savings.

## Implementation Slices

### Slice 1: Dashboard Read Model

- Extend dashboard data reads with per-workspace aggregate index facts.
- Keep missing, stale, corrupt, and schema-mismatched workspace DBs non-fatal.
- Add stable JSON for workspace facts and context savings.
- Keep `/workspaces.json` and `/telemetry.json` backward-compatible; add `/snapshot.json` for the richer
  dashboard data used by the UI.

### Slice 2: Workspace UI

- Replace the current workspace button list with a scan-friendly table.
- Show status, path, revision, files, symbols, and language bar.
- Add selected workspace detail with top languages, symbol kinds, freshness, last scan, and refresh action.
- Preserve htmx fragment refresh behavior.

### Slice 3: Context Savings

- Add dashboard rollups for returned tokens and context savings.
- Populate `source_bytes` for tools where Miller already knows the candidate/input byte volume:
  - `context`: bytes represented by candidate documents considered.
  - `impact`/`trace`: graph nodes or rows visited can remain `bytes_examined`; only set `source_bytes` if a real
    byte count is available.
  - `search`/`inspect`: set source bytes only when the projection or file/body read path has a cheap, defensible
    count.
- Do not invent savings from row counts.

### Slice 4: Visual Polish And Verification

- Apply a unique Miller theme.
- Verify desktop and mobile layouts in the browser.
- Keep text from overflowing table cells and metric tiles.
- Confirm static assets are packaged in release archives.

## Acceptance Criteria

- [ ] Dashboard shows all registered workspaces without hydrating full indexes.
- [ ] Each ready workspace shows file count, symbol count, language count, revision, and a language bar.
- [ ] Selected workspace shows top languages and top symbol kinds.
- [ ] Selected workspace shows freshness, last scan, and sidecar/search health when available.
- [ ] Dashboard shows context savings and returned-token metrics, with "not yet tracked" when source bytes are absent.
- [ ] Context-savings telemetry uses real byte counts, not guessed row counts.
- [ ] Refresh action remains available and reports errors visibly.
- [ ] Missing registry, missing telemetry DB, stale workspace DB, and schema-mismatched workspace DBs render safely.
- [ ] `/snapshot.json` exposes the same rich workspace/index/telemetry data used by the UI while
  `/workspaces.json` and `/telemetry.json` remain backward-compatible.
- [ ] Fast tests cover dashboard read models, rendering, and telemetry rollups.
- [ ] Relevant scale or integration tests prove the dashboard can read a real Miller workspace DB.
- [ ] Browser verification captures desktop and mobile screenshots before calling the UI complete.

## Public-Release Gate

This dashboard work is not required to justify the existing source-checkout beta tag. It is required before Miller
should be presented as public-ready beyond beta because it gives developers transparent evidence that indexing,
freshness, telemetry, and token-saving behavior are working locally.

Release evidence should include:

- Real workspace screenshot or browser-render proof.
- `/snapshot.json` or equivalent output for a real workspace.
- Context-savings rollup after representative `search`, `inspect`, and `context` calls.
- Build, fast suite, dashboard tests, and package static-asset checks.
