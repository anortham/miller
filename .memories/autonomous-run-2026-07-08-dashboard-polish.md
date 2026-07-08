# Autonomous Run Report — Dashboard Polish

**Status:** Complete (awaiting push/PR approval — user approval boundary)
**Plan:** `docs/plans/2026-07-08-dashboard-polish.md`
**Branch:** `feat/dashboard-polish` (worktree `.worktrees/dashboard-polish`, base main @ 6207978, HEAD fe459f5)
**Tasks:** 7/7 complete + 1 external-review fix. Batches: A (T1–T3 parallel), B (T4–T5 parallel), T6–T7 serial.

## What shipped

- **Spine resilience + endpoint parity** (`2fcc829`) — corrupt `workspaces.db`/`telemetry.db` degrade every machine-wide reader instead of 500ing the dashboard; additive `error` on the workspace index; `/snapshot.json` selects the same default workspace as `/workspace`; JSON refresh returns a failed body, not a 500.
- **Honest timestamps + units** (`187ce3d`) — `<time>` elements render humanized text server-side (buckets mirror the client JS, so no ISO flash), telemetry window label is human-readable, `FormatBytes` gains a GB tier.
- **Theme hygiene** (`0fd59ee`) — every theme token defined once via CSS `light-dark()`; `--muted` small-text contrast raised to WCAG AA (5.19:1 light, 6.57:1 dark).
- **Live workspace list** (`4c28d90`) — 30s visibility-gated auto-refresh; filter text, sort choice, and stale-section state survive swaps; ARIA table roles dropped from the anchor grid; sortable Workspace/Files/Symbols/Rev headers; registry error notice.
- **Telemetry efficiency** (`1524f16`) — per-tool P95 N+1 query loop replaced by one ordered scan with pinned byte-identical semantics; machine-wide recent errors now name their workspace via explicitly threaded `registryDbPath`.
- **Detail-page feedback** (`a1ee35d`) — refresh in-progress indicator, open-folder toast, sparkline min/max/latest scale labels, copyable truncated id chips, plain-English `title` explanations on jargon labels.
- **Live verification** (`256b592`) — see Tests below.

## External review

**Reviewer:** codex (single pass). **Verdict:** needs-attention. **Findings:** 1 total — **1 fixed** (`fe459f5`), 0 dismissed, 0 flagged.

- **Fixed:** "Corrupt telemetry DB rendered as 'no telemetry' with no error surfaced" (medium, confidence 0.88). Lead-verified real: the plan's Architecture Quality section requires every degrade to carry the underlying message; the registry path did, telemetry/activity did not. Fix mirrors the registry pattern — additive `error` on `DashboardTelemetrySummary` + `DashboardActivityFeed`, degrade message carried from the catch, shared `notice error-notice` markup rendered; healthy-empty pins `error` null. Context-savings `not_tracked` left as-is (legitimately distinct state).
- Cost: not reported by codex-cli.

## Judgment calls

1. **T1:** wrapped `SelectTelemetryWorkspace` beyond the brief's four readers — it sits on the `ReadSnapshot` critical path and would still have 500'd `/workspace` (approved: within task intent).
2. **T2:** telemetry window bounds use short absolute UTC (`AbsoluteShort`) rather than relative text — a relative "from … to …" reads oddly for a fixed window.
3. **T5 fix round:** rejected `connection.DataSource` sibling-path registry derivation in favor of an explicit optional `registryDbPath` parameter (codebase convention; env overrides can split the DB pair).
4. **T4 note:** `aria-sort` on plain `<button>` is ignored by some assistive tech (spec-required shape; revisit only on a11y feedback).
5. **T7:** ran the branch build on scratch ports 4991/4992 instead of killing the user's running dashboard — the session's MCP launcher would have served the installed build, not the branch build.

## Tests

- **Branch gate @ fe459f5:** `dotnet build Miller.slnx -c Release` 0 warnings / 0 errors; `scripts/test.sh all` — fast 3120/3120 (19s wall), Scale 48/48 (real julie-extract, `.tools` copied into the worktree).
- Baseline on main was 3055 fast → +65 new tests on the branch.
- **Live checks (T7):** all routes 200 from the branch build; corruption drill (truncated scratch telemetry via `MILLER_TELEMETRY_DB`) degraded instead of 500; real `~/.miller` DBs untouched (19,151 / 56 rows before and after); SSR HTML has zero raw-ISO time bodies.
- Two fast-suite wall-time tripwires (47–49s) during parallel batches were machine contention — bisect showed new classes <0.5s; quiet re-runs 19–20s.
- **Flagged for human eyes** (browser-only): theme toggle visual in both directions, one live 30s list swap with filter typed, open-folder toast, chip clipboard copy.

## Blockers hit

None. (Push/PR intentionally not performed — approval boundary.)

## Files changed

24 files, +2,427 / −655 (`git diff --stat main..HEAD`): `DashboardData.cs`, `DashboardEndpoints.cs`, `DashboardFormat.cs`, 6 razor components, `dashboard.css`, 3 JS files, 4 test files (2 new), plan + SDD reports.

## Next steps

1. **User decision:** push `feat/dashboard-polish` + PR, or merge locally into main. Note: local main is itself 7 commits ahead of origin/main (unpushed), so a PR opened now would also show those commits until main is pushed.
2. Browser eyeball of the four flagged visuals after merge.
3. Leftover clean worktree `.worktrees/dashboard-registry-hygiene` (branch already merged) can be removed when convenient.
