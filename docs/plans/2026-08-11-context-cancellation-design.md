# Context Tool Cancellation Design

**Date:** 2026-08-11

**Status:** Approved design; implementation pending.

## Problem

A canceled reference-aware `context` request continued consuming one CPU core after the client stopped waiting. The MCP handler does not accept a cancellation token, and its candidate/reference expansion performs substantial synchronous work across the workspace symbol graph.

## Architecture Quality

No Architecture Impact. This change preserves the existing context retrieval, ranking, rendering, and MCP result contracts. It adds cooperative cancellation to the existing synchronous pipeline.

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

- [ ] Canceling a running `context` request stops its synchronous work promptly.
- [ ] No background thread continues consuming a core after cancellation.
- [ ] Uncanceled compact and JSON results remain unchanged.
- [ ] The cancellation token is infrastructure-provided, not a published tool argument.
- [ ] Focused context tests pass with no warnings or errors.
