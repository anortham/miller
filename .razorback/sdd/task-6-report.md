# Task 6 report — Detail-page polish (feedback, sparklines, id chips)

**Status:** DONE
**Commit SHA:** <filled below>
**Worktree:** `/Users/murphy/source/miller/.worktrees/dashboard-polish`
**Branch:** `feat/dashboard-polish`
**Base commit:** 1524f16 (Tasks 1–5 merged)
**Dirty state at report time:** only my owned files + this report modified; pre-existing `M .razorback/sdd/task-5-report.md` left untouched (not mine).

## What I built

All four acceptance criteria, TDD (7 new render tests written first → red → implemented → green).

1. **Refresh button in-progress label** (`WorkspaceDetailPanel.razor:14`). The button now wraps two spans:
   `.refresh-button-label` ("Refresh index") and `.refresh-button-indicator` ("Refreshing…"). CSS shows the
   indicator and hides the label only while the requesting element carries htmx's default `.htmx-request`
   class — no separate `hx-indicator` target needed because the button itself is the requester. The pre-existing
   `hx-disabled-elt="this"` is kept, so the button also disables during the request. This replaces the
   "global opacity only" signal the audit (A9) called out with an explicit textual state.

2. **Open-folder as a real button + success toast** (`WorkspaceDetailPanel.razor:22`, `dashboard-site.js`).
   Reclassed from `.subtle-link open-folder-link` to `.refresh-button open-folder-button` (real control chrome,
   not a link). Added `data-toast-success="Opening the workspace folder…"`. A new delegated
   `htmx:afterRequest` listener in `dashboard-site.js` reads `data-toast-success` off `event.detail.elt` and, on
   `event.detail.successful`, calls the **existing** `window.showDashboardToast(message, 'ok')` — no second toast
   mechanism. The action stays `hx-swap="none"` (no visible swap), so this is the only success signal. Generic
   attribute-driven, so any future opt-in button reuses it.

3. **Sparkline min/max/latest scale labels** (`WorkspaceTrendsPanel.razor:34`). The `<svg>` is wrapped in a
   `.trend-plot` column; below it a `.sparkline-scale` row renders `min <SeriesMin>`, `max <SeriesMax>`,
   `latest <Latest>`, derived directly from the in-hand `DashboardTrendSeries.Points` (min/max) and `.Latest`.
   No charting library. Each label carries a `title` tooltip. `HasTrend` guarantees ≥2 points so `Points.Min()`
   /`.Max()` are safe.

4. **Truncated copyable id chips + jargon titles.**
   - Artifact id (`WorkspaceDetailPanel.razor`): visible `.id-chip` shows first 12 chars + `…`, full value in
     `title`; a `hidden-copy` `#copy-artifact-id` span holds the full value and a `Copy` button targets it via
     the existing `data-copy-target` delegated handler. `n/a` still renders when absent.
   - Clone body hash (`WorkspaceLocalMetricsPanel.razor`): same chip pattern, per-row ids `copy-clone-hash-{i}`
     (loop switched to indexed `for` to mint stable ids).
   - Jargon `title` explanations added to the `<dt>` for **Revision**, **Artifact**, **Search sidecar**,
     **Content sidecar** — one plain-English sentence each.
   - Last-scan `<time>` now renders `RelativeTime(Facts.LastScanAt, Now)` (Task 2 contract) instead of raw ISO;
     `data-ts`/`datetime` keep the raw ISO for the client repaint (same contract ActivityFeedPanel uses).

## Judgment calls

- **Ellipsis is U+2026 (`…`), asserted as `&#x2026;` in tests** (`DashboardActivityFeedTests.cs:514,578`). The
  Blazor `HtmlRenderer` default `HtmlEncoder` encodes U+2026 to the numeric entity `&#x2026;`, which renders as a
  correct ellipsis in-browser. Kept the typographic glyph in source; assertions match the encoded output.
- **`ChipText` duplicated (3 lines) in both razor `@code` blocks** rather than added to `DashboardFormat`
  (`WorkspaceDetailPanel.razor` + `WorkspaceLocalMetricsPanel.razor`). `DashboardFormat.cs` is outside my file
  ownership; a tiny private static helper per component respects that boundary.
