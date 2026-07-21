# Content-corpus zombie sources permanently wedge the chunk vector lane

**Date:** 2026-07-21 (found during first-night bge dogfooding on the primary miller workspace)
**Status:** FIXED — root cause corrected below (the original three-part analysis over-attributed);
`ContentCorpusSidecar.EnsureBuilt` is now hash-aware (same-day fix, TDD)
**Severity:** high for the semantic docs lane (chunk vectors never converge); invisible otherwise

## Symptom

`VectorConvergeService` logged, every cycle, indefinitely:

```
Vector chunk convergence deferred 2 source(s) for symbols.db hash disagreement:
  .../tests/Miller.Tests/TestResults/fast-timing.trx, .../release-fast-timing.trx
```

`chunk_completed_revision` stayed 0 while the symbol lane converged fine. Semantic served
symbol-route queries but the docs/chunk arm never became available.

## Root cause (corrected after code-level verification)

The initial three-part analysis over-attributed. The corpus has **no incremental path at all** —
`ContentCorpusSidecar.EnsureBuilt` either skips (fresh) or performs an honest full rebuild from
symbols.db's `files` table, which implicitly retires anything symbols.db no longer lists. The
actual defect was the freshness check:

**`IsFresh` proved freshness by revision equality alone, but the extractor updates
`files.content_hash` (and drops rows) for symbol-free files WITHOUT advancing the revision.** A
symbol-free file that churns (a `.trx` rewritten by every test run) moves symbols.db's recorded
hash while the corpus — "fresh" by revision — keeps the old one. The chunk-vector gate
(`EvaluateChunkCursor`) then correctly refuses to embed from a corpus that disagrees with
symbols.db, and since nothing ever advances the revision, nothing ever rebuilds the corpus: a
permanent wedge. The gate's per-cycle deferral was correct behavior against a corpus that could
never catch up.

(The observed survival across `workspace full` is consistent with the same blindness on the
post-promote converge; the surgical `status='missing'` update below plus the next
revision-advancing converge is what actually cleared this machine.)

## Reproduce

1. Let the corpus index a text file the extractor ignores (e.g. a `.trx` under `TestResults/`).
2. Rewrite or delete the file while no leader is running.
3. Start a leader with `MILLER_SEMANTIC=on`: symbol vectors converge; chunk convergence defers the
   file every cycle; `chunk_completed_revision` never reaches target. `workspace full` does not
   clear it.

## Local unwedge applied (2026-07-21, primary workspace only)

Deleted the two trx files, then manually retired the zombie rows —
`UPDATE content_sources SET status='missing' WHERE path LIKE '%timing.trx'` — mirroring what a
correct retirement would have written. All corpus/chunk queries filter `status='active'`, so this
cleanly removes them from the gate, FTS joins, and chunk candidates. Chunk lane converged on the
next serve round.

## Fix (shipped same day)

`ContentCorpusSidecar.EnsureBuilt` now requires `WorkspaceSourcesAgree` in addition to the revision
check: every active workspace-kind corpus source must exist in symbols.db `files` with an agreeing
normalized hash, else the corpus rebuilds. External/web imports are exempt (no symbols.db
counterpart by contract). Both wedge directions (hash moved in place; row dropped) are covered by
tests in `ContentCorpusSidecarTests`, plus a guard that external sources never force rebuilds. The
chunk gate is intentionally unchanged — its deferral is now transient by construction.

Remaining follow-up (julie-extractors side): consider excluding build-artifact directories
(`TestResults/`, `bin/`, `obj/`) from scan scope — churn there still causes corpus rebuild work,
just no longer a wedge.

## Related observations from the same night (separate follow-ups)

- `miller refresh --json --wait --workspace <root>` reported `status: lock_busy` repeatedly while
  `lsof` showed no holder of `<root>/.miller/indexer.lock`, and a subsequently spawned server claimed
  leadership instantly. Either the CLI probes the wrong `.miller` dir (current-workspace vs target
  row) or the busy-wait window (2s) misreports; needs a targeted test.
- `workspace leader` kept reporting an exited pid as `alive` (stale identity record) until a new
  claimant overwrote it; the queued `--handoff` from that state was never observed. Staleness
  detection for the recorded identity would make the diagnose verb trustworthy.
