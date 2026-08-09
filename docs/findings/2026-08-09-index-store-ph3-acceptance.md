# Index Store Ph3 Miller Acceptance

Status: implementation accepted locally on `codex/index-store-ph3`; public release is blocked on the
producer fix and pin described below. Store mode remains explicit with `MILLER_INDEX_STORE=1`.

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

## Acceptance evidence

The real-process scale contract uses the locally built 2.31 producer and proves legacy import, L1 visibility,
Full deepening, exact resolution, cross-workspace refresh, second-process reuse, and rollback export. Focused
contracts additionally cover linked-worktree identity, concurrent request fairness, dead-holder takeover,
sidecar replay, incompatible schema/floor refusal, and additive output compatibility.

Producer evidence is not inferred from the Miller fixture. Julie 2.31 dogfood ran two views over all 38
supported languages, churn, failures, exact resolution, crash recovery, and fresh-store comparison; both
views matched 21 normalized visible groups with zero mismatches. Miller's language-catalog scale contract
independently checks the pinned binary's complete language classification.

Final branch evidence:

- `scripts/test.sh`: the final review-fix run passed 6,214 fast tests, skipped two, and completed in
  26 seconds under the 30-second ceiling.
- `scripts/test.sh scale`: 133 Scale tests passed, five skipped, in 3 minutes 12 seconds.
- `scripts/test-plugin.sh`: 49/49 passed.
- `dotnet build Miller.slnx -c Release`: succeeded with zero warnings and zero errors.
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-build --filter
  'FullyQualifiedName~StoreWorkspaceIndexProviderScaleTests'`: 2/2 passed against the real locally built
  producer.
- The equivalent focused language-catalog command passed 2/2.
- `scripts/sync-agents.sh`, `cmp -s CLAUDE.md AGENTS.md`, and `git diff --check` passed.

## Compatibility and safety verdict

- The existing nine MCP tools and public read commands are unchanged. Store provenance is additive.
- Reader transactions are bounded; no store/sidecar database is attached to another database.
- Miller never writes producer-owned store state. Its mutations go through the public `julie-extract store`
  coordinator.
- Dashboard store facts bypass the legacy artifact timestamp cache, so current-generation changes cannot be
  hidden behind an unchanged legacy file.
- The explicit off-switch is honest. Default-on adoption remains a Ph5 scale decision.
- Security scope: no new network, credential, authorization, executable-download, or public mutation surface;
  no dependency change. Existing path containment and store compatibility validation remain the authority.

## Release dependency

Resolved by the stable `julie-extract` 2.31.1 release. Miller pins the four published archive digests and the
restored binary reports 2.31.1. The producer release contains the canonical imported-resolution-base identity
fix required by migration reuse, so the migration/restart acceptance claim now matches the shipped binary.
