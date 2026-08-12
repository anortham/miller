---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: active
created: 2026-07-29T00:13:39.439Z
updated: 2026-08-12T00:55:07.355Z
tags:
  - strategy
  - adoption
  - versioned-store
  - release-v1.18
---

## Direction

Miller replaces the retired Julie agent-tool core and is positioned on its measured advantage over a bare agent: 2.2× correct tasks and fewer wrong actions. External posting remains the user's choice.

## Versioned family store

- The approved storage design is released: one producer-owned versioned store per repository family, with a coherent view per checkout and deduplication across worktrees and time.
- Do not redesign the store, view, coordinator, migration, rollback, or sidecar boundaries. `julie-extractors` owns extraction and all writes to producer store state; Miller owns reads and derived sidecars.
- Family-store mode is default-on. Unset/blank `MILLER_INDEX_STORE` enables it; explicit `0|false|off|disabled` selects the legacy compatibility path, including export-before-serving and no stale fallback.
- Per-view Miller sidecars remain the shipped design. Family-shared vectors and cursor-incremental search/content sidecars are follow-up work, not an invitation to redesign the released path.

## Release status

- Stable Miller v1.18.1 is live as of 2026-08-11. The next Miller candidate is v1.18.2.
- `julie-extract` v2.32.0 is live with four platform assets as of 2026-08-11 local time. It makes scoped exact family-store resolution default, adds safe full fallback/rebase behavior, recovers partial-resolution stores, and carries Windows pointer-close/durability fixes.
- Miller `main` is a local continuation tree ahead of `origin/main`, containing the merged user-relief and context-cancellation work. A clean consumer branch adds exact base-rotation and partial-resolution RootRebind regressions.
- Current goal: adopt v2.32.0, reconcile those consumer tests, dogfood store recovery/sidecars/performance/platform contracts, and prepare—but do not publish—the v1.18.2 release candidate.

## Standing constraints

- No new MCP tools without explicit approval.
- Local semantic retrieval remains default-on/off-switchable and separate from fleet semantics.
- Preserve unrelated user changes in the main Miller checkout.
- Do not push, tag, publish, or release without explicit user approval.

## References

- `docs/plans/2026-08-11-user-relief-bugfix-program.md`
- `docs/findings/2026-08-11-user-relief-bugfix-verification.md`
- `docs/plans/2026-08-10-v1.18-default-store-release-plan.md`
- `docs/findings/2026-08-10-julie-extract-2.31.3-adoption.md`
