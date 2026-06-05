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
| Scoped content | `line_matches`, `file_pattern=src/ui/**` | Julie returned an out-of-scope hint and then showed full-codebase fallbacks. | Initially, Miller had no search-level `file_pattern` filter. Fixed locally: `search` now accepts `file_pattern=<glob>` and `language=<lang>` across symbol, content, and region result kinds. | Closed for first beta parity. Miller narrows results after ranking while preserving in-scope rank order. |
| File lookup, stale Julie fixture path | `src/tools/search/mod.rs` | Current Julie clone no longer has the old file path; Julie found references to that historical string in fixtures/docs. | Miller file mode returned no hits. | The committed Julie smoke case is stale for current `main`; update any reused case to the moved path. |
| File lookup, current path | `crates/julie-tools/src/search/mod.rs` | Julie reported a file definition for the exact file path. | Initially, Miller file mode returned module symbols from that file (`backend`, `execution`, `formatting`, ...), not a file-level row. Fixed locally: compact file mode now starts with `File match: crates/julie-tools/src/search/mod.rs` and then lists representative `:line name kind` rows. | Closed for compact output shape. JSON remains symbol-row based for compatibility. |
| Path/basename lookup | `mod.rs` | Not rerun in Julie for this slice. | Initially, Miller spent the page on symbols from the first matched file. Fixed locally: path-fragment lookup now returns one representative symbol per matched file before extra symbols, and compact file mode renders `File matches:` grouped by file. | Closed for basename discovery. This is still not a replacement for Julie-style `file_pattern` scoped search. |
| Natural-language docs/prose | `line mode scoped filter miss fixed` | Not rerun in Julie for this slice. | `mode=content` returns `docs/release-notes/v7.13.1.md` first with the relevant scoped-filter paragraph. `auto` intentionally stays in symbol/property search for code-shaped results. | Good. This supports current guidance: use `mode=content` for docs/prose; do not widen symbol ranking for this workflow. |
| Natural-language eval/prose | `where does fast search dispatch between lexical semantic and hybrid backends` | Not rerun in Julie for this slice. | `mode=content` returns semantic eval result/scorecard docs carrying the exact query and expected files. `auto` returns scorecard properties, which is less useful for prose intent but expected for symbol search. | Good mode split. User-facing guidance should continue steering prose to `mode=content`. |
| Doc-comment query | `canonicalized to prevent duplicate workspace IDs` | Not rerun in Julie for this slice. | `regions=doc_comment` returns `region search requires MILLER_REGION_INDEX=1 and a refreshed search sidecar.` `mode=content` searches docs/prose only and does not search source comments; `auto` returns code symbols such as `canonicalized`. | Deferred to source-region follow-up. This is not evidence to widen symbol ranking for beta. |
| Literal/env-var query | `JULIE_EMBEDDING_HOST_SPAWN_TIMEOUT_SECS` | Not rerun in Julie for this slice. | `regions=string_literal` returns the same region-index unavailable message. `auto` finds nearby source symbols such as `DEFAULT_HOST_SPAWN_TIMEOUT`; `mode=content` finds docs mentioning related env vars but not exact literal-region occurrences. | Deferred to source-region follow-up. Keep literal/env-var search opt-in via regions. |
| Call-flow workflow | `where does fast search dispatch between lexical semantic and hybrid backends` | Not rerun in Julie for this slice. | Natural-language `search` stays docs/prose oriented in `mode=content` and symbol/property oriented in `auto`. Exact-symbol follow-up works: `hybrid_search`, `ResolvedSearchBackend`, and `execute_search_unified` locate the implementation anchors; `context` with `entry_symbols=["hybrid_search","SearchBackend"]` pulls the dispatch files together. `trace execute_search_unified` is noisy on Julie's graph. | Workflow is usable through `search` + `context`/`inspect`; trace quality is a separate follow-up, not a reason to widen symbol ranking. |
| Bridge-provider workflow on MyraNext | `getAllSecurityUsers`, `AllUsers`, `ApplicationUser`, `SecurityUserResults` | Not applicable to Julie comparison clone. | `trace mode=bridge` proves the route edge from both symbol IDs: `getAllSecurityUsers --route--> AllUsers 0.90 (High)`. `ApplicationUser` bridges to `ApplicationUsers` via `DbSet` at `0.95 (High)`. The sampled TypeScript DTO interfaces and `SecurityUserResults`/`SecurityUser` classes did not produce DTO/type bridge links. Follow-up implemented locally: bridge mode can choose the single bridge-connected candidate from a duplicate symbol set, and `trace` now accepts `scope=<file>` when multiple candidates remain. | Bridge tracing is useful provider-scoped evidence, especially from route/controller/entity anchors. The remaining sample weakness is absent DTO/type links, not target disambiguation. |

## Initial Takeaways

1. **Closed first Miller fix from this smoke pass:** exact symbol queries now rank concrete definitions
   above import/module rows when names tie. `WorkspacePool` was the repro.
