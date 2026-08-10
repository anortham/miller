# Index Store Ph3 Miller Acceptance

Status: ACCEPTED FOR THE v1.18.0 DEFAULT-ON RELEASE 2026-08-10. A1-A6 are implemented and verified.
A7 durable reader pins/lock ordering and A8 cursor-incremental sidecars remain disclosed follow-up work;
the user explicitly accepted those boundaries and approved the existing family-store design as Miller's
default. This decision does not claim A7/A8 are closed and does not redesign the family, view, coordinator,
rollback, or sidecar contracts.

## What landed

- Every store-mode reader resolves one validated family/view/generation snapshot. No store read path falls
  back to a stale standalone artifact.
- Bootstrap imports the existing artifact once, binds fresh linked worktrees without copying it, serves L1,
  deepens and resolves through `julie-extract`, and reuses committed work after restart or takeover.
- Search, content, vectors, usage, history, and structural-fact consumers use view-scoped compatibility
  projections. Store sidecars publish checked completeness stamps; the current search/content store path
  rebuilds the complete current view when stale and does not yet have cursor-incremental convergence.
- Full-level store reads with inexact identifier resolution refuse usage-dependent `trace`, `context` usage,
  `inspect` overview/full, `impact`, rename, and reference-export results until the resolution layer is exact.
- Store vector sidecars are per-view in this Ph3 implementation. Family-shared vectors remain a marked Ph5
  design target rather than an undocumented contract claim.
- Turning store mode off exports the active view before legacy serving. Export failure leaves the workspace
  not ready instead of serving stale bytes.
- Status, health, and dashboard surfaces add family, view, generation, manifest, level, resolution,
  migration, rollback, and failure provenance. Legacy output remains unchanged when store mode is off.

## Cleanup evidence

The review gaps were converted into the v4.1-v4.3 amendments and verified as follows:

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
- A7: **open.** The family-store read session records store instance, view, generation, manifest
  generation, per-level state, resolution state, manifest hash, and store-log sequence, but it does
  not create the durable `coord.db` reader pin/heartbeat/expiry/release required by v4.3. Live Miller
  acquisition is `SingleWriterLock → ScanGovernor → _opsGate → sidecar lease`, not the frozen triple.
- A8: **open.** Store search/content sidecars rebuild the complete current view when stale; cursor-
  incremental convergence and a local reproducible cost gate remain Ph5 work.
- A13: **implemented.** Interactive MCP resolution consumers now refuse while the family-store resolution
  state is not exact; the guard tests use a Full-level store read snapshot and assert the expected diagnostic.
- A14: **disclosed.** Store vectors are keyed per view in v1.18; family-shared vectors require their own
  visibility/pre-filter design and cost gate before adoption.

The earlier process-count, scale-duration, and dogfood claims remain historical notes; all elapsed
times below are local report-only observations, not acceptance ceilings or CI performance gates. The
commands below are the cleanup evidence captured against the 2.31.1 producer. The final Miller release pin is
subsequently advanced to 2.31.3; its concurrent-writer hardening and downloaded-package verification are
recorded in `docs/findings/2026-08-10-julie-extract-2.31.3-adoption.md`.

Final branch evidence:

- `scripts/test.sh`: 6,224 fast tests passed, two skipped; local elapsed time was recorded for
  reproducibility only.
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

v1.18.0 default-on release-candidate evidence against published `julie-extract` 2.31.3:

- Release build: zero warnings and zero errors.
- Fast suite: 6,324 passed, four platform/runtime skips, zero failed.
- Scale suite: 135 passed, five optional-runtime skips, zero failed.
- Plugin and site contracts: 49/49 and 1/1 passed.
- NuGet audit found no vulnerable packages; the secrets-like value scan found no credentials.

## Compatibility and safety position

- The existing nine MCP tools and public read commands are unchanged. Store provenance is additive.
- Reader transactions are bounded; no store/sidecar database is attached to another database.
- Miller never writes producer-owned store state. Its mutations go through the public `julie-extract store`
  coordinator.
- Dashboard store facts bypass the legacy artifact timestamp cache, so current-generation changes cannot be
  hidden behind an unchanged legacy file.
- The explicit off-switch forces source reconciliation for malformed pointers in bootstrap and
  cross-workspace refresh; valid-but-unopenable stores remain not-ready. Unset or blank
  `MILLER_INDEX_STORE` uses the family store by default.
- Security scope: no new network, credential, authorization, executable-download, or public mutation surface;
  no dependency change. Existing path containment and store compatibility validation remain the authority.

## Release dependency

Resolved by stable `julie-extract` 2.31.3. Its four published archive digests, tag provenance, and restored
binary are verified in the adoption evidence. The producer release line contains the canonical
imported-resolution-base identity required by migration reuse plus concurrent writer and maintenance fencing,
so the migration/restart and multi-worktree claims match the shipped producer contract.
