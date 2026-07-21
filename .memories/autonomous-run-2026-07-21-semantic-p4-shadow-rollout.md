# Autonomous Run Report — Semantic P4 Shadow Rollout

**Status:** Complete
**Plan:** `docs/plans/2026-07-20-semantic-p4-shadow-rollout.md`
**Branch:** `worktree-semantic-p4` (base `a921bae` local main)
**Merged:** locally to `main` (no-ff); **not pushed** per standing directive (keep miller changes local until the semantic plan completes)
**Tasks:** 9/9 complete, plus 1 lead de-flake fix and 3 codex-review fixes

## What shipped

- **Circuit-open pause producer (T1, 3351d6b)** — `VectorConvergeService.ResolvePause` is the single producer of `converge_pause_state`/`converge_pause_reason` on the active artifact; circuit-open outranks disk-blocked; transition-edge writes; empty-string clear matching `PauseState` null semantics.
- **Disk preflight (T2, caefb9e)** — `DiskPreflight` pure verdict (unknown/-1 never blocks) with a 256 MiB floor, wired at `BuildShadowAsync` entry (no `.rebuild` debris) and slice boundaries; blocked builds surface `disk-blocked` via the same pause facts.
- **`miller semantic prepare` CLI verb (T3, 7732efe)** — pre-downloads the model outside the server so first drain never stalls on a cold download; writes the `semantic-prepare.marker` handshake file.
- **`downloading` status (T4, 6fc6216)** — `workspace status` shows `downloading` + `downloading_model` when a live prepare process holds the marker and classification would otherwise be `unavailable` (structural states keep precedence).
- **Generation GC (T5, db47d45)** — `VectorGenerationGc` runs at drain tail on the leader; `VectorLiveReaderRegistry` refcounts in-flight readers; held handles retry next wake; only incompatible identity upgrades retain a rollback generation.
- **Sidecar promotion-gate throughput floor (T6, sidecar repo)** — `docs/rc-promotion-gate.md` gains a hard floor: ≥40 units/s warm 64-text batch on M2 Ultra; `scripts/bench-throughput.py` measures it plus RSS.
- **Q8_0 footprint evidence (T7, 9143fe6)** — `docs/findings/2026-07-20-q8-footprint-benchmark.md`: no Q8_0 manifest pin exists; measured f16 82.9 u/s @ 1.27 GiB RSS vs bge-small 743.7 u/s @ 196 MiB (9.0× throughput at 0.15× memory). Model choice is a user decision.
- **Fast-suite ceiling (T8, 45ae5e4)** — `JulieDbFixture` batched transaction + `synchronous=OFF` killed the fsync amplifier (~27s → ~15s wall); scan-signal waits raised to 30s.
- **Shadow dogfood (T9, ce47cf8)** — `docs/findings/2026-07-20-p4-shadow-dogfood.md`: goldfish 40s/2.2 MiB clean; eros fault campaign (circuit-open self-reported cross-process in 16s, self-cleared on recovery); julie 244s/9.4 MiB zero errors; rebuild-promote debris correctly reclaimed by the next leader; registry prune dry-run 0/52.

## External review (codex)

- **Reviewer:** codex — verdict `needs-attention`, **4 findings**, all resolved single-pass.
- **Fixed: 3**
  - **F1 (high) retention-mtime bug — `3c75a0d`.** `File.Move` preserves mtime, so a retained rollback generation could look expired immediately on an idle workspace. Fix: `IVectorGenerationFiles.Touch` stamps the retained file after the move; red-first regression test.
  - **F2 Unix disk-probe mount — `26cee26`.** `Path.GetPathRoot` collapses every Unix path to `/`; the probe now uses `new DriveInfo(nearestExistingAncestor)` directly (Windows keeps `GetPathRoot`).
  - **F4 prepare-marker race — `ecc420e`.** Two concurrent `semantic prepare` runs could delete each other's marker. Fix: nonce ownership — only the process whose nonce matches deletes the marker; red-first concurrent-marker regression test.
- **Dismissed: 1**
  - **F3 incremental batches bypass disk preflight.** Plan-scoped: shadow builds carry the corruption risk the preflight exists for; the incremental path fails safe (visible cursor hold, retried next wake). Filed as a pre-P5 follow-up.
- **Flagged for human judgment: 0**
- **Cost:** codex does not surface per-request token counts in its JSON output; no cost figure available.

## Post-review fixes

- **vec0 park race — `412033d`.** Final-gate flake: the park test moves the shared `.tools/vec0` file, but `VectorStoreTests`, `VectorGenerationManagerScaleTests`, and `SemanticSidecarScaleTests` loaded the extension without `SqliteVecEnvironment` collection membership. All vec0-loading classes now serialize on the collection; a stale "needs no serializing collection" doc claim corrected.

## Judgment calls

- Serialized Lane 1 (T1→T2→T5) on shared `VectorConvergeService.cs` ownership; Lane 2 (T3→T4) ran in parallel with lead commits.
- Env-prefix pipeline gotcha (`VAR=x cmd1 | cmd2` binds to cmd1) cost one 20-minute semantic-off dogfood run — which incidentally proved the `MILLER_SEMANTIC` off-guarantee under a real full rescan.
- Harness exit predicate killed a server mid-rebuild leaving partial `.rebuild` debris — the next leader reclaimed it correctly (positive finding, kept in the dogfood doc).
- One transient fast-suite failure during the final gate did not reproduce across two clean full runs; recorded in the ledger.

## Tests

- Branch gate at `412033d` (post-review HEAD before metadata-only commits): `scripts/test.sh all` — fast 4227 passed / 2 expected skips, scale 86/86, exit 0.
- Release build: 0 warnings / 0 errors.

## Blockers hit

None.

## Files changed

26 files, +2666 / −152 (source + tests + docs; SDD evidence files additional).

## Next steps / pending user decisions

1. **Model footprint call** — f16 vs bge-small evidence in `docs/findings/2026-07-20-q8-footprint-benchmark.md`; no Q8_0 pin exists in the sidecar manifest.
2. **Sidecar RC → v0.1.0 promotion** — needs the new 40 u/s floor run recorded against the promoted RC; sidecar repo is 3 commits ahead of origin (pushes allowed, release needs approval).
3. **Miller push timing** — main is now N commits ahead of origin locally; pushes held until the plan completes.
4. **Pre-P5 follow-ups:** chunk-cursor starvation retry wake (medium), deferred-source logging (low), incremental-path disk gate (codex F3), sidecar RSS peak ceiling check, compact "ready (rebuilding)" hint.

**PR:** not created — pushes held per standing directive.
