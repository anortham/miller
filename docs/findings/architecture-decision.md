# Architecture decision (2026-05-28)

> Historical origin decision. This predates the Miller naming/release state and records why the project avoided
> embeddings/FFI. Use current README, harness docs, and active contracts for current behavior.

Status: **accepted** (supersedes the frozen 2026-01-28 design). Confidence ~89/100.

## Context

codesearch is a daily-use code-intelligence MCP server. North star (non-negotiable): **fast + token-thrifty +
daily-use + pure-.NET** (the user is a long-time C#/.NET dev who dislikes Rust and Python DX). It was frozen
because of (a) embedding hardware-accel pain and (b) a 107 MB UniFFI cdylib that re-imported Rust build pain and
pinned a drifting julie-extractors version. The 2026-05 investigation resolved both. Evidence:
[search-and-storage.md](search-and-storage.md), [embeddings.md](embeddings.md), [cross-language-bridge.md](cross-language-bridge.md).

## Decision

```
┌─ julie-server extract  (Rust, PREBUILT CLI subprocess)  → canonical SQLite      [parsing]
│
├─ C# / .NET 10 HOST  (everything you actually build)
│    • in-memory inverted index (FrozenDictionary postings, ~35 MB, 25 µs ranked)  [lexical search]
│    •   ↳ rebuilt from SQLite at startup (<1 s); SQLite/FTS5 = durable store
│    • deterministic structural cross-reference resolver                            [cross-language bridge]
│    •   ↳ CreateMap / ToDto(this X) sigs / new XDto{} projections   (entity↔DTO)
│    •   ↳ EF DbSet pluralization / ToTable / Dapper FROM            (entity↔table)
│    •   ↳ exact+affix name + typed call(axios<T>/useApi<T>)↔[Route] (TS↔C# DTO)
│    •   ↳ field-set Jaccard = corroborator only
│    • MCP tools (search / navigate / relationships / impact / trace-path / memory)
│
└─ ✗ NO Python   ✗ NO embeddings (in the default path)   ✗ NO GPU/MPS   ✗ NO vectors
   ✗ NO in-process Rust / FFI / cdylib   ✗ NO LanceDB   ✗ NO tantivy
```

### Rationale (each "no" is earned, not assumed)

- **No in-process Rust / cdylib** → shell out to `julie-server extract`. Extraction is batch/index-time; spawn
  cost is irrelevant. Removes the 107 MB cdylib, the per-platform cargo build, and the version-pin drift.
- **No tantivy / LanceDB** → pure-.NET lexical (78 MB / 3.5 ms FTS5, or 35 MB / 25 µs in-memory) is small and
  fast enough at single/multi-repo scale; .NET 10 TensorPrimitives makes brute-force cosine trivial if vectors
  are ever used.
- **No embeddings in the default path** → the bridge (their only justification) is recoverable without them
  across 3 repos; they rescue 0 concepts and would add FPs. The fast embedding path (MPS) has no pure-.NET peer,
  but it no longer matters.
- **No Python** → follows from "no embeddings in the default path." (If opt-in semantic search is ever added,
  reuse julie's existing Python MPS sidecar as a 2nd prebuilt subprocess — never a core dependency.)

### What we build (priority order)

1. **Host scaffold**: `julie-server extract` subprocess wrapper (scan/update/delete) + read-side over the SQLite
   contract; the in-memory inverted index + ranking.
2. **Structural cross-reference resolver** (the differentiator; spec in [cross-language-bridge.md](cross-language-bridge.md)).
   This is load-bearing — name/affix alone only covers the trivial TS↔C# leg.
3. **MCP tools** including a cross-language `trace-path` built on the resolver.

## Open questions (not blockers)

- **Generalization stress test**: one undisciplined-naming polyglot repo (TS names NOT mirroring C#, loose route
  discipline) to fully close the "is the cheap bridge universal" question. Settled for the user's own style.
- **Product scope & name**: julie-replacement (personal daily driver) first; eros-replacement (commercial) is a
  later repackaging of the same engine, not a separate build. Name TBD ("codesearch" is too generic). Tracked in
  the conversation/decision log, not yet decided.

## What is explicitly NOT decided here

The investigation is done; implementation has not started under this decision. The frozen `src/` (Codesearch.Server,
Codesearch.Interop, Codesearch.Embeddings) predates this and assumes the old cdylib/embedding design — expect to
gut Codesearch.Interop (FFI) and Codesearch.Embeddings (default-path embeddings) when building.
