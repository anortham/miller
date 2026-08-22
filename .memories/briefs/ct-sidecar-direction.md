---
id: ct-sidecar-direction
title: CT sidecar direction
status: active
created: 2026-08-19T01:19:24.556Z
updated: 2026-08-22T01:19:25.215Z
tags:
  - ct-sidecar
  - direction
  - safety
---

Approved direction, 2026-08-18; refreshed 2026-08-22. (Business strategy lives in the private eros repo; this brief carries only Miller's slice.)

**Direction:** Continuous testing is part of Miller. The daemon is a CLI verb of the main binary, launched detached from a private per-build shadow copy (never locks the install or build output). CT selection IS the `impact` engine; watermark freshness carries green forward; explicit runs retry red cases; auto-runs stay stale-only. Safety spec holds and is verified live: opt-in default off, `MILLER_CT=off` zero-work, one-workspace budget, status starts nothing, no full-suite fallback, loop-stall detection report-only.

**Shipped through 2026-08-22 (HEAD acef33e3, ~88 commits unpushed):** watermark freshness; control-plane honesty (dead daemon, detach, adoption retries); build awareness with numeric direction; loop-stall detection judged by a monotonic-derived published age; shadow-copy daemon; red-retry on explicit runs; store-sidecar reclaim (crash-durable, race-safe, owed-record retries) with 866 MB manually reclaimed; telemetry honesty (7-day window, busiest/slowest, drop signal, one-snapshot reads); inspect/context/impact serve last-good search sidecars; cross-workspace serve-then-refresh; CLI bounded fact reads (byte-identical); context whole-index linkage scan gated off (13.6s → 7.2s CLI). Codex external review: 9 findings resolved. Design rule (user-affirmed twice): query-time reads over whole-set precompute.

**Open items:** user rebuild Release + server restart to serve today's commits, then `tests start`; julie-extract `store maintain retire-view` verb + one-paragraph contract amendment (cross-repo); release planning for the unpushed commits; findings-doc open questions (FindByName bursts, usage-branch tail — now instrumented); context token budget still applied after the work; `daemon.version_match` cannot see a rebuild inside one commit (recorded gap); 4.2 GB store backup awaiting user deletion.

**Standing rules:** new MCP tools need explicit user approval; export contracts (`docs/contracts/cli-eros-v1.md`, `tests-cli-v1.md`, `workspace-status-v1.md`) are the external feed.
