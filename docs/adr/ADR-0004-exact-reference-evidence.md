# ADR-0004: Exact reference evidence is a deep indexing module

## Context

The agent-facing reference workflow accepted a symbol name after the caller had already resolved a symbol ID.
That discarded direct identifier targets, resolution overlays, relationships, and resolved pending
relationships, so homonyms were attributed to the wrong definition and duplicate sources appeared as separate
sites.

## Decision

`ReferenceEvidenceReader` owns the SQLite source union, canonical kind mapping, source precedence,
site deduplication, exact and fallback bounds, and fallback safety decision.

Its public interface accepts a resolved symbol ID plus explicit exact and fallback limits. It returns exact and
fallback rows separately with source, resolution status, confidence, tier, truncation, ambiguity, and coverage
facts. Name fallback is returned only for a unique definition and is capped at low confidence. Ambiguous
fallback candidates are counted but never attributed.

Both materialized and on-demand graph loading prefer direct and overlay target IDs, relationships, and pending
resolutions. They use name fallback only when exactly one candidate exists.

## Consequences

Callers no longer need to understand extractor resolution tables or invent their own homonym policy. Later
trace, inspect, context, impact, and rename migrations can share one evidence contract.

The reader performs local SQLite I/O and is tested against real temporary artifacts. `Miller.Core` contains only
the pure result and policy types.

## Applies To

- `src/Miller.Core/References/`
- `src/Miller.Indexing/ReferenceEvidenceReader.cs`
- `src/Miller.Indexing/SymbolGraphReader.cs`
- `src/Miller.Indexing/SqliteSymbolGraphIndex.cs`
- reference consumers in later takeover phases

## Future Agents

Do not reintroduce symbol-name input for symbol-specific reference operations. Preserve exact and fallback
provenance, keep fallback separately bounded, and add new extractor evidence sources inside
`ReferenceEvidenceReader` instead of teaching each tool about SQLite tables.
