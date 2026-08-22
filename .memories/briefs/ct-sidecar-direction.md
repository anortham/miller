---
id: ct-sidecar-direction
title: CT sidecar direction
status: active
created: 2026-08-19T01:19:24.556Z
updated: 2026-08-22T04:21:01.926Z
tags:
  - ct-sidecar
  - direction
  - safety
---

Approved direction, 2026-08-18; refreshed 2026-08-22 (post retire-view + fix batch 2). (Business strategy lives in the private eros repo; this brief carries only Miller's slice.)

**Direction:** Continuous testing is part of Miller. The daemon is a CLI verb of the main binary, launched detached from a private per-build shadow copy (never locks the install or build output). CT selection IS the `impact` engine; watermark freshness carries green forward; explicit runs retry red cases; auto-runs stay stale-only. Safety spec holds and is verified live: opt-in default off, `MILLER_CT=off` zero-work, one-workspace budget, status starts nothing, no full-suite fallback, loop-stall detection report-only.

**Shipped through 2026-08-22 (origin/main efcac1ca):** everything previously recorded, PLUS: CT dogfood fix batch 2 — F3 (one pytest project per config dir, pytest's own priority), F4 (proven cargo workspace members dropped, fixtures/__fixtures__/testdata pruned), F5 (`tests disable` reports what it turned off; JSON adds changed_count/changed_projects), F8 (node-test discovery follows Node's verbatim default patterns; script positional paths replace defaults), F10 (chained/fragment/positional-arg scripts refused, direct runner-binary invocation, visible spawn-failure reasons — never a false red). julie-extractors v2.35.0 released (retire-view verb, evidence + closeout clean) and Miller pinned to it across the full lockstep set (pins JSON, compiled constant, three test literals, THIRD-PARTY-NOTICES; adoption finding 2026-08-22). Gates: fast 8,100/1-known-flake, scale 154/0 at the new pin.

**Open items:** v1.21.0 release — notes → prep commit → publish, BOTH need explicit user approval (prep push is a live marketplace event); release plan should now say pin 2.35.0. F7 (fresh generation rebuilt per explicit run) needs its own design pass. F9 (partial-red node-test attribution) needs a discriminator repo. F10-lane follow-up: daemon auto-run spawn reasons need a ct.db run-level reason column to reach `tests status`. Server + CT daemon predate today's commits — next Release rebuild + restart serves them (leader runs the 2.35.0 upgrade rescan by design); the reference_items/lookup-backend telemetry questions still need normal-use data. jest remains supported-not-field-proven until a vercel/ms re-run under the F10 fix.

**Standing rules:** new MCP tools need explicit user approval; export contracts (`docs/contracts/cli-eros-v1.md`, `tests-cli-v1.md`, `workspace-status-v1.md`) are the external feed.
