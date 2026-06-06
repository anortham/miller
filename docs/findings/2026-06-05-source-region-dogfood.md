# Source-Region Search Dogfood

- **Date:** 2026-06-05
- **Follow-up remeasurement:** 2026-06-06
- **Miller build used for dogfood:** `0.1.0+e9b5a35c506c`
- **Feature:** explicit `search --regions comment|doc_comment|string_literal`
- **Decision:** keep region indexing opt-in for beta

## Summary

Initial real-repo dogfood validates the implementation path: `MILLER_REGION_INDEX=1` builds
`search_regions` and `regions_fts`, scoped searches return region-typed hits, and query latency is
usable on a medium repo.

The larger OpenClaw run showed two reasons not to make region indexing default-on for beta immediately:

- sidecar growth can be large when a repo has many string literals;
- multi-token region queries used broad OR-style recall, so result quality could be noisy.

Follow-up on `0.1.0+c53474eae69e` changed multi-token region search to require every distinct query term
while staying non-phrase, and exposed `MILLER_REGION_MAX_BYTES` to tune the existing per-region byte cap.

The 2026-06-06 cap remeasurement says the cap is a safety guardrail, not a realistic default-on size
control. On OpenClaw, lowering the cap from 64 KiB to 8 KiB only reduced the temp sidecar from
791,605,248 bytes to 790,835,200 bytes; even a 512-byte cap still produced 787,189,760 bytes. The size
cost is driven by many small `string_literal` regions rather than a few oversized regions. Keep region
indexing opt-in unless default-on is redesigned around kind-level indexing, for example comment/doc-comment
by default with `string_literal` still explicit.

## Current Miller Workspace

The restarted Miller binary was current for the source-region feature commit used in this dogfood run:

```text
0.1.0+e9b5a35c506c
```

The active MCP process holds the current workspace writer lock, so a standalone CLI full refresh of
`/Users/murphy/source/miller` returned `lock_busy`. The current server process was not started with
region indexing available for the already-built sidecar, and an MCP region query failed closed:

```text
region search requires MILLER_REGION_INDEX=1 and a refreshed search sidecar.
```

That fail-closed behavior is correct. To dogfood the current workspace through MCP, restart the MCP
server with `MILLER_REGION_INDEX=1` and refresh the workspace, or let the CLI acquire the lock.

## Medium Repo: `julie-extractors`

Command:

```bash
MILLER_REGION_INDEX=1 /usr/bin/time -p \
  /Users/murphy/source/miller/src/Miller.Server/bin/Release/net10.0/miller \
  workspace full --id julie-extractors-30a0bd2590c5 --json
```

Result:

```text
status=unchanged
revision=1
real=5.40s
symbols.db=163M
search.db=46M
symbols=33954
source_regions=80573
search_regions=80446
```

Indexed region kinds:

```text
comment=9382
doc_comment=5880
string_literal=65184
```

Representative queries:

```bash
MILLER_REGION_INDEX=1 miller search source --regions doc_comment --limit 5
```

Returned doc-comment hits in 0.49s, including `crates/julie-extractors/src/lib.rs:18`,
`crates/julie-extractors/src/base/types.rs:312`, and Bash module docs.

```bash
MILLER_REGION_INDEX=1 miller search TODO --regions comment --limit 5
```

Returned comment-only TODO hits in 0.47s from Rust tests and QML fixtures.

```bash
MILLER_REGION_INDEX=1 miller search source_regions --regions string_literal --limit 5
```

Returned string-literal hits in 0.50s from artifact/report/schema code.

```bash
MILLER_REGION_INDEX=1 miller search tree --mode content --regions comment --limit 5
```

Returned comment hits in 0.48s and emitted the expected routing note:

```text
mode=content ignored; regions search uses source-region text.
```

## Large Repo: `openclaw`

Command:

```bash
MILLER_REGION_INDEX=1 /usr/bin/time -p \
  /Users/murphy/source/miller/src/Miller.Server/bin/Release/net10.0/miller \
  workspace full --id openclaw-36c53da0da7d --json
```

Result:

```text
status=unchanged
revision=1
real=67.17s
symbols.db=1.7G
search.db=664M
symbols=640317
source_regions=881129
search_regions=877694
```

