## Task 5: Align workspace facts, health, refresh, and normal CLI search

**Depends on:** Tasks 1 and 2.

**Owns:**

- `src/Miller.Server/Tools/WorkspaceTool.cs`
- `src/Miller.Server/Tools/WorkspaceFactsAssembler.cs`
- `src/Miller.Server/Tools/WorkspaceHealthFacts.cs`
- `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
- `src/Miller.Server/Cli/CliDispatch.cs`
- focused workspace and CLI tests

**Red tests:**

1. Current-workspace status/health JSON and compact output show ready, stale, unavailable, pending, and semantic-off states truthfully.
2. Stale or failed vectors affect health warning/action; semantic off does not make lexical workspace health fail.
3. Current-workspace CLI refresh advances vectors when allowed or returns the resident-leader requirement; foreign refresh never generates.
4. Normal eligible CLI search matches MCP production-arm output for symbol and content queries.
5. Forced CLI arms remain explicit evaluation-only behavior and retain loud validation for unsupported modes.
6. Eligible normal CLI searches write privacy-preserving canary telemetry; lexical-off output stays byte-identical.

**Implementation:**

- Reuse `VectorSidecar` facts already used for registered workspaces, including pending files.
- Add vector health rules without making optional/off semantic state unhealthy.
- Route current refresh through the shared vector convergence boundary or report the actual leader requirement.
- Compose normal CLI semantic/canary behavior from the same policy and arm implementations as the server; do not fork ranking logic.

**Worker verification:** focused `WorkspaceToolTests`, `WorkspaceRenderTests`, workspace-health tests, `CliDispatchTests`, and CLI semantic/canary tests.

