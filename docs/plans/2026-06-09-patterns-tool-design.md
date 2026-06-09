# Patterns Tool Design

Date: 2026-06-09
Status: Approved concept, ready for implementation planning
Related plan: `docs/plans/2026-06-09-miller-data-opportunities-plan.md`

## Plain Language Model

`julie-extractors` reads source code and puts sticky notes on interesting code shapes it recognizes.
Miller stores those sticky notes as `structural_facts`.

The `patterns` tool lets callers ask:

- What kinds of sticky notes exist in this workspace?
- Where did this kind appear?
- Which files, languages, and symbols contain those matches?

Callers should not need to know AST or tree-sitter concepts. The public surface should say "patterns", not
"AST queries".

## Problem

`julie-extractors` v2.2.0 now emits parser-backed `structural_facts`. Miller currently reports aggregate
availability through `workspace health --json`, but callers cannot directly list or search those facts.

The next Miller slice should expose structural facts in a way that:

- works for unknown future patterns without a Miller rewrite,
- keeps parser recognition in `julie-extractors`,
- gives agents and Eros a stable CLI/MCP JSON surface,
- avoids exposing arbitrary AST query languages.

## Goals

- Add a standalone `patterns` tool and CLI command over `structural_facts`.
- Make the tool data-driven: every observed `pattern_id` is listable and searchable, even without a Miller catalog entry.
- Preserve top-level `metadata_json` as structured JSON when it is valid.
- Support generic exact metadata filters for pattern-specific details such as `name=hx-get`.
- Keep first-slice filtering bounded and predictable.
- Document a stable JSON contract before Eros or agents depend on the shape.

## Non-Goals

- Do not add arbitrary AST/tree-sitter query execution to Miller.
- Do not hard-code C#, htmx, TypeScript, Rust, or Go logic in the tool.
- Do not change `julie-extractors` contracts in this slice.
- Do not add complexity ranking or duplicate-code discovery here. Those are separate future tools.
- Do not make Eros read Miller's private SQLite files.

## Pattern Identity

`pattern_id` is the durable name for a code shape.

Examples:

```text
typescript.await_expression.v1
go.goroutine_launch.v1
go.defer_statement.v1
rust.unsafe_block.v1
csharp.attribute_usage.v1
csharp.await_expression.v1
htmx.attribute.v1
htmx.request_attribute.v1
```

Rules:

- `pattern_id` values come from `julie-extractors`.
- Miller treats unknown `pattern_id` values as valid data.
- The version suffix describes the meaning of the pattern. If the meaning changes, `julie-extractors` should emit
  a new id such as `.v2`.
- Miller may add friendly labels later, but labels are optional decoration. The id remains the contract.

## Metadata

Pattern-specific detail belongs in `metadata_json`.

For htmx:

```json
{
  "name": "hx-get",
  "value": "/orders"
}
```

For C# attributes:

```json
{
  "name": "Authorize"
}
```

For the first slice:

- JSON output returns `metadata` as an object when `metadata_json` is valid JSON.
- JSON output returns `metadata_error` when `metadata_json` is present but malformed.
- Compact output may show a short metadata summary, but must not dump large JSON blobs.
- Generic metadata filters are exact string comparisons against top-level keys.
- Metadata filters require a `pattern_id` in the first slice. This keeps queries bounded on large workspaces.

## Tool Surface

Add MCP:

```text
patterns(operation="list", workspace_id=null, language=null, format="compact")
patterns(operation="search", workspace_id=null, pattern_id="htmx.attribute.v1", language="html", path="Views/**", where="name=hx-get", limit=50, format="json")
patterns(operation="summary", workspace_id=null, pattern_id=null, language=null, path=null, format="json")
```

Add CLI:

```bash
miller patterns list
miller patterns list --json
miller patterns list --workspace-id other-repo --json
miller patterns summary --json
miller patterns search --pattern htmx.attribute.v1 --where name=hx-get --json
miller patterns search --pattern csharp.attribute_usage.v1 --where name=Authorize --path "src/**"
```

Parameter rules:

- `operation`: `list`, `summary`, or `search`.
- `workspace_id` / `--workspace-id`: optional read-workspace selector, matching `search`, `inspect`, `context`,
  `impact`, and `trace`. CLI should also accept `--workspace DIR`.
- `pattern_id` / `--pattern`: required for `search`.
- `language`: optional exact language filter.
- `path`: optional workspace-relative glob filter.
- `where`: optional top-level metadata equality filter as `key=value`. Requires `pattern_id`.
- `limit`: applies to `search`; default `50`, maximum `500`.
- `format`: `compact` or `json`.

## JSON Shape

`patterns list --json`:

```json
{
  "schema_version": 1,
  "operation": "list",
  "patterns": [
    {
      "pattern_id": "htmx.attribute.v1",
      "label": "htmx.attribute.v1",
      "languages": ["html", "razor"],
      "captures": ["attribute"],
      "count": 42,
      "catalog": "observed"
    }
  ]
}
```

`patterns search --json`:

