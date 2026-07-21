# Content-corpus zombie sources permanently wedge the chunk vector lane

**Date:** 2026-07-21 (found during first-night bge dogfooding on the primary miller workspace)
**Status:** DIAGNOSED — surgical local unwedge applied; product fix pending
**Severity:** high for the semantic docs lane (chunk vectors never converge); invisible otherwise

## Symptom

`VectorConvergeService` logged, every cycle, indefinitely:

```
Vector chunk convergence deferred 2 source(s) for symbols.db hash disagreement:
  .../tests/Miller.Tests/TestResults/fast-timing.trx, .../release-fast-timing.trx
```

`chunk_completed_revision` stayed 0 while the symbol lane converged fine. Semantic served
symbol-route queries but the docs/chunk arm never became available.

## Root cause — three interacting gaps

1. **`ContentCorpusWriter` has no retirement path.** Sources are only ever inserted/updated with
   `status='active'` (grep: no `DELETE FROM content_sources`, no status transition anywhere). A
   workspace file that disappears stays an active corpus source forever, with its last-seen hash.
2. **Out-of-extractor-scope files never bump the revision.** `.trx` (TestResults artifacts) are
   text-corpus-visible but outside julie-extract's scan scope, so editing or deleting them produces
   "Startup delta scan complete: 0 files updated" — the corpus converge, keyed on revision advance,
   never re-examines them. Even `workspace full` (which promoted a fresh symbols.db and new
   artifact id) left the deleted files `active` in content.db.
3. **`VectorConvergePlanner.EvaluateChunkCursor` treats an absent symbols.db hash as disagreement
   and defers the source forever.** Deferral is the right call for a transiently-stale file; it is
   the wrong call for a source whose file no longer exists (or was never extractor-scoped). There is
   no escalation from "deferred N cycles" to "retire or embed from the corpus hash".

Net effect: **any churning or deleted text file that the extractor does not scan wedges the chunk
lane permanently.** Build artifacts under `TestResults/` are the canonical trigger (they rewrite on
every local test run).

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

## Product fix (pending — needs tests, not a hotfix)

- Corpus converge retires sources whose files are gone (`status='missing'`, chunks excluded via the
  existing active-only joins), including a sweep pass that does not depend on revision advance.
- `EvaluateChunkCursor` distinguishes "file missing / not extractor-scoped" from "hash disagreement",
  retiring the former instead of deferring forever; deferral gets a bounded escalation.
- Consider excluding build-artifact directories (`TestResults/`, `bin/`, `obj/`) from corpus scope
  outright — churn there can also thrash corpus rebuild work even without the wedge.

## Related observations from the same night (separate follow-ups)

- `miller refresh --json --wait --workspace <root>` reported `status: lock_busy` repeatedly while
  `lsof` showed no holder of `<root>/.miller/indexer.lock`, and a subsequently spawned server claimed
  leadership instantly. Either the CLI probes the wrong `.miller` dir (current-workspace vs target
  row) or the busy-wait window (2s) misreports; needs a targeted test.
- `workspace leader` kept reporting an exited pid as `alive` (stale identity record) until a new
  claimant overwrote it; the queued `--handoff` from that state was never observed. Staleness
  detection for the recorded identity would make the diagnose verb trustworthy.
