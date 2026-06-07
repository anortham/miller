---
name: miller-text-audit
description: Use when auditing registered Miller workspaces for dangerous strings, deprecated APIs, compatibility markers, secrets-like terms, or other exact text across source, docs, logs, external files, and web imports.
user-invocable: true
arguments: "<audit terms or term file>"
allowed-tools: mcp__miller__content, mcp__miller__search, mcp__miller__workspace
---

# Miller Text Audit

Use this workflow for token-efficient text audits. Prefer content-corpus search over raw grep because it returns ranked snippets, workspace identity, and bounded read coordinates.

## Workflow

1. Check registered workspaces:

```bash
miller workspace list
```

2. Search each audit term across registered content DBs:

```bash
miller content search "dangerous phrase" --workspace-id all --kind source --limit 20 --json
miller content search "deprecated config key" --workspace-id all --kind config --limit 20 --json
miller content search "old docs phrase" --workspace-id all --kind docs --limit 20 --json
```

Use `--kind external_file` for imported logs/reports and `--kind web` for imported web markdown. Use `search --mode all-text` only when a broad one-workspace union query is more useful than a registered-workspace audit.

3. Read only bounded windows for evidence:

```bash
miller content read --source-id SOURCE_ID --line 120 --context-lines 8
```

4. Summarize findings as workspace, file/display path, line, term, and one short snippet. Do not paste full files or full exported JSONL.

## Rules

- Context remains opt-in; do not feed audit hits into `context` unless the user asks for code context around a finding.
- Keep exact-term audits exact first. Raise `--limit` or switch kind only after the targeted query misses.
- Use `content export` only for integration feeds, not for interactive audit output.
- If `--workspace-id all` returns no rows, confirm workspaces are registered and content DBs are built before falling back to shell search.
