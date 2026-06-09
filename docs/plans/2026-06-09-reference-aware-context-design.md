# Reference-Aware Context Design

Date: 2026-06-09
Status: Implemented in `codex/miller-data-opportunities`
Related plan: `docs/plans/2026-06-09-miller-data-opportunities-plan.md`

## Problem

The current `context` tool builds a token-budgeted bundle from symbol search seeds plus graph neighbors. That is useful for orientation, but it often stops at definitions and nearby graph nodes. It does not explain why a symbol matters in practice, where it is referenced, what code chunk contains it, or which calls around the target are most useful for an agent task.

Miller already has richer local evidence:

- Symbol definitions through the in-memory index and `SmartTargetResolver`.
- Name-based identifier rows through `ExtractReader.ReadReferences`.
- One-hop call identifiers from `ExtractReader.ReadCallees`.
- Content corpus chunks with `containing_symbol_id` and `containing_symbol_name`.
- Dependency graph reachability through `ISymbolGraphReachability`.

The first implementation should use those facts to add an opt-in reference-aware mode while keeping the default `context` behavior unchanged.

## Goals

- Preserve the current default `context` output and performance.
- Add an opt-in mode that returns a bounded mix of definitions, possible references, calls, graph neighbors, and containing content chunks.
- Label why every returned item was included.
- Be honest about confidence. Current identifier references are name-based, so they must not be presented as exact target-symbol references.
- Keep `Miller.Core` pure. Storage reads stay in `Miller.Indexing` and server/tool seams.
- Keep output small enough for agent use. Better selection is the goal, not a larger bundle.

## Non-Goals

- Do not add embeddings or semantic ranking.
- Do not change extractor contracts in this slice.
- Do not make name-based references look exact.
- Do not broaden symbol search ranking with source text, comments, or literals. Content corpus search remains the text surface.
- Do not add dashboard or Eros-specific UI. Eros can consume the JSON shape later if it is useful.

## Proposed Surface

Add an opt-in parameter to the MCP and CLI `context` surfaces:

```text
reference_mode = "off" | "usage"
```

Default: `off`.

`off` keeps existing behavior.

`usage` enriches the bundle with reference-aware evidence around the selected seeds.

Also add a bounded depth parameter:

```text
reference_depth = 1
```

Initial allowed range: `0..1`.

- `0`: include seed definitions and containing content chunks only.
- `1`: include seed definitions, possible name references, one-hop calls, graph neighbors, and containing content chunks.

Add an explicit test filter:

```text
exclude_tests = false
```

Default: `false`, matching current `context` behavior where graph expansion may include test symbols.

When `reference_mode = usage` and `exclude_tests = true`, filter test symbols, identifier rows in test paths, and content chunks marked as tests. This keeps the enriched mode useful for production-code orientation without silently changing current context output.

Reason for this surface:

- A mode is clearer than several booleans.
- It keeps existing calls stable.
- It leaves room for future modes such as `thread` or `callers` without overloading the first slice.

## Data Accuracy

The earlier opportunity note mentioned `identifiers.target_symbol_id`. Current Miller code should not depend on that as the primary reference path. `ExtractReader` documents and implements name-based reference reads because extracted identifier target ids are not reliable for current use.

This means reference-aware context should classify identifier rows as:

- `definition`: exact selected indexed symbol.
- `possible_reference`: an identifier row whose `name` matches the selected symbol name.
- `callee_identifier`: a `kind = call` identifier inside the selected symbol body, using `containing_symbol_id`.
- `graph_neighbor`: a dependency graph neighbor reached from selected symbols.
- `containing_chunk`: a content corpus chunk attached to a selected or related symbol by `containing_symbol_id` or `containing_symbol_name`.

Compact and JSON output must expose the reason. JSON should also expose confidence:

- `exact` for selected definitions and exact symbol-id content chunks.
- `containing_symbol` for rows tied to a containing symbol id.
- `name_based` for possible references.

