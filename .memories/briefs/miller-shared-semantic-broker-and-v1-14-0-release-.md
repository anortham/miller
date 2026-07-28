---
id: miller-shared-semantic-broker-and-v1-14-0-release-
title: Miller shared semantic broker and v1.14.0 release gate
status: active
created: 2026-07-28T02:32:01.919Z
updated: 2026-07-28T09:34:34.853Z
tags:
  - miller
  - semantic-search
  - shared-broker
  - gpu
  - windows
  - release
  - v1.14.0
---

## Goal

Ship Miller semantic retrieval as the default without letting concurrent Miller sessions multiply GPU memory use.

## Why now

The prepared v1.14.0 candidate previously launched one semantic sidecar per Miller process. Default-on in that shape could exhaust a 6 GB Windows NVIDIA GPU and repeat lifecycle risks from Julie's abandoned general daemon.

## Constraints

- Use one user-local, lease-owned `julie-semantic-sidecar` compute broker per compatible protocol/model identity.
- Keep the broker pure compute: frozen protocol-v1 embedding methods only; no workspace, index, database, watcher, HTTP, PID, state, or token control plane.
- Use Unix-domain sockets and a genuinely cancellable Windows named pipe.
- Owner stdin EOF plus a Windows kill-on-close Job Object controls lifetime; deterministic protocol/model identities coexist without restart fights.
- Permit only one user-global accelerator holder. Other model brokers use CPU; runtime GPU resource exhaustion demotes to CPU and retries once.
- `MILLER_SEMANTIC=off` remains a permanent zero-work guarantee; broker failure always falls back to lexical.
- Do not push or release sidecar rc.5 or Miller v1.14.0 without the explicit approval packets in the plan.

## Implemented

- Tasks 1-9 are complete locally on the dedicated Miller and sidecar branches.
- Unset/blank semantic mode defaults to On; explicit Off bypasses broker/path/tool work.
- Same-model Miller sessions share one broker/model load; diagnostics expose owner/non-owner, backend, accelerator lease, reconnect, and degradation state.
- Runtime accelerator exhaustion demotes the broker identity to CPU; Miller refreshes the post-embed health snapshot without protocol desynchronization.
- One-shot CLI status/health can passively observe an existing broker with a 500 ms bound and cannot elect or spawn.
- macOS Apple Silicon rc.5 packaging, broker smoke, Miller all-suite, Release build, and Native-AOT semantic smoke are green. Grok review is GO.

## Remaining release gates

- Fresh approval to push/tag/publish `julie-semantic-sidecar` v0.1.0-rc.5.
- CI production and downloaded verification of all four platform archives and checksums; only the Apple Silicon candidate was produced locally.
- Update Miller `semantic-pins.json` only from those published/downloaded assets, then run Task 10 fleet/hardware gates including Windows sleep/resume and 6 GB NVIDIA VRAM behavior.
- Fresh approval to push/tag/publish Miller v1.14.0 after the release packet is complete.

## Reference

- `docs/plans/2026-07-27-shared-semantic-broker-implementation-plan.md`
- `.razorback/sdd/task-9-report.md`
