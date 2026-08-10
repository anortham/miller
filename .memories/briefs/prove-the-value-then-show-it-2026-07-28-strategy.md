---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: active
created: 2026-07-29T00:13:39.439Z
updated: 2026-08-10T19:23:07.214Z
tags:
  - strategy
  - adoption
  - versioned-store
  - release-v1.18
---

## Direction

Miller replaces the retired Julie agent-tool core and is positioned on its measured advantage over a bare agent: 2.2× correct tasks and fewer wrong actions. External posting remains the user's choice.

## Versioned family store

- The approved storage design is landed: one producer-owned versioned store per repository family, with a coherent view per checkout and deduplication across worktrees and time.
- Do not redesign the store, view, coordinator, migration, rollback, or sidecar boundaries. `julie-extractors` owns extraction and all writes to producer store state; Miller owns reads and derived sidecars.
- User decision 2026-08-10: family-store mode is the default, not opt-in. Unset/blank `MILLER_INDEX_STORE` enables it; explicit `0|false|off|disabled` selects the existing legacy compatibility path, including export-before-serving and no stale fallback.
- Per-view Miller sidecars remain the shipped design. Family-shared vectors and cursor-incremental search/content sidecars are follow-up work, not release blockers or an invitation to redesign v1.18.

## Release direction

- Ship stable Miller v1.18.0 in this working session after clean local, Windows CI, Scale, package, checksum, and marketplace-download gates.
- Push and release/publish approval were explicitly granted by the user on 2026-08-10.
- `julie-extract` 2.31.3 is the required producer pin. Its stable four-platform release was published from commit `4e07f5e9`; it hardens concurrent multi-worktree writer fencing and maintenance recovery.
- Do not modify `/home/murphy/source/julie-extractors`; another session owns it.

## Standing constraints

- No new MCP tools without explicit approval.
- Local semantic retrieval remains default-on/off-switchable and separate from fleet semantics.
- Release source must be clean and intentional; preserve unrelated user changes in the main Miller checkout.

## References

- `docs/plans/2026-08-10-v1.18-default-store-release-plan.md`
- `docs/findings/2026-08-09-index-store-ph3-acceptance.md`
- `docs/findings/2026-08-10-julie-extract-2.31.3-adoption.md`
