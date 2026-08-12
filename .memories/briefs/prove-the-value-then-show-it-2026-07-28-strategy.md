---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: completed
created: 2026-07-29T00:13:39.439Z
updated: 2026-08-12T13:52:05.214Z
tags:
  - strategy
  - performance
  - family-store
  - release-blocker
  - overnight
---

## Outcome

Miller's family-store performance recovery was proved with exact dogfood and published as stable `v1.18.2`; the coordinated producer improvements were published as stable `julie-extract v2.32.1`.

## Verified release

- Miller source/tag `c49dc3712ad81ed359236e433c7eaf63d0f04197`.
- Four-target package-only workflow `31602463908` passed; exact-artifact promotion `31603272634` passed.
- Public archives and checksum sidecars verified from fresh downloads.
- Bundled Linux package reports Miller `1.18.2+c49dc3712ad8`, `julie-extract 2.32.1`, and semantic sidecar `0.1.0`.
- Final candidate measurements: inspect 254.855 ms, context 1,938.450 ms, impact 1,260.196 ms, trace 145.721 ms; exact output preserved.
- Fast gate 6,439/4 skipped; scale gate 138/5 skipped; no failures.

## Follow-ups outside this completed release

- PERF-010 registry/open isolation still needs a supported registry override; shared registry mutation and HOME repurposing remain prohibited.
- PERF-009 bridge-mode process dogfood remains unmeasured, while ordinary trace acceptance passed.
