---
id: miller-full-review-and-latest-julie-integration
title: Miller full review and latest Julie integration
status: completed
created: 2026-10-02T14:02:53.072Z
updated: 2026-10-02T15:17:28.389Z
tags: []
---

## Outcome

Completed the full project review and fixed every confirmed actionable finding. Latest published Julie v3.7.2 is pinned and restored; all 42 extractor languages were verified on the real upstream fixture corpus. Julie 3 removed store/export commands, so Miller reuses its standalone artifact path with forced promotion, exact family identity validation, preservation of imports, and pointer retirement last. Older MCP servers and CT daemons must be stopped before switching builds; no mixed-version migration guarantee.

## Verification

Release build: zero warnings/errors. Linux fast: 10,520 passed, 9 skipped; Scale: 208 passed, 64 skipped. Plugin: 104 passed. Python: 436 tests, 3 skips. Windows fast: 10,471 passed, 58 skipped; plugin: 97 passed, 7 skipped. Zero failures. Native Windows PowerShell restored both runtimes from clean a05655c2 with preloaded archive module and preserved caller settings. PowerShell 7 verifier skipped because that toolchain is unavailable. Live legacy migration, CLI/MCP, dashboard HTTP smoke, and dependency vulnerability audit passed.

## Evidence and boundaries

See docs/findings/2026-10-02-project-review.md, its committed language matrix and smoke JSON, and docs/julie-3-migration.md. Task used /home/murphy/source/miller on main; unrelated worktrees were preserved. Changes committed locally; no push, release, deploy, new MCP tools, or destructive store cleanup. Windows test guest shut down after verification.
