---
name: miller-text-audit
description: Use when auditing registered Miller workspaces for dangerous strings, deprecated APIs, compatibility markers, secrets-like terms, or other exact text across source, docs, logs, external files, and web imports.
user-invocable: true
arguments: "<audit terms or term file>"
allowed-tools: mcp__miller__content, mcp__miller__search, mcp__miller__workspace
---

# Miller Text Audit

Use this workflow for token-efficient text audits. Prefer content-corpus search over raw grep because it returns ranked snippets, workspace identity, and bounded read coordinates.

## Workspace targeting (required)

Every workspace-bound Miller MCP call names its target with `workspace_id`; Miller never infers it from the
launch directory, environment variables, MCP Roots, or a previous call. Get the ID from
`workspace(operation="list")`, or from `workspace(operation="open", path="/absolute/project")` when the repo is
absent. The examples below write it as `workspace_id="<id>"`. Only `workspace` `list`, `open`, `remove`,
`prune`, and `dashboard` run without one; `current` and `primary` are CLI-only. The full targeting rules live
in the `miller-orientation` skill.

## Workflow

1. Check registered workspaces:

```text
workspace(operation="list")
```

CLI equivalent: `miller workspace list`.

2. Search each audit term across registered content DBs. `workspace_id="all"` is the one read-only fan-out
   exception for text audits:

```text
content(operation="search", workspace_id="all", query="dangerous phrase", content_kind="source", limit=20)
content(operation="search", workspace_id="all", query="deprecated config key", content_kind="config", limit=20)
content(operation="search", workspace_id="all", query="old docs phrase", content_kind="docs", limit=20)
```

CLI equivalent:

```bash
miller content search "dangerous phrase" --workspace-id all --kind source --limit 20 --json
```

Use `content_kind="external_file"` (`--kind external_file`) for imported logs/reports and `content_kind="web"`
(`--kind web`) for imported web markdown. Use `search(mode="all-text")` only when a broad one-workspace union
query is more useful than a registered-workspace audit.

3. Read only bounded windows for evidence:

```text
content(operation="read", workspace_id="<hit workspace_id>", source_id="<hit source_id>", line=120, context_lines=8)
```

CLI equivalent: `miller content read --source-id SOURCE_ID --line 120 --context-lines 8`.

Use the `source_id` from each `content search` hit or `content list`. When the hit came from
`workspace_id="all"`, pass its `workspace_id` back to the read so Miller reads the right registered workspace.

4. Summarize findings as workspace, file/display path, line, term, and one short snippet. Do not paste full files or full exported JSONL.

## Rules

- Context remains opt-in; do not feed audit hits into `context` unless the user asks for code context around a finding.
- Keep exact-term audits exact first. Raise `--limit` or switch kind only after the targeted query misses.
- Use `content export` only for integration feeds, not for interactive audit output.
- Empty searches and failed reads include compact recovery text and JSON `diagnostic_code`/`next_actions`; follow those before falling back to shell search.
- If `workspace_id="all"` returns no rows, confirm workspaces are registered and content DBs are built before falling back to shell search.
