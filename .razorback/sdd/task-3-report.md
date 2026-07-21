# Task 3 report — `miller semantic prepare` CLI verb (consented model download)

> Replaced a stale `task-3-report.md` from a DIFFERENT plan ("Edit failure-reason completeness"). This path
> collides across plans; this content is the P4 semantic Task 3 report.

## Status
Complete. Verb implemented, wired, and tested. Commit deferred (parallel-lead-commit).

## Files
- Create: `src/Miller.Server/Cli/SemanticPrepareCli.cs`
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` (verb-table case, `Semantic` dispatcher, help text)
- Test: `tests/Miller.Tests/Server/SemanticPrepareCliTests.cs` (create, 16 tests)

## Miller-first orientation (calls + what they proved)
Read/Grep against the worktree source the Miller MCP indexes (live build in this worktree). Evidence:
- `CliDispatch.cs` verb table (lines 90–154): flat `switch (verb.ToLowerInvariant())`, one case per verb →
  added `case "semantic"`. Proved the branch pattern (`version`/`dashboard` load no index).
- `CliDispatch.cs:525–532` (`RunSymbolRoute`): existing missing-sidecar wording
  (`"needs the pinned julie-semantic-sidecar binary under '{ctx.ToolsRoot}'; run the restore script and retry."`)
  — matched in the missing-binary refusal.
- `VectorConvergeService.cs:792–799` (`ProcessSession`): exact sidecar resolution
  (`Path.Combine(ToolsRoot, OperatingSystem.IsWindows() ? "julie-semantic-sidecar.exe" : "julie-semantic-sidecar")`)
  — reused verbatim.
- `DashboardCliLauncher.cs`: the established process-spawn shape (`ProcessStartInfo`, injected
  `Func<ProcessStartInfo,Process?>` seam, `UseShellExecute=false`, `CreateNoWindow`). Mirrored the
  inject-the-runner discipline instead of inventing a spawn path.
- `CliOptions.cs`: `Parse(args, booleanFlags…)`, `Value`/`Has`/`Positionals`/`FlagNames` — used for
  `--model`/`--json` parsing and unknown-flag rejection (same technique as the `rules` verb).
- `WorkspaceContext.cs`: `ExtractDbPath = <root>/.miller/symbols.db` → marker dir is
  `Path.GetDirectoryName(ExtractDbPath)`; `ToolsRoot` is app-base `.tools`, not the repo.
- `FakeSemanticSidecar.cs`: fakes the **RPC** protocol (health/embed), NOT the `prepare` subcommand — so
  prepare shells out as a plain child process, faked in the fast suite via an injected
  `SemanticPrepareProcessRunner` delegate (no RPC, no real spawn).

## API-shape evidence (every signature relied on)
- `WorkspaceContext.ToolsRoot` / `.ExtractDbPath` — record props (WorkspaceContext.cs:15–23).
- `CliOptions.Parse`/`Value`/`Has`/`Positionals`/`FlagNames` — CliOptions.cs.
- `CliDispatch.Usage(err, usage)` → 2 — existing helper.
- `Environment.ProcessId` (int), `DriveInfo.AvailableFreeSpace`, `Utf8JsonWriter`, `Process.Start` — BCL,
  AOT-safe (marker + JSON refusals via `Utf8JsonWriter`, no reflection serializer, so the Native-AOT main
  binary stays clean).

## Architecture as built
Approved shape held: **consent in Miller, mechanics in the sidecar.** `SemanticPrepareCli` is a pure core
with three injected seams — binary probe (`Func<string,bool>`), preflight (`ISemanticPreparePreflight`),
process runner (`SemanticPrepareProcessRunner`) — plus pid/clock funcs. `CliDispatch.Semantic` parses and
validates, then calls `SemanticPrepareCli.Production()`. No host build, no index load, no Serilog. No MCP
tool or parameter added (MCP-stinginess honored).

Flow: resolve `<ToolsRoot>/julie-semantic-sidecar[.exe]` → absent ⇒ fail loud (exit 3, restore-script
message), no marker; → disk preflight on the model cache dir, blocked ⇒ refuse (exit 3, free/required in
message), no spawn, no marker; → write marker → run child (`prepare [--model <id>] [--json]`) streaming
stdout/stderr → **finally** delete marker → return the child's exit code.

## Marker contract as implemented (load-bearing for Task 4)
- Path: `<workspace>/.miller/semantic-prepare.marker` (`SemanticPrepareCli.MarkerFileName`;
  `MarkerPathFor(millerDir)` helper exposed).
- Content: one JSON object, e.g.
  `{"model":"qwen3-0.6b-f16","pid":4242,"createdUtc":"2026-07-20T18:30:00.0000000Z"}`.
  - `model` — the `--model` value, or literal `"default"` (`DefaultModelLabel`) when omitted.
  - `pid` — CLI process id (`Environment.ProcessId`); Task 4 checks pid-alive, a dead-pid marker is stale.
  - `createdUtc` — ISO-8601 round-trip (`"O"`) UTC.
- Lifecycle: created BEFORE the child; ALWAYS deleted in a `finally` — success, nonzero exit, AND exception
  (delete is best-effort, never masks the child's exit code).
- Task 4 note: `VectorSidecar` (Miller.Indexing) cannot reference Miller.Server, so Task 4 must derive the
  same path and parse these three fields on its side; the constant/format above is the contract to mirror.

## Disk preflight seam (plan-mismatch note — anticipated by the brief)
`src/Miller.Indexing/Semantic/DiskPreflight.cs` (Task 2) does NOT exist in this lane yet. Per instructions
I did NOT create it. The verb owns a local seam (`ISemanticPreparePreflight`) with a conservative
`DefaultPreflight`: nearest-existing-ancestor `DriveInfo.AvailableFreeSpace` on the resolved cache dir vs a
stated `DefaultRequiredBytes = 1.2 GiB`; a probe fault returns unknown/OK so a glitch never blocks a
consented download. **Wiring to the shared `DiskPreflight` is Task 4's lane-2 slot** (swap `DefaultPreflight`;
footprint constant should track Task 7's Q8_0 benchmark).

Cache dir resolution (preflight only; Miller never parses model URLs): `JULIE_EMBEDDING_CACHE_DIR` → else
`%LOCALAPPDATA%/julie-semantic` (Windows) / `$XDG_CACHE_HOME|~/.cache` + `/julie-semantic` (unix).

## `--json`
Passes through to the sidecar (`prepare … --json`, sidecar owns progress format) AND governs Miller's
PRE-spawn refusal output: missing-binary/disk-blocked emit a JSON object to stdout
(`{"status":"sidecar_missing"|"disk_blocked","message":…,"free_bytes":…,"required_bytes":…}`) instead of a
stderr line. After the child spawns, the sidecar owns all output.

## Gate invariants (per test)
worker-red-green: `dotnet test --filter FullyQualifiedName~SemanticPrepareCliTests` → **16 passed, 0 failed**.
- `CreatesMarkerBeforeSpawn_RecordingModelAndPid_AndDeletesOnSuccess` — marker exists during the child with
  model+pid+createdUtc; gone after (live exactly while downloading).
- `WithoutModel_RecordsDefaultLabelInMarker` — omitted `--model` ⇒ `"default"`.
- `DeletesMarker_WhenSidecarFails` / `DeletesMarker_WhenRunnerThrows` — finally-delete on failure AND
  exception (no stale live-marker after a crash).
- `PassesExitCodeThrough` — sidecar status is the verb's status.
- `ForwardsPrepareSubcommand_ModelAndJsonFlags` / `ForwardsBarePrepare_WhenNoModelOrJson` — arg contract
  `prepare [--model <id>] [--json]`.
- `MissingBinary_FailsLoud_…_NoSpawn_NoMarker` + `_Json_…` — exit 3, restore wording, no spawn/marker; JSON
  refusal on stdout.
- `PreflightBlocked_ShortCircuits_NoSpawn_NoMarker_…` + `_Json_CarriesFreeAndRequiredBytes` — refusal
  short-circuits the spawn with free/required facts.
- `Dispatch_SemanticWithoutOperation` / `_UnknownSemanticOperation` / `_UnknownOption` — usage errors (2).
- `Dispatch_PrepareWithMissingSidecar_ReturnsOperationalFailure` — end-to-end wiring ⇒ exit 3.
- `Help_DocumentsTheSemanticVerb` — verb registered in `help`.

worker-ceiling: `scripts/test.sh` (fast) → **4188 passed, 2 skipped, 0 failed**, wall 28s (< 30s ceiling;
the >10s target is Task 8's separate concern — these 16 tests add ~54ms). `dotnet build -c Release`:
0 warnings / 0 errors (warnings-are-errors, AOT-clean).

## Decisions
- No real-process test — packaged smoke covers a real spawn; the fast suite fakes the runner (brief's
  stated preference). No `[Trait("Category","Scale")]` needed.
- Exit codes: 0 / sidecar-passthrough on spawn; 3 for Miller's operational refusals; usage errors (2) in
  CliDispatch.
- `Run` lets an unexpected runner exception propagate (marker still deleted in finally); CliDispatch's outer
  catch maps it to exit 1 — consistent with every other verb.

## Concerns
- None blocking. Coordination note: Task 4 must mirror the marker path+JSON in `Miller.Indexing` and swap
  the placeholder `DefaultPreflight` for the shared `DiskPreflight` + Task 7's real footprint constant.
