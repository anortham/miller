---
id: explicit-workspace-targeting-for-user-level-mcp
title: Explicit workspace targeting for user-level MCP
status: completed
created: 2026-08-30T23:34:37.687Z
updated: 2026-08-31T02:50:11.181Z
tags:
  - mcp
  - workspace
  - stateless
  - gui
  - architecture
---

## Goal

Make one user-level Miller MCP registration safe across Codex, Cursor, VS Code, and other GUI clients by requiring an explicit workspace selector on every workspace-bound MCP call.

## Why now

Process cwd and MCP Roots cannot identify the project for a long-lived GUI server. MCP 2026-07-28 is stateless and deprecates Roots, so the target must travel with each request.

## Constraints

- Keep CLI cwd/current behavior unchanged.
- Add no MCP tool.
- Registry list/open bootstrap the workspace ID.
- Mutations never guess between aliases.
- Unbound processes must serve registered targets without constructing primary-only dependencies.
- Keep the startup primary only for indexing, watching, and vector convergence, never target selection.
- Implement against ModelContextProtocol 1.4.0; leave the prerelease SDK v2 migration separate.

## Success criteria

One server process safely serves several workspace IDs; edit locks and converges the named target; list/open work without a primary; no MCP call requests Roots; schemas, diagnostics, docs, fast/build/scale, and focused Windows verification pass.

## Reference

- docs/plans/2026-08-30-stateless-workspace-targeting-design.md
- Linear BRE-57
