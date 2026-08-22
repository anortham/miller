---
id: ct-sidecar-direction
title: CT sidecar direction
status: completed
created: 2026-08-19T01:19:24.556Z
updated: 2026-08-22T23:12:02.793Z
tags:
  - ct-sidecar
  - direction
  - safety
---

Approved direction, 2026-08-18; refreshed 2026-08-22 post v1.21.0 release. (Business strategy lives in the private eros repo; this brief carries only Miller's slice.)

**Direction:** Continuous testing is part of Miller and is now RELEASED (v1.21.0, 2026-08-22, stable, four platform archives + sha256 sidecars, real notes body). The daemon is a CLI verb of the main binary, launched detached from a private per-build shadow copy. CT selection IS the `impact` engine; watermark freshness carries green forward; explicit runs retry red cases; auto-runs stay stale-only. Safety spec holds and is verified live: opt-in default off, `MILLER_CT=off` zero-work, one-workspace budget, status starts nothing, no full-suite fallback, loop-stall detection report-only.

**Shipped in v1.21.0 (origin/main 10596867):** everything previously recorded PLUS: F7 fixed (project-stable `cache/<tool>` build caches beside the generations; two-failure wipe+retry; coverage epoch marker), F9 fixed (per-file node:test junit attribution via the `file` attribute), F10 field-proven (jest green twice on vercel/ms, direct runner invocation — jest upgraded from "supported, not field-proven"), pin julie-extract 2.35.0, and a cross-model review campaign (codex+grok+security, 3 invocations) closed CLEAN with six verified mediums fixed: unacked-run honesty, theory false-green worst-wins fold, sticky-impact poll backoff with visible reason, command-file crash-loop guard, VSTest filter escaping, bounded child-output capture. Evidence: docs/findings/2026-08-22-v1.21.0-review-campaign.md, docs/release-notes/v1.21.0.md. Final gate fast 8,156/0 (8,183) + scale 156/0.

**Open items (all post-release, none urgent):** deferred lows from the review campaign (start-lease wait, stop-identity CAS, mixed-report node fallback gate, empty-selection guards, provider-exception Kind for the wipe streak, execution-blocked generation leak + uncalled ReleaseStaleCtGenerationOwners, sensitive-root in TestsCore.RequireRoot + forbidden-list dedupe, env-inheritance doc note, macOS shadow-copy probe). Pre-existing: F7a (skip rediscovery — needs a replacement inventory-refresh trigger), F11 (`--wait` doesn't wait for budget), F12 (budget-pause temp litter), F13 (CLI paper cuts), run-level spawn-failure reason column (schema+contract). Local machine: Release rebuild + server/CT-daemon restart still owed — running processes predate the six review fixes. Telemetry questions (reference_items/lookup-backend) still need normal-use data.

**Standing rules:** new MCP tools need explicit user approval; export contracts (`docs/contracts/cli-eros-v1.md`, `tests-cli-v1.md`, `workspace-status-v1.md`) are the external feed; external reviews (codex/grok) are now an established release-gate option — no external-model policy block exists in the repo, so each dispatch carries the loud no-policy note.
