---
id: release-miller-1-30-0
title: Release Miller 1.30.0
status: active
created: 2026-10-02T15:20:43.716Z
updated: 2026-10-02T15:20:43.716Z
tags: []
---

## Goal
Publish stable Miller 1.30.0 with the completed project-review fixes and Julie 3.7.2 integration. User explicitly authorized release on 2026-10-02, including necessary clean commits, pushes, validation workflow and publication; no existing release overwrite or history rewrite.

## Constraints
Use /home/murphy/source/miller main checkout. Preserve other worktrees. Complete local no-skip native release preflight, aligned manifests and release notes, and existing qualified Linux/Windows test baseline. Use validate-then-promote pipeline, set full release body, verify live assets/version and record evidence. Once version manifests reach main, publish in this same session to avoid broken marketplace downloads.

## Baseline
HEAD 7b086a0c clean, origin/main behind three local commits. C# baseline e725b252: Linux fast 10520/9/0, Scale208/64/0, Windows fast10471/58/0. PowerShell-only correction a05655c2 passed native Windows restores; PowerShell7 verifier skipped unavailable. Report docs/findings/2026-10-02-project-review.md. Older Miller MCP and CT processes must stop before upgrade; migration retains imports and old family data.
