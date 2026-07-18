---
name: miller-patterns-audit
description: Use when an agent needs extractor-recognized code-shape facts such as routes, htmx attributes, Alpine directives, SQL DDL/DML, or data-document structure.
user-invocable: true
arguments: "<pattern family, path, language, or metadata filter>"
allowed-tools: mcp__miller__patterns, mcp__miller__search, mcp__miller__inspect, mcp__miller__workspace
---

# Miller Patterns Audit

The current extractor catalog contains 194 pattern IDs across 36 languages. Use `patterns` when the question is
about known structural facts emitted by `julie-extractors`, not arbitrary AST matching. Start by listing observed
pattern IDs in the selected workspace, then search the relevant ID with path, language, and metadata filters.

## Workflow

1. Discover available IDs:

```text
patterns(operation="list")
patterns(operation="list", language="razor")
```

List output includes `Next:` / JSON `next_actions` derived from observed IDs. Prefer those follow-up calls
before inventing a raw text grep.

2. Search a concrete pattern:

```text
patterns(operation="search", pattern_id="aspnet.minimal_api.route.v1", where="verb=GET")
patterns(operation="search", pattern_id="htmx.attribute.v1", where="name=hx-get", path="Views/**")
patterns(operation="search", pattern_id="alpine.directive.v1", where="directive=x-data", path="Views/**")
patterns(operation="search", pattern_id="sql.merge_statement.v1")
```

3. Summarize before broad audits:

```text
patterns(operation="summary", pattern_id="markdown.heading.v1", path="docs/**")
patterns(operation="summary", pattern_id="json.property.v1", language="json")
```

4. Inspect source only after the pattern rows identify a file and line:

```text
inspect(target="<path from pattern row>")
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
