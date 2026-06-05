# Julie Vs Miller Search Quality Matrix

- **Date:** 2026-06-05
- **Purpose:** Start TODO item 15 with live Julie-vs-Miller search-quality evidence before widening
  Miller symbol ranking or merging result kinds.
- **Miller checkout:** `/Users/murphy/source/miller`
- **Julie comparison checkout:** `/Users/murphy/source/julie-head-to-head`
- **Julie comparison commit:** `b13b1d40` (`main`)
- **Safety:** `/Users/murphy/source/julie` was not used for test writes; the comparison used the isolated
  clone above.

## Setup

Miller side:

```bash
/Users/murphy/source/miller/src/Miller.Server/bin/Release/net10.0/miller search <query> \
  --mode <auto|symbol|file|content> --limit 5 --json
```

Run from `/Users/murphy/source/julie-head-to-head`, so Miller reads the comparison clone's
workspace index. Miller also has that clone registered as:

```text
julie-head-to-head-d3b8aedb8554
d3b8aedb85549020e9445c642d3a47a117cdcf7285ad42ab5a5aa5cf3027e1d5
```

Julie side:

```bash
JULIE_HOME=/Users/murphy/source/julie-head-to-head/.julie-matrix-home \
  /Users/murphy/source/julie-head-to-head/target/debug/julie-server \
  --workspace /Users/murphy/source/julie-head-to-head \
  --standalone --json search <query> --limit 5
```

Julie standalone CLI avoids the active daemon and keeps comparison state under the clone. A baseline-only
`cargo xtask search-matrix baseline --profile smoke` probe showed that ablation-free baseline runs only
execute searches; ablation variants are still unsafe without an isolated `JULIE_HOME` because the harness
force-reindexes affected workspaces for non-baseline ablations.

## Smoke Matrix

| Case | Query | Julie result | Miller result | Assessment |
| --- | --- | --- | --- | --- |
| Exact Rust symbol | `WorkspacePool` | Definition first: `src/daemon/workspace_pool.rs:59` `pub struct WorkspacePool`; imports listed as secondary matches. | Initially, `auto`/`symbol` ranked imports first, then the struct at rank 3. Fixed locally: exact-name `import`/`module` rows now receive a low-signal penalty after the exact-name boost, so the struct ranks first while imports remain visible. | Closed for this repro. |
| Exact Rust symbol | `FastSearchTool` | Definition first: `crates/julie-tools/src/search/mod.rs:58` `pub struct FastSearchTool`. | `symbol` ranked the struct first, then re-exports/imports. | Good. The import-ranking issue is not universal. |
| Identifier-looking query | `line_matches` | Definition first: `crates/julie-tools/src/search/query.rs:219` `pub fn line_matches(...)`. | `auto`/`symbol` ranked the function first. `mode=content` instead ranked docs/fixtures above code. | Current Miller guidance is right: identifier-shaped queries should stay in `auto`/`symbol`, not forced into content mode. |
| Scoped content | `line_matches`, `file_pattern=src/ui/**` | Julie returned an out-of-scope hint and then showed full-codebase fallbacks. | Miller has no search-level `file_pattern` filter. | Julie has a useful scoped-search affordance Miller lacks. Track separately from ranking. |
| File lookup, stale Julie fixture path | `src/tools/search/mod.rs` | Current Julie clone no longer has the old file path; Julie found references to that historical string in fixtures/docs. | Miller file mode returned no hits. | The committed Julie smoke case is stale for current `main`; update any reused case to the moved path. |
| File lookup, current path | `crates/julie-tools/src/search/mod.rs` | Julie reported a file definition for the exact file path. | Miller file mode returned module symbols from that file (`backend`, `execution`, `formatting`, ...), not a file-level row. | Miller finds the right file but presents symbol rows instead of a file result. This is usable but less direct than Julie. |

## Initial Takeaways

1. **Closed first Miller fix from this smoke pass:** exact symbol queries now rank concrete definitions
   above import/module rows when names tie. `WorkspacePool` was the repro.
2. **No evidence yet for widening symbol projection.** The `line_matches` case works in `auto`/`symbol`.
   The content miss is mode misuse, not proof that symbol ranking needs doc/comment/literal widening.
3. **File-pattern filtering is a product-surface gap.** Julie's scoped fallback hint is useful for agents.
   Miller currently cannot express the same constraint in `search`.
4. **File lookup result shape could improve.** Miller should consider returning a file-level result for
   exact path queries, or at least label symbol rows as coming from the exact matched file.
5. **Reuse Julie's matrix manifests carefully.** `fixtures/search-quality/search-matrix-cases.toml`
   contains at least one stale path for current Julie `main`, so copied cases need current-path validation.
6. **Closed compact output noise:** compact symbol output now follows Julie's promoted-definition pattern
   for exact non-low-signal hits, groups secondary matches by file path, and abbreviates low-signal
   `import`/`module` signatures as `low_signal`; JSON still carries full signatures for chaining.

## Next Matrix Rows

- Natural-language query that should hit docs/prose.
- Doc-comment query that should route through `mode=content` or opt-in `regions=doc_comment`.
- Literal/env-var query that should route through `regions=string_literal` when region indexing is on.
- Path/basename query on a file whose symbols do not contain the query.
- Bridge-provider query on MyraNext (`trace mode=bridge`) to compare workflow value, not raw search only.

## Candidate Follow-Ups

1. Decide whether Miller beta needs search `file_pattern` / language filters, or whether this remains a
   post-beta parity feature.
2. Decide whether exact path queries should return file-level rows instead of only symbols contained in
   the file.

## WorkspacePool Fix Evidence

- Added `MillerSearchIndexTests.Search_ExactNameDefinitionOutranksImportRows`.
- Added `FtsSymbolSearchIndexTests.Search_ExactNameDefinitionOutranksImportRows_ParityWithInMemory`.
- Implemented shared `Bm25.ApplyExactNameAdjustments`: existing exact-name boost remains, and exact-name
  `import`/`module` rows receive a 0.75 low-signal penalty.
- Dogfood after rebuild:

```text
miller search WorkspacePool --mode auto --limit 5 --json
rank 1: WorkspacePool struct src/daemon/workspace_pool.rs:59
rank 2+: WorkspacePool import rows
```

## Compact Output Noise Fix Evidence

The original compact `FastSearchTool` result repeated the query in every import signature:

```text
FastSearchTool  import  src/tools/mod.rs:31  pub use search::FastSearchTool;
```

Compact output now promotes the definition once, then groups secondary rows by file path without repeating
the queried symbol name:

```text
Definition found: FastSearchTool
  crates/julie-tools/src/search/mod.rs:58 (struct)
  pub struct FastSearchTool

Other matches:

crates/julie-tools/src/lib.rs:26 (import) low_signal

src/tools/mod.rs:31 (import) low_signal

src/cli_tools/generic.rs:
  :147 (import) low_signal
  :335 (import) low_signal
… 25 more (raise limit)
```

JSON remains unchanged and still includes full import signatures.
