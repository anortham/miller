# Tool Output Token Savings Dogfood

Date: 2026-06-05

## Scope

Measured the current CLI-shaped output for Miller's main tools after the beta-candidate docs and source-region fixes. The goal was to find output that wastes agent context in normal compact mode before widening the beta scope.

## Baseline

Largest outputs from the measurement pass:

| tool call | bytes | note |
| --- | ---: | --- |
| `impact src/Miller.Server/Cli/CliDispatch.cs --max-depth 2 --json` | 19,141 | full structured contract; intentionally complete |
| `impact AgentInstructions --max-depth 2` | 11,451 | compact output dominated by likely tests |
| `impact src/Miller.Server/Cli/CliDispatch.cs --max-depth 2` | 11,141 | compact output dominated by likely tests |
| `search mode=content` | 3,425 | acceptable for multi-hit content search |
| `trace AgentInstructions` | 2,640-2,704 | acceptable for graph context |
| `inspect AgentInstructions --depth full` | 2,284 | acceptable |

## Change

Compact `impact` now caps the rendered likely-test list at 20 entries while preserving:

- the full likely-test count in the section header
- a remainder note, for example `... 56 more likely tests; use format=json for full list.`
- complete JSON output for callers that need the full machine-readable list

## Result

Measured on the rebuilt Release CLI:

| tool call | before | after |
| --- | ---: | ---: |
| `impact AgentInstructions --max-depth 2` | 11,451 bytes | 3,719 bytes |
| `impact src/Miller.Server/Cli/CliDispatch.cs --max-depth 2` | 11,141 bytes | 3,680 bytes |

This fixes the largest compact-mode waste without changing reachability, counts, telemetry, or JSON payloads.

## Remaining Notes

`impact --json` can still be large because it is the full integration contract. Leave it complete unless live agent usage shows JSON being requested accidentally or too often.
