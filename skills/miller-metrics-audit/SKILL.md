---
name: miller-metrics-audit
description: Use when an agent needs deterministic local churn, clone, or complexity facts from Miller without adding MCP tool surface.
user-invocable: true
arguments: "<workspace selector, commit range, or metric kind>"
allowed-tools: Bash, mcp__miller__workspace
---

# Miller Metrics Audit

Use the `miller metrics` CLI family for deterministic local reports that are useful in review, refactor planning,
or backlog triage. Metrics are CLI-only; do not call or describe an MCP `metrics` tool.

## Commands

Run from the target workspace, or pass `--workspace-id <selector>` / `--workspace <path>` for a registered
workspace:

```bash
miller metrics churn --range HEAD~20..HEAD --limit 25 --json
miller metrics clones --min-count 2 --limit 25 --json
miller metrics complexity --min-severity moderate --exclude-tests --limit 25 --json
```

For a source checkout without `miller` on `PATH`, use the built binary if present:

```bash
src/Miller.Server/bin/Release/net10.0/miller metrics complexity --json
```

## Interpretation

- Churn maps changed git hunks to the current index, so renamed or deleted code can fall back to file-only rows.
- Clone rows are identical non-empty `body_hash` groups from the extractor, not semantic similarity.
- Complexity rows use Miller-owned transparent thresholds over extracted complexity metrics.
- These reports are local facts. Do not turn them into cleanup advice, suppressions, fleet history, or semantic
  ranking without a higher-level product workflow.

## Report

Keep the summary factual:

- Command and workspace selector used
- Top rows or groups with path/symbol references
- Limits/filters such as range, `--min-count`, `--min-severity`, and test inclusion
- Any caveat such as non-git workspace, file-only churn rows, or an empty report
