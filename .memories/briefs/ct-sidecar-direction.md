---
id: ct-sidecar-direction
title: CT sidecar direction
status: active
created: 2026-08-19T01:19:24.556Z
updated: 2026-08-21T15:08:38.856Z
tags:
  - ct-sidecar
  - direction
  - safety
---

Approved direction, 2026-08-18; refreshed 2026-08-21 after the watermark + control-plane + build-awareness work landed. (Business strategy context lives in the private eros repo; this brief carries only Miller's slice.)

**Direction:** Continuous testing is part of Miller. The daemon is a CLI verb of the main binary (`miller tests serve`) — separate process, same executable, no new packaging (recorded decision in `docs/plans/2026-08-18-ct-sidecar-migration-design.md`; this supersedes the earlier "sidecar binary" packaging idea). CT's test selection IS the `impact` engine; triggering is the watcher/freshness machinery; throttling reuses the supervision discipline.

**Safety spec (holds, verified live):** per-workspace opt-in with default off, `MILLER_CT=off` zero-work kill switch, one-workspace execution budget, status reads start nothing, no full-suite fallback (unknown impact = everything stale, nothing executes).

**Shipped and verified on the live repo (through commit 211eee34):**
- Watermark freshness: green results ride revision advances forward; a one-line edit selected 7 of 7,835 cases (~1,100x reduction).
- Control-plane honesty: dead daemon reads stopped, detach never resurrects a removed worktree, lost attach records retry.
- Build awareness: `CtDaemonVersion` — sameness is the whole build string, direction is numeric; explicit start replaces an older daemon; ping-pong gate proven live from both sides.
- The tenth MCP tool `tests` (approved 2026-08-18). JSON contract: `docs/contracts/tests-cli-v1.md`.

**Open items:** hung-daemon detection (live pid, frozen heartbeat still reads running); stale-backlog re-baseline after the identity flip; retired dogfood views still listed as family-store members; a release that ships the CT work (v1.20.1 is tagged, ~88 commits ahead).

**Standing rules:** new MCP tools need explicit user approval; export contracts (`docs/contracts/cli-eros-v1.md`, `tests-cli-v1.md`) are the external feed.
