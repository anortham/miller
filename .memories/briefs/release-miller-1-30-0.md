---
id: release-miller-1-30-0
title: Release Miller 1.30.0
status: completed
created: 2026-10-02T15:20:43.716Z
updated: 2026-10-02T15:41:22.695Z
tags: []
---

## Outcome
Stable Miller v1.30.0 published at 2026-10-02T15:38:43Z: https://github.com/anortham/miller/releases/tag/v1.30.0. User explicitly authorized release and necessary pushes in this session. Source/tag 51e6ff576da365b62be31b63597b9843d6b614f1; package validation37026646988 and exact-artifact promotion37028350340 both succeeded. Main and release/v1.30.0 carried the validated source before promotion.

## Evidence
All four public archives and all eight asset digests match GitHub metadata, checksum sidecars and downloaded workflow artifacts. Each extracted payload and semantic manifest passed verification. Public Linux version 1.30.0+51e6ff576da3 / Julie3.7.2 passed native MCP, CT lifecycle and semantic distance-zero smoke. Cold plugin download into isolated cache passed no-folder and separate-project launches, all10tools, clean exits. Full notes match GitHub body byte-for-byte. README and migration guide now describe verified live release. Detailed ledger docs/findings/2026-10-02-v1.30.0-release-verification.md; local evidence ~/.cache/miller-dogfood/release-1.30.0/.

## Boundaries
No existing release overwrite, history rewrite, new MCP tools or unrelated worktree changes. Windows guest is shut off. Older Miller MCP/CT processes must stop before upgrade; existing imported content and original family store are retained during source rebuild. Closeout documentation/memory commit is the only remaining source-control action; production/tag/artifacts remain unchanged.
