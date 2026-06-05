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

## Impact Compact Follow-Up

The likely-test cap fixed the largest `impact` output, but compact impacted rows still repeated the full file path
on every row and surfaced low-signal `import`/`module` rows before more useful methods.

Compact `impact` output now:

- groups impacted rows by file
- groups likely-test rows by file while keeping the 20-row cap
- hides low-signal `import`/`module` rows in compact impacted output
- preserves the full impacted count plus a hidden-row note
- leaves JSON complete with every impacted row, including low-signal rows

Dogfood on the rebuilt Release CLI:

| tool call | output size | note |
| --- | ---: | --- |
| `impact AgentInstructions --max-depth 2` | 2,778 bytes | down from 3,719 bytes after the likely-test cap |
| `impact src/Miller.Server/Cli/CliDispatch.cs --max-depth 2` | 2,702 bytes | down from 3,680 bytes after the likely-test cap |
| `impact src/Miller.Server/Tools/SearchTool.cs --max-depth 2` | 2,965 bytes | hides 9 low-signal rows and groups both sections by file |

Representative compact shape:

```text
# impacted (4)
src/Service.cs:
  :20 Process method hop=1
  :30 Helper class hop=1
low_signal hidden: 2 imports/modules (use format=json for full list.)

# likely tests (1)
tests/ServiceTests.cs:
  :8 ProcessWorks method hop=1
```

## Remaining Notes

`impact --json` can still be large because it is the full integration contract. Leave it complete unless live agent usage shows JSON being requested accidentally or too often.

## Inspect Compact Follow-Up

After applying the Julie-style compact lessons to `search`, the next noisy compact shape was file-level
`inspect`: source-order output spent the first rows on `import` symbols and repeated the same file path on
every child row.

Compact file summaries now:

- hide low-signal `import`/`module` rows by default
- keep those rows available through explicit `kind=import` / `kind=module`
- group visible children by kind
- omit the repeated file path inside each child row
- leave JSON complete

Dogfood on the rebuilt Debug CLI:

| tool call | output size | note |
| --- | ---: | --- |
| `inspect src/Miller.Server/Tools/SearchTool.cs --limit 20` | 1,861 bytes | starts with class/enum/constructor/method/constant/field groups; hides 11 imports |
| `inspect src/Miller.Server/Tools/SearchTool.cs --kind import --limit 5` | 270 bytes | explicit low-signal request still shows imports |

Representative compact shape:

```text
# src/Miller.Server/Tools/SearchTool.cs
class (1)
  SearchTool  :41  [McpServerToolType] public sealed class SearchTool
method (4)
  Search  :80  [McpServerTool(Name = "search")] ...
...
low_signal hidden: 11 imports (pass kind=import/module)
```

## Context Compact Follow-Up

`context` had the same repeated-path problem in its bundle rows: each candidate repeated the file path even
when several selected symbols came from one file.

Compact context bundles now:

- group candidates by file in first-seen bundle order
- show each candidate as `:line name kind hop=N` plus signature
- preserve hop distance, selected count, signatures, and provenance
- keep JSON complete with per-candidate `file` fields
- cost bundle packing conservatively by including the file path per candidate even though compact text prints it once per file

Dogfood on the rebuilt Release CLI:

| tool call | output size | note |
| --- | ---: | --- |
| `context "SearchTool InspectTool compact output renderers tests token savings" --budget 3500` | 7,642 bytes | grouped by file; no repeated file path per candidate row |

Representative compact shape:

```text
# context bundle (3)
src/Shared.cs:
  :10 Alpha method hop=0  method Alpha()
  :20 Beta method hop=0  method Beta()
src/Gamma.cs:
  :30 Gamma class hop=0  class Gamma
```
