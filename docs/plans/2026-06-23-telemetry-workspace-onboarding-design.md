# Telemetry-Derived Workspace Onboarding Design

- **Date:** 2026-06-23
- **Status:** Proposed
- **Scope:** Generic Miller feature for any indexed workspace.

## Purpose

Use Miller's local telemetry to help a new agent understand how to start in a workspace.

The report should answer:

> What do agents usually do successfully in this repo, where do they get stuck, and what should a new session try first?

This is not a replacement for `CLAUDE.md`, `AGENTS.md`, or a repo's human-written contributor guide. It is an
advisory onboarding report generated from local, query-safe telemetry plus the current Miller index.

## Product Decision

Add a read-only workspace onboarding report.

Preferred surface:

```bash
miller workspace onboarding [--json] [--markdown] [--workspace-id SELECTOR | --workspace DIR]
```

MCP surface:

```text
workspace(operation="onboarding", workspace_id=?, path=?, format?)
```

Default compact/Markdown output should be useful for an agent at session start. JSON should expose the same facts
for dashboards or future automation.

Do not auto-edit `CLAUDE.md`, `AGENTS.md`, or `ONBOARDING.md` in the first slice. A future explicit
`--write ONBOARDING.md` flag can be considered after the report proves useful.

Document the feature in `README.md` and the GitHub Pages site (`docs/site/index.html`) when the first slice ships.
The public docs should describe the report as advisory telemetry-derived guidance for any indexed repo, not as an
automatic instruction-file writer.

## Data Sources

The report combines:

- Machine-global telemetry: `~/.miller/telemetry.db`, scoped by stored `workspace_id`.
- Current workspace index: `.miller/symbols.db`.
- Existing sidecar/readiness facts where cheap: search sidecar, content corpus, workspace health/status.
- Optional repo docs presence: whether `CLAUDE.md`, `AGENTS.md`, `README.md`, or `ONBOARDING.md` exist.

Telemetry remains query-safe. Miller should not store raw queries, paths, snippets, or targets in telemetry for
this feature. Existing `target_hash` values may be locally matched against current index candidates only while
generating the report.

## Report Sections

### Start Here

Three to five suggested first calls for a new agent session.

Examples:

- `workspace health` when stale sidecars or recent errors are common.
- `context(query="...")` when successful sessions usually begin with broad orientation.
- `search` followed by `inspect` when telemetry shows that flow dominates.
- `impact git=true` when the workspace has recent edit/refactor workflows.

When telemetry is sparse, fall back to generic Miller guidance instead of inventing repo-specific advice.

### Common Successful Flows

Aggregate call transitions within a short window, scoped per workspace.

Examples:

- `search -> inspect`
- `context -> inspect`
- `inspect -> impact`
- `impact -> edit`

Only show flows above a small count threshold. The report should not treat a single unusual session as a stable
workspace pattern.

### Hot Areas

Recover frequently inspected/searched targets when safe.

Approach:

1. Build candidate strings from the current index: symbol ids, symbol names, file paths, and `path:name` scoped
   forms.
2. Hash candidates with the same SHA-256 routine used for telemetry targets.
3. Join against repeated `target_hash` values.
4. Emit recovered symbols/files with a confidence label.

Confidence examples:

- `symbol_id_hash`: exact id candidate matched.
- `symbol_name_hash`: name candidate matched; may collide by same name.
- `file_path_hash`: path candidate matched.
- `unresolved_hash`: repeated target hash exists but no current candidate matched.

Never show raw unresolved hashes in compact output. JSON may include counts but should avoid making hashes look
like useful agent targets.

### Common Misses

Summarize empty/error patterns using query-safe fields:

- tool and op
- `empty_reason`
- `error_category`
- mode/format/depth/limit buckets
- stale/fresh index signal

Examples:

- Many `search` empties with `mode=auto`: suggest `mode=source` or `mode=content` for text/prose searches.
- Many `trace` empties: suggest `inspect depth=full` first or using `trace mode=refs`.
- Many unknown-workspace errors: suggest `workspace list`.

This section is important because telemetry may reveal bad habits. Bad habits should become cautions, not
canonical instructions.

### Tool Cost And Friction

Summarize high-cost or high-output calls:

- slowest tools by p95 duration.
- largest outputs by returned bytes / estimated tokens.
- high empty or error ratios.

