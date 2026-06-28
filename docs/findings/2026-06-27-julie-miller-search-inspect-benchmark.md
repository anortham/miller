# Julie vs Miller Search/Inspect Benchmark

Date: 2026-06-27

## Purpose

Measure the practical gap between Miller and Julie on the tools agents actually use heavily:

- Miller: `search`, `inspect`
- Julie: `fast_search`, `deep_dive`

The benchmark intentionally ignores lower-use surfaces such as metrics, impact, patterns, and editing.

Status note: this finding remains the narrow search/inspect evidence record. The broader
[Miller Julie foundation effectiveness matrix](2026-06-27-miller-julie-foundation-effectiveness-matrix.md)
now supersedes it for product planning and adaptation-candidate ranking.

## Method

Runner: [`scripts/bench-julie-miller-search-inspect.py`](../../scripts/bench-julie-miller-search-inspect.py)

Raw evidence:

- Summary: [`benchmarks/2026-06-27-search-inspect/summary.md`](benchmarks/2026-06-27-search-inspect/summary.md)
- Results CSV: [`benchmarks/2026-06-27-search-inspect/results.csv`](benchmarks/2026-06-27-search-inspect/results.csv)
- Prep CSV: [`benchmarks/2026-06-27-search-inspect/prep.csv`](benchmarks/2026-06-27-search-inspect/prep.csv)

Gate mode:

```bash
python3 scripts/bench-julie-miller-search-inspect.py --gate
```

The gate treats Julie numbers, latency, and output-size medians as report-only. It exits non-zero only when
Miller misses the acceptance thresholds from this finding: compact `search auto` source-body present at least
8/9, JSON exact-symbol present 9/9, and JSON file-query present at least 7/9. Thresholds scale to the selected
repo count for focused runs such as `--repos miller`.

Repos tested:

| repo | language |
|---|---|
| `/Users/murphy/source/miller` | C# |
| `/Users/murphy/source/julie` | Rust |
| `/Users/murphy/source/eros` | C# |
| `/Users/murphy/source/express` | JavaScript |
| `/Users/murphy/source/flask` | Python |
| `/Users/murphy/source/gson` | Java |
| `/Users/murphy/source/Newtonsoft.Json` | C# |
| `/Users/murphy/source/zod` | TypeScript |
| `/Users/murphy/source/jq` | C |

Scoring:

- `top`: expected file is the first visible result/file.
- `present`: expected file appears anywhere in output.
- `empty`: tool returned a no-result/not-found/index-required response.

Prep:

- Miller: `refresh --workspace <repo> --wait --json`
- Julie: `manage_workspace(operation="open", path=<repo>)`
- Timed calls use warm MCP processes for both tools.
- Miller measured reads pass `ensure_fresh=false` because refresh is already recorded as prep.

## Top-Level Results

| tool | tasks | top | present | empty | median ms | p95 ms | median chars |
|---|---:|---:|---:|---:|---:|---:|---:|
| `miller.search.auto` | 27 | 20 | 22 | 0 | 22 | 99 | 1248 |
| `miller.search.source` | 9 | 8 | 9 | 0 | 69 | 130 | 2076 |
| `miller.inspect.full` | 9 | 7 | 9 | 0 | 16 | 43 | 4409 |
| `julie.fast_search` | 27 | 8 | 25 | 0 | 14 | 73 | 480 |
| `julie.deep_dive.overview` | 9 | 8 | 9 | 0 | 22 | 93 | 1129 |

## Task Breakdown

| task/tool | n | top | present | median ms |
|---|---:|---:|---:|---:|
| symbol / `miller.search.auto` | 9 | 8 | 9 | 19 |
| symbol / `julie.fast_search` | 9 | 6 | 9 | 13 |
| file / `miller.search.auto` | 9 | 7 | 7 | 14 |
| file / `julie.fast_search` | 9 | 2 | 8 | 42 |
| source intent / `miller.search.auto` | 9 | 5 | 6 | 66 |
| source intent / `miller.search.source` | 9 | 8 | 9 | 69 |
| source intent / `julie.fast_search` | 9 | 0 | 8 | 11 |
| inspect / `miller.inspect.full` | 9 | 7 | 9 | 16 |
| inspect / `julie.deep_dive.overview` | 9 | 8 | 9 | 22 |