```json
{
  "schema_version": 1,
  "operation": "search",
  "pattern_id": "htmx.attribute.v1",
  "matches": [
    {
      "fact_id": "fact-1",
      "pattern_id": "htmx.attribute.v1",
      "language": "html",
      "path": "Views/Orders.cshtml",
      "capture_name": "attribute",
      "node_kind": "attribute",
      "containing_symbol_id": "symbol-1",
      "span": {
        "start_line": 42,
        "start_column": 13,
        "end_line": 42,
        "end_column": 41,
        "start_byte": 1250,
        "end_byte": 1278
      },
      "confidence": 1.0,
      "metadata": {
        "name": "hx-get",
        "value": "/orders"
      }
    }
  ]
}
```

`patterns summary --json`:

```json
{
  "schema_version": 1,
  "operation": "summary",
  "groups": [
    {
      "language": "html",
      "pattern_id": "htmx.attribute.v1",
      "capture_name": "attribute",
      "count": 42
    }
  ]
}
```

## Catalog Overlay

The first implementation does not need a rich catalog. It should default `label` to `pattern_id`.

Future Miller versions may add a small catalog overlay with:

- friendly label,
- tags,
- description,
- expected metadata keys.

The catalog must not decide whether a pattern exists. Observed facts decide that. Unknown patterns must still
work.

## Architecture Quality

**Affected modules:**

- `src/Miller.Indexing`: add a small reader over `structural_facts`.
- `src/Miller.Server/Tools`: add `PatternsTool` and compact/JSON rendering.
- `src/Miller.Server/Cli`: add `patterns` dispatch and JSON support.
- `src/Miller.Server/Hosting`: register the new MCP tool.
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`: document the new tool.
- `docs/contracts`: add `patterns-json-v1.md`.
- `tests/Miller.Tests`: add reader, tool, CLI, capability, and agent-instruction coverage.

**Caller-facing interface:**

- New MCP tool: `patterns`.
- New CLI command: `miller patterns`.
- New stable JSON contract: `patterns-json-v1`.
- `capabilities --json` advertises the command and contract.

**Depth/locality check:**

- Recognition stays in `julie-extractors`.
- Miller reads, filters, groups, and renders facts.
- No `Miller.Core` dependency is required for the first slice.
- SQLite access stays in `Miller.Indexing`; rendering stays in `Miller.Server`.

**Test surface:**

- Tests should use generic fixture rows, including an unknown future pattern id.
- Tests should prove the public CLI/MCP JSON shape, not private helper details.
- Metadata filter tests should include htmx-like and C#-like rows without adding htmx/C# branches in Miller.

**Seams/adapters:**

- Add a narrow `StructuralFactsReader` or `PatternFactsReader`.
- Keep metadata parsing/filtering behind the reader or a small model helper so CLI and MCP share behavior.
- Keep compact rendering separate from JSON rendering.

**Rejected shortcuts:**

- Do not add raw AST query execution.
- Do not fold this into default `search`.
- Do not switch on known pattern ids inside the tool.
- Do not require catalog entries before a pattern can be listed or searched.
- Do not expose private SQLite paths as the Eros integration surface.

**Architecture risk:** medium. This is a new public contract, but the behavior is read-only and can stay local.

## Error Handling

- Missing `structural_facts` table returns a clean unavailable error with guidance to restore/re-index with a
  compatible `julie-extract`.
- `search` without `pattern_id` returns a usage error.
- `where` without `pattern_id` returns a usage error in the first slice.
- Invalid `--where` syntax returns a usage error naming the invalid argument.
- Malformed row metadata does not crash the tool. JSON includes `metadata_error`; metadata-filtered searches skip
  malformed metadata rows.
- Empty results are successful and return an empty array.

## Data Flow

1. CLI/MCP parses operation and filters.
2. Tool resolves the workspace through the existing workspace index provider path.
3. `PatternFactsReader` reads `structural_facts` with required table/schema checks.
4. Reader applies SQL-level filters for `pattern_id`, `language`, and coarse path constraints where practical.
5. Reader parses top-level metadata JSON for rows that need metadata output or filtering.
6. Tool renders compact or JSON output.
7. Telemetry records tool name, operation, result count, and error kind using existing tool telemetry conventions.

## Expansion Rules

When `julie-extractors` adds a new pattern:

1. The next Miller pin bump receives the new `pattern_id`.
2. `patterns list` shows it automatically after re-indexing.
3. `patterns search --pattern <id>` works without Miller code changes.
4. If the pattern has top-level metadata, `--where key=value` can filter it without a pattern-specific branch.
5. A later Miller release may add a friendly catalog label, but that is optional.

This is the main design guarantee.

## Acceptance Criteria

- `miller patterns list --json` lists observed pattern ids grouped by language/capture/count.
- `miller patterns search --pattern <id> --json` returns stable match objects with span, confidence, and metadata.
- `miller patterns search --pattern htmx.attribute.v1 --where name=hx-get --json` works against generic fixture data.
- Unknown future pattern ids are listable and searchable without catalog entries.
- Invalid metadata JSON is reported in JSON output and skipped for metadata-filtered searches.
- `capabilities --json` lists `patterns --json` and the `patterns-json-v1` contract.
- `docs/contracts/cli-eros-v1.md` points Eros to the public `patterns` JSON contract.
- `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` explains the tool in plain language.
- Fast tests pass with `scripts/test.sh`.
- Build passes with `dotnet build Miller.slnx -c Release`.