2. **No evidence yet for widening symbol projection.** The `line_matches` case works in `auto`/`symbol`.
   The content miss is mode misuse, not proof that symbol ranking needs doc/comment/literal widening.
3. **Closed scoped search filters.** Julie's scoped fallback hint is useful for agents. Miller now accepts
   `file_pattern=<glob>` and `language=<lang>` to narrow symbol/content/region results before rendering.
4. **Closed file lookup result shape.** Compact file mode now labels exact/single-file hits with
   `File match: <path>` and ambiguous path-fragment hits with `File matches:` grouped by file.
5. **Reuse Julie's matrix manifests carefully.** `fixtures/search-quality/search-matrix-cases.toml`
   contains at least one stale path for current Julie `main`, so copied cases need current-path validation.
6. **Closed compact output noise:** compact symbol output now follows Julie's promoted-definition pattern
   for exact non-low-signal hits, groups secondary matches by file path, and abbreviates low-signal
   `import`/`module` signatures as `low_signal`; JSON still carries full signatures for chaining.
7. **Docs/prose mode split is holding.** Natural-language documentation and eval queries work through
   `mode=content`. `auto` stays symbol-oriented, which is expected and should be reinforced in guidance.
8. **Doc comments and literals remain source-region scope.** Region queries correctly fail closed when the
   sidecar was not built with `MILLER_REGION_INDEX=1`. Keep this tied to the existing source-region follow-up
   instead of widening symbol ranking.
9. **Call-flow discovery is a workflow, not one search query.** Natural-language search can find docs/eval
   evidence, while exact-symbol search plus `context`/`inspect` finds implementation anchors. `trace` is
   currently too noisy for this Julie flow and should be treated as trace-quality work.
10. **Bridge tracing has real agent value when anchored concretely.** MyraNext route and EF/table links
    produced high-confidence evidence. Bridge mode now resolves common duplicate function/export/import cases
    when exactly one candidate is bridge-connected, and `scope=<file>` handles cases that still need file
    disambiguation. The remaining sample weakness is absent DTO/type links, not generic semantic similarity.

## Next Matrix Rows

- First beta pass complete. Continue only if fresh dogfood produces a concrete miss or if we choose one of
  the follow-ups below.

## Candidate Follow-Ups

1. Decide whether JSON file mode should eventually grow explicit file-level result objects. Current beta
   behavior keeps JSON as symbol rows for compatibility.

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

## File Mode Shape Fix Evidence

The original file-mode result for an exact path was technically correct but less direct than Julie:

```text
backend  namespace  crates/julie-tools/src/search/mod.rs:23  mod backend
execution  namespace  crates/julie-tools/src/search/mod.rs:24  pub mod execution
```

Compact file mode now makes the file itself the first signal:

```text
File match: crates/julie-tools/src/search/mod.rs
  :23 backend namespace
  :24 execution namespace
  :25 formatting namespace
  :26 hint_formatter namespace
  :27 input_diagnostics namespace
  :28 line_mode namespace
  :29 nl_embeddings namespace
  :30 query namespace
… 52 more (raise limit)
```

For ambiguous basename/path-fragment queries, `FindByFilePathFragment` now returns one representative symbol
per matched file before extra symbols from earlier files. Dogfood after the fix:

```text
miller search mod.rs --mode file --limit 8
File matches:
src/tests/mod.rs:
  :12 analysis namespace
src/tools/mod.rs:
  :8 metrics namespace
src/utils/mod.rs:
  :10 file_utils namespace
src/daemon/mod.rs:
  :5 app namespace
src/health/mod.rs:
  :5 checker namespace
src/cli_tools/mod.rs:
  :13 commands namespace
src/dashboard/mod.rs:
  :3 error_buffer namespace
src/tests/daemon/mod.rs:
  :1 admit_initialize_short_circuit namespace
… 376 more (raise limit)
```

Added tests:

- `SearchToolTests.Run_FileMode_SearchesFilePathFragments`
- `SearchToolTests.Run_AutoMode_RoutesPathLikeQueryToFileSearch`
- `SearchToolTests.Run_FileMode_GroupsMultipleFileMatches`
- `SearchToolTests.Run_FileMode_Json_KeepsSymbolRows`
- `SymbolLookupTablesTests.FindByFilePathFragment_ReturnsOneSymbolPerFileBeforeExtraSymbols`

## Docs/Regions Mode Split Evidence

Docs/prose query:

```text
miller search "line mode scoped filter miss fixed" --mode content --limit 5
docs/release-notes/v7.13.1.md:55
  - **Line-mode scoped-filter miss fixed.** Scoped line-mode searches
    (`file_pattern` / language filters) could miss in-scope results hiding behind
    many high-scoring out-of-scope files.
```

Eval/prose query:

```text
miller search "where does fast search dispatch between lexical semantic and hybrid backends" --mode content --limit 5
docs/eval/semantic-value/results/2026-05-23T17-35-16Z.json:198
docs/eval/semantic-value/scorecard.toml:94
```

