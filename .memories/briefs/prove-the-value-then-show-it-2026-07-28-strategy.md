---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: active
created: 2026-07-29T00:13:39.439Z
updated: 2026-08-12T12:36:15.367Z
tags:
  - strategy
  - performance
  - family-store
  - release-blocker
  - overnight
---

## Direction

Miller replaces the retired Julie agent-tool core. The family-store interactive read recovery is now accepted locally; release preparation continues across Miller and julie-extractors.

## Verified Miller state

- Final candidate `e46e72e2`: warm inspect 254.855 ms, context 1,938.450 ms, impact 1,260.196 ms, trace 145.721 ms.
- Context peak 151,516 KB PSS; post-read 3 s idle peak 161,214 KB PSS. Outputs remained exact.
- Fast branch gate: 6,439 passed, 4 skipped. Scale gate: 138 passed, 5 skipped. Zero failures and zero build warnings/errors.
- PERF-001/002/003/011 and the consumer-side portions of PERF-004/005 are accepted.

## Julie state to integrate

- `main` includes writer heartbeat, scope crossover, clean replay evidence, and patch-equivalent artifact retry `70cd205f`.
- `perf/store-resolution-query-amplification` adds diagnostic history and the accepted cached exact-writer fix `ab3aa957`; faithful resolution improved 49.98 s → 43.10 s with exact output.
- Prepare the local v2.32.1 candidate only after integrating that branch; no publication claim.

## Remaining work

- Commit the Miller gate/docs evidence and reconcile worktrees.
- Integrate Julie performance history into `release/2.32.1`, update release prep metadata/notes, and run its declared local gates.
- Prepare Miller v1.18.2 metadata/notes and local package/security/plugin/site gates after Julie candidate state is settled.
- PERF-010 registry/open isolation remains separate: no supported registry override exists, and shared registry mutation/HOME repurposing is prohibited.
- PERF-009 bridge-mode process dogfood remains unmeasured but ordinary trace is accepted.

## Approval boundary

Do not push, tag, publish, deploy, advertise marketplace versions, or create releases without explicit user approval.
