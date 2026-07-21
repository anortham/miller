---
id: miller-machine-service-architecture-deferred
title: Miller machine service architecture deferred
status: active
created: 2026-07-20T14:36:43.327Z
updated: 2026-07-20T14:36:43.327Z
tags:
  - architecture
  - machine-service
  - windows
  - semantic
  - deferred
---

## Goal

After the semantic integration program completes, evaluate and plan a demand-started machine-wide Miller service with thin MCP/CLI adapters, dashboard hosting, active/pinned workspace runtimes, and one shared semantic session.

## Direction

- Keep all code, content, history, and vector artifacts per workspace.
- Keep registered workspaces cold unless active or explicitly pinned.
- Keep `miller` as the Native AOT public executable; add a packaged non-AOT service host.
- Make the service disposable and demand-started, never OS-installed or self-updating by default.
- Install immutable versions side-by-side; newest compatible service wins and older clients never downgrade it.
- Preserve kernel writer locks, public MCP/CLI contracts, local-first privacy, and the no-new-MCP-tool approval boundary.

## Blocking follow-up

The shared-service configuration model, especially the exact meaning of `MILLER_SEMANTIC=off`, needs explicit approval and an ADR update before implementation. Protocol-based compatibility and the permanent direct-mode decision also remain open.

## Reference

- `docs/plans/2026-07-20-miller-machine-service-design.md`

## Status

Draft awaiting review. Do not start implementation or alter the active semantic program from this brief.
