# Phase 2 typed diagnostics and output budgets — 2026-07-23

## Result

Miller now derives compact diagnostics, versioned JSON diagnostics, next actions, telemetry outcomes, and the
MCP error channel from one typed value across `search`, `inspect`, `context`, `trace`, `impact`, `patterns`, and
`workspace`.

`inspect depth=full` now has a deterministic 16 KiB UTF-8 body budget with stateless continuation in MCP and
CLI output. The continuation foundation is ready for the Phase 3 reference-consumer migration without adding
an MCP tool or server-side spillover state.

## Diagnostic contract

The shared contract distinguishes:

- successful empty states: `expected_empty`, `ambiguity`, `refusal`, and `unsupported`;
- hard failures: `corruption`, `unavailable`, and `internal_failure`.

Empty states remain actionable successful tool results. Hard failures set MCP `IsError`, including in hosts that
do not register the telemetry ledger. The filter receives this classification through an async-local typed
signal; it does not infer errors by scanning returned source text.

JSON-capable tools always return valid JSON. Existing object payloads retain their fields, existing arrays move
under `results` only when a diagnostic must be attached, and payload-free failures use the versioned diagnostic
envelope. Trace retains its mode-specific `diagnostics[]` evidence while top-level `diagnostic` is authoritative
for call outcome.

## Continuation contract

Full inspect bodies are paged on UTF-8 code-point boundaries. Version-1 tokens checksum-bind:

- workspace ID;
- exact symbol ID;
- extractor body hash;
- source start and end bytes;
- next body byte offset.

Miller rejects malformed, non-canonical, stale, cross-workspace, cross-symbol, hash-mismatched, span-mismatched,
mid-code-point, unavailable-body, and wrong-target continuations as typed refusals. Compact, JSON, and CLI
resume the same byte sequence. Token serialization uses source-generated JSON metadata and passes Native AOT
publication.

Diagnostic action arguments are bounded to 160 UTF-16 characters without splitting surrogate pairs.

## Fault and parity evidence

| Gate | Result |
| --- | --- |
| schema incompatibility | typed `corruption/schema_incompatible` |
| corrupt search or dashboard sidecar | typed `corruption/artifact_corrupt` |
| missing or unavailable artifact | typed `unavailable` |
| unexpected provider failure | typed `internal_failure` |
| invalid Patterns input | typed `refusal/invalid_request`; CLI exit `2` |
| workspace safety refusal | typed `refusal` |
| empty and ambiguous tool results | typed successful empty result |
| telemetry without hard failure | no error kind/message/detail pollution |
| host without telemetry ledger | hard diagnostic still uses MCP error channel |
| diagnostic-looking source text | remains successful content |
| compact/JSON continuation | deterministic byte-identical reassembly |
| resumed body becomes unavailable | compact and JSON both refuse |

## Claude review disposition

| Finding | Resolution |
| --- | --- |
| CLI could not resume inspect bodies | added `inspect --continuation`, full-depth validation, and resume coverage |
| Patterns masked user input and unexpected failures | typed validation refusals; genuine unexpected failures remain internal failures |
| MCP hard-error behavior depended on telemetry | added typed async-local delivery independent of the ledger |
| tool-specific JSON transitions and trace dual diagnostics were unclear | updated central, trace, and Patterns contracts |
| refusals polluted error telemetry | tools classify before calling `SetError`; refusal telemetry remains empty |
| workspace safety refusals looked like generic empty results | classified live/sensitive/lock safety outcomes as `refusal` |
| Impact empty messages were generic | added code-specific messages and tests |
| compact continuation fixture was missing | added compact reassembly coverage |
| workspace and sidecar hard-failure fixtures were missing | added dashboard and search-sidecar corruption coverage |
| stale comments and formatting remained | corrected affected source and test documentation |
| continuation misuse, UTF-8 offsets, and action targets were weakly guarded | added target/applicability checks, code-point validation, bounded actions, and marker-specific empty guidance |
| continuation JSON was reflection-based | moved both payloads to source-generated JSON and proved Native AOT publish |
| JSON resume could silently return an unavailable body | routed resumed JSON bodies through the same refusal path as compact |
| CLI continuation refusal exited as an unexpected failure | mapped typed inspect refusals to CLI exit `2` and added success/refusal parity coverage |

## Verification

- focused diagnostic, continuation, CLI, telemetry, and tool integration tests: 32 passed.
- fast suite: 4,634 passed, two expected skips, under the 30-second ceiling.
- scale suite: 87 passed against the real `julie-extract`.
- Release build: zero warnings and zero errors.
- Native AOT `osx-arm64` publish: passed.
- `git diff --check`: clean.

## Phase 3 handoff

Phase 3 must migrate trace refs, inspect refs/callers, context usage, and rename onto the Phase 1 exact-reference
seam. Any reference list that can exceed its output budget must reuse this continuation contract rather than
inventing a tool-specific token or an MCP surface.
