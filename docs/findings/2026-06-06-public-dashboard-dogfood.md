# Public Dashboard Dogfood Evidence - 2026-06-06

## Scope

Feature branch: `feature/public-dashboard`

Dashboard target: `http://127.0.0.1:5097/`

The dashboard was run from the feature worktree:

```bash
MILLER_DASHBOARD_PORT=5097 dotnet run --project src/Miller.Dashboard -c Release --no-build
```

## JSON Smoke

`/healthz` returned:

```text
miller-dashboard ok
```

`/snapshot.json` returned a live registry snapshot with:

```text
selected 816288f47c5b0cf50c2eed1a22557d97100a6811846cc2061687421eaa5cc227
workspaces 19
workspace_facts 1
facts 403 6844 10
savings not_tracked 0 0
```

Interpretation: the selected workspace facts were readable from `symbols.db`; the snapshot reads index facts only
for the selected workspace; context savings correctly stayed `not_tracked` because the existing telemetry rows had
no positive `source_bytes`.

## Browser Verification

Browser driver: `playwright-core` installed under `/tmp/miller-playwright`, using local Google Chrome.

Viewports:

- Desktop: `1440x1000`
- Mobile: `390x844`

Automated DOM checks passed in both viewports:

- `Index transparency` panel visible
- `Context saved` panel visible
- `Telemetry` panel visible
- workspace panel visible
- `snapshot.json` link visible
- no detected horizontal body overflow

Screenshots captured during verification:

- `/tmp/miller-dashboard-desktop-final.png`
- `/tmp/miller-dashboard-mobile-final.png`

Visual notes:

- Desktop layout presents the workspace list beside the selected workspace facts and telemetry stack.
- Mobile layout collapses into one column with workspace, index facts, context savings, and telemetry in order.
- The live data shows the expected `not_tracked` context-savings empty state; tracked savings are covered by
  `DashboardShell_RendersWorkspaceFactsContextSavingsAndSnapshotLink`.

## Verification Commands

```bash
scripts/test.sh
dotnet build Miller.slnx -c Release
git diff --check
```

Results:

- Fast suite: `1638` passed, `0` failed.
- Release build: `0` warnings, `0` errors.
- Whitespace check: clean.

## Claude Review Follow-Up

Requested reviewer: Claude.

Claude returned three material findings:

- content-search `source_bytes` was corpus-wide per call, inflating context savings
- `ReadSnapshot` aggregate-scanned every registered workspace DB per page render
- `language_count` used the top-12 displayed language list instead of the true distinct count

All three were fixed and covered by focused regression tests before the final branch gate.
