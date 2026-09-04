---
name: miller-patterns-audit
description: Use when an agent needs extractor-recognized code-shape facts such as routes, htmx attributes, Alpine directives, SQL DDL/DML, or data-document structure.
user-invocable: true
arguments: "<pattern family, path, language, or metadata filter>"
allowed-tools: mcp__miller__patterns, mcp__miller__search, mcp__miller__inspect, mcp__miller__workspace
---

# Miller Patterns Audit

Use `patterns` when the question is about known structural facts emitted by `julie-extractors`, not arbitrary AST
matching. Runtime list output is authoritative for the current catalog. Start by listing observed pattern IDs in
the selected workspace, then search the relevant ID with path, language, and metadata filters.

## Workspace targeting (required)

Every workspace-bound Miller MCP call names its target with `workspace_id`; Miller never infers it from the
launch directory, environment variables, MCP Roots, or a previous call. Get the ID from
`workspace(operation="list")`, or from `workspace(operation="open", path="/absolute/project")` when the repo is
absent. The examples below write it as `workspace_id="<id>"`. Only `workspace` `list`, `open`, `remove`,
`prune`, and `dashboard` run without one; `current` and `primary` are CLI-only. The full targeting rules live
in the `miller-orientation` skill.

## Workflow

1. Discover available IDs:

```text
patterns(workspace_id="<id>", operation="list")
patterns(workspace_id="<id>", operation="list", language="razor")
```

List output includes `Next:` / JSON `next_actions` derived from observed IDs. Prefer those follow-up calls
before inventing a raw text grep.

2. Search a concrete pattern:

```text
patterns(workspace_id="<id>", operation="search", pattern_id="aspnet.minimal_api.route.v1", where="verb=GET")
patterns(workspace_id="<id>", operation="search", pattern_id="htmx.attribute.v1", where="name=hx-get", path="Views/**")
patterns(workspace_id="<id>", operation="search", pattern_id="alpine.directive.v1", where="directive=x-data", path="Views/**")
patterns(workspace_id="<id>", operation="search", pattern_id="sql.merge_statement.v1")
```

3. Summarize before broad audits:

```text
patterns(workspace_id="<id>", operation="summary", pattern_id="markdown.heading.v1", path="docs/**")
patterns(workspace_id="<id>", operation="summary", pattern_id="json.property.v1", language="json")
```

4. Inspect source only after the pattern rows identify a file and line:

```text
inspect(workspace_id="<id>", target="<path from pattern row>")
```

## Good Uses

- List ASP.NET minimal API routes without scanning source text.
- Find htmx or Alpine usage in Razor/HTML views.
- Audit SQL DDL/DML, including MERGE, JSON/YAML/TOML keys, or Markdown structure.
- Compare code-shape facts across registered workspaces with `workspace_id`.

## Limits

- `patterns` is not a raw AST query engine.
- Pattern IDs come from the extractor catalog; if a shape is missing, add extractor support first.
- No-match output can include near matches and active-filter guidance. If the pattern exists but filters remove
  every row, loosen `path`, `language`, or `where`.
- Use `search(mode="source")` for arbitrary source text and `search(mode="content")` for docs/config prose.
