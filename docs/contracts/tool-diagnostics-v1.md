# Tool Diagnostics Contract v1

Status: active for `search`, `inspect`, `context`, `trace`, `impact`, `patterns`, and `workspace`.

This contract separates useful empty results from hard operational failures without adding an MCP tool.
Compact output, JSON, next actions, telemetry, and the MCP error channel are derived from one
`ToolDiagnostic` value.

## Classes And Outcomes

| Class | Outcome | MCP error |
|---|---|---|
| `expected_empty` | `empty` | no |
| `ambiguity` | `empty` | no |
| `refusal` | `empty` | no |
| `unsupported` | `empty` | no |
| `corruption` | `error` | yes |
| `unavailable` | `error` | yes |
| `internal_failure` | `error` | yes |

Empty diagnostics are successful, actionable tool results. Hard diagnostics set `CallToolResult.IsError`
through the central tool-call filter. A caller must not infer success from the presence of text alone.

## Compact Shape

The tool's useful payload or diagnostic message is followed by:

```text
diagnostic_code=<stable_code>
diagnostic_class=<class>
next: <copyable call> — <reason>
```

`next:` repeats once per typed action and is omitted when there are no actions.

## JSON Shape

A diagnostic attached to an existing JSON object adds:

```json
{
  "diagnostic_schema_version": 1,
  "diagnostic": {
    "code": "not_found",
    "class": "expected_empty",
    "outcome": "empty",
    "message": "No indexed file or symbol matched the target.",
    "next_actions": [
      {
        "call": "search(query=\"Target\")",
        "reason": "find the canonical file or symbol identity"
      }
    ]
  }
}
```

An existing top-level array is preserved under `results`. A hard failure with no payload uses:

```json
{
  "schema_version": 1,
  "tool": "inspect",
  "diagnostic": {
    "code": "artifact_missing",
    "class": "unavailable",
    "outcome": "error",
    "message": "The workspace artifact is missing.",
    "next_actions": [
      {
        "call": "workspace(operation=\"refresh\")",
        "reason": "restore the missing artifact"
      }
    ]
  }
}
```

Invalid or scalar JSON from a JSON-capable tool is reclassified as
`internal_failure/invalid_json_output`; it is never returned as malformed success content.

## Tool Transitions

| Tool | Existing payload retained | Typed diagnostic transition |
|---|---|---|
| `search` | Non-empty route payloads retain their fields; bounded snippets add `snippet_truncated` only when changed. | Empty routes add `diagnostic`; validation, availability, and final-budget refusals use the diagnostic envelope. See `search-mcp-v1.md`. |
| `inspect` | Resolved file and symbol objects retain their fields. | Not-found files/symbols and ambiguity add `diagnostic`; continuation, final-budget, and hard failures use the envelope. See `inspect-json-v1.md`. |
| `context` | Non-empty bundles retain their fields. | Zero-entry bundles add `diagnostic`; refusals and hard failures use the envelope. |
| `trace` | Trace mode fields and algorithm `diagnostics[]` remain present. | Overall empty/refusal/error classification is authoritative in top-level `diagnostic`; see `trace-json-v1.md`. |
| `impact` | Non-empty impact objects retain their fields. | Empty diffs, unresolved targets, and zero dependents add distinct diagnostics; refusals and hard failures use the envelope. |
| `patterns` | Existing `empty_reason`, `near_matches`, `active_filters`, and `next_actions` remain present. | Empty results add top-level `diagnostic`; invalid requests and hard failures use the envelope. |
| `workspace` | Existing operation-specific payload fields remain present. | Safe no-results, refusals, unsupported operations, and hard failures add `diagnostic`. |

The top-level `diagnostic` is the authoritative outcome classification whenever present. Tool-specific
diagnostic arrays or notes remain evidence inside the retained payload and must not override it.

## Telemetry

Every diagnostic records `diagnostic_code` and `diagnostic_class`.

- Empty diagnostics set `outcome=empty` and `empty_reason=<code>`.
- Hard diagnostics set `outcome=error` and `error_category=<code>`.
- The central filter preserves the tool classification and marks only hard diagnostics as MCP errors.

Raw targets, queries, and source content are not added to diagnostic telemetry.
