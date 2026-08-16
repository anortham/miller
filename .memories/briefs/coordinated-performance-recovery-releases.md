---
id: coordinated-performance-recovery-releases
title: Coordinated performance recovery releases
status: active
created: 2026-08-16T12:16:17.621Z
updated: 2026-08-16T12:16:17.621Z
tags:
  - release
  - performance
  - julie-extract
  - miller
  - windows
  - linux
---

## Goal

Publish the completed performance recovery as coordinated stable releases: `julie-extract 2.33.3`, then Miller `1.19.2` pinned to that exact live producer.

## Sequence

1. Prepare, fully verify, integrate, and publish Julie `2.33.3`.
2. Verify four live Julie archives and capture their SHA-256 values.
3. Fast-forward Miller's recovery branch, pin the live Julie release, prepare Miller `1.19.2`, and run the full Release/fast/Scale/plugin gate.
4. Validate Miller packages, promote the exact successful run, publish notes, verify assets, and reconcile live documentation.

## Decisions

- Both releases are patches because the recovery changes behavior and performance internally without expanding public contracts, schemas, or the nine-tool MCP surface.
- The exact version targets are Julie `2.33.3` and Miller `1.19.2`.
- The dirty Miller primary checkout remains untouched; release work stays in the clean performance-recovery worktree.
- Existing stale v2.33.2 publication wording and diff-check whitespace failures are release-prep defects and will be corrected.
- Cold full indexing remains a documented cost; the release must not claim universal cold-build speed.

## Authority

The user's 2026-08-16 instruction explicitly authorizes merging both recovery branches to main, version bumps, pushes, tags, GitHub releases, Julie pin adoption, and the coordinated Miller release. Overwrites and force pushes are not authorized.

## Reference

`docs/plans/2026-08-16-coordinated-performance-releases-plan.md`
