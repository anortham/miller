# Phase 10 Workspace re-audit

## Outcome

APPROVE. No Workspace source or contract change is required.

The re-audit treated the July 25 lifecycle, truthfulness, and output-bound remediation as complete. It did not
repeat the shipped removal, prune, health, onboarding, registry, diagnostic, or CLI work.

## Current evidence

- The freshly built HEAD CLI filtered 75 registrations to the takeover worktree and reported one matched and
  returned row, zero omitted rows, nine registered missing roots, and zero matched or returned missing roots.
- Selected status resolved the exact takeover root, workspace ID, and artifact without changing the process
  binding.
- Selected health returned `usable_with_warnings`, two warnings, and two recommended actions.
- Removal derives `.miller` only from the validated registered canonical root. It refuses current, sensitive,
  machine-global, corrupt-path, unregistered, symlinked, and write-locked targets before deletion.
- Recursive cleanup removes contained symlinks as links; it does not traverse them outside the registered
  `.miller` directory.
- Prune removes missing-root registry rows only and protects the current workspace row. It never deletes
  workspace files.
- List uses one registry snapshot and reports exact registered, matched, returned, omitted, error, and
  missing-root totals after filtering and byte-budget trimming.
- MCP health is compact or summary JSON, onboarding is row-bounded, and every final response is remeasured after
  diagnostic attachment against the 12 KiB UTF-8 ceiling. Oversize success or exception output becomes a fixed
  bounded refusal.
- CLI health and onboarding retain their exhaustive contracts.
- The focused non-Scale Workspace, renderer, removal, health, onboarding, registry, CLI, and dashboard-read scope
  passed 289 tests.

The connected Miller server was used only for stale-index structural orientation with the explicit worktree
selector and `ensure_fresh=false`. Current behavior evidence came from the freshly built HEAD CLI and focused
tests.

## Fresh review

Fresh read-only Claude passes approved:

- registered-only removal, corrupt-row refusal, root and `.miller` symlink refusal, contained-symlink safety,
  live-root protection, and all-write-lease exclusion;
- registry-only prune with current-row protection;
- final 12 KiB MCP enforcement on success and exception paths, including after diagnostics;
- truthful list, health, and onboarding total and omission fields;
- bounded MCP summaries versus exhaustive CLI output.

One excerpt-only review initially classified row-width overflow, health-summary overflow, and post-attachment
growth as defects. Those claims were rejected after the complete call chain was supplied: `WorkspaceTool`
remeasures every final output, the health overflow exception returns through the same bound, and the replacement
diagnostic contains only fixed text. Its observation that list prefix accounting recomputes every retained and
omitted count was accepted. Suggestions for graceful onboarding truncation or different diagnostic codes do not
change the active acceptance criteria.

## Ownership

Miller continues to own fixed process binding, registry lifecycle, health, onboarding, leadership, and bounded
agent output. Julie's mutable session switching, list-time cleanup, unbounded registry output, and broad
destructive hints remain intentionally rejected. No MCP tool or workspace-binding mutation was added.
