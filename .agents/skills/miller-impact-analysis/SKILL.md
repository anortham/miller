---
name: miller-impact-analysis
description: Use when assessing blast radius with Miller, choosing tests for a change, answering who uses a symbol, or planning a refactor.
user-invocable: true
arguments: "<symbol, file path, diff, or change target>"
allowed-tools: mcp__miller__impact, mcp__miller__search, mcp__miller__inspect, mcp__miller__trace, mcp__miller__context, mcp__miller__workspace
---

# Miller Impact Analysis

Use Miller's graph-backed `impact` tool before refactors or shared behavior changes. One impact call should replace manual reference greps.

## Workflow

1. Resolve the target if it is ambiguous:

```text
search(query="<symbol or concept>")
inspect(target="<candidate>")
```

2. Run exactly one impact seed:

```text
impact()
impact(target="<symbol-or-file>")
impact(changed_paths=["<file1>", "<file2>"])
impact(diff="<unified diff>")
```

With no args, `impact()` reads the working-tree git diff and maps changed ranges to impacted symbols plus likely
tests — run it after edits, before committing, to see what your uncommitted change affects. Use `git=true`
(`base`/`staged` imply git) to scope to a specific git diff instead.

3. Inspect or trace only the risky results:

```text
inspect(target="<impacted-symbol>", depth="full")
trace(target="<impacted-symbol>")
trace(target="<source>", mode="path", to="<sink>")
```

4. If `impact` reports likely tests, treat that list as the starting verification set. Add broader tests when the touched surface is shared infrastructure or cross-workspace behavior.

## Risk Tiers

- High: public entry points, tool handlers, workspace/indexing paths, shared contracts, or many downstream symbols.
- Medium: feature modules, storage/read-path code, bridge providers, or several callers.
- Low: isolated helpers, tests, docs, or one-off internal callers.

## Report

Lead with:

- Target and definition
- High/medium/low risk groups
- Likely tests or verification commands
- Unknowns such as stale index, ambiguous symbol names, or dynamic usage

Do not replace `impact` with `rg` unless Miller cannot resolve the target.
