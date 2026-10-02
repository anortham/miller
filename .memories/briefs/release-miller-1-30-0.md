---
id: release-miller-1-30-0
title: Release Miller 1.30.0
status: completed
created: 2026-10-02T15:20:43.716Z
updated: 2026-10-02T15:50:57.033Z
tags: []
---

## Outcome
Stable Miller v1.30.0 published at 2026-10-02T15:38:43Z: https://github.com/anortham/miller/releases/tag/v1.30.0. User authorized release and necessary pushes. Source/tag51e6ff576da365b62be31b63597b9843d6b614f1; package validation37026646988 and exact-artifact promotion37028350340 succeeded.

## Verification
All four public archives/eight digests match metadata, sidecars and workflow artifacts; payload/runtime manifests pass. Public Linux1.30.0+51e6ff576da3 / Julie3.7.2 passed MCP, CT lifecycle and distance-zero semantic smoke. Cold plugin downloads and no-folder/project launches passed10tools/cleanexits. Full release notes match body; README/migration docs updated and pushed. Final independent CI exposed a restored-binary dependency only in a host-lifecycle test fixture. Fixed test-only at17f64038; focused Linux15/15 and Windows15/15 pass. Binary-free CI37029138882 passes10520tests/9skips/0failures and allfour native SQLite checks. No published payload/tag was changed. Detailed ledger docs/findings/2026-10-02-v1.30.0-release-verification.md.

## Boundaries
No overwrite, history rewrite, new MCP tools or unrelated worktree changes. Stop older resident MCP/CT processes before upgrading; migration preserves imported content and original family store. Native Windows compiler server kept the runner open after tests finished; dotnet build-server shutdown drained it and returned passing output. Guest shutdown requested after verification. Only final evidence/memory commit and push remains.