## Architecture Quality Gate

Affected modules:

- `src/Miller.Server/Tools/ContextTool.cs`: parse the new mode/depth parameters, route to the enriched runner, render compact and JSON output.
- `src/Miller.Indexing/ExtractReader.cs`: reuse current identifier reads; add small bounded readers only if existing methods cannot express the required query.
- `src/Miller.Indexing/*Content*`: add a narrow content chunk reader by containing symbol id/name if the current search interface cannot retrieve chunks without FTS query guessing.
- `src/Miller.Core/Graph/ContextPacker.cs`: keep unchanged unless an item-agnostic packing helper is needed.
- `tests/Miller.Tests/Server/ContextToolTests.cs`: pin default compatibility and new reference-aware behavior.

Caller-facing interface:

- Existing callers are unaffected because `reference_mode` defaults to `off`.
- JSON callers still get the existing `bundle` fields for symbol rows.
- In `usage` mode, JSON rows add `item_type`, `reason`, and `confidence`.
- Test filtering is explicit through `exclude_tests`; no existing context call starts hiding tests.

Locality:

- The current pure `ContextTool.Run(...)` core should remain available for existing tests and default behavior.
- The enriched runner can live in the server layer because it must touch workspace DB paths and content corpus reads.

Seams and adapters:

- Do not hydrate full indexes beyond what `context` already resolves.
- Use `IWorkspaceIndexProvider` for symbol index and resolver.
- Use `IWorkspaceTextContentSearchProvider` or a new narrow content corpus read seam for content chunks.
- Reuse existing test-path/test-chunk filtering helpers where possible instead of adding another path heuristic.
- Keep SQLite access in `Miller.Indexing`, not `Miller.Server` rendering code.

Rejected shortcuts:

- Do not fake exact references from name matches.
- Do not search all source text and call the top hits "references".
- Do not increase the default token budget to hide noisy ranking.
- Do not add a public JSON shape that mirrors private graph internals.

Risk:

- Name collisions can make possible references noisy. Mitigation: label them `name_based`, cap them per seed, and prefer rows whose containing symbol is already in the candidate set.
- Content chunks can dominate the budget. Mitigation: cap chunks per symbol and pack all item types through one token budget.
- Cross-language coverage may vary. Mitigation: use existing contract data only and degrade gracefully when a language lacks a fact.

## Data Flow

1. Resolve seeds exactly as current `context` does:
   - BM25 symbol search from `query`.
   - Resolved `entry_symbols`.
   - Identifier tokens from `failing_test` and `stack_trace`.
2. Build the current symbol candidate list with hop distance.
3. If `reference_mode = off`, return the current result unchanged.
4. If `reference_mode = usage`, build enriched items:
   - Add seed and selected graph symbols as `definition` items.
   - Read bounded `ReadReferences(indexDbPath, symbol.Name)` rows for seed symbols and classify them as `possible_reference`.
   - Read bounded `ReadCallees(indexDbPath, symbol.SymbolId)` rows for seed symbols and classify them as `callee_identifier`.
   - Read bounded content chunks for seed and selected graph symbols by containing symbol id/name and classify them as `containing_chunk`.
   - Add graph neighbors with direction metadata where available and classify them as `graph_neighbor`.
   - Apply `exclude_tests` before ranking if requested.
5. Dedupe by stable identity:
   - Symbols: `symbol_id`.
   - Identifier rows: `file_path + start_line + name + kind + containing_symbol_id`.
   - Content chunks: `source_id + chunk_id`.
6. Rank deterministically:
   - Seed definitions.
   - Exact containing-symbol chunks for seeds.
   - Callee identifiers inside seeds.
   - Possible references where containing symbol is selected.
   - Graph neighbors.
   - Remaining possible references.
7. Pack through `token_budget`.

## Output Shape

Compact mode should stay terse and grouped by file where possible:

