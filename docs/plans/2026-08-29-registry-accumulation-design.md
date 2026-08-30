# Workspace registry accumulation — design

Date: 2026-08-29
Status: approved, ready to implement
Branch: `worktree-dashboard-smoothness` (continuation — step 3 touches files the dashboard commit changed)

## Problem

The registry accumulates rows for agent worktrees that no longer exist, and `workspace prune`
cannot clear them.

Measured on the author's `~/.miller/workspaces.db`: 59 rows, 29 roots gone. Every dead root is an
ephemeral worktree or temp dir — 16 under `.claude/worktrees`, 10 under `.worktrees`, 3 under
`/tmp`. Not one real checkout died. Row names like `wf_1f3c0682-fde-1` … `-5` are workflow-created
worktrees: the agent harness makes them, Miller registers them, nothing unregisters them.

Registration needs no tool call. `IndexBootstrapService.StartAsync` resolves the process cwd and
inserts a row at state `refreshing` before the scan runs, and the plugin launches with `"cwd": "."`.
Any directory an agent session starts in becomes a permanent row. `/tmp` and `/home/murphy/source`
are both rows today, both stuck at `refreshing`.

Nothing removes them. `WorkspaceRegistry.Remove` has three call sites, all user-triggered
(`WorkspaceRegistryPrune.cs:144`, `WorkspaceRemoval.cs:323`, `WorkspaceRemoval.cs:386`). No hosted
service touches the registry.

## Why prune refuses

Replaying `WorkspaceRegistryPrune.Run`'s own predicate over the live registry:

| Outcome | Rows |
|---|---|
| Root present — kept, correctly | 30 |
| Root gone, not a store member — prunes freely | 4 |
| Root gone, passes the confirmation gate | 2 (capped at 1 per run) |
| Root gone, **blocked permanently** | **23** |

Run 1 removes 4–5, run 2 removes 1, every run after removes 0. The registry settles at 53 rows with
23 dead ones.

`HasConfirmedLinkedWorktreeRemoval` (`WorkspaceRegistryPrune.cs:162-181`) requires
`git_is_linked == 1`, a non-blank `git_dir`, `git_dir` itself gone, **and its parent directory
present**. Two defects:

- The parent is `<repo>/.git/worktrees`, which **git deletes when the last worktree goes**. Tidy
  cleanup is what makes a row unprunable. 3 rows fail this way.
- `workspaces.git_is_linked` / `git_dir` are NULL on 38 of 59 rows (the columns were added later),
  and NULL can never pass. 20 rows fail this way.

`MaxProducerRetirementsPerRun = 1` (`:26`), and `producerRetirements++` at `:119` runs **before**
`TryRetireView` at `:120`, so a failed retirement burns the single slot.

## The lineage is not missing

Of 47 store-member rows: 19 carry `workspaces.git_dir`; **24 more carry
`store_members.root_git_dir`** where the workspace column is NULL; only 4 have neither. 43 of 47
carry `store_families.canonical_common_dir`. Prune already loads the member row one line earlier via
`WorkspaceRemoval.CaptureStoreView`.

A corrected gate reading those columns confirms **23 of 25** dead member rows instead of 2.

## Live bug

```
$ miller workspace status --workspace-id miller
ambiguous workspace selector 'miller'. Matches: miller-91250a0fd4f3,
miller-release-smoke.Jmoi8N-dd49aef90432.
```

A dead `/tmp` row from a release smoke test blocks the obvious short name for the main repo.
Reproduced on the live registry.

## What already exists

`miller workspace list` already prints `missing roots: 29 — preview registry cleanup with a prune
dry run` and marks rows `state: ready (root missing)`. The suggestion is real and honest; it points
at a prune that then refuses. `workspace status` and `workspace onboarding` never mention it.
`MILLER_AGENT_INSTRUCTIONS.md` contains no `prune` and has 12 bytes of headroom — it must not grow.

## Non-goals

- **No automatic sweeper.** `docs/plans/2026-08-28-worktree-view-retirement-design.md:108-109`
  rejects automatic startup pruning. Prune destroys real data — a store view plus its whole per-view
  sidecar set — and `Directory.Exists` returns false on any error, not only on absence. Nothing here
  reverses that decision.
- No new MCP tool.
- No schema migration. Every column this design reads already exists.
- Do not fix the ~1 s `ReadSnapshot` detail-page read; out of scope.

## Step 1 — fix the proof

`src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs`

- `HasConfirmedLinkedWorktreeRemoval` takes the registry (or the already-captured member/family) so
  it can fall back: admin dir = `row.GitDir ?? member.RootGitDir`; common dir =
  `row.GitCommonDir ?? family.CanonicalCommonDir`.
- Replace the admin-**parent** test with a **common-dir** test: the repository's common dir is
  present and readable, and this worktree's admin dir is gone.
- Refuse when common dir equals admin dir — that is a plain checkout, never a linked worktree.
- Refuse when `git_is_linked == 0` (an explicit negative still means no).
- Move `producerRetirements++` to **after** a successful `TryRetireView`.
- Make the cap a `Run()` parameter, default 5, instead of the hard `1`.

