---
id: dogfood-and-release-miller-1-28-0
title: Dogfood and release Miller 1.28.0
status: active
created: 2026-09-06T15:14:00.897Z
updated: 2026-09-06T15:14:00.897Z
tags:
  - release
  - dogfood
  - windows
  - store
---

## Goal

Dogfood the merged architecture changes, repair release-gating defects, and publish Miller1.28.0 only after required verification.

## Why now

User rebuilt/restarted main68f3fd66 after M2/M4/M5 integration and explicitly approved a Miller release once verified.

## Constraints

Preserve store retention and removed-root safety; never bypass protections or weaken tests. Keep running main Release output untouched; test isolated builds. No paid agent-efficacy campaign. Supporting julie-extract publication needs the pending explicit approval; its2.40.5 candidate remains local. Windows guest restart is no longer needed. Preserve unrelated worktrees/user changes. Reuse green unchanged scopes.

## Success criteria

Live dogfood and Linux/Windows gates pass on source-final changes; published extractor patch is pinned by verified asset hashes; release metadata aligns; package-only validation succeeds, exact artifacts are promoted, release notes and downloaded-asset verification complete. No publication from incomplete or failed gates.

## References

- docs/findings/2026-09-06-v1.28.0-release-verification.md
- docs/release-process.md
- docs/release-notes/v1.28.0.md (candidate, not publishable yet)
- julie-extractors docs/findings/2026-09-06-cursor-command-cost.md

## Status

In progress. Reader-admission diagnostics and batched test fixtures are committed locally. Julie cursor performance/compatibility patch and local release candidate are verified on Linux and focused Windows scopes. Miller Windows full suite found a removed-worktree resurrection race; deterministic repair and broader CT verification are in progress. No pushes or releases.
