### Task 7: Provenance surfacing + contract docs

**Files:**
- Modify: `src/Miller.Server/Tools/WorkspaceRender.cs` (`WorkspaceRender` :325+),
  `src/Miller.Dashboard/DashboardData.cs` (workspace detail),
  `docs/contracts/cli-eros-v1.md` (additive `rebound_from` section beside `scan_failure` :205)
- Test: `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`

**Interfaces:**
- Consumes: the three artifact metadata keys (`rebound_from_root`, `rebound_from_artifact_id`,
  `rebound_at`) read through the existing artifact-metadata read path that already serves
  `workspace status` facts; the registry (to resolve the source root to a display id when
  registered).
- Produces: an OPTIONAL additive `rebound_from` object in `workspace status --json` and
  `workspace health --json` — `{ "source_root": ..., "source_workspace": <display id or null>,
  "source_artifact_id": ..., "rebound_at": ... }` — present only when the artifact carries the
  keys; a one-line compact-status rendering ("rebound from `<display id>` at `<rebound_at>`");
  the same facts on the dashboard workspace detail.

**Contract inputs:** contract design §8 provenance surfacing; the `scan_failure` section of
`docs/contracts/cli-eros-v1.md:205` as the additive-conditional-object precedent (document shape,
optionality, and JSON stability the same way). No new MCP tools; no new CLI verbs — this rides
the existing status/health payloads.

**File ownership:** Modify `src/Miller.Server/Tools/WorkspaceRender.cs`, `src/Miller.Dashboard/DashboardData.cs`, `docs/contracts/cli-eros-v1.md`; Test `tests/Miller.Tests/Server/WorkspaceRenderTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch (no file overlap with Task 6; end-to-end JSON assertion runs at branch gate).

**What to build:** Make a rebound workspace say so: status/health JSON, compact status output, and
the dashboard each render the rebind provenance; the Eros-facing contract doc records the additive
object.

**Approach:** Fast tests drive the render from fixture metadata (keys present/absent —
absent renders nothing, no empty object). Follow `scan_failure`'s conditional-object pattern
exactly for JSON shape and doc language. Source display id resolves via the registry when the
source root is registered; otherwise `source_workspace` is null and the raw root still renders.

**Acceptance criteria:**
- [ ] `workspace status --json` and `health --json` include `rebound_from` exactly when the
      artifact carries the provenance keys; never an empty object.
- [ ] Compact status renders the one-line provenance; dashboard detail shows the same facts.
- [ ] `docs/contracts/cli-eros-v1.md` documents the additive object in the `scan_failure` style.
- [ ] Worker-scope verification passes and the change is handed to the lead per
      parallel-lead-commit.

---

## Program bookkeeping on completion

After Batch C lands and the branch gate passes, tick the P3 acceptance boxes in
`docs/plans/2026-08-02-worktree-delta-rebind-program.md` (§P3) and the P3 items in
`docs/plans/2026-08-05-rebind-contract-design.md` §9, citing the test evidence. P4 (scale
validation) remains a separate phase and is NOT part of this plan.