- **Copy source is a hidden full-value span, not CSS-truncation.** Truncating server-side (testable "12 chars +
  ellipsis" markup) means the visible text can't be the copy source, so I reuse the codebase's existing
  `hidden-copy` + `data-copy-target` pattern (already used for workspace_id / root path) to copy the full value.
- **Toast tone `ok` needed a style.** Base `.dashboard-toast` is danger-coloured; added `.dashboard-toast-ok`
  (uses `--ok`/`--ok-soft` tokens) so a success confirmation isn't red.
- **Global `.htmx-request { opacity:.5 }` still applies** to the refreshing button; the added text label is the
  primary signal, dim is secondary. Acceptable and consistent with the rest of the dashboard.

## Miller calls + confirmations

- `mcp__miller__inspect`/`mcp__miller__search` schemas loaded via ToolSearch; used Read on the three worktree
  razor files as the source of truth (worktree is authoritative per brief). Drift note: the worktree razor files
  match main's structure at the regions the brief cited (refresh :16 region, open-folder :22 region, sparkline
  :34-39, clone `<code>` hash) — no unexpected divergence.
- API-shape evidence (read from `src/Miller.Dashboard/DashboardData.cs`):
  - `DashboardWorkspaceFacts` (:118) — `ArtifactId` is an optional trailing ctor param (nullable string,
    default null); `LastScanAt` nullable string; sidecar statuses strings.
  - `DashboardTrendSeries` (:229) — `Points: IReadOnlyList<double>`, `First`, `Latest`; `HasTrend => Points.Count >= 2`.
  - `DashboardSparkline` (:270) — `ViewBox`, `Points(...)` already exist; I only added text labels, no geometry change.
  - `DashboardMetricCloneGroup` (:212) — `BodyHash`, `Count`, `Symbols`.
- Reused the established toast (`dashboard-site.js:140 window.showDashboardToast`), copy pattern
  (`data-copy-target` delegated click at `dashboard-site.js:169`), and the `.rel-ts`/`data-ts`/`datetime` time
  contract from `ActivityFeedPanel.razor:27`.

## What Task 7 must live-verify (browser)

- **Refresh indicator visual** — click Refresh index; confirm the label flips to "Refreshing…" and the button
  disables/dims during the in-flight request, then restores after swap.
- **Open-folder toast** — click Open folder; confirm a green (ok-tone) toast "Opening the workspace folder…"
  appears bottom-right on 2xx, and no success toast on failure (the existing error toasts should fire instead).
- **Clipboard** — click Copy on the artifact id and a clone body hash; confirm the **full** value is copied
  (not the truncated chip text) and the button flashes "Copied"; confirm graceful degrade when
  `navigator.clipboard` is unavailable (non-secure context) — chip `title` still shows the full value.
- **Chip titles / jargon tooltips** — hover the truncated chips (full id) and the Revision/Artifact/sidecar
  `<dt>` labels (plain-English sentence).
- **Sparkline scale** — confirm min/max/latest read correctly against the drawn line for a real history.db.

## Gate invariants + results

- **worker-red-green** — `dotnet test … --filter "Category!=Scale&FullyQualifiedName~DashboardActivityFeedTests"`
  → **Passed 28/28** (7 new + 21 existing). Proves the four acceptance-criteria markup contracts: refresh
  indicator spans, open-folder button + toast hook, artifact/clone truncated chips with full-value title + copy
  target, jargon `<dt>` titles, last-scan relative time, sparkline min/max/latest labels. Confirmed red first —
  the two chip tests failed on the ellipsis assertion before implementation.
- **worker-ceiling** — `scripts/test.sh` (fast suite) → **Passed 3116/3116**, wall time **18s** (ceiling 30s).
  Proves no regression across the suite. Build is 0 warnings / 0 errors (Release, `TreatWarningsAsErrors`).
- **CSP** — no `onclick=` / inline `<script>` in any changed file; copy + toast go through delegated listeners in
  `dashboard-site.js`. Clipboard degrades (existing `copyTextFromTarget` fallback). Confirmed by the passing
  `Shells_IncludeDashboardBehaviorScripts` (`DoesNotContain onclick=`).
- **Read surface** — no new endpoints, no mutations, JSON contracts untouched. CSS additions are append-only.
