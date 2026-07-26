---
id: cross-repo-miller-dogfood-contract-repair
title: Cross-repo Miller dogfood contract repair
status: active
created: 2026-07-26T20:24:11.878Z
updated: 2026-07-26T20:24:11.878Z
tags:
  - miller
  - julie-extractors
  - eros
  - dogfood
  - contracts
  - semantic-search
---

## Goal

Fix the structural contract defects found by latest Miller dogfooding across `julie-extractors`, Miller, and unreleased Eros in one breaking cut.

## Why now

Three days of testing still exposed producer identity, marker extraction, continuation, output-envelope, semantic admission, and exact Eros consumer defects. All repositories are owned and Eros is unreleased, so compatibility must not dilute the correct fix.

## Constraints

- `julie-extractors` owns parser-backed reference spans and marker facts.
- Miller owns schema gates, bounded agent surfaces, continuations, local semantics, and exports.
- Eros updates atomically to the new public contracts; no adapters or version ranges.
- Keep nine MCP tools; no new tool.
- Preserve lexical-only byte identity and `MILLER_SEMANTIC=off` zero work.
- No push, merge, release, or publish without separate approval.

## Success criteria

Schema 5 / extract 4 / JSONL 4 land together; reference sites are source-attested; marker and gap invariants are exact; every MCP response is bounded and losslessly continuable; semantic routing and admission use policy version 2; Eros consumes the replacement schema exactly; focused and branch gates pass in all three repos.

## References

- `docs/plans/2026-07-26-cross-repo-dogfood-contract-repair-design.md`
- `docs/plans/2026-07-26-cross-repo-dogfood-contract-repair-implementation.md`
