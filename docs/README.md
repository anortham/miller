# Miller documentation map

This directory mixes active contracts, current operating docs, historical design notes, and dogfood evidence.
Use this page to avoid treating old milestone plans as the current product contract.

## Current docs

- [`../README.md`](../README.md) - public entry point, install paths, current architecture summary, CLI surface.
- [`../CLAUDE.md`](../CLAUDE.md) / [`../AGENTS.md`](../AGENTS.md) - source-of-truth agent working notes and generated mirror.
- [`contracts/cli-eros-v1.md`](contracts/cli-eros-v1.md) - active Eros-facing CLI/export contract.
- [`contracts/content-corpus-v1.md`](contracts/content-corpus-v1.md) - active content corpus schema/export contract.
- [`release-process.md`](release-process.md) - current release validation and promotion flow.
- [`release-notes/v0.3.0.md`](release-notes/v0.3.0.md) - candidate release notes for the next stable release.
- [`release-notes/v0.2.0.md`](release-notes/v0.2.0.md) - latest published release notes.
- [`findings/2026-06-08-v0.3.0-release-prep-review.md`](findings/2026-06-08-v0.3.0-release-prep-review.md) - current `0.3.0` release-prep review.
- [`search-quality-runner.md`](search-quality-runner.md) - current search-quality comparison runner.
- [`plans/2026-06-04-symbol-search-collapsed-trigram-design.md`](plans/2026-06-04-symbol-search-collapsed-trigram-design.md) - active symbol-search sidecar design history, with current fail-visible behavior noted.
- [`plans/2026-06-07-content-corpus-fts5-search-plan.md`](plans/2026-06-07-content-corpus-fts5-search-plan.md) - implemented content corpus plan; current contract is in `contracts/content-corpus-v1.md`.

## Historical evidence

The `m*-design.md`, `miller-mvp-plan.md`, most dated `docs/plans/`, and dated `docs/findings/` files are
historical design records, implementation plans, and dogfood evidence. Keep them when they explain why a decision
was made, but do not use them as current behavior unless a current doc above links to them for that purpose.

## Cleanup rule

Remove duplicate drafts and abandoned plans when they are unreferenced and actively misleading. Otherwise, prefer a
short historical-status banner over rewriting evidence docs to match today's implementation.
