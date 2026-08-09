# Index Store Ph3 Miller Acceptance

Status: READY FOR PH4 2026-08-09. The earlier local acceptance overclaimed producer evidence
and did not match several shipped Miller paths. A1-A7 are now implemented and verified. Store mode
remains explicit with `MILLER_INDEX_STORE=1`; physical-byte aggregation and default-on adoption remain
Ph5 decisions.

## What landed

- Every store-mode reader resolves one validated family/view/generation snapshot. No store read path falls
  back to a stale standalone artifact.
- Bootstrap imports the existing artifact once, binds fresh linked worktrees without copying it, serves L1,
  deepens and resolves through `julie-extract`, and reuses committed work after restart or takeover.
- Search, content, vectors, usage, history, and structural-fact consumers use view-scoped compatibility
  projections. Store sidecars advance from `store_log` sequence cursors and publish checked completeness
  stamps.
- Turning store mode off exports the active view before legacy serving. Export failure leaves the workspace
  not ready instead of serving stale bytes.
- Status, health, and dashboard surfaces add family, view, generation, manifest, level, resolution,
  migration, rollback, and failure provenance. Legacy output remains unchanged when store mode is off.

## Cleanup evidence

The review gaps were converted into the v4.1 amendments and verified as follows:

- A1/A2: producer GC steps incremental vacuum within its configured budget, reports store and
  producer-owned physical bytes before/after the final truncate checkpoint, persists physical
  target-breach streaks, and emits `compaction_required`; the physical ceiling is reported as a
  separate pressure guard. Miller-owned sidecars remain a separate Ph5 aggregate measurement.
- A3: import, update, and `--from-artifact` preflight capacity before store mutation and return
  `capacity_insufficient` when the peak cannot fit.
- A4: equivalence, mixed-version, crash, and resolution comparisons construct the complete 38-
  language fixture matrix and assert the exact producer-supported set.
- A5: malformed pointer metadata forces source reconciliation in bootstrap and cross-workspace
  refresh; a valid pointer whose store cannot open is preserved and remains not-ready.
- A6: progressive level-up reads the family-store session, so an L1 store schedules the Full
  upgrade instead of consulting the legacy artifact.
- A7: the family-store read session pins store instance, view, generation, manifest generation,
  per-level state, resolution state, manifest hash, and store-log sequence. Workspace cache keys and
  sidecar completeness stamps carry the same identity. Machine-governor admission ends before
  sidecar convergence; the family-scoped sidecar-converger lease covers content/search/vector writes,
  vector shadow lifecycle, creation/recovery, and retained-generation GC. The coordinator and sidecar
  serialization tests plus the focused, fast, build, and full Scale gates pass.

The earlier process-count, scale-duration, and dogfood claims remain historical notes; the commands
below are the current cleanup evidence against the 2.31.1 producer.

Final branch evidence:

- `scripts/test.sh`: 6,224 fast tests passed, two skipped, and completed in 27 seconds under the
  30-second ceiling.
- `scripts/test.sh scale`: 133 Scale tests passed, five optional-runtime tests skipped, and no
  failures occurred in 3 minutes 26 seconds.
- The Julie artifact maintenance contract passed 15/15; the full Julie CLI store contract feature
  suite passed, including equivalence, mixed-version, resolution, crash, maintenance, and the
  all-language fixture matrix.
- `dotnet build Miller.slnx -c Release --no-restore`: succeeded with zero warnings and zero errors.
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-build --filter
  'FullyQualifiedName~StoreWorkspaceIndexProviderScaleTests'`: 2/2 passed against the real locally built
  producer.
- The A7 focused set passed 111/111; the focused store workspace and progressive-level Scale command
  remains included in the full 133-test Scale pass against the restored producer.
- `scripts/sync-agents.sh`, `cmp -s CLAUDE.md AGENTS.md`, and `git diff --check` passed.

## Compatibility and safety position

- The existing nine MCP tools and public read commands are unchanged. Store provenance is additive.
- Reader transactions are bounded; no store/sidecar database is attached to another database.
- Miller never writes producer-owned store state. Its mutations go through the public `julie-extract store`
  coordinator.
- Dashboard store facts bypass the legacy artifact timestamp cache, so current-generation changes cannot be
  hidden behind an unchanged legacy file.
- The explicit off-switch now forces source reconciliation for malformed pointers in bootstrap and
  cross-workspace refresh; valid-but-unopenable stores remain not-ready. Default-on adoption remains
  a Ph5 scale decision.
- Security scope: no new network, credential, authorization, executable-download, or public mutation surface;
  no dependency change. Existing path containment and store compatibility validation remain the authority.

## Release dependency

Resolved by the stable `julie-extract` 2.31.1 release. Miller pins the four published archive digests and the
restored binary reports 2.31.1. The producer release contains the canonical imported-resolution-base identity
fix required by migration reuse, so the migration/restart acceptance claim now matches the shipped binary.
