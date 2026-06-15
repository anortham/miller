# Tool Output Efficiency Audit

Date: 2026-06-14

## Scope

Reviewed Miller's user-facing MCP/CLI tool outputs for the balance between compactness and one-call usefulness.
The audit covered:

- `search`, `inspect`, `context`, `impact`, `trace`
- `workspace`, `content`, `patterns`
- explicit JSON/export contracts from `capabilities`, `content export`, `telemetry export`, `symbols export`, and
  `complexity export`

The baseline was the earlier compact-output tuning in
[`2026-06-05-tool-output-token-savings.md`](2026-06-05-tool-output-token-savings.md).

## Method

Used the installed `miller 0.5.2+c56615a2f1a4` binary, matching the current checkout, plus focused unit tests
for renderer behavior. Output size was measured from real commands against `/Users/murphy/source/miller`.

Representative compact measurements before the candidate-output fix:

| call | bytes | lines | finding |
| --- | ---: | ---: | --- |
| `workspace status` | 263 | 6 | good: compact current state |
| `workspace health` | 561 | 10 | good: verdict first, bounded warnings |
| `workspace list` | 8,470 | 83 | acceptable inventory output for 82 registered workspaces |
| `search SearchTool` | 751 | 22 | good: definition-first and grouped matches |
| `inspect Search` | 10,702 | 139 | too large for an ambiguity response |
| `inspect SearchTool --scope src/Miller.Server/Tools/SearchTool.cs` | 710 | 9 | guidance still said `scope=<file>` after scope was already applied |
| `context "SearchTool InspectTool compact output renderers tests token savings" --token-budget 3500` | 10,088 | 112 | acceptable: token-budgeted bundle |
| `impact src/Miller.Server/Tools/SearchTool.cs --max-depth 2` | 4,570 | 97 | acceptable: bounded graph output with hidden-row notes |
| `patterns list --limit 20` | 1,389 | 33 | good: explicit limit |
| `content read --context-lines 5` | 1,703 | 7 | acceptable: explicit bounded read, raw line fidelity |

Explicit export contracts are intentionally large and remain machine-first:

| export | bytes | lines |
| --- | ---: | ---: |
| `content export` | 7,770,193 | 1,168 |
| `telemetry export --jsonl` | 3,516,720 | 6,714 |
| `symbols export --jsonl` | 5,219,104 | 10,963 |
| `complexity export --jsonl` | 2,074,366 | 4,722 |

## Change

Compact ambiguous-candidate output now:

- renders at most 20 candidates
- appends a remainder note, for example `... 60 more candidates; refine target to narrow.`
- chooses the guidance line from the candidate shape:
  - cross-file ambiguity: `pass scope=<file>`
  - same-file ambiguity: `pass a more specific target`
- normalizes multi-line `inspect` signatures into one compact row before truncation

The cap is shared by `inspect`, `impact`, and `trace` through `CandidateOutput`.

## Result

After the change:

| call | before | after |
| --- | ---: | ---: |
| `inspect Search` | 10,702 bytes / 139 lines | 2,569 bytes / 22 lines |
| `inspect SearchTool --scope src/Miller.Server/Tools/SearchTool.cs` | 710 bytes / 9 lines | 704 bytes / 5 lines |
| `trace SearchTool --scope src/Miller.Server/Tools/SearchTool.cs` | misleading scope guidance | `pass a more specific target` |
| `impact Search` | unbounded candidates | 1,411 bytes / 22 lines with a remainder note |

## Left Unchanged

- `workspace list` remains an inventory response. It can be several KB on a heavily registered machine, but hiding
  entries would often force another selector-discovery call.
- `content read` keeps raw line fidelity. Long JSON/log lines can be large, but the caller explicitly controls
  `line` and `context_lines`.
- `context` remains token-budgeted rather than line-count capped. It should provide enough neighboring code to avoid
  immediate manual file reads.
- JSON and JSONL export modes remain complete because they are explicit machine contracts.

## Verification

- Red tests were observed for candidate caps, same-file ambiguity guidance, and multiline signature normalization.
- Focused renderer tests passed: `InspectToolTests`, `TraceToolTests`, `ImpactToolTests`, `WorkspaceRenderTests`,
  `ContentToolTests`, `PatternsToolTests`, `SearchToolTests`, `ContextToolTests`, and `AgentInstructionsTests`
  (`252` tests).