This is **stricter**, not looser. An unmounted worktree volume leaves the admin dir intact → refuse.
A whole repo on an unmounted volume loses the common dir → refuse. A permission fault makes both
unreadable → refuse. Today's gate reads a directory git deletes on purpose; the new one reads a
directory that only vanishes with the repository.

## Step 2 — stop creating unprunable rows

- `src/Miller.Server/Tools/WorkspaceTool.cs:1269` and `src/Miller.Server/Cli/CliDispatch.cs:3632` —
  both `workspace open` paths currently pass no lineage. Pass captured lineage.
  `RegisterRefreshing` already accepts an optional `lineage`, and `CaptureLineage`
  (`IndexBootstrapService.cs:1639-1650`) is filesystem-only and never throws.
- Re-capture lineage on `UpsertSeen` when the root exists, so rows written before the columns
  existed heal on their next touch.

Removes zero rows. Makes every future row prunable.

## Step 3 — fix the visible harms

- `src/Miller.Server/Workspaces/WorkspaceRegistrySelector.cs` — on a **tie only**, prefer the
  candidate whose root exists. Do not change unambiguous resolution, and do not hide a genuine
  ambiguity between two live roots.
- `src/Miller.Dashboard/DashboardData.cs:650-651` — skip `DashboardIndexFactsCache.Read` when the
  root does not exist. That is 29 reads per page today, each of which throws.
- `src/Miller.Dashboard/Components/WorkspaceIndex.razor:51,134` — the button says "Prune 29 stale"
  and one click delivers 4. Dry-run first, show the real split, then apply.

## Step 4 — say it where agents look

- A one-line `NextStepHint` on `workspace status` and `workspace onboarding`, above a threshold
  (10 or more dead rows **and** 25% of the registry). `ToolDiagnosticAction.CompactOnly` per
  ADR-0001 — the JSON stays byte-identical, and the tool `[Description]` budgets in
  `AgentInstructionsTests` must still pass.
- `hooks/miller-routing-block.md` rule 7 fires only "after `git worktree remove <path>` succeeds".
  Reword to cover deletion by any means, and add a session-end `workspace prune dry_run=true`.
- Do **not** touch `MILLER_AGENT_INSTRUCTIONS.md`; it has 12 bytes of headroom.
- After editing `CLAUDE.md`, run `scripts/sync-agents.sh` and confirm `cmp -s CLAUDE.md AGENTS.md`.

## Acceptance criteria

- [ ] A dead linked-worktree row whose `workspaces.git_dir` is NULL but whose
      `store_members.root_git_dir` is set is confirmed for pruning.
- [ ] A dead row whose `<repo>/.git/worktrees` parent is gone but whose repo common dir is present
      is confirmed for pruning.
- [ ] A row whose admin dir is still present is refused (the unmount case).
- [ ] A row whose common dir is absent or unreadable is refused (the whole-volume unmount case).
- [ ] A row whose common dir equals its admin dir is refused (a plain checkout).
- [ ] A row with `git_is_linked = 0` is refused.
- [ ] A failed `TryRetireView` does not consume a retirement slot.
- [ ] The per-run cap is a parameter with default 5; a caller can still pass 1.
- [ ] Both `workspace open` paths persist lineage; a row touched by `UpsertSeen` with a live root
      gains lineage it lacked.
- [ ] An ambiguous selector that matches one live root and one dead root resolves to the live one.
      Two live roots still report ambiguous.
- [ ] The dashboard performs no facts read for a row whose root is gone.
- [ ] The dashboard prune control reports the count prune will actually remove.
- [ ] `workspace status` and `onboarding` emit the stale-registry hint only above the threshold, and
      only in compact output.
- [ ] `dotnet build Miller.slnx -c Release` is 0 warnings / 0 errors.
- [ ] The fast suite passes (baseline on this branch: 9263 passed, 0 failed, 9 skipped).
- [ ] `cmp -s CLAUDE.md AGENTS.md` if `CLAUDE.md` changed.

## Work split and file ownership

| Lane | Source files | Test files |
|---|---|---|
| 1 — prune proof | `Workspaces/WorkspaceRegistryPrune.cs` | `Server/WorkspaceRegistryPruneTests.cs` |
| 2 — lineage capture + hint | `Tools/WorkspaceTool.cs`, `Cli/CliDispatch.cs`, `Hosting/IndexBootstrapService.cs` | `Server/WorkspaceToolPruneTests.cs` |
| 3 — selector tie-break | `Workspaces/WorkspaceRegistrySelector.cs` | new `Server/WorkspaceRegistrySelectorTests.cs` |
| 4 — dashboard | `Miller.Dashboard/DashboardData.cs`, `Components/WorkspaceIndex.razor` | `Server/DashboardRegistryReadTests.cs` |
| 5 — guidance text | `hooks/miller-routing-block.md`, `CLAUDE.md`, `AGENTS.md` | `Docs/*` if a guard exists |

Lane 2 owns `WorkspaceTool.cs` entirely, including the step-4 `NextStepHint`, so lane 5 stays
documentation-only.