```text
# context bundle (8)
src/Miller.Server/Tools/ContextTool.cs:
  :23 ContextTool class reason=definition confidence=exact hop=0
  :63 Context method reason=definition confidence=exact hop=0
  :118 Search reason=callee_identifier confidence=containing_symbol
tests/Miller.Tests/Server/ContextToolTests.cs:
  :41 Context_WithEntrySymbols_SeedsBundle reason=possible_reference confidence=name_based
```

JSON mode should keep the current symbol fields and add fields only where relevant:

```json
{
  "bundle": [
    {
      "item_type": "symbol",
      "reason": "definition",
      "confidence": "exact",
      "name": "ContextTool",
      "kind": "class",
      "file": "src/Miller.Server/Tools/ContextTool.cs",
      "line": 23,
      "hop": 0,
      "signature": "public sealed partial class ContextTool",
      "symbol_id": "..."
    },
    {
      "item_type": "identifier",
      "reason": "possible_reference",
      "confidence": "name_based",
      "name": "ContextTool",
      "kind": "reference",
      "file": "tests/Miller.Tests/Server/ContextToolTests.cs",
      "line": 41,
      "containing_symbol_id": "..."
    }
  ]
}
```

The JSON contract for this mode should be documented in the tool guidance or a small contract note only after the implementation shape is pinned by tests.

## Test Plan

Fast tests:

- Existing default `context` tests still pass without changed output.
- `reference_mode = usage` includes reason and confidence fields in compact output.
- `format=json` preserves existing symbol fields and adds `item_type`, `reason`, and `confidence`.
- Name-based references are labeled `possible_reference` and `name_based`.
- Duplicate identifier rows and duplicate content chunks are deduped.
- Ambiguous `entry_symbols` do not crash and do not invent a seed.
- Missing reference data returns definitions only, not an error.
- `reference_depth` clamps or rejects out-of-range values consistently with existing tool style.
- `token_budget` caps enriched items, including chunks.
- `exclude_tests = false` can include test symbols/references, matching current `context` behavior.
- `exclude_tests = true` filters test symbols, identifier rows in test paths, and test content chunks in enriched mode.

Scale or dogfood tests:

- Run one real Miller workspace query where current `context` is thin and `reference_mode = usage` returns at least one useful reference or containing chunk.
- Verify the output labels the evidence source clearly enough that an agent can decide whether to trust it.

## Acceptance Criteria

- [x] Default `context` behavior and output are unchanged.
- [x] Enriched mode is opt-in, bounded, and deterministic.
- [x] Every enriched item has `reason` and `confidence`.
- [x] Name-based rows are never labeled as exact references.
- [x] Implementation keeps `Miller.Core` pure.
- [x] Tests cover duplicate references, missing content data, ambiguity, test-file behavior, and token budget enforcement.
- [x] At least one dogfood query is documented after implementation.

## Dogfood Evidence

Command:

```bash
dotnet run --project src/Miller.Server/Miller.Server.csproj --no-build -- context "ContextTool RunReferenceAware" --reference-mode usage --max-hops 0 --token-budget 3000 --json
```

Summary from the JSON bundle:

- `count`: 43
- reasons: `definition=10`, `containing_chunk=1`, `callee_identifier=14`, `possible_reference=18`
- confidence: `exact=11`, `containing_symbol=14`, `name_based=18`
- content chunk sample: `src/Miller.Server/Hosting/IndexerService.cs:281`, `reason=containing_chunk`, `confidence=exact`

## Implementation Sequence

1. Add tests that pin default behavior and describe the new `usage` mode.
2. Add a small enriched item model in the server/tool area.
3. Add or reuse bounded indexing readers for identifier references, callees, and content chunks.
4. Wire `ContextTool.Context(...)` parameters and keep current `Run(...)` for default mode.
5. Render compact and JSON enriched output.
6. Run focused tests, fast suite, build, diff check, and guidance sync check.
7. Dogfood one live query and record the result in this plan or a follow-up evidence note.
