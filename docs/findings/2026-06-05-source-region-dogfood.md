# Source-Region Search Dogfood

- **Date:** 2026-06-05
- **Miller build:** `0.1.0+e9b5a35c506c`
- **Feature:** explicit `search --regions comment|doc_comment|string_literal`
- **Decision:** keep region indexing opt-in for beta

## Summary

Initial real-repo dogfood validates the implementation path: `MILLER_REGION_INDEX=1` builds
`search_regions` and `regions_fts`, scoped searches return region-typed hits, and query latency is
usable on a medium repo.

The larger OpenClaw run shows two reasons not to make region indexing default-on for beta yet:

- sidecar growth can be large when a repo has many string literals;
- multi-token region queries currently use broad OR-style recall, so result quality can be noisy.

Keep the beta behavior opt-in until quality and size tradeoffs are better understood.

## Current Miller Workspace

The restarted Miller binary is current:

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

## Decision For Beta

Keep source-region indexing **opt-in** for beta:

- `MILLER_REGION_INDEX=1` remains required to populate region rows.
- `regions=` remains explicit and fail-closed.
- Do not enable region indexing by default until the query-quality and sidecar-size tradeoffs are
  re-measured after follow-up work.

## Follow-Ups

- Investigate whether region search should require all distinct query terms by default, or expose a
  clear AND-style mode for scoped region search.
- Consider a size guard or separate default for `string_literal` indexing on very large repos.
- Keep `embedded` regions deferred.
- Keep region trigram recall deferred.
- Keep exclusion queries deferred.
