---
id: ct-sidecar-direction
title: CT sidecar direction
status: active
created: 2026-08-19T01:19:24.556Z
updated: 2026-08-19T01:19:24.556Z
tags:
  - ct-sidecar
  - direction
  - safety
---

Approved direction, 2026-08-18. (Business-specific strategy context lives in the private eros repo; this brief carries only Miller's slice.)

**Direction:** Continuous testing joins Miller as an **opt-in sidecar binary** (same packaging pattern as the dashboard executable). Rationale: CT's test selection IS the `impact` engine; triggering is the existing watcher/freshness machinery; throttling reuses the supervision discipline (`--jobs` caps, backoff journal, exit-137 handling). The MCP server stays a pure read surface; test status becomes data it can report.

**Safety spec** (from the Eros 2026-07-28 refresh-storm incident): per-workspace opt-in with default off, a global budget (one active CT workspace, bounded provider processes), degraded-index backoff (no enqueue while the index is unhealthy), no full-suite fallback on cold start.

**Surfaces:** CLI verb + JSON export first. A new MCP tool needs explicit user approval per the standing rule.

**Also relevant generically:** Miller's export contracts (`docs/contracts/cli-eros-v1.md`) become the push feed for an external human-facing console; regulated deployments (HIPAA/PHI-class) require on-prem + local models, which Miller already satisfies.

**Next:** brainstorm/design doc for the CT sidecar in this repo.
