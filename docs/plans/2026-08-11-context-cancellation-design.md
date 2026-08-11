# Context Tool Cancellation Design

**Date:** 2026-08-11

**Status:** Implemented and verified locally.

## Problem

A canceled reference-aware `context` request continued consuming one CPU core after the client stopped waiting. The MCP handler does not accept a cancellation token, and its candidate/reference expansion performs substantial synchronous work across the workspace symbol graph.

## Architecture Quality

- **Affected modules:** `ContextTool` MCP binding and its internal retrieval/render pipeline.
- **Caller-facing interface:** The published `context` name, JSON arguments, outputs, and direct `Context` wrapper are unchanged; only the framework-bound handler receives cancellation.
- **Depth/locality check:** Cancellation stays inside `ContextTool` and does not alter shared search, graph, or reference-reader interfaces.
- **Test surface:** Handler cancellation, generated JSON schema, and existing compact/JSON context behavior.
- **Seams/adapters:** Internal cancellation-aware entry points preserve existing non-cancelable test and CLI callers.
- **Rejected shortcuts:** Entry-only checks, fixed timeouts, and worker isolation.
- **Architecture risk:** Low.

## Design

- Accept the MCP framework-provided `CancellationToken` in `ContextTool.Context` without exposing it in the tool's JSON arguments.
- Propagate the token through actionable, reference-aware, candidate-building, reference-item, and budget-rendering phases that can perform substantial synchronous work.
- Check cancellation before expensive phases and inside corpus- or candidate-sized loops so cancellation latency is bounded by one iteration rather than the whole request.
- Preserve byte-identical output for requests whose token is not canceled.
- Let `OperationCanceledException` propagate through the MCP framework's normal cancellation path; do not convert cancellation into an error payload.

## Rejected Alternatives

- A fixed timeout does not honor immediate client cancellation and imposes a new product policy on legitimate large requests.
- Moving context work to an isolated worker is a broader lifecycle and IPC change that is unnecessary for cooperative cancellation.
- Checking only at handler entry does not stop a request canceled after candidate expansion begins.

## Verification

- Add a focused regression that starts expensive reference-aware context work, cancels it, and proves the request terminates through cancellation.
- Prove the test fails against the current non-cancelable implementation before changing production code.
- Run the focused `ContextToolTests` scope after implementation.
- Re-run Miller impact analysis on the final diff and confirm no MCP argument-schema change.

## Acceptance Criteria

- [x] Canceling a running `context` request stops its synchronous work promptly.
- [x] No background thread continues consuming a core after cancellation.
- [x] Uncanceled compact and JSON results remain unchanged.
- [x] The cancellation token is infrastructure-provided, not a published tool argument.
- [x] Focused context tests pass with no warnings or errors.
