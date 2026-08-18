---
id: retire-materialized-resolution-query-time-resoluti
title: "Retire materialized resolution: query-time resolution + fact-store direction"
status: active
created: 2026-08-18T13:50:10.771Z
updated: 2026-08-18T14:05:00.269Z
tags:
  - architecture
  - query-time-resolution
  - direction
---

## Direction (user-approved 2026-08-18, decisions final)

The stack's storage/resolution paradigm changes: the materialized global resolution layer (bases/deltas/rebases/proofs/scope-journal, the resolve subprocess on every save) is retired in favor of query-time resolution over extraction facts. User: "the tool isn't useful at this speed."

**Final decisions (2026-08-18):**
1. **No fallback window.** The old resolution path is removed outright in both repos; julie's phase-2 cleanup follows Miller's phase 1 immediately. No MILLER_RESOLUTION=store mode ships.
2. **Dead-code candidates feature is REMOVED** (CLI `references candidates`, contract retired, dashboard trend counts, history metrics). Dead-code detection is delegated to LSP-class tooling. No whole-graph sweep survives anywhere.
3. Boundary principle (user): julie-extract is a file-local extractor; workspace-global semantics belong to the serving layer. Resolution in julie was scope creep.

## Evidence base

- `docs/findings/2026-08-18-whole-stack-architecture-assessment.md` — structural diagnosis (resolve p95 60–77 s per save batch; ~200x write amplification).
- `docs/findings/2026-08-18-query-time-resolution-spike.md` — spike (branch `prototype/query-time-resolution`): Miller repo 100.000% parity, 475k identifiers; aspnetcore scale 99.9997% parity on 2.15M identifiers with all 7 divergences being the STORED graph under-resolving; refs p95 22 ms / max 286 ms vs 500 ms gate; full-corpus resolve 5.0 s vs producer's ~213 s.
- aspnetcore cold index (current architecture): 14.5 min, 5.3 GB store; first attempt wedged on the julie dual-language-classification bug (`.h` extension-only vs content sniff) — workaround `.julieignore *.h`, root fix owed in julie-extractors (manifest carries extraction's language).

## Design & plan

- `docs/plans/2026-08-18-query-time-resolution-integration-design.md` (decisions folded in).
- Phase 1 (Miller, no julie change): QueryTimeResolver port in Miller.Core, interned RevisionFactCache (identifiers streamed, never resident — naive model hit 2.96 GB at scale, budgets 350/600 MB), read-path swap replacing resolution TEMP views, stop submitting resolve requests, remove dead-code surfaces. Gates: parity fixtures, p95 ≤500 ms at scale, memory ≤350 MB idle, save-to-answer ≤5 s.
- Phase 2 (julie): delete resolution write path + resolve command + tables (schema bump), fix the language-classification bug, release, pin bump.

## Constraints that stand

- Lexical-only output byte-identical; MILLER_SEMANTIC=off zero-work guarantee unchanged.
- Language parity rule for any new extraction facts.
- MCP surface stays stingy; no new tools without approval.
- Do not push/release without explicit approval.
