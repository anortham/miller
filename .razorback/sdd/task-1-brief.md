## Task 1: Make canary assignment govern the full search and add true content retrieval

**Owns:**

- `src/Miller.Server/Tools/SearchTool.cs`
- `src/Miller.Indexing/ITextContentSearchIndex.cs` only if the optional interface belongs beside it
- a new narrow semantic content lookup interface under `src/Miller.Indexing/`
- `src/Miller.Indexing/FtsTextContentSearchIndex.cs`
- focused search/content indexing tests

**Red tests:**

1. An eligible control query with a weak primary result never invokes semantic rescue and matches semantic-off bytes.
2. An eligible shadow query may execute shadow measurement but returns lexical bytes even when semantic ranks a different result.
3. `MILLER_SEMANTIC=shadow` never constructs a serving treatment arm.
4. Content lexical-zero plus a valid semantic chunk returns a materialized hit in treatment.
5. A semantic-only content hit still obeys content-kind and `excludeTests` filters.
6. An index without the optional materializer and every semantic fallback remain lexical byte-identical.

**Implementation:**

- Represent request serving policy once and carry it through `RunSymbolsWithCanary`, the rescue ladder, and content canary execution.
- Permit semantic rescue only for treatment or the existing explicitly non-canary production arm.
- Keep shadow measurement separate from serving output.
- Materialize semantic chunk IDs through the FTS-owned metadata map, union lexical and semantic membership, then use deterministic fusion/tie-breaking.
- Do not widen `ITextContentSearchIndex` for adapters that cannot materialize chunk IDs; prefer a separate optional capability.

**Worker verification:** focused `CanarySearchTests`, `CanaryContentSearchTests`, `SearchToolRescueTests`, and `FtsTextContentSearchIndexTests`.

