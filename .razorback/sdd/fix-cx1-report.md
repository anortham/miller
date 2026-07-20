# fix-cx1 — cross-workspace semantic fusion used the wrong workspace's vectors

**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-p3` (branch `worktree-semantic-p3`)

## Finding

`SearchTool.Search` resolves `workspace_id` into `WorkspaceSymbolSearchContext` (possibly workspace B), but
`SymbolFusionRequest` carried no root, and the DI fusion arm built `SemanticSearchArm` from the ambient
`WorkspaceContext.WorkspaceRoot` (A). A `workspace_id`-routed hybrid search therefore joined B's index against
A's `vectors.db` — usually a silent semantic no-op (symbol ids do not resolve), and a wrong rerank on id
collision.

## Fix

Carry the resolved root through the fusion seam, mirroring F4's `SemanticTextArm` per-call-root pattern.

- `SearchRouteExecutionRequest` gains `WorkspaceRoot` (default `""`).
- `SymbolFusionRequest` gains `WorkspaceRoot`; `SearchRouteExecutor.FusionRequestFor` populates it.
- `SemanticSymbolFusionArm`'s factory becomes `Func<string, SemanticSearchArm>` and opens for
  `request.WorkspaceRoot`. The single-arm convenience ctor is preserved (`_ => arm`).
- `SearchTool` passes `context.WorkspaceRoot` from the resolved search context.
- `MillerServiceRegistration` opens the arm for the request root, falling back to the ambient workspace only
  when a caller supplied no root.
- `CliDispatch`: mechanical lambda-arity adaptation of the `--arm policy` factory only (`() =>` → `_ =>`).
  `ForcedHybridFusionArm` needed no change; its served/unserved logic (fix-cx3's) was untouched.

## Test

`HybridSearchTests.FusionArm_OpensTheArmForTheRequestWorkspaceRootNotTheAmbientOne` — a recording factory
asserts the arm is opened for the request's root (`/ws-b`) and never for the ambient `Root` (`/ws`).
Red before the fix (factory arity / root mismatch), green after.

## Verification

See "Status" below.

## Status

**Complete.** Commit `c632649` on `worktree-semantic-p3`.

- Red proof: with the arm pinned to the ambient root, the new test fails
  `Expected "/ws-b", Actual "/ws"`. Green after the fix.
- `dotnet test --filter "HybridSearch|SearchGoldenParity|HostStartupRegistration|SearchDeterminism|SearchRouteExecutor"`
  — 61/61 passed. Byte-identity and golden parity green.
- `scripts/test.sh` — 4159 passed, 2 skipped, 0 failed.

## Concerns

1. **`scripts/test.sh` wall-clock tripwire fires under parallel load.** Two runs: 32s and 142s, both with an
   identical 4161/4161 pass. Three other fix workers were building and testing concurrently. Machine
   contention, not a leaked slow test — but the branch gate should be re-run serially to confirm.
2. **`CliDispatch.cs` is shared with fix-cx3.** Only my one-line lambda-arity hunk was staged (via
   `git apply --cached`); fix-cx3's substantial in-flight `RunForcedArm` work remains uncommitted in the
   working tree. This commit alone does not compile without that line, so it must not be cherry-picked
   in isolation.
3. **Ambient fallback retained.** `MillerServiceRegistration` still falls back to `WorkspaceContext.WorkspaceRoot`
   when a request carries no root. The server path always populates it now; the fallback only covers callers
   that construct `SearchRouteExecutionRequest` without a root (the CLI, whose arms ignore the parameter).
