---
id: dogfood-and-release-miller-1-28-0
title: Dogfood and release Miller 1.28.0
status: active
created: 2026-09-06T15:14:00.897Z
updated: 2026-09-06T17:16:43.828Z
tags:
  - release
  - dogfood
  - windows
  - store
---

## Goal

Publish Miller 1.28.0 after verified dogfood repairs, dependency adoption, and package promotion.

## Approval

User explicitly approved Miller release and subsequently approved julie-extract 2.40.5 publication plus Miller pin update. No paid agent campaign, semantic runtime change, tag rewrite, or unrelated work is authorized.

## Verified state

Miller source-final d96a6303 passed Linux fast 10024/9 skipped, Scale 220/24 skipped, isolated Release zero warnings/errors, plugin 82/82, and root/non-repo launcher smokes. Windows full fast passed 9975/58 skipped, zero failures; focused CT 33/33. Later 927c4fa5 is docs/checkpoints only. Julie corrected cursor source and version candidate passed Linux and focused Windows gates.

## Current work

Julie 2.40.5 tagged at9b8503f7, four-platform publication underway. Miller 1.28.0 metadata prepared locally, published pin hashes still needed. Preserve running main Release output, use isolated builds. Live vector lag is downstream of old cursor cost, not a separate embedding fault; verify stable-revision convergence after adoption.

## Completion

Published dependency archives verified and pinned; final adoption checks and Linux/Windows release gates; package-only validation and exact-run promotion; release notes/body and downloaded archives verified. Keep source trees clean and preserve unrelated worktrees. M1–M4 implemented; M5 harness qualified, paid campaign not run; S1 prepared-runtime qualification remains separate.

## Evidence

See docs/findings/2026-09-06-v1.28.0-release-verification.md and docs/release-process.md.
