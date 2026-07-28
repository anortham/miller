---
id: miller-shared-semantic-broker-and-v1-14-0-release-
title: Miller shared semantic broker and v1.14.0 release gate
status: active
created: 2026-07-28T02:32:01.919Z
updated: 2026-07-28T02:32:01.919Z
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

The prepared v1.14.0 candidate still launches one semantic sidecar per Miller process. Releasing default-on in that shape can exhaust a 6 GB Windows NVIDIA GPU and would repeat lifecycle risks from Julie's abandoned general daemon.

## Constraints

- Build one user-local, lease-owned `julie-semantic-sidecar` compute broker shared by compatible Miller sessions.
- Keep the broker pure compute: frozen protocol-v1 embedding methods only; no workspace, index, database, watcher, HTTP, PID, state, or token control plane.
- Use Unix-domain sockets and a genuinely cancellable Windows named pipe.
- Owner stdin EOF plus a Windows kill-on-close Job Object controls lifetime; deterministic protocol/model identities coexist without restart fights.
- Permit only one user-global accelerator holder. Other model brokers use CPU; runtime GPU resource exhaustion demotes to CPU and retries once.
- `MILLER_SEMANTIC=off` remains a permanent zero-work guarantee; broker failure always falls back to lexical.
- Do not push or release sidecar rc.5 or Miller v1.14.0 without the explicit approval packets in the plan.

## Success criteria

Concurrent same-model sessions load one model once, aggregate VRAM stays near one-session use, owner/client/broker death is bounded and leaves no orphan, Windows cancellation and 6 GB NVIDIA behavior pass real hardware soak, semantic retrieval is on when unset, and lexical output stays byte-identical on abstention/failure.

## Status

The implementation plan is complete; implementation has not started. Miller v1.14.0 release remains paused.

## Reference

- `docs/plans/2026-07-27-shared-semantic-broker-implementation-plan.md`
