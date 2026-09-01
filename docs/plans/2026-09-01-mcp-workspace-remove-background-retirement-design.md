# MCP workspace remove: background store-view retirement

## Why

`workspace remove` / `prune` always wait on `julie-extract store maintain retire-view --apply` (default timeout 2 minutes) before deleting the registry row. That is load-bearing for family-store views, but it makes the MCP tool miss its 30s deadline. Observed: CLI remove of a missing worktree returned in 32s; MCP timed out at 30s. Sidecar reclaim then deleted 0 files.

## Decision

- **CLI** stays synchronous: capture → preview/apply retirement → `RecordIntent` → registry delete → sidecar reclaim. Operators can wait.
- **MCP** (and the dashboard POST, same serve process) returns as soon as the registry row is gone. Retirement and reclaim run on a background worker in that process.

No new MCP tool.

## Flow (MCP remove)

1. Capture the store view from `store_members` (same as today).
2. Write `StoreSidecarReclaim.RecordIntent` (`.reclaim-owed`).
3. Delete the registry row.
4. Delete the `.miller` dir if it exists (existing lock/CT-lease rules unchanged).
5. Return `removed` immediately. JSON must say retirement is **owed**, not completed. Do not report a fake `Retired` outcome.
6. Enqueue the captured target on an in-process queue. The worker runs today's `RetireView(..., apply: true)` then `StoreSidecarReclaim.Reclaim`. Success clears the owed record. Failure leaves it.

Crash between 2 and 6 is already the owed-record case. `DischargeOwed` / prune sweep remains the recovery path.

## Flow (MCP prune)

Same split: missing-root rows that pass today's linked-worktree proof are unregistered immediately. Producer `retire-view` is not awaited on the tool call. Dry-run stays a read-only preview and does not enqueue.

CLI prune still awaits producer retirement (existing `maxProducerRetirements` cap unchanged).

## Module shape

- `WorkspaceRemoval` keeps one sync path for CLI. Add a **commit** path used by MCP/dashboard that stops after registry delete + intent write, and a **finish** path the worker calls (`RetireView` apply + `Reclaim`).
- New queue lives in `Miller.Server` (channel + drain loop). Register it in `MillerServiceRegistration` as an `IHostedService` that does **not** read `IndexBootstrapService` getters in its constructor.
- `WorkspaceTool` remove/prune enqueue finish work; they do not spawn `julie-extract` on the tool thread.
- Reuse `StoreViewRetirementRunner` and `StoreSidecarReclaim`. Do not add a second retirement implementation.

## JSON / compact honesty

- MCP remove success: registry gone; `view_retirement` absent or explicitly owed — never `Retired` until the worker finishes.
- Compact next-step may say retirement is owed. `next_actions` JSON stays unchanged unless a test already asserts a field we must add; prefer a compact-only hint.

## Tests

- MCP-shaped remove of a missing-root family member returns without calling the retirement producer. Registry row is gone. Intent record exists. Worker invoke then retires and reclaim runs.
- CLI remove still calls apply and waits (existing `WorkspaceRemovalTests` stay green).
- If the worker is not run, a later `DischargeOwed` / prune sweep still finishes the view.
- No Scale spawn of `julie-extract` in the fast-path tests: inject a fake `retireView` / queue.

## Non-goals

- Changing julie-extract `retire-view`.
- Raising the MCP timeout.
- Backgrounding CLI remove.
- A new MCP tool or dashboard page for owed retirements.

## Acceptance

- [ ] MCP `workspace remove` of a missing-root registered worktree returns in well under 30s and the row is gone.
- [ ] Family-store view retirement still happens (in-process worker or later owed discharge).
- [ ] CLI `miller workspace remove` still waits and reports the real reclaim/retirement result.
- [ ] MCP prune dry-run does not enqueue work.
- [ ] Hosted worker construction does not touch bootstrap getters.
- [ ] Fast-suite tests cover the split without spawning `julie-extract`.
