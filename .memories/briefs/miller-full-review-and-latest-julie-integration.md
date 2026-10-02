---
id: miller-full-review-and-latest-julie-integration
title: Miller full review and latest Julie integration
status: active
created: 2026-10-02T14:02:53.072Z
updated: 2026-10-02T14:02:53.072Z
tags: []
---

## Goal

Keep Miller supported for users who prefer it to code-kb: review the project, fix confirmed defects, and verify working workflows. User additionally requires latest published julie-extractors.

## Constraints

No new MCP tools, pushes, releases, destructive store changes, or changes to unrelated worktrees. Work stays in clean-start main checkout /home/murphy/source/miller at 3ed3920b. Preserve external/web imports and existing store data during migration; failed rebuild must retain the workspace pointer.

## Direction

GitHub latest Julie is v3.7.2, while Miller pins 2.42.0. Astra confirmed Julie v3 removed the store and export commands. A pin-only bump is broken. Reuse Miller's existing standalone SQLite path and RequiresSourceRebuild rollback handling; make standalone the supported default, refuse unsupported store opt-in clearly, and remove old pointer only after successful forced promotion and preservation of imports. Historical store default guidance must be updated. Do not revive the removed upstream store implementation.

## Success criteria

Current pins/restored binaries, fresh and existing workspace migrations, failed-migration preservation, focused regression checks, zero-warning Release build, fast/Scale/plugin checks, and real CLI/MCP smoke. Windows checks where available. Review concrete findings with Astra; preserve user work.