The goal is practical guidance, such as "use summary inspect before full inspect" or "scope content searches with
kind/path filters".

### Suggested Instruction Additions

Optional final section:

```markdown
## Suggested CLAUDE.md Additions

- ...
```

These are recommendations only. Miller should not write canonical instruction files automatically.

## Architecture Quality

**Affected modules:** `Miller.Server` workspace CLI/MCP surface, `Miller.Server.Telemetry` aggregation readers,
`Miller.Indexing` symbol/file candidate reads, workspace renderers, README/site docs, contracts, and tests.

**Caller-facing interface:** `miller workspace onboarding`, MCP `workspace(operation="onboarding")`, compact
Markdown-like text, and JSON output.

**Depth/locality check:** The report belongs with workspace status/health because it is workspace-scoped and uses
registry selectors. Telemetry aggregation should stay read-only against `~/.miller/telemetry.db`. Target recovery
should use cheap projections from the current index, not full repository hydration.

**Test surface:** Prove behavior through CLI dispatch and workspace tool tests. Add focused aggregation tests for
transition counting, sparse telemetry fallback, query-safe metadata handling, and target-hash recovery.

**Seams/adapters:** Keep raw SQLite access in small readers. Rendering should consume models only. Do not couple
the report to dashboard components. A dashboard panel can render the same JSON in a separate approved slice.

**Rejected shortcuts:** No raw query storage. No automatic edits to `CLAUDE.md`/`AGENTS.md`. No silent changes to
search ranking or result ordering. No output caching in this slice. No Eros-only workflow fields.

**Architecture risk:** medium. The feature is read-only, but it adds a new public report and could accidentally
teach bad agent habits if the "common misses" distinction is weak.

## Proposed Components

### `WorkspaceOnboardingFacts`

Server-level model that combines:

- workspace identity and sample window.
- telemetry coverage counts.
- successful flow summaries.
- recovered hot targets.
- empty/error guidance.
- cost/friction facts.
- suggested first calls.
- suggested instruction additions.

### `TelemetryOnboardingReader`

Read-only aggregator over `tool_telemetry`.

Responsibilities:

- Scope by `workspace_id`.
- Use a bounded time window, defaulting to the last 30 days.
- Count tool/op/outcome groups.
- Count call transitions within the same workspace and a short time window.
- Count repeated target hashes.
- Summarize empty/error metadata keys without exposing raw target text.

### `WorkspaceTargetHashResolver`

Cheap helper that builds candidate hashes from current index facts.

Candidate sources:

- symbol id
- symbol name
- file path
- `path:name` scoped display strings

The resolver should label confidence and cap output. Hash recovery is best-effort; failure to recover a target is
not an error.

### `WorkspaceRender.Onboarding`

Pure compact/Markdown and JSON rendering.

Compact output should lead with practical guidance:

```text
# workspace onboarding
workspace: miller-b275269b2d7c
telemetry: 30d  482 calls  search=41% inspect=38% context=8%

start here:
- workspace health
- context query="..."
- search query="..." then inspect the top symbol

successful flows:
- search -> inspect (87)
- context -> inspect (19)

common misses:
- search auto often returns empty; use mode=source for source-body text.
```

JSON should preserve the same facts with stable field names once the contract is documented.

## Privacy And Safety

- Do not persist raw queries or raw targets.
- Do not print raw unresolved target hashes in compact output.
- Do not infer human intent from a single call.
- Use minimum-count thresholds before calling a pattern "common".
- Treat telemetry as local machine evidence, not project truth.
- Make stale or sparse telemetry explicit.

## Non-Goals

- No automatic instruction-file rewriting.
- No ranking changes.
- No prediction/prefetching.
- No semantic search or embeddings.
- No Eros dashboard workflow state.
- No cross-machine telemetry sync.

## Acceptance Criteria

- `miller workspace onboarding --markdown` returns useful generic guidance even with no telemetry.
- With telemetry, the report shows top successful flows, common misses, and tool cost/friction without raw query
  text.
- Repeated target hashes are recovered only when they match current index candidates and are labeled with
  confidence.
- JSON output is deterministic and covered by tests.
- Selector behavior matches `workspace status` and `workspace health`.
- The feature is read-only and never modifies `CLAUDE.md`, `AGENTS.md`, or `ONBOARDING.md`.
- `README.md` and `docs/site/index.html` document the onboarding report and its non-goals.
