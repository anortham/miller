---
name: miller-impact-analysis
description: Use when assessing blast radius with Miller, choosing tests for a change, answering who uses a symbol, or planning a refactor.
user-invocable: true
arguments: "<symbol, file path, diff, or change target>"
allowed-tools: mcp__miller__impact, mcp__miller__search, mcp__miller__inspect, mcp__miller__trace, mcp__miller__context, mcp__miller__workspace, mcp__miller__tests
---

# Miller Impact Analysis

Use Miller's graph-backed `impact` tool before refactors or shared behavior changes. One impact call should replace manual reference greps.

## Workspace targeting (required)

Every workspace-bound Miller MCP call names its target with `workspace_id`; Miller never infers it from the
launch directory, environment variables, MCP Roots, or a previous call. Get the ID from
`workspace(operation="list")`, or from `workspace(operation="open", path="/absolute/project")` when the repo is
absent. The examples below write it as `workspace_id="<id>"`. Only `workspace` `list`, `open`, `remove`,
`prune`, and `dashboard` run without one; `current` and `primary` are CLI-only. The full targeting rules live
in the `miller-orientation` skill.

## Workflow

1. Resolve the target if it is ambiguous:

```text
search(workspace_id="<id>", query="<symbol or concept>")
inspect(workspace_id="<id>", target="<candidate>")
```

2. Run exactly one impact seed:

```text
impact(workspace_id="<id>")
impact(workspace_id="<id>", target="<symbol-or-file>")
impact(workspace_id="<id>", changed_paths=["<file1>", "<file2>"])
impact(workspace_id="<id>", diff="<unified diff>")
```

With no args, `impact()` reads the working-tree git diff and maps changed ranges to impacted symbols plus likely
tests — run it after edits, before committing, to see what your uncommitted change affects. Use `git=true`
(`base`/`staged` imply git) to scope to a specific git diff instead.

3. Inspect or trace only the risky results:

```text
inspect(workspace_id="<id>", target="<impacted-symbol>", depth="full")
trace(workspace_id="<id>", target="<impacted-symbol>")
trace(workspace_id="<id>", target="<source>", mode="path", to="<sink>")
```

4. If `impact` reports likely tests, treat that list as the starting verification set. Add broader tests when the touched surface is shared infrastructure or cross-workspace behavior.

5. After the edit lands, check continuous testing (CT) before reaching for the whole suite:

```text
tests(workspace_id="<id>", operation="status")
tests(workspace_id="<id>", operation="run", wait=true)
```

`status` is cheap and starts nothing. When CT is enabled it names the cases the edit staled and their last
verdict; `run` executes only that stale set. When status reports `enabled: false`, run the likely tests from step
4 with the project's test runner instead.

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