The same eval/prose query in `auto` returned scorecard `query` properties rather than prose/document sections,
which is expected for symbol search and reinforces the mode guidance.

Source-region queries fail closed without the opt-in sidecar:

```text
miller search "canonicalized to prevent duplicate workspace IDs" --regions doc_comment
search failed: region search requires MILLER_REGION_INDEX=1 and a refreshed search sidecar.

miller search "JULIE_EMBEDDING_HOST_SPAWN_TIMEOUT_SECS" --regions string_literal
search failed: region search requires MILLER_REGION_INDEX=1 and a refreshed search sidecar.
```

This keeps doc-comment and literal/env-var search tied to the source-region default-on decision tracked in
`TODO.md` item 7.

## Scoped Search Filter Evidence

Scoped filters are now first-class search parameters:

```text
miller search GetUser --file-pattern auth/** --language csharp
```

The filter is applied after ranking and before rendering, preserving rank order inside the requested scope. It
works across result kinds:

- Symbol/file search filters by `IndexedSymbol.FilePath` and `IndexedSymbol.Language`.
- Content search filters by content hit path and the language carried from the `files` manifest.
- Region search filters by region path and language from `search_regions`.

The beta behavior is intentionally narrow: filters reduce the result set; they do not widen symbol scoring or
merge docs/comments/literals into symbol ranking.

## Call Flow Workflow Evidence

Natural-language call-flow intent is better handled as a two-step workflow than by widening symbol search:

```text
miller search "where does fast search dispatch between lexical semantic and hybrid backends" --mode content --limit 5
docs/eval/semantic-value/results/2026-05-23T17-35-16Z.json:198
docs/eval/semantic-value/scorecard.toml:94
```

Exact-symbol follow-up found the implementation anchors:

```text
miller search hybrid_search --limit 5
Definition found: hybrid_search
  crates/julie-index/src/search/hybrid.rs:194 (function) has_doc

miller search ResolvedSearchBackend --limit 5
Definition found: ResolvedSearchBackend
  crates/julie-tools/src/search/backend.rs:13 (enum) has_doc

miller search execute_search_unified --limit 5
Definition found: execute_search_unified
  crates/julie-tools/src/search/execution.rs:93 (function) has_doc
```

`context` with the natural-language query and `entry_symbols=["hybrid_search","SearchBackend"]` pulled the
relevant dispatch files together: `crates/julie-tools/src/search/execution.rs`,
`crates/julie-tools/src/search/backend.rs`, `crates/julie-tools/src/search/mod.rs`, and
`crates/julie-index/src/search/hybrid.rs`.

`trace execute_search_unified` returned broad graph neighbors on Julie's codebase, so call-flow trace quality
should be tracked separately from search ranking.

## Bridge Provider Workflow Evidence

MyraNext route anchors prove the provider can give agents useful cross-language evidence when the target is
concrete:

```text
miller trace AllUsers --mode bridge --workspace-id /Users/murphy/source/MyraNext
# trace bridge AllUsers (1 link(s))
getAllSecurityUsers  --route-->  AllUsers  0.90 (High)

miller trace d24dfe866c85b2c27cd9eaab283b913f --mode bridge --workspace-id /Users/murphy/source/MyraNext
# trace bridge getAllSecurityUsers (1 link(s))
getAllSecurityUsers  --route-->  AllUsers  0.90 (High)
```

The same workspace also proves non-route provider evidence:

```text
miller trace b324937b307cfac01895bc7cac117a71 --mode bridge --workspace-id /Users/murphy/source/MyraNext
# trace bridge ApplicationUser (1 link(s))
ApplicationUser  --DbSet-->  ApplicationUsers  0.95 (High)
```

The sampled DTO/type anchors did not bridge:

```text
miller trace 0ed9161386197c3aa730a54750913643 --mode bridge
'0ed9161386197c3aa730a54750913643' is not on a cross-language bridge.

miller trace f1cb16bfeeba3dec01f28c06ccfe679b --mode bridge
'f1cb16bfeeba3dec01f28c06ccfe679b' is not on a cross-language bridge.
```

Name-only bridge targets were also rough in real code. `getAllSecurityUsers` is both a function and export,
and imported elsewhere, so the original `trace getAllSecurityUsers --mode bridge` path was ambiguous. The
local follow-up now lets bridge mode select the single bridge-connected candidate from that duplicate set:

```text
miller trace getAllSecurityUsers --mode bridge
# trace bridge getAllSecurityUsers (1 link(s))
getAllSecurityUsers  --route-->  AllUsers  0.90 (High)
```

`trace` also has the same scope/disambiguation affordance as `inspect` when multiple candidates remain:

```text
miller trace getAllSecurityUsers --mode bridge --scope MyraNext/MyraNext.Web/ClientApp/src/services/api/userManagementservice.ts
```

Both paths avoid a JSON symbol-id lookup for the common bridge workflow.
