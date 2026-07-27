---
id: cross-repo-miller-dogfood-contract-repair
title: Miller and producer dogfood contract repair
status: completed
created: 2026-07-26T20:24:11.878Z
updated: 2026-07-27T01:48:52.734Z
tags:
  - miller
  - julie-extractors
  - semantic-sidecar
  - dogfood
  - contracts
  - semantic-search
---

## Goal

Make Miller and its shipping dependencies (`julie-extractors` / `julie-extract` and the pinned `julie-semantic-sidecar`) correct, efficient, and fully dogfooded under one intentional breaking contract cut.

## Priority

Miller and its direct producers were the only critical path. Eros remains unreleased and downstream; repair Eros only after the final Miller contracts are proven.

## Landed contract

- `julie-extract` 2.18.0 owns schema 5 / extract contract 4 / JSONL 4, source-attested reference sites, closed `open | exception` gaps, parser-backed marker facts, and complete mutable-FK indexes.
- Miller requires those exact contracts and exposes nine strict, lossless, bounded MCP tools plus schema-2 Patterns and References process contracts.
- Semantic policy version 2 separates routing from admission, preserves lexical-only identity and one-hit protection, and improves open-set recall/nDCG without a guessed cosine threshold.
- Test fixtures share the current schema builder; no v1 scale-fixture compatibility lane remains.

## Verification

- Julie HEAD `500416af`: fmt/check/workspace tests/doctests green; extractor 3,021 passed and 7 ignored.
- Miller HEAD `7a1512aa`: Release build clean; fast 5,121 passed/2 skipped; Scale 92 passed/3 optional-runtime skips.
- Full rebuild: schema 5 / contract 4, 422,081 sites, 70 open gaps, zero missing/orphan/scope-conflicting reference evidence.
- Converged nine-tool MCP matrix: zero errors, max 7,055 bytes, zero results above 12 KiB.
- Verification: `docs/findings/2026-07-26-cross-repo-dogfood-contract-repair-verification.md`.

## Remaining external gate

The user-owned sealed semantic set was unavailable. No sealed query text was inspected or tuned against. This does not reopen the implemented Miller/producer contract; run the sealed gate when the set is supplied.

## Downstream

Eros compatibility is a separate follow-up and may currently be broken. No compatibility adapters were added.

## Constraints retained

- Keep nine MCP tools; no new tool.
- Unknown schema-5 vocabulary is a refusal, not a fallback.
- No push, merge, release, or publish occurred.