## Findings

### 1. Miller is not broadly behind on exact symbol or file lookup

For exact symbol search, Miller was stronger in this sample:

- Miller `search auto`: 8/9 top, 9/9 present.
- Julie `fast_search`: 6/9 top, 9/9 present.

For file-name-ish search, Miller was also stronger:

- Miller `search auto`: 7/9 top, 7/9 present.
- Julie `fast_search`: 2/9 top, 8/9 present.

This argues against a general “Miller cannot find code” diagnosis.

### 2. The real `search` gap is route recovery for source-body intent

When the task was a source-body phrase:

- Miller default `search auto`: 5/9 top, 6/9 present.
- Miller explicit `mode=source`: 8/9 top, 9/9 present.
- Julie `fast_search`: 0/9 top, 8/9 present.

Miller has the data and ranking when the caller chooses `mode=source`. The weaker part is the default route: an agent using plain `search` can miss source-body intent that Miller can answer with a different mode.

Concrete misses:

- Julie repo query `semantic fallback candidates`: Miller `auto` returned `docs/eval/semantic-value/run_scorecard.py`; `mode=source` found `crates/julie-tools/src/search/mod.rs`.
- Eros query `ReplaceSemanticInputs storeWorkspaceId inputs`: Miller `auto` returned store code; `mode=source` found `src/Eros.Semantic/SemanticMillerImporter.cs`.
- Flask query `The flask object implements a WSGI application`: Miller `auto` returned `src/flask/testing.py`; `mode=source` found `src/flask/app.py`.

### 3. Julie is more compact by default

Median output size:

- `julie.deep_dive.overview`: 1129 chars.
- `miller.inspect.full`: 4409 chars.

Miller `inspect full` is useful and complete, but it is not the same UX shape as Julie `deep_dive overview`. Agents often need a compact “enough to edit safely” read, not a full body plus all refs/callers/callees every time.

### 4. `inspect` target resolution still has test/version ambiguity

Both tools found every expected inspect target somewhere, but Miller was top on 7/9 and Julie was top on 8/9.

Miller misses were ambiguity cases:

- Flask `inspect Flask`: expected `src/flask/app.py`; first visible path was a test/example path.
- Zod `inspect ZodObject`: expected v4 classic schema; first visible path was v3/helper code.

This is not extraction failure. It is default target selection. Miller should probably prefer non-test concrete definitions when an exact symbol name has multiple candidates, and make version/package ambiguity easier to resolve.

### 5. Warm-read latency is not the main problem

With both tools measured through warm MCP processes:

- Miller `search auto` median: 22 ms.
- Julie `fast_search` median: 14 ms.
- Miller `inspect full` median: 16 ms.
- Julie `deep_dive overview` median: 22 ms.

Miller is slower on `mode=source` median 69 ms, but still well inside interactive tool latency. The bigger issue is whether the first call returns the right file and the right amount of context.

## Recommended Next Changes

1. Improve `search auto` source-body recovery.
   - For phrase-like queries, run a bounded source-content rescue when symbol hits are weak or absent.
   - Keep this inside the existing `search` tool; no new MCP tool needed.
   - Output should make the route explicit, for example: `source hits also matched`.

2. Add an `inspect` overview profile or reduce `full` overuse.
   - Existing `summary` is often too shallow.
   - Existing `full` is often too large.
   - A middle profile should return definition, doc/signature, compact children, top callers/callees/refs, and a bounded body excerpt.

3. Tighten inspect resolution ranking.
   - Prefer non-test concrete definitions over test/example symbols when exact names collide.
   - Surface “multiple definitions across packages/versions” more clearly for repos like Zod.

4. Keep measuring with this matrix.
   - Add real failed agent queries as rows.
   - Keep exact expected file evidence.
   - Track `auto` vs best-routed mode so we know whether a miss is data/ranking or tool-routing.
