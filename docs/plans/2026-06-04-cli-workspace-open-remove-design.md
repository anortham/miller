# CLI `workspace open` / `workspace remove` — design

Date: 2026-06-04
Status: approved-pending-review
Scope: close the CLI/server parity gap for workspace lifecycle. Add `miller workspace open`
(bootstrap + index a fresh directory from the CLI) and `miller workspace remove` (delete a
workspace's `.miller` index dir from the CLI).

## Problem

The one-shot CLI (`CliDispatch.Workspace`) wires only `status | list | refresh | full`.
`refresh`/`full` route through `CrossWorkspaceRefreshService.Refresh(id, force)`, which resolves an
**already-registered** registry row by selector — so on a fresh, unregistered directory they return
exit 2 with "the current workspace is not registered…". There is no CLI way to build an index from
scratch, which blocks a true CLI-first / CI workflow (`cd repo && miller workspace open && miller search …`).

The **server** already implements both ops (`WorkspaceTool.Open` "prime a path", `WorkspaceTool.Remove`).
The gap is purely that the CLI dispatch doesn't expose them.

## Goal

- `miller workspace open [--path DIR] [--full] [--json]` — register the target dir and index it
  (julie-extract scan + search sidecar), creating `.miller/symbols.db` on first run.
- `miller workspace remove (--id ID | --path DIR) [--json]` — delete a workspace's `.miller` index dir
  and unregister it, guarded by the cross-process writer lock.
- Reuse the existing, tested services. No changes outside `CliDispatch.cs` + the CLI test files.

## Non-goals

- No new MCP tool surface (the `workspace` MCP tool already documents open/remove).
- No change to the server's `WorkspaceTool` / `CrossWorkspaceRefreshService` / renderers.
- No "live switch" semantics — the CLI is one-shot; there is no served index in-process.

## Design

### `workspace open`

Target root = `--path DIR` if given, else the current workspace (`ctx.WorkspaceRoot`). This makes the
80% flow `cd repo && miller workspace open` work, while `--path` mirrors the existing selector style.

Flow (all in a new `WorkspaceOpen(ctx, path, full, json, outw, err)` in `CliDispatch`). **Ordering is
load-bearing** — see the two review fixes inline (R1, R3):

1. `targetRoot = string.IsNullOrWhiteSpace(path) ? ctx.WorkspaceRoot : path`.
2. If `!Directory.Exists(targetRoot)` → `err`: "cannot open: no directory at '…'." → **exit 2**.
   (`PathCanonicalizer.CanonicalizeRoot` requires an existing dir, so this check comes first.)
3. **Canonicalize first** (R3): `canonicalRoot = PathCanonicalizer.CanonicalizeRoot(targetRoot)`. Deriving
   the symlink-resolved root *before* the safety check is what lets the guard catch a symlink that points
   at `~`/`/`.
4. **Sensitive-root guard on the canonical root** (R3 — was pre-canonicalization in the first draft, which
   a symlink alias could slip past): if
   `WorkspaceRootSafety.IsSensitiveRoot(canonicalRoot, WorkspaceRootSafety.SensitiveRootCandidates())`
   → `err`: "refusing to index sensitive system path '…': choose a project directory." → **exit 2**,
   **before any registry write**. (Guards `cd ~ && miller workspace open`, `--path /`, and a symlink to either.)
5. Derive identity: `millerDir = <canonicalRoot>/.miller`, `dbPath = <millerDir>/symbols.db`,
   `id = WorkspaceId.FromCanonicalRoot(canonicalRoot)`, `display = WorkspaceId.Display(canonicalRoot, id)`.
6. **Locate the tool + construct the services BEFORE registering** (R1 — a missing `julie-extract` must not
   leave an orphan `ready` row): `JulieExtractRunner.Locate` throws `FileNotFoundException` (the
   restore-script message) when the binary is absent. Do it first; on throw, write the message to `err` and
   **exit 3** with **no registry row written**.
   ```csharp
   JulieExtractRunner runner = JulieExtractRunner.Locate(ctx.ToolsRoot); // may throw → exit 3, no row
   var sidecar = SymbolSearchSidecar.FromEnvironment();
   var refresh = new CrossWorkspaceRefreshService(registry, runner, sidecar);
   ```
7. **Register** the row (only now that the tool is confirmed present):
   `registry.UpsertSeen(id, display, canonicalRoot, dbPath, WorkspaceRegistryState.Ready)`.
8. **Index** via the same machinery `full`/`refresh` already use:
   ```csharp
   WorkspaceRefreshResult result = refresh.Refresh(id, force: full);
   ```
   `Refresh` acquires the single-writer lock (which `Directory.CreateDirectory`s `.miller` on first run),
   runs julie-extract (creating `symbols.db`), reads the revision, **best-effort builds the search sidecar
   when enabled** (R2 — `MarkScanned` happens first and a sidecar failure is swallowed by design; reads
   self-heal to in-memory BM25, and `MILLER_SEARCH_SIDECAR=0` skips it), and marks the row scanned. A scan
   that *fails* now marks the **existing** row error (visible in `list`) — that is the property register-
   before-scan buys us. `--full` → `force:true` (from-scratch rebuild); default → `force:false` (delta on
   re-open; a fresh dir has no DB so it is a full initial scan regardless).
9. **Render + exit** via `WorkspaceRender.Action(operation: "open", …)` for **all** outcomes, exit via
   the existing `RefreshExitCode(result.Status)`:
   ```csharp
   var action = new WorkspaceActionResult(
       Operation: "open", Scanned: result.Scanned, Swapped: false, Revision: result.Revision ?? 0,
       Note: result.Error ?? result.WarningText, WorkspaceId: result.WorkspaceId,
       Root: result.WorkspaceRoot, Status: result.StatusText);
   outw.WriteLine(WorkspaceRender.Action(action, json));
   return RefreshExitCode(result.Status);
   ```

**Why `WorkspaceRender.Action`, not `WorkspaceRender.Open`:** the server's `Open` renderer hardcodes
"primed this path's index … NOT a live switch. This Miller keeps serving its launch directory" — that
copy is server semantics and is **false in the CLI** (the CLI has no live served workspace; `open` on
the current dir *is* the workspace you'll use). The `Action` renderer reports `root / status / scanned /
revision` honestly and uniformly with `refresh`/`full`. The indexed-symbol count is one
`miller workspace status` away.

**Idempotency:** a second `open` finds the row registered + DB present (root matches) → `Refresh` does a
delta scan → `Unchanged`/`Refreshed`, exit 0. `--full` forces a rebuild.

**Failure honesty:** a registered-then-failed scan leaves the row in an error state (via
`CrossWorkspaceRefreshService`'s `MarkError`/`MarkMissing`), visible in `workspace list` — correct for a
CLI bootstrap (the workspace is known, with its failure recorded). Exit is non-zero (3) so
`miller workspace open && deploy` cannot proceed on a broken index.

### `workspace remove`

New `WorkspaceRemove(ctx, id, path, json, outw, err)` in `CliDispatch`, porting the essential logic from
`WorkspaceTool.Remove` minus the in-process "live workspace" refusal (the CLI serves nothing in-process;
the cross-process writer lock is the only guard needed).

Require an explicit selector — **no current-dir default** (removing the dir you're standing in by
accident is a foot-gun; the server also requires a selector):

1. If both `--id` and `--path` are blank → `err` usage: "workspace remove requires --id <display-id> or
   --path <dir>." → **exit 2**.
2. **By `--id`** (takes precedence):
   - `row = WorkspaceRegistrySelector.Resolve(registry, id)`; `KeyNotFoundException` → `err` (its message)
     → **exit 2**.
   - `millerDir = Path.GetDirectoryName(row.IndexDbPath)`.
   - If `!Directory.Exists(millerDir)` → `registry.Remove(row.WorkspaceId)`; render
     `WorkspaceRemoveResult.NotFound(millerDir, id, root)` → **exit 0** (clean no-op; stale row pruned).
   - `lease = SingleWriterLock.TryAcquire(millerDir)`; `null` → render `RefusedInUse(…)` → **exit 3**.
   - Else `Directory.Delete(millerDir, recursive: true)`; `registry.Remove(row.WorkspaceId)`; render
     `Removed(…)` → **exit 0**.
3. **By `--path`** (no `--id`):
   - **Missing dir (R4):** if `!Directory.Exists(path)`, the dir is gone but a stale registry row may still
     point at it, and `PathCanonicalizer.CanonicalizeRoot` cannot run on a missing dir. Match lexically:
     `full = Path.GetFullPath(path)`; if any `registry.List()` row has `CanonicalRoot == full` (OS-case
     comparison, like `WorkspaceRootSafety`), `registry.Remove(row.WorkspaceId)` and render `Removed` →
     **exit 0**; else render `NotFound(<full>/.miller)` → **exit 0**. (A CI teardown that already deleted the
     repo can still prune the registry with `remove --path $REPO`.)
   - **Existing dir:** `canonicalRoot = PathCanonicalizer.CanonicalizeRoot(path)`; find the row by canonical
     root (`registry.List()` match on `CanonicalRoot` ordinal, else `WorkspaceSafety.IsLiveWorkspace`). If a
     row is found → same delete + unregister as the id branch.
   - If no row → backward-compatible local cleanup: `millerDir = <full>/.miller`; not-exists → `NotFound`
     exit 0; lock busy → `RefusedInUse` exit 3; else delete → `Removed` exit 0 (no row to unregister).
4. Render via `WorkspaceRender.Remove(result, json)`; exit via a new `RemoveExitCode` helper:
   ```csharp
   internal static int RemoveExitCode(WorkspaceRemoveResult.Outcome outcome) => outcome switch
   {
       WorkspaceRemoveResult.Outcome.Removed or WorkspaceRemoveResult.Outcome.NotFound => 0,
       WorkspaceRemoveResult.Outcome.RefusedInUse or WorkspaceRemoveResult.Outcome.RefusedLive => 3,
       _ => 1,
   };
   ```
   (`RefusedLive` cannot occur in the CLI but is mapped for completeness.) **R5 — call site:** the
   `WorkspaceRemoveResult` record's enum-typed property is `Result`, not `Outcome`, so the call is
   `RemoveExitCode(result.Result)` (and `WorkspaceRender.Remove(result, json)` for the text).

### Dispatch + parsing

In `CliDispatch.Workspace`:
- Parse with `CliOptions.Parse(args, "json", "full")` so `--full` is a known boolean flag.
- Add `case "open": return WorkspaceOpen(ctx, path, o.Has("full"), json, outw, err);`
- Add `case "remove": return WorkspaceRemove(ctx, id, path, json, outw, err);`
- Update the default-branch message to list `status|list|refresh|full|open|remove`.

### Help text

Update the `workspace [op]` block in `HelpText`:
```
  workspace [op]     Index lifecycle. op = status (default) | list | refresh | full | open | remove.
                     open   [--path DIR] [--full]   Register + index a directory (creates .miller/symbols.db).
                     remove (--id ID | --path DIR)  Delete a workspace's .miller index dir.
                     status|refresh|full [--id ID | --path DIR] [--json]
```

## Files

- `src/Miller.Server/Cli/CliDispatch.cs` — add `open`/`remove` dispatch cases; `WorkspaceOpen`,
  `WorkspaceRemove`, `RemoveExitCode`; update `HelpText`. **Only production file changed.**
- `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` — fast, in-process tests (no julie).
- `tests/Miller.Tests/Server/Cli/CliBinarySubprocessTests.cs` — Scale, real-binary end-to-end.

## Test plan (TDD)

### Fast suite — `CliDispatchTests` (no subprocess, no julie)

`remove` is fully fast-testable (it only deletes dirs + registry rows); `open`'s happy path needs julie
(Scale), but its **guard** branches are fast:

- `open` guards (no julie reached):
  - `workspace open --path <nonexistent>` → exit 2, "no directory".
  - `workspace open --path /` → exit 2, "sensitive" (a filesystem root has no parent → always sensitive).
  - **R3** `workspace open --path <symlink→/>` (create a temp symlink to `/`) → exit 2, and assert the
    registry has **no** row for it (the guard fired before `UpsertSeen`).
  - **R1** `workspace open` with `ctx.ToolsRoot` pointing at an empty dir (no `julie-extract`) → exit 3,
    and assert `registry.List()` has **no** row (Locate threw before registration). This reaches
    `JulieExtractRunner.Locate` but **not** a julie subprocess, so it stays in the fast suite.
    *Caveat:* `Locate(toolsRoot)` also searches `PATH`, so the test must run with `julie-extract` **not**
    on `PATH` to fail deterministically (it normally lives at `.tools/`, off `PATH`). If that can't be
    guaranteed in CI, set `PATH=""` for the test or move this assertion to the Scale suite with a bogus
    `ToolsRoot`. Do **not** add a production locate-seam just for the test.
- `remove`:
  - `workspace remove` (no selector) → exit 2 usage.
  - `workspace remove --id <unknown>` → exit 2.
  - `workspace remove --path <dir with a pre-created .miller/>` → exit 0, dir deleted (assert gone). No julie:
    just `Directory.CreateDirectory(<dir>/.miller)`.
  - `workspace remove --path <dir without .miller>` → exit 0, `not found`.
  - `workspace remove --id <seeded row whose .miller exists>` → exit 0, dir deleted **and** row unregistered
    (assert `registry.Get(id)` is null).
  - **RefusedInUse:** hold `SingleWriterLock.TryAcquire(<millerDir>)` in-test, then `remove --path <dir>`
    → exit 3, `in use`, dir still present.
  - **R4** `workspace remove --path <gone dir with a seeded row>` → exit 0, `removed`, row pruned
    (`registry.Get(id)` null). Seed a row with `CanonicalRoot = Path.GetFullPath(<dir>)`; do **not** create
    the dir.
- `RemoveExitCode` `[Theory]` over all four outcomes (mirrors the existing `RefreshExitCode` theory):
  Removed→0, NotFound→0, RefusedInUse→3, RefusedLive→3.

### Scale suite — `CliBinarySubprocessTests` (real `miller` binary + julie)

Extend with a from-scratch bootstrap test (the path no in-process test can reach — proves `open` builds
an index in a fresh dir via a real second process):

- Fresh source tree (no pre-built DB). `miller workspace open` → exit 0; assert `<root>/.miller/symbols.db`
  now exists; then `miller search <Symbol>` → exit 0 and finds the symbol.
- Idempotency: second `miller workspace open` → exit 0 (status `unchanged`).
- `--full` (R-test): `miller workspace open --full` → exit 0 with `scanned: yes`, proving `--full` reaches
  `force:true` (a delta re-open without `--full` reports `unchanged`/`scanned: no`).
- `miller workspace remove --path <root>` → exit 0; assert `<root>/.miller` is gone.

Note (R2): the open Scale test asserts `symbols.db` + that `search` works, but does **not** assert the
`search.db` sidecar exists — `search` self-heals to in-memory BM25, so a sidecar miss would not fail it
(a false positive). The sidecar's on-disk build is already pinned by `CrossWorkspaceRefreshServiceTests`.

## Acceptance criteria

- [ ] `miller workspace open` on a fresh, unregistered dir builds `.miller/symbols.db`, registers the
      workspace, builds the search sidecar when enabled (best-effort), and exits 0.
- [ ] A missing `julie-extract` fails `open` with exit 3 and **no registry row written** (R1).
- [ ] A symlink whose canonical target is a sensitive root is refused (exit 2) before any registry write (R3).
- [ ] `miller workspace open --path DIR` targets DIR; default targets the current dir.
- [ ] `--full` forces a from-scratch rebuild; default re-open is a cheap delta (exit 0, `unchanged`).
- [ ] Sensitive-root and missing-dir targets are refused with exit 2 before any registry write.
- [ ] Every non-success `open` outcome (missing root, scan failure, lock busy) exits non-zero (3); the
      registry row records the failure.
- [ ] `miller workspace remove --id ID` / `--path DIR` deletes the `.miller` dir and unregisters the row;
      a lock held by another writer yields `RefusedInUse` exit 3 without deleting; a missing index is a
      clean `not found` exit 0; an unknown selector / missing selector is exit 2.
- [ ] Help text and the unknown-operation message list `open` and `remove`.
- [ ] No production change outside `CliDispatch.cs`.
- [ ] `dotnet build Miller.slnx -c Release` is 0 warnings / 0 errors.
- [ ] `scripts/test.sh` (fast) and `scripts/test.sh scale` both pass; the scale guard (`ScaleTraitConventionTests`)
      stays green (the new subprocess assertions live in the already-Scale `CliBinarySubprocessTests`).
```