Indexed region kinds:

```text
comment=20044
doc_comment=7242
string_literal=850408
```

Representative queries:

```bash
MILLER_REGION_INDEX=1 miller search "fetch alert" --regions doc_comment --limit 5
```

Returned the expected `fetch-alert` doc-comment first, then broader `fetch`-only hits. Runtime:
3.52s.

```bash
MILLER_REGION_INDEX=1 miller search "node crypto" --regions string_literal --limit 5
```

Returned useful `"node:crypto"` string-literal hits. Runtime: 3.58s.

```bash
MILLER_REGION_INDEX=1 miller search "secret scanning" --regions comment --limit 5
```

Returned `// keep scanning` hits ahead of a more specific secret-scanning comment. Runtime: 3.55s.
This demonstrates the current broad recall behavior can be noisy for multi-token queries.

```bash
MILLER_REGION_INDEX=1 miller search "redacted content" --regions doc_comment --limit 5
```

Returned the expected `redact-body` doc-comment but also broad `content` hits. Runtime: 3.52s.

## Cap Remeasurement After All-Terms Semantics

The follow-up run used a temporary ignored harness that calls `SqliteSymbolReader.Read` and
`SearchIndexWriter.Write` directly against existing `symbols.db` files. It wrote temp `search.db` files
under `/tmp`, so the registered workspace sidecars were not replaced. Build times below measure sidecar
writer time only; query times measure in-process `FtsRegionSearchIndex` calls over the temp DB.

### `julie-extractors`

Current artifact: revision 3, 34,236 symbols, 81,202 source regions.

| Cap | Temp `search.db` bytes | Indexed regions | Indexed kinds | Build |
| --- | ---: | ---: | --- | ---: |
| 65,536 | 53,092,352 | 81,062 | comment=9,482; doc_comment=5,784; string_literal=65,796 | 1.65s |
| 8,192 | 52,961,280 | 81,057 | comment=9,482; doc_comment=5,784; string_literal=65,791 | 1.19s |

Representative query shape was unchanged: `source_regions` in `string_literal` returned 5 hits at both
caps, with `crates/julie-extract-artifact/src/reports.rs:27` first.

### `openclaw`

Current artifact: revision 1, 640,317 symbols, 881,129 source regions.

| Cap | Temp `search.db` bytes | Indexed regions | Indexed kinds | Build |
| --- | ---: | ---: | --- | ---: |
| 65,536 | 791,605,248 | 877,694 | comment=20,044; doc_comment=7,242; string_literal=850,408 | 24.37s |
| 8,192 | 790,835,200 | 877,670 | comment=20,044; doc_comment=7,242; string_literal=850,384 | 21.77s |
| 2,048 | 789,557,248 | 877,522 | comment=20,044; doc_comment=7,241; string_literal=850,237 | 21.14s |
| 512 | 787,189,760 | 876,823 | comment=20,042; doc_comment=7,119; string_literal=849,662 | 22.00s |

Representative query shape was also unchanged:

- `node crypto` in `string_literal` returned 5 hits at every cap, with
  `.agents/skills/openclaw-secret-scanning-maintainer/scripts/secret-scanning.mjs:6` first.
- `secret scanning` in `comment` returned 2 hits at every cap, with the usage comment in
  `.agents/skills/openclaw-secret-scanning-maintainer/scripts/secret-scanning.mjs:3` first.

## Decision For Beta

Keep source-region indexing **opt-in** for beta:

- `MILLER_REGION_INDEX=1` remains required to populate region rows.
- `regions=` remains explicit and fail-closed.
- `MILLER_REGION_MAX_BYTES` remains useful as a defensive cap, but it is not enough to control sidecar
  size for string-heavy large repos.
- Do not enable region indexing by default until there is a kind-level default-on design that avoids
  indexing every `string_literal` region by default.

## Follow-Ups

- If default-on source-region search is reopened, design kind-level indexing controls before changing the
  default. The likely shape is comment/doc-comment by default and `string_literal` behind an explicit opt-in.
- Keep `embedded` regions deferred.
- Keep region trigram recall deferred.
- Keep exclusion queries deferred.
